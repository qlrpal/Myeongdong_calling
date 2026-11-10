#[cfg(windows)]
fn run() -> Result<(), Box<dyn std::error::Error>> {
    use mdc_audio::{
        probe_table::{self, Metadata},
        wasapi,
    };
    use std::{fs::OpenOptions, io::Write, path::PathBuf, process::Command};
    let mut args = std::env::args_os().skip(1);
    if args.next().as_deref() != Some(std::ffi::OsStr::new("devices")) {
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
