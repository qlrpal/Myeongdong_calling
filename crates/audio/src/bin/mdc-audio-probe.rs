#[cfg(windows)]
fn run() -> Result<(), Box<dyn std::error::Error>> {
    use mdc_audio::{
        probe_table::{self, Metadata},
        wasapi,
    };
    use std::{fs::OpenOptions, io::Write, path::PathBuf};
    let mut args = std::env::args_os().skip(1);
    let command = args.next();
    if command.as_deref() == Some(std::ffi::OsStr::new("loopback")) {
        return loopback(args);
    }
    if command.as_deref() == Some(std::ffi::OsStr::new("stream")) {
        return stream(args);
    }
    if command.as_deref() != Some(std::ffi::OsStr::new("devices")) {
        return Err("usage: mdc-audio-probe devices [--all] [--out DIR] (timestamps: UTC)".into());
    }
    let mut all = false;
    let mut out = None;
    while let Some(arg) = args.next() {
        if arg == "--all" && !all {
            all = true;
        } else if arg == "--out" && out.is_none() {
            out = Some(PathBuf::from(args.next().ok_or("--out requires DIR")?));
        } else {
            return Err(format!("unknown or repeated argument: {}", arg.to_string_lossy()).into());
        }
    }
    let (executed_at, filename_time) = wasapi::timestamp();
    let devices = wasapi::devices(all)?;
    let commit = mdc_audio::provenance::commit_label();
    let metadata = Metadata {
        executed_at,
        os_version: wasapi::os_version(),
        tool_version: env!("CARGO_PKG_VERSION").into(),
        commit,
        all_states: all,
    };
    let directory = out.unwrap_or_else(|| PathBuf::from(".local/p1"));
    std::fs::create_dir_all(&directory)?;
    let path = directory.join(format!("devices-{filename_time}.csv"));
    // Atomic create_new prevents overwriting an earlier run, including races.
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)?;
    file.write_all(probe_table::csv(&metadata, &devices).as_bytes())?;
    file.flush()?;
    let mut stdout = std::io::stdout().lock();
    writeln!(
        stdout,
        "{} (UTC); {} devices",
        metadata.executed_at,
        devices.len()
    )?;
    write!(stdout, "{}", probe_table::table(&devices))?;
    writeln!(stdout, "CSV: {} (filename timestamp UTC)", path.display())?;
    Ok(())
}

#[cfg(windows)]
fn stream(
    mut args: impl Iterator<Item = std::ffi::OsString>,
) -> Result<(), Box<dyn std::error::Error>> {
    use mdc_audio::wasapi::stream::{self, Mode, Options};
    use std::{io::Write, path::PathBuf};
    let (mut endpoint, mut mode, mut seconds, mut out) = (None, None, None, None);
    while let Some(arg) = args.next() {
        if arg == "--endpoint" && endpoint.is_none() {
            let value = args
                .next()
                .ok_or("--endpoint requires an exact endpoint ID")?;
            let value = value
                .into_string()
                .map_err(|_| "endpoint ID must be Unicode")?;
            if value.is_empty() || value.contains('\0') {
                return Err("invalid endpoint ID".into());
            }
            endpoint = Some(value);
        } else if arg == "--mode" && mode.is_none() {
            let value = args.next().ok_or("--mode requires a value")?;
            mode = Some(
                Mode::parse(value.to_str().ok_or("invalid mode")?)
                    .ok_or("mode must be shared-default|shared-min|shared-default-raw|shared-min-raw|exclusive-min")?,
            );
        } else if arg == "--seconds" && seconds.is_none() {
            let value = args.next().ok_or("--seconds requires N")?;
            let n = value.to_str().ok_or("invalid seconds")?.parse::<u32>()?;
            if !(1..=120).contains(&n) {
                return Err("seconds must be 1..=120".into());
            }
            seconds = Some(n);
        } else if arg == "--out" && out.is_none() {
            out = Some(PathBuf::from(args.next().ok_or("--out requires DIR")?));
        } else {
            return Err(format!(
                "unknown or repeated stream argument: {}",
                arg.to_string_lossy()
            )
            .into());
        }
    }
    let result = stream::run(Options {
        endpoint: endpoint.ok_or("--endpoint is required")?,
        mode: mode.ok_or("--mode is required")?,
        seconds: seconds.ok_or("--seconds is required")?,
        out: out.unwrap_or_else(|| PathBuf::from(".local/p1")),
    })?;
    let mut stdout = std::io::stdout().lock();
    write!(stdout, "{}", result.summary)?;
    writeln!(
        stdout,
        "CSV: {}\nMetadata: {}",
        result.records.display(),
        result.metadata.display()
    )?;
    if result.successful {
        Ok(())
    } else {
        Err(
            "stream did not complete normally; see metadata for raw HRESULT and termination reason"
                .into(),
        )
    }
}

