param(
    [switch]$ValidateOnly,
    [ValidateRange(10, 180)][int]$Seconds = 125,
    [string]$OutputPath = (Join-Path $PSScriptRoot ".local/reports/network-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss')).etl")
)
$ErrorActionPreference = 'Stop'
# This provider records network metadata, not packet bytes. Fail closed if its schema changes.
$providerName = 'Microsoft-Windows-Kernel-Network'
$provider = Get-WinEvent -ListProvider $providerName
$allowed = @('PID', 'size', 'daddr', 'saddr', 'dport', 'sport', 'startime', 'endtime',
    'seqnum', 'connid', 'mss', 'sackopt', 'tsopt', 'wsopt', 'rcvwin', 'rcvwinscale',
    'sndwinscale', 'Proto', 'FailureCode')
foreach ($event in $provider.Events) {
    foreach ($field in ([xml]$event.Template).template.data) {
        if ($field.name -notin $allowed) { throw "Unreviewed provider field: $($field.name). Trace cancelled." }
    }
}
if ($ValidateOnly) { Write-Output 'PASS: metadata-only provider schema; no trace started.'; return }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows ETW requires an administrator PowerShell. No trace was started.'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw "Output exists: $OutputPath" }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$session = 'VoiceNative-Net-' + [Guid]::NewGuid().ToString('N')
$started = $false
$beforeQpc = [Diagnostics.Stopwatch]::GetTimestamp() / [double][Diagnostics.Stopwatch]::Frequency
$utc = [DateTimeOffset]::UtcNow
$afterQpc = [Diagnostics.Stopwatch]::GetTimestamp() / [double][Diagnostics.Stopwatch]::Frequency
try {
    & logman create trace $session -p $providerName 0x30 4 -o $OutputPath -f bincirc -max 32 -bs 64 -nb 4 16 -ets
    if ($LASTEXITCODE -ne 0) { throw 'ETW session start failed.' }
    $started = $true
    [ordered]@{session=$session;output=$OutputPath;qpcStarted=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency}|ConvertTo-Json|Set-Content -LiteralPath ($OutputPath+'.active.json') -Encoding utf8
    Write-Output "Network metadata only, $Seconds seconds, maximum 32MiB. IP/ports/PIDs of other network activity may be included."
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) { Start-Sleep -Milliseconds 250 }
}
finally {
    if ($started) {
        & logman stop $session -ets
        $stopExit = $LASTEXITCODE
        [ordered]@{
            provider = $providerName
            session = $session
            seconds = $Seconds
            utc = $utc.ToString('O')
            qpcBefore = $beforeQpc
            qpcAfter = $afterQpc
            stopExitCode = $stopExit
            note = 'Metadata-only OS network events; not NIC hardware arrival timestamps. Prefer tracerpt -rts raw QPC timestamps. UTC/QPC anchor brackets only call duration, not UTC API precision. Correlate endpoint/PID/size and intervals; no RTP timestamp or payload is captured. Circular trace may wrap: verify completeness before claiming delay.'
        } | ConvertTo-Json | Set-Content -LiteralPath ($OutputPath + '.json') -Encoding utf8
        if ($stopExit -ne 0) { Write-Warning "ETW stop failed; stop own session with: logman stop $session -ets" }
    }
}
Write-Output "Saved: $OutputPath"
