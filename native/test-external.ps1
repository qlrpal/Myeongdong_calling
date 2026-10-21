param(
    [ValidateSet('pc','laptop')][string]$Role,
    [ValidateSet('auto','relay')][string]$Mode,
    [string]$RunId,
    [string]$Server = 'https://qlrpal.ddns.net/',
    [string]$ToolDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
try {
    if (-not $Role) { $Role = Read-Host 'Role (pc / laptop)' }
    if ($Role -notin @('pc','laptop')) { throw 'Role must be pc or laptop.' }
    if (-not $Mode) { $Mode = Read-Host 'Mode (auto / relay)' }
    if ($Mode -notin @('auto','relay')) { throw 'Mode must be auto or relay.' }
    if (-not $RunId) { $RunId = Read-Host 'Same run ID on both computers (example: 20a)' }
    if ($RunId -notmatch '^[a-zA-Z0-9-]{1,16}$') { throw 'Use 1-16 letters, digits or hyphens for run ID.' }
    $probe = Join-Path $ToolDirectory 'TransportProbe.exe'
    $wave = Join-Path $ToolDirectory 'speech.wav'
    if (-not (Test-Path -LiteralPath $probe) -or -not (Test-Path -LiteralPath $wave)) { throw 'TransportProbe.exe or speech.wav is missing from the tool directory.' }
    $results = Join-Path $ToolDirectory 'results'
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $stem = '{0}-{1}-{2}-{3}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'),$RunId,$Mode,$Role
    $report = Join-Path $results ($stem + '.json')
    Write-Host "Room: external059-$RunId-$Mode"
    Write-Host 'Start the other computer within 2 minutes. Connected test lasts 5 minutes.'
    Write-Host 'Synthetic speech is audible. No microphone recording or device setting changes.'
    Write-Host "Report: $report"
    & $probe --external-speech-soak $Server "external059-$RunId-$Mode" $report $Role $Mode $wave 2>&1 |
        Tee-Object -FilePath (Join-Path $results ($stem + '.log'))
    $probeExit = $LASTEXITCODE
    Write-Host "Finished. Exit code: $probeExit. Keep the results folder even if the connection failed."
    if ($probeExit -ne 0) { exit $probeExit }
}
catch { Write-Host $_ -ForegroundColor Red; exit 1 }
