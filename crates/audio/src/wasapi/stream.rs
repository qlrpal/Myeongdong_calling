//! One-stream diagnostic probe. COM objects never cross the worker boundary.
use crate::{
    CaptureFlags, CaptureRecord, DataFlow, DevicePosition, Format, Hns, MmcssTask, QpcTicks,
    RenderRecord, StreamActual, StreamRequest,
    spsc::{self, Producer},
    stream_report::{self, Api, CallStatuses, ProbeEvent, WaitResult, WakeRecord},
};
use std::{
    path::PathBuf,
    sync::atomic::{AtomicBool, Ordering},
    time::Duration,
};
use windows::{
    Win32::{
        Devices::FunctionDiscovery::PKEY_Device_FriendlyName,
        Foundation::{CloseHandle, GetLastError, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT},
        Media::Audio::*,
        System::{
            Com::*, Console::*, Performance::QueryPerformanceFrequency,
            RemoteDesktop::ProcessIdToSessionId, Threading::*,
        },
    },
    core::{GUID, Interface, PCWSTR, w},
};

pub(super) static STOP: AtomicBool = AtomicBool::new(false);
pub(super) static WORKER_DONE: AtomicBool = AtomicBool::new(false);
pub(super) static BUSY: AtomicBool = AtomicBool::new(false);
pub(super) const E_FAIL: i32 = 0x80004005u32 as i32;
pub(super) const E_INVALID: i32 = 0x80070057u32 as i32;
const RING_CAPACITY: usize = 131072;
pub(super) const PACKET_LIMIT: u32 = 64;
pub use crate::stream_mode::Mode;
pub struct Options {
    pub endpoint: String,
    pub mode: Mode,
    pub mmcss: MmcssTask,
    pub seconds: u32,
    pub out: PathBuf,
}
pub struct Output {
    pub records: PathBuf,
    pub metadata: PathBuf,
    pub summary: String,
    pub successful: bool,
}
#[derive(Clone, Copy, Debug)]
pub(super) struct End {
    pub(super) stage: &'static str,
    api: &'static str,
    pub(super) hresult: i32,
}
impl End {
    pub(super) fn setup(api: &'static str, hresult: i32) -> Self {
        Self {
            stage: "prepare",
            api,
            hresult,
        }
    }
}
#[derive(Default)]
pub(super) struct Report {
    pub(super) fields: Vec<(String, String)>,
    dropped: u64,
    frequency: u64,
    started: u64,
    ended: u64,
    pub(super) successful: bool,
    pub(super) exclusive_attempted: bool,
}
impl Report {
    pub(super) fn put(&mut self, key: impl Into<String>, value: impl ToString) {
        let key = key.into();
        let mut unique = key.clone();
        let mut occurrence = 2u64;
        while self.fields.iter().any(|(k, _)| *k == unique) {
            unique = format!("{key}.repeat.{occurrence}");
            occurrence += 1;
        }
        self.fields.push((unique, value.to_string()));
    }
    pub(super) fn hr(&mut self, api: &'static str, hr: i32) -> Result<(), End> {
        self.put(format!("{api}.hresult"), format!("0x{:08X}", hr as u32));
        if hr < 0 {
            Err(End::setup(api, hr))
        } else {
            Ok(())
        }
    }
    pub(super) fn call<T>(
        &mut self,
        api: &'static str,
        r: windows::core::Result<T>,
    ) -> Result<T, End> {
        match r {
            Ok(v) => {
                self.hr(api, 0)?;
                Ok(v)
            }
            Err(e) => {
                let hr = e.code().0;
                self.hr(api, hr)?;
                Err(End::setup(api, hr))
            }
        }
    }
}
pub(super) unsafe extern "system" fn console_handler(kind: u32) -> windows::core::BOOL {
    if kind == CTRL_C_EVENT
        || kind == CTRL_BREAK_EVENT
        || kind == CTRL_CLOSE_EVENT
        || kind == CTRL_LOGOFF_EVENT
        || kind == CTRL_SHUTDOWN_EVENT
    {
        STOP.store(true, Ordering::Release);
        if kind == CTRL_CLOSE_EVENT || kind == CTRL_LOGOFF_EVENT || kind == CTRL_SHUTDOWN_EVENT {
            let deadline = std::time::Instant::now() + Duration::from_secs(4);
            while !WORKER_DONE.load(Ordering::Acquire) {
                let remaining = deadline.saturating_duration_since(std::time::Instant::now());
                if remaining.is_zero() {
                    break;
                }
                // SAFETY: console handler is not a realtime thread; bounded cleanup polling.
                unsafe { Sleep(remaining.as_millis().min(10) as u32) };
            }
        }
        true.into()
    } else {
        false.into()
    }
}
struct WorkerDone;
impl Drop for WorkerDone {
    fn drop(&mut self) {
        WORKER_DONE.store(true, Ordering::Release);
    }
}
pub(super) struct ConsoleGuard(pub(super) bool);
impl Drop for ConsoleGuard {
    fn drop(&mut self) {
        if self.0 {
            // SAFETY: removes the same static handler registered by run.
            let _ = unsafe { SetConsoleCtrlHandler(Some(console_handler), false) };
        }
        BUSY.store(false, Ordering::Release);
    }
}
pub(super) struct Event(pub(super) HANDLE);
impl Drop for Event {
    fn drop(&mut self) {
        // SAFETY: Event uniquely owns a successfully created kernel handle.
        let _ = unsafe { CloseHandle(self.0) };
    }
}
pub(super) struct Mmcss(pub(super) Option<HANDLE>);
impl Drop for Mmcss {
    fn drop(&mut self) {
        if let Some(h) = self.0.take() {
            // SAFETY: registration and reversion occur on this worker thread.
            let _ = unsafe { AvRevertMmThreadCharacteristics(h) };
        }
    }
}
// Setup-only diagnostic: preserve last error before formatting or another API call.
pub(super) fn thread_priority() -> String {
    // SAFETY: the pseudo-handle refers to this live worker thread.
    let priority = unsafe { GetThreadPriority(GetCurrentThread()) };
    // Win32 THREAD_PRIORITY_ERROR_RETURN (0x7fffffff); no extra API feature needed.
    const THREAD_PRIORITY_ERROR_RETURN: i32 = i32::MAX;
    if priority == THREAD_PRIORITY_ERROR_RETURN {
        // SAFETY: immediately captures this thread's failed query error.
        let error = unsafe { GetLastError() }.0;
        format!("error=0x{error:08X}")
    } else {
        priority.to_string()
    }
}

struct Started<'a> {
    client: &'a IAudioClient,
    active: bool,
}
impl Drop for Started<'_> {
    fn drop(&mut self) {
        if self.active {
            // SAFETY: fallback cleanup on unwind, outside the realtime function.
            let _ = unsafe { self.client.Stop() };
        }
    }
}

