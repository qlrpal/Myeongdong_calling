use mdc_rig::analyze::{AnalysisConfig, Detection, align_left, decoded_id};
use mdc_rig::*;

fn index(outcome: IdOutcome) -> Option<u16> {
    match outcome {
        IdOutcome::Exact(i)
        | IdOutcome::Corrected { index: i, .. }
        | IdOutcome::ErasureCorrected { index: i, .. } => Some(i),
        _ => None,
    }
}

#[test]
fn a0_placement_reproduction_and_hash() {
    let table = marker_placement();
    assert_eq!(table.len(), 1704);
    assert_eq!(
        &table[..12],
        &[0, 15, 113, 126, 402, 413, 160, 7, 73, 118, 314, 405]
    );
    let words: Vec<_> = (0..CODE_SPACE as u16)
        .map(|i| encode_marker_id(i).unwrap())
        .collect();
    let mut remaining: Vec<_> = (0..CODE_SPACE as u16).collect();
    let mut reproduced = Vec::new();
    loop {
        let candidate =
            remaining.iter().position(|&c| {
                reproduced.iter().rev().take(5).all(|&p: &u16| {
                    (words[usize::from(c)] ^ words[usize::from(p)]).count_ones() >= 8
                })
            });
        let Some(k) = candidate else { break };
        reproduced.push(remaining.remove(k));
    }
    assert_eq!(table, reproduced);
    let mut seen = [false; CODE_SPACE];
    for (i, &c) in table.iter().enumerate() {
        assert!(!seen[usize::from(c)]);
        seen[usize::from(c)] = true;
        assert_eq!(codeword_of_marker(i as u16), Some(c));
        assert_eq!(marker_of_codeword(c), Some(i as u16));
    }
    for window in table.windows(6) {
        for (i, &a) in window.iter().enumerate() {
            for &b in &window[i + 1..] {
                assert!((words[usize::from(a)] ^ words[usize::from(b)]).count_ones() >= 8);
            }
        }
    }
    for c in 0..=u16::MAX {
        if c >= CODE_SPACE as u16 || !seen[usize::from(c)] {
            assert_eq!(marker_of_codeword(c), None);
        }
    }
    for i in MAX_MARKERS as u16..=u16::MAX {
        assert_eq!(codeword_of_marker(i), None);
    }
    let hash = table
        .iter()
        .flat_map(|c| c.to_le_bytes())
        .fold(0xcbf29ce484222325u64, |h, b| {
            (h ^ u64::from(b)).wrapping_mul(0x100000001b3)
        });
    assert_eq!(hash, 0xf04865d91d86aa86);
    println!("A0 FNV-1a64 LE u16: {hash:016x}");
}

#[test]
fn a0_schedule_and_templates() {
    assert_eq!((CODE_SPACE, MAX_MARKERS), (2048, 1704));
    let mut cfg = ChirpConfig {
        marker_count: MAX_MARKERS,
        ..Default::default()
    };
    cfg.validate().unwrap();
    let up = bit_template(&cfg, false);
    let down = bit_template(&cfg, true);
    for (i, start, word) in marker_schedule(&cfg).unwrap() {
        assert_eq!(start, cfg.lead_silence + usize::from(i) * cfg.interval_len);
        assert_eq!(
            word,
            encode_marker_id(codeword_of_marker(i).unwrap()).unwrap()
        );
        let template = marker_template(&cfg, i).unwrap();
        for slot in 0..16 {
            let start = cfg.sync_len + cfg.gap_len + slot * (cfg.bit_len + cfg.gap_len);
            let expected = if word & (1 << (15 - slot)) == 0 {
                &up
            } else {
                &down
            };
            assert_eq!(&template[start..start + cfg.bit_len], expected);
        }
    }
    cfg.marker_count += 1;
    assert!(cfg.validate().is_err());
    assert!(marker_schedule(&cfg).is_err());
    assert!(generate_samples(&cfg).is_err());
    assert!(marker_template(&ChirpConfig::default(), 1704).is_err());
    for c in 0..CODE_SPACE as u16 {
        assert_eq!(
            decode_marker_id(encode_marker_id(c).unwrap()),
            MarkerIdDecode::Exact(c)
        );
    }
    for c in CODE_SPACE as u16..=u16::MAX {
        assert_eq!(encode_marker_id(c), None);
    }
}

#[test]
fn a3_6a_dec_67584() {
    let mut count = 0;
    for c in 0..CODE_SPACE as u16 {
        let word = encode_marker_id(c).unwrap();
        assert_eq!(decode_id(word, 0), IdOutcome::Exact(c));
        count += 1;
        for bit in 0..16 {
            let mask = 1 << bit;
            assert_eq!(
                decode_id(word ^ mask, 0),
                IdOutcome::Corrected {
                    index: c,
                    flipped_bit: bit
                }
            );
            assert_eq!(
                decode_id(word & !mask, mask),
                IdOutcome::ErasureCorrected {
                    index: c,
                    erased_bit: bit
                }
            );
            // Erased input values are ignored, including a supplied one.
            assert_eq!(decode_id(word | mask, mask), decode_id(word & !mask, mask));
            count += 2;
        }
    }
    assert_eq!(count, 67584);
    println!("6a-dec {count}/{count} correct codeword confirmations");
}

