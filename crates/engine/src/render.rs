use mdc_audio::{RenderTarget, spsc::Consumer};
use mdc_codec::Decoder;
use mdc_jitter::PlayoutSlots;
use mdc_timeline::{Admission, Epoch, FrameLedger, MediaFrame, SampleIndex};

pub const MAX_PEERS: usize = 5;
pub const FRAME_SAMPLES: usize = 128;
// A cycle runs from the end of one render call through the end of the next; only render resets
// these budgets. Budget and slot stops are observed directly by their counters. Resulting loss may
// later appear indirectly as late, missing_samples, or discarded_late.
// The three stop counters increase at most once per cycle: they count cycles that stopped for that
// reason while the ring was non-empty.
pub const MAX_INSPECT_PER_CYCLE: usize = 8;
pub const MAX_ADMIT_PER_CYCLE: usize = 6;
pub const MAX_CYCLE_FRAMES: usize = 4096;
// This is a slot-blocking bound, not a receive-slack policy.
// The receive-slack policy will be decided by a later experiment (E9).
pub const PLAYOUT_WINDOW_SAMPLES: usize = mdc_jitter::SLOT_CAPACITY * FRAME_SAMPLES;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RenderCounters {
    pub popped: u64,
    pub admitted_new: u64,
    pub duplicate: u64,
    pub conflict: u64,
    pub late: u64,
    pub stale_epoch: u64,
    pub newer_epoch: u64,
    pub slot_full: u64,
    pub beyond_window: u64,
    pub missing_samples: u64,
    pub played_samples: u64,
    pub skipped_samples: u64,
    pub decode_errors: u64,
    pub discarded_late: u64,
    pub inspect_budget_exhausted: u64,
    pub admit_budget_exhausted: u64,
    pub slots_full_stops: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Stage {
    AfterPop,
    AfterDecode,
    AfterMix,
}

/// Observes render-cycle stages from the real-time render thread.
///
/// Implementations must not allocate or deallocate, lock, perform I/O, or panic, and must use
/// only fixed-size storage.
pub trait CycleProbe {
    /// Records a stage on the real-time render thread. This must obey the trait's real-time
    /// contract: no allocation/deallocation, locks, I/O, or panics, using fixed-size storage only.
    fn mark(&mut self, stage: Stage);
}

pub struct NoProbe;

impl CycleProbe for NoProbe {
    fn mark(&mut self, _stage: Stage) {}
}

pub struct PeerRender<D: Decoder> {
    consumer: Consumer<MediaFrame>,
    ledger: FrameLedger,
    slots: PlayoutSlots,
    decoder: D,
    block: [i16; FRAME_SAMPLES],
    block_offset: usize,
    block_ready: bool,
    block_silent: bool,
    cursor: SampleIndex,
    counters: RenderCounters,
    inspect_remaining: usize,
    admit_remaining: usize,
    inspect_stop_latched: bool,
    admit_stop_latched: bool,
    slots_stop_latched: bool,
    cycle: [i16; MAX_CYCLE_FRAMES],
}

impl<D: Decoder> PeerRender<D> {
    #[must_use]
    pub fn new(
        consumer: Consumer<MediaFrame>,
        decoder: D,
        epoch: Epoch,
        cursor: SampleIndex,
    ) -> Self {
        Self {
            consumer,
            ledger: FrameLedger::new(epoch),
            slots: PlayoutSlots::new(),
            decoder,
            block: [0; FRAME_SAMPLES],
            block_offset: FRAME_SAMPLES,
            block_ready: false,
            block_silent: false,
            cursor,
            counters: RenderCounters::default(),
            inspect_remaining: MAX_INSPECT_PER_CYCLE,
            admit_remaining: MAX_ADMIT_PER_CYCLE,
            inspect_stop_latched: false,
            admit_stop_latched: false,
            slots_stop_latched: false,
            cycle: [0; MAX_CYCLE_FRAMES],
        }
    }

    #[must_use]
    pub const fn counters(&self) -> RenderCounters {
        self.counters
    }

    #[must_use]
    pub const fn cursor(&self) -> SampleIndex {
        self.cursor
    }

    fn pop_admit(&mut self) {
        loop {
            if self.inspect_remaining == 0 {
                if !self.inspect_stop_latched && !self.consumer.is_empty() {
                    self.counters.inspect_budget_exhausted =
                        self.counters.inspect_budget_exhausted.saturating_add(1);
                    self.inspect_stop_latched = true;
                }
                break;
            }
            if self.admit_remaining == 0 {
                if !self.admit_stop_latched && !self.consumer.is_empty() {
                    self.counters.admit_budget_exhausted =
                        self.counters.admit_budget_exhausted.saturating_add(1);
                    self.admit_stop_latched = true;
                }
                break;
            }
            if self.slots.len() >= mdc_jitter::SLOT_CAPACITY {
                if !self.slots_stop_latched && !self.consumer.is_empty() {
                    self.counters.slots_full_stops =
                        self.counters.slots_full_stops.saturating_add(1);
                    self.slots_stop_latched = true;
                }
                break;
            }
            let Some(frame) = self.consumer.pop() else {
                break;
            };
            self.inspect_remaining = self.inspect_remaining.saturating_sub(1);
            self.counters.popped = self.counters.popped.saturating_add(1);
            if frame.key().epoch == self.ledger.epoch() {
                let window_end = self
                    .cursor
                    .get()
                    .saturating_add(PLAYOUT_WINDOW_SAMPLES as u64);
                if frame.range().start().get() >= window_end {
                    self.counters.beyond_window = self.counters.beyond_window.saturating_add(1);
                    continue;
                }
            }
            match self.ledger.admit(frame.key().epoch, frame.range()) {
                Admission::New => {
                    self.admit_remaining = self.admit_remaining.saturating_sub(1);
                    self.counters.admitted_new = self.counters.admitted_new.saturating_add(1);
                    if !self.slots.insert(frame) {
                        self.counters.slot_full = self.counters.slot_full.saturating_add(1);
                    }
                }
                Admission::Duplicate => {
                    self.counters.duplicate = self.counters.duplicate.saturating_add(1);
                }
                Admission::Conflict => {
                    self.counters.conflict = self.counters.conflict.saturating_add(1);
                }
                Admission::Late => self.counters.late = self.counters.late.saturating_add(1),
                Admission::StaleEpoch => {
                    self.counters.stale_epoch = self.counters.stale_epoch.saturating_add(1);
                }
                Admission::NewerEpoch => {
                    self.counters.newer_epoch = self.counters.newer_epoch.saturating_add(1);
                }
            }
        }
    }

    fn load_block(&mut self) {
        self.block.fill(0);
        let block_start =
            SampleIndex::new(self.cursor.get().saturating_sub(self.block_offset as u64));
        let Some(frame) = self.slots.take_starting_at(block_start) else {
            self.block_ready = true;
            self.block_silent = true;
            return;
        };
        if frame.range().len() as usize != FRAME_SAMPLES
            || self.decoder.decode(&frame, &mut self.block).is_err()
        {
            self.block.fill(0);
            self.counters.decode_errors = self.counters.decode_errors.saturating_add(1);
            self.block_silent = true;
        } else {
            self.block_silent = false;
        }
        self.block_ready = true;
    }

    fn decode_cycle(&mut self, frames: usize) {
        let mut written = 0usize;
        while written < frames {
            if self.block_offset >= FRAME_SAMPLES {
                self.block_offset = 0;
                self.block_ready = false;
            }
            if !self.block_ready {
                self.load_block();
            }
            let available = FRAME_SAMPLES.saturating_sub(self.block_offset);
            let wanted = frames.saturating_sub(written);
            let count = available.min(wanted);
            let Some(src) = self
                .block
                .get(self.block_offset..self.block_offset.saturating_add(count))
            else {
                break;
            };
            let Some(dst) = self.cycle.get_mut(written..written.saturating_add(count)) else {
                break;
            };
            dst.copy_from_slice(src);
            if self.block_silent {
                self.counters.missing_samples =
                    self.counters.missing_samples.saturating_add(count as u64);
            }
            self.block_offset = self.block_offset.saturating_add(count);
            written = written.saturating_add(count);
            self.cursor = SampleIndex::new(self.cursor.get().saturating_add(count as u64));
        }
        self.counters.played_samples = self.counters.played_samples.saturating_add(written as u64);
    }

    fn skip(&mut self, samples: u64) {
        let residual = FRAME_SAMPLES.saturating_sub(self.block_offset) as u64;
        if samples < residual {
            self.block_offset = self.block_offset.saturating_add(samples as usize);
        } else {
            let remaining = samples.saturating_sub(residual);
            self.block_offset = (remaining % FRAME_SAMPLES as u64) as usize;
            if self.block_offset == 0 {
                self.block_offset = FRAME_SAMPLES;
            }
            self.block_ready = false;
            self.block_silent = false;
        }
        self.cursor = SampleIndex::new(self.cursor.get().saturating_add(samples));
        self.counters.skipped_samples = self.counters.skipped_samples.saturating_add(samples);
    }

    fn finish_cycle(&mut self) {
        self.ledger.advance_played(self.cursor);
        let discarded = self.slots.discard_before(self.cursor);
        self.counters.discarded_late = self
            .counters
            .discarded_late
            .saturating_add(u64::from(discarded));
    }

    fn reset_cycle_budgets(&mut self) {
        self.inspect_remaining = MAX_INSPECT_PER_CYCLE;
        self.admit_remaining = MAX_ADMIT_PER_CYCLE;
        self.inspect_stop_latched = false;
        self.admit_stop_latched = false;
        self.slots_stop_latched = false;
    }
}

pub struct RenderCore<D: Decoder> {
    peers: [Option<PeerRender<D>>; MAX_PEERS],
    mix: [i32; MAX_CYCLE_FRAMES],
    oversize_cycles: u64,
}

impl<D: Decoder> RenderCore<D> {
    #[must_use]
    pub fn new(peers: [Option<PeerRender<D>>; MAX_PEERS]) -> Self {
        assert!(
            peers.iter().any(Option::is_some),
            "RenderCore requires at least one peer"
        );
        Self {
            peers,
            mix: [0; MAX_CYCLE_FRAMES],
            oversize_cycles: 0,
        }
    }

    #[must_use]
    pub const fn oversize_cycles(&self) -> u64 {
        self.oversize_cycles
    }

    #[must_use]
    pub fn peer(&self, index: usize) -> Option<&PeerRender<D>> {
        self.peers.get(index)?.as_ref()
    }

    pub fn render(&mut self, mut target: RenderTarget<'_>, probe: &mut impl CycleProbe) {
        let requested = target.frames();
        let frames = requested.min(MAX_CYCLE_FRAMES);
        if requested > frames {
            self.oversize_cycles = self.oversize_cycles.saturating_add(1);
        }
        for peer in self.peers.iter_mut().flatten() {
            peer.pop_admit();
        }
        probe.mark(Stage::AfterPop);

        for peer in self.peers.iter_mut().flatten() {
            peer.decode_cycle(frames);
            if requested > frames {
                peer.skip((requested.saturating_sub(frames)) as u64);
            }
        }
        probe.mark(Stage::AfterDecode);

        if let Some(mix) = self.mix.get_mut(..frames) {
            mix.fill(0);
            for peer in self.peers.iter().flatten() {
                for (sum, sample) in mix.iter_mut().zip(peer.cycle.iter()) {
                    *sum = sum.saturating_add(i32::from(*sample));
                }
            }
        }
        write_target(&mut target, &self.mix, frames);
        probe.mark(Stage::AfterMix);

        for peer in self.peers.iter_mut().flatten() {
            peer.finish_cycle();
            peer.reset_cycle_budgets();
        }
    }

    pub fn skip(&mut self, samples: u64) {
        for peer in self.peers.iter_mut().flatten() {
            peer.pop_admit();
            peer.skip(samples);
            peer.finish_cycle();
        }
    }
}

fn clamped_i16(sample: i32) -> i16 {
    sample.clamp(i32::from(i16::MIN), i32::from(i16::MAX)) as i16
}

fn write_target(target: &mut RenderTarget<'_>, mix: &[i32; MAX_CYCLE_FRAMES], frames: usize) {
    match target {
        RenderTarget::I16 { samples, channels } => {
            write_interleaved(samples, *channels, frames, mix, |v| v)
        }
        RenderTarget::I32 { samples, channels } => {
            write_interleaved(samples, *channels, frames, mix, |v| i32::from(v) << 16)
        }
        RenderTarget::F32 { samples, channels } => {
            write_interleaved(samples, *channels, frames, mix, |v| f32::from(v) / 32768.0)
        }
    }
}

fn write_interleaved<T: Copy + Default>(
    output: &mut [T],
    channels: u16,
    frames: usize,
    mix: &[i32; MAX_CYCLE_FRAMES],
    convert: impl Fn(i16) -> T,
) {
    let channel_count = usize::from(channels);
    if channel_count == 0 {
        output.fill(T::default());
        return;
    }
    let mut chunks = output.chunks_exact_mut(channel_count);
    for (frame_index, frame) in chunks.by_ref().enumerate() {
        let value = if frame_index < frames {
            mix.get(frame_index).copied().map(clamped_i16).unwrap_or(0)
        } else {
            0
        };
        let converted = convert(value);
        frame.fill(converted);
    }
    chunks.into_remainder().fill(T::default());
}
