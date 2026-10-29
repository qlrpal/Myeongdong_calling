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
$buildTools = Join-Path $PSScriptRoot 'BuildTools/bin/Release/net10.0/BuildTools.exe'
& $buildTools self-test
if ($LASTEXITCODE -ne 0) { throw 'BuildTools regression checks failed.' }
$probe = Join-Path $PSScriptRoot 'TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe'
& $probe --sdk-build-check
if ($LASTEXITCODE -ne 0) { throw 'Default runtime did not load the production patched SDK.' }
$checks = @('--extreme-checks', '--native-converter-test', '--codec-delay-test', '--frame-timeline-test', '--latency-summary-test',
    '--acoustic-probe-test', '--ten-ms-acoustic-probe-test', '--speech-fixture-test', '--latency-model-test', '--send-queue-test',
    '--buffer-recovery-test', '--gap-restart-test', '--precision-self-test', '--period-buffer-test', '--low-latency-buffer-test',
    '--playout-self-test', '--metrics-self-test', '--rtp-playout-test', '--pipeline-trace-test', '--playout-policy-test', '--capture-period-test', '--render-target-test', '--render-path-test', '--codec-render-reference-test')
foreach ($check in $checks) {
    & $probe $check
    if ($LASTEXITCODE -ne 0) { throw "Check failed: $check" }
}
& $probe --product-checks (Join-Path $PSScriptRoot '.local/reports/product-checks')
if ($LASTEXITCODE -ne 0) { throw 'Product controls/settings/reconnection checks failed.' }
& $probe --ui-reading-checks (Join-Path $PSScriptRoot '.local/reports/ui-reading')
if ($LASTEXITCODE -ne 0) { throw 'UI reading-position checks failed.' }
if ($Stress) {
    & $probe --fractional-network-checks (Join-Path $PSScriptRoot '.local/reports/adaptive-network-verify.json')
    if ($LASTEXITCODE -ne 0) { throw 'Adaptive frame/network recovery checks failed.' }
    & $probe --rtp-stress-test (Join-Path $PSScriptRoot '.local/reports/rtp-stress-verify.json')
    if ($LASTEXITCODE -ne 0) { throw 'RTP clock/backlog stress checks failed.' }
    & $probe --playout-target-test (Join-Path $PSScriptRoot '.local/reports/playout-target-simulation.json')
    if ($LASTEXITCODE -ne 0) { throw 'Playout target comparison failed.' }
}
& $probe --opus-comparison (Join-Path $PSScriptRoot '.local/reports/opus-comparison-verify.json')
if ($LASTEXITCODE -ne 0) { throw 'Opus comparison failed.' }
& $probe --recovery-gap-test (Join-Path $PSScriptRoot '.local/reports/recovery-gap-verify.json')
if ($LASTEXITCODE -ne 0) { throw 'Recovery gap comparison failed.' }
& $probe --recovery-device-period-test (Join-Path $PSScriptRoot '.local/reports/recovery-device-periods-verify.json')
if ($LASTEXITCODE -ne 0) { throw 'Recovery device-period checks failed.' }
& $probe --recovery-policy-test (Join-Path $PSScriptRoot '.local/reports/recovery-policy-verify.json')
if ($LASTEXITCODE -ne 0) { throw 'Responsive recovery policy checks failed.' }
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
& node --test (Join-Path $PSScriptRoot 'TransportProbe/Runner/check-runner.test.mjs') (Join-Path $PSScriptRoot 'TransportProbe/Runner/pair-summary.test.mjs') (Join-Path $PSScriptRoot 'TransportProbe/Runner/pair-observation.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'App check runner regression tests failed.' }
if ($Integration) {
    foreach ($check in @('--lan-self-test', '--ten-ms-rtcp-integration', '--rtp-playout-integration', '--variable-frame-integration', '--ipv6-loopback-integration')) {
        & $probe $check
        if ($LASTEXITCODE -ne 0) { throw "Integration failed: $check" }
    }
}
Write-Output 'PASS: static analysis and regression checks completed.'