/// No fallible output operation occurs until the worker has been joined.
pub fn run(options: Options) -> Result<Output, Box<dyn std::error::Error>> {
    if !(1..=120).contains(&options.seconds)
        || options.endpoint.is_empty()
        || options.endpoint.contains('\0')
    {
        return Err("seconds must be 1..=120; endpoint must be nonempty and contain no NUL".into());
    }
    if BUSY
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
        .is_err()
    {
        return Err("stream probe already running".into());
    }
    STOP.store(false, Ordering::Release);
    WORKER_DONE.store(false, Ordering::Release);
    // SAFETY: static handler signals stop and polls worker cleanup on close events.
    let registered = unsafe { SetConsoleCtrlHandler(Some(console_handler), true) };
    let _console = ConsoleGuard(registered.is_ok());
    let mut report = Report::default();
    report.put(
        "ctrl_c.registration.hresult",
        format!(
            "0x{:08X}",
            registered.err().map_or(0, |e| e.code().0) as u32
        ),
    );
    let (time, stamp) = super::timestamp();
    report.put("executed_at_utc", time);
    report.put("os", super::os_version());
    report.put("tool_version", env!("CARGO_PKG_VERSION"));
    let commit = crate::provenance::commit_label();
    report.put("commit", commit);
    let mut session = 0;
    // SAFETY: output points to a live u32; querying current process only.
    let result = unsafe { ProcessIdToSessionId(std::process::id(), &mut session) };
    report.put(
        "process_session.hresult",
        format!(
            "0x{:08X}",
            result.as_ref().err().map_or(0, |e| e.code().0) as u32
        ),
    );
    if result.is_ok() {
        report.put("process_session.id", session);
    }
    report.put("endpoint.id", &options.endpoint);
    report.put("mode", options.mode.name());
    report.put("requested_seconds", options.seconds);
    report.put("deadline_missed", "not evaluated in P1-b");
    report.put("measurement","diagnostic stream timestamps only; no mouth-to-ear latency judgment; no audio samples retained; no source sample preservation estimate");
    report.put("ring_capacity", RING_CAPACITY);
    report.put("packet_iteration_limit", PACKET_LIMIT);
    report.put("exclusive_capture", "one GetBuffer per event wake");
    let (producer, mut consumer) = spsc::channel(RING_CAPACITY).ok_or("invalid ring capacity")?;
    let mut records = Vec::with_capacity(1_048_576);
    let mode = options.mode;
    let endpoint = options.endpoint;
    let worker = std::thread::Builder::new()
        .name("wasapi-probe".into())
        .spawn(move || {
            worker(
                endpoint,
                mode,
                options.mmcss,
                options.seconds,
                producer,
                report,
            )
        })?;
    while !worker.is_finished() {
        while let Some(record) = consumer.pop() {
            records.push(record);
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    STOP.store(true, Ordering::Release);
    let mut report = worker
        .join()
        .map_err(|_| "stream thread panicked; RAII cleanup attempted")?;
    while let Some(record) = consumer.pop() {
        records.push(record);
    }
    let summary = stream_report::summarize(&records, report.frequency, report.dropped).text();
    report.put("dropped_records", report.dropped);
    report.put("records", records.len());
    report.put("run.start_qpc", report.started);
    report.put("run.end_qpc", report.ended);
    report.put("run.completed_normally", report.successful);
    report.put("run.started", report.started != 0);
    report.put(
        "run.actual_seconds",
        if report.frequency == 0 {
            0.0
        } else {
            report.ended.saturating_sub(report.started) as f64 / report.frequency as f64
        },
    );
    report.put(
        "run.boundary",
        if report.started != 0 {
            "realtime loop entry QPC (read after Start returned) -> realtime loop exit; Stop follows"
        } else {
            "not started; no realtime interval"
        },
    );
    let mut meta = stream_report::metadata(&report.fields);
    meta.push_str(&summary);
    std::fs::create_dir_all(&options.out)?;
    let records_path = options.out.join(format!("stream-{stamp}-records.csv"));
    let meta_path = options.out.join(format!("stream-{stamp}-meta.txt"));
    use std::io::Write;
    let mut csv = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&records_path)?;
    let mut metadata = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&meta_path)?;
    csv.write_all(stream_report::csv(&records).as_bytes())?;
    csv.flush()?;
    metadata.write_all(meta.as_bytes())?;
    metadata.flush()?;
    Ok(Output {
        records: records_path,
        metadata: meta_path,
        summary,
        successful: report.successful,
    })
}
fn worker(
    endpoint: String,
    mode: Mode,
    mmcss: MmcssTask,
    seconds: u32,
    mut producer: Producer<ProbeEvent>,
    mut report: Report,
) -> Report {
    // Declared first so COM and all stream cleanup finish before signalling, also on unwind.
    let _done = WorkerDone;
    report.put("mmcss.request", mmcss.as_str());
    // SAFETY: initializes MTA on this worker; all COM values below die before guard.
    let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.0;
    if report.hr("CoInitializeEx", hr).is_err() {
        report.put("end.stage", "prepare");
        report.put("end.api", "CoInitializeEx");
        report.put("end.hresult", hr);
        return report;
    }
    let _com = super::Com;
    let result = prepare(&endpoint, mode, mmcss, seconds, &mut producer, &mut report);
    match result {
        Ok(end) => {
            report.successful = end.hresult >= 0 && end.stage == "complete";
            report.put("end.stage", end.stage);
            report.put("end.api", end.api);
            report.put("end.hresult", format!("0x{:08X}", end.hresult as u32));
        }
        Err(end) => {
            report.put("end.stage", end.stage);
            report.put("end.api", end.api);
            report.put("end.hresult", format!("0x{:08X}", end.hresult as u32));
        }
    }
    // prepare has returned: every stream COM object, event and MMCSS guard is gone.
    if mode.is_exclusive() && report.exclusive_attempted {
        release_check(&endpoint, &mut report);
    }
    report
}

/// Owns an API-allocated WAVEFORMATEX including arbitrary trailing bytes.
pub(super) struct Wave {
    storage: Vec<u64>,
    length: usize,
}
impl Wave {
    pub(super) fn bytes(&self) -> &[u8] {
        // SAFETY: constructors initialize length bytes in owned storage.
        unsafe { std::slice::from_raw_parts(self.ptr().cast(), self.length) }
    }

