use mdc_rig::route::{RouteVerdict, check_route, resolve_remote};
use std::env;

const USAGE: &str = "usage: mdc-rig-route-check --remote <host.local | LAN IP>";

fn run() -> Result<(), String> {
    let mut args = env::args().skip(1);
    let mut remote = None;
    while let Some(arg) = args.next() {
        if arg != "--remote" || remote.is_some() {
            return Err(USAGE.to_owned());
        }
        let value = args
            .next()
            .filter(|value| !value.starts_with("--"))
            .ok_or_else(|| USAGE.to_owned())?;
        remote = Some(value);
    }
    let input = remote.as_deref().ok_or(USAGE).map_err(str::to_owned)?;
    let resolved = resolve_remote(input).map_err(|error| error.to_string())?;
    let mut results = Vec::with_capacity(resolved.checked.len());
    for address in resolved.checked {
        let result = check_route(address).map_err(|error| error.to_string())?;
        if result.verdict == RouteVerdict::Tailscale {
            return Err(format!(
                "{}; disable received subnet routes with `tailscale set --accept-routes=false` before measuring",
                result.reason
            ));
        }
        results.push(result);
    }

    println!("remote={input}");
    for result in results {
        println!("resolved={}", result.remote);
        println!("interface={}", result.interface);
        if let Some(description) = result.description {
            println!("description={description}");
        }
    }
    for address in resolved.ignored {
        println!("ignored={address} (ipv6 link-local)");
    }
    println!("verdict=direct");
    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
