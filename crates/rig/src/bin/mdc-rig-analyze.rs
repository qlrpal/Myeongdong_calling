use mdc_rig::analyze::{AnalysisConfig, analyze};
use mdc_rig::calib::{Calibration, calibrate_offset, calibrate_xtalk};
use mdc_rig::report::{
    aggregate, read_summary, summarize, write_aggregate, write_markers, write_summary,
};
use mdc_rig::{RigError, read_stereo_wav};
use std::{
    collections::BTreeMap,
    env,
    fs::{self, File},
    path::{Path, PathBuf},
};
const USAGE: &str = "usage: mdc-rig-analyze analyze <rec.wav> --calibration FILE [--markers N] [--label L] [--out DIR]\n       calibrate-offset <rec.wav> [--markers N] --out FILE\n       calibrate-xtalk <rec.wav> --calibration FILE [--markers N]\n       set-guard --calibration FILE --z Z --x X --basis TEXT\n       aggregate <summary.csv>... [--out DIR]";
fn require_new_outputs(paths: &[PathBuf]) -> Result<(), Box<dyn std::error::Error>> {
    for path in paths {
        match fs::symlink_metadata(path) {
            Ok(_) => {
                return Err(format!(
                    "output already exists: {}; use a new output path",
                    path.display()
                )
                .into());
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        }
    }
    Ok(())
}
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args().skip(1);
    let command = args.next().ok_or(USAGE)?;
    let mut files = Vec::new();
    let mut options = BTreeMap::new();
    while let Some(arg) = args.next() {
        if arg.starts_with("--") {
            if ![
                "--calibration",
                "--markers",
                "--label",
                "--out",
                "--z",
                "--x",
                "--basis",
            ]
            .contains(&arg.as_str())
            {
                return Err(USAGE.into());
            }
            let value = args.next().filter(|v| !v.starts_with("--")).ok_or(USAGE)?;
            if options.insert(arg, value).is_some() {
                return Err("duplicate option".into());
            }
        } else {
            files.push(arg);
        }
    }
    let allowed: &[&str] = match command.as_str() {
        "analyze" => &["--calibration", "--markers", "--label", "--out"],
        "calibrate-offset" => &["--markers", "--out"],
        "calibrate-xtalk" => &["--calibration", "--markers"],
        "aggregate" => &["--out"],
        "set-guard" => &["--calibration", "--z", "--x", "--basis"],
        _ => return Err(USAGE.into()),
    };
    if options.keys().any(|k| !allowed.contains(&k.as_str())) {
        return Err(USAGE.into());
    }
    if command == "set-guard" {
        if !files.is_empty() {
            return Err(USAGE.into());
        }
        let path = options.get("--calibration").ok_or(USAGE)?;
        let mut cal = Calibration::read(path)?;
        cal.z = options.get("--z").ok_or(USAGE)?.parse()?;
        cal.x = options.get("--x").ok_or(USAGE)?.parse()?;
        cal.z_basis = options.get("--basis").ok_or(USAGE)?.clone();
        if cal.z_basis.trim().is_empty() {
            return Err("basis must contain non-whitespace text".into());
        }
        cal.validate()?;
        cal.write(path)?;
        let (lo, hi) = cal.window();
        println!(
            "z={} x={} basis={} window_samples=[{lo},{hi}] A_x=[{lo},{}] search_window=({},{}]",
            cal.z,
            cal.x,
            cal.z_basis,
            hi + cal.z,
            lo - cal.x,
            hi + cal.z + cal.x
        );
        return Ok(());
    }
    let out = PathBuf::from(
        options
            .get("--out")
            .map(String::as_str)
            .unwrap_or(".local/rig/"),
    );
    if command == "aggregate" {
        if files.is_empty() {
            return Err(USAGE.into());
        }
        let summaries = files
            .iter()
            .map(|f| read_summary(File::open(f)?))
            .collect::<Result<Vec<_>, RigError>>()?;
        let groups = aggregate(&summaries)?;
        require_new_outputs(&[
            out.join("aggregate.run-metadata.txt"),
            out.join("aggregate.csv"),
        ])?;
        fs::create_dir_all(&out)?;
        fs::write(
            out.join("aggregate.run-metadata.txt"),
            format!(
                "analyzer_commit={}; marker_placement_fnv1a64=0x{:016x}\n",
                mdc_rig::analyzer_commit_hash(),
                mdc_rig::marker_placement_fnv1a64()
            ),
        )?;
        write_aggregate(File::create(out.join("aggregate.csv"))?, &groups)?;
        return Ok(());
    }
    if files.len() != 1 {
        return Err(USAGE.into());
    }
    let input = files.first().ok_or(USAGE)?;
    let mut config = AnalysisConfig::default();
    if let Some(n) = options.get("--markers") {
        config.chirp.marker_count = n.parse()?;
    }
    config.validate()?;
    // Require calibration before reading/analyzing any measurement recording.
    let calibration = if ["analyze", "calibrate-xtalk"].contains(&command.as_str()) {
        let path = options
            .get("--calibration")
            .ok_or("판정 불가: --calibration FILE required")?;
        Some(Calibration::read(path).map_err(|e| format!("판정 불가: calibration: {e}"))?)
    } else {
        None
    };
    let rec = read_stereo_wav(File::open(input)?)?;
    match command.as_str() {
        "analyze" => {
            let a = analyze(&config, calibration.as_ref(), &rec)?;
            let label = options.get("--label").map(String::as_str).unwrap_or("");
            let summary = summarize(&a, &rec, label, input);
            let stem = Path::new(input)
                .file_stem()
                .and_then(|s| s.to_str())
                .ok_or("invalid input filename")?;
            require_new_outputs(&[
                out.join(format!("{stem}.run-metadata.txt")),
                out.join(format!("{stem}.markers.csv")),
                out.join(format!("{stem}.summary.csv")),
            ])?;
            fs::create_dir_all(&out)?;
            fs::write(
                out.join(format!("{stem}.run-metadata.txt")),
                format!(
                    "analyzer_commit={}; marker_placement_fnv1a64=0x{:016x}; analysis_mode={:?}; uncertainty_note=u and u_L are residual-based estimates, not guaranteed bounds.\n",
                    mdc_rig::analyzer_commit_hash(),
                    mdc_rig::marker_placement_fnv1a64(),
                    config.mode
                ),
            )?;
            write_markers(File::create(out.join(format!("{stem}.markers.csv")))?, &a)?;
            write_summary(
                File::create(out.join(format!("{stem}.summary.csv")))?,
                &summary,
            )?;
            println!(
                "analyzed {} markers; rejected={:?}; warnings={}; mode={:?}",
                a.markers.len(),
                a.reject_reasons,
                a.warnings.join(";"),
                config.mode
            );
        }
        "calibrate-offset" => {
            if !options.contains_key("--out") {
                return Err(USAGE.into());
            }
            require_new_outputs(std::slice::from_ref(&out))?;
            let (cal, stats) = calibrate_offset(&config, &rec, input)?;
            cal.write(&out)?;
            println!(
                "offset_samples={:.12} min={:.12} max={:.12} pairs={}",
                stats.median, stats.min, stats.max, stats.count
            );
        }
        "calibrate-xtalk" => {
            let mut cal = calibration.ok_or("calibration required")?;
            let n = calibrate_xtalk(&config, &rec, &mut cal, input)?;
            cal.write(options.get("--calibration").ok_or(USAGE)?)?;
            let (lo, hi) = cal.window();
            println!("xtalk_pairs={n} window_samples=[{lo:.12},{hi:.12}]");
        }
        _ => return Err(USAGE.into()),
    }
    Ok(())
}
fn main() {
    if let Err(e) = run() {
        eprintln!("{e}");
        std::process::exit(1);
    }
}