    fn from_bytes(bytes: &[u8]) -> Result<Self, End> {
        if bytes.len() < 18 {
            return Err(End::setup("waveformat.length", E_INVALID));
        }
        let extra = u16::from_le_bytes([bytes[16], bytes[17]]) as usize;
        if bytes.len() != 18 + extra {
            return Err(End::setup("waveformat.cbSize", E_INVALID));
        }
        let mut storage = vec![0u64; bytes.len().div_ceil(8)];
        // SAFETY: storage has enough bytes, alignment >= WAVEFORMATEX, no overlap.
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), storage.as_mut_ptr().cast(), bytes.len());
        }
        Ok(Self {
            storage,
            length: bytes.len(),
        })
    }
    pub(super) fn ptr(&self) -> *const WAVEFORMATEX {
        self.storage.as_ptr().cast()
    }
    pub(super) fn base(&self) -> WAVEFORMATEX {
        // SAFETY: constructors validate the base header and own the backing allocation.
        unsafe { self.ptr().read_unaligned() }
    }
    pub(super) fn from_api(p: *mut WAVEFORMATEX) -> Result<Self, End> {
        if p.is_null() {
            return Err(End::setup("waveformat.null", E_FAIL));
        }
        let _memory = super::TaskMem(p);
        // SAFETY: GetMixFormat/GetCurrentSharedModeEnginePeriod allocate header + cbSize bytes.
        let length = 18 + usize::from(unsafe { p.read_unaligned() }.cbSize);
        // SAFETY: same API allocation remains alive while copied.
        Self::from_bytes(unsafe { std::slice::from_raw_parts(p.cast(), length) })
    }
    pub(super) fn description(&self) -> String {
        let f = self.base();
        let tag = f.wFormatTag;
        let ch = f.nChannels;
        let rate = f.nSamplesPerSec;
        let bits = f.wBitsPerSample;
        let extra = f.cbSize;
        let align = f.nBlockAlign;
        let avg = f.nAvgBytesPerSec;
        let mut text = format!(
            "tag={tag:#06x};channels={ch};rate={rate};bits={bits};cbSize={extra};block_align={align};bytes_per_second={avg}"
        );
        if tag == 0xfffe && extra >= 22 && self.length >= 40 {
            // SAFETY: validated extended length, unaligned read handles packed representation.
            let ext = unsafe { self.ptr().cast::<WAVEFORMATEXTENSIBLE>().read_unaligned() };
            // SAFETY: WAVEFORMATEXTENSIBLE defines this union member as valid bits for PCM/float.
            let valid = unsafe { ext.Samples.wValidBitsPerSample };
            let mask = ext.dwChannelMask;
            let subtype = ext.SubFormat;
            text.push_str(&format!(
                ";valid_bits={valid};channel_mask={mask:#x};subtype={subtype:?}"
            ));
        } else {
            text.push_str(";valid_bits=not_present;channel_mask=not_present;subtype=not_present");
        }
        // Preserve all bytes, including vendor extensions, for a complete format record.
        text.push_str(";raw_hex=");
        // SAFETY: owned allocation contains length initialized bytes.
        for b in unsafe { std::slice::from_raw_parts(self.ptr().cast::<u8>(), self.length) } {
            text.push_str(&format!("{b:02x}"));
        }
        text
    }
    fn format(&self) -> Format {
        let f = self.base();
        let mut float = f.wFormatTag == 3;
        if f.wFormatTag == 0xfffe && self.length >= 40 {
            // SAFETY: extended header fits owned allocation.
            let subtype =
                unsafe { self.ptr().cast::<WAVEFORMATEXTENSIBLE>().read_unaligned() }.SubFormat;
            float = subtype == GUID::from_u128(0x00000003_0000_0010_8000_00aa00389b71);
        }
        Format {
            sample_rate: f.nSamplesPerSec,
            channels: f.nChannels,
            bits_per_sample: f.wBitsPerSample,
            float,
        }
    }
}
pub(super) fn current(c: &IAudioClient3, label: &str, r: &mut Report) -> Result<u32, End> {
    let mut p = std::ptr::null_mut();
    let mut frames = 0;
    // SAFETY: valid interface and writable output locations; the returned allocation is freed below.
    let hr =
        unsafe { (c.vtable().GetCurrentSharedModeEnginePeriod)(c.as_raw(), &mut p, &mut frames) }.0;
    r.put(
        format!("current.{label}.hresult"),
        format!("0x{:08X}", hr as u32),
    );
    if hr < 0 {
        return Err(End::setup("GetCurrentSharedModeEnginePeriod", hr));
    }
    r.put(format!("current.{label}.frames"), frames);
    match Wave::from_api(p) {
        Ok(f) => r.put(format!("current.{label}.format"), f.description()),
        Err(e) => {
            r.put(format!("current.{label}.format_error"), format!("{e:?}"));
            return Err(e);
        }
    }
    Ok(frames)
}
pub(super) fn device(endpoint: &str, r: &mut Report) -> Result<IMMDevice, End> {
    // SAFETY: caller owns an initialized COM apartment; exact ID is nul-terminated.
    let e: IMMDeviceEnumerator = r.call("CoCreateInstance", unsafe {
        CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)
    })?;
    let wide = super::wide(endpoint);
    // SAFETY: wide stays alive for the synchronous call.
    r.call("GetDevice", unsafe { e.GetDevice(PCWSTR(wide.as_ptr())) })
}
pub(super) fn parse_device_format(
    value: &windows::Win32::System::Com::StructuredStorage::PROPVARIANT,
) -> Result<Option<Wave>, String> {
    // SAFETY: inspect discriminant before reading the blob union member.
    let inner = unsafe { &value.Anonymous.Anonymous };
    if inner.vt == windows::Win32::System::Variant::VT_EMPTY {
        return Ok(None);
    }
    if inner.vt != windows::Win32::System::Variant::VT_BLOB {
        return Err("expected VT_BLOB".into());
    }
    // SAFETY: vt is VT_BLOB. Size and pointer are checked before constructing slice.
    let blob = unsafe { inner.Anonymous.blob };
    if blob.pBlobData.is_null() || !(18..=65553).contains(&blob.cbSize) {
        return Err("null pointer or invalid blob size".into());
    }
    // SAFETY: property store owns cbSize bytes for the lifetime of value.
    Wave::from_bytes(unsafe { std::slice::from_raw_parts(blob.pBlobData, blob.cbSize as usize) })
        .map(Some)
        .map_err(|e| format!("{e:?}"))
}

