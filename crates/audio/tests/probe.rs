use mdc_audio::{probe_table::*, *};

fn fixture() -> DeviceProbe {
    DeviceProbe {
        flow: DataFlow::Render,
        endpoint_id: Ok(Some("id".into())),
        friendly_name: Ok(Some("name,\"quoted\"\r\nnext".into())),
        state: Ok(Some("active".into())),
        default_console: Ok(Some(true)),
        default_communications: Ok(None),
        driver: Err("denied (HRESULT 0x80070005)".into()),
        mix_format: Ok(Some(Format {
            sample_rate: 48000,
            channels: 2,
            bits_per_sample: 32,
            float: true,
        })),
        mix_details: Ok(None),
        device_format: Ok(Some(Format {
            sample_rate: 44100,
            channels: 2,
            bits_per_sample: 16,
            float: false,
        })),
        device_format_details: Ok(Some("tag=0x0001; cbSize=0".into())),
        shared: Ok(Some(SharedPeriods {
            default_frames: 480,
            fundamental_frames: 48,
            min_frames: 96,
            max_frames: 960,
        })),
        exclusive: Ok(Some(ExclusivePeriods {
            default: Hns(100000),
            minimum: Hns(30000),
        })),
        raw_supported: Ok(None),
        effects: vec![EffectRegistration {
            source: "registry".into(),
            key: "EFX".into(),
            endpoint: true,
            clsids: Ok(Some("{clsid}".into())),
        }],
    }
}

fn csv_rows(csv: &str) -> Vec<Vec<String>> {
    let mut rows = Vec::new();
    let mut row = Vec::new();
    let mut field = String::new();
    let mut chars = csv.chars().peekable();
    let mut quoted = false;
    while let Some(ch) = chars.next() {
        match ch {
            '"' if quoted && chars.peek() == Some(&'"') => {
                field.push('"');
                chars.next();
            }
            '"' => quoted = !quoted,
            ',' if !quoted => row.push(std::mem::take(&mut field)),
            '\r' if !quoted && chars.peek() == Some(&'\n') => {
                chars.next();
                row.push(std::mem::take(&mut field));
                rows.push(std::mem::take(&mut row));
            }
            _ => field.push(ch),
        }
    }
    rows
}
fn metadata() -> Metadata {
    Metadata {
        executed_at: "2026-11-10T12:00:00Z".into(),
        os_version: "Windows".into(),
        tool_version: "test".into(),
        commit: "abc".into(),
        all_states: false,
    }
}

#[test]
fn csv_escaping_and_query_cells() {
    assert_eq!(csv_field("a,b\"c\r\nd"), "\"a,b\"\"c\r\nd\"");
    assert_eq!(csv_field("plain"), "plain");
    let output = csv(&metadata(), &[fixture()]);
    assert!(output.contains("\"name,\"\"quoted\"\"\r\nnext\""));
    assert!(output.contains("ERROR: denied (HRESULT 0x80070005)"));
    assert!(output.contains("None"));
    assert!(output.contains(
        ",480,48,96,960,10.000000,1.000000,2.000000,20.000000,100000,30000,10.000000,3.000000,"
    ));
    assert!(output.contains(",registry,EFX,true,{clsid}\r\n"));
}
#[test]
fn milliseconds_and_table_warning() {
    assert_eq!(frames_ms(48, 48000), Some(1.0));
    assert_eq!(frames_ms(48, 0), None);
    assert_eq!(hns_ms(Hns(10000)), 1.0);
    let mut d = fixture();
    let output = table(&[d.clone()]);
    assert_eq!(output.lines().count(), 4);
    assert!(output.contains("2.000000/1.000000"));
    assert!(output.contains("48000 Hz / 2 ch / 32 bit / float"));
    assert!(output.contains("44100 Hz / 2 ch / 16 bit / PCM/other"));
    assert!(output.contains("WARNING: endpoint FX registered"));
    d.flow = DataFlow::Capture;
    assert!(!endpoint_warning(&d).starts_with("WARNING"));
    d.effects[0].clsids = Err("HRESULT 0x80070005".into());
    assert!(endpoint_warning(&d).contains("unknown"));
}

