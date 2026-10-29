[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$Server,
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'Run')][ValidateSet('a','b')][string]$Role,
    [Parameter(ParameterSetName = 'Run')][ValidateSet('direct','relay')][string]$Route = 'direct',
    [Parameter(ParameterSetName = 'Run')][switch]$SharedOutput,
    [Parameter(ParameterSetName = 'Run')][string]$MediaAddress,
    [Parameter(ParameterSetName = 'Run')][ValidateRange(5,300)][int]$ObserveSeconds = 5,
    [Parameter(ParameterSetName = 'Run')][ValidateRange(10,40)][int]$ReserveMs = 20,
    [Parameter(ParameterSetName = 'Run')][switch]$TraceBaseline,
    [Parameter(ParameterSetName = 'Run')][switch]$NoRestore,
    [Parameter(ParameterSetName = 'Run')][switch]$NoBuild,
    [Parameter(Mandatory, ParameterSetName = 'Merge')][string]$ReportA,
    [Parameter(Mandatory, ParameterSetName = 'Merge')][string]$ReportB
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$runnerArgs = @((Join-Path $PSScriptRoot 'TransportProbe/Runner/pair-checks.mjs'))
if ($PSCmdlet.ParameterSetName -eq 'Merge') {
    $runnerArgs += @('merge', $ReportA, $ReportB)
} else {
    $runnerArgs += @('run', '--server', $Server, '--run', $RunId, '--role', $Role, '--route', $Route, '--shared', $SharedOutput.IsPresent.ToString().ToLowerInvariant())
    if ($MediaAddress) { $runnerArgs += @('--media-address', $MediaAddress) }
    $runnerArgs += @('--observe-seconds', $ObserveSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    $runnerArgs += @('--reserve-ms', $ReserveMs.ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($TraceBaseline) { $runnerArgs += '--trace-baseline' }
    if ($NoRestore) { $runnerArgs += '--no-restore' }
    if ($NoBuild) { $runnerArgs += '--no-build' }
}
& node @runnerArgs
exit $LASTEXITCODE
