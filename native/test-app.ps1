param(
    [ValidateSet('core', 'devices', 'group')][string]$Profile = 'core',
    [switch]$NoRestore,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$runnerArgs = @((Join-Path $PSScriptRoot 'TransportProbe/Runner/app-checks.mjs'), '--profile', $Profile)
if ($NoRestore) { $runnerArgs += '--no-restore' }
if ($NoBuild) { $runnerArgs += '--no-build' }
& node @runnerArgs
exit $LASTEXITCODE
