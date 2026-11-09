use mdc_rig::*;
fn random(state: &mut u64) -> f64 {
    *state = state.wrapping_mul(6364136223846793005).wrapping_add(1);
    ((*state >> 11) as f64) / (1u64 << 53) as f64 * 2.0 - 1.0
}
#[test]
fn f1_dft_and_roundtrip() {
    let mut seed = 13;
    for power in 0..=10 {
        let n = 1 << power;
        let original: Vec<_> = (0..n)
            .map(|_| Complex {
                re: random(&mut seed),
                im: random(&mut seed),
            })
            .collect();
        let mut b = original.clone();
        fft_in_place(&mut b, false).unwrap();
        let scale = original
            .iter()
            .map(|z| z.re.abs() + z.im.abs())
            .sum::<f64>()
            .max(1.0);
        for (k, z) in b.iter().enumerate() {
            let mut re = 0.0;
            let mut im = 0.0;
            for (j, v) in original.iter().enumerate() {
                let a = -std::f64::consts::TAU * (j * k) as f64 / n as f64;
                re += v.re * a.cos() - v.im * a.sin();
                im += v.re * a.sin() + v.im * a.cos();
            }
            assert!((z.re - re).hypot(z.im - im) <= 1e-9 * scale);
        }
        fft_in_place(&mut b, true).unwrap();
        for (a, b) in b.iter().zip(original) {
            assert!((a.re - b.re).hypot(a.im - b.im) < 1e-9);
        }
    }
    for n in [0, 3, 6, 1023] {
        assert!(fft_in_place(&mut vec![Complex::default(); n], false).is_err());
    }
}
#[test]
fn f2_overlap_save() {
    let mut seed = 714;
    for len in [1usize, 7, 64, 960, 2048, 3000] {
        let n = (len * 2).next_power_of_two().max(4096);
        let step = n - len + 1;
        let t: Vec<_> = (0..len)
            .map(|_| Complex {
                re: random(&mut seed),
                im: random(&mut seed),
            })
            .collect();
        for count in [1, step - 1, step, step + 1, step * 3 + 17] {
            let x: Vec<_> = (0..count + len - 1)
                .map(|_| random(&mut seed) as f32)
                .collect();
            let got = correlate_complex(&x, &t).unwrap();
            assert_eq!(got.len(), count);
            for (m, z) in got.iter().enumerate() {
                let mut re = 0.0;
                let mut im = 0.0;
                let mut scale = 1.0;
                for (v, t) in x[m..m + len].iter().zip(&t) {
                    re += f64::from(*v) * t.re;
                    im -= f64::from(*v) * t.im;
                    scale += f64::from(*v).abs() * (t.re.abs() + t.im.abs());
                }
                assert!((z.re - re).hypot(z.im - im) <= 1e-9 * scale);
            }
        }
    }
    assert!(
        correlate_complex(&[], &[Complex::default()])
            .unwrap()
            .is_empty()
    );
    assert!(correlate_complex(&[1.0], &[]).is_err());
}
