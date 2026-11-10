//! Bounded, single-producer/single-consumer value queue. No cursor is shared for writing.
use std::{
    cell::{Cell, UnsafeCell},
    marker::PhantomData,
    mem::MaybeUninit,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
};

struct Ring<T: Copy> {
    slots: Box<[UnsafeCell<MaybeUninit<T>>]>,
    mask: usize,
    write: AtomicUsize,
    read: AtomicUsize,
}
// SAFETY: the unique producer writes only unpublished slots; the unique consumer
// reads only published slots. Release/Acquire hands off slot access in both directions.
unsafe impl<T: Copy + Send> Sync for Ring<T> {}

pub struct Producer<T: Copy> {
    ring: Arc<Ring<T>>,
    unique: PhantomData<Cell<()>>,
}
pub struct Consumer<T: Copy> {
    ring: Arc<Ring<T>>,
    unique: PhantomData<Cell<()>>,
}

/// Allocates and touches every slot once. Invalid capacities return None.
pub fn channel<T: Copy>(capacity: usize) -> Option<(Producer<T>, Consumer<T>)> {
    channel_at(capacity, 0)
}
fn channel_at<T: Copy>(capacity: usize, cursor: usize) -> Option<(Producer<T>, Consumer<T>)> {
    if !capacity.is_power_of_two() || capacity > isize::MAX as usize {
        return None;
    }
    let slots = (0..capacity)
        .map(|_| UnsafeCell::new(MaybeUninit::zeroed()))
        .collect::<Vec<_>>()
        .into_boxed_slice();
    // Volatile writes force physical access even when the allocator supplies
    // lazily zeroed pages. MaybeUninit permits zero bytes for every Copy T.
    for slot in &slots {
        // SAFETY: private allocation, valid aligned pointer; no handles exist yet.
        unsafe {
            slot.get().write_volatile(MaybeUninit::zeroed());
        }
    }
    let ring = Arc::new(Ring {
        slots,
        mask: capacity.wrapping_sub(1),
        write: AtomicUsize::new(cursor),
        read: AtomicUsize::new(cursor),
    });
    Some((
        Producer {
            ring: Arc::clone(&ring),
            unique: PhantomData,
        },
        Consumer {
            ring,
            unique: PhantomData,
        },
    ))
}
impl<T: Copy> Producer<T> {
    /// Full means discard this new value; never advances the consumer cursor.
    pub fn push(&mut self, value: T) -> bool {
        let w = self.ring.write.load(Ordering::Relaxed);
        let r = self.ring.read.load(Ordering::Acquire);
        if w.wrapping_sub(r) >= self.ring.slots.len() {
            return false;
        }
        let Some(slot) = self.ring.slots.get(w & self.ring.mask) else {
            return false;
        };
        // SAFETY: w is an unoccupied slot owned exclusively by this producer.
        unsafe {
            (*slot.get()).write(value);
        }
        self.ring.write.store(w.wrapping_add(1), Ordering::Release);
        true
    }
}
impl<T: Copy> Consumer<T> {
    pub fn pop(&mut self) -> Option<T> {
        let r = self.ring.read.load(Ordering::Relaxed);
        if r == self.ring.write.load(Ordering::Acquire) {
            return None;
        }
        let slot = self.ring.slots.get(r & self.ring.mask)?;
        // SAFETY: Acquire observed publication of this initialized slot; only
        // this consumer reads it until the Release below permits producer reuse.
        let value = unsafe { (*slot.get()).assume_init_read() };
        self.ring.read.store(r.wrapping_add(1), Ordering::Release);
        Some(value)
    }
}
#[cfg(test)]
mod tests {
    #[test]
    fn integer_cursor_wrap() {
        let (mut p, mut c) = super::channel_at(4, usize::MAX - 1).unwrap();
        for i in 0..100 {
            assert!(p.push(i));
            assert_eq!(c.pop(), Some(i));
        }
        for i in 0..4 {
            assert!(p.push(i));
        }
        assert!(!p.push(99));
        for i in 0..4 {
            assert_eq!(c.pop(), Some(i));
        }
        assert_eq!(c.pop(), None);
    }
}
