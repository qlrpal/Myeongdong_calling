//! A0 이전 신호 전용, 실험 001과 비교 불가.
//! Independent experiment 001 oracle. No existing test helpers are used.
//!
//! 실험 001 재현 절차(A0 이전 신호):
//! - 역할: 하네스 커밋 7b7c9d8, 분석기 771c99f(7b7c9d8에 포함).
//! - 저장소 밖 경로에 작업 트리를 만든다: git worktree add --detach <저장소 밖 경로> 7b7c9d8
//! - 그 작업 트리에서 실행한다: cargo test --release -p mdc-rig --test exp001 -- --ignored exp001_full
//! - 출력은 그 작업 트리의 .local/001/에 쓰인다. 현재 저장소의 .local/001/과 .local/001-backup-2026-11-10/에는 영향이 없다.
//! - 재현 결과는 백업과 파일 해시(SHA-256)로 비교한다. 기준 해시:
//!   full-cells.csv 08ff208735d0982ef3aa6fcba579241e77b7439e79942aa68f96340527d1c211
//!   full-markers.csv ad00f6c3968886d44897340209efe515bd5ab74859ceea478e52d04ba01bc8e1
//!   full-failures.csv 282b3297128abfe65d30ee2586d5232ca665028d1a8008c5be8fa00cee43f66a
//!   full-calibration.csv 8388f13d0cb2c40aa654f627db016726affb8c13ddf1f23b07398c2c55cc8e0f
//! - 확인 기록: 2026-11-10 Windows MSVC에서 위 절차로 실행해 네 파일의 SHA-256이 백업과 같음을 확인(132초). Linux에서는 미확인.
use mdc_rig::{
    analyze::{self, AnalysisConfig, Class, LeftClass},
    calib::{Calibration, Xtalk, calibrate_xtalk},
    *,
};
use std::{
    fs::{self, File},
    io::{BufWriter, Write},
    path::{Component, Path, PathBuf},
    time::Instant,
};
#[derive(Clone, Debug)]
struct Cell {
    group: &'static str,
    delay: f64,
    frac: f64,
    db: f64,
    sign: f64,
    other: Option<(f64, f64, f64)>,
    snr: Option<f64>,
    damage: &'static str,
    ppm: f64,
}
fn base(group: &'static str) -> Cell {
    Cell {
        group,
        delay: 960.,
        frac: 0.,
        db: 0.,
        sign: 1.,
        other: None,
        snr: None,
        damage: "none",
        ppm: 0.,
    }
}
fn cells() -> Vec<Cell> {
    let mut v = Vec::new();
    for ms in [5., 20., 100., 316., 600.] {
        for frac in [0., 0.25, 0.5, 0.75] {
            for sign in [1., -1.] {
                for db in [0., -20., -40.] {
                    for snr in [None, Some(20.), Some(10.)] {
                        v.push(Cell {
                            delay: ms * 48.,
                            frac,
                            sign,
                            db,
                            snr,
                            ..base("G1")
                        });
                    }
                }
            }
        }
    }
    for ms in [5., 20., 100.] {
        for db in [0., -10., -20., -30., -40.] {
            for xd in [0., 0.3] {
                for xdb in [-60., -40., -30., -20.] {
                    for sign in [1., -1.] {
                        for xs in [1., -1.] {
                            for snr in [None, Some(10.)] {
                                v.push(Cell {
                                    delay: ms * 48.,
                                    db,
                                    sign,
                                    other: Some((xd, xdb, xs)),
                                    snr,
                                    ..base("G2")
                                });
                            }
                        }
                    }
                }
            }
        }
    }
    for ms in [1., 2., 5., 10., 20., 21., 50.] {
        for edb in [6., 0., -3., -6., -10., -15.] {
            for es in [1., -1.] {
                for frac in [0., 0.5] {
                    v.push(Cell {
                        frac,
                        other: Some((960. + ms * 48. + frac, edb, es)),
                        ..base("G3")
                    });
                }
            }
        }
    }
    for db in [-60., -40., -30., -20.] {
        for snr in [None, Some(10.)] {
            v.push(Cell {
                other: Some((0.3, db, 1.)),
                snr,
                ..base("G4")
            });
        }
    }
    for damage in ["delete", "flip1", "flip2", "silent", "clip", "crop"] {
        v.push(Cell {
            damage,
            ..base("G5")
        });
    }
    for ppm in [-100., 100.] {
        v.push(Cell { ppm, ..base("G5") });
    }
    for damage in ["triple_neighbor", "triple_other"] {
        for delay in [960., 28800., 43200.] {
            v.push(Cell {
                damage,
                delay,
                ..base("G5-risk")
            });
        }
    }
    v
}
// 128-tap centered Hann-windowed sinc; no causal filter delay is added.
fn weights(frac: f64) -> [f64; 128] {
    let mut w = [0.; 128];
    let mut sum = 0.;
    for (j, a) in w.iter_mut().enumerate() {
        let x = j as f64 - 63. - frac;
        *a = if x.abs() < 1e-12 {
            1.
        } else {
            (std::f64::consts::PI * x).sin() / (std::f64::consts::PI * x)
        } * (0.5 + 0.5 * (std::f64::consts::PI * x / 64.).cos());
        sum += *a;
    }
    for a in &mut w {
        *a /= sum;
    }
    w
}
fn path(src: &[f64], dst: &mut [f64], delay: f64, gain: f64, ratio: f64) {
    if ratio == 1. {
        let n = delay.floor() as isize;
        let f = delay - n as f64;
        if f == 0. {
            for (i, x) in src.iter().enumerate() {
                if let Some(d) = dst.get_mut((i as isize + n) as usize) {
                    *d += x * gain;
                }
            }
        } else {
            let w = weights(f);
            for (i, x) in src.iter().enumerate().filter(|(_, x)| **x != 0.) {
                for (j, a) in w.iter().enumerate() {
                    if let Some(d) = dst.get_mut((i as isize + n + j as isize - 63) as usize) {
                        *d += x * gain * a;
                    }
                }
            }
        }
    } else {
        for (i, d) in dst.iter_mut().enumerate() {
            let t = (i as f64 - delay) / ratio;
            let k = t.floor() as isize;
            if k < -64 || k >= src.len() as isize + 64 {
                continue;
            }
            let w = weights(t - k as f64);
            for (j, a) in w.iter().enumerate() {
                if let Some(x) = src.get((k + j as isize - 63) as usize) {
                    *d += x * gain * a;
                }
            }
        }
    }
}
fn wav(l: &[f64], r: &[f64]) -> Vec<u8> {
    let size = (l.len() * 4) as u32;
    let mut b = Vec::with_capacity(size as usize + 44);
    b.extend(b"RIFF");
    b.extend((size + 36).to_le_bytes());
    b.extend(b"WAVEfmt ");
    b.extend(16u32.to_le_bytes());
    b.extend(1u16.to_le_bytes());
    b.extend(2u16.to_le_bytes());
    b.extend(48000u32.to_le_bytes());
    b.extend(192000u32.to_le_bytes());
    b.extend(4u16.to_le_bytes());
    b.extend(16u16.to_le_bytes());
    b.extend(b"data");
    b.extend(size.to_le_bytes());
    for (a, c) in l.iter().zip(r) {
        for x in [a, c] {
            b.extend((x.round().clamp(-32768., 32767.) as i16).to_le_bytes());
        }
    }
    b
}
fn damage(src: &mut [f64], cfg: &ChirpConfig, kind: &str) {
    for (id, t, word) in marker_schedule(cfg).unwrap() {
        if id % 3 != 1 {
            continue;
        }
        if kind == "delete" {
            src[t..t + cfg.marker_len()].fill(0.);
            continue;
        }
        let mask = match kind {
            "flip1" => 1,
            "flip2" => 3,
            "triple_neighbor" | "triple_other" => {
                let target = (0..cfg.marker_count as u16)
                    .find(|target| {
                        let neighbor = target.abs_diff(id) == 1;
                        *target != id
                            && neighbor == (kind == "triple_neighbor")
                            && (word ^ encode_marker_id(*target).unwrap()).count_ones() == 4
                    })
                    .expect("predefined ID set has a distance-four target");
                let diff = word ^ encode_marker_id(target).unwrap();
                diff & !(1 << diff.trailing_zeros())
            }
            _ => 0,
        };
        for k in 0..16 {
            let start = t + cfg.sync_len + cfg.gap_len + k * (cfg.bit_len + cfg.gap_len);
            if kind == "silent" {
                src[start..start + cfg.bit_len].fill(0.);
            } else if mask & (1 << (15 - k)) != 0 {
                let bit = word & (1 << (15 - k)) == 0;
                for (d, x) in src[start..start + cfg.bit_len]
                    .iter_mut()
                    .zip(bit_template(cfg, bit))
                {
                    *d = (x * 10f64.powf(cfg.peak_dbfs / 20.) * 32767.).round();
                }
            }
        }
    }
}
const NOTICE: &str = "A0 이전 신호 전용, 실험 001과 비교 불가";

