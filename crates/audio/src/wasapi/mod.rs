//! Read-only WASAPI probe. No stream initialization, start, or realtime threads.
use crate::probe_table::{DeviceProbe, EffectRegistration, Query};
use crate::{DataFlow, ExclusivePeriods, Format, Hns, SharedPeriods};
use windows::{
    Win32::{
        Devices::FunctionDiscovery::*,
        Foundation::{
            ERROR_FILE_NOT_FOUND, ERROR_NO_MORE_ITEMS, ERROR_PATH_NOT_FOUND, PROPERTYKEY,
        },
        Media::Audio::{Apo::*, *},
        Storage::EnhancedStorage::PKEY_Devices_AudioDevice_RawProcessingSupported,
        System::{
            Com::{StructuredStorage::*, *},
            Registry::*,
            SystemInformation::GetSystemTime,
            Variant::{VT_EMPTY, VT_VECTOR},
        },
        UI::Shell::PropertiesSystem::IPropertyStore,
    },
    core::{Interface, PCWSTR, PWSTR},
};

fn error(e: windows::core::Error) -> String {
    format!("{} (HRESULT 0x{:08X})", e.message(), e.code().0 as u32)
}
fn query<T>(r: windows::core::Result<T>) -> Query<T> {
    r.map(Some).map_err(error)
}
struct Com;
impl Drop for Com {
    fn drop(&mut self) {
        unsafe { CoUninitialize() };
    }
}
struct TaskMem<T>(*mut T);
impl<T> Drop for TaskMem<T> {
    fn drop(&mut self) {
        unsafe { CoTaskMemFree(Some(self.0.cast())) };
    }
}
fn owned_string(p: PWSTR) -> windows::core::Result<String> {
    if p.is_null() {
        return Err(windows::core::Error::from_hresult(windows::core::HRESULT(
            0x80004003u32 as i32,
        )));
    }
    let memory = TaskMem(p.0);
    // SAFETY: all callers supply a CoTaskMem-allocated, terminated API string.
    unsafe { Ok(PWSTR(memory.0).to_string()?) }
}
fn id(d: &IMMDevice) -> windows::core::Result<String> {
    owned_string(unsafe { d.GetId()? })
}

fn property(store: &IPropertyStore, key: &PROPERTYKEY) -> Query<String> {
    // PROPVARIANT's Drop clears its allocation; converted text has a separate owner.
    let value = unsafe { store.GetValue(key) }.map_err(error)?;
    if unsafe { value.Anonymous.Anonymous.vt } == VT_EMPTY {
        return Ok(None);
    }
    if value.vt().0 & VT_VECTOR.0 != 0 {
        let mut parts = Vec::new();
        for index in 0..unsafe { PropVariantGetElementCount(&value) } {
            let part = unsafe { PropVariantGetStringElem(&value, index) }
                .and_then(owned_string)
                .map_err(error)?;
            parts.push(part);
        }
        return Ok(Some(parts.join("; ")));
    }
    query(unsafe { PropVariantToStringAlloc(&value) }.and_then(owned_string))
}
fn raw(store: &IPropertyStore) -> Query<bool> {
    let value = unsafe { store.GetValue(&PKEY_Devices_AudioDevice_RawProcessingSupported) }
        .map_err(error)?;
    if unsafe { value.Anonymous.Anonymous.vt } == VT_EMPTY {
        return Ok(None);
    }
    query(unsafe { PropVariantToBoolean(&value) }.map(|v| v.as_bool()))
}

