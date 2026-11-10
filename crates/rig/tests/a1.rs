mod common;
use common::*;
use mdc_rig::analyze::*;
use mdc_rig::*;
fn left(c: &AnalysisConfig) -> Vec<Detection> {
    marker_schedule(&c.chirp)
        .unwrap()
        .into_iter()
        .map(|(id, t, _)| {
            let bits = encode_marker_id(codeword_of_marker(id).unwrap()).unwrap();
            Detection {
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
                    outcome: decode_id(bits, 0),
                    word: Some(bits),
                    bits,
                    erasure_mask: 0,
                    segments_present: 17,
                    min_margin_db: None,
                },
            }
        })
        .collect()
}
fn at(l: &[Detection], id: usize, delay: f64) -> Detection {
    let mut p = l[id].clone();
    p.sync.time += delay;
    p
}
fn run(c: &AnalysisConfig, l: &[Detection], r: &[Detection]) -> Analysis {
    match_detections(c, Some(&calibration()), 10_000_000, l, r).unwrap()
}
#[test]
fn table_steps_and_diagnostics() {
    let c = config(4);
    let l = left(&c);
    let a = run(&c, &l, &[]);
    assert_eq!(a.markers[0].decision_step, 3);
    assert_eq!(a.markers[0].reason, "no_candidate");
    let a = run(&c, &l, &[at(&l, 1, 100.0)]);
    assert_eq!(a.markers[0].reason, "explained_elsewhere");
    assert_eq!(a.markers[0].decision_step, 4);
    let a = run(&c, &l, &[at(&l, 0, 0.0)]);
    assert_eq!(a.markers[0].reason, "xtalk_like");
    assert_eq!(a.markers[0].decision_step, 4);
    assert_eq!(a.blocked_other, 0);
    let a = run(&c, &l, &[at(&l, 0, 100.0), at(&l, 0, 101.0)]);
    assert_eq!(a.markers[0].reason, "tie");
    assert_eq!(a.markers[0].decision_step, 5);
    let mut p = at(&l, 0, 5.0);
    p.id.outcome = IdOutcome::Uncorrectable;
    let mut near_c = c.clone();
    near_c.z = 10.0;
    let a = run(&near_c, &l, &[p]);
    assert_eq!(a.xtalk_candidates, 0);
    assert_eq!(a.markers[0].decision_step, 6);
    assert_eq!(a.markers[0].reason, "abnormal_near");
    let mut boundary = at(&l, 0, 3.0);
    boundary.id.outcome = IdOutcome::Uncorrectable;
    let a = run(&c, &l, &[boundary]);
    assert_eq!(a.markers[0].reason, "boundary");
    let mut p = at(&l, 0, 100.0);
    p.id.bits ^= 3;
    p.id.outcome = decode_id(p.id.bits, 0);
    let a = run(&c, &l, &[p.clone(), at(&l, 0, 200.0)]);
    assert_eq!(a.markers[0].class, Some(Class::IdUndecodable));
    assert_eq!(a.markers[0].decision_step, 7);
    assert_eq!(a.blocked_earlier, 1);
    assert_eq!(a.blocked_self_id, 1);
    p.id.bits = !l[0].id.bits;
    p.id.outcome = IdOutcome::Uncorrectable;
    let a = run(&c, &l, &[p]);
    assert_eq!(a.markers[0].reason, "unassigned_candidate");
    let a = run(&c, &l, &[at(&l, 0, c.max_delay)]);
    assert_eq!(a.markers[0].decision_step, 8);
    assert_eq!(a.markers[0].reason, "out_of_support");
    assert_eq!(a.blocked_boundary, 1);
    let a = run(&c, &l, &[at(&l, 0, 100.0)]);
    assert_eq!(a.markers[0].decision_step, 9);
    assert_eq!(a.markers[0].delay, Some(100.0));
}
#[test]
fn wide_delays_xtalk_duplicates_and_unique_assignment() {
    let mut c = config(6);
    // Keep the entire xtalk interval inside W_hi + Z despite L uncertainty.
    c.z = 10.0;
    let l = left(&c);
    let mut r = (0..6)
        .map(|i| at(&l, i, [100.0, 47000.0, 1000.0, 33000.0, 21000.0, 9000.0][i]))
        .collect::<Vec<_>>();
    r.push(at(&l, 0, 0.0));
    r.push(at(&l, 0, 500.0));
    let a = run(&c, &l, &r);
    assert!(a.markers.iter().all(|m| m.class == Some(Class::Normal)));
    assert_eq!(a.r_duplicate, 1);
    assert_eq!(a.xtalk_candidates, 1);
    let mut times = a
        .markers
        .iter()
        .filter(|m| matches!(m.class, Some(Class::Normal | Class::IdUndecodable)))
        .map(|m| m.right.as_ref().unwrap().sync.time.to_bits())
        .collect::<Vec<_>>();
    let n = times.len();
    times.sort();
    times.dedup();
    assert_eq!(times.len(), n);
}
#[test]
fn xtalk_boundary_blocks_later_echo_but_contained_xtalk_allows_normal() {
    let mut c = config(6);
    c.z = 0.0;
    c.x = 0.0;
    c.xtalk_min_candidates = 10;
    let l = left(&c);
    let first = at(&l, 0, 4.0);
    let a = run(&c, &l, &[at(&l, 0, 200.0), first.clone()]);
    let m = &a.markers[0];
    assert_eq!(a.xtalk_candidates, 1);
    assert!(a.reject_reasons.is_empty());
    assert_eq!(m.class, Some(Class::Ambiguous));
    assert_eq!(m.reason, "xtalk_boundary");
    assert_eq!(m.decision_step, 4);
    assert_eq!(m.c0.as_ref().unwrap().sync.time, first.sync.time);
    assert_eq!(m.right.as_ref().unwrap().sync.time, first.sync.time);
    assert_eq!(m.delay, None);
    assert_eq!(a.blocked_boundary, 1);
    assert_eq!(a.blocked_other, 0);

    c.z = 10.0;
    let a = run(&c, &l, &[at(&l, 0, 0.0), at(&l, 0, 200.0)]);
    assert_eq!(a.xtalk_candidates, 1);
    assert_eq!(a.markers[0].class, Some(Class::Normal));
    assert_eq!(a.markers[0].decision_step, 9);
    assert_eq!(a.markers[0].delay, Some(200.0));
    assert_eq!(a.blocked_boundary, 0);
}
#[test]
fn source_distance_and_shared_candidates() {
    let mut c = config(8);
    c.source_max = 200000.0;
    let l = left(&c);
    let a = run(&c, &l, &[at(&l, 6, 100.0)]);
    assert_eq!(a.markers[6].reason, "source_set_distance");
    assert!(a.markers[6].distance_violations.contains(&(0, 6)));
    assert!(a.r_shared > 0);
    // A wide xtalk window includes the same violating pair: no xtalk exemption.
    let mut cal = calibration();
    cal.xtalk = mdc_rig::calib::CalibrationState::Measured(mdc_rig::calib::Xtalk {
        delay_min: 0.0,
        delay_max: 200000.0,
        ratio_min_db: -40.0,
        ratio_max_db: 0.0,
    });
    let a = match_detections(&c, Some(&cal), 10_000_000, &l, &[at(&l, 6, 0.0)]).unwrap();
    assert_eq!(a.xtalk_candidates, 0);
    assert_eq!(a.markers[6].decision_step, 6);
}
#[test]
fn three_bit_miscorrections_cannot_become_normal() {
    let c = config(8);
    let l = left(&c);
    for outside in [false, true] {
        let original = l[0].id.bits;
        let (bits, outcome) = (0..16)
            .flat_map(|a| {
                (a + 1..16).flat_map(move |b| {
                    (b + 1..16).map(move |d| (1u16 << a) | (1u16 << b) | (1u16 << d))
                })
            })
            .find_map(|mask| {
                let bits = original ^ mask;
                let out = decode_id(bits, 0);
                if let IdOutcome::Corrected { index, .. } = out {
                    let id = marker_of_codeword(index);
                    let is_out = id.is_none_or(|id| usize::from(id) >= c.chirp.marker_count);
                    if is_out == outside && (outside || id.is_some_and(|id| id > 0)) {
                        return Some((bits, out));
                    }
                }
                None
            })
            .unwrap();
        let mut p = at(&l, 0, 100.0);
        p.id.bits = bits;
        p.id.outcome = outcome;
        let a = run(&c, &l, &[p]);
        assert!(a.markers.iter().all(|m| m.class != Some(Class::Normal)));
        assert_ne!(a.markers[0].class, Some(Class::Normal));
        if outside {
            assert_eq!(a.outside_ids.len(), 1);
        } else {
            assert_eq!(a.e1_suspect, 1);
        }
    }
}
#[test]
fn source_config_validation() {
    let mut c = config(3);
    for value in [f64::NAN, 0.0, 47999.0] {
        c.source_max = value;
        assert!(c.validate().is_err());
    }
}
#[test]
fn uncertainty_expands_source_set_to_seven() {
    let mut c = config(8);
    c.chirp.interval_len = 16000;
    let mut l = left(&c);
    // Alternating residuals retain all Exact anchors but enlarge u_L.
    for (i, p) in l.iter_mut().enumerate() {
        p.sync.time += if i % 4 == 0 || i % 4 == 3 {
            2000.0
        } else {
            -2000.0
        };
    }
    let p = at(&left(&c), 6, 3000.0);
    let a = run(&c, &l, &[p]);
    assert!(a.reject_reasons.is_empty());
    assert_eq!(a.markers[6].reason, "source_set_distance");
    assert!(a.markers[6].distance_violations.contains(&(0, 6)));
}
#[test]
fn censored_markers_still_contribute_xtalk_and_erasure_corrections_count() {
    let mut c = config(12);
    c.x = 5.0;
    let l = left(&c);
    let r = l
        .iter()
        .cloned()
        .map(|mut p| {
            p.sync.time -= 3.0;
            p
        })
        .collect::<Vec<_>>();
    let a = match_detections(&c, Some(&calibration()), 1, &l, &r).unwrap();
    assert_eq!(a.xtalk_candidates, 12);
    assert!(
        a.reject_reasons
            .contains(&RejectReason::CalibrationMismatch)
    );
    assert!(a.markers.is_empty());
    let mut p = at(&l, 0, 100.0);
    p.id.erasure_mask = 1;
    p.id.bits &= !1;
    p.id.outcome = decode_id(p.id.bits, 1);
    let a = run(&c, &l, &[p]);
    assert_eq!(a.markers[0].class, Some(Class::Normal));
    let summary = mdc_rig::report::summarize(&a, &recording(vec![], vec![]), "", "");
    assert_eq!(summary.count("r_corrected").unwrap(), 1);
}
