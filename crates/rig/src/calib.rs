//! Versioned key=value calibration. All delays are raw ADC R-L samples.
use crate::analyze::{AnalysisConfig, decoded_id, detect_channel, median};
use crate::{RigError, StereoRecording};
use std::{collections::BTreeMap, fs, path::Path};
#[derive(Clone, Debug, PartialEq)]
pub struct Xtalk {
    pub delay_min: f64,
    pub delay_max: f64,
    pub ratio_min_db: f64,
    pub ratio_max_db: f64,
}
#[derive(Clone, Debug, PartialEq)]
pub enum CalibrationState {
    NotPerformed,
    ValidNoDetection,
    Insufficient,
    Measured(Xtalk),
}
impl CalibrationState {
    pub fn as_ref(&self) -> Option<&Xtalk> {
        if let Self::Measured(x) = self {
            Some(x)
        } else {
            None
        }
    }
    pub fn is_none(&self) -> bool {
        self.as_ref().is_none()
    }
}
#[derive(Clone, Debug, PartialEq)]
pub struct Calibration {
    pub channel_offset: f64,
    pub xtalk: CalibrationState,
    pub z: f64,
    pub x: f64,
    pub z_basis: String,
    pub source_files: String,
}
impl Calibration {
    pub fn validate(&self) -> Result<(), RigError> {
        if !self.z.is_finite()
            || !(0.0..=u32::MAX as f64).contains(&self.z)
            || !self.x.is_finite()
            || !(0.0..=u32::MAX as f64).contains(&self.x)
            || self.z_basis.contains(['\r', '\n'])
        {
            return Err(RigError::Invalid("calibration safety settings"));
        }
        if !self.channel_offset.is_finite()
            || self.channel_offset.abs() > u32::MAX as f64
            || self.source_files.contains(['\r', '\n'])
        {
            return Err(RigError::Invalid("calibration offset or source_files"));
        }
        if let Some(x) = self.xtalk.as_ref()
            && (![x.delay_min, x.delay_max, x.ratio_min_db, x.ratio_max_db]
                .iter()
                .all(|v| v.is_finite())
                || x.delay_min > x.delay_max
                || x.ratio_min_db > x.ratio_max_db
                || x.delay_min.abs() > u32::MAX as f64
                || x.delay_max.abs() > u32::MAX as f64)
        {
            return Err(RigError::Invalid("calibration xtalk ranges"));
        }
        Ok(())
    }
    pub fn window(&self) -> (f64, f64) {
        self.xtalk.as_ref().map_or(
            (self.channel_offset - 2.0, self.channel_offset + 2.0),
            |x| {
                (
                    x.delay_min.min(self.channel_offset) - 2.0,
                    x.delay_max.max(self.channel_offset) + 2.0,
                )
            },
        )
    }
    /// Exact in-memory comparison identity; provenance is included deliberately.
    pub fn identity(&self) -> String {
        format!("version=2;{self:?}")
    }
    pub fn to_text(&self) -> Result<String, RigError> {
        self.validate()?;
        let optional = |f: fn(&Xtalk) -> f64| {
            self.xtalk
                .as_ref()
                .map_or_else(|| "none".into(), |x| format!("{:.12}", f(x)))
        };
        Ok(format!(
            "version=2\nchannel_offset={:.12}\nxtalk_delay_min={}\nxtalk_delay_max={}\nxtalk_ratio_min_db={}\nxtalk_ratio_max_db={}\nsource_files={}\nstate={}\nz={:?}\nx={:?}\nz_basis={}\n",
            self.channel_offset,
            optional(|x| x.delay_min),
            optional(|x| x.delay_max),
            optional(|x| x.ratio_min_db),
            optional(|x| x.ratio_max_db),
            self.source_files,
            match self.xtalk {
                CalibrationState::NotPerformed => "not_performed",
                CalibrationState::ValidNoDetection => "valid_no_detection",
                CalibrationState::Insufficient => "insufficient",
                CalibrationState::Measured(_) => "measured",
            },
            self.z,
            self.x,
            self.z_basis
        ))
    }
    /// Version 1 is rejected: it cannot distinguish unperformed from valid no detection.
    pub fn from_text(s: &str) -> Result<Self, RigError> {
        const BAD: RigError = RigError::Invalid("calibration file");
        let mut values = BTreeMap::new();
        for line in s.lines().filter(|l| !l.trim().is_empty()) {
            let (k, v) = line.split_once('=').ok_or(BAD)?;
            if values.insert(k, v).is_some() {
                return Err(BAD);
            }
        }
        let get = |k| values.get(k).copied().ok_or(BAD);
        if values.len() != 11 || get("version")? != "2" {
            return Err(BAD);
        }
        let number = |k| -> Result<f64, RigError> { get(k)?.parse().map_err(|_| BAD) };
        let keys = [
            "xtalk_delay_min",
            "xtalk_delay_max",
            "xtalk_ratio_min_db",
            "xtalk_ratio_max_db",
        ];
        let none = keys
            .iter()
            .map(|k| get(k).map(|v| v == "none"))
            .collect::<Result<Vec<_>, _>>()?;
        let xtalk = if none.iter().all(|v| *v) {
            None
        } else if none.iter().any(|v| *v) {
            return Err(BAD);
        } else {
            Some(Xtalk {
                delay_min: number(keys.first().copied().ok_or(BAD)?)?,
                delay_max: number("xtalk_delay_max")?,
                ratio_min_db: number("xtalk_ratio_min_db")?,
                ratio_max_db: number("xtalk_ratio_max_db")?,
            })
        };
        let result = Self {
            channel_offset: number("channel_offset")?,
            xtalk: match (get("state")?, xtalk) {
                ("not_performed", None) => CalibrationState::NotPerformed,
                ("valid_no_detection", None) => CalibrationState::ValidNoDetection,
                ("insufficient", None) => CalibrationState::Insufficient,
                ("measured", Some(x)) => CalibrationState::Measured(x),
                _ => return Err(BAD),
            },
            z: number("z")?,
            x: number("x")?,
            z_basis: get("z_basis")?.into(),
            source_files: get("source_files")?.into(),
        };
        result.validate()?;
        Ok(result)
    }
    pub fn read(path: impl AsRef<Path>) -> Result<Self, RigError> {
        Self::from_text(&fs::read_to_string(path)?)
    }
    pub fn write(&self, path: impl AsRef<Path>) -> Result<(), RigError> {
        fs::write(path, self.to_text()?)?;
        Ok(())
    }
}
#[derive(Clone, Debug)]
pub struct OffsetStats {
    pub count: usize,
    pub median: f64,
    pub min: f64,
    pub max: f64,
}
fn pairs(c: &AnalysisConfig, rec: &StereoRecording) -> Result<Vec<(f64, f64)>, RigError> {
    if rec.left.len() != rec.right.len() {
        return Err(RigError::Invalid("channel length mismatch"));
    }
    let left = detect_channel(c, &rec.left)?;
    let right = detect_channel(c, &rec.right)?;
    let mut pairs = Vec::new();
    for l in &left {
        let Some(id) = decoded_id(l.id.outcome) else {
            continue;
        };
        // Calibration is not restricted by W or max_delay. Retain all ID-matched paths.
        for r in right
            .iter()
            .filter(|r| decoded_id(r.id.outcome) == Some(id))
        {
            pairs.push((
                r.sync.time - l.sync.time,
                20.0 * (r.sync.amplitude / l.sync.amplitude).log10(),
            ));
        }
    }
    Ok(pairs)
}
pub fn calibrate_offset(
    c: &AnalysisConfig,
    rec: &StereoRecording,
    source: &str,
) -> Result<(Calibration, OffsetStats), RigError> {
    let pairs = pairs(c, rec)?;
    let ds: Vec<_> = pairs.iter().map(|p| p.0).collect();
    let center = median(&ds).ok_or(RigError::Invalid(
        "offset calibration has no ID matched pairs",
    ))?;
    let stats = OffsetStats {
        count: ds.len(),
        median: center,
        min: ds.iter().copied().fold(f64::INFINITY, f64::min),
        max: ds.iter().copied().fold(f64::NEG_INFINITY, f64::max),
    };
    let cal = Calibration {
        channel_offset: center,
        xtalk: CalibrationState::NotPerformed,
        z: 0.0,
        x: 0.0,
        z_basis: String::new(),
        source_files: source.into(),
    };
    cal.validate()?;
    Ok((cal, stats))
}
pub fn calibrate_xtalk(
    c: &AnalysisConfig,
    rec: &StereoRecording,
    cal: &mut Calibration,
    source: &str,
) -> Result<usize, RigError> {
    cal.validate()?;
    let pairs = pairs(c, rec)?;
    let mut next = cal.clone();
    let left = detect_channel(c, &rec.left)?;
    let successful = left
        .iter()
        .filter_map(|p| decoded_id(p.id.outcome))
        .collect::<std::collections::BTreeSet<_>>()
        .len();
    let left_aligned = crate::analyze::align_left(c, &left).is_ok();
    let sufficient = c.no_detection.as_ref().is_some_and(|v| {
        left_aligned
            && successful >= v.min_left
            && rec.left.len() >= v.min_frames
            && crate::amplitude_gate(&c.detector, &rec.right) <= v.detection_limit
    });
    next.xtalk = if !left_aligned || successful < 3 {
        CalibrationState::Insufficient
    } else if pairs.is_empty() {
        if sufficient {
            CalibrationState::ValidNoDetection
        } else {
            CalibrationState::Insufficient
        }
    } else {
        CalibrationState::Measured(Xtalk {
            delay_min: pairs.iter().map(|p| p.0).fold(f64::INFINITY, f64::min),
            delay_max: pairs.iter().map(|p| p.0).fold(f64::NEG_INFINITY, f64::max),
            ratio_min_db: pairs.iter().map(|p| p.1).fold(f64::INFINITY, f64::min),
            ratio_max_db: pairs.iter().map(|p| p.1).fold(f64::NEG_INFINITY, f64::max),
        })
    };
    next.source_files.push(';');
    next.source_files.push_str(source);
    next.validate()?;
    *cal = next;
    Ok(pairs.len())
}
