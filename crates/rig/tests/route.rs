use mdc_rig::route::{
    RouteInterface, RouteVerdict, classify_route, parse_linux_route, parse_windows_route,
    validate_remote, validate_resolved_remote,
};
use std::{net::IpAddr, process::Command};

#[test]
fn parses_windows_route_output() {
    let route =
        parse_windows_route("alias=Tailscale\nindex=12\ndescription=Tailscale Tunnel\n").unwrap();
    assert_eq!(route.name, "Tailscale");
    assert_eq!(route.description.as_deref(), Some("Tailscale Tunnel"));

    let route = parse_windows_route("alias=이더넷\nindex=4\ndescription=Realtek Gaming\n").unwrap();
    assert_eq!(route.name, "이더넷");
    assert!(parse_windows_route("").is_err());
    assert!(parse_windows_route("InterfaceAlias=Ethernet\n").is_err());
}

#[test]
fn parses_linux_route_output() {
    assert_eq!(
        parse_linux_route("172.30.1.37 dev eth0 src 172.30.1.10 uid 1000\n")
            .unwrap()
            .name,
        "eth0"
    );
    assert_eq!(
        parse_linux_route("172.30.1.37 dev tailscale0 table 52 src 100.84.1.41\n")
            .unwrap()
            .name,
        "tailscale0"
    );
    assert!(parse_linux_route("").is_err());
    assert!(parse_linux_route("172.30.1.37 via 172.30.1.1").is_err());
}

#[test]
fn classifies_tailscale_interfaces() {
    let remote: IpAddr = "172.30.1.37".parse().unwrap();
    for (name, description) in [
        ("Tailscale", None),
        ("tailscale0", None),
        ("이더넷", Some("Tailscale Tunnel")),
    ] {
        assert_eq!(
            classify_route(
                remote,
                RouteInterface {
                    name: name.into(),
                    description: description.map(str::to_owned),
                }
            )
            .verdict,
            RouteVerdict::Tailscale
        );
    }
    for name in ["이더넷", "eth0", "Wi-Fi"] {
        assert_eq!(
            classify_route(
                remote,
                RouteInterface {
                    name: name.into(),
                    description: None,
                }
            )
            .verdict,
            RouteVerdict::Direct
        );
    }
}

#[test]
fn validates_remote_addresses() {
    for address in ["100.84.1.41", "fd7a:115c:a1e0::1", "127.0.0.1", "0.0.0.0"] {
        assert!(validate_remote(address).is_err(), "{address}");
    }
    for address in ["100.63.255.255", "100.128.0.0", "172.30.1.37"] {
        assert!(validate_remote(address).is_ok(), "{address}");
    }
}

#[test]
fn validates_resolved_remote_sets() {
    let lan: IpAddr = "172.30.1.37".parse().unwrap();
    let link_local: IpAddr = "fe80::1".parse().unwrap();
    let tailscale: IpAddr = "100.84.1.41".parse().unwrap();

    let resolved = validate_resolved_remote("peer.local", &[lan, link_local]).unwrap();
    assert_eq!(resolved.checked, vec![lan]);
    assert_eq!(resolved.ignored, vec![link_local]);

    for addresses in [vec![tailscale], vec![lan, tailscale]] {
        let error = validate_resolved_remote("peer.local", &addresses).unwrap_err();
        assert!(error.to_string().contains("peer.local"));
        assert!(error.to_string().contains("100.84.1.41"));
    }
    assert!(validate_resolved_remote("peer.local", &[link_local]).is_err());
    assert!(validate_resolved_remote("peer.local", &[]).is_err());
    assert!(validate_resolved_remote("peer.local", &["127.0.0.1".parse().unwrap()]).is_err());

    let resolved =
        validate_resolved_remote("peer.local", &[lan, lan, link_local, link_local]).unwrap();
    assert_eq!(resolved.checked, vec![lan]);
    assert_eq!(resolved.ignored, vec![link_local]);
}

fn call(args: &[&str]) -> std::process::Output {
    Command::new(env!("CARGO_BIN_EXE_mdc-rig-route-check"))
        .args(args)
        .output()
        .unwrap()
}

#[test]
fn cli_rejects_invalid_arguments_before_route_lookup() {
    for args in [Vec::<&str>::new(), vec!["--remote", "100.84.1.41"]] {
        assert!(!call(&args).status.success());
    }
}

#[test]
fn cli_rejects_unresolvable_name() {
    assert!(
        !call(&["--remote", "no-such-host-mdc.invalid"])
            .status
            .success()
    );
}
