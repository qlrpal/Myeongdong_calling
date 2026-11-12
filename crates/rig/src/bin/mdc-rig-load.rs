use mdc_rig::load::{Options, Priority, ThreadResult, USAGE, parse_args, run_load};
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

fn main() {
    let options = match parse_args(std::env::args().skip(1)) {
        Ok(options) => options,
        Err(error) => {
            eprintln!("error: {error}\n{USAGE}");
            std::process::exit(2);
        }
    };
    if let Err(error) = execute(&options) {
        eprintln!("error: {error}");
        std::process::exit(1);
    }
}

fn execute(options: &Options) -> Result<(), String> {
    let mut out = options
        .out
        .as_ref()
        .map(|path| {
            OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(path)
                .map_err(|error| format!("failed to create {}: {error}", path.display()))
        })
        .transpose()?;
    let started_unix_ms = unix_ms()?;
    let started = std::time::Instant::now();
    let initial = format!(
        "tool=mdc-rig-load\ncommit={}\nload_condition={}\nlogical_cpus={}\nstarted_unix_ms={}\n",
        mdc_rig::analyzer_commit_hash(),
        options.load_condition(),
        std::thread::available_parallelism()
            .map(|count| count.get().to_string())
            .unwrap_or_else(|_| "unknown".to_owned()),
        started_unix_ms
    );
    write_output(&initial, &mut out, true)?;
    let results = run_load(
        options,
        Duration::from_secs(options.seconds),
        set_thread_priority,
    )?;
    let ended_unix_ms = unix_ms()?;
    let elapsed_ms = started.elapsed().as_millis();
    let (min, mean, max) = duty_stats(&results);
    let final_output = format!(
        "ended_unix_ms={ended_unix_ms}\nelapsed_ms={elapsed_ms}\nachieved_duty_percent.min={min:.1}\nachieved_duty_percent.mean={mean:.1}\nachieved_duty_percent.max={max:.1}\npriority_applied={}\n",
        options.priority
    );
    write_output(&final_output, &mut out, true)
}

fn write_output(text: &str, file: &mut Option<File>, flush: bool) -> Result<(), String> {
    let mut stdout = io::stdout().lock();
    stdout
        .write_all(text.as_bytes())
        .and_then(|()| if flush { stdout.flush() } else { Ok(()) })
        .map_err(|error| format!("failed to write stdout: {error}"))?;
    if let Some(file) = file {
        file.write_all(text.as_bytes())
            .and_then(|()| if flush { file.flush() } else { Ok(()) })
            .map_err(|error| format!("failed to write output file: {error}"))?;
    }
    Ok(())
}

fn unix_ms() -> Result<u128, String> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis())
        .map_err(|error| format!("system time is before Unix epoch: {error}"))
}

fn duty_stats(results: &[ThreadResult]) -> (f64, f64, f64) {
    let mut min = f64::INFINITY;
    let mut max = f64::NEG_INFINITY;
    let mut sum = 0.0;
    for result in results {
        let duty = result.achieved_duty_percent();
        min = min.min(duty);
        max = max.max(duty);
        sum += duty;
    }
    (min, sum / results.len() as f64, max)
}

#[cfg(windows)]
fn set_thread_priority(priority: Priority) -> Result<(), String> {
    type Handle = *mut core::ffi::c_void;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentThread() -> Handle;
        fn SetThreadPriority(thread: Handle, priority: i32) -> i32;
        fn GetLastError() -> u32;
    }
    let value = match priority {
        Priority::Idle => -15,
        Priority::Lowest => -2,
        Priority::BelowNormal => -1,
        Priority::Normal => 0,
        Priority::AboveNormal => 1,
        Priority::Highest => 2,
    };
    // SAFETY: GetCurrentThread returns a pseudo-handle valid for the calling thread;
    // SetThreadPriority accepts that handle and a documented priority constant.
    if unsafe { SetThreadPriority(GetCurrentThread(), value) } == 0 {
        // SAFETY: GetLastError has no preconditions and is called immediately after failure.
        return Err(format!("SetThreadPriority failed: {}", unsafe {
            GetLastError()
        }));
    }
    Ok(())
}

#[cfg(not(windows))]
fn set_thread_priority(priority: Priority) -> Result<(), String> {
    if priority == Priority::Normal {
        Ok(())
    } else {
        Err(format!("priority {priority} unsupported on this platform"))
    }
}
