use crate::RigError;
use std::io::Read;

#[derive(Debug)]
pub struct StereoRecording {
    pub left: Vec<f32>,
    pub right: Vec<f32>,
    pub truncated: bool,
    pub clipped_left: usize,
    pub clipped_right: usize,
    pub peak_left_dbfs: Option<f64>,
    pub peak_right_dbfs: Option<f64>,
}
const BAD: RigError = RigError::Invalid("invalid stereo PCM WAV");
fn u16_at(b: &[u8], n: usize) -> Result<u16, RigError> {
    let end = n.checked_add(2).ok_or(BAD)?;
    Ok(u16::from_le_bytes(
        b.get(n..end).ok_or(BAD)?.try_into().map_err(|_| BAD)?,
    ))
}
fn u32_at(b: &[u8], n: usize) -> Result<u32, RigError> {
    let end = n.checked_add(4).ok_or(BAD)?;
    Ok(u32::from_le_bytes(
        b.get(n..end).ok_or(BAD)?.try_into().map_err(|_| BAD)?,
    ))
}
fn format(b: &[u8]) -> Result<(), RigError> {
    if b.len() < 16
        || u16_at(b, 2)? != 2
        || u32_at(b, 4)? != 48000
        || u32_at(b, 8)? != 192000
        || u16_at(b, 12)? != 4
        || u16_at(b, 14)? != 16
    {
        return Err(BAD);
    }
    match u16_at(b, 0)? {
        1 => Ok(()),
        0xfffe => {
            const GUID: [u8; 16] = [1, 0, 0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113];
            if b.len() < 40
                || u16_at(b, 16)? < 22
                || u16_at(b, 18)? != 16
                || ![0, 3].contains(&u32_at(b, 20)?)
                || b.get(24..40) != Some(GUID.as_slice())
            {
                return Err(BAD);
            }
            let extension_end = usize::from(u16_at(b, 16)?).checked_add(18).ok_or(BAD)?;
            if extension_end > b.len() {
                return Err(BAD);
            }
            Ok(())
        }
        _ => Err(BAD),
    }
}
/// Reads little-endian, 48 kHz, stereo PCM16. Incomplete data retains whole frames.
pub fn read_stereo_wav<R: Read>(mut r: R) -> Result<StereoRecording, RigError> {
    let mut bytes = Vec::new();
    r.read_to_end(&mut bytes)?;
    if bytes.get(0..4) != Some(b"RIFF") || bytes.get(8..12) != Some(b"WAVE") {
        return Err(BAD);
    }
    let declared_end = usize::try_from(u32_at(&bytes, 4)?)
        .map_err(|_| BAD)?
        .checked_add(8)
        .ok_or(BAD)?;
    if declared_end < 12 {
        return Err(BAD);
    }
    let boundary = bytes.len().min(declared_end);
    let mut pos = 12usize;
    let mut fmt = false;
    let mut data = None;
    let mut truncated = false;
    while pos < boundary {
        let header_end = pos.checked_add(8).ok_or(BAD)?;
        if header_end > boundary {
            return Err(BAD);
        }
        let id_end = pos.checked_add(4).ok_or(BAD)?;
        let id = bytes.get(pos..id_end).ok_or(BAD)?;
        let size = usize::try_from(u32_at(&bytes, id_end)?).map_err(|_| BAD)?;
        let end = header_end.checked_add(size).ok_or(BAD)?;
        let padded = end.checked_add(size & 1).ok_or(BAD)?;
        match id {
            b"fmt " => {
                if fmt || end > boundary {
                    return Err(BAD);
                }
                format(bytes.get(header_end..end).ok_or(BAD)?)?;
                fmt = true;
            }
            b"data" => {
                if !fmt || data.is_some() {
                    return Err(BAD);
                }
                truncated |= end > boundary || size % 4 != 0;
                data = Some(bytes.get(header_end..end.min(boundary)).ok_or(BAD)?);
            }
            _ => {
                if padded > boundary {
                    return Err(BAD);
                }
            }
        }
        if end > boundary {
            break;
        }
        if padded > boundary {
            return Err(BAD);
        }
        pos = padded;
    }
    let data = data.ok_or(BAD)?;
    let frames = data.len() / 4;
    let mut left = Vec::new();
    let mut right = Vec::new();
    left.try_reserve_exact(frames)
        .map_err(RigError::Allocation)?;
    right
        .try_reserve_exact(frames)
        .map_err(RigError::Allocation)?;
    let (mut cl, mut cr, mut pl, mut pr) = (0usize, 0usize, 0f64, 0f64);
    for frame in data.chunks_exact(4) {
        let l = i16::from_le_bytes(frame.get(..2).ok_or(BAD)?.try_into().map_err(|_| BAD)?);
        let r = i16::from_le_bytes(frame.get(2..4).ok_or(BAD)?.try_into().map_err(|_| BAD)?);
        if l == i16::MAX || l == i16::MIN {
            cl = cl.checked_add(1).ok_or(BAD)?;
        }
        if r == i16::MAX || r == i16::MIN {
            cr = cr.checked_add(1).ok_or(BAD)?;
        }
        pl = pl.max(f64::from(l).abs());
        pr = pr.max(f64::from(r).abs());
        left.push(f32::from(l) / 32768.0);
        right.push(f32::from(r) / 32768.0);
    }
    let db = |p: f64| {
        if p > 0.0 {
            Some(20.0 * (p / 32768.0).log10())
        } else {
            None
        }
    };
    Ok(StereoRecording {
        left,
        right,
        truncated,
        clipped_left: cl,
        clipped_right: cr,
        peak_left_dbfs: db(pl),
        peak_right_dbfs: db(pr),
    })
}