#[cfg(windows)]
fn loopback(
    mut args: impl Iterator<Item = std::ffi::OsString>,
) -> Result<(), Box<dyn std::error::Error>> {
    use mdc_audio::wasapi::{
        loopback::{self, Options},
        stream::Mode,
    };
    const HELP: &str = "usage: mdc-audio-probe loopback --capture ID --render ID --capture-mode M --render-mode M --seconds N [--gain-db G] [--prefill-frames P] [--out DIR]\nM: shared-default|shared-min|shared-default-raw|shared-min-raw|exclusive-min; N: 1..=120; G: -60..=0 (default -20); P: 0..=16384 (default capture actual period); out: .local/p1\nexclusive-min occupies the device; other apps using it must be closed\nloopback means microphone-to-headphone direct routing, not WASAPI output capture (AUDCLNT_STREAMFLAGS_LOOPBACK).";
    let mut values = std::collections::BTreeMap::new();
    while let Some(arg) = args.next() {
        if arg == "--help" || arg == "-h" {
            println!("{HELP}");
            return Ok(());
        }
        let key = arg.into_string().map_err(|_| "invalid argument")?;
        if ![
            "--capture",
            "--render",
            "--capture-mode",
            "--render-mode",
            "--seconds",
            "--gain-db",
            "--prefill-frames",
            "--out",
        ]
        .contains(&key.as_str())
            || values.contains_key(&key)
        {
            return Err(format!("unknown or repeated loopback argument: {key}\n{HELP}").into());
        }
        values.insert(key, args.next().ok_or(HELP)?);
    }
    let text = |key: &str| -> Result<&str, Box<dyn std::error::Error>> {
        values
            .get(key)
            .ok_or_else(|| format!("{key} is required\n{HELP}").into())
            .and_then(|v| v.to_str().ok_or_else(|| "argument must be Unicode".into()))
    };
    let mode = |key| -> Result<Mode, Box<dyn std::error::Error>> {
        let m = Mode::parse(text(key)?).ok_or("invalid mode")?;
        Ok(m)
    };
    let result = loopback::run(Options {
        capture: text("--capture")?.into(),
        render: text("--render")?.into(),
        capture_mode: mode("--capture-mode")?,
        render_mode: mode("--render-mode")?,
        seconds: text("--seconds")?.parse()?,
        gain_db: if values.contains_key("--gain-db") {
            text("--gain-db")?.parse()?
        } else {
            -20.0
        },
        prefill_frames: if values.contains_key("--prefill-frames") {
            Some(text("--prefill-frames")?.parse()?)
        } else {
            None
        },
        out: values
            .get("--out")
            .map(std::path::PathBuf::from)
            .unwrap_or_else(|| ".local/p1".into()),
    })?;
    use std::io::Write;
    let mut stdout = std::io::stdout().lock();
    write!(stdout, "{}", result.summary)?;
    writeln!(
        stdout,
        "CSV: {}\nMetadata: {}",
        result.records.display(),
        result.metadata.display()
    )?;
    if result.successful {
        Ok(())
    } else {
        Err("loopback did not complete normally; see metadata".into())
    }
}

#[cfg(not(windows))]
fn run() -> Result<(), Box<dyn std::error::Error>> {
    Err("Windows only".into())
}

fn main() -> std::process::ExitCode {
    match run() {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            use std::io::Write;
            let _ = writeln!(std::io::stderr().lock(), "{e}");
            std::process::ExitCode::FAILURE
        }
    }
}
