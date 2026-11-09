mod common;
use common::*;
use mdc_rig::analyze::*;
use mdc_rig::calib::*;
use mdc_rig::*;
fn run(c: &AnalysisConfig, l: Vec<f32>, r: Vec<f32>) -> Analysis {
    analyze(c, Some(&calibration()), &recording(l, r)).unwrap()
}
fn classes(a: &Analysis, class: Class) -> usize {
    a.markers.iter().filter(|r| r.class == Some(class)).count()
}
#[test]
fn b1_clean_100ms() {
    let c = config(4);
    let l = signal(&c);
    let a = run(&c, l.clone(), shifted(&l, 4800, 1.0));
    assert_eq!(classes(&a, Class::Normal), 4);
    for r in a.markers {
        assert!((r.delay.unwrap() - 4800.0).abs() <= 0.05);
    }
}
#[test]
fn b2_deleted() {
    let c = config(5);
    let l = signal(&c);
    let mut r = shifted(&l, 4800, 1.0);
    erase_marker(&c, &mut r, 2, 4800);
    let a = run(&c, l, r);
    assert_eq!(classes(&a, Class::Missing), 1);
    assert_eq!(a.markers[2].class, Some(Class::Missing));
}
#[test]
fn b3_errors() {
    let c = config(6);
    let l = signal(&c);
    let mut r = shifted(&l, 4800, 1.0);
    replace_bits(&c, &mut r, 1, 4800, &[5], false);
    replace_bits(&c, &mut r, 2, 4800, &[5, 9], false);
    replace_bits(&c, &mut r, 3, 4800, &(0..16).collect::<Vec<_>>(), true);
    let a = run(&c, l.clone(), r.clone());
    assert_eq!(classes(&a, Class::Missing), 0);
    assert_eq!(a.markers[1].class, Some(Class::Normal));
    assert!(matches!(
        a.markers[1].right.as_ref().unwrap().id.outcome,
        IdOutcome::Corrected { .. }
    ));
    for i in [2, 3] {
        assert_eq!(a.markers[i].class, Some(Class::IdUndecodable));
    }
    let s = mdc_rig::report::summarize(&a, &recording(l, r), "", "");
    assert_eq!(s.count("r_corrected").unwrap(), 1);
}
#[test]
fn b4_mismatch() {
    let c = config(5);
    let l = signal(&c);
    let mut r = shifted(&l, 4800, 1.0);
    let t = start(&c, 2) + 4800;
    for (v, s) in r[t..t + c.chirp.marker_len()]
        .iter_mut()
        .zip(marker_template(&c.chirp, 2047).unwrap())
    {
        *v = (s * 10f64.powf(-12.0 / 20.0)) as f32;
    }
    let a = run(&c, l, r);
    assert_eq!(a.markers[2].class, Some(Class::IdMismatch));
}
#[test]
fn b5_xtalk_and_silence() {
    let c = config(4);
    let l = signal(&c);
    let a = run(&c, l.clone(), shifted(&l, 0, 0.03));
    assert_eq!(classes(&a, Class::Normal), 0);
    assert_eq!(classes(&a, Class::Ambiguous), 4);
    let a = run(&c, l.clone(), vec![0.0; l.len()]);
    assert_eq!(classes(&a, Class::Missing), 4);
}
#[test]
fn b6_weak_real_xtalk() {
    let c = config(4);
    let l = signal(&c);
    let mut r = shifted(&l, 960, 0.1);
    for (dst, src) in r.iter_mut().zip(&l) {
        *dst += *src * 10f32.powf(-10.0 / 20.0);
    }
    let cal = Calibration {
        xtalk: Some(Xtalk {
            delay_min: 0.0,
            delay_max: 0.0,
            ratio_min_db: -10.0,
            ratio_max_db: -10.0,
        }),
        ..calibration()
    };
    let a = analyze(&c, Some(&cal), &recording(l, r)).unwrap();
    println!(
        "B6 Normal={} Ambiguous={} U={} Mismatch={} Missing={}",
        classes(&a, Class::Normal),
        classes(&a, Class::Ambiguous),
        classes(&a, Class::IdUndecodable),
        classes(&a, Class::IdMismatch),
        classes(&a, Class::Missing)
    );
    for r in a.markers {
        assert!(matches!(r.class, Some(Class::Normal | Class::Ambiguous)));
        if let Some(d) = r.delay {
            assert!((d - 960.0).abs() <= 2.0);
        }
    }
}
#[test]
fn b7_long_delay() {
    let c = config(4);
    let l = signal(&c);
    let a = run(&c, l.clone(), shifted(&l, 28800, 1.0));
    assert_eq!(classes(&a, Class::Normal), 4);
    assert!(
        a.markers
            .iter()
            .all(|r| (r.delay.unwrap() - 28800.0).abs() < 0.05)
    );
}
#[test]
fn b8_censored() {
    let c = config(8);
    let l = signal(&c);
    let rec = recording(l.clone(), shifted(&l, 4800, 1.0));
    let begin = start(&c, 0) + 100;
    let end = start(&c, 7) + 4000;
    let rec = recording(
        rec.left[begin..end].to_vec(),
        rec.right[begin..end].to_vec(),
    );
    let a = analyze(&c, Some(&calibration()), &rec).unwrap();
    assert_eq!(a.markers[0].left_class, LeftClass::Censored);
    assert_eq!(a.markers[7].left_class, LeftClass::Censored);
    let s = mdc_rig::report::summarize(&a, &rec, "", "");
    assert!(s.count("censored").unwrap() >= 3);
    assert_eq!(
        s.count("observable").unwrap() + s.count("censored").unwrap(),
        8
    );
    assert_eq!(s.value("l_evaluable_rate").unwrap(), Some(1.0));
}
#[test]
fn b9_exact_only() {
    let c = config(5);
    let mut l = signal(&c);
    let r = shifted(&l, 4800, 1.0);
    replace_bits(&c, &mut l, 2, 0, &[0, 1, 2], false);
    let a = run(&c, l, r);
    assert_eq!(a.alignment.anchors, 4);
    assert!(a.alignment.ppm.abs() < 0.01);
    let c = config(3);
    let mut l = signal(&c);
    let r = shifted(&l, 4800, 1.0);
    replace_bits(&c, &mut l, 1, 0, &[0, 1, 2], false);
    assert!(analyze(&c, Some(&calibration()), &recording(l, r)).is_err());
}
#[test]
#[ignore = "sinc resampling acceptance; run in release"]
fn b10_ppm() {
    let c = config(5);
    let x = signal(&c);
    for ppm in [-100.0, 100.0] {
        let l = resample(&x, 0.0, 1.0 + ppm / 1e6);
        let a = run(&c, l.clone(), shifted(&l, 4800, 1.0));
        println!("B10 input_ppm={ppm} estimated_ppm={:.9}", a.alignment.ppm);
        assert_eq!(classes(&a, Class::Normal), 5);
        assert!((a.alignment.ppm - ppm).abs() < 1.0);
    }
}
#[test]
fn b11_calibration() {
    let c = config(3);
    let l = signal(&c);
    let rec = recording(l.clone(), resample(&l, 0.3, 1.0));
    let (mut cal, s) = calibrate_offset(&c, &rec, "offset.wav").unwrap();
    println!(
        "B11 offset={:.9} min={:.9} max={:.9}",
        s.median, s.min, s.max
    );
    assert!((cal.channel_offset - 0.3).abs() <= 0.05);
    let rec = recording(l.clone(), shifted(&l, 0, 10f32.powf(-30.0 / 20.0)));
    assert_eq!(calibrate_xtalk(&c, &rec, &mut cal, "xtalk.wav").unwrap(), 3);
    let x = cal.xtalk.as_ref().unwrap();
    println!("B11 xtalk={x:?} W={:?}", cal.window());
    assert!(x.delay_min.abs() < 0.05 && x.delay_max.abs() < 0.05);
    assert!((x.ratio_min_db + 30.0).abs() < 0.05);
    let text = cal.to_text().unwrap();
    let restored = Calibration::from_text(&text).unwrap();
    assert!((restored.channel_offset - cal.channel_offset).abs() < 1e-11);
    assert_eq!(restored.source_files, cal.source_files);
    let tmp = Temp::new("calibration");
    cal.write(tmp.0.join("cal.txt")).unwrap();
    assert!(Calibration::read(tmp.0.join("cal.txt")).is_ok());
    assert!(analyze(&c, None, &rec).is_err());
    for invalid in [
        text.replace("version=1", "version=2"),
        text.replace("channel_offset=", "channel_offset=NaN"),
        format!("{text}version=1\n"),
    ] {
        assert!(Calibration::from_text(&invalid).is_err());
    }
    let rec = recording(l.clone(), vec![0.0; l.len()]);
    calibrate_xtalk(&c, &rec, &mut cal, "silent.wav").unwrap();
    assert!(cal.xtalk.is_none());
}
#[test]
fn b14_all_undecodable() {
    let c = config(4);
    let l = signal(&c);
    let mut r = shifted(&l, 4800, 1.0);
    for id in 0..4 {
        replace_bits(&c, &mut r, id, 4800, &[5, 9], false);
    }
    let a = run(&c, l, r);
    assert_eq!(classes(&a, Class::Normal), 0);
    assert_eq!(classes(&a, Class::IdUndecodable), 0);
    assert_eq!(classes(&a, Class::Missing), 0);
    assert_eq!(classes(&a, Class::Ambiguous), 4);
    assert!(
        a.markers
            .iter()
            .all(|r| r.reason == "unassigned_undecodable")
    );
    assert!(a.warnings.iter().any(|w| w == "no_latency_reference"));
}
#[test]
fn b14b_far_undecodable() {
    let c = config(5);
    let l = signal(&c);
    let mut r = shifted(&l, 4800, 1.0);
    replace_bits(&c, &mut r, 4, 4800, &[5, 9], false);
    let t = start(&c, 4) + 4800;
    let marker = r[t..t + c.chirp.marker_len()].to_vec();
    r[t..t + c.chirp.marker_len()].fill(0.0);
    r[t + 13000..t + 13000 + c.chirp.marker_len()].copy_from_slice(&marker);
    let a = run(&c, l, r);
    assert_eq!(classes(&a, Class::Normal), 4);
    assert_eq!(a.markers[4].class, Some(Class::Ambiguous));
    assert_eq!(a.markers[4].reason, "unassigned_undecodable");
    assert_eq!(classes(&a, Class::Missing), 0);
}
#[test]
fn window_boundaries_duplicates_and_alignment_outlier() {
    let c = config(7);
    let x = signal(&c);
    let left = detect_channel(&c, &x).unwrap();
    let frames = x.len() + 48000;
    for d in [-2.0, 2.0] {
        let right: Vec<_> = left
            .iter()
            .cloned()
            .map(|mut p| {
                p.sync.time += d;
                p
            })
            .collect();
        let a = match_detections(&c, Some(&calibration()), frames, &left, &right).unwrap();
        assert_eq!(classes(&a, Class::Normal), 0);
        assert_eq!(classes(&a, Class::Ambiguous), 7);
    }
    let mut right = Vec::new();
    for l in &left {
        for d in [4900.0, 0.0, 4800.0] {
            let mut p = l.clone();
            p.sync.time += d;
            right.push(p);
        }
    }
    let a = match_detections(&c, Some(&calibration()), frames, &left, &right).unwrap();
    assert_eq!(a.r_duplicate, 7);
    assert_eq!(classes(&a, Class::Normal), 7);
    assert!(
        a.markers
            .iter()
            .all(|r| (r.delay.unwrap() - 4800.0).abs() < 1e-8 && r.near_candidates.len() == 1)
    );
    let mut bad = left.clone();
    bad[3].sync.time += 12000.0;
    let alignment = align_left(&c, &bad).unwrap();
    assert_eq!(alignment.anchors, 6);
    assert!(alignment.ppm.abs() < 0.001);
    let mut invalid = right.clone();
    invalid[0].sync.time = f64::NAN;
    assert!(match_detections(&c, Some(&calibration()), frames, &left, &invalid).is_err());
    let mut config = c.clone();
    config.max_delay = f64::NAN;
    assert!(config.validate().is_err());
}
