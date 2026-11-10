mod common;
use common::*;
use mdc_rig::calib::Calibration;
use std::{
    fs,
    path::Path,
    process::{Command, Output},
};

fn call(args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_mdc-rig-analyze"))
        .args(args)
        .output()
        .unwrap()
}
fn text(p: &Path) -> &str {
    p.to_str().unwrap()
}
fn success(o: Output) -> String {
    assert!(o.status.success(), "{}", String::from_utf8_lossy(&o.stderr));
    String::from_utf8(o.stdout).unwrap()
}
fn refuses(o: Output) {
    assert!(!o.status.success());
    assert!(String::from_utf8_lossy(&o.stderr).contains("use a new output path"));
}
#[test]
fn set_guard_validation_and_measurement() {
    let tmp = Temp::new("guard");
    let path = tmp.0.join("cal.txt");
    let mut cal = calibration();
    cal.z_basis.clear();
    cal.write(&path).unwrap();
    let original = fs::read(&path).unwrap();
    for (z, x, basis) in [
        ("0", "0", ""),
        ("0", "0", " \t"),
        ("-1", "0", "ok"),
        ("0", "-1", "ok"),
        ("NaN", "0", "ok"),
        ("0", "inf", "ok"),
        ("-inf", "0", "ok"),
        ("0", "NaN", "ok"),
        ("4294967296", "0", "ok"),
        ("0", "4294967296", "ok"),
        ("0", "0", "bad\nline"),
    ] {
        assert!(
            !call(&[
                "set-guard",
                "--calibration",
                text(&path),
                "--z",
                z,
                "--x",
                x,
                "--basis",
                basis
            ])
            .status
            .success()
        );
        assert_eq!(fs::read(&path).unwrap(), original);
    }
    assert!(
        !call(&[
            "set-guard",
            "input.wav",
            "--calibration",
            text(&path),
            "--z",
            "0",
            "--x",
            "0",
            "--basis",
            "ok"
        ])
        .status
        .success()
    );
    assert_eq!(fs::read(&path).unwrap(), original);
    for command in [
        "analyze",
        "aggregate",
        "calibrate-offset",
        "calibrate-xtalk",
    ] {
        for option in ["--z", "--x", "--basis"] {
            assert!(
                !call(&[command, "--calibration", text(&path), option, "0"])
                    .status
                    .success()
            );
        }
    }
    let out = tmp.0.join("out");
    let input = tmp.0.join("short.wav");
    fs::write(&input, wav(&recording(vec![0.0; 480], vec![0.0; 480]))).unwrap();
    let args = [
        "analyze",
        text(&input),
        "--calibration",
        text(&path),
        "--markers",
        "1",
        "--out",
        text(&out),
    ];
    assert!(success(call(&args)).contains("ZBasisMissing"));
    fs::remove_dir_all(&out).unwrap();
    let line = success(call(&[
        "set-guard",
        "--calibration",
        text(&path),
        "--z",
        "3.5",
        "--x",
        "1.25",
        "--basis",
        "synthetic guard",
    ]));
    assert_eq!(line.lines().count(), 1);
    assert!(line.contains("window_samples=[-2,2] A_x=[-2,5.5] search_window=(-3.25,6.75]"));
    let updated = Calibration::read(&path).unwrap();
    cal.z = 3.5;
    cal.x = 1.25;
    cal.z_basis = "synthetic guard".into();
    assert_eq!(updated, cal);
    assert!(!success(call(&args)).contains("ZBasisMissing"));
    success(call(&[
        "set-guard",
        "--calibration",
        text(&path),
        "--z",
        "0",
        "--x",
        "0",
        "--basis",
        "zero",
    ]));
}
#[test]
fn outputs_are_preserved_and_stems_coexist() {
    let tmp = Temp::new("overwrite");
    let cal = tmp.0.join("cal.txt");
    calibration().write(&cal).unwrap();
    let input = tmp.0.join("first.wav");
    fs::write(&input, wav(&recording(vec![0.0; 480], vec![0.0; 480]))).unwrap();
    let out = tmp.0.join("out");
    let args = [
        "analyze",
        text(&input),
        "--calibration",
        text(&cal),
        "--markers",
        "1",
        "--out",
        text(&out),
    ];
    let names = [
        "first.run-metadata.txt",
        "first.markers.csv",
        "first.summary.csv",
    ];
    // Each target alone must prevent creation of all other targets.
    fs::create_dir(&out).unwrap();
    for name in names {
        let target = out.join(name);
        fs::write(&target, b"sentinel").unwrap();
        refuses(call(&args));
        assert_eq!(fs::read(&target).unwrap(), b"sentinel");
        assert_eq!(fs::read_dir(&out).unwrap().count(), 1);
        fs::remove_file(target).unwrap();
    }
    success(call(&args));
    let before: Vec<_> = names
        .iter()
        .map(|n| fs::read(out.join(n)).unwrap())
        .collect();
    refuses(call(&args));
    for (name, bytes) in names.iter().zip(&before) {
        assert_eq!(&fs::read(out.join(name)).unwrap(), bytes);
    }
    let second = tmp.0.join("second.wav");
    fs::copy(&input, &second).unwrap();
    success(call(&[
        "analyze",
        text(&second),
        "--calibration",
        text(&cal),
        "--markers",
        "1",
        "--out",
        text(&out),
    ]));
    assert_eq!(fs::read_dir(&out).unwrap().count(), 6);
    for (name, bytes) in names.iter().zip(&before) {
        assert_eq!(&fs::read(out.join(name)).unwrap(), bytes);
    }
    let summary = out.join("first.summary.csv");
    let aggregate_out = tmp.0.join("aggregate");
    fs::create_dir(&aggregate_out).unwrap();
    let agg_args = ["aggregate", text(&summary), "--out", text(&aggregate_out)];
    let agg_names = ["aggregate.run-metadata.txt", "aggregate.csv"];
    for name in agg_names {
        let target = aggregate_out.join(name);
        fs::write(&target, b"sentinel").unwrap();
        refuses(call(&agg_args));
        assert_eq!(fs::read(&target).unwrap(), b"sentinel");
        assert_eq!(fs::read_dir(&aggregate_out).unwrap().count(), 1);
        fs::remove_file(target).unwrap();
    }
    success(call(&agg_args));
    let before: Vec<_> = agg_names
        .iter()
        .map(|n| fs::read(aggregate_out.join(n)).unwrap())
        .collect();
    refuses(call(&agg_args));
    for (name, bytes) in agg_names.iter().zip(before) {
        assert_eq!(fs::read(aggregate_out.join(name)).unwrap(), bytes);
    }
    let before = fs::read(&cal).unwrap();
    refuses(call(&[
        "calibrate-offset",
        text(&input),
        "--markers",
        "1",
        "--out",
        text(&cal),
    ]));
    assert_eq!(fs::read(&cal).unwrap(), before);
}