#[test]
fn a3_read_id_slot_erasures() {
    let cfg = ChirpConfig {
        marker_count: 1,
        ..Default::default()
    };
    let det = DetectorConfig::default();
    // Include the first, last and both valued bits using a nonzero placement.
    let ordinal = 4;
    let c = codeword_of_marker(ordinal).unwrap();
    let word = encode_marker_id(c).unwrap();
    let sync = SyncCandidate {
        overlap: false,
        peak_index: 0,
        time: 0.0,
        q: 1.0,
        amplitude: 0.25,
        level_dbfs: -12.0,
        polarity: 1,
    };
    for erased in [vec![], vec![0], vec![15], vec![5], vec![0, 15]] {
        let mut x: Vec<_> = marker_template(&cfg, ordinal)
            .unwrap()
            .iter()
            .map(|v| (v * 0.25) as f32)
            .collect();
        let mut mask = 0;
        for &bit in &erased {
            mask |= 1 << bit;
            let start = cfg.sync_len + cfg.gap_len + (15 - bit) * (cfg.bit_len + cfg.gap_len);
            x[start..start + cfg.bit_len].fill(0.0);
        }
        let r = read_id(&cfg, &det, &x, 0.001, &sync);
        assert_eq!(r.erasure_mask, mask);
        assert_eq!(r.bits, word & !mask);
        assert_eq!(r.word, if mask == 0 { Some(word) } else { None });
        assert_eq!(r.segments_present, 17 - erased.len() as u8);
        assert!(r.min_margin_db.is_some());
        assert_eq!(
            r.outcome,
            match erased.len() {
                0 => IdOutcome::Exact(c),
                1 => IdOutcome::ErasureCorrected {
                    index: c,
                    erased_bit: erased[0] as u8
                },
                _ => IdOutcome::Erased { count: 2 },
            }
        );
    }
    let r = read_id(&cfg, &det, &[], f64::NAN, &sync);
    assert_eq!((r.bits, r.erasure_mask), (0, u16::MAX));
    assert_eq!(r.outcome, IdOutcome::Erased { count: 16 });
}