/// Explicit allowlist of registration keys (not UI CLSIDs or association GUIDs).
fn fx_keys() -> Vec<(&'static str, PROPERTYKEY, bool)> {
    vec![
        (
            "PKEY_FX_PreMixEffectClsid",
            PKEY_FX_PreMixEffectClsid,
            false,
        ),
        (
            "PKEY_FX_PostMixEffectClsid",
            PKEY_FX_PostMixEffectClsid,
            false,
        ),
        (
            "PKEY_FX_StreamEffectClsid",
            PKEY_FX_StreamEffectClsid,
            false,
        ),
        ("PKEY_FX_ModeEffectClsid", PKEY_FX_ModeEffectClsid, false),
        (
            "PKEY_FX_EndpointEffectClsid",
            PKEY_FX_EndpointEffectClsid,
            true,
        ),
        (
            "PKEY_FX_KeywordDetector_StreamEffectClsid",
            PKEY_FX_KeywordDetector_StreamEffectClsid,
            false,
        ),
        (
            "PKEY_FX_KeywordDetector_ModeEffectClsid",
            PKEY_FX_KeywordDetector_ModeEffectClsid,
            false,
        ),
        (
            "PKEY_FX_KeywordDetector_EndpointEffectClsid",
            PKEY_FX_KeywordDetector_EndpointEffectClsid,
            true,
        ),
        (
            "PKEY_FX_Offload_StreamEffectClsid",
            PKEY_FX_Offload_StreamEffectClsid,
            false,
        ),
        (
            "PKEY_FX_Offload_ModeEffectClsid",
            PKEY_FX_Offload_ModeEffectClsid,
            false,
        ),
        (
            "PKEY_CompositeFX_StreamEffectClsid",
            PKEY_CompositeFX_StreamEffectClsid,
            false,
        ),
        (
            "PKEY_CompositeFX_ModeEffectClsid",
            PKEY_CompositeFX_ModeEffectClsid,
            false,
        ),
        (
            "PKEY_CompositeFX_EndpointEffectClsid",
            PKEY_CompositeFX_EndpointEffectClsid,
            true,
        ),
        (
            "PKEY_CompositeFX_KeywordDetector_StreamEffectClsid",
            PKEY_CompositeFX_KeywordDetector_StreamEffectClsid,
            false,
        ),
        (
            "PKEY_CompositeFX_KeywordDetector_ModeEffectClsid",
            PKEY_CompositeFX_KeywordDetector_ModeEffectClsid,
            false,
        ),
        (
            "PKEY_CompositeFX_KeywordDetector_EndpointEffectClsid",
            PKEY_CompositeFX_KeywordDetector_EndpointEffectClsid,
            true,
        ),
        (
            "PKEY_CompositeFX_Offload_StreamEffectClsid",
            PKEY_CompositeFX_Offload_StreamEffectClsid,
            false,
        ),
        (
            "PKEY_CompositeFX_Offload_ModeEffectClsid",
            PKEY_CompositeFX_Offload_ModeEffectClsid,
            false,
        ),
    ]
}
struct Key(HKEY);
impl Drop for Key {
    fn drop(&mut self) {
        let _ = unsafe { RegCloseKey(self.0) };
    }
}
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}
fn open(parent: HKEY, path: &str) -> Result<Option<Key>, String> {
    let path = wide(path);
    let mut key = HKEY::default();
    let status = unsafe { RegOpenKeyExW(parent, PCWSTR(path.as_ptr()), None, KEY_READ, &mut key) };
    if status == ERROR_FILE_NOT_FOUND || status == ERROR_PATH_NOT_FOUND {
        return Ok(None);
    }
    status.ok().map_err(error)?;
    Ok(Some(Key(key)))
}
fn registry_value(key: HKEY, name: &str) -> Query<String> {
    let name = wide(name);
    let mut kind = REG_VALUE_TYPE::default();
    let mut size = 0;
    let status = unsafe {
        RegQueryValueExW(
            key,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            None,
            Some(&mut size),
        )
    };
    if status == ERROR_FILE_NOT_FOUND {
        return Ok(None);
    }
    status.ok().map_err(error)?;
    let initial_kind = kind;
    if kind != REG_SZ && kind != REG_MULTI_SZ && kind != REG_EXPAND_SZ {
        return Err(format!("unsupported registry type {}", kind.0));
    }
    if size > 1_048_576 || !size.is_multiple_of(2) {
        return Err("invalid registry string length".into());
    }
    let mut data = vec![0u16; size as usize / 2];
    unsafe {
        RegQueryValueExW(
            key,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            Some(data.as_mut_ptr().cast()),
            Some(&mut size),
        )
    }
    .ok()
    .map_err(error)?;
    if kind != initial_kind || !size.is_multiple_of(2) || size as usize / 2 > data.len() {
        return Err("registry value changed type/size during read".into());
    }
    data.truncate(size as usize / 2);
    let parts = data
        .split(|v| *v == 0)
        .filter(|v| !v.is_empty())
        .map(String::from_utf16)
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    let value = parts.join("; ");
    Ok((!value.is_empty()).then_some(value))
}
// A null CLSID is an explicit absence of an APO, not a registered effect.
// Keep malformed values as errors so they cannot become a false clean result.
fn clsids(value: Query<String>) -> Query<String> {
    let Some(value) = value? else {
        return Ok(None);
    };
    let mut registered = Vec::new();
    for part in value.split(';').map(str::trim).filter(|p| !p.is_empty()) {
        let inner = part
            .strip_prefix('{')
            .and_then(|p| p.strip_suffix('}'))
            .unwrap_or(part);
        let guid = windows::core::GUID::try_from(inner).map_err(error)?;
        if guid != windows::core::GUID::zeroed() {
            registered.push(format!("{{{guid:?}}}"));
        }
    }
    Ok((!registered.is_empty()).then(|| registered.join("; ")))
}
fn registry_effects(path: &str, key: HKEY, out: &mut Vec<EffectRegistration>, depth: usize) {
    for (name, property, endpoint) in fx_keys() {
        let value_name = format!("{{{:?}}},{}", property.fmtid, property.pid);
        out.push(EffectRegistration {
            source: path.into(),
            key: name.into(),
            endpoint,
            clsids: clsids(registry_value(key, &value_name)),
        });
    }
    // Driver-specific FX stores can contain numbered/mode subkeys. Inspect them
    // read-only, with finite depth; incomplete inspection remains explicit.
    let mut index = 0;
    loop {
        let mut name = [0u16; 256];
        let mut len = name.len() as u32;
        let status = unsafe {
            RegEnumKeyExW(
                key,
                index,
                Some(PWSTR(name.as_mut_ptr())),
                &mut len,
                None,
                None,
                None,
                None,
            )
        };
        if status == ERROR_NO_MORE_ITEMS {
            break;
        }
        if let Err(e) = status.ok() {
            out.push(EffectRegistration {
                source: path.into(),
                key: "subkey enumeration".into(),
                endpoint: true,
                clsids: Err(error(e)),
            });
            break;
        }
        let name = String::from_utf16_lossy(name.get(..len as usize).unwrap_or_default());
        let child_path = format!("{path}\\{name}");
        match open(key, &name) {
            Ok(Some(child)) if depth < 8 => registry_effects(&child_path, child.0, out, depth + 1),
            Ok(None) => out.push(EffectRegistration {
                source: child_path,
                key: "subkey".into(),
                endpoint: true,
                clsids: Err("subkey disappeared".into()),
            }),
            Ok(Some(_)) => out.push(EffectRegistration {
                source: child_path,
                key: "subkey".into(),
                endpoint: true,
                clsids: Err("FX inspection depth limit".into()),
            }),
            Err(e) => out.push(EffectRegistration {
                source: child_path,
                key: "subkey".into(),
                endpoint: true,
                clsids: Err(e),
            }),
        }
        index += 1;
    }
}
fn effects(
    store: &Result<IPropertyStore, String>,
    endpoint_id: &Query<String>,
    flow: DataFlow,
) -> Vec<EffectRegistration> {
    let mut out = Vec::new();
    for (name, key, endpoint) in fx_keys() {
        out.push(EffectRegistration {
            source: "endpoint property store".into(),
            key: name.into(),
            endpoint,
            clsids: clsids(
                store
                    .as_ref()
                    .map_err(Clone::clone)
                    .and_then(|s| property(s, &key)),
            ),
        });
    }
    let result = (|| {
        let id = endpoint_id
            .as_ref()
            .map_err(Clone::clone)?
            .as_ref()
            .ok_or("endpoint ID missing")?;
        let start = id.rfind('{').ok_or("endpoint ID lacks GUID")?;
        let guid = id.get(start..).ok_or("invalid endpoint ID")?;
        if guid.len() != 38
            || !guid.ends_with('}')
            || !guid
                .chars()
                .all(|c| c.is_ascii_hexdigit() || "{}-".contains(c))
        {
            return Err("invalid endpoint GUID".into());
        }
        let direction = if flow == DataFlow::Render {
            "Render"
        } else {
            "Capture"
        };
        let path = format!(
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\{direction}\\{guid}\\FxProperties"
        );
        match open(HKEY_LOCAL_MACHINE, &path)? {
            Some(key) => registry_effects(&path, key.0, &mut out, 0),
            None => out.push(EffectRegistration {
                source: path,
                key: "FxProperties store".into(),
                endpoint: true,
                clsids: Ok(None),
            }),
        }
        Ok::<_, String>(())
    })();
    if let Err(e) = result {
        out.push(EffectRegistration {
            source: "FxProperties registry".into(),
            key: "store access".into(),
            endpoint: true,
            clsids: Err(e),
        });
    }
    out
}

