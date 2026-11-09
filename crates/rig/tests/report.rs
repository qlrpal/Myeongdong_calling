mod common;
use common::*;
use mdc_rig::analyze::*;
use mdc_rig::report::*;
#[test]
fn b12_reports() {
    assert_eq!(percentile(&[3.0], 99), Some(3.0));
    assert_eq!(percentile(&[10.0, 20.0], 50), Some(10.0));
    assert_eq!(percentile(&[10.0, 20.0], 95), Some(20.0));
    let v: Vec<_> = (1..=600).map(f64::from).collect();
    assert_eq!(percentile(&v, 50), Some(300.0));
    assert_eq!(percentile(&v, 95), Some(570.0));
    assert_eq!(percentile(&v, 99), Some(594.0));
    assert_eq!(percentile(&[], 50), None);
    assert_eq!(sample_stddev(&[1.0]), None);
    assert!((sample_stddev(&[1.0, 3.0]).unwrap() - 2f64.sqrt()).abs() < 1e-12);
    let c = config(3);
    let l = signal(&c);
    let rec = recording(l.clone(), shifted(&l, 4800, 1.0));
    let a = analyze(&c, Some(&calibration()), &rec).unwrap();
    let s = summarize(&a, &rec, "a,\"b\"\nline", "input,\"x\".wav");
    let mut csv = Vec::new();
    write_summary(&mut csv, &s).unwrap();
    assert_eq!(
        String::from_utf8(csv.clone())
            .unwrap()
            .lines()
            .next()
            .unwrap(),
        SUMMARY_HEADER.join(",")
    );
    let restored = read_summary(csv.as_slice()).unwrap();
    assert_eq!(s.fields, restored.fields);
    let agg = aggregate(std::slice::from_ref(&s)).unwrap();
    assert_eq!(agg[0].fields["p50_sample_stddev_ms"], "NA");
    assert_eq!(agg[0].fields["runs"], "1");
    let mut other = s.clone();
    other.fields.insert("label".into(), "second".into());
    assert_eq!(aggregate(&[s.clone(), other.clone()]).unwrap().len(), 2);
    other.fields.insert("settings".into(), "different".into());
    assert!(aggregate(&[s.clone(), other]).is_err());
    let mut other = s.clone();
    other
        .fields
        .insert("calibration".into(), "different".into());
    assert!(aggregate(&[s.clone(), other]).is_err());
    let mut other = s.clone();
    other
        .fields
        .insert("p50_ms".into(), "200.000000000000".into());
    other.fields.insert("normal".into(), "600".into());
    let agg = aggregate(&[s.clone(), other]).unwrap();
    assert_eq!(agg[0].fields["p50_mean_ms"], "150.000000000000");
    assert_eq!(agg[0].fields["runs"], "2");
    let mut out = Vec::new();
    write_aggregate(&mut out, &agg).unwrap();
    assert_eq!(
        String::from_utf8(out).unwrap().lines().next().unwrap(),
        AGGREGATE_HEADER.join(",")
    );
    let mut markers = Vec::new();
    write_markers(&mut markers, &a).unwrap();
    assert_eq!(
        String::from_utf8(markers).unwrap().lines().next().unwrap(),
        MARKER_HEADER.join(",")
    );
    let silent = recording(l.clone(), vec![0.0; l.len()]);
    let a = analyze(&c, Some(&calibration()), &silent).unwrap();
    let s = summarize(&a, &silent, "", "");
    assert_eq!(s.get("p50_ms").unwrap(), "NA");
    assert_eq!(s.get("p99_samples").unwrap(), "NA");
    let mut csv = Vec::new();
    write_summary(&mut csv, &s).unwrap();
    assert!(!String::from_utf8(csv.clone()).unwrap().contains("NaN"));
    read_summary(csv.as_slice()).unwrap();
}
#[test]
fn b13_cli() {
    use std::process::Command;
    let tmp = Temp::new("cli");
    let c = config(3);
    let l = signal(&c);
    let rec = recording(l.clone(), l.clone());
    let wavpath = tmp.0.join("offset.wav");
    std::fs::write(&wavpath, wav(&rec)).unwrap();
    let cal = tmp.0.join("cal.txt");
    let out = tmp.0.join("output");
    let binary = env!("CARGO_BIN_EXE_mdc-rig-analyze");
    let call = |args: &[&str]| {
        let output = Command::new(binary).args(args).output().unwrap();
        assert!(
            output.status.success(),
            "args={args:?} stderr={}",
            String::from_utf8_lossy(&output.stderr)
        );
    };
    call(&[
        "calibrate-offset",
        wavpath.to_str().unwrap(),
        "--markers",
        "3",
        "--out",
        cal.to_str().unwrap(),
    ]);
    call(&[
        "calibrate-xtalk",
        wavpath.to_str().unwrap(),
        "--markers",
        "3",
        "--calibration",
        cal.to_str().unwrap(),
    ]);
    let rec = recording(l.clone(), shifted(&l, 4800, 1.0));
    let input = tmp.0.join("measure.wav");
    std::fs::write(&input, wav(&rec)).unwrap();
    call(&[
        "analyze",
        input.to_str().unwrap(),
        "--markers",
        "3",
        "--calibration",
        cal.to_str().unwrap(),
        "--out",
        out.to_str().unwrap(),
        "--label",
        "test",
    ]);
    let summary = out.join("measure.summary.csv");
    let s = read_summary(std::fs::File::open(&summary).unwrap()).unwrap();
    assert_eq!(s.count("normal").unwrap(), 3);
    call(&[
        "aggregate",
        summary.to_str().unwrap(),
        "--out",
        out.to_str().unwrap(),
    ]);
    assert!(out.join("aggregate.csv").is_file());
    for args in [
        vec![],
        vec!["analyze"],
        vec!["calibrate-offset"],
        vec!["calibrate-xtalk"],
        vec!["aggregate"],
        vec![
            "analyze",
            input.to_str().unwrap(),
            "--calibration",
            cal.to_str().unwrap(),
            "--markers",
            "0",
        ],
        vec!["aggregate", summary.to_str().unwrap(), "--label", "bad"],
        vec!["analyze", input.to_str().unwrap(), "--unknown", "x"],
        vec![
            "calibrate-offset",
            input.to_str().unwrap(),
            "--out",
            cal.to_str().unwrap(),
            "--out",
            "x",
        ],
    ] {
        assert!(
            !Command::new(binary)
                .args(args)
                .output()
                .unwrap()
                .status
                .success()
        );
    }
    let failure = Command::new(binary)
        .args(["analyze", input.to_str().unwrap()])
        .output()
        .unwrap();
    assert!(!failure.status.success());
    assert!(String::from_utf8_lossy(&failure.stderr).contains("판정 불가"));
    let failure = Command::new(binary)
        .args([
            "analyze",
            input.to_str().unwrap(),
            "--calibration",
            tmp.0.join("absent").to_str().unwrap(),
        ])
        .output()
        .unwrap();
    assert!(!failure.status.success());
    assert!(String::from_utf8_lossy(&failure.stderr).contains("판정 불가"));
}
