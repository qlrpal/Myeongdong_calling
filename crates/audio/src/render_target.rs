#[derive(Debug, PartialEq, Eq)]
pub enum RenderTargetError {
    ZeroChannels,
    UnalignedLength,
}

pub enum RenderTarget<'a> {
    I16 {
        samples: &'a mut [i16],
        channels: u16,
    },
    I32 {
        samples: &'a mut [i32],
        channels: u16,
    },
    F32 {
        samples: &'a mut [f32],
        channels: u16,
    },
}

impl<'a> RenderTarget<'a> {
    pub fn i16(samples: &'a mut [i16], channels: u16) -> Result<Self, RenderTargetError> {
        validate(samples.len(), channels)?;
        Ok(Self::I16 { samples, channels })
    }

    pub fn i32(samples: &'a mut [i32], channels: u16) -> Result<Self, RenderTargetError> {
        validate(samples.len(), channels)?;
        Ok(Self::I32 { samples, channels })
    }

    pub fn f32(samples: &'a mut [f32], channels: u16) -> Result<Self, RenderTargetError> {
        validate(samples.len(), channels)?;
        Ok(Self::F32 { samples, channels })
    }

    #[must_use]
    pub fn frames(&self) -> usize {
        match self {
            Self::I16 { samples, channels } => frames(samples.len(), *channels),
            Self::I32 { samples, channels } => frames(samples.len(), *channels),
            Self::F32 { samples, channels } => frames(samples.len(), *channels),
        }
    }
}

fn frames(len: usize, channels: u16) -> usize {
    len.checked_div(usize::from(channels)).unwrap_or(0)
}

fn validate(len: usize, channels: u16) -> Result<(), RenderTargetError> {
    if channels == 0 {
        return Err(RenderTargetError::ZeroChannels);
    }
    if !len.is_multiple_of(usize::from(channels)) {
        return Err(RenderTargetError::UnalignedLength);
    }
    Ok(())
}