pub(super) fn record_device_format(d: &IMMDevice, r: &mut Report) {
    // SAFETY: read-only property store; PROPVARIANT remains live while copied.
    let property = (|| {
        let store = unsafe { d.OpenPropertyStore(STGM_READ) }?;
        unsafe { store.GetValue(&PKEY_AudioEngine_DeviceFormat) }
    })();
    let value = match property {
        Ok(value) => value,
        Err(e) => {
            r.put("device_format", "not_available");
            r.put(
                "device_format.hresult",
                format!("0x{:08X}", e.code().0 as u32),
            );
            return;
        }
    };
    match parse_device_format(&value) {
        Ok(Some(wave)) => {
            r.put("device_format", wave.description());
            r.put("device_format.sample_rate", wave.base().nSamplesPerSec);
        }
        Ok(None) => {
            r.put("device_format", "not_available");
            r.put("device_format.invalid", "expected VT_BLOB");
        }
        Err(e) => {
            r.put("device_format", "not_available");
            r.put("device_format.invalid", e);
        }
    }
}
pub(super) fn activate(d: &IMMDevice, r: &mut Report) -> Result<IAudioClient, End> {
    // SAFETY: endpoint and COM apartment are live on the current worker.
    r.call("Activate.IAudioClient", unsafe {
        d.Activate(CLSCTX_ALL, None)
    })
}
pub(super) fn preflight(d: &IMMDevice, r: &mut Report) -> Result<(), End> {
    r.put("exclusive.preflight.caveat","auxiliary refusal condition only; session enumeration is racy and does not guarantee non-interference");
    // SAFETY: read-only activation/enumeration in initialized COM apartment.
    let mgr: IAudioSessionManager2 = r.call("Activate.IAudioSessionManager2", unsafe {
        d.Activate(CLSCTX_ALL, None)
    })?;
    // SAFETY: valid session manager, enumeration does not change sessions.
    let sessions = r.call("GetSessionEnumerator", unsafe {
        mgr.GetSessionEnumerator()
    })?;
    // SAFETY: live enumerator.
    let n = r.call("sessions.GetCount", unsafe { sessions.GetCount() })?;
    if n < 0 {
        return Err(End::setup("sessions.negative_count", E_FAIL));
    }
    let (mut active, mut inactive, mut expired) = (0u32, 0u32, 0u32);
    for i in 0..n {
        // SAFETY: index from enumerated count; all failures cause refusal.
        let s = r.call("sessions.GetSession", unsafe { sessions.GetSession(i) })?;
        // SAFETY: live session, read-only query.
        let state = r.call("sessions.GetState", unsafe { s.GetState() })?;
        if state == AudioSessionStateActive {
            active += 1;
        } else if state == AudioSessionStateInactive {
            inactive += 1;
        } else if state == AudioSessionStateExpired {
            expired += 1;
        } else {
            return Err(End::setup("sessions.unknown_state", E_FAIL));
        }
    }
    r.put("sessions.active", active);
    r.put("sessions.inactive", inactive);
    r.put("sessions.expired", expired);
    if active != 0 {
        return Err(End {
            stage: "refused",
            api: "sessions.active",
            hresult: 0,
        });
    }
    Ok(())
}
fn candidate(
    rate: u32,
    channels: u16,
    bits: u16,
    valid: u16,
    float: bool,
    ext: bool,
    mask: u32,
) -> Result<Wave, End> {
    let align = channels
        .checked_mul(bits / 8)
        .ok_or(End::setup("candidate.align", E_INVALID))?;
    let avg = rate
        .checked_mul(u32::from(align))
        .ok_or(End::setup("candidate.byte_rate", E_INVALID))?;
    let mut bytes = Vec::new();
    bytes.extend_from_slice(&(if ext { 0xfffeu16 } else { 1u16 }).to_le_bytes());
    bytes.extend_from_slice(&channels.to_le_bytes());
    bytes.extend_from_slice(&rate.to_le_bytes());
    bytes.extend_from_slice(&avg.to_le_bytes());
    bytes.extend_from_slice(&align.to_le_bytes());
    bytes.extend_from_slice(&bits.to_le_bytes());
    bytes.extend_from_slice(&(if ext { 22u16 } else { 0u16 }).to_le_bytes());
    if ext {
        bytes.extend_from_slice(&valid.to_le_bytes());
        bytes.extend_from_slice(&mask.to_le_bytes());
        bytes.extend_from_slice(&(if float { 3u32 } else { 1u32 }).to_le_bytes());
        bytes.extend_from_slice(&0u16.to_le_bytes());
        bytes.extend_from_slice(&0x10u16.to_le_bytes());
        bytes.extend_from_slice(&[0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71]);
    }
    Wave::from_bytes(&bytes)
}
pub(super) fn exclusive(
    d: &IMMDevice,
    mix: &Wave,
    r: &mut Report,
) -> Result<(IAudioClient, Wave, u32, i64), End> {
    let f = mix.base();
    let mask = if f.wFormatTag == 0xfffe && mix.length >= 40 {
        // SAFETY: owned extended header validated.
        unsafe { mix.ptr().cast::<WAVEFORMATEXTENSIBLE>().read_unaligned() }.dwChannelMask
    } else {
        match f.nChannels {
            1 => 4,
            2 => 3,
            _ => 0,
        }
    };
    let mut candidates = Vec::new();
    for (bits, valid, float, ext) in [
        (16, 16, false, true),
        (32, 24, false, true),
        (24, 24, false, true),
        (32, 32, false, true),
        (32, 32, true, true),
        (16, 16, false, false),
    ] {
        candidates.push(candidate(
            f.nSamplesPerSec,
            f.nChannels,
            bits,
            valid,
            float,
            ext,
            mask,
        )?);
    }
    // SAFETY: read-only property store; PROPVARIANT remains live while copied.
    let property = (|| {
        let store = unsafe { d.OpenPropertyStore(STGM_READ) }?;
        unsafe { store.GetValue(&PKEY_AudioEngine_DeviceFormat) }
    })();
    match property {
        Ok(value) => {
            r.hr("DeviceFormat.GetValue", 0)?;
            // SAFETY: inspect discriminant before reading the blob union member.
            let inner = unsafe { &value.Anonymous.Anonymous };
            if inner.vt == windows::Win32::System::Variant::VT_BLOB {
                // SAFETY: vt is VT_BLOB. Size and pointer are checked before constructing slice.
                let blob = unsafe { inner.Anonymous.blob };
                if !blob.pBlobData.is_null() && (18..=65553).contains(&blob.cbSize) {
                    // SAFETY: property store owns cbSize bytes for the lifetime of value.
                    match Wave::from_bytes(unsafe {
                        std::slice::from_raw_parts(blob.pBlobData, blob.cbSize as usize)
                    }) {
                        Ok(w) => candidates.push(w),
                        Err(e) => r.put("DeviceFormat.invalid", format!("{e:?}")),
                    }
                } else {
                    r.put("DeviceFormat.invalid", "null pointer or invalid blob size");
                }
            } else {
                r.put("DeviceFormat.invalid", "expected VT_BLOB");
            }
        }
        Err(e) => {
            r.put(
                "DeviceFormat.GetValue.hresult",
                format!("0x{:08X}", e.code().0 as u32),
            );
        }
    }
    let test = activate(d, r)?;
    let mut min = 0;
    let mut default = 0;
    // SAFETY: live client and writable output periods.
    r.call("GetDevicePeriod", unsafe {
        test.GetDevicePeriod(Some(&mut default), Some(&mut min))
    })?;
    r.put("exclusive.default_period_hns", default);
    r.put("exclusive.min_period_hns", min);
    if min <= 0 {
        return Err(End::setup("exclusive.min_period", E_INVALID));
    }
    let mut supported = Vec::new();
    for (i, wave) in candidates.into_iter().enumerate() {
        r.put(format!("candidate.{i}.format"), wave.description());
        // SAFETY: wave owns a complete format; exclusive mode requires no closest-match output.
        let hr = unsafe { test.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, wave.ptr(), None) }.0;
        r.put(
            format!("candidate.{i}.IsFormatSupported.hresult"),
            format!("0x{:08X}", hr as u32),
        );
        if hr == 0 {
            supported.push((i, wave));
        }
    }
    drop(test);
    let mut last = End::setup(
        "exclusive.no_supported_format",
        AUDCLNT_E_UNSUPPORTED_FORMAT.0,
    );
    for (i, wave) in supported {
        let mut client = activate(d, r)?;
        let rate = wave.base().nSamplesPerSec;
        let original_frames = hns_frames(min, rate)?;
        r.put(format!("attempt.{i}.buffer_hns"), min);
        r.put(format!("attempt.{i}.periodicity_hns"), min);
        r.put(format!("attempt.{i}.request_frames"), original_frames);
        let mut duration = min;
        r.exclusive_attempted = true;
        let mut hr = initialize_exclusive(&client, &wave, min);
        r.put(
            format!("attempt.{i}.Initialize.hresult"),
            format!("0x{:08X}", hr as u32),
        );
        if hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED.0 {
            // SAFETY: this failed client exposes the aligned buffer size per WASAPI contract.
            let aligned = r.call("alignment.GetBufferSize", unsafe { client.GetBufferSize() })?;
            if rate == 0 || aligned == 0 {
                return Err(End::setup("alignment.invalid", E_INVALID));
            }
            duration = i64::try_from(
                (u64::from(aligned) * 10_000_000 + u64::from(rate) / 2) / u64::from(rate),
            )
            .map_err(|_| End::setup("alignment.overflow", E_INVALID))?;
            r.put(format!("attempt.{i}.aligned_frames"), aligned);
            r.put(format!("attempt.{i}.aligned_hns"), duration);
            drop(client);
            client = activate(d, r)?;
            hr = initialize_exclusive(&client, &wave, duration);
            r.put(
                format!("attempt.{i}.aligned.Initialize.hresult"),
                format!("0x{:08X}", hr as u32),
            );
        }
        if hr >= 0 {
            r.put("selected_candidate", i);
            return Ok((client, wave, original_frames, duration));
        }
        last = End::setup("Initialize.exclusive", hr);
        if hr == AUDCLNT_E_DEVICE_INVALIDATED.0 {
            return Err(last);
        }
    }
    Err(last)
}
fn hns_frames(hns: i64, rate: u32) -> Result<u32, End> {
    let ticks = u64::try_from(hns).map_err(|_| End::setup("period.negative", E_INVALID))?;
    let frames = ticks
        .checked_mul(u64::from(rate))
        .and_then(|v| v.checked_add(5_000_000))
        .map(|v| v / 10_000_000)
        .and_then(|v| u32::try_from(v).ok())
        .ok_or(End::setup("period.overflow", E_INVALID))?;
    Ok(frames)
}
fn initialize_exclusive(c: &IAudioClient, w: &Wave, hns: i64) -> i32 {
    // SAFETY: current-thread client, complete live format, no session GUID.
    unsafe {
        (c.vtable().Initialize)(
            c.as_raw(),
            AUDCLNT_SHAREMODE_EXCLUSIVE,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            hns,
            hns,
            w.ptr(),
            std::ptr::null(),
        )
    }
    .0
}
fn prepare(
    endpoint: &str,
    mode: Mode,
    mmcss: MmcssTask,
    seconds: u32,
    producer: &mut Producer<ProbeEvent>,
    r: &mut Report,
) -> Result<End, End> {
    // Declare the handle owner before clients: it outlives every client reference,
    // including preparation failures after SetEventHandle.
    let event;
    struct PeriodGuard<'a> {
        c: Option<IAudioClient3>,
        report: &'a mut Report,
    }
    impl Drop for PeriodGuard<'_> {
        fn drop(&mut self) {
            if let Some(c) = &self.c {
                let _ = current(c, "before_teardown", self.report);
            }
        }
    }
    let mut period_guard = PeriodGuard { c: None, report: r };
    let r = &mut *period_guard.report;
    r.put(
        "initialization_flags",
        format!("0x{AUDCLNT_STREAMFLAGS_EVENTCALLBACK:08X}"),
    );
    r.put(
        "initialization_api",
        if mode.is_exclusive() {
            "IAudioClient::Initialize(EXCLUSIVE)"
        } else {
            "IAudioClient3::InitializeSharedAudioStream"
        },
    );
    let d = device(endpoint, r)?;
    record_device_format(&d, r);
    // SAFETY: endpoint supports IMMEndpoint for read-only direction query.
    let ep = r.call("QueryInterface.IMMEndpoint", d.cast::<IMMEndpoint>())?;
    // SAFETY: valid endpoint.
    let direction = r.call("GetDataFlow", unsafe { ep.GetDataFlow() })?;
    let flow = if direction == eCapture {
        DataFlow::Capture
    } else if direction == eRender {
        DataFlow::Render
    } else {
        return Err(End::setup("GetDataFlow.invalid", E_FAIL));
    };
    // SAFETY: read-only endpoint properties.
    let name = unsafe { d.OpenPropertyStore(STGM_READ) }
        .map_err(super::error)
        .and_then(|store| super::property(&store, &PKEY_Device_FriendlyName));
    r.put(
        "endpoint.name",
        crate::probe_table::cell(&name, Clone::clone),
    );
    if mode.is_exclusive()
        && let Err(mut e) = preflight(&d, r)
    {
        e.stage = "refused";
        return Err(e);
    }
    let initial = activate(&d, r)?;
    if mode.is_raw() {
        let properties = AudioClientProperties {
            cbSize: std::mem::size_of::<AudioClientProperties>() as u32,
            bIsOffload: false.into(),
            eCategory: AudioCategory_Other,
            Options: AUDCLNT_STREAMOPTIONS_RAW,
        };
        r.put("client_properties.cbSize", properties.cbSize);
        r.put("client_properties.bIsOffload", false);
        r.put(
            "client_properties.category",
            format!("AudioCategory_Other ({})", properties.eCategory.0),
        );
        r.put(
            "client_properties.options",
            format!("AUDCLNT_STREAMOPTIONS_RAW (0x{:X})", properties.Options.0),
        );
        r.put("raw.application_verification", "not_verified; request accepted does not prove processing bypass; endpoint/driver/hardware processing may remain");
        let c2 = r.call(
            "QueryInterface.IAudioClient2",
            initial.cast::<IAudioClient2>(),
        )?;
        // SAFETY: live uninitialized client and complete properties valid for this call.
        let hr = unsafe { (c2.vtable().SetClientProperties)(c2.as_raw(), &properties) }.0;
        r.hr("SetClientProperties", hr)?;
    }
    // SAFETY: live uninitialized client; Wave copies and frees the returned allocation.
    let mix = Wave::from_api(r.call("GetMixFormat", unsafe { initial.GetMixFormat() })?)?;
    r.put("mix.format", mix.description());
    let (client, wave, requested_frames, exclusive_period) = if mode.is_exclusive() {
        drop(initial);
        exclusive(&d, &mix, r)?
    } else {
        let c3 = r.call(
            "QueryInterface.IAudioClient3",
            initial.cast::<IAudioClient3>(),
        )?;
        period_guard.c = Some(c3.clone());
        let _ = current(&c3, "before_initialize", r);
        let (mut default, mut fundamental, mut min, mut max) = (0, 0, 0, 0);
        // SAFETY: complete mix format and independent writable period outputs.
        r.call("GetSharedModeEnginePeriod", unsafe {
            c3.GetSharedModeEnginePeriod(
                mix.ptr(),
                &mut default,
                &mut fundamental,
                &mut min,
                &mut max,
            )
        })?;
        r.put("shared.default_frames", default);
        r.put("shared.fundamental_frames", fundamental);
        r.put("shared.min_frames", min);
        r.put("shared.max_frames", max);
        let frames = if mode.uses_default_period() {
            default
        } else {
            min
        };
        r.put(
            "request.period_semantics",
            if mode.uses_default_period() {
                "IAudioClient3 default_frames requested"
            } else {
                "IAudioClient3 min_frames requested"
            },
        );
        r.put("request.period_frames", frames);
        let request = StreamRequest {
            endpoint_id: endpoint.into(),
            flow,
            mode: mode.stream_mode(),
            period_frames: frames,
            format: mix.format(),
            mmcss,
        };
        r.put("StreamRequest", format!("{request:?}"));
        r.put("used.format", mix.description());
        // SAFETY: supported period requested with mix format, event callback and no session GUID.
        let hr = unsafe {
            (c3.vtable().InitializeSharedAudioStream)(
                c3.as_raw(),
                AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                frames,
                mix.ptr(),
                std::ptr::null(),
            )
        }
        .0;
        let _ = current(&c3, "after_initialize", r);
        r.hr("InitializeSharedAudioStream", hr)?;
        (initial, mix, frames, 0)
    };
    let c3 = if mode.is_exclusive() {
        None
    } else {
        Some(r.call(
            "QueryInterface.IAudioClient3.actual",
            client.cast::<IAudioClient3>(),
        )?)
    };
    let period = if let Some(c) = &c3 {
        current(c, "actual", r)?
    } else {
        hns_frames(exclusive_period, wave.base().nSamplesPerSec)?
    };
    if mode.is_exclusive() {
        r.put("initialization_api", "IAudioClient::Initialize(EXCLUSIVE)");
    }
    r.put(
        "initialization_flags",
        format!("0x{AUDCLNT_STREAMFLAGS_EVENTCALLBACK:08X}"),
    );
    r.put("used.format", wave.description());
    let request = StreamRequest {
        endpoint_id: endpoint.into(),
        flow,
        mode: mode.stream_mode(),
        period_frames: requested_frames,
        format: wave.format(),
        mmcss,
    };
    r.put("StreamRequest", format!("{request:?}"));
    // SAFETY: initialized client, read-only size/latency queries.
    let buffer_frames = r.call("GetBufferSize", unsafe { client.GetBufferSize() })?;
    // SAFETY: initialized client.
    let latency = r.call("GetStreamLatency", unsafe { client.GetStreamLatency() })?;
    // SAFETY: creates a unique auto-reset initially nonsignaled event, no name/security override.
    event = Event(r.call("CreateEventW", unsafe {
        CreateEventW(None, false, false, None)
    })?);
    // SAFETY: event remains alive through Stop and client service use.
    r.call("SetEventHandle", unsafe { client.SetEventHandle(event.0) })?;
    // SAFETY: initialized client provides clock service.
    let clock: IAudioClock = r.call("GetService.IAudioClock", unsafe { client.GetService() })?;
    // SAFETY: live clock.
    let clock_frequency = r.call("IAudioClock.GetFrequency", unsafe { clock.GetFrequency() })?;
    let mut frequency = 0;
    // SAFETY: valid frequency output pointer.
    r.call("QueryPerformanceFrequency", unsafe {
        QueryPerformanceFrequency(&mut frequency)
    })?;
    let frequency = u64::try_from(frequency)
        .ok()
        .filter(|v| *v != 0)
        .ok_or(End::setup("QPC.frequency.invalid", E_FAIL))?;
    r.frequency = frequency;
    let latency =
        u64::try_from(latency).map_err(|_| End::setup("GetStreamLatency.negative", E_FAIL))?;
    let actual = StreamActual {
        buffer_frames,
        period_frames: if mode.is_exclusive() {
            buffer_frames
        } else {
            period
        },
        stream_latency: Hns(latency),
        qpc_frequency: frequency,
        clock_frequency,
    };
    r.put("StreamActual", format!("{actual:?}"));
    r.put("actual.period_basis",if mode.is_exclusive() {"GetBufferSize frames; exclusive event period; requested REFERENCE_TIME recorded separately"}else{"GetCurrentSharedModeEnginePeriod"});
    r.put(
        "actual.period_hns",
        if mode.is_exclusive() {
            exclusive_period.to_string()
        } else {
            "not applicable; engine period measured in frames".into()
        },
    );
    if buffer_frames == 0 {
        return Err(End::setup("buffer.zero", E_FAIL));
    }
    let capture: Option<IAudioCaptureClient> = if flow == DataFlow::Capture {
        // SAFETY: direction-appropriate service.
        Some(r.call("GetService.IAudioCaptureClient", unsafe {
            client.GetService()
        })?)
    } else {
        None
    };
    let render: Option<IAudioRenderClient> = if flow == DataFlow::Render {
        // SAFETY: direction-appropriate service.
        Some(r.call("GetService.IAudioRenderClient", unsafe {
            client.GetService()
        })?)
    } else {
        None
    };
    // Ring storage was allocated and touched on control thread. Touch worker stack
    // workspace and record value before MMCSS/Start; sample buffers are API-owned.
    let mut state = RtState {
        dropped: 0,
        seq: 0,
        wake_seq: 0,
        ended: 0,
    };
    std::hint::black_box(&mut state);
    let mut task_index = 0;
    let priority_before = thread_priority();
    // SAFETY: registers only this worker; each task name is a fixed literal.
    // Transfer a successful HANDLE immediately, before diagnostics can fail/unwind.
    let registration = match mmcss {
        MmcssTask::None => None,
        MmcssTask::Audio => Some(
            unsafe { AvSetMmThreadCharacteristicsW(w!("Audio"), &mut task_index) }
                .map(|h| Mmcss(Some(h))),
        ),
        MmcssTask::ProAudio => Some(
            unsafe { AvSetMmThreadCharacteristicsW(w!("Pro Audio"), &mut task_index) }
                .map(|h| Mmcss(Some(h))),
        ),
    };
    let priority_after = thread_priority();
    r.put("mmcss.thread_priority.before", priority_before);
    r.put("mmcss.thread_priority.after", priority_after);
    r.put(
        "mmcss.thread_priority.semantics",
        "auxiliary GetThreadPriority diagnostic; not MMCSS scheduling priority or latency evidence",
    );
    r.put(
        "mmcss.registration.hresult.semantics",
        "AVRT GetLastError converted to HRESULT by windows crate",
    );
    let mmcss = match registration {
        None => {
            r.put("mmcss.registration", "skipped");
            r.put("mmcss.registration.hresult", "not_applicable");
            r.put("mmcss.task_index", "not_applicable");
            Mmcss(None)
        }
        Some(Ok(guard)) => {
            r.put("mmcss.registration", "success");
            r.put("mmcss.registration.hresult", "0x00000000");
            r.put("mmcss.task_index", task_index);
            if mmcss == MmcssTask::ProAudio {
                r.hr("MMCSS.ProAudio", 0)?;
            }
            guard
        }
        Some(Err(e)) => {
            r.put("mmcss.registration", "failed");
            r.put(
                "mmcss.registration.hresult",
                format!("0x{:08X}", e.code().0 as u32),
            );
            r.put("mmcss.task_index", "not_applicable");
            if mmcss == MmcssTask::ProAudio {
                r.put(
                    "MMCSS.ProAudio.hresult",
                    format!("0x{:08X}", e.code().0 as u32),
                );
            }
            Mmcss(None)
        }
    };
    if let Some(render) = &render {
        let hr = raw_render_get(render, buffer_frames);
        r.hr("render.prefill.GetBuffer", hr)?;
        r.hr(
            "render.prefill.ReleaseBuffer",
            raw_render_release(render, buffer_frames),
        )?;
    }
    let budget = frequency
        .checked_mul(u64::from(seconds))
        .ok_or(End::setup("QPC.budget", E_INVALID))?;
    let (qhr, start) = qpc();
    r.hr("QPC.before_start", qhr)?;
    r.put("run.before_start_qpc", start);
    let mut started = Started {
        client: &client,
        active: false,
    };
    // SAFETY: fully prepared event stream; no realtime function has begun yet.
    let start_hr = unsafe { (client.vtable().Start)(client.as_raw()) }.0;
    if start_hr < 0 {
        r.hr("Start", start_hr)?;
    }
    started.active = true;
    // No allocation, COM wrapper Result/Error, I/O, lock or panic in realtime().
    let outcome = realtime(
        Rt {
            client: &client,
            capture: capture.as_ref(),
            render: render.as_ref(),
            clock: &clock,
            event: event.0,
            exclusive: mode.is_exclusive(),
            buffer: buffer_frames,
            budget,
            start,
        },
        producer,
        &mut state,
    );
    r.hr("Start", start_hr)?;
    r.started = outcome.1;
    r.ended = state.ended;
    r.dropped = state.dropped;
    // SAFETY: active stream, outside realtime loop. Even failure cannot skip other cleanup.
    let stop = unsafe { client.Stop() };
    started.active = false;
    let stop_result = r.call("Stop", stop);
    drop(started);
    drop(mmcss);
    if let Err(mut end) = stop_result {
        end.stage = "teardown";
        return Err(end);
    }
    Ok(outcome.0)
}

