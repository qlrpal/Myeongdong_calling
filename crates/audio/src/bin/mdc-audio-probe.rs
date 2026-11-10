#[cfg(windows)]
fn run() -> Result<(), Box<dyn std::error::Error>> {
    use mdc_audio::{
        probe_table::{self, Metadata},
        wasapi,
    };
    use std::{fs::OpenOptions, io::Write, path::PathBuf, process::Command};
    let mut args = std::env::args_os().skip(1);
    let command = args.next();
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
    let commit = Command::new("git")
        .args(["rev-parse", "HEAD"])
        .output()
        .ok()
        .filter(|o| o.status.success())
        .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string())
        .unwrap_or_else(|| "None".into());
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
                    .ok_or("mode must be shared-default|shared-min|exclusive-min")?,
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
