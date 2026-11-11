use std::{
    error::Error,
    fmt,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, ToSocketAddrs},
    process::Command,
};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RouteError(String);

impl RouteError {
    fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

impl fmt::Display for RouteError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl Error for RouteError {}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RouteVerdict {
    Direct,
    Tailscale,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RouteResult {
    pub remote: IpAddr,
    pub interface: String,
    pub description: Option<String>,
    pub verdict: RouteVerdict,
    pub reason: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RouteInterface {
    pub name: String,
    pub description: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedRemote {
    pub checked: Vec<IpAddr>,
    pub ignored: Vec<IpAddr>,
}

pub fn resolve_remote(value: &str) -> Result<ResolvedRemote, RouteError> {
    if let Ok(address) = value.parse::<IpAddr>() {
        return validate_resolved_remote(value, &[address]);
    }

    let addresses: Vec<_> = (value, 0)
        .to_socket_addrs()
        .map_err(|error| RouteError::new(format!("failed to resolve {value}: {error}")))?
        .map(|address| address.ip())
        .collect();
    validate_resolved_remote(value, &addresses)
}

pub fn validate_resolved_remote(
    original_name: &str,
    addresses: &[IpAddr],
) -> Result<ResolvedRemote, RouteError> {
    let mut checked = Vec::new();
    let mut ignored = Vec::new();

    for &address in addresses {
        let destination = if is_ipv6_link_local(address) {
            &mut ignored
        } else {
            &mut checked
        };
        if !destination.contains(&address) {
            destination.push(address);
        }
    }

    for &address in &checked {
        if address.is_loopback() || address.is_unspecified() {
            return Err(RouteError::new(format!(
                "remote {original_name} resolved to loopback or unspecified address {address}; specify the peer's .local name or LAN IP"
            )));
        }
        if is_tailscale_address(address) {
            return Err(RouteError::new(format!(
                "remote {original_name} resolved to Tailscale address {address}; specify the peer's .local name or LAN IP"
            )));
        }
    }

    if checked.is_empty() {
        return Err(RouteError::new(format!(
            "remote {original_name} resolved to no usable addresses"
        )));
    }

    Ok(ResolvedRemote { checked, ignored })
}

pub fn validate_remote(value: &str) -> Result<IpAddr, RouteError> {
    let address = value
        .parse::<IpAddr>()
        .map_err(|_| RouteError::new(format!("invalid IP address: {value}")))?;
    if address.is_loopback() || address.is_unspecified() {
        return Err(RouteError::new(format!(
            "remote address {address} is loopback or unspecified; specify the peer's LAN IP"
        )));
    }
    if is_tailscale_address(address) {
        return Err(RouteError::new(format!(
            "remote address {address} is a Tailscale address; specify the peer's LAN IP"
        )));
    }
    Ok(address)
}

fn is_ipv6_link_local(address: IpAddr) -> bool {
    matches!(address, IpAddr::V6(address) if address.segments()[0] & 0xffc0 == 0xfe80)
}

fn is_tailscale_address(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(address) => {
            let value = u32::from(address);
            let base = u32::from(Ipv4Addr::new(100, 64, 0, 0));
            value & 0xffc0_0000 == base
        }
        IpAddr::V6(address) => {
            let value = u128::from(address);
            let base = u128::from(Ipv6Addr::new(0xfd7a, 0x115c, 0xa1e0, 0, 0, 0, 0, 0));
            value >> 80 == base >> 80
        }
    }
}

pub fn parse_windows_route(output: &str) -> Result<RouteInterface, RouteError> {
    if output.trim().is_empty() {
        return Err(RouteError::new("Windows route query returned empty output"));
    }
    let mut alias = None;
    let mut index = None;
    let mut description = None;
    for line in output.lines() {
        let Some((key, value)) = line.split_once('=') else {
            return Err(RouteError::new("unexpected Windows route query output"));
        };
        let value = value.trim();
        match key.trim() {
            "alias" if !value.is_empty() => alias = Some(value.to_owned()),
            "index" if value.parse::<u32>().is_ok() => index = Some(value),
            "description" if !value.is_empty() => description = Some(value.to_owned()),
            "description" => {}
            _ => return Err(RouteError::new("unexpected Windows route query output")),
        }
    }
    if index.is_none() {
        return Err(RouteError::new(
            "Windows route query output has no valid interface index",
        ));
    }
    Ok(RouteInterface {
        name: alias.ok_or_else(|| RouteError::new("Windows route query output has no alias"))?,
        description,
    })
}

pub fn parse_linux_route(output: &str) -> Result<RouteInterface, RouteError> {
    if output.trim().is_empty() {
        return Err(RouteError::new("Linux route query returned empty output"));
    }
    let words: Vec<_> = output.split_whitespace().collect();
    let name = words
        .windows(2)
        .find_map(|pair| (pair[0] == "dev").then_some(pair[1]))
        .filter(|name| !name.is_empty())
        .ok_or_else(|| RouteError::new("Linux route query output has no dev interface"))?;
    Ok(RouteInterface {
        name: name.to_owned(),
        description: None,
    })
}

pub fn classify_route(remote: IpAddr, interface: RouteInterface) -> RouteResult {
    let tailscale = contains_tailscale(&interface.name)
        || interface
            .description
            .as_deref()
            .is_some_and(contains_tailscale);
    let verdict = if tailscale {
        RouteVerdict::Tailscale
    } else {
        RouteVerdict::Direct
    };
    let reason = if tailscale {
        format!(
            "route to {remote} uses Tailscale interface {}{}",
            interface.name,
            interface
                .description
                .as_deref()
                .map(|value| format!(" ({value})"))
                .unwrap_or_default()
        )
    } else {
        format!(
            "route to {remote} uses non-Tailscale interface {}",
            interface.name
        )
    };
    RouteResult {
        remote,
        interface: interface.name,
        description: interface.description,
        verdict,
        reason,
    }
}

fn contains_tailscale(value: &str) -> bool {
    value.to_ascii_lowercase().contains("tailscale")
}

pub fn check_route(remote: IpAddr) -> Result<RouteResult, RouteError> {
    let interface = query_route(remote)?;
    Ok(classify_route(remote, interface))
}

#[cfg(windows)]
fn query_route(remote: IpAddr) -> Result<RouteInterface, RouteError> {
    let remote = remote.to_string();
    let script = format!(
        "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); \
         $route=Find-NetRoute -RemoteIPAddress {remote} | Select-Object -First 1; \
         if ($null -eq $route) {{ throw 'route not found' }}; \
         $adapter=Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -IncludeHidden -ErrorAction SilentlyContinue; \
         'alias=' + $route.InterfaceAlias; 'index=' + $route.InterfaceIndex; \
         if ($null -ne $adapter) {{ 'description=' + $adapter.InterfaceDescription }} else {{ 'description=' }}"
    );
    let output = Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .output()
        .map_err(|error| RouteError::new(format!("failed to run Windows route query: {error}")))?;
    let output = command_output(output, "Windows route query")?;
    parse_windows_route(&output)
}

#[cfg(target_os = "linux")]
fn query_route(remote: IpAddr) -> Result<RouteInterface, RouteError> {
    let output = Command::new("ip")
        .args(["route", "get", &remote.to_string()])
        .output()
        .map_err(|error| RouteError::new(format!("failed to run Linux route query: {error}")))?;
    let output = command_output(output, "Linux route query")?;
    parse_linux_route(&output)
}

#[cfg(not(any(windows, target_os = "linux")))]
fn query_route(_remote: IpAddr) -> Result<RouteInterface, RouteError> {
    Err(RouteError::new(
        "route lookup is supported only on Windows and Linux",
    ))
}

#[cfg(any(windows, target_os = "linux"))]
fn command_output(output: std::process::Output, label: &str) -> Result<String, RouteError> {
    if !output.status.success() {
        return Err(RouteError::new(format!(
            "{label} failed with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )));
    }
    String::from_utf8(output.stdout)
        .map_err(|error| RouteError::new(format!("{label} returned non-UTF-8 output: {error}")))
}