pub(super) fn release_check(endpoint: &str, r: &mut Report) {
    let result = (|| {
        let d = device(endpoint, r)?;
        let c = activate(&d, r)?;
        // SAFETY: new client after all old stream objects were released; never Start.
        let wave =
            Wave::from_api(r.call("release_check.GetMixFormat", unsafe { c.GetMixFormat() })?)?;
        // SAFETY: new shared stream initialization only, released immediately on return.
        r.call("release_check.Initialize", unsafe {
            c.Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 0, 0, wave.ptr(), None)
        })?;
        Ok::<(), End>(())
    })();
    match result {
        Ok(()) => r.put("exclusive.release_check", "success"),
        Err(e) => {
            r.successful = false;
            r.put(
                "exclusive.release_check",
                if e.hresult == AUDCLNT_E_DEVICE_INVALIDATED.0 || e.hresult == 0x80070490u32 as i32
                {
                    "device-lost"
                } else {
                    "failed"
                },
            );
            r.put(
                "exclusive.release_check.hresult",
                format!("0x{:08X}", e.hresult as u32),
            );
        }
    }
}

// Raw realtime adapters: each returns the HRESULT verbatim, never builds Error.
mod raw_qpc {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        pub fn QueryPerformanceCounter(value: *mut i64) -> i32;
    }
}
pub(super) fn qpc() -> (i32, u64) {
    let mut value = 0;
    // SAFETY: writable stack output; kernel function has no allocation or COM error wrapper.
    if unsafe { raw_qpc::QueryPerformanceCounter(&mut value) } == 0 {
        return (last_error(), 0);
    }
    match u64::try_from(value) {
        Ok(v) => (0, v),
        Err(_) => (E_FAIL, 0),
    }
}
pub(super) fn last_error() -> i32 {
    // SAFETY: retrieves calling thread's Win32 error without constructing Error.
    let e = unsafe { GetLastError() }.0;
    if e == 0 {
        E_FAIL
    } else {
        windows::core::HRESULT::from_win32(e).0
    }
}
pub(super) fn raw_next(c: &IAudioCaptureClient, n: &mut u32) -> i32 {
    // SAFETY: live worker-owned interface, writable output; caller checks HRESULT.
    unsafe { (c.vtable().GetNextPacketSize)(c.as_raw(), n) }.0
}
#[derive(Default)]
struct Packet {
    frames: u32,
    flags: u32,
    position: u64,
    qpc: u64,
}
fn raw_capture_get(c: &IAudioCaptureClient, p: &mut Packet) -> i32 {
    let mut data = std::ptr::null_mut();
    // SAFETY: all outputs writable, interface live; sample data is deliberately never dereferenced.
    unsafe {
        (c.vtable().GetBuffer)(
            c.as_raw(),
            &mut data,
            &mut p.frames,
            &mut p.flags,
            &mut p.position,
            &mut p.qpc,
        )
    }
    .0
}
pub(super) fn raw_capture_release(c: &IAudioCaptureClient, frames: u32) -> i32 {
    // SAFETY: caller pairs each acquired GetBuffer exactly once, including zero frames.
    unsafe { (c.vtable().ReleaseBuffer)(c.as_raw(), frames) }.0
}
pub(super) fn raw_padding(c: &IAudioClient, n: &mut u32) -> i32 {
    // SAFETY: initialized shared render client, writable output; caller checks HRESULT.
    unsafe { (c.vtable().GetCurrentPadding)(c.as_raw(), n) }.0
}
fn raw_render_get(c: &IAudioRenderClient, frames: u32) -> i32 {
    let mut data = std::ptr::null_mut();
    // SAFETY: requested frame count validated by caller; live interface; data never accessed.
    unsafe { (c.vtable().GetBuffer)(c.as_raw(), frames, &mut data) }.0
}
fn raw_render_release(c: &IAudioRenderClient, frames: u32) -> i32 {
    // SAFETY: paired GetBuffer frame count; SILENT requests zero audio without touching samples.
    unsafe { (c.vtable().ReleaseBuffer)(c.as_raw(), frames, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32) }.0
}
fn raw_position(c: &IAudioClock, position: &mut u64, qpc: &mut u64) -> i32 {
    // SAFETY: live clock and writable outputs; caller checks HRESULT before use.
    unsafe { (c.vtable().GetPosition)(c.as_raw(), position, qpc) }.0
}