fn assert_safe_output(path: &Path) {
    // Resolve existing ancestors (including symlinks), then normalize nonexistent suffixes.
    fn normalize(path: &Path) -> PathBuf {
        let absolute = if path.is_absolute() {
            path.to_path_buf()
        } else {
            std::env::current_dir().unwrap().join(path)
        };
        let mut result = PathBuf::new();
        for part in absolute.components() {
            match part {
                Component::CurDir => {}
                Component::ParentDir => {
                    result.pop();
                }
                other => {
                    result.push(other.as_os_str());
                    if result.exists() {
                        result = result.canonicalize().unwrap();
                    }
                }
            }
        }
        result
    }
    let local = normalize(&PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.local"));
    let normalized = normalize(path);
    if let Ok(relative) = normalized.strip_prefix(local) {
        let first = relative
            .components()
            .next()
            .map(|c| c.as_os_str().to_string_lossy().to_lowercase());
        assert!(
            !first.is_some_and(|s| s == "001" || s.starts_with("001-backup-")),
            "protected experiment 001 output path"
        );
    }
}
fn outdir() -> PathBuf {
    let dir =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.local/runs/exp001-harness-postA0");
    assert_safe_output(&dir);
    dir
}
#[test]
fn rejects_protected_output_paths() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../.local");
    for suffix in [
        "001",
        "001/child",
        "runs/../001/child",
        "001-backup-2026-11-10/child",
        "001-backup-other",
    ] {
        assert!(
            std::panic::catch_unwind(|| assert_safe_output(&root.join(suffix))).is_err(),
            "{suffix}"
        );
    }
    assert_safe_output(&root.join("runs/exp001-harness-postA0"));
}
fn run(all: bool) {
    let now = Instant::now();
    let dir = outdir();
    fs::create_dir_all(&dir).unwrap();
    let metadata = format!(
        "{NOTICE}; analyzer_commit={}; marker_placement_fnv1a64=0x{:016x}",
        analyzer_commit_hash(),
        marker_placement_fnv1a64()
    );
    println!("{metadata}");
    fs::write(dir.join("run-metadata.txt"), format!("{metadata}\n")).unwrap();
    let prefix = if all { "full" } else { "smoke" };
    let mut csv = BufWriter::new(File::create(dir.join(format!("{prefix}-cells.csv"))).unwrap());
    let mut markers =
        BufWriter::new(File::create(dir.join(format!("{prefix}-markers.csv"))).unwrap());
    let mut failures =
        BufWriter::new(File::create(dir.join(format!("{prefix}-failures.csv"))).unwrap());
    let mut calout =
        BufWriter::new(File::create(dir.join(format!("{prefix}-calibration.csv"))).unwrap());
    for output in [&mut csv, &mut markers, &mut failures, &mut calout] {
        writeln!(output, "# {metadata}").unwrap();
    }
    writeln!(csv,"cell,group,delay,frac,db,sign,other,snr,damage,ppm,total,actual,distinguishable,normal,undecodable,mismatch,ambiguous,missing,excluded,l_not_ok,censored,false_confirm,miss,overlap_fn,overlap_fp,c4_bad,c6_bad,accuracy_max,error").unwrap();
    let header = "cell,group,id,actual,truth_delay,paths,distinguishable,injected_overlap,left_class,class,delay,overlap,false_confirm,miss,c4_bad,c6_bad,cause,evidence,c4_applicable,c5_bad,truth_censored,reason";
    writeln!(markers, "{header}").unwrap();
    writeln!(failures, "{header}").unwrap();
    writeln!(
        calout,
        "cell,injected,measured,pairs,normal_direct,normal_measured"
    )
    .unwrap();
    let list = if all { cells() } else { vec![base("smoke")] };
    let mut totals = [0usize; 5];
    for (ci, c) in list.iter().enumerate() {
        let cfg = ChirpConfig {
            marker_count: if all { 20 } else { 3 },
            ..ChirpConfig::default()
        };
        let ac = AnalysisConfig {
            chirp: cfg.clone(),
            ..AnalysisConfig::default()
        };
        let mut l: Vec<f64> = generate_samples(&cfg)
            .unwrap()
            .iter()
            .map(|x| f64::from(*x))
            .collect();
        // Preserve generator defaults, append max_delay + marker_len + 4096 guard to BOTH channels.
        l.resize(l.len() + 48000 + cfg.marker_len() + 4096, 0.);
        let mut src = l.clone();
        damage(&mut src, &cfg, c.damage);
        let ratio = 1. + c.ppm / 1e6;
        let delay = c.delay + c.frac;
        let gain = 10f64.powf(c.db / 20.);
        let mut r = vec![0.; l.len()];
        if c.group != "G4" {
            path(&src, &mut r, delay, gain * c.sign, ratio);
        }
        if c.damage == "clip" {
            let threshold = 10f64.powf(cfg.peak_dbfs / 20.) * 32767. * 0.5;
            for x in &mut r {
                *x = x.clamp(-threshold, threshold);
            }
        }
        if let Some((d, db, s)) = c.other {
            path(&l, &mut r, d, 10f64.powf(db / 20.) * s, 1.);
        }
        if let Some(snr) = c.snr {
            let reference = if c.group == "G4" {
                10f64.powf(c.other.unwrap().1 / 20.)
            } else {
                gain
            };
            let active: Vec<_> = src.iter().filter(|x| x.abs() > 0.).collect();
            let rms = (active.iter().map(|x| **x * **x).sum::<f64>() / active.len() as f64).sqrt()
                * reference;
            let target = rms / 10f64.powf(snr / 20.);
            let mut seed = 0x001_771c99fu64;
            let mut noise = Vec::with_capacity(r.len());
            for _ in 0..r.len() {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                noise.push((seed >> 11) as f64 / (1u64 << 53) as f64 * 2. - 1.);
            }
            let nr = (noise.iter().map(|x| x * x).sum::<f64>() / noise.len() as f64).sqrt();
            for (x, n) in r.iter_mut().zip(noise) {
                *x += n * target / nr;
            }
        }
        let mut crop = 0.;
        if c.damage == "crop" {
            let cut = cfg.lead_silence + cfg.sync_len / 2;
            l.drain(..cut);
            r.drain(..cut);
            crop = cut as f64;
            let end =
                cfg.lead_silence + (cfg.marker_count - 1) * cfg.interval_len + cfg.sync_len / 2
                    - cut;
            l.truncate(end);
            r.truncate(end);
        }
        let cal = Calibration {
            channel_offset: 0.,
            xtalk: if c.group == "G2" || c.group == "G4" {
                c.other.map(|(d, db, _)| Xtalk {
                    delay_min: d,
                    delay_max: d,
                    ratio_min_db: db,
                    ratio_max_db: db,
                })
            } else {
                None
            },
            source_files: "independent injected truth".into(),
        };
        let (wlo, whi) = if let Some(x) = &cal.xtalk {
            (x.delay_min.min(0.) - 2., x.delay_max.max(0.) + 2.)
        } else {
            (-2., 2.)
        };
        let rec = read_stereo_wav(wav(&l, &r).as_slice()).unwrap();
        let result = analyze::analyze(&ac, Some(&cal), &rec);
        // Public detector observations are used ONLY for failure attribution.
        // The injection oracle and all acceptance decisions remain independent.
        let diagnostic = analyze::detect_channel(&ac, &rec.right);
        let err = result
            .as_ref()
            .err()
            .map(ToString::to_string)
            .unwrap_or_default();
        if c.group == "G4" {
            let mut measured = Calibration {
                xtalk: None,
                ..cal.clone()
            };
            let pairs = calibrate_xtalk(&ac, &rec, &mut measured, "G4");
            let alt = analyze::analyze(&ac, Some(&measured), &rec);
            writeln!(
                calout,
                "{ci},\"{:?}\",\"{:?}\",\"{:?}\",{},{}",
                cal.xtalk,
                measured.xtalk,
                pairs,
                result.as_ref().map_or(0, |a| a
                    .markers
                    .iter()
                    .filter(|m| m.class == Some(Class::Normal))
                    .count()),
                alt.as_ref().map_or(0, |a| a
                    .markers
                    .iter()
                    .filter(|m| m.class == Some(Class::Normal))
                    .count())
            )
            .unwrap();
        }
        let schedule = marker_schedule(&cfg).unwrap();
        let mut paths = Vec::new();
        for (id, t, _) in &schedule {
            if c.group != "G4" && !(c.damage == "delete" && id % 3 == 1) {
                paths.push((*id, *t as f64 * ratio + delay - crop, c.db, "actual"));
            }
            if let Some((d, db, _)) = c.other {
                paths.push((
                    *id,
                    *t as f64 + d - crop,
                    db,
                    if c.group == "G3" { "echo" } else { "xtalk" },
                ));
            }
        }
        let mut counts = [0usize; 6];
        let (
            mut actual_n,
            mut dn,
            mut bad,
            mut miss,
            mut lokbad,
            mut censored,
            mut ofn,
            mut ofp,
            mut c4bad,
            mut c6bad,
        ) = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        let mut accuracy = 0f64;
        let has_normal = result
            .as_ref()
            .is_ok_and(|a| a.markers.iter().any(|m| m.class == Some(Class::Normal)));
        for (id, t, _) in &schedule {
            let truth = delay + *t as f64 * (ratio - 1.);
            let start = *t as f64 * ratio + delay - crop;
            let actual = c.group != "G4" && !(c.damage == "delete" && id % 3 == 1);
            actual_n += usize::from(actual);
            let cens = *t as f64 - crop < 0.
                || *t as f64 - crop + cfg.marker_len() as f64 + ac.max_delay > l.len() as f64;
            let others: Vec<_> = paths
                .iter()
                .filter(|p| !(p.0 == *id && p.3 == "actual"))
                .collect();
            let inj_overlap = actual
                && others
                    .iter()
                    .any(|p| (p.1 - start).abs() <= cfg.marker_len() as f64);
            let distinguish = actual
                && !cens
                && truth > whi
                && truth <= ac.max_delay
                && c.snr.is_none_or(|s| s >= 10.)
                && others.iter().all(|p| {
                    ((p.1 - start).abs() > cfg.marker_len() as f64 || p.2 <= c.db)
                        && (p.1 - start).abs() >= 96.
                });
            dn += usize::from(distinguish);
            let row = result
                .as_ref()
                .ok()
                .and_then(|a| a.markers.iter().find(|m| m.id == *id));
            let class = row.and_then(|m| m.class);
            let normal = class == Some(Class::Normal);
            let ix = match class {
                Some(Class::Normal) => 0,
                Some(Class::IdUndecodable) => 1,
                Some(Class::IdMismatch) => 2,
                Some(Class::Ambiguous) => 3,
                Some(Class::Missing) => 4,
                None => 5,
            };
            counts[ix] += 1;
            lokbad += usize::from(row.is_none_or(|m| m.left_class != LeftClass::Ok));
            censored += usize::from(row.is_some_and(|m| m.left_class == LeftClass::Censored));
            let observed = row.and_then(|m| m.delay);
            let overlap = row
                .and_then(|m| m.right.as_ref())
                .is_some_and(|d| d.sync.overlap);
            let ae = observed.map_or(f64::INFINITY, |d| (d - truth).abs());
            let false_c = normal
                && (!actual
                    || ae > if overlap { 2. } else { 0.5 }
                    || observed.is_some_and(|d| d >= wlo && d <= whi));
            let missed = distinguish && !normal;
            bad += usize::from(false_c);
            miss += usize::from(missed);
            // No selected R candidate means no overlap flag was exposed. Count it
            // explicitly as false here; marker CSV preserves the absent selection.
            ofn += usize::from(inj_overlap && !overlap);
            ofp += usize::from(!inj_overlap && overlap);
            // Gate evidence comes from the actual near candidate, never from distinguishability.
            let c4 = c.group == "G2"
                && c.other.is_some_and(|(d, db, _)| {
                    db > c.db && (delay - d).abs() <= cfg.marker_len() as f64
                })
                && row.is_some_and(|m| !m.near_candidates.is_empty());
            let b4 = c4 && !(class == Some(Class::Ambiguous) || (normal && !false_c));
            c4bad += usize::from(b4);
            let b6 = has_normal
                && id % 3 == 1
                && ["flip1", "flip2", "silent"].contains(&c.damage)
                && class
                    != Some(if c.damage == "flip1" {
                        Class::Normal
                    } else {
                        Class::IdUndecodable
                    });
            c6bad += usize::from(b6);
            // Criterion 5 says noiseless single path, without a G1-only
            // restriction. Non-Normal rows remain counted by criterion 2;
            // their unavailable final delay cannot substantiate accuracy.
            let timing_case = c.other.is_none() && c.snr.is_none() && actual && !cens;
            if timing_case && normal {
                accuracy = accuracy.max(ae);
            }
            let b5 = timing_case && normal && ae > 0.05;
            let at_truth = diagnostic
                .as_ref()
                .ok()
                .and_then(|ds| ds.iter().find(|d| (d.sync.time - start).abs() <= 2.));
            let cause = if false_c && (!actual || c.damage.starts_with("triple")) {
                "ii"
            } else if (false_c || missed || b4 || b6 || b5) && actual && at_truth.is_none() {
                "i"
            } else if b5 || (false_c && ae <= 2.) {
                "iv"
            } else if false_c || missed || b4 || b6 {
                "ii"
            } else {
                ""
            };
            let explanation = if cause == "iv" {
                "Normal timing exceeds injected truth tolerance"
            } else if cause == "i" {
                "no retained sync within two samples of injected onset; sync/gate limitation suspected"
            } else if cause == "ii" {
                "retained onset or miscorrected ID conflicts with final matching; rule attribution provisional"
            } else {
                ""
            };
            let evidence = format!(
                "{};truth_candidate={:?};diagnostic_error={:?}",
                explanation,
                at_truth.map(|d| (d.sync.time, d.sync.q, d.id.outcome)),
                diagnostic.as_ref().err()
            )
            .replace(',', ";");
            let ptext = paths
                .iter()
                .map(|p| format!("{}:{}:{:.6}:{:.1}", p.0, p.3, p.1, p.2))
                .collect::<Vec<_>>()
                .join(";");
            let line = format!(
                "{ci},{},{id},{actual},{truth:.9},{ptext},{distinguish},{inj_overlap},{:?},{class:?},{observed:?},{overlap},{false_c},{missed},{b4},{b6},{cause},{evidence},{c4},{b5},{cens},{}",
                c.group,
                row.map(|m| m.left_class),
                row.map_or(err.clone(), |m| m.reason.replace(',', ";"))
            );
            writeln!(markers, "{line}").unwrap();
            if false_c || missed || b4 || b6 || b5 {
                writeln!(failures, "{line}").unwrap();
            }
        }
        for (total, n) in totals.iter_mut().zip([actual_n, dn, bad, miss, counts[0]]) {
            *total += n;
        }
        writeln!(csv,"{ci},{},{},{},{},{},\"{:?}\",{:?},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{}",c.group,c.delay,c.frac,c.db,c.sign,c.other,c.snr,c.damage,c.ppm,cfg.marker_count,actual_n,dn,counts[0],counts[1],counts[2],counts[3],counts[4],counts[5],lokbad,censored,bad,miss,ofn,ofp,c4bad,c6bad,accuracy,err.replace(',',";")).unwrap();
        if ci % 50 == 0 || !all {
            println!(
                "cell {ci}/{} {} false={bad} missed={miss}/{dn} classes={counts:?} error={err}",
                list.len(),
                c.group
            );
        }
    }
    println!(
        "actual={} distinguishable={} false_confirm={} miss={} normal={}",
        totals[0], totals[1], totals[2], totals[3], totals[4]
    );
    println!(
        "exp001 {prefix}: {} cells elapsed {:.3}s output {}",
        list.len(),
        now.elapsed().as_secs_f64(),
        dir.display()
    );
}
#[test]
fn exp001_smoke() {
    run(false);
}
#[test]
#[ignore = "full independent experiment matrix; run in release"]
fn exp001_full() {
    println!(
        "exp001_full: A0 이전 신호 전용, 현재 코드에서는 실행할 수 없음. 재현은 파일 상단 주석의 절차를 따른다."
    );
}
