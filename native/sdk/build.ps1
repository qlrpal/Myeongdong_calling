param([string]$VerifyDirectory)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
# PowerShell 7 callers can pass their module path to Windows PowerShell 5.1.
# Use the OS modules in this child process, without changing the caller's environment.
if ($PSVersionTable.PSEdition -eq 'Desktop') { $env:PSModulePath = Join-Path $PSHOME 'Modules' }
$nativeRoot = Split-Path $PSScriptRoot -Parent
$cacheRoot = Join-Path $nativeRoot '.local/patched-sdk'
$commit = '08c2ed0ad72c423ca06950dee2e842488a219884'
$archiveHash = '5263A026BBFA39ADE13CC22DEC8925EB6503AD47A041DEB832C2AE639AB0FAAE'
$revision = 'udp1-turn1-rtp0'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
[Console]::InputEncoding = $utf8
$OutputEncoding = $utf8
function Hash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path); $hasher = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') }
    finally { $hasher.Dispose(); $stream.Dispose() }
}
function Replace-Once([string]$Content, [string]$Old, [string]$New) {
    if ([regex]::Matches($Content, [regex]::Escape($Old)).Count -ne 1) { throw 'Pinned SDK patch anchor mismatch.' }
    $Content.Replace($Old, $New)
}
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
$lockStream = $null
$deadline = [DateTime]::UtcNow.AddMinutes(5)
while ($null -eq $lockStream) {
    try { $lockStream = [IO.File]::Open((Join-Path $cacheRoot 'build.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch [IO.IOException] { if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for SDK preparation.' }; Start-Sleep -Milliseconds 200 }
}
try {
    $sdkDll = Join-Path $cacheRoot 'SIPSorcery.dll'
    $manifestPath = Join-Path $cacheRoot 'manifest.json'
    if ($VerifyDirectory) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
        $outputDll = Join-Path $VerifyDirectory 'SIPSorcery.dll'
        if (-not (Test-Path -LiteralPath $outputDll) -or (Hash $outputDll) -ne $manifest.sdkSha256) {
            throw "Build/publish did not contain the patched SDK: $outputDll"
        }
        return
    }
    $inputFiles = @('build.ps1','PatchedSipsorcery.csproj','RuntimeDependencies.props','PatchIdentity.cs','ReceiveClaim.cs.txt','TurnRelease.cs.txt','packages.lock.json')
    $inputHashes = @($inputFiles | ForEach-Object { Hash (Join-Path $PSScriptRoot $_) })
    $keyBytes = [Text.Encoding]::UTF8.GetBytes(($commit + $archiveHash + ($inputHashes -join '')))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $inputKey = [BitConverter]::ToString($sha.ComputeHash($keyBytes)).Replace('-','') } finally { $sha.Dispose() }
    if ((Test-Path -LiteralPath $manifestPath) -and (Test-Path -LiteralPath $sdkDll)) {
        $cached = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($cached.inputKey -eq $inputKey -and (Hash $sdkDll) -eq $cached.sdkSha256 -and
            (Test-Path -LiteralPath (Join-Path $cacheRoot 'SIPSorcery.LICENSE.md'))) {
            Write-Output "Patched SIPSorcery $revision (verified cache)"
            return
        }
    }
    $archive = Join-Path $cacheRoot 'upstream.zip'
    if (-not (Test-Path -LiteralPath $archive) -or (Hash $archive) -ne $archiveHash) {
        Write-Output "Downloading pinned SIPSorcery $commit"
        Add-Type -AssemblyName System.Net.Http
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromMinutes(3)
        try {
            # ResponseContentRead applies the timeout to the complete download. Avoid the
            # very slow per-chunk progress path of Windows PowerShell's web cmdlet.
            $response = $client.GetAsync("https://codeload.github.com/sipsorcery-org/sipsorcery/zip/$commit").GetAwaiter().GetResult()
            try {
                $response.EnsureSuccessStatusCode() | Out-Null
                $destination = [IO.File]::Create($archive)
                try { $response.Content.CopyToAsync($destination).GetAwaiter().GetResult() } finally { $destination.Dispose() }
            } finally { $response.Dispose() }
        } finally { $client.Dispose() }
    }
    if ((Hash $archive) -ne $archiveHash) { throw 'Pinned SDK archive SHA-256 mismatch.' }
    $stage = Join-Path $cacheRoot ('build-' + [guid]::NewGuid().ToString('N'))
    # The upstream archive includes large examples. Extract only SDK sources/resources
    # and the license; do not replicate the entire upstream repository per build.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $prefix = "sipsorcery-$commit/"
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not ($entry.FullName.StartsWith($prefix + 'src/SIPSorcery/',[StringComparison]::Ordinal) -or
                      $entry.FullName -eq ($prefix + 'LICENSE.md'))) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $stage $entry.FullName))
            if (-not $target.StartsWith(([IO.Path]::GetFullPath($stage) + [IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) {
                throw 'Unsafe path in pinned SDK archive.'
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target)
        }
    } finally { $zip.Dispose() }
    $upstream = Join-Path $stage "sipsorcery-$commit"
    $sourceRoot = Join-Path $upstream 'src/SIPSorcery'
    $receiverPath = Join-Path $sourceRoot 'net/RTP/UdpReceiver.cs'
    $icePath = Join-Path $sourceRoot 'net/ICE/RtpIceChannel.cs'
    if ((Hash $receiverPath) -ne '335C8AA5934A9BF0D24A1DF27EF8EF9E2C9D5B9F4F6B237099BDDC407227E8FC' -or
        (Hash $icePath) -ne 'D967AEA0C5C82B7BAF96E20DC082CA5287F22C60B4DBE94B4824801C75FE50A9') {
        throw 'Pinned upstream source SHA-256 mismatch.'
    }
    $receiver = [IO.File]::ReadAllText($receiverPath).Replace("`r`n","`n")
    $receiver = Replace-Once $receiver '    protected byte[] m_recvBuffer;' "    protected byte[] m_recvBuffer;`n    private readonly object _receiveBeginSync = new object();"
    $oldClaim = @'
        //Prevent call BeginReceiveFrom if it is already running
        if (m_isClosed && m_isRunningReceive)
        {
            m_isRunningReceive = false;
        }
        if (m_isRunningReceive || m_isClosed)
        {
            return;
        }

        try
        {
            m_isRunningReceive = true;
'@
    $oldClaim = $oldClaim.Replace("`r`n","`n")
    $claim = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ReceiveClaim.cs.txt')).Replace("`r`n","`n").TrimEnd()
    $receiver = Replace-Once $receiver $oldClaim $claim
    [IO.File]::WriteAllText($receiverPath,$receiver,$utf8)
    $ice = [IO.File]::ReadAllText($icePath).Replace("`r`n","`n")
    $oldClose = @'
                _closed = true;
                _connectivityChecksTimer?.Dispose();
                _processIceServersTimer?.Dispose();
                _refreshTurnTimer?.Dispose();
'@
    $oldClose = $oldClose.Replace("`r`n","`n")
    $ice = Replace-Once $ice $oldClose ($oldClose + "`n                ReleaseUdpTurnAllocations();")
    $anchor = '        public void AddRemoteCandidate(RTCIceCandidate candidate)'
    if ([regex]::Matches($ice,[regex]::Escape($anchor)).Count -ne 1) { throw 'TURN method anchor mismatch.' }
    $commentAt = $ice.LastIndexOf('        /// <summary>', $ice.IndexOf($anchor,[StringComparison]::Ordinal),[StringComparison]::Ordinal)
    if ($commentAt -lt 0) { throw 'TURN comment anchor mismatch.' }
    $release = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TurnRelease.cs.txt')).Replace("`r`n","`n").TrimEnd()
    $ice = $ice.Insert($commentAt,$release + "`n`n")
    [IO.File]::WriteAllText($icePath,$ice,$utf8)
    $srtpPath = Join-Path $sourceRoot 'net/DtlsSrtp/Lib/SRTP/SrtpContext.cs'
    if ((Hash $srtpPath) -ne '2631C0AFED7B88A24DEBBD2B289548E962D1C94D2764557A3A9E577A2E968D17') {
        throw 'Pinned SRTP source SHA-256 mismatch.'
    }
    $srtp = [IO.File]::ReadAllText($srtpPath).Replace("`r`n","`n")
    $initial = @'
            ssrcContext.SetInitialSequence(sequenceNumber);

            // Derive ROC/index for this packet from the last accepted index.
            var lastIndex = ssrcContext.S_l;
'@
    $initial = $initial.Replace("`r`n","`n")
    $authenticatedInitial = @'
            // Estimate the first index locally. Only authenticated packets may seed
            // replay state; otherwise initial sequence zero is falsely a replay.
            var lastIndex = ssrcContext.S_l_set ? ssrcContext.S_l : sequenceNumber;
'@
    $srtp = Replace-Once $srtp $initial ($authenticatedInitial.Replace("`r`n","`n"))
    [IO.File]::WriteAllText($srtpPath,$srtp,$utf8)
    # Expose only the negotiated public cipher name, never session keys or contexts.
    $threadSafePath = Join-Path $sourceRoot 'net/DtlsSrtp/Lib/SRTP/ThreadSafeSrtpSessionContext.cs'
    $threadSafe = [IO.File]::ReadAllText($threadSafePath)
    $threadSafe = Replace-Once $threadSafe '        private ISrtpContext _srtpSessionContext;' ('        private ISrtpContext _srtpSessionContext;' + "`n        public string MyeongdongCipherName => (_srtpSessionContext as SrtpSessionContext)?.EncodeRtpContext.Cipher.ToString();")
    [IO.File]::WriteAllText($threadSafePath,$threadSafe,$utf8)
    $peerPath = Join-Path $sourceRoot 'net/WebRTC/RTCPeerConnection.cs'
    $peerSource = [IO.File]::ReadAllText($peerPath)
    $peerSource = Replace-Once $peerSource '        private volatile DtlsSrtpTransport _dtlsHandle;' ('        private volatile DtlsSrtpTransport _dtlsHandle;' + "`n        public string MyeongdongSrtpCipherName => _dtlsHandle?.Context?.MyeongdongCipherName;")
    [IO.File]::WriteAllText($peerPath,$peerSource,$utf8)
    $project = Join-Path $PSScriptRoot 'PatchedSipsorcery.csproj'
    & dotnet restore $project --locked-mode "-p:SdkSourceRoot=$sourceRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Patched SDK locked restore failed.' }
    & dotnet build $project -c Release --no-restore "-p:SdkSourceRoot=$sourceRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Patched SDK build failed.' }
    $builtDll = Join-Path $PSScriptRoot 'bin/Release/net10.0/SIPSorcery.dll'
    Copy-Item -LiteralPath $builtDll -Destination $sdkDll -Force
    Copy-Item -LiteralPath (Join-Path $upstream 'LICENSE.md') -Destination (Join-Path $cacheRoot 'SIPSorcery.LICENSE.md') -Force
    @{
        revision=$revision;upstreamCommit=$commit;archiveSha256=$archiveHash
        inputKey=$inputKey;sdkSha256=(Hash $sdkDll);inputHashes=$inputHashes
        sourceDirectory=$sourceRoot;receiverSha256=(Hash $receiverPath);iceSha256=(Hash $icePath);srtpSha256=(Hash $srtpPath)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output "Prepared patched SIPSorcery $revision"
}
finally { $lockStream.Dispose() }