fn default_match(default: &Query<String>, endpoint: &Query<String>) -> Query<bool> {
    match (default, endpoint) {
        (Err(e), _) | (_, Err(e)) => Err(e.clone()),
        (Ok(Some(a)), Ok(Some(b))) => Ok(Some(a == b)),
        _ => Ok(None),
    }
}
fn inspect(
    d: windows::core::Result<IMMDevice>,
    flow: DataFlow,
    console: &Query<String>,
    communications: &Query<String>,
) -> DeviceProbe {
    let d = d.map_err(error);
    let endpoint_id = d.as_ref().map_err(Clone::clone).and_then(|d| query(id(d)));
    let store = d
        .as_ref()
        .map_err(Clone::clone)
        .and_then(|d| unsafe { d.OpenPropertyStore(STGM_READ) }.map_err(error));
    let prop = |k: &PROPERTYKEY| {
        store
            .as_ref()
            .map_err(Clone::clone)
            .and_then(|s| property(s, k))
    };
    let mut driver_parts = Vec::new();
    // Retain each individual missing/error result, rather than inventing a driver.
    for (name, key) in [
        ("DriverDesc", PKEY_Device_DriverDesc),
        ("DriverProvider", PKEY_Device_DriverProvider),
        ("DriverVersion", PKEY_Device_DriverVersion),
        ("Driver", PKEY_Device_Driver),
        ("InterfaceName", PKEY_DeviceInterface_FriendlyName),
        ("Manufacturer", PKEY_Device_Manufacturer),
        ("DeviceDesc", PKEY_Device_DeviceDesc),
    ] {
        driver_parts.push(format!(
            "{name}={}",
            crate::probe_table::cell(&prop(&key), Clone::clone)
        ));
    }
    let client = d
        .as_ref()
        .map_err(Clone::clone)
        .and_then(|d| unsafe { d.Activate::<IAudioClient>(CLSCTX_ALL, None) }.map_err(error));
    let shared;
    let mix_format;
    let mix_details;
    match &client {
        Err(e) => {
            shared = Err(e.clone());
            mix_format = Err(e.clone());
            mix_details = Err(e.clone());
        }
        Ok(c) => match unsafe { c.GetMixFormat() } {
            Err(e) => {
                let e = error(e);
                shared = Err(e.clone());
                mix_format = Err(e.clone());
                mix_details = Err(e);
            }
            Ok(ptr) if ptr.is_null() => {
                let e = "GetMixFormat returned null".to_string();
                shared = Err(e.clone());
                mix_format = Err(e.clone());
                mix_details = Err(e);
            }
            Ok(ptr) => {
                let memory = TaskMem(ptr);
                // SAFETY: GetMixFormat returns a WAVEFORMATEX plus cbSize bytes.
                let f = unsafe { ptr.read_unaligned() };
                let mut is_float = f.wFormatTag == 3;
                let tag = f.wFormatTag;
                let extra = f.cbSize;
                let align = f.nBlockAlign;
                let byte_rate = f.nAvgBytesPerSec;
                let mut detail = format!(
                    "tag={tag:#06x}; cbSize={extra}; block_align={align}; bytes_per_sec={byte_rate}"
                );
                if tag == 0xfffe && extra >= 22 {
                    let ext = unsafe { ptr.cast::<WAVEFORMATEXTENSIBLE>().read_unaligned() };
                    let subtype = ext.SubFormat;
                    let mask = ext.dwChannelMask;
                    let valid = unsafe { ext.Samples.wValidBitsPerSample };
                    is_float = subtype
                        == windows::core::GUID::from_u128(0x00000003_0000_0010_8000_00aa00389b71);
                    detail.push_str(&format!(
                        "; subtype={subtype:?}; valid_bits={valid}; channel_mask={mask:#x}"
                    ));
                }
                mix_format = Ok(Some(Format {
                    sample_rate: f.nSamplesPerSec,
                    channels: f.nChannels,
                    bits_per_sample: f.wBitsPerSample,
                    float: is_float,
                }));
                mix_details = Ok(Some(detail));
                shared = c.cast::<IAudioClient3>().map_err(error).and_then(|c3| {
                    let mut p = SharedPeriods {
                        default_frames: 0,
                        fundamental_frames: 0,
                        min_frames: 0,
                        max_frames: 0,
                    };
                    query(
                        unsafe {
                            c3.GetSharedModeEnginePeriod(
                                memory.0,
                                &mut p.default_frames,
                                &mut p.fundamental_frames,
                                &mut p.min_frames,
                                &mut p.max_frames,
                            )
                        }
                        .map(|()| p),
                    )
                });
            }
        },
    }
    let exclusive = client.as_ref().map_err(Clone::clone).and_then(|c| {
        let mut default = 0;
        let mut min = 0;
        unsafe { c.GetDevicePeriod(Some(&mut default), Some(&mut min)) }.map_err(error)?;
        let default = u64::try_from(default).map_err(|_| "negative default device period")?;
        let min = u64::try_from(min).map_err(|_| "negative minimum device period")?;
        Ok(Some(ExclusivePeriods {
            default: Hns(default),
            minimum: Hns(min),
        }))
    });
    DeviceProbe {
        flow,
        friendly_name: prop(&PKEY_Device_FriendlyName),
        state: d.as_ref().map_err(Clone::clone).and_then(|d| {
            query(unsafe { d.GetState() }.map(|s| match s {
                DEVICE_STATE_ACTIVE => "active".into(),
                DEVICE_STATE_DISABLED => "disabled".into(),
                DEVICE_STATE_NOTPRESENT => "not-present".into(),
                DEVICE_STATE_UNPLUGGED => "unplugged".into(),
                _ => format!("unknown({:#x})", s.0),
            }))
        }),
        default_console: default_match(console, &endpoint_id),
        default_communications: default_match(communications, &endpoint_id),
        driver: Ok(Some(driver_parts.join("; "))),
        mix_format,
        mix_details,
        shared,
        exclusive,
        raw_supported: store.as_ref().map_err(Clone::clone).and_then(raw),
        effects: effects(&store, &endpoint_id, flow),
        endpoint_id,
    }
}

