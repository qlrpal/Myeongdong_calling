use mdc_rig::load::{Options, Priority, parse_args, period_parts, run_load};
use std::process::Command;
use std::time::{Duration, Instant};

fn options() -> Options {
    Options {
        threads: 2,
        duty_percent: 50,
        seconds: 1,
        priority: Priority::Normal,
        period_ms: 10,
        out: None,
    }
}

#[test]
fn parses_full_and_default_options() {
    let full = parse_args([
        "--threads",
        "4",
        "--duty",
        "50",
        "--seconds",
        "30",
        "--priority",
        "above-normal",
        "--period-ms",
        "20",
        "--out",
        "result.txt",
    ])
    .unwrap();
    assert_eq!(full.threads, 4);
    assert_eq!(full.priority, Priority::AboveNormal);
    assert_eq!(full.period_ms, 20);
    assert!(full.out.is_some());
    let defaults = parse_args(["--threads", "1", "--duty", "1", "--seconds", "1"]).unwrap();
    assert_eq!(defaults.priority, Priority::Normal);
    assert_eq!(defaults.period_ms, 10);
}

#[test]
fn rejects_missing_invalid_duplicate_unknown_and_time_critical() {
    assert!(parse_args(["--threads", "1", "--duty", "1"]).is_err());
    for (name, values) in [
        ("--threads", ["0", "257"]),
        ("--duty", ["0", "101"]),
        ("--seconds", ["0", "3601"]),
        ("--period-ms", ["0", "1001"]),
    ] {
        for value in values {
            let mut args = vec!["--threads", "1", "--duty", "1", "--seconds", "1"];
            args.extend([name, value]);
            if name != "--period-ms" {
                args = vec!["--threads", "1", "--duty", "1", "--seconds", "1"];
                let position = args.iter().position(|arg| *arg == name).unwrap();
                args[position + 1] = value;
            }
            assert!(parse_args(args).is_err(), "{name}={value}");
        }
    }
    assert!(
        parse_args([
            "--threads",
            "1",
            "--threads",
            "2",
            "--duty",
            "1",
            "--seconds",
            "1"
        ])
        .is_err()
    );
    assert!(
        parse_args([
            "--threads",
            "1",
            "--duty",
            "1",
            "--seconds",
            "1",
            "--wat",
            "2"
        ])
        .is_err()
    );
    assert!(
        parse_args([
            "--threads",
            "1",
            "--duty",
            "1",
            "--seconds",
            "1",
            "--priority",
            "time-critical"
        ])
        .is_err()
    );
}

#[test]
fn load_condition_is_stable() {
    let mut value = options();
    value.threads = 4;
    value.seconds = 30;
    value.priority = Priority::AboveNormal;
    assert_eq!(
        value.load_condition(),
        "threads=4;duty_percent=50;priority=above-normal;period_ms=10;seconds=30"
    );
}

#[test]
fn calculates_period_parts() {
    assert_eq!(
        period_parts(50, Duration::from_millis(10)),
        (Duration::from_millis(5), Duration::from_millis(5))
    );
    assert_eq!(
        period_parts(100, Duration::from_millis(10)).1,
        Duration::ZERO
    );
    assert!(!period_parts(1, Duration::from_millis(1)).0.is_zero());
}

#[test]
fn runs_short_load_and_reports_each_thread() {
    let value = options();
    let started = Instant::now();
    let results = run_load(&value, Duration::from_millis(300), |_| Ok(())).unwrap();
    assert_eq!(results.len(), 2);
    assert!(started.elapsed() >= Duration::from_millis(300));
    assert!(started.elapsed() < Duration::from_secs(5));
    assert!(results.iter().all(|result| {
        let duty = result.achieved_duty_percent();
        duty > 0.0 && duty <= 100.0
    }));
}

#[test]
fn priority_failure_is_returned() {
    assert!(
        run_load(&options(), Duration::from_millis(1), |_| Err(
            "no".to_owned()
        ))
        .is_err()
    );
}

#[test]
fn binary_success_bad_args_and_existing_out() {
    let binary = env!("CARGO_BIN_EXE_mdc-rig-load");
    let output = Command::new(binary)
        .args(["--threads", "1", "--duty", "10", "--seconds", "1"])
        .output()
        .unwrap();
    assert!(output.status.success());
    assert!(String::from_utf8(output.stdout).unwrap().lines().any(|line| line == "load_condition=threads=1;duty_percent=10;priority=normal;period_ms=10;seconds=1"));
    assert_eq!(
        Command::new(binary).arg("--bad").status().unwrap().code(),
        Some(2)
    );

    let path = std::env::temp_dir().join(format!("mdc-rig-load-existing-{}", std::process::id()));
    std::fs::write(&path, b"keep").unwrap();
    let status = Command::new(binary)
        .args(["--threads", "1", "--duty", "10", "--seconds", "1", "--out"])
        .arg(&path)
        .status()
        .unwrap();
    assert_eq!(status.code(), Some(1));
    assert_eq!(std::fs::read(&path).unwrap(), b"keep");
    std::fs::remove_file(path).unwrap();
}
