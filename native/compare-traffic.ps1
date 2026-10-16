param(
    [Parameter(Mandatory)][string]$ProbePath,
    [Parameter(Mandatory)][string]$Peer,
    [string]$Gateway='172.30.1.254',
    [Parameter(Mandatory)][ValidateSet('pc','laptop')][string]$Role,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Room,
    [string]$Server='https://qlrpal.ddns.net/'
)
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$ProbePath=[IO.Path]::GetFullPath($ProbePath)
foreach($artifact in @('run.json','call.json','idle-before-icmp.json','call-icmp.json','idle-after-icmp.json')){
    if(Test-Path -LiteralPath (Join-Path $OutputDirectory $artifact)){throw 'Run artifacts already exist; choose a new output directory.'}
}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
function Get-LinkSnapshot {
    $at=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
    $adapters=@();$statistics=@();$errors=@();$wlan=@();$routes=@()
    try{$adapters=@(Get-NetAdapter -Physical|Select-Object Name,InterfaceDescription,Status,LinkSpeed,InterfaceGuid)}catch{$errors+=('adapters: '+$_.Exception.Message)}
    try{$statistics=@(Get-NetAdapterStatistics|Select-Object Name,ReceivedBytes,SentBytes,ReceivedUnicastPackets,SentUnicastPackets,ReceivedPacketErrors,OutboundPacketErrors,ReceivedDiscardedPackets,OutboundDiscardedPackets)}catch{$errors+=('statistics: '+$_.Exception.Message)}
    try{
        foreach($destination in @($Peer,$Gateway)){
            $routes+=@{destination=$destination;selection=@(Find-NetRoute -RemoteIPAddress $destination|Select-Object IPAddress,InterfaceAlias,NextHop,InterfaceMetric)}
        }
    }catch{$errors+=('routes: '+$_.Exception.Message)}
    try{
        # Query current link only; never request a scan. Omit SSID/BSSID/MAC/profile fields.
        $wlan=@(& netsh wlan show interfaces 2>&1|ForEach-Object {"$_"}|Where-Object {$_ -match '^\s*(Signal|Channel|Band|Receive rate|Transmit rate|State|Radio type|신호|채널|대역|수신 속도|전송 속도|상태|무선 유형)\s*:' -or $_ -match 'location permission|Location services|위치.*(권한|서비스)|access is denied|액세스.*거부'})
        if($LASTEXITCODE -ne 0){$errors+=('netsh exit '+$LASTEXITCODE)}
        if(-not $wlan.Count){$errors+='No allowlisted WLAN link fields returned; unavailable or unrecognized output, not evidence of no WLAN.'}
    }catch{$errors+=('wlan: '+$_.Exception.Message)}
    [ordered]@{at=$at;utc=[DateTimeOffset]::Now.ToString('O');adapters=$adapters;statistics=$statistics;routes=$routes;wlan=$wlan;errors=$errors}
}
$phases=@();$currentCall=$null;$currentIcmp=$null
try{
    foreach($phase in @('idle-before','call','idle-after')){
        $before=Get-LinkSnapshot
        Write-Output "$([DateTimeOffset]::Now.ToString('O')) phase=$phase started"
        $icmpPath=Join-Path $OutputDirectory ($phase+'-icmp.json')
        $currentIcmp=Start-Process -WindowStyle Hidden -FilePath $ProbePath -ArgumentList '--icmp-profile',$Peer,$Gateway,('"'+$icmpPath+'"') -RedirectStandardOutput (Join-Path $OutputDirectory ($phase+'-icmp.stdout.log')) -RedirectStandardError (Join-Path $OutputDirectory ($phase+'-icmp.stderr.log')) -PassThru
        # Cache the process handle before it exits: Windows PowerShell can otherwise return a null ExitCode.
        $null=$currentIcmp.Handle
        $startedAt=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
        if($phase -eq 'call'){
            $currentCall=Start-Process -WindowStyle Hidden -FilePath $ProbePath -ArgumentList '--pipeline-passive',$Server,$Room,('"'+(Join-Path $OutputDirectory 'call.json')+'"'),$Role -RedirectStandardOutput (Join-Path $OutputDirectory 'call.stdout.log') -RedirectStandardError (Join-Path $OutputDirectory 'call.stderr.log') -PassThru
            $null=$currentCall.Handle
        }
        if(-not $currentIcmp.WaitForExit(155000)){throw "Own ICMP probe timed out: $phase"}
        if($null -eq $currentIcmp.ExitCode -or $currentIcmp.ExitCode -ne 0){throw "ICMP failed: $phase (exit=$($currentIcmp.ExitCode))"}
        $currentIcmp=$null
        if($currentCall){
            if(-not $currentCall.WaitForExit(45000)){throw 'Own call probe did not finish.'}
            if($currentCall.ExitCode -ne 0){throw 'Call probe failed; do not interpret as a completed comparison.'}
            $currentCall=$null
        }
        $endedAt=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
        $after=Get-LinkSnapshot
        $phases+=@{phase=$phase;startedAt=$startedAt;endedAt=$endedAt;before=$before;after=$after}
        [ordered]@{role=$Role;phases=$phases;complete=($phase -eq 'idle-after');note='Same peer/gateway/loopback ICMP in all three phases. Idle means no call probe from this run, not zero network traffic. Snapshots only at phase edges; no forced WLAN scan or setting changes. Concurrent ping can itself change radio behavior. Sequential A/B/A is not a randomized causal test.'}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $OutputDirectory 'run.json') -Encoding utf8
        Write-Output "$([DateTimeOffset]::Now.ToString('O')) phase=$phase completed"
    }
}
finally{
    # Never stop unrelated app/processes or sessions.
    foreach($process in @($currentCall,$currentIcmp)){
        if($process -and -not $process.HasExited){$process.Kill();$process.WaitForExit()}
    }
}
