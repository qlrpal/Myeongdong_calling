param([switch]$NoRestore, [switch]$Package)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$publishArguments = @('publish', (Join-Path $PSScriptRoot 'VoiceNative'), '-c', 'Release', '-o', (Join-Path $PSScriptRoot 'dist'))
if ($NoRestore) { $publishArguments += '--no-restore' }
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Native application build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $PSScriptRoot 'dist/README.md')
if ($Package) {
    Compress-Archive -Path (Join-Path $PSScriptRoot 'dist/*') -DestinationPath (Join-Path $PSScriptRoot 'dist.zip') -Force
}