#[test]
fn device_format_query_cells_and_csv_layout() {
    let output = csv(&metadata(), &[fixture()]);
    let rows = csv_rows(&output);
    let headers = &rows[0];
    let record = headers.iter().position(|v| v == "record").unwrap();
    let mix_details = headers.iter().position(|v| v == "mix_details").unwrap();
    let device_format = headers.iter().position(|v| v == "device_format").unwrap();
    assert_eq!(headers[mix_details + 1], "device_format");
    assert_eq!(headers[mix_details + 2], "device_format_details");
    assert!(rows.iter().all(|row| row.len() == headers.len()));

    let effect_source = headers.iter().position(|v| v == "effect_source").unwrap();
    let effect = rows.iter().find(|row| row[record] == "effect").unwrap();
    assert_eq!(effect[effect_source], "registry");

    let empty_rows = csv_rows(&csv(&metadata(), &[]));
    assert!(
        empty_rows
            .iter()
            .all(|row| row.len() == empty_rows[0].len())
    );

    let mut d = fixture();
    d.device_format = Ok(None);
    let output = csv(&metadata(), &[d.clone()]);
    let rows = csv_rows(&output);
    let device = rows.iter().find(|row| row[record] == "device").unwrap();
    assert_eq!(device[device_format], "None");
    let output = table(&[d.clone()]);
    let cells: Vec<_> = output.lines().nth(3).unwrap().split(" | ").collect();
    assert_eq!(cells[1], "None");

    d.device_format = Err("invalid blob (HRESULT 0x80070057)".into());
    let output = csv(&metadata(), &[d.clone()]);
    let rows = csv_rows(&output);
    let device = rows.iter().find(|row| row[record] == "device").unwrap();
    assert_eq!(
        device[device_format],
        "ERROR: invalid blob (HRESULT 0x80070057)"
    );
    let output = table(&[d.clone()]);
    let cells: Vec<_> = output.lines().nth(3).unwrap().split(" | ").collect();
    assert_eq!(cells[1], "ERROR: invalid blob (HRESULT 0x80070057)");

    d.mix_format = Ok(Some(Format {
        sample_rate: 48000,
        channels: 2,
        bits_per_sample: 16,
        float: false,
    }));
    d.device_format = Ok(Some(Format {
        sample_rate: 48000,
        channels: 2,
        bits_per_sample: 16,
        float: false,
    }));
    let output = table(&[d.clone()]);
    let cells: Vec<_> = output.lines().nth(3).unwrap().split(" | ").collect();
    assert!(cells[0].contains("PCM/other (see mix_details)"));
    assert!(cells[1].contains("PCM/other (see device_format_details)"));
    let output = csv(&metadata(), &[d]);
    let rows = csv_rows(&output);
    let mix_format = headers.iter().position(|v| v == "mix_format").unwrap();
    let device = rows.iter().find(|row| row[record] == "device").unwrap();
    assert_eq!(
        device[mix_format],
        "48000 Hz / 2 ch / 16 bit / PCM/other (see mix_details)"
    );
    assert_eq!(
        device[device_format],
        "48000 Hz / 2 ch / 16 bit / PCM/other (see device_format_details)"
    );
}
#[test]
fn empty_csv_preserves_metadata_and_shared_error() {
    let output = csv(&metadata(), &[]);
    assert_eq!(output.lines().count(), 2);
    assert!(output.contains("2026-11-10T12:00:00Z,Windows,test,abc,false,metadata"));
    let mut d = fixture();
    d.shared = Err("HRESULT 0x80004002".into());
    assert!(table(&[d.clone()]).contains("ERROR: HRESULT 0x80004002"));
    assert!(csv(&metadata(), &[d]).contains("ERROR: HRESULT 0x80004002"));
}

#[cfg(windows)]
#[test]
#[ignore = "read-only Windows device/service smoke; no stream Initialize or Start, so no device occupation"]
fn p1_read_only_device_probe_smoke() {
    for all_states in [false, true] {
        match mdc_audio::wasapi::devices(all_states) {
            Ok(_) => {} // Includes an empty device list on headless CI runners.
            Err(e) => {
                // Audio service not running / RPC unavailable / endpoint unavailable.
                assert!(
                    [
                        0x88890010u32,
                        0x80070426,
                        0x800706ba,
                        0x800706be,
                        0x80070490,
                        0x88890004
                    ]
                    .contains(&(e.code().0 as u32)),
                    "unexpected probe error: {e:?}"
                );
            }
        }
    }
}
