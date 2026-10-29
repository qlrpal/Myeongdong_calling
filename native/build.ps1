param([switch]$NoRestore, [switch]$Package, [switch]$ReadyToRun, [switch]$StageOnly,
    [switch]$Beta, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
if ($ReadyToRun -and $NoRestore) { throw 'ReadyToRun requires RID-specific restore; omit -NoRestore.' }
if ($StageOnly -and $Package) { throw 'StageOnly preserves dist and does not create a release ZIP.' }
if ($Beta -and $NoRestore) { throw 'Beta uses an isolated win-x64 restore; omit -NoRestore.' }
if ($FrameworkDependent -and -not $Beta) { throw 'FrameworkDependent is an explicit beta package option.' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$nativeRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$staging = Join-Path $nativeRoot ('.local/publish-' + [guid]::NewGuid().ToString('N'))
$outputName = if ($Beta) { 'dist-beta' } else { 'dist' }
$dist = Join-Path $nativeRoot $outputName
$zip = Join-Path $nativeRoot ($outputName + '.zip')
$pendingZip = Join-Path $nativeRoot ('.local/package-' + [guid]::NewGuid().ToString('N') + '.zip')
$zipCheck = Join-Path $nativeRoot ('.local/package-check-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $nativeRoot ('.local/archive/build-' + $outputName + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
# Resolve and bound every recursive move before touching existing output.
foreach ($path in @($staging, $dist, $archive, $zip, $pendingZip, $zipCheck)) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($nativeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build output path escaped the native workspace.'
    }
}
$publishArguments = @('publish', (Join-Path $PSScriptRoot 'VoiceNative'), '-c', 'Release', '-o', $staging)
if ($Beta) {
    $privateLock = Join-Path $nativeRoot ('.local/publish-lock-' + [guid]::NewGuid().ToString('N') + '.json')
    New-Item -ItemType Directory -Path (Split-Path $privateLock) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $nativeRoot 'VoiceNative/packages.lock.json') -Destination $privateLock
    $contained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $publishArguments += @('-r', 'win-x64', '--self-contained', $contained, '-p:BetaRelease=true',
        '-p:DebugType=none', '-p:DebugSymbols=false', ('-p:NuGetLockFilePath=' + $privateLock))
}
if ($NoRestore) { $publishArguments += '--no-restore' }
if ($ReadyToRun) {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    # RID-specific experiments must not rewrite the source checkout's package lock.
    if ($Beta) { $publishArguments += '-p:PublishReadyToRun=true' }
    else {
        $publishArguments += @('-r','win-x64','--self-contained','false','-p:PublishReadyToRun=true',
            ('-p:NuGetLockFilePath=' + (Join-Path $nativeRoot ('.local/r2r-lock-' + [guid]::NewGuid().ToString('N') + '.json'))))
    }
}
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Native application build failed; previous output retained.' }
if ($Beta) {
    & node (Join-Path $nativeRoot 'release/package.mjs') prepare $staging $nativeRoot $contained
    if ($LASTEXITCODE -ne 0) { throw 'Beta package checks failed; previous output retained.' }
}
else {
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $staging 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/USER-GUIDE.md') -Destination (Join-Path $staging '시작하기.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $staging -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server/README.md') -Destination (Join-Path $staging 'docs/SERVER.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'server/deploy') -Destination (Join-Path $staging 'docs') -Recurse
$packageReadme = Join-Path $staging 'README.md'
$readmeText = [IO.File]::ReadAllText($packageReadme).Replace('server/README.md', 'docs/SERVER.md')
[IO.File]::WriteAllText($packageReadme, $readmeText, [Text.UTF8Encoding]::new($false))
}
if ($StageOnly) { Write-Output "Prepared isolated output: $staging"; return }
if ($Package) {
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $pendingZip
    if ($Beta) {
        Expand-Archive -LiteralPath $pendingZip -DestinationPath $zipCheck
        & node (Join-Path $nativeRoot 'release/package.mjs') verify $zipCheck
        if ($LASTEXITCODE -ne 0) { throw 'Beta ZIP verification failed; previous output retained.' }
    }
}
# Retain old packages and user-created diagnostic files without including them in the new ZIP.
New-Item -ItemType Directory -Path $archive -Force | Out-Null
if (Test-Path -LiteralPath $dist) { Move-Item -LiteralPath $dist -Destination (Join-Path $archive 'dist') }
if (Test-Path -LiteralPath $zip) {
    Move-Item -LiteralPath $zip -Destination (Join-Path $archive ($outputName + '.zip'))
}
Move-Item -LiteralPath $staging -Destination $dist
if ($Package) {
    Move-Item -LiteralPath $pendingZip -Destination $zip
}
Write-Output "Previous output retained in $archive"
Write-Output "Prepared package: $dist"
if ($Package) { Write-Output "ZIP: $zip" }