struct Rt<'a> {
    client: &'a IAudioClient,
    capture: Option<&'a IAudioCaptureClient>,
    render: Option<&'a IAudioRenderClient>,
    clock: &'a IAudioClock,
    event: HANDLE,
    exclusive: bool,
    buffer: u32,
    budget: u64,
    start: u64,
}
struct RtState {
    dropped: u64,
    seq: u64,
    wake_seq: u64,
    ended: u64,
}
fn emit(p: &mut Producer<ProbeEvent>, s: &mut RtState, e: ProbeEvent) {
    if !p.push(e) {
        s.dropped = s.dropped.wrapping_add(1);
    }
}
fn fail(w: &mut WakeRecord, api: Api, hr: i32) {
    if w.failure_api == Api::None {
        w.failure_api = api;
        w.hresult = hr;
    }
}
fn capture_cycle(
    c: &IAudioCaptureClient,
    exclusive: bool,
    w: &mut WakeRecord,
    p: &mut Producer<ProbeEvent>,
    s: &mut RtState,
) {
    for _ in 0..PACKET_LIMIT {
        if !exclusive {
            let mut next = 0;
            let hr = raw_next(c, &mut next);
            w.statuses.next_packet = Some(hr);
            if hr < 0 {
                fail(w, Api::NextPacket, hr);
                return;
            }
            if next == 0 {
                return;
            }
        }
        let mut packet = Packet::default();
        let hr = raw_capture_get(c, &mut packet);
        w.statuses.capture_get = Some(hr);
        w.last_capture_status = hr;
        if hr == AUDCLNT_S_BUFFER_EMPTY.0 {
            w.buffer_empty_count = w.buffer_empty_count.wrapping_add(1);
            return;
        }
        if hr == AUDCLNT_E_BUFFER_ERROR.0 {
            w.buffer_error_count = w.buffer_error_count.wrapping_add(1);
            return;
        }
        if hr < 0 {
            fail(w, Api::CaptureGet, hr);
            return;
        }
        let (get_hr, after_get) = qpc();
        // Always release an acquired packet even if timestamp acquisition failed.
        let release = raw_capture_release(c, packet.frames);
        let (done_hr, done) = qpc();
        w.statuses.capture_release = Some(release);
        if get_hr < 0 {
            fail(w, Api::Qpc, get_hr);
        }
        if release < 0 {
            fail(w, Api::CaptureRelease, release);
        }
        if done_hr < 0 {
            fail(w, Api::Qpc, done_hr);
        }
        if packet.frames == 0 {
            w.zero_frame_count = w.zero_frame_count.wrapping_add(1);
            return;
        }
        let record = CaptureRecord {
            seq: s.seq,
            wake: QpcTicks(w.wait_return),
            after_get_buffer: QpcTicks(after_get),
            device_position: DevicePosition(packet.position),
            first_frame_qpc: Hns(packet.qpc),
            frames: packet.frames,
            flags: CaptureFlags {
                data_discontinuity: packet.flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32
                    != 0,
                silent: packet.flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0,
                timestamp_error: packet.flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0,
            },
            done: QpcTicks(done),
        };
        emit(p, s, ProbeEvent::Capture(record));
        s.seq = s.seq.wrapping_add(1);
        w.packets = w.packets.wrapping_add(1);
        w.frames = w.frames.wrapping_add(u64::from(packet.frames));
        if exclusive || w.failure_api != Api::None {
            return;
        }
    }
    // A persistent backlog must not hide unbounded work or fabricate normal completion.
    fail(w, Api::PacketLimit, E_FAIL);
}
fn render_cycle(
    rt: &Rt<'_>,
    c: &IAudioRenderClient,
    w: &mut WakeRecord,
    p: &mut Producer<ProbeEvent>,
    s: &mut RtState,
) {
    let mut padding = 0;
    if !rt.exclusive {
        let hr = raw_padding(rt.client, &mut padding);
        w.statuses.padding = Some(hr);
        if hr < 0 {
            fail(w, Api::Padding, hr);
            return;
        }
    }
    let Some(frames) = rt.buffer.checked_sub(padding) else {
        fail(w, Api::Arithmetic, E_FAIL);
        return;
    };
    let mut after_release = w.wait_return;
    if frames != 0 {
        let hr = raw_render_get(c, frames);
        w.statuses.render_get = Some(hr);
        if hr < 0 {
            fail(w, Api::RenderGet, hr);
            return;
        }
        let hr = raw_render_release(c, frames);
        let (qhr, now) = qpc();
        w.statuses.render_release = Some(hr);
        after_release = now;
        if hr < 0 {
            fail(w, Api::RenderRelease, hr);
            return;
        }
        if qhr < 0 {
            fail(w, Api::Qpc, qhr);
            return;
        }
        w.packets = 1;
        w.frames = u64::from(frames);
    }
    let (mut position, mut clock_qpc) = (0, 0);
    let hr = raw_position(rt.clock, &mut position, &mut clock_qpc);
    w.statuses.clock_position = Some(hr);
    if hr < 0 {
        fail(w, Api::ClockPosition, hr);
        return;
    }
    let (qhr, done) = qpc();
    if qhr < 0 {
        fail(w, Api::Qpc, qhr);
        return;
    }
    let Some(work) = done.checked_sub(w.wait_return) else {
        fail(w, Api::Arithmetic, E_FAIL);
        return;
    };
    emit(
        p,
        s,
        ProbeEvent::Render(RenderRecord {
            seq: s.seq,
            wake: QpcTicks(w.wait_return),
            padding_before: padding,
            frames_written: frames,
            after_release: QpcTicks(after_release),
            clock_position: position,
            clock_qpc: Hns(clock_qpc),
            source: None,
            work: QpcTicks(work),
            deadline_missed: false,
        }),
    );
    s.seq = s.seq.wrapping_add(1);
}
fn realtime(rt: Rt<'_>, p: &mut Producer<ProbeEvent>, s: &mut RtState) -> (End, u64) {
    let (hr, started) = qpc();
    if hr < 0 {
        s.ended = rt.start;
        return (
            End {
                stage: "realtime",
                api: "Qpc",
                hresult: hr,
            },
            rt.start,
        );
    }
    let mut timeouts = 0u32;
    let mut buffer_errors = 0u32;
    loop {
        let (hr, before) = qpc();
        s.ended = before;
        if hr < 0 {
            return (
                End {
                    stage: "realtime",
                    api: "Qpc",
                    hresult: hr,
                },
                started,
            );
        }
        if STOP.load(Ordering::Acquire) {
            return (
                End {
                    stage: "complete",
                    api: "stop_flag",
                    hresult: 0,
                },
                started,
            );
        }
        let Some(elapsed) = before.checked_sub(started) else {
            return (
                End {
                    stage: "realtime",
                    api: "Qpc.backwards",
                    hresult: E_FAIL,
                },
                started,
            );
        };
        if elapsed >= rt.budget {
            return (
                End {
                    stage: "complete",
                    api: "duration",
                    hresult: 0,
                },
                started,
            );
        }
        // SAFETY: unique auto-reset event remains live through the entire realtime function.
        let wait = unsafe { WaitForSingleObject(rt.event, 200) };
        // GetLastError must precede any subsequent Win32 call on wait failure.
        let wait_hr = if wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT {
            last_error()
        } else {
            0
        };
        let (qhr, returned) = qpc();
        let result = if wait == WAIT_OBJECT_0 {
            WaitResult::Event
        } else if wait == WAIT_TIMEOUT {
            WaitResult::Timeout
        } else {
            WaitResult::Failed
        };
        let mut w = WakeRecord {
            wake_seq: s.wake_seq,
            wait_start: before,
            wait_return: returned,
            result,
            packets: 0,
            frames: 0,
            done: returned,
            failure_api: Api::None,
            hresult: 0,
            buffer_empty_count: 0,
            buffer_error_count: 0,
            zero_frame_count: 0,
            last_capture_status: 0,
            statuses: CallStatuses::default(),
        };
        if wait_hr < 0 {
            fail(&mut w, Api::Wait, wait_hr);
        }
        if qhr < 0 {
            fail(&mut w, Api::Qpc, qhr);
        }
        if w.failure_api == Api::None {
            if result == WaitResult::Timeout {
                timeouts = timeouts.wrapping_add(1);
            } else {
                timeouts = 0;
                if let Some(c) = rt.capture {
                    capture_cycle(c, rt.exclusive, &mut w, p, s);
                }
                if let Some(c) = rt.render {
                    render_cycle(&rt, c, &mut w, p, s);
                }
            }
        }
        if w.packets != 0 {
            buffer_errors = 0;
        } else if w.buffer_error_count != 0 {
            buffer_errors = buffer_errors.saturating_add(1);
        } else {
            buffer_errors = 0;
        }
        let (qhr, done) = qpc();
        if qhr < 0 {
            fail(&mut w, Api::Qpc, qhr);
        }
        w.done = done;
        s.ended = done;
        emit(p, s, ProbeEvent::Wake(w));
        s.wake_seq = s.wake_seq.wrapping_add(1);
        if w.failure_api != Api::None {
            return (
                End {
                    stage: "realtime",
                    api: api_name(w.failure_api),
                    hresult: w.hresult,
                },
                started,
            );
        }
        if buffer_errors >= 10 {
            return (
                End {
                    stage: "realtime",
                    api: "consecutive_buffer_errors",
                    hresult: AUDCLNT_E_BUFFER_ERROR.0,
                },
                started,
            );
        }
        if timeouts >= 10 {
            return (
                End {
                    stage: "realtime",
                    api: "consecutive_timeouts",
                    hresult: E_FAIL,
                },
                started,
            );
        }
    }
}
fn api_name(api: Api) -> &'static str {
    match api {
        Api::None => "None",
        Api::Qpc => "Qpc",
        Api::Wait => "WaitForSingleObject",
        Api::NextPacket => "GetNextPacketSize",
        Api::CaptureGet => "capture.GetBuffer",
        Api::CaptureRelease => "capture.ReleaseBuffer",
        Api::Padding => "GetCurrentPadding",
        Api::RenderGet => "render.GetBuffer",
        Api::RenderRelease => "render.ReleaseBuffer",
        Api::ClockPosition => "IAudioClock.GetPosition",
        Api::Arithmetic => "checked_arithmetic",
        Api::PacketLimit => "packet_iteration_limit",
    }
}
