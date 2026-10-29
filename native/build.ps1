param([switch]$NoRestore, [switch]$Package, [switch]$ReadyToRun, [switch]$StageOnly,
    [switch]$Beta, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$toolArguments = @('build', '--native-root', $PSScriptRoot)
if ($NoRestore) { $toolArguments += '--no-restore' }
if ($Package) { $toolArguments += '--package' }
if ($ReadyToRun) { $toolArguments += '--ready-to-run' }
if ($StageOnly) { $toolArguments += '--stage-only' }
if ($Beta) { $toolArguments += '--beta' }
if ($FrameworkDependent) { $toolArguments += '--framework-dependent' }
& dotnet run --project (Join-Path $PSScriptRoot 'BuildTools/BuildTools.csproj') -c Release -- @toolArguments
if ($LASTEXITCODE -ne 0) { throw "BuildTools failed (exit $LASTEXITCODE). See the preceding error; prior output is preserved or restored." }
