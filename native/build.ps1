param([switch]$NoRestore, [switch]$Package)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$nativeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$staging = Join-Path $nativeRoot ('.local/publish-' + [guid]::NewGuid().ToString('N'))
$dist = Join-Path $nativeRoot 'dist'
$archive = Join-Path $nativeRoot ('.local/archive/build-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
# Resolve and bound every recursive move before touching existing output.
foreach ($path in @($staging, $dist, $archive)) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($nativeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build output path escaped the native workspace.'
    }
}
$publishArguments = @('publish', (Join-Path $PSScriptRoot 'VoiceNative'), '-c', 'Release', '-o', $staging)
if ($NoRestore) { $publishArguments += '--no-restore' }
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Native application build failed; previous output retained.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $staging 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $staging -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server/README.md') -Destination (Join-Path $staging 'docs/SERVER.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server/deploy') -Destination (Join-Path $staging 'docs') -Recurse
$packageReadme = Join-Path $staging 'README.md'
$readmeText = [IO.File]::ReadAllText($packageReadme).Replace('server/README.md', 'docs/SERVER.md')
[IO.File]::WriteAllText($packageReadme, $readmeText, [Text.UTF8Encoding]::new($false))
# Retain old packages and user-created diagnostic files without including them in the new ZIP.
New-Item -ItemType Directory -Path $archive -Force | Out-Null
if (Test-Path -LiteralPath $dist) { Move-Item -LiteralPath $dist -Destination (Join-Path $archive 'dist') }
if (Test-Path -LiteralPath (Join-Path $nativeRoot 'dist.zip')) {
    Move-Item -LiteralPath (Join-Path $nativeRoot 'dist.zip') -Destination (Join-Path $archive 'dist.zip')
}
Move-Item -LiteralPath $staging -Destination $dist
if ($Package) {
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath (Join-Path $nativeRoot 'dist.zip')
}
Write-Output "Previous output retained in $archive"