/// Synchronous control-thread query. COM interfaces die before apartment teardown.
/// Never calls Initialize/InitializeSharedAudioStream/Start or opens a stream.
pub fn devices(all: bool) -> windows::core::Result<Vec<DeviceProbe>> {
    unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.ok()?;
    let _com = Com;
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)? };
    let mut result = Vec::new();
    for (flow, api_flow) in [(DataFlow::Render, eRender), (DataFlow::Capture, eCapture)] {
        let default = |role| {
            query(
                unsafe { enumerator.GetDefaultAudioEndpoint(api_flow, role) }.and_then(|d| id(&d)),
            )
        };
        let console = default(eConsole);
        let communications = default(eCommunications);
        let collection = unsafe {
            enumerator.EnumAudioEndpoints(
                api_flow,
                if all {
                    DEVICE_STATE(DEVICE_STATEMASK_ALL)
                } else {
                    DEVICE_STATE_ACTIVE
                },
            )?
        };
        for index in 0..unsafe { collection.GetCount()? } {
            result.push(inspect(
                unsafe { collection.Item(index) },
                flow,
                &console,
                &communications,
            ));
        }
    }
    Ok(result)
}

/// Timestamp is explicitly UTC in both metadata and filename.
pub fn timestamp() -> (String, String) {
    let t = unsafe { GetSystemTime() };
    (
        format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}Z",
            t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds
        ),
        format!(
            "{:04}{:02}{:02}-{:02}{:02}{:02}",
            t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond
        ),
    )
}

