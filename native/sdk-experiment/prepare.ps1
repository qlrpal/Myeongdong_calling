param()
$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path $PSScriptRoot -Parent
$commit = '08c2ed0ad72c423ca06950dee2e842488a219884'
$archiveHash = '5263A026BBFA39ADE13CC22DEC8925EB6503AD47A041DEB832C2AE639AB0FAAE'
$trialRoot = Join-Path $nativeRoot ('.local/sdk-receive-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $trialRoot | Out-Null
$archive = Join-Path $trialRoot 'source.zip'
Invoke-WebRequest -Uri "https://codeload.github.com/sipsorcery-org/sipsorcery/zip/$commit" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive).Hash -ne $archiveHash) { throw 'Pinned SDK archive hash mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $trialRoot 'upstream')
$sdkRoot = Join-Path $trialRoot "upstream/sipsorcery-$commit"
& python (Join-Path $PSScriptRoot 'patch_udp_receiver.py') (Join-Path $sdkRoot 'src/SIPSorcery/net/RTP/UdpReceiver.cs')
if ($LASTEXITCODE -ne 0) { throw 'Pinned receiver patch failed.' }
& python (Join-Path $PSScriptRoot 'patch_turn_release.py') (Join-Path $sdkRoot 'src/SIPSorcery/net/ICE/RtpIceChannel.cs')
if ($LASTEXITCODE -ne 0) { throw 'Pinned TURN release patch failed.' }
$sdkProject = Join-Path $sdkRoot 'src/SIPSorcery/SIPSorcery.csproj'
$oldCliHome = $env:DOTNET_CLI_HOME; $oldPackages = $env:NUGET_PACKAGES
try {
    $env:DOTNET_CLI_HOME = Join-Path $nativeRoot '.dotnet'
    $env:NUGET_PACKAGES = Join-Path $nativeRoot '.nuget'
    # Preserve the upstream dependency graph; NU1510 pruning would otherwise fail its warning-as-error build.
    & dotnet restore $sdkProject -p:TargetFrameworks=net10.0 -p:RestoreEnablePackagePruning=false
    if ($LASTEXITCODE -ne 0) { throw 'SDK restore failed.' }
    & dotnet build $sdkProject -c Release -f net10.0 --no-restore -p:TargetFrameworks=net10.0 -p:GeneratePackageOnBuild=false -p:RestoreEnablePackagePruning=false
    if ($LASTEXITCODE -ne 0) { throw 'SDK build failed.' }
    & dotnet build (Join-Path $nativeRoot 'VoiceCall.slnx') -c Release --no-restore -warnaserror -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Product/probe build failed. Restore the normal solution first.' }
}
finally { $env:DOTNET_CLI_HOME = $oldCliHome; $env:NUGET_PACKAGES = $oldPackages }
$probeDirectory = Join-Path $nativeRoot 'TransportProbe/bin/Release/net10.0-windows10.0.19041.0'
$trialBin = Join-Path $trialRoot 'bin'
New-Item -ItemType Directory -Path $trialBin | Out-Null
Get-ChildItem -LiteralPath $probeDirectory | Copy-Item -Destination $trialBin -Recurse
Copy-Item -LiteralPath (Join-Path $sdkRoot 'src/SIPSorcery/bin/Release/net10.0/SIPSorcery.dll') -Destination (Join-Path $trialBin 'SIPSorcery.dll') -Force
Copy-Item -LiteralPath (Join-Path $sdkRoot 'LICENSE.md') -Destination (Join-Path $trialRoot 'SIPSorcery-LICENSE.md')
@{
    upstreamCommit=$commit; archiveSha256=$archiveHash
    sdkSha256=(Get-FileHash -LiteralPath (Join-Path $trialBin 'SIPSorcery.dll')).Hash
    probeSha256=(Get-FileHash -LiteralPath (Join-Path $trialBin 'TransportProbe.dll')).Hash
    patchSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'patch_udp_receiver.py')).Hash
    helperSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'UdpReceiveExperiment.cs')).Hash
    turnReleasePatchSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'patch_turn_release.py')).Hash
    defaultPackageUnchanged=$true; testOnly=$true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $trialRoot 'manifest.json') -Encoding utf8
Write-Output "TEST BIN: $trialBin"
