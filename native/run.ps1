param([string]$MediaAddress, [ValidateSet('baseline','opus-learned','opus-adaptive','opus-auto-frames','opus-adaptive-5','opus-5','opus-2.5','opus-2.5-r10','opus-2.5-r10-h1','opus-2.5-r5-hold','opus-zero','pcm-1','pcm-2.5-r10')][string]$Experiment,
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$appArguments = @()
if ($MediaAddress) { $appArguments += @('--media-address', $MediaAddress) }
if ($Experiment) { $appArguments += @('--experiment', $Experiment) }
dotnet run --project (Join-Path $PSScriptRoot 'VoiceNative') --configuration $Configuration -- @appArguments