/// Read the current OS build from a read-only registry key; avoids manifest-
/// dependent GetVersion reporting. Each missing/error field remains explicit.
pub fn os_version() -> String {
    match open(
        HKEY_LOCAL_MACHINE,
        "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
    ) {
        Ok(Some(k)) => ["ProductName", "DisplayVersion", "CurrentBuildNumber", "UBR"]
            .iter()
            .map(|name| {
                format!(
                    "{name}={}",
                    crate::probe_table::cell(
                        &if *name == "UBR" {
                            registry_dword(k.0, name)
                        } else {
                            registry_value(k.0, name)
                        },
                        Clone::clone
                    )
                )
            })
            .collect::<Vec<_>>()
            .join("; "),
        Ok(None) => "Windows (version key missing)".into(),
        Err(e) => format!("Windows (ERROR: {e})"),
    }
}

// Used for OS metadata only; FX registration values must remain strings.
fn registry_dword(key: HKEY, name: &str) -> Query<String> {
    let name = wide(name);
    let mut kind = REG_VALUE_TYPE::default();
    let mut data = 0u32;
    let mut size = 4;
    let status = unsafe {
        RegQueryValueExW(
            key,
            PCWSTR(name.as_ptr()),
            None,
            Some(&mut kind),
            Some((&mut data as *mut u32).cast()),
            Some(&mut size),
        )
    };
    if status == ERROR_FILE_NOT_FOUND {
        return Ok(None);
    }
    status.ok().map_err(error)?;
    if kind != REG_DWORD || size != 4 {
        return Err("expected REG_DWORD".into());
    }
    Ok(Some(data.to_string()))
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn effect_clsids_reject_malformed_and_ignore_null() {
        assert_eq!(clsids(Ok(None)), Ok(None));
        assert_eq!(
            clsids(Ok(Some("{00000000-0000-0000-0000-000000000000}".into()))),
            Ok(None)
        );
        let real = "{C9453E73-8C5C-4463-9984-AF8BAB2F5447}";
        assert_eq!(
            clsids(Ok(Some(format!(
                "{real}; {{00000000-0000-0000-0000-000000000000}}; {real}"
            )))),
            Ok(Some(format!("{real}; {real}")))
        );
        assert!(clsids(Ok(Some("invalid GUID".into()))).is_err());
        assert!(clsids(Err("HRESULT 0x80070005".into())).is_err());
    }
}
