use mdc_rig::*;
fn config(n: usize) -> ChirpConfig {
    ChirpConfig {
        marker_count: n,
        lead_silence: 480,
        tail_silence: 480,
        ..Default::default()
    }
}
fn samples(c: &ChirpConfig) -> Vec<f32> {
    generate_samples(c)
        .unwrap()
        .into_iter()
        .map(|v| f32::from(v) / 32768.0)
        .collect()
}
fn check(c: &ChirpConfig, x: &[f32], starts: &[f64], tol: f64, ids: bool) -> f64 {
    let d = DetectorConfig::default();
    let found = detect_sync(c, &d, x).unwrap();
    assert_eq!(
        found.len(),
        starts.len(),
        "candidates: {:?}",
        found
            .iter()
            .map(|p| (p.time, p.q, p.level_dbfs))
            .collect::<Vec<_>>()
    );
    let gate = amplitude_gate(&d, x);
    let mut max = 0.0f64;
    for (k, (p, t)) in found.iter().zip(starts).enumerate() {
        max = max.max((p.time - t).abs());
        if ids {
            assert_eq!(
                read_id(c, &d, x, gate, p).outcome,
                IdOutcome::Exact(codeword_of_marker(k as u16).unwrap())
            );
        }
        assert!(p.q.is_finite() && p.amplitude.is_finite() && p.level_dbfs.is_finite());
    }
    println!("maximum timing error {max:.9} samples");
    assert!(max <= tol, "maximum {max} > {tol}");
    max
}
fn starts(c: &ChirpConfig) -> Vec<f64> {
    marker_schedule(c)
        .unwrap()
        .iter()
        .map(|(_, t, _)| *t as f64)
        .collect()
}
// Independent 128-tap Blackman-windowed sinc. y(n)=x((n-delay)/ratio).
// Integer positions are exact, and zero extension covers the recording boundaries.
fn resample(x: &[f32], delay: f64, ratio: f64) -> Vec<f32> {
    let len = ((x.len() as f64 * ratio + delay).ceil() as usize) + 128;
    let mut y = vec![0.0; len];
    for (n, out) in y.iter_mut().enumerate() {
        let pos = (n as f64 - delay) / ratio;
        let base = pos.floor() as isize;
        let mut sum = 0.0;
        let mut norm = 0.0;
        for tap in -63isize..=64 {
            let index = base + tap;
            let d = pos - index as f64;
            let window = 0.42
                + 0.5 * (std::f64::consts::PI * d / 64.0).cos()
                + 0.08 * (std::f64::consts::TAU * d / 64.0).cos();
            let sinc = if d.abs() < 1e-12 {
                1.0
            } else {
                (std::f64::consts::PI * d).sin() / (std::f64::consts::PI * d)
            };
            let w = window * sinc;
            norm += w;
            if let Ok(i) = usize::try_from(index)
                && let Some(v) = x.get(i)
            {
                sum += w * f64::from(*v);
            }
        }
        *out = (sum / norm) as f32;
    }
    y
}
fn noise(x: &mut [f32], rms: f64, seed: &mut u64) {
    for v in x {
        *seed = seed
            .wrapping_mul(6364136223846793005)
            .wrapping_add(1442695040888963407);
        let u = ((*seed >> 11) as f64) / (1u64 << 53) as f64 * 2.0 - 1.0;
        *v += (u * 3.0f64.sqrt() * rms) as f32;
    }
}
#[test]
// 합성 신호 조건의 알고리즘 정확도 (잡음 없음, 단일 경로).
fn d1_default() {
    let c = config(3);
    let x = samples(&c);
    check(&c, &x, &starts(&c), 0.05, true);
    for p in detect_sync(&c, &Default::default(), &x).unwrap() {
        assert!(p.q >= 0.99);
        assert!((p.level_dbfs + 12.0).abs() <= 0.5);
        println!("D1 q={} level={}", p.q, p.level_dbfs);
    }
}
#[test]
fn d2_levels() {
    for level in [-12.0, -37.0, -45.0] {
        let c = ChirpConfig {
            peak_dbfs: level,
            ..config(1)
        };
        let x = samples(&c);
        let p = detect_sync(&c, &Default::default(), &x).unwrap();
        assert_eq!(p.len(), 1);
        println!("D2 requested={level} measured={}", p[0].level_dbfs);
        assert!((p[0].level_dbfs - level).abs() <= 0.5);
    }
}
#[test]
// 합성 신호 조건의 알고리즘 정확도 (잡음 없음, 단일 경로).
fn d3_fractional() {
    let c = config(1);
    let x = samples(&c);
    let mut max = 0.0f64;
    for integer in [1, 4800, 12345] {
        for tenth in 0..=9 {
            let delay = integer as f64 + f64::from(tenth) / 10.0;
            let y = resample(&x, delay, 1.0);
            max = max.max(check(&c, &y, &[c.lead_silence as f64 + delay], 0.05, true));
        }
    }
    println!("D3 maximum={max:.9}");
}
fn noisy(n: usize) {
    let c = config(n);
    let mut x = samples(&c);
    let marker = marker_template(&c, 0).unwrap();
    let active: Vec<_> = marker.iter().filter(|v| v.abs() > 1e-12).collect();
    let rms = (active.iter().map(|v| **v * **v).sum::<f64>() / active.len() as f64).sqrt()
        * 10f64.powf(-12.0 / 20.0);
    noise(&mut x, rms / 10f64.powf(10.0 / 20.0), &mut 7123);
    let p = detect_sync(&c, &Default::default(), &x).unwrap();
    assert_eq!(p.len(), n, "miss or false alarm");
    let errors: Vec<_> = p.iter().zip(starts(&c)).map(|(p, t)| p.time - t).collect();
    let bias = errors.iter().sum::<f64>() / n as f64;
    let rmse = (errors.iter().map(|e| e * e).sum::<f64>() / n as f64).sqrt();
    let mut abs: Vec<_> = errors.iter().map(|e| e.abs()).collect();
    abs.sort_by(f64::total_cmp);
    println!(
        "D4 n={n} bias={bias:.9} RMSE={rmse:.9} P95={:.9} P99={:.9} max={:.9}",
        abs[(n * 95).div_ceil(100) - 1],
        abs[(n * 99).div_ceil(100) - 1],
        abs[n - 1]
    );
    assert!(bias.abs() <= 0.05);
    assert!(rmse <= 0.25);
}
#[test]
fn d4_noise_small() {
    noisy(8);
}
#[test]
#[ignore = "full 200-marker SNR acceptance; run in release"]
fn d4_noise_200() {
    noisy(200);
}
// Inspect every valid delay, including points rejected by either detector gate.
// Chunk overlap preserves full sync windows without allocating a recording-sized FFT result.
fn sync_qualities(c: &ChirpConfig, x: &[f32], include: impl Fn(usize, f64) -> bool) -> f64 {
    let t: Vec<_> = sync_template(c)
        .into_iter()
        .zip(sync_template_quadrature(c))
        .map(|(re, im)| Complex { re, im })
        .collect();
    let ts = t.iter().map(|z| z.re * z.re).sum::<f64>();
    let mut max = 0.0f64;
    let count = x.len().saturating_sub(t.len()) + 1;
    for base in (0..count).step_by(65536) {
        let end = (base + 65536).min(count);
        let chunk = &x[base..end + t.len() - 1];
        let corr = correlate_complex(chunk, &t).unwrap();
        let mut energy = chunk[..t.len()]
            .iter()
            .map(|v| f64::from(*v).powi(2))
            .sum::<f64>();
        for (offset, z) in corr.iter().enumerate() {
            if offset > 0 {
                energy = (energy - f64::from(chunk[offset - 1]).powi(2)
                    + f64::from(chunk[offset + t.len() - 1]).powi(2))
                .max(0.0);
            }
            let q = if ts <= 1e-20 || energy <= 1e-20 {
                0.0
            } else {
                z.re.hypot(z.im) / (ts * energy).sqrt()
            };
            assert!(q.is_finite());
            if include(base + offset, q) {
                max = max.max(q);
            }
        }
    }
    max
}
fn margin(c: &ChirpConfig, x: &[f32], syncs: &[f64], label: &str) {
    let radius = DetectorConfig::default().dedup_radius as f64;
    let max = sync_qualities(c, x, |i, _| {
        let time = i as f64;
        let next = syncs.partition_point(|t| *t < time - radius);
        syncs.get(next).is_none_or(|t| *t > time + radius)
    });
    println!("{label} non-sync max q={max:.9}");
}
#[test]
fn d5_no_sync() {
    let c = config(1);
    for mode in 0..3 {
        let mut x = vec![0.0; 48000];
        if mode == 1 {
            noise(&mut x, 0.001, &mut 13);
        }
        if mode == 2 {
            for (i, chunk) in x.chunks_mut(c.bit_len + c.gap_len).enumerate() {
                for (v, t) in chunk.iter_mut().zip(bit_template(&c, i % 2 == 0)) {
                    *v = (t * 0.25) as f32;
                }
            }
        }
        assert!(detect_sync(&c, &Default::default(), &x).unwrap().is_empty());
        margin(
            &c,
            &x,
            &[],
            ["D5 silence", "D5 -60dBFS noise", "D5 bits only"][mode],
        );
    }
    assert_eq!(noise_rms(&[]), 0.0);
    assert_eq!(noise_rms(&[0.0; 479]), 0.0);
}
fn all_ids(n: usize) {
    let c = config(n);
    let x = samples(&c);
    check(&c, &x, &starts(&c), 0.05, true);
    margin(&c, &x, &starts(&c), &format!("D6 n={n}"));
}
#[test]
fn d6_ids_small() {
    all_ids(16);
}
#[test]
#[ignore = "all 1704 marker IDs; run in release"]
fn d6_all_ids() {
    all_ids(MAX_MARKERS);
}
#[test]
fn d7_paths() {
    // Keep more than 10% quiet RMS blocks so paths do not set the noise gate.
    let c = ChirpConfig {
        lead_silence: 4800,
        tail_silence: 4800,
        ..config(1)
    };
    let x = samples(&c);
    let mut failures = Vec::new();
    for ms in [0.5, 2.0, 10.0, 20.0, 21.0] {
        let shift = (ms * 48.0) as usize;
        let y = multipath(&x, shift, -6.0);
        let det = DetectorConfig::default();
        let p = detect_sync(&c, &det, &y).unwrap();
        println!(
            "D7 {ms}ms {:?}",
            p.iter().map(|p| (p.time, p.q)).collect::<Vec<_>>()
        );
        if ms == 0.5 {
            assert_eq!(p.len(), 1);
        } else {
            if p.len() != 2 {
                failures.push(format!("{ms}ms count={}", p.len()));
            }
            for (p, t) in p.iter().zip([c.lead_silence, c.lead_silence + shift]) {
                let error = (p.time - t as f64).abs();
                println!("D7 {ms}ms target={t} error={error:.9} samples");
                assert!(p.overlap);
                if error > 2.0 {
                    failures.push(format!("{ms}ms target={t} error={error}"));
                }
            }
            if let Some(weak) = p.get(1) {
                println!(
                    "D7 {ms}ms weak ID {:?}",
                    read_id(&c, &det, &y, amplitude_gate(&det, &y), weak).outcome
                );
            }
        }
    }
    assert!(failures.is_empty(), "D7 failures: {failures:?}");
}
fn multipath(x: &[f32], shift: usize, db: f32) -> Vec<f32> {
    let mut y = vec![0.0; x.len() + shift];
    for (n, v) in x.iter().enumerate() {
        y[n] += *v;
        y[n + shift] += *v * 10f32.powf(db / 20.0);
    }
    y
}
#[test]
fn d7b_path_limit() {
    // Keep more than 10% quiet RMS blocks so paths do not set the noise gate.
    let c = ChirpConfig {
        lead_silence: 4800,
        tail_silence: 4800,
        ..config(1)
    };
    let x = samples(&c);
    let det = DetectorConfig::default();
    let mut limit = None;
    let mut false_candidates = Vec::new();
    for db in [-6.0, -8.0, -10.0, -12.0, -15.0, -20.0] {
        let y = multipath(&x, 480, db);
        let p = detect_sync(&c, &det, &y).unwrap();
        let weak_time = (c.lead_silence + 480) as f64;
        let weak = p
            .iter()
            .find(|p| (p.time - weak_time).abs() <= det.dedup_radius as f64);
        let q = sync_qualities(&c, &y, |i, _| i == c.lead_silence + 480);
        println!(
            "D7b {db}dB detected={} q={q:.9} gate={} candidates={p:?}",
            weak.is_some(),
            amplitude_gate(&det, &y)
        );
        if weak.is_some() {
            limit = Some(db);
        }
        for candidate in &p {
            if ![c.lead_silence as f64, weak_time]
                .iter()
                .any(|t| (candidate.time - t).abs() <= det.dedup_radius as f64)
            {
                false_candidates.push((db, candidate.time));
            }
        }
    }
    println!("D7b weakest detected level={limit:?} dB (path retained within dedup radius)");
    assert!(
        false_candidates.is_empty(),
        "D7b candidates at unrelated times: {false_candidates:?}"
    );
}

