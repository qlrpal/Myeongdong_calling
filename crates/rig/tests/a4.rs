use mdc_rig::*;

fn random(seed: &mut u64) -> f64 {
    *seed = seed
        .wrapping_mul(6364136223846793005)
        .wrapping_add(1442695040888963407);
    (*seed >> 11) as f64 / (1u64 << 53) as f64
}

#[test]
fn paired_witnesses() {
    // Flattened [template][start]: same template/different start and vice versa.
    for observations in [
        [(0.8, 0.1), (0.1, 0.8), (0.0, 0.0), (0.0, 0.0)],
        [(0.8, 0.1), (0.0, 0.0), (0.1, 0.8), (0.0, 0.0)],
        [(0.8, 0.1), (0.0, 0.0), (0.0, 0.0), (0.1, 0.8)],
    ] {
        assert!(bit_gate_exists(&observations, 0.5, 0.5, false));
        assert!(!bit_gate_exists(&observations, 0.5, 0.5, true));
    }
    for joint in [false, true] {
        assert!(bit_gate_exists(&[(0.5, 0.5)], 0.5, 0.5, joint));
        assert!(!bit_gate_exists(&[], 0.5, 0.5, joint));
        assert!(!bit_gate_exists(&[(0.49, 0.49)], 0.5, 0.5, joint));
    }
}

#[test]
fn randomized_monotonicity() {
    let mut seed = 0xa4;
    for _ in 0..10_000 {
        let observations: Vec<_> = (0..10)
            .map(|_| (random(&mut seed), random(&mut seed)))
            .collect();
        let q = random(&mut seed);
        let gate = random(&mut seed);
        let legacy =
            observations.iter().any(|p| p.0 >= q) && observations.iter().any(|p| p.1 >= gate);
        assert_eq!(bit_gate_exists(&observations, q, gate, false), legacy);
        assert!(!bit_gate_exists(&observations, q, gate, true) || legacy);
    }
}

fn compare(c: &ChirpConfig, x: &[f32], syncs: &[SyncCandidate], clean: bool) {
    let joint = DetectorConfig::default();
    let split = DetectorConfig {
        joint_bit_gate: false,
        ..Default::default()
    };
    for (k, s) in syncs.iter().enumerate() {
        for gate in [amplitude_gate(&joint, x), 0.01, 0.1, 0.25] {
            let old = read_id(c, &split, x, gate, s);
            let new = read_id(c, &joint, x, gate, s);
            assert_eq!(new.erasure_mask | old.erasure_mask, new.erasure_mask);
            assert_eq!((new.bits ^ old.bits) & !new.erasure_mask, 0);
            assert!(new.segments_present <= old.segments_present);
            if clean {
                assert_eq!(new.erasure_mask, old.erasure_mask);
                assert_eq!(new.bits, old.bits);
                assert_eq!(new.outcome, old.outcome);
                if gate < 0.25 {
                    assert_eq!(
                        new.outcome,
                        IdOutcome::Exact(codeword_of_marker(k as u16).unwrap())
                    );
                }
            }
        }
    }
}

#[test]
fn wav_clean_noise_and_crosstalk() {
    let c = ChirpConfig {
        channels: 2,
        marker_count: 3,
        lead_silence: 4800,
        tail_silence: 4800,
        ..Default::default()
    };
    let mut wav = Vec::new();
    write_wav(&c, &mut wav).unwrap();
    let recording = read_stereo_wav(wav.as_slice()).unwrap();
    let syncs = detect_sync(&c, &Default::default(), &recording.left).unwrap();
    assert_eq!(syncs.len(), c.marker_count);
    compare(&c, &recording.left, &syncs, true);
    let mut seed = 0xa4002;
    for (noise, gain, shift) in [
        (0.001, 0.1, 2),
        (0.01, -0.5, 8),
        (0.05, 0.8, 240),
        (0.1, 1.0, 480),
    ] {
        // Mix in the PCM16 data chunk, then decode the WAV again.
        let mut mixed = wav.clone();
        for (n, frame) in mixed[44..].chunks_exact_mut(4).enumerate() {
            let echo = n
                .checked_sub(shift)
                .and_then(|i| recording.left.get(i))
                .copied()
                .unwrap_or(0.0);
            let value =
                recording.left[n] + gain * echo + (noise * (random(&mut seed) * 2.0 - 1.0)) as f32;
            let pcm = (value.clamp(-1.0, 0.9999) * 32768.0).round() as i16;
            frame[..2].copy_from_slice(&pcm.to_le_bytes());
        }
        let input = read_stereo_wav(mixed.as_slice()).unwrap();
        compare(&c, &input.left, &syncs, false);
        let candidates = detect_sync(&c, &Default::default(), &input.left).unwrap();
        assert!(!candidates.is_empty());
        compare(&c, &input.left, &candidates, false);
    }
}

#[test]
fn identity_records_gate_mode() {
    let mut c = mdc_rig::analyze::AnalysisConfig::default();
    assert!(c.identity().contains("joint_bit_gate: true"));
    let identity = c.identity();
    c.detector.joint_bit_gate = false;
    assert!(c.identity().contains("joint_bit_gate: false"));
    assert_ne!(identity, c.identity());
}