#[test]
fn a0_decoded_id_and_alignment_use_ordinals() {
    for c in 0..CODE_SPACE as u16 {
        for outcome in [
            IdOutcome::Exact(c),
            IdOutcome::Corrected {
                index: c,
                flipped_bit: 0,
            },
            IdOutcome::ErasureCorrected {
                index: c,
                erased_bit: 0,
            },
        ] {
            assert_eq!(decoded_id(outcome), marker_of_codeword(c));
        }
    }
    assert_eq!(decoded_id(IdOutcome::Erased { count: 1 }), None);
    assert_eq!(decoded_id(IdOutcome::Uncorrectable), None);
    let cfg = AnalysisConfig {
        chirp: ChirpConfig {
            marker_count: 12,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut detections: Vec<_> = marker_schedule(&cfg.chirp)
        .unwrap()
        .into_iter()
        .map(|(i, t, word)| Detection {
            sync: SyncCandidate {
                overlap: false,
                peak_index: t,
                time: t as f64 + 100.0,
                q: 1.0,
                amplitude: 0.25,
                level_dbfs: -12.0,
                polarity: 1,
            },
            id: IdReading {
                outcome: IdOutcome::Exact(codeword_of_marker(i).unwrap()),
                word: Some(word),
                bits: word,
                erasure_mask: 0,
                segments_present: 17,
                min_margin_db: Some(20.0),
            },
        })
        .collect();
    let absent = (0..CODE_SPACE as u16)
        .find(|c| marker_of_codeword(*c).is_none())
        .unwrap();
    let mut outside = detections[0].clone();
    outside.id.outcome = IdOutcome::Exact(absent);
    outside.sync.time += 1000000.0;
    detections.push(outside);
    let alignment = align_left(&cfg, &detections).unwrap();
    assert_eq!(alignment.anchors, 12);
    assert_eq!(alignment.offset, 100.0);
    assert_eq!(alignment.ratio, 1.0);
}

fn masks(n: u32) -> Vec<u16> {
    (0..=u16::MAX).filter(|m| m.count_ones() == n).collect()
}

#[test]
#[ignore = "6c-full exhaustive disjoint erasure/error positions; run this test in release"]
fn a3_6c_full() {
    let words: Vec<_> = (0..CODE_SPACE as u16)
        .map(|i| encode_marker_id(i).unwrap())
        .collect();
    let errors: Vec<_> = (0..=4).map(masks).collect();
    let mut grand_total = 0u64;
    for e in 0..=3 {
        let mut totals = [0u64; 5];
        let mut wrong = [0u64; 5];
        let mut exact_wrong = [0u64; 5];
        for erasure in masks(e) {
            // Evaluate the production decoder on every possible observation for this mask.
            // The lookup only avoids re-decoding identical (bits, mask) observations across
            // source codewords. Every source x disjoint-position input is still counted.
            let lookup: Vec<_> = (0..=u16::MAX)
                .map(|bits| decode_id(bits & !erasure, erasure))
                .collect();
            for (t, flips) in errors.iter().enumerate() {
                for &flip in flips.iter().filter(|&&f| f & erasure == 0) {
                    for (c, &word) in words.iter().enumerate() {
                        let bits = (word ^ flip) & !erasure;
                        let outcome = lookup[usize::from(bits)];
                        let bad = index(outcome).is_some_and(|i| usize::from(i) != c);
                        totals[t] += 1;
                        wrong[t] += u64::from(bad);
                        exact_wrong[t] += u64::from(bad && matches!(outcome, IdOutcome::Exact(_)));
                        if e >= 2 {
                            assert_eq!(outcome, IdOutcome::Erased { count: e as u8 });
                        }
                    }
                }
            }
        }
        for t in 0..=4 {
            let expected = CODE_SPACE as u64 * choose(16, e) * choose(16 - e, t as u32);
            assert_eq!(totals[t], expected);
            if e >= 2 || t < 3 {
                assert_eq!(wrong[t], 0, "e={e} t={t}");
            }
            if t < 4 {
                assert_eq!(exact_wrong[t], 0, "Exact e={e} t={t}");
            }
            println!(
                "6c-full e={e} t={t}: wrong={}/{} ({:.9}%), Exact_wrong={}",
                wrong[t],
                totals[t],
                100.0 * wrong[t] as f64 / totals[t] as f64,
                exact_wrong[t]
            );
            grand_total += totals[t];
        }
    }
    println!("6c-full total={grand_total}");
}
fn choose(n: u32, k: u32) -> u64 {
    (0..k).fold(1u64, |v, i| v * u64::from(n - i) / u64::from(i + 1))
}

#[test]
fn a0_window_distance_bounds_and_attainment() {
    // Triangle bound for every pair within six consecutive ordinals, in both directions.
    // To confirm another word: t >= distance - e - tau. This tests the code bound,
    // not A1 source-window construction (outside this implementation's scope).
    for (i, &a) in marker_placement().iter().enumerate() {
        for &b in marker_placement().iter().skip(i + 1).take(5) {
            let d = (encode_marker_id(a).unwrap() ^ encode_marker_id(b).unwrap()).count_ones();
            // Enumerate the entire confirmation ball of the other codeword.
            // These are all observations that can confirm that target under A3:
            // e=0: its exact word and 16 single flips; e=1: its 16 punctures.
            // Check both source/target directions for every local pair.
            for (source, target) in [(a, b), (b, a)] {
                let source_word = encode_marker_id(source).unwrap();
                let target_word = encode_marker_id(target).unwrap();
                assert_eq!(decode_id(target_word, 0), IdOutcome::Exact(target));
                assert!(nonerased_distance(source_word, 0, target_word) >= 8);
                for bit in 0..16 {
                    let mask = 1 << bit;
                    let received = target_word ^ mask;
                    assert_eq!(
                        decode_id(received, 0),
                        IdOutcome::Corrected {
                            index: target,
                            flipped_bit: bit
                        }
                    );
                    assert!(nonerased_distance(received, 0, source_word) >= 7);
                    let received = target_word & !mask;
                    assert_eq!(
                        decode_id(received, mask),
                        IdOutcome::ErasureCorrected {
                            index: target,
                            erased_bit: bit
                        }
                    );
                    assert!(nonerased_distance(received, mask, source_word) >= 7);
                }
            }
            for (e, tau, minimum) in [(0, 0, 8), (0, 1, 7), (1, 0, 7)] {
                assert!(d - e - tau >= minimum);
            }
        }
    }
    let a = encode_marker_id(codeword_of_marker(0).unwrap()).unwrap();
    let b_index = codeword_of_marker(1).unwrap();
    let b = encode_marker_id(b_index).unwrap();
    let diff = a ^ b;
    assert_eq!(diff.count_ones(), 8);
    let one = 1 << diff.trailing_zeros();
    assert_eq!(
        decode_id(b ^ one, 0),
        IdOutcome::Corrected {
            index: b_index,
            flipped_bit: diff.trailing_zeros() as u8
        }
    );
    assert_eq!(((b ^ one) ^ a).count_ones(), 7);
    assert_eq!(
        decode_id(b & !one, one),
        IdOutcome::ErasureCorrected {
            index: b_index,
            erased_bit: diff.trailing_zeros() as u8
        }
    );
    assert_eq!(nonerased_distance(a, one, b), 7);
    assert_eq!(decode_id(b, 0), IdOutcome::Exact(b_index));
    assert_eq!(nonerased_distance(a, 0, b), 8);
    println!(
        "6c-win code-distance bounds: Corrected/ErasureCorrected t>=7, Exact t>=8; boundaries attained by ordinals 0 -> 1"
    );
}
