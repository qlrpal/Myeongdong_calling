use mdc_timeline::{MediaFrame, SampleIndex};

pub const SLOT_CAPACITY: usize = 8;

#[derive(Clone, Copy, Debug)]
pub struct PlayoutSlots {
    slots: [Option<MediaFrame>; SLOT_CAPACITY],
    len: usize,
}

impl PlayoutSlots {
    #[must_use]
    pub const fn new() -> Self {
        Self {
            slots: [None; SLOT_CAPACITY],
            len: 0,
        }
    }

    pub fn insert(&mut self, frame: MediaFrame) -> bool {
        let Some(slot) = self.slots.iter_mut().find(|slot| slot.is_none()) else {
            return false;
        };
        *slot = Some(frame);
        self.len = self.len.saturating_add(1);
        true
    }

    pub fn take_starting_at(&mut self, start: SampleIndex) -> Option<MediaFrame> {
        let slot = self.slots.iter_mut().find(|slot| {
            slot.as_ref()
                .is_some_and(|frame| frame.range().start() == start)
        })?;
        let frame = slot.take();
        if frame.is_some() {
            self.len = self.len.saturating_sub(1);
        }
        frame
    }

    pub fn discard_before(&mut self, point: SampleIndex) -> u32 {
        let mut discarded = 0u32;
        for slot in &mut self.slots {
            if slot
                .as_ref()
                .is_some_and(|frame| frame.range().end() <= point)
            {
                *slot = None;
                self.len = self.len.saturating_sub(1);
                discarded = discarded.saturating_add(1);
            }
        }
        discarded
    }

    #[must_use]
    pub const fn len(&self) -> usize {
        self.len
    }

    #[must_use]
    pub const fn is_empty(&self) -> bool {
        self.len == 0
    }
}

impl Default for PlayoutSlots {
    fn default() -> Self {
        Self::new()
    }
}
