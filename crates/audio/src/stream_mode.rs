//! Platform-independent stream probe mode selection.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    SharedDefault,
    SharedMin,
    SharedDefaultRaw,
    SharedMinRaw,
    ExclusiveMin,
}
impl Mode {
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "shared-default" => Some(Self::SharedDefault),
            "shared-min" => Some(Self::SharedMin),
            "shared-default-raw" => Some(Self::SharedDefaultRaw),
            "shared-min-raw" => Some(Self::SharedMinRaw),
            "exclusive-min" => Some(Self::ExclusiveMin),
            _ => None,
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::SharedDefault => "shared-default",
            Self::SharedMin => "shared-min",
            Self::SharedDefaultRaw => "shared-default-raw",
            Self::SharedMinRaw => "shared-min-raw",
            Self::ExclusiveMin => "exclusive-min",
        }
    }
    pub fn is_exclusive(self) -> bool {
        matches!(self, Self::ExclusiveMin)
    }
    pub fn is_raw(self) -> bool {
        matches!(self, Self::SharedDefaultRaw | Self::SharedMinRaw)
    }
    pub fn uses_default_period(self) -> bool {
        matches!(self, Self::SharedDefault | Self::SharedDefaultRaw)
    }
    pub fn stream_mode(self) -> crate::StreamMode {
        if self.is_exclusive() {
            crate::StreamMode::Exclusive
        } else if self.is_raw() {
            crate::StreamMode::SharedRaw
        } else {
            crate::StreamMode::Shared
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Mode;
    use crate::StreamMode;

    // mode, exclusive, raw, default period, recorded stream mode
    const CASES: [(Mode, bool, bool, bool, StreamMode); 5] = [
        (Mode::SharedDefault, false, false, true, StreamMode::Shared),
        (Mode::SharedMin, false, false, false, StreamMode::Shared),
        (
            Mode::SharedDefaultRaw,
            false,
            true,
            true,
            StreamMode::SharedRaw,
        ),
        (
            Mode::SharedMinRaw,
            false,
            true,
            false,
            StreamMode::SharedRaw,
        ),
        (
            Mode::ExclusiveMin,
            true,
            false,
            false,
            StreamMode::Exclusive,
        ),
    ];

    #[test]
    fn names_round_trip_and_are_unique() {
        let mut names = std::collections::HashSet::new();
        for (mode, ..) in CASES {
            assert_eq!(Mode::parse(mode.name()), Some(mode));
            assert!(names.insert(mode.name()));
        }
    }

    #[test]
    fn invalid_names_are_rejected() {
        for name in ["", "shared", "shared-min-RAW", "exclusive-min-raw"] {
            assert_eq!(Mode::parse(name), None);
        }
    }

    #[test]
    fn mode_behavior_matches_table() {
        for (mode, exclusive, raw, default_period, stream_mode) in CASES {
            assert_eq!(mode.is_exclusive(), exclusive, "{mode:?}");
            assert_eq!(mode.is_raw(), raw, "{mode:?}");
            assert_eq!(mode.uses_default_period(), default_period, "{mode:?}");
            assert_eq!(mode.stream_mode(), stream_mode, "{mode:?}");
        }
    }
}
