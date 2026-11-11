//! Microphone-to-headphone diagnostic; not WASAPI output loopback.
use super::stream::{
    self, BUSY, E_FAIL, E_INVALID, End, Event, Mmcss, Mode, PACKET_LIMIT, Report, STOP,
    WORKER_DONE, Wave, qpc,
};
use crate::{
    MmcssTask,
    loopback_report::{self as report, *},
    spsc::{self, Consumer, Producer},
};
use std::{
    path::PathBuf,
    sync::{atomic::Ordering, mpsc},
    time::Duration,
};
use windows::{
    Win32::{
        Devices::FunctionDiscovery::PKEY_Device_FriendlyName,
        Foundation::{WAIT_OBJECT_0, WAIT_TIMEOUT},
        Media::Audio::*,
        System::{
            Com::*, Console::SetConsoleCtrlHandler, Performance::QueryPerformanceFrequency,
            RemoteDesktop::ProcessIdToSessionId, Threading::*,
        },
    },
    core::{Interface, w},
};

struct Started<'a> {
    client: &'a IAudioClient,
    active: bool,
}
impl Drop for Started<'_> {
    fn drop(&mut self) {
        if self.active {
            // SAFETY: active client on its owning thread; raw fallback avoids Error construction.
            let _ = unsafe { (self.client.vtable().Stop)(self.client.as_raw()) };
        }
    }
}

pub struct Options {
    pub capture: String,
    pub render: String,
    pub capture_mode: Mode,
    pub render_mode: Mode,
    pub mmcss: MmcssTask,
    pub seconds: u32,
    pub gain_db: f32,
    pub prefill_frames: Option<u32>,
    pub out: PathBuf,
}
#[derive(Clone, Copy)]
struct Ready {
    period: u32,
    buffer: u32,
    layout: SampleLayout,
}
enum Status {
    Ready(Ready),
    Failed,
}
enum Command {
    Go { deadline_qpc: u64, prefill: u32 },
    Abort,
}
struct Sync {
    status: mpsc::Sender<Status>,
    command: mpsc::Receiver<Command>,
}
enum Samples {
    Capture(Producer<[f32; 2]>),
    Render(Consumer<[f32; 2]>),
}
/// Last result per realtime API, or the first failing result (loop then stops).
/// None explicitly means this API was never called.
#[derive(Default)]
struct RtHresults {
    next: Option<i32>,
    capture_get: Option<i32>,
    capture_release: Option<i32>,
    padding: Option<i32>,
    render_get: Option<i32>,
    render_release: Option<i32>,
    qpc: Option<i32>,
    wait: Option<i32>,
}
#[derive(Default)]
struct Outcome {
    capture: CaptureTotals,
    render: RenderTotals,
    before: u64,
    after: u64,
    end: u64,
    hr: i32,
    reason: &'static str,
    stop_hr: Option<i32>,
    apis: RtHresults,
    buffer_empty_count: u64,
    buffer_error_count: u64,
    consecutive_buffer_errors: u32,
}
struct Returned {
    samples: Samples,
    outcome: Outcome,
    report: Report,
}

