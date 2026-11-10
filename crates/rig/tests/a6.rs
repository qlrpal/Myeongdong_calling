mod common;
use common::*;
use mdc_rig::analyze::*;
use mdc_rig::calib::*;
use mdc_rig::*;

fn detections(c: &AnalysisConfig) -> Vec<Detection> {
    marker_schedule(&c.chirp)
        .unwrap()
        .into_iter()
        .map(|(id, t, _)| Detection {
            sync: SyncCandidate {
                time: t as f64,
                peak_index: t,
                q: 1.0,
                amplitude: 0.2,
                level_dbfs: -14.0,
                polarity: 1,
                overlap: false,
            },
            id: IdReading {
                outcome: IdOutcome::Exact(codeword_of_marker(id).unwrap()),
                word: None,
                bits: encode_marker_id(codeword_of_marker(id).unwrap()).unwrap(),
                erasure_mask: 0,
                segments_present: 17,
                min_margin_db: None,
            },
        })
        .collect()
}
fn matched(c: &AnalysisConfig, cal: &Calibration, l: &[Detection], r: &[Detection]) -> Analysis {
    match_detections(c, Some(cal), 10_000_000, l, r).unwrap()
}
fn shifted_detections(l: &[Detection], d: f64) -> Vec<Detection> {
    l.iter()
        .cloned()
        .map(|mut p| {
            p.sync.time += d;
            p
        })
        .collect()
}
#[test]
fn anchors_uncertainty_and_intervals() {
    let c = config(8);
    let l = detections(&c);
    let mut cases = vec![l[..2].to_vec(), vec![l[0].clone(); 3]];
    // Three detections but fewer than three distinct scheduled anchor times.
    cases.push(vec![l[0].clone(), l[1].clone(), l[1].clone()]);
    let mut compressed = l[..3].to_vec();
    compressed[2].sync.time -= 1.0;
    // A one-sample compression satisfies the residual and ppm criteria.
    assert!(align_left(&c, &compressed).is_ok());
    for bad in cases {
        let a = matched(&c, &calibration(), &bad, &[]);
        assert!(a.reject_reasons.contains(&RejectReason::AlignmentFailed));
        assert!(a.markers.is_empty());
    }
    let mut anchors = l[2..6].to_vec();
    anchors[1].sync.time += 4.0;
    let a = align_left(&c, &anchors).unwrap();
    assert_eq!(a.u, (a.residual_max + 1.0).max(2.0));
    for t in [
        0.0,
        start(&c, 0) as f64,
        start(&c, 3) as f64,
        start(&c, 7) as f64,
        1e7,
    ] {
        let n = a.anchor_times.len() as f64;
        let mean = a.anchor_times.iter().sum::<f64>() / n;
        let den = a
            .anchor_times
            .iter()
            .map(|v| (v - mean).powi(2))
            .sum::<f64>();
        let expected: Vec<_> = a
            .anchor_times
            .iter()
            .map(|v| 1.0 / n + (t - mean) * (v - mean) / den)
            .collect();
        assert!((expected.iter().sum::<f64>() - 1.0).abs() < 1e-10);
        assert_eq!(a.weights(t), expected);
        assert!(
            (a.uncertainty(t) - a.u * expected.iter().map(|h| h.abs()).sum::<f64>()).abs() < 1e-10
        );
        assert!(a.uncertainty(t) >= a.u);
    }
    assert!(a.uncertainty(1e7) > a.uncertainty(start(&c, 3) as f64));
    let result = matched(&c, &calibration(), &anchors, &[]);
    for row in result.markers {
        let t = start(&c, usize::from(row.id)) as f64;
        assert_eq!(row.predicted_left, a.predict(t));
        assert_eq!(row.left_uncertainty, a.uncertainty(t));
        let actual = if row.left_class == LeftClass::Ok {
            row.left.unwrap().sync.time
        } else {
            row.predicted_left
        };
        assert_eq!(
            row.left_interval,
            (
                actual.min(row.predicted_left) - row.left_uncertainty,
                actual.max(row.predicted_left) + row.left_uncertainty
            )
        );
    }
}
#[test]
fn alignment_accepts_three_anchors_at_ppm_extremes() {
    let mut c = config(3);
    c.mode = AnalysisMode::Measurement;
    let cal = calibration();
    assert!(!cal.z_basis.is_empty());
    cal.validate().unwrap();
    let original = detections(&c);
    for ppm in [-100.0, 0.0, 100.0] {
        let mut l = original.clone();
        for p in &mut l {
            p.sync.time = 1000.0 + (1.0 + ppm * 1e-6) * p.sync.time;
        }
        let alignment = align_left(&c, &l).unwrap();
        assert_eq!(alignment.anchors, 3);
        assert!((alignment.ppm - ppm).abs() < 1e-6, "ppm={ppm}");
        let a = matched(&c, &cal, &l, &[]);
        assert!(!a.reject_reasons.contains(&RejectReason::AlignmentFailed));
        assert!(!a.reject_reasons.contains(&RejectReason::PpmOutOfRange));
    }
}
#[test]
fn ppm_and_multiple_reasons() {
    let mut c = config(6);
    let original = detections(&c);
    for ppm in [-101.0, -100.0, 0.0, 100.0, 101.0] {
        let l: Vec<_> = original
            .iter()
            .cloned()
            .map(|mut p| {
                p.sync.time *= 1.0 + ppm / 1e6;
                p
            })
            .collect();
        let a = matched(&c, &calibration(), &l, &[]);
        assert_eq!(
            a.reject_reasons.contains(&RejectReason::PpmOutOfRange),
            ppm.abs() > 100.0
        );
        if ppm.abs() > 100.0 {
            assert!(a.markers.is_empty());
        }
    }
    c.mode = AnalysisMode::Measurement;
    for (state, reason) in [
        (
            CalibrationState::NotPerformed,
            RejectReason::CalibrationNotPerformed,
        ),
        (
            CalibrationState::Insufficient,
            RejectReason::CalibrationInsufficient,
        ),
    ] {
        let cal = Calibration {
            xtalk: state,
            z_basis: String::new(),
            ..calibration()
        };
        let a = matched(&c, &cal, &original[..2], &[]);
        assert_eq!(a.reject_reasons.len(), 3);
        for expected in [
            reason,
            RejectReason::ZBasisMissing,
            RejectReason::AlignmentFailed,
        ] {
            assert!(a.reject_reasons.contains(&expected));
        }
        assert!(a.markers.is_empty());
        let a = match_detections(&c, None, 1_000_000, &original, &[]).unwrap();
        assert!(
            a.reject_reasons
                .contains(&RejectReason::CalibrationNotPerformed)
        );
    }
}
#[test]
fn left_uniqueness_and_duplicate_amplitude() {
    let c = config(8);
    let mut l = detections(&c);
    let mut extra = l[3].clone();
    // Corrected candidates affect L selection, never Exact alignment anchors.
    extra.id.outcome = IdOutcome::Corrected {
        index: codeword_of_marker(3).unwrap(),
        flipped_bit: 0,
    };
    extra.sync.time += 20.0;
    extra.sync.amplitude = 0.4;
    l.push(extra);
    let a = matched(&c, &calibration(), &l, &shifted_detections(&l, 4800.0));
    assert_eq!(a.markers[3].left_class, LeftClass::Ambiguous);
    assert!(a.markers[3].left.is_none());
    assert!(a.markers[3].class.is_none());
    l.last_mut().unwrap().sync.time -= 18.0;
    let a = matched(&c, &calibration(), &l, &[]);
    assert_eq!(a.markers[3].left_class, LeftClass::Ok);
    assert_eq!(a.markers[3].left.as_ref().unwrap().sync.amplitude, 0.4);
}
#[test]
fn xtalk_safety_boundaries_and_sample_count() {
    let mut c = config(12);
    c.z = 8.0;
    c.x = 5.0;
    let l = detections(&c);
    // W=[-2,2], A_x=[-2,10], search=(-7,15].
    for (d, rejected, unchecked) in [
        (-7.0, true, false),
        (-6.99, true, false),
        (-2.01, true, false),
        (-2.0, false, false),
        (2.0, false, false),
        (9.99, false, false),
        (10.0, false, false),
        (10.01, true, false),
        (15.0, true, false),
        (15.01, true, false),
    ] {
        let r = shifted_detections(&l, d);
        let a = matched(&c, &calibration(), &l, &r);
        assert_eq!(
            a.reject_reasons
                .contains(&RejectReason::CalibrationMismatch),
            rejected,
            "d={d}"
        );
        assert_eq!(a.e5_unchecked, unchecked, "d={d}");
        if rejected {
            assert!(a.markers.is_empty());
        }
    }
    for count in [9, 10] {
        let r = shifted_detections(&l[..count], 10.01);
        let a = matched(&c, &calibration(), &l, &r);
        assert_eq!(
            a.reject_reasons
                .contains(&RejectReason::CalibrationMismatch),
            count >= 10
        );
        assert_eq!(a.e5_unchecked, count < 10);
        if count < 10 {
            assert!(a.warnings.iter().any(|s| s == "E5_unchecked"));
        }
    }
    let mut cal = calibration();
    cal.xtalk = CalibrationState::Insufficient;
    cal.z_basis.clear();
    c.mode = AnalysisMode::Measurement;
    cal.z = c.z;
    cal.x = c.x;
    let a = matched(&c, &cal, &l, &shifted_detections(&l, 10.01));
    assert_eq!(a.reject_reasons.len(), 3);
    assert!(
        a.reject_reasons
            .contains(&RejectReason::CalibrationMismatch)
    );
    assert!(a.reject_reasons.contains(&RejectReason::ZBasisMissing));
    assert!(
        a.reject_reasons
            .contains(&RejectReason::CalibrationInsufficient)
    );
}
#[test]
fn calibration_states_and_version_policy() {
    let mut c = config(4);
    let l = signal(&c);
    let rec = recording(l.clone(), l.clone());
    let (mut cal, _) = calibrate_offset(&c, &rec, "offset").unwrap();
    assert_eq!(cal.xtalk, CalibrationState::NotPerformed);
    let silent = recording(l.clone(), vec![0.0; l.len()]);
    calibrate_xtalk(&c, &silent, &mut cal, "silent").unwrap();
    assert_eq!(cal.xtalk, CalibrationState::Insufficient);
    c.no_detection = Some(NoDetectionCriteria {
        min_left: 4,
        min_frames: l.len(),
        detection_limit: 0.01,
    });
    calibrate_xtalk(&c, &silent, &mut cal, "silent-valid").unwrap();
    assert_eq!(cal.xtalk, CalibrationState::ValidNoDetection);
    c.no_detection.as_mut().unwrap().min_frames += 1;
    calibrate_xtalk(&c, &silent, &mut cal, "short").unwrap();
    assert_eq!(cal.xtalk, CalibrationState::Insufficient);
    c.no_detection.as_mut().unwrap().min_frames = l.len();
    c.no_detection.as_mut().unwrap().detection_limit = 1e-12;
    calibrate_xtalk(&c, &silent, &mut cal, "limit").unwrap();
    assert_eq!(cal.xtalk, CalibrationState::Insufficient);
    calibrate_xtalk(
        &c,
        &recording(vec![0.0; l.len()], vec![0.0; l.len()]),
        &mut cal,
        "no-left",
    )
    .unwrap();
    assert_eq!(cal.xtalk, CalibrationState::Insufficient);
    calibrate_xtalk(&c, &rec, &mut cal, "measured").unwrap();
    assert!(matches!(cal.xtalk, CalibrationState::Measured(_)));
    for state in [
        CalibrationState::NotPerformed,
        CalibrationState::Insufficient,
        CalibrationState::ValidNoDetection,
        cal.xtalk.clone(),
    ] {
        cal.xtalk = state;
        cal.z = 8.0;
        cal.x = 5.0;
        cal.z_basis = "docs/experiments/rig-validation.md".into();
        let text = cal.to_text().unwrap();
        let restored = Calibration::from_text(&text).unwrap();
        assert_eq!(restored.xtalk, cal.xtalk);
        assert_eq!(
            (restored.z, restored.x, restored.z_basis),
            (cal.z, cal.x, cal.z_basis.clone())
        );
        assert!(Calibration::from_text(&text.replace("version=2", "version=1")).is_err());
    }
    let legacy = "version=1\nchannel_offset=0\nxtalk_delay_min=none\nxtalk_delay_max=none\nxtalk_ratio_min_db=none\nxtalk_ratio_max_db=none\nsource_files=legacy\n";
    assert!(Calibration::from_text(legacy).is_err());
}
#[test]
fn rejected_summary_roundtrip() {
    let c = config(4);
    let l = detections(&c);
    let mut cal = calibration();
    cal.xtalk = CalibrationState::NotPerformed;
    let a = matched(&c, &cal, &l, &[]);
    let rec = recording(vec![0.0; 10], vec![0.0; 10]);
    let s = mdc_rig::report::summarize(&a, &rec, "rejected", "");
    assert!(
        s.get("reject_reasons")
            .unwrap()
            .contains("CalibrationNotPerformed")
    );
    let mut bytes = Vec::new();
    mdc_rig::report::write_summary(&mut bytes, &s).unwrap();
    assert_eq!(
        mdc_rig::report::read_summary(bytes.as_slice())
            .unwrap()
            .fields,
        s.fields
    );
    assert_eq!(s.count("normal").unwrap(), 0);
}
