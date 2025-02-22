use std::sync::atomic::{AtomicUsize, Ordering};

#[derive(Default)]
pub struct Metrics {
    pub found: AtomicUsize,
    pub already_saw: AtomicUsize,
}

impl Metrics {
    pub fn new() -> Self {
        Self {
            found: AtomicUsize::new(0),
            already_saw: AtomicUsize::new(0),
        }
    }

    pub fn increment_found(&self, count: usize) {
        self.found.fetch_add(count, Ordering::Relaxed);
    }

    pub fn increment_already_seen(&self) {
        self.already_saw.fetch_add(1, Ordering::Relaxed);
    }
}