pub fn run(o: Options) -> Result<stream::Output, Box<dyn std::error::Error>> {
    let gain = gain_linear(o.gain_db).ok_or("gain-db must be -60..=0")?;
    if !(1..=120).contains(&o.seconds)
        || o.prefill_frames.is_some_and(|p| p > 16384)
        || [&o.capture, &o.render]
            .iter()
            .any(|id| id.is_empty() || id.contains('\0'))
    {
        return Err("invalid seconds (1..=120), prefill (0..=16384), or endpoint ID".into());
    }
    if BUSY
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
        .is_err()
    {
        return Err("stream probe already running".into());
    }
    STOP.store(false, Ordering::Release);
    WORKER_DONE.store(false, Ordering::Release);
    // SAFETY: same static handler and lifetime guard as stream; only control sets DONE.
    let registered = unsafe { SetConsoleCtrlHandler(Some(stream::console_handler), true) };
    let _console = stream::ConsoleGuard(registered.is_ok());
    let _done = Done;
    let (time, stamp) = super::timestamp();
    std::fs::create_dir_all(&o.out)?;
    let records_path = o.out.join(format!("loopback-{stamp}-records.csv"));
    let meta_path = o.out.join(format!("loopback-{stamp}-meta.txt"));
    let open = |p: &PathBuf| {
        std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(p)
    };
    let mut csv = open(&records_path)?;
    let mut meta = open(&meta_path)?;
    let mut frequency = 0;
    // SAFETY: writable frequency output, no device access.
    unsafe { QueryPerformanceFrequency(&mut frequency) }?;
    let frequency = u64::try_from(frequency)
        .ok()
        .filter(|n| *n != 0)
        .ok_or("invalid QPC frequency")?;
    let mut fields = Report::default();
    fields.put("executed_at_utc", time);
    fields.put("os", super::os_version());
    fields.put("tool_version", env!("CARGO_PKG_VERSION"));
    fields.put("commit", crate::provenance::commit_label());
    fields.put(
        "ctrl_c.registration.hresult",
        format!(
            "0x{:08X}",
            registered.err().map_or(0, |e| e.code().0) as u32
        ),
    );
    let mut session = 0;
    // SAFETY: writable output, query current process only.
    let session_hr = unsafe { ProcessIdToSessionId(std::process::id(), &mut session) };
    fields.put(
        "process_session.hresult",
        format!(
            "0x{:08X}",
            session_hr.as_ref().err().map_or(0, |e| e.code().0) as u32
        ),
    );
    if session_hr.is_ok() {
        fields.put("process_session.id", session);
    }
    fields.put("measurement", "internal diagnostic record only; no mouth-to-ear latency judgment; ratio is ring-to-WASAPI submission boundary, not full source preservation");
    fields.put("gain_db", o.gain_db);
    fields.put(
        "prefill_frames.request",
        o.prefill_frames
            .map_or_else(|| "capture actual period".into(), |p| p.to_string()),
    );
    fields.put("ring_capacity", 16384);
    fields.put("requested_seconds", o.seconds);
    fields.put("qpc_frequency", frequency);
    fields.put("csv.order", "wait_return_qpc ascending");
    let (p, c, keeper) =
        spsc::channel_with_keeper::<[f32; 2]>(16384).ok_or("invalid sample ring")?;
    let (cp, mut cc) = spsc::channel::<LoopEvent>(131072).ok_or("invalid capture record ring")?;
    let (rp, mut rc) = spsc::channel::<LoopEvent>(131072).ok_or("invalid render record ring")?;
    let (cs, cr) = mpsc::channel();
    let (cg, ci) = mpsc::channel();
    let (rs, rr) = mpsc::channel();
    let (rg, ri) = mpsc::channel();
    let capture_endpoint = o.capture.clone();
    let render_endpoint = o.render.clone();
    let capture_mode = o.capture_mode;
    let render_mode = o.render_mode;
    let cap = std::thread::Builder::new()
        .name("loopback-capture".into())
        .spawn(move || {
            worker(
                o.capture,
                o.capture_mode,
                o.mmcss,
                Samples::Capture(p),
                cp,
                Sync {
                    status: cs,
                    command: ci,
                },
                gain,
            )
        })?;
    let ren = std::thread::Builder::new()
        .name("loopback-render".into())
        .spawn(move || {
            worker(
                o.render,
                o.render_mode,
                o.mmcss,
                Samples::Render(c),
                rp,
                Sync {
                    status: rs,
                    command: ri,
                },
                gain,
            )
        });
    let ren = match ren {
        Ok(h) => h,
        Err(e) => {
            STOP.store(true, Ordering::Release);
            let _ = cg.send(Command::Abort);
            let joined = cap.join();
            let attempted = joined
                .as_ref()
                .map_or(true, |ret| ret.report.exclusive_attempted);
            check_release(
                "capture",
                &capture_endpoint,
                capture_mode,
                attempted,
                &mut fields,
            );
            if let Ok(ret) = &joined {
                for (k, v) in &ret.report.fields {
                    fields.put(format!("capture.{k}"), v);
                }
            }
            use std::io::Write;
            csv.write_all(report::csv(&[]).as_bytes())?;
            csv.flush()?;
            meta.write_all(crate::stream_report::metadata(&fields.fields).as_bytes())?;
            meta.flush()?;
            drop(joined);
            drop(keeper);
            return Err(e.into());
        }
    };
    let ready = |rx: &mpsc::Receiver<Status>| -> Option<Ready> {
        loop {
            if STOP.load(Ordering::Acquire) {
                return None;
            }
            match rx.recv_timeout(Duration::from_millis(20)) {
                Ok(Status::Ready(r)) => return Some(r),
                Ok(Status::Failed) | Err(mpsc::RecvTimeoutError::Disconnected) => return None,
                Err(mpsc::RecvTimeoutError::Timeout) => (),
            }
        }
    };
    let a = ready(&cr);
    let b = if a.is_some() { ready(&rr) } else { None };
    let valid = |r: Ready| {
        matches!(r.layout, SampleLayout::F32 | SampleLayout::I16)
            && r.period != 0
            && r.buffer != 0
            && r.period <= r.buffer
    };
    let prefill = a.map(|r| o.prefill_frames.unwrap_or(r.period));
    let (qhr, now) = qpc();
    let deadline = frequency
        .checked_mul(u64::from(o.seconds))
        .and_then(|n| now.checked_add(n));
    if a.is_some_and(valid)
        && b.is_some_and(valid)
        && prefill.is_some_and(|p| p <= 16384)
        && qhr >= 0
        && deadline.is_some()
        && !STOP.load(Ordering::Acquire)
    {
        if let (Some(deadline_qpc), Some(prefill)) = (deadline, prefill) {
            fields.put("deadline_qpc", deadline_qpc);
            fields.put("prefill_frames.applied", prefill);
            let sent_a = cg
                .send(Command::Go {
                    deadline_qpc,
                    prefill,
                })
                .is_ok();
            let sent_b = rg
                .send(Command::Go {
                    deadline_qpc,
                    prefill,
                })
                .is_ok();
            if !sent_a || !sent_b {
                STOP.store(true, Ordering::Release);
            }
        }
    } else {
        STOP.store(true, Ordering::Release);
        let _ = cg.send(Command::Abort);
        let _ = rg.send(Command::Abort);
        fields.put("start_coordination", "aborted before Start");
    }
    let mut events = Vec::new();
    while !cap.is_finished() || !ren.is_finished() {
        while let Some(e) = cc.pop() {
            events.push(e);
        }
        while let Some(e) = rc.pop() {
            events.push(e);
        }
        if (cap.is_finished() || ren.is_finished()) && !STOP.load(Ordering::Acquire) {
            STOP.store(true, Ordering::Release);
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    // Always join both, including when the first join reports a panic.
    let a = cap.join();
    let b = ren.join();
    while let Some(e) = cc.pop() {
        events.push(e);
    }
    while let Some(e) = rc.pop() {
        events.push(e);
    }
    events.sort_by_key(|e| match e {
        LoopEvent::Capture(w) => w.wait_return_qpc,
        LoopEvent::Render(w) => w.wait_return_qpc,
    });
    let mut ct = CaptureTotals::default();
    let mut rt = RenderTotals::default();
    let mut final_frames = 0;
    let mut successful = true;
    for (prefix, endpoint, mode, joined) in [
        ("capture", &capture_endpoint, capture_mode, a),
        ("render", &render_endpoint, render_mode, b),
    ] {
        let attempted = joined
            .as_ref()
            .map_or(true, |ret| ret.report.exclusive_attempted);
        successful &= check_release(prefix, endpoint, mode, attempted, &mut fields);
        match joined {
            Ok(ret) => {
                let end = &ret.outcome;
                if mode.is_exclusive() && prefix == "capture" {
                    fields.put("capture.buffer_empty_count", end.buffer_empty_count);
                    fields.put("capture.buffer_error_count", end.buffer_error_count);
                }
                successful &= matches!(end.reason, "duration" | "stop_flag")
                    && end.hr >= 0
                    && end.stop_hr.is_some_and(|hr| hr >= 0)
                    && end.after != 0;
                fields.put(format!("{prefix}.start.before_qpc"), end.before);
                fields.put(format!("{prefix}.start.after_qpc"), end.after);
                fields.put(format!("{prefix}.end.qpc"), end.end);
                fields.put(format!("{prefix}.end.reason"), end.reason);
                fields.put(
                    format!("{prefix}.end.hresult"),
                    format!("0x{:08X}", end.hr as u32),
                );
                fields.put(
                    format!("{prefix}.Stop.hresult"),
                    end.stop_hr
                        .map_or_else(|| "not_called".into(), |hr| format!("0x{:08X}", hr as u32)),
                );
                for (api, hr) in [
                    ("GetNextPacketSize", end.apis.next),
                    (
                        "GetBuffer",
                        if prefix == "capture" {
                            end.apis.capture_get
                        } else {
                            end.apis.render_get
                        },
                    ),
                    (
                        "ReleaseBuffer",
                        if prefix == "capture" {
                            end.apis.capture_release
                        } else {
                            end.apis.render_release
                        },
                    ),
                    ("GetCurrentPadding", end.apis.padding),
                    ("QPC.realtime", end.apis.qpc),
                    ("WaitForSingleObject", end.apis.wait),
                ] {
                    fields.put(
                        format!("{prefix}.{api}.hresult"),
                        hr.map_or_else(|| "not_called".into(), |h| format!("0x{:08X}", h as u32)),
                    );
                }
                fields.put(format!("{prefix}.realtime_api_status_semantics"), "last result; loop stops on first failing API; waits mapped to HRESULT, timeouts are S_OK; except exclusive capture AUDCLNT_E_BUFFER_ERROR, which is counted in capture.buffer_error_count and is non-fatal until 10 consecutive GetBuffer results");
                for (k, v) in ret.report.fields {
                    fields.put(format!("{prefix}.{k}"), v);
                }
                if prefix == "capture" {
                    ct = end.capture;
                } else {
                    rt = end.render;
                }
                if let Samples::Render(c) = ret.samples {
                    final_frames = c.len() as u64;
                }
            }
            Err(_) => {
                successful = false;
                fields.put(
                    format!("{prefix}.end.reason"),
                    "worker panic; cleanup attempted",
                );
            }
        }
    }
    drop(keeper);
    let summary = report::summarize(&events, frequency, &ct, &rt, final_frames);
    successful &=
        summary.accounting.capture_ok && summary.accounting.ring_ok && summary.accounting.render_ok;
    let text = summary.text();
    use std::io::Write;
    csv.write_all(report::csv(&events).as_bytes())?;
    csv.flush()?;
    meta.write_all(crate::stream_report::metadata(&fields.fields).as_bytes())?;
    meta.write_all(text.as_bytes())?;
    meta.flush()?;
    Ok(stream::Output {
        records: records_path,
        metadata: meta_path,
        summary: text,
        successful,
    })
}

fn worker(
    endpoint: String,
    mode: Mode,
    mmcss: MmcssTask,
    mut samples: Samples,
    mut records: Producer<LoopEvent>,
    sync: Sync,
    gain: f32,
) -> Returned {
    let mut r = Report::default();
    r.put("mmcss.request", mmcss.as_str());
    let mut outcome = Outcome {
        reason: "prepare_error",
        ..Outcome::default()
    };
    r.put("endpoint.id", &endpoint);
    r.put("mode", mode.name());
    // SAFETY: MTA initialized and torn down on this worker; no COM value escapes.
    let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.0;
    let result = if r.hr("CoInitializeEx", hr).is_ok() {
        let _com = super::Com;
        prepare(
            &endpoint,
            mode,
            mmcss,
            &mut samples,
            &mut records,
            &sync,
            gain,
            &mut outcome,
            &mut r,
        )
    } else {
        Err(End::setup("CoInitializeEx", hr))
    };
    if let Err(e) = result {
        STOP.store(true, Ordering::Release);
        let _ = sync.status.send(Status::Failed);
        if mode.is_exclusive() {
            r.put("end.stage", e.stage);
        }
        r.put("failure", format!("{e:?}"));
        outcome.hr = e.hresult;
    }
    Returned {
        samples,
        outcome,
        report: r,
    }
}

#[allow(clippy::too_many_arguments)]
fn prepare(
    endpoint: &str,
    mode: Mode,
    mmcss: MmcssTask,
    samples: &mut Samples,
    records: &mut Producer<LoopEvent>,
    sync: &Sync,
    gain: f32,
    out: &mut Outcome,
    r: &mut Report,
) -> Result<(), End> {
    // Event declared first: all clients/services die before its handle closes.
    let event;
    let d = stream::device(endpoint, r)?;
    let ep = r.call("QueryInterface.IMMEndpoint", d.cast::<IMMEndpoint>())?;
    // SAFETY: initialized apartment and live endpoint; read-only property queries.
    let flow = r.call("GetDataFlow", unsafe { ep.GetDataFlow() })?;
    let capture = matches!(samples, Samples::Capture(_));
    if flow != if capture { eCapture } else { eRender } {
        return Err(End::setup("endpoint.direction", E_INVALID));
    }
    let name = unsafe { d.OpenPropertyStore(STGM_READ) }
        .map_err(super::error)
        .and_then(|s| super::property(&s, &PKEY_Device_FriendlyName));
    r.put(
        "endpoint.name",
        crate::probe_table::cell(&name, Clone::clone),
    );
    if mode.is_exclusive()
        && let Err(mut e) = stream::preflight(&d, r)
    {
        e.stage = "refused";
        return Err(e);
    }
    let (client, _wave, period, buffer, layout, _shared_client3) = if mode.is_exclusive() {
        let initial = stream::activate(&d, r)?;
        // SAFETY: copy API allocation before releasing initial client.
        let mix = Wave::from_api(r.call("GetMixFormat", unsafe { initial.GetMixFormat() })?)?;
        r.put("mix.format", mix.description());
        drop(initial);
        let (client, wave, _, _) = stream::exclusive(&d, &mix, r)?;
        r.put("used.format", wave.description());
        let Some(layout) = sample_layout(wave.bytes()) else {
            r.put("exclusive.format_policy", "first format that Initialize accepted is used; loopback supports only F32 or PCM16 at 48 kHz stereo and does not retry later candidates");
            return Err(End::setup("format.unsupported", E_INVALID));
        };
        // SAFETY: initialized exclusive client before Start.
        let buffer = r.call("GetBufferSize", unsafe { client.GetBufferSize() })?;
        r.put("actual.buffer_frames", buffer);
        r.put("actual.period_frames", buffer);
        r.put("exclusive.volume", "exclusive stream bypasses session volume; gain is the only software attenuation in this probe");
        (client, wave, buffer, buffer, layout, None)
    } else {
        let client = stream::activate(&d, r)?;
        r.put("raw.requested", mode.is_raw());
        if mode.is_raw() {
            let properties = AudioClientProperties {
                cbSize: std::mem::size_of::<AudioClientProperties>() as u32,
                bIsOffload: false.into(),
                eCategory: AudioCategory_Other,
                Options: AUDCLNT_STREAMOPTIONS_RAW,
            };
            r.put("client_properties.cbSize", properties.cbSize);
            r.put("client_properties.bIsOffload", false);
            r.put("client_properties.category", "AudioCategory_Other");
            r.put(
                "client_properties.options",
                "AUDCLNT_STREAMOPTIONS_RAW (0x1)",
            );
            r.put(
                "raw.application_verification",
                "not_verified; acceptance does not prove processing bypass",
            );
            let c2 = r.call(
                "QueryInterface.IAudioClient2",
                client.cast::<IAudioClient2>(),
            )?;
            // SAFETY: complete properties and uninitialized live client; retain raw HRESULT.
            r.hr(
                "SetClientProperties",
                unsafe { (c2.vtable().SetClientProperties)(c2.as_raw(), &properties) }.0,
            )?;
        }
        // SAFETY: API returns owned format allocation, copied and freed by Wave.
        let wave = Wave::from_api(r.call("GetMixFormat", unsafe { client.GetMixFormat() })?)?;
        r.put("mix.format", wave.description());
        if !is_f32_stereo_48k(wave.bytes()) {
            return Err(End::setup("format.unsupported", E_INVALID));
        }
        let c3 = r.call(
            "QueryInterface.IAudioClient3",
            client.cast::<IAudioClient3>(),
        )?;
        let _ = stream::current(&c3, "before_initialize", r);
        let (mut default, mut fundamental, mut min, mut max) = (0, 0, 0, 0);
        // SAFETY: complete format and writable period outputs.
        r.call("GetSharedModeEnginePeriod", unsafe {
            c3.GetSharedModeEnginePeriod(
                wave.ptr(),
                &mut default,
                &mut fundamental,
                &mut min,
                &mut max,
            )
        })?;
        for (k, v) in [
            ("default", default),
            ("fundamental", fundamental),
            ("min", min),
            ("max", max),
        ] {
            r.put(format!("shared.{k}_frames"), v);
        }
        let requested = if mode.uses_default_period() {
            default
        } else {
            min
        };
        r.put("request.period_frames", requested);
        // SAFETY: live client, validated mix format, queried period; no conversion flags.
        let hr = unsafe {
            (c3.vtable().InitializeSharedAudioStream)(
                c3.as_raw(),
                AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                requested,
                wave.ptr(),
                std::ptr::null(),
            )
        }
        .0;
        let _ = stream::current(&c3, "after_initialize", r);
        r.hr("InitializeSharedAudioStream", hr)?;
        // SAFETY: initialized client.
        let buffer = r.call("GetBufferSize", unsafe { client.GetBufferSize() })?;
        let period = stream::current(&c3, "actual", r)?;
        r.put("actual.buffer_frames", buffer);
        r.put("actual.period_frames", period);
        (client, wave, period, buffer, SampleLayout::F32, Some(c3))
    };
    r.put(
        "sample_layout",
        match layout {
            SampleLayout::F32 => "F32",
            SampleLayout::I16 => "I16",
        },
    );
    if period == 0 || buffer == 0 || period > buffer {
        return Err(End::setup("period.invalid", E_INVALID));
    }
    // SAFETY: unique unnamed auto-reset event, alive through all client releases.
    event = Event(r.call("CreateEventW", unsafe {
        CreateEventW(None, false, false, None)
    })?);
    r.call("SetEventHandle", unsafe { client.SetEventHandle(event.0) })?;
    // SAFETY: direction-appropriate service of initialized client.
    let cap: Option<IAudioCaptureClient> = if capture {
        Some(r.call("GetService.IAudioCaptureClient", unsafe {
            client.GetService()
        })?)
    } else {
        None
    };
    let ren: Option<IAudioRenderClient> = if !capture {
        Some(r.call("GetService.IAudioRenderClient", unsafe {
            client.GetService()
        })?)
    } else {
        None
    };
    let mut task_index = 0;
    let priority_before = stream::thread_priority();
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
    let priority_after = stream::thread_priority();
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
            return Err(End::setup(
                if mmcss == MmcssTask::ProAudio {
                    "MMCSS.ProAudio"
                } else {
                    "MMCSS.Audio"
                },
                e.code().0,
            ));
        }
    };
    if let Some(c) = &ren {
        let silence_frames = if mode.is_exclusive() { buffer } else { period };
        let mut ptr = std::ptr::null_mut();
        r.hr("prefill.GetBuffer", render_get(c, silence_frames, &mut ptr))?;
        r.hr(
            "prefill.ReleaseBuffer",
            render_release(c, silence_frames, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32),
        )?;
        r.put("prefill.silence_frames", silence_frames);
    }
    sync.status
        .send(Status::Ready(Ready {
            period,
            buffer,
            layout,
        }))
        .map_err(|_| End::setup("ready.disconnected", E_FAIL))?;
    let (deadline, prefill) = match sync.command.recv() {
        Ok(Command::Go {
            deadline_qpc,
            prefill,
        }) => (deadline_qpc, prefill),
        _ => {
            out.reason = "abort";
            return Ok(());
        }
    };
    let mut priming = Priming::new(prefill);
    let exclusive = mode.is_exclusive();
    std::hint::black_box(&mut priming);
    std::hint::black_box(&mut *out);
    if STOP.load(Ordering::Acquire) {
        out.reason = "abort";
        return Ok(());
    }
    let (hr, before) = qpc();
    r.hr("QPC.before_start", hr)?;
    out.before = before;
    // SAFETY: prepared stream; raw calls avoid Error construction throughout active interval.
    let start_hr = unsafe { (client.vtable().Start)(client.as_raw()) }.0;
    if start_hr < 0 {
        STOP.store(true, Ordering::Release);
        out.reason = "start_error";
        r.hr("Start", start_hr)?;
    }
    let mut started = Started {
        client: &client,
        active: true,
    };
    let (after_hr, after) = qpc();
    out.after = after;
    if after_hr < 0 {
        out.hr = after_hr;
        out.reason = "qpc_error";
        STOP.store(true, Ordering::Release);
    } else {
        realtime(
            &client,
            cap.as_ref(),
            ren.as_ref(),
            event.0,
            period,
            buffer,
            deadline,
            samples,
            records,
            gain,
            exclusive,
            layout,
            &mut priming,
            out,
        );
    }
    // SAFETY: active client on owning thread; stop before formatting or cleanup.
    let stop_hr = unsafe { (client.vtable().Stop)(client.as_raw()) }.0;
    started.active = false;
    out.stop_hr = Some(stop_hr);
    if stop_hr < 0 {
        STOP.store(true, Ordering::Release);
    }
    r.hr("Start", start_hr)?;
    r.hr("QPC.after_start", after_hr)?;
    drop(mmcss);
    Ok(())
}