#[test]
// 합성 신호 조건의 알고리즘 정확도 (잡음 없음, 단일 경로).
fn d8_inverted() {
    let c = config(3);
    let x: Vec<_> = samples(&c).into_iter().map(|v| -v).collect();
    check(&c, &x, &starts(&c), 0.05, true);
    assert!(
        detect_sync(&c, &Default::default(), &x)
            .unwrap()
            .iter()
            .all(|p| p.polarity == -1)
    );
}
fn sync(c: &ChirpConfig, x: &[f32]) -> SyncCandidate {
    detect_sync(c, &Default::default(), x)
        .unwrap()
        .into_iter()
        .next()
        .unwrap()
}
fn replace(c: &ChirpConfig, x: &mut [f32], word_bits: &[usize], silent: bool) {
    let word = encode_marker_id(0).unwrap();
    for &bit in word_bits {
        let k = 15 - bit;
        let start = c.lead_silence + c.sync_len + c.gap_len + k * (c.bit_len + c.gap_len);
        let t = bit_template(c, word & (1 << bit) == 0);
        for (v, t) in x[start..start + c.bit_len].iter_mut().zip(t) {
            *v = if silent {
                0.0
            } else {
                (t * 10f64.powf(-12.0 / 20.0)) as f32
            };
        }
    }
}
#[test]
fn d9_secded_and_erasures() {
    let c = config(1);
    let det = Default::default();
    for bits in [&[5usize][..], &[5usize, 9][..], &[0usize, 1, 2][..]] {
        let mut x = samples(&c);
        replace(&c, &mut x, bits, false);
        let r = read_id(&c, &det, &x, amplitude_gate(&det, &x), &sync(&c, &x));
        let expected = match bits.len() {
            1 => IdOutcome::Corrected {
                index: 0,
                flipped_bit: 5,
            },
            2 => IdOutcome::Uncorrectable,
            _ => IdOutcome::Corrected {
                index: 1,
                flipped_bit: 15,
            },
        }; // SECDED cannot distinguish this three-bit error from a single parity-bit error.
        assert_eq!(r.outcome, expected);
    }
    for bits in [vec![5usize], (0..16).collect()] {
        let mut x = samples(&c);
        replace(&c, &mut x, &bits, true);
        let r = read_id(&c, &det, &x, amplitude_gate(&det, &x), &sync(&c, &x));
        assert_eq!(
            r.outcome,
            if bits.len() == 1 {
                IdOutcome::ErasureCorrected {
                    index: 0,
                    erased_bit: 5,
                }
            } else {
                IdOutcome::Erased {
                    count: bits.len() as u8,
                }
            }
        );
        assert_eq!(r.word, None);
        if bits.len() == 16 {
            assert_ne!(r.outcome, IdOutcome::Exact(0));
            assert_eq!(r.segments_present, 1);
        }
    }
    let mut x = samples(&c);
    let s = sync(&c, &x);
    x.truncate(
        c.lead_silence + c.sync_len + c.gap_len + 8 * (c.bit_len + c.gap_len) + c.bit_len / 2,
    );
    assert!(matches!(
        read_id(&c, &det, &x, amplitude_gate(&det, &x), &s).outcome,
        IdOutcome::Erased { .. }
    ));
}
#[test]
fn d10_ppm() {
    let c = config(20);
    let x = samples(&c);
    for ppm in [-100.0, 100.0] {
        let ratio = 1.0 + ppm / 1e6;
        let y = resample(&x, 0.0, ratio);
        let target: Vec<_> = starts(&c).into_iter().map(|t| t * ratio).collect();
        let max = check(&c, &y, &target, 0.25, true);
        println!("D10 ppm={ppm} max={max:.9}");
    }
}
#[test]
fn d11_clipped() {
    let c = config(3);
    let cap = (10f64.powf(-12.0 / 20.0) * 0.5) as f32;
    let x: Vec<_> = samples(&c)
        .into_iter()
        .map(|v| v.clamp(-cap, cap))
        .collect();
    let max = check(&c, &x, &starts(&c), 0.25, true);
    println!("D11 max={max:.9}");
}
#[test]
#[ignore = "600-marker stereo performance measurement; run in release"]
fn performance_600() {
    let c = ChirpConfig {
        channels: 2,
        ..Default::default()
    };
    let x = samples(&c);
    let right = x.clone();
    let det = DetectorConfig::default();
    let now = std::time::Instant::now();
    let candidates = detect_sync(&c, &det, &x).unwrap();
    let detection_elapsed = now.elapsed();
    let gate_start = std::time::Instant::now();
    let gate = amplitude_gate(&det, &x);
    let gate_elapsed = gate_start.elapsed();
    let read_start = std::time::Instant::now();
    let readings: Vec<_> = candidates
        .iter()
        .map(|s| read_id(&c, &det, &x, gate, s))
        .collect();
    let read_elapsed = read_start.elapsed();
    let elapsed = now.elapsed();
    println!(
        "PERFORMANCE detect={:.6}s gate={:.6}s total_read_id={:.6}s",
        detection_elapsed.as_secs_f64(),
        gate_elapsed.as_secs_f64(),
        read_elapsed.as_secs_f64()
    );
    assert_eq!(candidates.len(), 600);
    assert!(
        readings
            .iter()
            .enumerate()
            .all(|(i, r)| r.outcome == IdOutcome::Exact(codeword_of_marker(i as u16).unwrap()))
    );
    println!(
        "PERFORMANCE stereo frames={} duration={:.3}s candidates={} detect+read_id={:.6}s",
        right.len(),
        right.len() as f64 / 48000.0,
        candidates.len(),
        elapsed.as_secs_f64()
    );
    std::hint::black_box(right);
}
#[test]
fn invalid_and_boundaries() {
    let c = config(1);
    let x = samples(&c);
    let s = sync(&c, &x);
    for value in [f64::NAN, f64::INFINITY, -1.0, 1.1] {
        let d = DetectorConfig {
            q_min: value,
            ..Default::default()
        };
        assert!(d.validate().is_err());
        assert!(detect_sync(&c, &d, &x).is_err());
        assert_eq!(
            read_id(&c, &d, &x, amplitude_gate(&d, &x), &s).outcome,
            IdOutcome::Erased { count: 16 }
        );
    }
    for time in [f64::NAN, f64::INFINITY, -1.0, usize::MAX as f64] {
        let s = SyncCandidate { time, ..s.clone() };
        assert_eq!(
            read_id(
                &c,
                &Default::default(),
                &x,
                amplitude_gate(&Default::default(), &x),
                &s
            )
            .outcome,
            IdOutcome::Erased { count: 16 }
        );
    }
    let det = DetectorConfig::default();
    let gate = amplitude_gate(&det, &x);
    for gate in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY, -1.0] {
        assert_eq!(
            read_id(&c, &det, &x, gate, &s).outcome,
            IdOutcome::Erased { count: 16 }
        );
    }
    let mut outside = x.clone();
    outside[0] = f32::NAN;
    assert_eq!(
        read_id(&c, &det, &outside, gate, &s).outcome,
        IdOutcome::Exact(0)
    );
    let bit_start = c.lead_silence + c.sync_len + c.gap_len;
    for value in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
        let mut inside = x.clone();
        inside[bit_start] = value;
        let r = read_id(&c, &det, &inside, gate, &s);
        assert_eq!(
            r.outcome,
            IdOutcome::ErasureCorrected {
                index: 0,
                erased_bit: 15
            }
        );
        assert_eq!(r.erasure_mask, 1 << 15);
        assert_eq!(r.bits, 0);
        assert_eq!(r.word, None);
        assert!(r.min_margin_db.is_none_or(f64::is_finite));
    }
    let silent = vec![0.0; x.len()];
    assert_eq!(
        read_id(&c, &det, &silent, 0.0, &s).outcome,
        IdOutcome::Erased { count: 16 }
    );
    assert!(detect_sync(&c, &Default::default(), &[f32::NAN]).is_err());
}
