use mdc_rig::*;
fn small() -> ChirpConfig {
    ChirpConfig {
        marker_count: 3,
        lead_silence: 13,
        tail_silence: 17,
        ..Default::default()
    }
}
fn u16_at(b: &[u8], n: usize) -> u16 {
    u16::from_le_bytes(b[n..n + 2].try_into().unwrap())
}
fn u32_at(b: &[u8], n: usize) -> u32 {
    u32::from_le_bytes(b[n..n + 4].try_into().unwrap())
}
fn parse(b: &[u8]) -> Vec<i16> {
    assert_eq!(&b[..4], b"RIFF");
    assert_eq!(&b[8..16], b"WAVEfmt ");
    assert_eq!(&b[36..40], b"data");
    b[44..]
        .chunks_exact(2)
        .map(|p| i16::from_le_bytes(p.try_into().unwrap()))
        .collect()
}
#[test]
fn t1_header() {
    for channels in [1, 2] {
        let c = ChirpConfig {
            channels,
            ..small()
        };
        let mut b = Vec::new();
        write_wav(&c, &mut b).unwrap();
        assert_eq!(u32_at(&b, 4) as usize, b.len() - 8);
        assert_eq!(u32_at(&b, 16), 16);
        assert_eq!(u16_at(&b, 20), 1);
        assert_eq!(u16_at(&b, 22), channels);
        assert_eq!(u32_at(&b, 24), 48000);
        assert_eq!(u32_at(&b, 28), 48000 * u32::from(channels) * 2);
        assert_eq!(u16_at(&b, 32), channels * 2);
        assert_eq!(u16_at(&b, 34), 16);
        assert_eq!(
            u32_at(&b, 40) as usize,
            c.total_len().unwrap() * usize::from(channels) * 2
        );
        assert_eq!(
            b.len(),
            44 + c.total_len().unwrap() * usize::from(channels) * 2
        );
    }
}
#[test]
fn t2_roundtrip() {
    for channels in [1, 2] {
        let c = ChirpConfig {
            channels,
            ..small()
        };
        let mut b = Vec::new();
        write_wav(&c, &mut b).unwrap();
        let decoded = parse(&b);
        let mono = generate_samples(&c).unwrap();
        for (frame, s) in decoded.chunks_exact(usize::from(channels)).zip(mono) {
            assert!(frame.iter().all(|v| *v == s));
        }
    }
}
#[test]
fn t3_schedule_silence_sync() {
    let c = small();
    let mono = generate_samples(&c).unwrap();
    let schedule = marker_schedule(&c).unwrap();
    assert_eq!(
        mono.len(),
        c.lead_silence + (c.marker_count - 1) * c.interval_len + c.marker_len() + c.tail_silence
    );
    let mut active = vec![false; mono.len()];
    let mut first_sync = None;
    for (k, (id, start, word)) in schedule.into_iter().enumerate() {
        assert_eq!(usize::from(id), k);
        assert_eq!(start, c.lead_silence + k * c.interval_len);
        assert_eq!(Some(word), encode_marker_id(id));
        assert!(mono[start..start + c.marker_len()].iter().any(|s| *s != 0));
        active[start..start + c.marker_len()].fill(true);
        let sync = mono[start..start + c.sync_len].to_vec();
        if let Some(ref first) = first_sync {
            assert_eq!(&sync, first);
        } else {
            first_sync = Some(sync);
        }
    }
    for (s, on) in mono.iter().zip(active) {
        if !on {
            assert_eq!(*s, 0);
        }
    }
}
#[test]
fn t4_peak() {
    for peak_dbfs in [-60.0, -12.0, -1.0] {
        let c = ChirpConfig {
            peak_dbfs,
            ..small()
        };
        let amp = 10f64.powf(peak_dbfs / 20.0) * 32767.0;
        let peak = generate_samples(&c)
            .unwrap()
            .into_iter()
            .map(|s| i32::from(s).abs())
            .max()
            .unwrap() as f64;
        assert!(peak <= amp.round());
        assert!(peak >= 0.9 * amp);
    }
}
// One-sided DFT, with the usual factor of two except DC/Nyquist.
fn outside_db(x: &[f64], low: f64, high: f64) -> f64 {
    let mut total = 0.0;
    let mut outside = 0.0;
    for k in 0..=x.len() / 2 {
        let mut re = 0.0;
        let mut im = 0.0;
        for (n, s) in x.iter().enumerate() {
            let p = std::f64::consts::TAU * k as f64 * n as f64 / x.len() as f64;
            re += s * p.cos();
            im -= s * p.sin();
        }
        let weight = if k == 0 || k * 2 == x.len() { 1.0 } else { 2.0 };
        let energy = weight * (re * re + im * im);
        let f = k as f64 * 48000.0 / x.len() as f64;
        total += energy;
        if f < low || f > high {
            outside += energy;
        }
    }
    10.0 * (outside / total).log10()
}
#[test]
fn t5_band() {
    let c = ChirpConfig::default();
    for (name, x) in [
        ("sync", sync_template(&c)),
        ("bit0", bit_template(&c, false)),
        ("bit1", bit_template(&c, true)),
    ] {
        let db = outside_db(&x, c.f_low_hz - 500.0, c.f_high_hz + 500.0);
        println!("T5 {name} outside energy: {db:.9} dB");
        assert!(db < -30.0, "{name}: {db}");
    }
}
// Normalize with full template energies and zero-pad outside each template.
fn correlation(a: &[f64], b: &[f64], lag: isize) -> f64 {
    let dot: f64 = a
        .iter()
        .enumerate()
        .filter_map(|(i, x)| {
            let j = i as isize + lag;
            usize::try_from(j)
                .ok()
                .and_then(|j| b.get(j))
                .map(|y| x * y)
        })
        .sum();
    dot / (a.iter().map(|x| x * x).sum::<f64>() * b.iter().map(|x| x * x).sum::<f64>()).sqrt()
}
#[test]
fn t6_direction() {
    let c = ChirpConfig::default();
    let a = bit_template(&c, false);
    let b = bit_template(&c, true);
    let lags = -(a.len() as isize - 1)..=(a.len() as isize - 1);
    let peak = lags
        .clone()
        .map(|l| correlation(&a, &b, l).abs())
        .fold(0.0, f64::max);
    println!("T6 max absolute cross correlation: {peak:.12}");
    assert!(peak < 0.3);
    for x in [&a, &b] {
        assert!((correlation(x, x, 0) - 1.0).abs() < 1e-12);
        let peak = lags
            .clone()
            .map(|l| correlation(x, x, l).abs())
            .fold(0.0, f64::max);
        assert!((peak - 1.0).abs() < 1e-12);
    }
}
#[test]
fn t7_codes() {
    assert_eq!(MAX_MARKERS, 2048);
    let words: Vec<_> = (0..2048)
        .map(|id| {
            let w = encode_marker_id(id).unwrap();
            assert_eq!(decode_marker_id(w), MarkerIdDecode::Exact(id));
            for bit in 0..16 {
                assert_eq!(
                    decode_marker_id(w ^ (1 << bit)),
                    MarkerIdDecode::Corrected {
                        index: id,
                        flipped_bit: bit
                    }
                );
                for second in bit + 1..16 {
                    assert_eq!(
                        decode_marker_id(w ^ (1 << bit) ^ (1 << second)),
                        MarkerIdDecode::Uncorrectable
                    );
                }
            }
            w
        })
        .collect();
    for id in 2048..=u16::MAX {
        assert_eq!(encode_marker_id(id), None);
    }
    let mut minimum_distance = 16;
    for (i, a) in words.iter().enumerate() {
        for b in &words[i + 1..] {
            let distance = (a ^ b).count_ones();
            assert!(distance >= 4);
            minimum_distance = minimum_distance.min(distance);
        }
    }
    println!("T7 minimum Hamming distance: {minimum_distance}");
    assert_eq!(minimum_distance, 4);
    let (mut exact, mut corrected, mut uncorrectable) = (0, 0, 0);
    for word in 0..=u16::MAX {
        match decode_marker_id(word) {
            MarkerIdDecode::Exact(_) => exact += 1,
            MarkerIdDecode::Corrected { .. } => corrected += 1,
            MarkerIdDecode::Uncorrectable => uncorrectable += 1,
        }
    }
    println!("T7e Exact: {exact}, Corrected: {corrected}, Uncorrectable: {uncorrectable}");
    assert_eq!((exact, corrected, uncorrectable), (2048, 32768, 30720));
}
#[test]
fn t8_unique() {
    let c = small();
    let zero = encode_marker_id(0).unwrap();
    let distance_four_id = (3..2047)
        .find(|id| (encode_marker_id(*id).unwrap() ^ zero).count_ones() == 4)
        .unwrap();
    let ids = [0, 1, 2, 2047, distance_four_id];
    println!("T8 distance-four pair: 0 vs {distance_four_id}");
    let templates: Vec<_> = ids
        .iter()
        .map(|i| marker_template(&c, *i).unwrap())
        .collect();
    let start = c.sync_len + c.gap_len;
    for (i, a) in templates.iter().enumerate() {
        let self_r = correlation(&a[start..], &a[start..], 0);
        println!("T8 {} self ID correlation: {self_r:.12}", ids[i]);
        assert!((self_r - 1.0).abs() < 1e-12);
        for (j, b) in templates.iter().enumerate().skip(i + 1) {
            let r = correlation(&a[start..], &b[start..], 0);
            println!("T8 {} vs {} ID correlation: {r:.12}", ids[i], ids[j]);
            assert!(r < 0.8);
        }
    }
}
#[test]
fn t9_invalid() {
    let mut cases = Vec::new();
    for f in [
        f64::NAN,
        f64::INFINITY,
        f64::NEG_INFINITY,
        0.0,
        -1.0,
        4000.0,
        5000.0,
    ] {
        cases.push(ChirpConfig {
            f_low_hz: f,
            ..small()
        });
    }
    for f in [
        f64::NAN,
        f64::INFINITY,
        f64::NEG_INFINITY,
        500.0,
        499.0,
        21600.1,
    ] {
        cases.push(ChirpConfig {
            f_high_hz: f,
            ..small()
        });
    }
    for taper_fraction in [f64::NAN, f64::INFINITY, -0.01, 0.51] {
        cases.push(ChirpConfig {
            taper_fraction,
            ..small()
        });
    }
    for peak_dbfs in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY, -60.1, -0.9] {
        cases.push(ChirpConfig {
            peak_dbfs,
            ..small()
        });
    }
    for marker_count in [0, 2049, usize::MAX] {
        cases.push(ChirpConfig {
            marker_count,
            ..small()
        });
    }
    for channels in [0, 3, u16::MAX] {
        cases.push(ChirpConfig {
            channels,
            ..small()
        });
    }
    for field in 0..6 {
        let mut c = small();
        match field {
            0 => c.sync_len = 0,
            1 => c.bit_len = 0,
            2 => c.gap_len = 0,
            3 => c.interval_len = 0,
            4 => c.lead_silence = 0,
            _ => c.tail_silence = 0,
        };
        cases.push(c);
    }
    cases.push(ChirpConfig {
        interval_len: 8831,
        ..small()
    });
    for field in 0..6 {
        let mut c = small();
        match field {
            0 => c.sync_len = usize::MAX,
            1 => c.bit_len = usize::MAX,
            2 => c.gap_len = usize::MAX,
            3 => c.interval_len = usize::MAX,
            4 => c.lead_silence = usize::MAX,
            _ => c.tail_silence = usize::MAX,
        };
        cases.push(c);
    }
    cases.push(ChirpConfig {
        tail_silence: u32::MAX as usize,
        ..small()
    });
    // data itself fits u32, but the RIFF size (data + 36) does not.
    let mut c = ChirpConfig {
        marker_count: 1,
        lead_silence: 1,
        ..small()
    };
    c.tail_silence = (u32::MAX as usize / 2) - c.marker_len() - 1;
    cases.push(c);
    for c in cases {
        assert!(
            std::panic::catch_unwind(|| c.validate()).unwrap().is_err(),
            "{c:?}"
        );
        assert!(generate_samples(&c).is_err());
        assert!(marker_schedule(&c).is_err());
        assert!(marker_template(&c, 0).is_err());
        assert!(write_wav(&c, Vec::new()).is_err());
    }
    assert!(marker_template(&small(), 2048).is_err());
    for taper_fraction in [0.0, 0.5] {
        ChirpConfig {
            taper_fraction,
            ..small()
        }
        .validate()
        .unwrap();
    }
    ChirpConfig {
        f_high_hz: 21600.0,
        marker_count: 2048,
        ..small()
    }
    .validate()
    .unwrap();
}
#[test]
fn t10_deterministic() {
    let c = small();
    let mut a = Vec::new();
    let mut b = Vec::new();
    write_wav(&c, &mut a).unwrap();
    write_wav(&c, &mut b).unwrap();
    assert_eq!(a, b);
}
#[test]
fn t11_defaults() {
    let c = ChirpConfig::default();
    c.validate().unwrap();
    assert_eq!(c.marker_len(), 8736);
}
#[test]
fn io_error_is_wrapped() {
    struct Broken;
    impl std::io::Write for Broken {
        fn write(&mut self, _: &[u8]) -> std::io::Result<usize> {
            Err(std::io::Error::other("broken"))
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
    assert!(matches!(write_wav(&small(), Broken), Err(RigError::Io(_))));
}
