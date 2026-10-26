param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$Report
)
$ErrorActionPreference = 'Stop'
$program = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Executable))
if (-not (Test-Path -LiteralPath $program -PathType Leaf)) { throw 'Executable does not exist.' }
$rules = @()
$unreadable = @()
$stores = @(
    'SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules',
    'SOFTWARE\Policies\Microsoft\WindowsFirewall\FirewallRules'
)
foreach ($store in $stores) {
    $key = $null
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($store)
        if ($null -eq $key) { continue }
        foreach ($name in $key.GetValueNames()) {
            $fields = @{}
            foreach ($part in ([string]$key.GetValue($name)).Split('|')) {
                $at = $part.IndexOf('=')
                if ($at -gt 0) {
                    $field = $part.Substring(0, $at)
                    $value = $part.Substring($at + 1)
                    if ($fields.ContainsKey($field)) { $fields[$field] += ',' + $value }
                    else { $fields[$field] = $value }
                }
            }
            if (-not $fields.App) { continue }
            $app = [Environment]::ExpandEnvironmentVariables($fields.App)
            if (-not [string]::Equals($app, $program, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($fields.Active -ne 'TRUE' -or $fields.Dir -ne 'In') { continue }
            if ($fields.Protocol -and $fields.Protocol -notin @('17', '256')) { continue }
            $rules += [pscustomobject]@{
                store = $store; ruleId = $name; action = $fields.Action
                profiles = $fields.Profile; protocol = $fields.Protocol
                localPorts = $fields.LPort; remotePorts = $fields.RPort
                localIPv4 = $fields.LA4; remoteIPv4 = $fields.RA4
                localIPv6 = $fields.LA6; remoteIPv6 = $fields.RA6
                interfaceTypes = $fields.IFType; service = $fields.Svc
            }
        }
    }
    catch [System.Security.SecurityException] { $unreadable += $store }
    catch [System.UnauthorizedAccessException] { $unreadable += $store }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
$result = [pscustomobject]@{
    executable = $program
    matchingActiveInboundUdpRules = @($rules)
    explicitBlockRules = @($rules | Where-Object action -eq 'Block').Count
    unreadableStores = @($unreadable)
    limits = 'Read-only per-program rule inventory, not an effective-policy or connectivity verdict. Check listed profile, address, port and service conditions. General rules, third-party filters, NAT and runtime prompts are outside this inventory. No matching allow rule does not prove blocking. Rules and network settings are never changed.'
}
$json = $result | ConvertTo-Json -Depth 6
if ($Report) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Report), $json, [Text.UTF8Encoding]::new($false))
}
$json
