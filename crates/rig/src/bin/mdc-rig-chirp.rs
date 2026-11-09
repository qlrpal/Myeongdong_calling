use mdc_rig::{ChirpConfig, SAMPLE_RATE_HZ, write_wav};
use std::{
    env,
    fs::File,
    io::{BufWriter, Write},
    process::ExitCode,
};
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args_os().skip(1);
    let path = args
        .next()
        .ok_or("usage: mdc-rig-chirp <out.wav> [marker_count] [--stereo]")?;
    let mut cfg = ChirpConfig::default();
    let mut count = false;
    let mut stereo = false;
    for arg in args {
        if arg == "--stereo" && !stereo {
            stereo = true;
            cfg.channels = 2;
        } else if !count && arg != "--stereo" {
            cfg.marker_count = arg.to_str().ok_or("invalid marker_count")?.parse()?;
            count = true;
        } else {
            return Err("usage: mdc-rig-chirp <out.wav> [marker_count] [--stereo]".into());
        }
    }
    cfg.validate()?;
    let mut writer = BufWriter::new(File::create(path)?);
    write_wav(&cfg, &mut writer)?;
    writer.flush()?;
    println!(
        "markers={} duration={:.3}s band={:.0}-{:.0}Hz peak={:.1}dBFS",
        cfg.marker_count,
        cfg.total_len()? as f64 / f64::from(SAMPLE_RATE_HZ),
        cfg.f_low_hz,
        cfg.f_high_hz,
        cfg.peak_dbfs
    );
    Ok(())
}
fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("{e}");
            ExitCode::FAILURE
        }
    }
}
