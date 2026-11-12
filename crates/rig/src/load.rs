//! Platform-independent CPU load generation for measurement runs.

use std::fmt;
use std::path::PathBuf;
use std::sync::{Condvar, Mutex};
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Priority {
    Idle,
    Lowest,
    BelowNormal,
    Normal,
    AboveNormal,
    Highest,
}

impl fmt::Display for Priority {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::Idle => "idle",
            Self::Lowest => "lowest",
            Self::BelowNormal => "below-normal",
            Self::Normal => "normal",
            Self::AboveNormal => "above-normal",
            Self::Highest => "highest",
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Options {
    pub threads: usize,
    pub duty_percent: u32,
    pub seconds: u64,
    pub priority: Priority,
    pub period_ms: u64,
    pub out: Option<PathBuf>,
}

impl Options {
    pub fn load_condition(&self) -> String {
        format!(
            "threads={};duty_percent={};priority={};period_ms={};seconds={}",
            self.threads, self.duty_percent, self.priority, self.period_ms, self.seconds
        )
    }
}

pub const USAGE: &str = "usage: mdc-rig-load --threads N --duty PERCENT --seconds S [--priority P] [--period-ms M] [--out FILE]";

pub fn parse_args<I, S>(args: I) -> Result<Options, String>
where
    I: IntoIterator<Item = S>,
    S: Into<String>,
{
    let mut threads = None;
    let mut duty = None;
    let mut seconds = None;
    let mut priority = None;
    let mut period_ms = None;
    let mut out = None;
    let mut args = args.into_iter().map(Into::into);

    while let Some(name) = args.next() {
        let value = args
            .next()
            .ok_or_else(|| format!("missing value for {name}"))?;
        match name.as_str() {
            "--threads" => set_once(&mut threads, parse_range(&value, 1, 256, &name)?, &name)?,
            "--duty" => set_once(&mut duty, parse_range(&value, 1, 100, &name)?, &name)?,
            "--seconds" => set_once(&mut seconds, parse_range(&value, 1, 3600, &name)?, &name)?,
            "--period-ms" => set_once(&mut period_ms, parse_range(&value, 1, 1000, &name)?, &name)?,
            "--priority" => set_once(&mut priority, parse_priority(&value)?, &name)?,
            "--out" => set_once(&mut out, PathBuf::from(value), &name)?,
            _ => return Err(format!("unknown argument: {name}")),
        }
    }

    Ok(Options {
        threads: threads.ok_or_else(|| "missing required argument: --threads".to_owned())?,
        duty_percent: duty.ok_or_else(|| "missing required argument: --duty".to_owned())?,
        seconds: seconds.ok_or_else(|| "missing required argument: --seconds".to_owned())?,
        priority: priority.unwrap_or(Priority::Normal),
        period_ms: period_ms.unwrap_or(10),
        out,
    })
}

fn set_once<T>(slot: &mut Option<T>, value: T, name: &str) -> Result<(), String> {
    if slot.is_some() {
        return Err(format!("duplicate argument: {name}"));
    }
    *slot = Some(value);
    Ok(())
}

fn parse_range<T>(value: &str, min: T, max: T, name: &str) -> Result<T, String>
where
    T: std::str::FromStr + PartialOrd + Copy + fmt::Display,
{
    let parsed = value
        .parse::<T>()
        .map_err(|_| format!("invalid value for {name}: {value}"))?;
    if parsed < min || parsed > max {
        return Err(format!("{name} must be in {min}..={max}"));
    }
    Ok(parsed)
}

fn parse_priority(value: &str) -> Result<Priority, String> {
    match value {
        "idle" => Ok(Priority::Idle),
        "lowest" => Ok(Priority::Lowest),
        "below-normal" => Ok(Priority::BelowNormal),
        "normal" => Ok(Priority::Normal),
        "above-normal" => Ok(Priority::AboveNormal),
        "highest" => Ok(Priority::Highest),
        "time-critical" => Err("priority time-critical is not supported".to_owned()),
        _ => Err(format!("invalid priority: {value}")),
    }
}

pub fn period_parts(duty_percent: u32, period: Duration) -> (Duration, Duration) {
    let busy = period.mul_f64(f64::from(duty_percent) / 100.0);
    (busy, period.saturating_sub(busy))
}

#[derive(Clone, Copy, Debug)]
pub struct ThreadResult {
    pub busy: Duration,
    pub elapsed: Duration,
}

impl ThreadResult {
    pub fn achieved_duty_percent(self) -> f64 {
        100.0 * self.busy.as_secs_f64() / self.elapsed.as_secs_f64()
    }
}

pub fn run_load<F>(
    options: &Options,
    duration: Duration,
    set_priority: F,
) -> Result<Vec<ThreadResult>, String>
where
    F: Fn(Priority) -> Result<(), String> + Sync,
{
    let start_gate = (Mutex::new(false), Condvar::new());
    let period = Duration::from_millis(options.period_ms);
    let (busy_target, sleep_target) = period_parts(options.duty_percent, period);
    let mut results = Vec::with_capacity(options.threads);

    std::thread::scope(|scope| -> Result<(), String> {
        let mut handles = Vec::with_capacity(options.threads);
        for index in 0..options.threads {
            let builder = std::thread::Builder::new().name(format!("mdc-rig-load-{index}"));
            let handle = match builder.spawn_scoped(scope, || -> Result<ThreadResult, String> {
                let priority_result = set_priority(options.priority);
                let (lock, ready) = &start_gate;
                let mut started = lock
                    .lock()
                    .map_err(|_| "start gate mutex poisoned".to_owned())?;
                while !*started {
                    started = ready
                        .wait(started)
                        .map_err(|_| "start gate mutex poisoned".to_owned())?;
                }
                drop(started);
                priority_result?;
                let started = Instant::now();
                let deadline = started + duration;
                let mut busy_total = Duration::ZERO;
                while Instant::now() < deadline {
                    let busy_started = Instant::now();
                    let busy_deadline = (busy_started + busy_target).min(deadline);
                    while Instant::now() < busy_deadline {
                        std::hint::spin_loop();
                    }
                    busy_total += Instant::now().saturating_duration_since(busy_started);
                    if !sleep_target.is_zero() && Instant::now() < deadline {
                        std::thread::sleep(
                            sleep_target.min(deadline.saturating_duration_since(Instant::now())),
                        );
                    }
                }
                Ok(ThreadResult {
                    busy: busy_total.min(started.elapsed()),
                    elapsed: started.elapsed(),
                })
            }) {
                Ok(handle) => handle,
                Err(error) => {
                    let (lock, ready) = &start_gate;
                    if let Ok(mut started) = lock.lock() {
                        *started = true;
                        ready.notify_all();
                    }
                    return Err(format!("failed to spawn worker {index}: {error}"));
                }
            };
            handles.push(handle);
        }
        {
            let (lock, ready) = &start_gate;
            let mut started = lock
                .lock()
                .map_err(|_| "start gate mutex poisoned".to_owned())?;
            *started = true;
            ready.notify_all();
        }
        for handle in handles {
            let result = handle
                .join()
                .map_err(|_| "load worker panicked".to_owned())??;
            results.push(result);
        }
        Ok(())
    })?;
    Ok(results)
}