fn render_get(c: &IAudioRenderClient, n: u32, p: &mut *mut u8) -> i32 {
    // SAFETY: live render service and writable pointer output; n bounded by buffer/padding.
    unsafe { (c.vtable().GetBuffer)(c.as_raw(), n, p) }.0
}
fn render_release(c: &IAudioRenderClient, n: u32, flags: u32) -> i32 {
    // SAFETY: paired with successful GetBuffer(n), same thread, flags 0 or SILENT.
    unsafe { (c.vtable().ReleaseBuffer)(c.as_raw(), n, flags) }.0
}
fn capture_get(
    c: &IAudioCaptureClient,
    p: &mut *mut u8,
    n: &mut u32,
    flags: &mut u32,
    pos: &mut u64,
    time: &mut u64,
) -> i32 {
    // SAFETY: live capture service and distinct writable outputs; pointer used only until release.
    unsafe { (c.vtable().GetBuffer)(c.as_raw(), p, n, flags, pos, time) }.0
}

#[allow(clippy::too_many_arguments)]
fn realtime(
    client: &IAudioClient,
    cap: Option<&IAudioCaptureClient>,
    ren: Option<&IAudioRenderClient>,
    event: windows::Win32::Foundation::HANDLE,
    period: u32,
    buffer: u32,
    deadline: u64,
    samples: &mut Samples,
    records: &mut Producer<LoopEvent>,
    gain: f32,
    exclusive: bool,
    layout: SampleLayout,
    priming: &mut Priming,
    out: &mut Outcome,
) {
    let mut timeouts = 0u32;
    loop {
        let (hr, now) = qpc();
        if !out.apis.qpc.is_some_and(|previous| previous < 0) {
            out.apis.qpc = Some(hr);
        }
        if hr < 0 {
            out.hr = hr;
            out.reason = "qpc_error";
            break;
        }
        if now >= deadline {
            out.reason = "duration";
            break;
        }
        if STOP.load(Ordering::Acquire) {
            out.reason = "stop_flag";
            break;
        }
        // SAFETY: event remains owned by prepare; bounded wait, no wrapper Error.
        let wait = unsafe { WaitForSingleObject(event, 200) };
        let wait_hr = if wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT {
            stream::last_error()
        } else {
            0
        };
        out.apis.wait = Some(wait_hr);
        let (hr, time) = qpc();
        if !out.apis.qpc.is_some_and(|previous| previous < 0) {
            out.apis.qpc = Some(hr);
        }
        let event_ready = wait == WAIT_OBJECT_0;
        let failure = if hr < 0 {
            hr
        } else if wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT {
            wait_hr
        } else {
            0
        };
        timeouts = if wait == WAIT_TIMEOUT {
            timeouts.saturating_add(1)
        } else {
            0
        };
        let mut wake_hr = failure;
        match samples {
            Samples::Capture(p) => {
                let mut w = CaptureWake {
                    wake_seq: out.capture.wakes,
                    wait_start_qpc: now,
                    wait_return_qpc: time,
                    event: event_ready,
                    failure_hresult: failure,
                    ..CaptureWake::default()
                };
                if failure >= 0
                    && event_ready
                    && let Some(c) = cap
                {
                    capture_cycle(c, p, &mut w, out, exclusive, layout);
                }
                let (hr, done) = qpc();
                if !out.apis.qpc.is_some_and(|previous| previous < 0) {
                    out.apis.qpc = Some(hr);
                }
                w.done_qpc = done;
                if hr < 0 {
                    w.failure_hresult = hr;
                }
                wake_hr = wake_hr.min(w.failure_hresult);
                out.capture.add(&w);
                if !records.push(LoopEvent::Capture(w)) {
                    out.capture.dropped_records = out.capture.dropped_records.saturating_add(1);
                }
            }
            Samples::Render(c) => {
                let mut w = RenderWake {
                    wake_seq: out.render.wakes,
                    wait_start_qpc: now,
                    wait_return_qpc: time,
                    event: event_ready,
                    failure_hresult: failure,
                    ..RenderWake::default()
                };
                if failure >= 0
                    && event_ready
                    && let Some(r) = ren
                {
                    render_cycle(
                        client,
                        r,
                        c,
                        period,
                        buffer,
                        gain,
                        exclusive,
                        layout,
                        priming,
                        &mut w,
                        &mut out.apis,
                    );
                }
                let (hr, done) = qpc();
                if !out.apis.qpc.is_some_and(|previous| previous < 0) {
                    out.apis.qpc = Some(hr);
                }
                w.done_qpc = done;
                if hr < 0 {
                    w.failure_hresult = hr;
                }
                wake_hr = wake_hr.min(w.failure_hresult);
                out.render.add(&w);
                if !records.push(LoopEvent::Render(w)) {
                    out.render.dropped_records = out.render.dropped_records.saturating_add(1);
                }
            }
        }
        if wake_hr < 0 {
            out.hr = wake_hr;
            out.reason = "api_error";
            break;
        }
        if exclusive && out.consecutive_buffer_errors >= 10 {
            out.hr = AUDCLNT_E_BUFFER_ERROR.0;
            out.reason = "consecutive_buffer_errors";
            break;
        }
        if timeouts >= 10 {
            out.hr = E_FAIL;
            out.reason = "timeout";
            break;
        }
    }
    let (hr, end) = qpc();
    out.end = end;
    if hr < 0 {
        if !out.apis.qpc.is_some_and(|previous| previous < 0) {
            out.apis.qpc = Some(hr);
        }
        out.hr = hr;
        out.reason = "qpc_error";
    }
    if out.hr < 0 {
        STOP.store(true, Ordering::Release);
    }
}
fn capture_cycle(
    c: &IAudioCaptureClient,
    p: &mut Producer<[f32; 2]>,
    w: &mut CaptureWake,
    out: &mut Outcome,
    exclusive: bool,
    layout: SampleLayout,
) {
    let apis = &mut out.apis;
    let frame_bytes = match layout {
        SampleLayout::F32 => 8,
        SampleLayout::I16 => 4,
    };
    for _ in 0..PACKET_LIMIT {
        if !exclusive {
            let mut next = 0;
            let hr = stream::raw_next(c, &mut next);
            apis.next = Some(hr);
            if hr < 0 {
                w.failure_hresult = hr;
                break;
            }
            if next == 0 {
                break;
            }
        }
        let (mut ptr, mut n, mut flags, mut pos, mut time) = (std::ptr::null_mut(), 0, 0, 0, 0);
        let hr = capture_get(c, &mut ptr, &mut n, &mut flags, &mut pos, &mut time);
        apis.capture_get = Some(hr);
        if exclusive {
            if hr == AUDCLNT_S_BUFFER_EMPTY.0 {
                out.buffer_empty_count = out.buffer_empty_count.saturating_add(1);
                out.consecutive_buffer_errors = 0;
                return;
            }
            if hr == AUDCLNT_E_BUFFER_ERROR.0 {
                out.buffer_error_count = out.buffer_error_count.saturating_add(1);
                out.consecutive_buffer_errors = out.consecutive_buffer_errors.saturating_add(1);
                return;
            }
            out.consecutive_buffer_errors = 0;
        }
        if hr < 0 {
            w.failure_hresult = hr;
            break;
        }
        w.frames_received = w.frames_received.saturating_add(n);
        if w.packets == 0 {
            w.first_device_position = pos;
            w.first_packet_qpc_hns = time;
        }
        w.packets = w.packets.saturating_add(1);
        let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
        w.discontinuity_packets = w.discontinuity_packets.saturating_add(u32::from(
            flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0,
        ));
        w.timestamp_error_packets = w.timestamp_error_packets.saturating_add(u32::from(
            flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0,
        ));
        let bytes = usize::try_from(n)
            .ok()
            .and_then(|n| n.checked_mul(frame_bytes))
            .filter(|n| *n <= isize::MAX as usize);
        if bytes.is_none() || (!silent && n > 0 && ptr.is_null()) {
            w.invalid_frames = w.invalid_frames.saturating_add(n);
            w.failure_hresult = E_FAIL;
        } else {
            if silent {
                w.silent_frames = w.silent_frames.saturating_add(n);
            }
            for i in 0..n {
                let value = if silent {
                    [0.0, 0.0]
                } else {
                    // SAFETY: validated 8/4-byte frame layout, checked n*frame_bytes <= isize::MAX;
                    // i<n, nonnull API buffer valid until the paired release below.
                    unsafe {
                        let ptr = ptr.add((i as usize).saturating_mul(frame_bytes));
                        match layout {
                            SampleLayout::F32 => ptr.cast::<[f32; 2]>().read_unaligned(),
                            SampleLayout::I16 => {
                                let [l, r] = ptr.cast::<[i16; 2]>().read_unaligned();
                                [i16_to_f32(l), i16_to_f32(r)]
                            }
                        }
                    }
                };
                if p.push(value) {
                    w.frames_pushed = w.frames_pushed.saturating_add(1);
                } else {
                    w.overflow_frames = w.overflow_frames.saturating_add(1);
                }
            }
        }
        // Every successful GetBuffer is released, even invalid pointer/size cases.
        let hr = stream::raw_capture_release(c, n);
        apis.capture_release = Some(hr);
        if hr < 0 {
            w.failure_hresult = hr;
        }
        if exclusive || w.failure_hresult < 0 {
            break;
        }
    }
}
#[allow(clippy::too_many_arguments)]
fn render_cycle(
    client: &IAudioClient,
    r: &IAudioRenderClient,
    c: &mut Consumer<[f32; 2]>,
    period: u32,
    buffer: u32,
    gain: f32,
    exclusive: bool,
    layout: SampleLayout,
    priming: &mut Priming,
    w: &mut RenderWake,
    apis: &mut RtHresults,
) {
    let frame_bytes = match layout {
        SampleLayout::F32 => 8,
        SampleLayout::I16 => 4,
    };
    let n = if exclusive {
        buffer
    } else {
        let hr = stream::raw_padding(client, &mut w.padding_before);
        apis.padding = Some(hr);
        if hr < 0 {
            w.failure_hresult = hr;
            return;
        }
        w.padding_valid = true;
        let Some(n) = render_frames(buffer, w.padding_before, period) else {
            w.failure_hresult = E_INVALID;
            return;
        };
        n
    };
    let before = c.len();
    w.ring_frames_before = u32::try_from(before).unwrap_or(u32::MAX);
    w.primed = priming.observe(before);
    if n == 0 {
        return;
    }
    let mut ptr = std::ptr::null_mut();
    let hr = render_get(r, n, &mut ptr);
    apis.render_get = Some(hr);
    if hr < 0 {
        w.failure_hresult = hr;
        return;
    }
    let bytes = usize::try_from(n)
        .ok()
        .and_then(|n| n.checked_mul(frame_bytes))
        .filter(|n| *n <= isize::MAX as usize);
    if ptr.is_null() || bytes.is_none() {
        // Successful acquisition must still be released; SILENT requires no dereference.
        let hr = render_release(r, n, AUDCLNT_BUFFERFLAGS_SILENT.0 as u32);
        apis.render_release = Some(hr);
        w.failure_hresult = if hr < 0 { hr } else { E_FAIL };
        return;
    }
    for i in 0..n {
        let frame = if w.primed {
            match c.pop() {
                Some([l, r]) => {
                    w.frames_from_ring = w.frames_from_ring.saturating_add(1);
                    let (l, lf) = protect(l, gain);
                    let (r, rf) = protect(r, gain);
                    for fault in [lf, rf] {
                        match fault {
                            SampleFault::NonFinite => {
                                w.nonfinite_samples = w.nonfinite_samples.saturating_add(1)
                            }
                            SampleFault::Clamped => {
                                w.clamped_samples = w.clamped_samples.saturating_add(1)
                            }
                            SampleFault::None => (),
                        }
                    }
                    [l, r]
                }
                None => {
                    w.underrun_frames = w.underrun_frames.saturating_add(1);
                    [0.0, 0.0]
                }
            }
        } else {
            w.priming_silence_frames = w.priming_silence_frames.saturating_add(1);
            [0.0, 0.0]
        };
        // SAFETY: checked n*frame_bytes <= isize::MAX, i<n, nonnull API-owned buffer until ReleaseBuffer;
        // unaligned write does not create a Rust reference; frame protected or zero.
        unsafe {
            let ptr = ptr.add((i as usize).saturating_mul(frame_bytes));
            match layout {
                SampleLayout::F32 => ptr.cast::<[f32; 2]>().write_unaligned(frame),
                SampleLayout::I16 => {
                    let [l, r] = frame;
                    ptr.cast::<[i16; 2]>()
                        .write_unaligned([f32_to_i16(l), f32_to_i16(r)]);
                }
            }
        }
    }
    w.frames_written = n;
    let hr = render_release(r, n, 0);
    apis.render_release = Some(hr);
    w.submitted = hr >= 0;
    if hr < 0 {
        w.failure_hresult = hr;
    }
}

