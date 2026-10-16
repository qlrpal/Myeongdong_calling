param([switch]$NoRestore, [switch]$Integration, [switch]$Stress)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$solution = Join-Path $PSScriptRoot 'VoiceCall.slnx'
if (-not $NoRestore) {
    & dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
}
& dotnet build $solution -c Release --no-restore -warnaserror -m:1
if ($LASTEXITCODE -ne 0) { throw 'Static analysis/build failed.' }
$probe = Join-Path $PSScriptRoot 'TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe'
$checks = @('--native-converter-test', '--codec-delay-test', '--frame-timeline-test', '--latency-summary-test',
    '--acoustic-probe-test', '--ten-ms-acoustic-probe-test', '--latency-model-test', '--send-queue-test',
    '--buffer-recovery-test', '--precision-self-test', '--period-buffer-test', '--low-latency-buffer-test',
    '--playout-self-test', '--metrics-self-test', '--rtp-playout-test', '--pipeline-trace-test', '--playout-policy-test')
foreach ($check in $checks) {
    & $probe $check
    if ($LASTEXITCODE -ne 0) { throw "Check failed: $check" }
}
if ($Stress) {
    & $probe --rtp-stress-test (Join-Path $PSScriptRoot '.local/reports/rtp-stress-verify.json')
    if ($LASTEXITCODE -ne 0) { throw 'RTP clock/backlog stress checks failed.' }
    & $probe --playout-target-test (Join-Path $PSScriptRoot '.local/reports/playout-target-simulation.json')
    if ($LASTEXITCODE -ne 0) { throw 'Playout target comparison failed.' }
}
& $probe --opus-comparison (Join-Path $PSScriptRoot '.local/reports/opus-comparison-verify.json')
if ($LASTEXITCODE -ne 0) { throw 'Opus comparison failed.' }
$server = Join-Path $PSScriptRoot 'server'
foreach ($file in (Get-ChildItem -LiteralPath $server -Filter '*.js' -Recurse)) {
    & node --check $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "JavaScript syntax failed: $($file.Name)" }
}
Push-Location $server
try {
    & node --test
    if ($LASTEXITCODE -ne 0) { throw 'Server regression tests failed.' }
}
finally { Pop-Location }
if ($Integration) {
    foreach ($check in @('--lan-self-test', '--ten-ms-rtcp-integration', '--rtp-playout-integration')) {
        & $probe $check
        if ($LASTEXITCODE -ne 0) { throw "Integration failed: $check" }
    }
}
Write-Output 'PASS: static analysis and regression checks completed.'
