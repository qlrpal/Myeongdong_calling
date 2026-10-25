param(
    [Parameter(Mandatory=$true)][string]$TrialDirectory,
    [ValidateSet('serialized','original')][string]$Gate = 'serialized',
    [switch]$TraceReceive
)
$ErrorActionPreference = 'Stop'
$trialRoot = (Resolve-Path -LiteralPath $TrialDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $trialRoot 'manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
if (-not $manifest.testOnly -or $manifest.upstreamCommit -ne '08c2ed0ad72c423ca06950dee2e842488a219884') {
    throw 'Expected the pinned SDK experiment manifest.'
}
$trialBin = Join-Path $trialRoot 'bin'
if ((Get-FileHash -LiteralPath (Join-Path $trialBin 'SIPSorcery.dll')).Hash -ne $manifest.sdkSha256) {
    throw 'SDK binary differs from experiment manifest.'
}
$oldGate = $env:VOICE_SDK_RECEIVE_GATE; $oldTrace = $env:VOICE_SDK_RECEIVE_DIAGNOSTICS
$oldRelease = $env:VOICE_SDK_TURN_RELEASE
try {
    $env:VOICE_SDK_RECEIVE_GATE = $Gate
    $env:VOICE_SDK_RECEIVE_DIAGNOSTICS = if ($TraceReceive) { '1' } else { '0' }
    $env:VOICE_SDK_TURN_RELEASE = if ($manifest.turnReleasePatchSha256) { '1' } else { '0' }
    & (Join-Path $trialBin 'VoiceNative.exe')
}
finally { $env:VOICE_SDK_RECEIVE_GATE = $oldGate; $env:VOICE_SDK_RECEIVE_DIAGNOSTICS = $oldTrace; $env:VOICE_SDK_TURN_RELEASE = $oldRelease }