struct Done;
impl Drop for Done {
    fn drop(&mut self) {
        WORKER_DONE.store(true, Ordering::Release);
    }
}
fn check_release(
    prefix: &str,
    endpoint: &str,
    mode: Mode,
    attempted: bool,
    fields: &mut Report,
) -> bool {
    if !mode.is_exclusive() {
        return true;
    }
    if !attempted {
        fields.put(
            format!("{prefix}.release_check_run.exclusive.release_check"),
            "skipped: not attempted",
        );
        return true;
    }
    let endpoint = endpoint.to_owned();
    let checked = std::thread::Builder::new()
        .name("loopback-release-check".into())
        .spawn(move || {
            let mut r = Report::default();
            r.successful = true;
            // SAFETY: COM initialized and torn down on this dedicated, joined thread.
            let hr = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.0;
            if r.hr("release_check.CoInitializeEx", hr).is_ok() {
                let _com = super::Com;
                stream::release_check(&endpoint, &mut r);
            } else {
                r.successful = false;
                r.put("exclusive.release_check", "failed: COM initialization");
            }
            r
        });
    match checked.map(|h| h.join()) {
        Ok(Ok(r)) => {
            let success = r.successful;
            for (k, v) in r.fields {
                fields.put(format!("{prefix}.release_check_run.{k}"), v);
            }
            success
        }
        _ => {
            fields.put(
                format!("{prefix}.release_check_run.exclusive.release_check"),
                "failed: check thread",
            );
            false
        }
    }
}
