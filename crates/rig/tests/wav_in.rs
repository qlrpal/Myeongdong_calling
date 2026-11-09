use mdc_rig::*;
fn cfg() -> ChirpConfig {
    ChirpConfig {
        channels: 2,
        marker_count: 1,
        lead_silence: 480,
        tail_silence: 480,
        ..Default::default()
    }
}
fn chunk(id: &[u8; 4], body: &[u8]) -> Vec<u8> {
    let mut b = id.to_vec();
    b.extend((body.len() as u32).to_le_bytes());
    b.extend(body);
    if !body.len().is_multiple_of(2) {
        b.push(0);
    }
    b
}
fn fmt() -> Vec<u8> {
    let mut b = vec![];
    b.extend(1u16.to_le_bytes());
    b.extend(2u16.to_le_bytes());
    b.extend(48000u32.to_le_bytes());
    b.extend(192000u32.to_le_bytes());
    b.extend(4u16.to_le_bytes());
    b.extend(16u16.to_le_bytes());
    b
}
fn riff(chunks: &[Vec<u8>]) -> Vec<u8> {
    let mut b = b"RIFF".to_vec();
    let size = 4 + chunks.iter().map(Vec::len).sum::<usize>();
    b.extend((size as u32).to_le_bytes());
    b.extend(b"WAVE");
    for c in chunks {
        b.extend(c);
    }
    b
}
fn ext() -> Vec<u8> {
    let mut b = fmt();
    b[0..2].copy_from_slice(&0xfffeu16.to_le_bytes());
    b.extend(22u16.to_le_bytes());
    b.extend(16u16.to_le_bytes());
    b.extend(3u32.to_le_bytes());
    b.extend([1, 0, 0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113]);
    b
}
#[test]
fn w1_roundtrip() {
    let c = cfg();
    let mut b = vec![];
    write_wav(&c, &mut b).unwrap();
    let r = read_stereo_wav(b.as_slice()).unwrap();
    let s = generate_samples(&c).unwrap();
    assert!(!r.truncated);
    assert_eq!(r.left, r.right);
    assert_eq!(
        r.left,
        s.iter()
            .map(|v| f32::from(*v) / 32768.0)
            .collect::<Vec<_>>()
    );
}
#[test]
fn w2_extensible() {
    for mask in [0u32, 3] {
        let mut f = ext();
        f[20..24].copy_from_slice(&mask.to_le_bytes());
        let b = riff(&[chunk(b"fmt ", &f), chunk(b"data", &[0; 4])]);
        assert_eq!(read_stereo_wav(b.as_slice()).unwrap().left, vec![0.0]);
    }
}
#[test]
fn w3_unknown_chunks() {
    let b = riff(&[
        chunk(b"JUNK", &[1, 2, 3]),
        chunk(b"fmt ", &fmt()),
        chunk(b"LIST", &[5; 8]),
        chunk(b"data", &[0; 4]),
        chunk(b"JUNK", &[2]),
    ]);
    assert!(read_stereo_wav(b.as_slice()).is_ok());
}
#[test]
fn w4_rejections() {
    let f = chunk(b"fmt ", &fmt());
    let d = chunk(b"data", &[0; 4]);
    for chunks in [
        vec![d.clone(), f.clone()],
        vec![f.clone(), f.clone(), d.clone()],
        vec![f.clone(), d.clone(), d.clone()],
    ] {
        assert!(read_stereo_wav(riff(&chunks).as_slice()).is_err());
    }
    for id in [b"RIFX", b"RF64"] {
        let mut b = riff(&[f.clone(), d.clone()]);
        b[..4].copy_from_slice(id);
        assert!(read_stereo_wav(b.as_slice()).is_err());
    }
    let mut invalid = vec![];
    for (offset, value) in [(0, 3u16), (2, 1), (14, 24), (12, 2)] {
        let mut b = fmt();
        b[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
        invalid.push(b);
    }
    for (offset, value) in [(4, 44100u32), (8, 96000)] {
        let mut b = fmt();
        b[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
        invalid.push(b);
    }
    for (offset, value) in [(16, 21u16), (18, 24)] {
        let mut b = ext();
        b[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
        invalid.push(b);
    }
    let mut b = ext();
    b[20..24].copy_from_slice(&4u32.to_le_bytes());
    invalid.push(b);
    let mut b = ext();
    b[24] = 3;
    invalid.push(b);
    for n in [0, 1, 15] {
        invalid.push(fmt()[..n].to_vec());
    }
    for n in [16, 18, 39] {
        invalid.push(ext()[..n].to_vec());
    }
    for f in invalid {
        assert!(read_stereo_wav(riff(&[chunk(b"fmt ", &f), d.clone()]).as_slice()).is_err());
    }
}
#[test]
fn w5_truncated() {
    let b = riff(&[chunk(b"fmt ", &fmt()), chunk(b"data", &[0; 6])]);
    let r = read_stereo_wav(b.as_slice()).unwrap();
    assert_eq!(r.left.len(), 1);
    assert!(r.truncated);
    let mut b = riff(&[chunk(b"fmt ", &fmt()), chunk(b"data", &[0; 12])]);
    b.truncate(b.len() - 5);
    let r = read_stereo_wav(b.as_slice()).unwrap();
    assert_eq!(r.left.len(), 1);
    assert!(r.truncated);
}
#[test]
fn w6_levels() {
    let samples = [32767i16, -32768, 100, 0, -32768, 16384];
    let bytes: Vec<u8> = samples.iter().flat_map(|s| s.to_le_bytes()).collect();
    let b = riff(&[chunk(b"fmt ", &fmt()), chunk(b"data", &bytes)]);
    let r = read_stereo_wav(b.as_slice()).unwrap();
    assert_eq!((r.clipped_left, r.clipped_right), (2, 1));
    assert_eq!(r.peak_left_dbfs, Some(0.0));
    assert_eq!(r.peak_right_dbfs, Some(0.0));
    let b = riff(&[chunk(b"fmt ", &fmt()), chunk(b"data", &[0, 0, 0, 64])]);
    let r = read_stereo_wav(b.as_slice()).unwrap();
    assert_eq!(r.peak_left_dbfs, None);
    assert!((r.peak_right_dbfs.unwrap() + 6.020599913).abs() < 1e-8);
}
