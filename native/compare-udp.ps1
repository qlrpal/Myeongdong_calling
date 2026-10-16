param(
    [Parameter(Mandatory)][string]$ProbePath,
    [Parameter(Mandatory)][string]$TraceScript,
    [Parameter(Mandatory)][string]$Peer,
    [Parameter(Mandatory)][ValidateSet('pc','laptop')][string]$Role,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_-]{1,64}$')][string]$Run,
    [ValidateRange(32,1200)][int]$PayloadBytes=68,
    [string]$Gateway='172.30.1.254',
    [string]$Server='https://qlrpal.ddns.net/',
    [string]$PatternPath,
    [switch]$ReplayOnly,
    [switch]$WithWlanMetrics,
    [switch]$WlanObserverAb,
    [switch]$WithWlanEvents,
    [string]$WlanTraceScript=(Join-Path $PSScriptRoot 'trace-wlan.ps1'),
    [string]$WlanProfilePath=(Join-Path $PSScriptRoot 'TransportProbe/Profiles/VoiceWlan.wprp')
)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator PowerShell required for OS metadata.'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$ProbePath=[IO.Path]::GetFullPath($ProbePath)
$TraceScript=[IO.Path]::GetFullPath($TraceScript)
if($PatternPath){$PatternPath=[IO.Path]::GetFullPath($PatternPath)}
$order=if($PatternPath){@('uniform1','replay1','replay2','uniform2')}else{@('udp1','call1','call2','udp2')}
if($ReplayOnly){if(-not $PatternPath){throw 'ReplayOnly requires PatternPath.'};$order=@('replay1','replay2')}
$orderNote=if($ReplayOnly){'Repeated replay order.'}else{'Counterbalanced order.'}
$trafficNote='Pattern mode holds packet count/size order and changes only intended timing.'
if($WlanObserverAb){
    if(-not $PatternPath -or $ReplayOnly -or $WithWlanMetrics){throw 'WlanObserverAb requires PatternPath and cannot combine with ReplayOnly/WithWlanMetrics.'}
    $order=@('off1','on1','on2','off2')
    $orderNote='WLAN polling off/on/on/off; identical replay plans.'
    $trafficNote='All phases replay the same packet-size/timing plan; only own WLAN polling changes.'
}
if($WithWlanEvents){
    if(-not $ReplayOnly -or $WithWlanMetrics -or $WlanObserverAb){throw 'WithWlanEvents requires ReplayOnly without WLAN polling.'}
    $WlanTraceScript=[IO.Path]::GetFullPath($WlanTraceScript)
    $WlanProfilePath=[IO.Path]::GetFullPath($WlanProfilePath)
}
if(Test-Path -LiteralPath (Join-Path $OutputDirectory 'run.json')){throw 'Existing run; choose a fresh directory.'}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$phases=@();$test=$null;$icmp=$null;$trace=$null;$metrics=$null;$wlan=$null;$etl=$null
try{
    foreach($name in $order){
        $dir=Join-Path $OutputDirectory $name
        if(Test-Path -LiteralPath $dir){throw "Phase already exists: $name"}
        [IO.Directory]::CreateDirectory($dir)|Out-Null
        if($WithWlanEvents){
            $wlanEtl=Join-Path $dir 'wlan.etl'
            $wlan=Start-Process -WindowStyle Hidden powershell.exe -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$WlanTraceScript+'"'),'-Seconds','170','-OutputPath',('"'+$wlanEtl+'"'),'-ProfilePath',('"'+$WlanProfilePath+'"') -RedirectStandardOutput (Join-Path $dir 'wlan-trace.stdout.log') -RedirectStandardError (Join-Path $dir 'wlan-trace.stderr.log') -PassThru
            $null=$wlan.Handle
            $wlanWait=[Diagnostics.Stopwatch]::StartNew()
            while(-not (Test-Path -LiteralPath ($wlanEtl+'.active.json'))){
                if($wlan.HasExited -or $wlanWait.Elapsed.TotalSeconds -gt 20){throw 'Own WLAN trace did not start.'}
                Start-Sleep -Milliseconds 100
            }
        }
        $etl=Join-Path $dir 'network.etl'
        $trace=Start-Process -WindowStyle Hidden powershell.exe -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$TraceScript+'"'),'-Seconds','145','-OutputPath',('"'+$etl+'"') -RedirectStandardOutput (Join-Path $dir 'trace.stdout.log') -RedirectStandardError (Join-Path $dir 'trace.stderr.log') -PassThru
        $null=$trace.Handle
        $wait=[Diagnostics.Stopwatch]::StartNew()
        while(-not (Test-Path -LiteralPath ($etl+'.active.json'))){
            if($trace.HasExited -or $wait.Elapsed.TotalSeconds -gt 15){throw 'Own network trace did not start.'}
            Start-Sleep -Milliseconds 100
        }
        $polling=[bool]($WithWlanMetrics -or ($WlanObserverAb -and $name.StartsWith('on')))
        if($polling){
            $metrics=Start-Process -WindowStyle Hidden $ProbePath -ArgumentList '--wlan-metrics','145',('"'+(Join-Path $dir 'wlan.json')+'"') -RedirectStandardOutput (Join-Path $dir 'wlan.stdout.log') -RedirectStandardError (Join-Path $dir 'wlan.stderr.log') -PassThru
            $null=$metrics.Handle
        }
        $icmp=Start-Process -WindowStyle Hidden $ProbePath -ArgumentList '--icmp-profile',$Peer,$Gateway,('"'+(Join-Path $dir 'icmp.json')+'"') -RedirectStandardOutput (Join-Path $dir 'icmp.stdout.log') -RedirectStandardError (Join-Path $dir 'icmp.stderr.log') -PassThru
        $null=$icmp.Handle
        if($PatternPath){
            $mode=if($name.StartsWith('uniform')){'uniform'}else{'replay'}
            $arguments=@('--udp-pattern',$Peer,'42000','42000','120',$Role,($Run+'-'+$name),('"'+$PatternPath+'"'),$mode,('"'+(Join-Path $dir 'test.json')+'"'))
        }elseif($name.StartsWith('udp')){
            $arguments=@('--udp-paced',$Peer,'42000','42000',"$PayloadBytes",'120',$Role,($Run+'-'+$name),('"'+(Join-Path $dir 'test.json')+'"'))
        }else{
            $arguments=@('--pipeline-passive',$Server,($Run+'-'+$name),('"'+(Join-Path $dir 'test.json')+'"'),$Role)
        }
        $test=Start-Process -WindowStyle Hidden $ProbePath -ArgumentList $arguments -RedirectStandardOutput (Join-Path $dir 'test.stdout.log') -RedirectStandardError (Join-Path $dir 'test.stderr.log') -PassThru
        $null=$test.Handle
        $phase=@{name=$name;testPid=$test.Id;icmpPid=$icmp.Id;wlanPolling=$polling;startedAt=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency}
        $phase|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'phase.json') -Encoding utf8
        Write-Output "$([DateTimeOffset]::Now.ToString('O')) $name started testPid=$($test.Id)"
        if(-not $test.WaitForExit(160000) -or $null -eq $test.ExitCode -or $test.ExitCode -ne 0){throw "Own test failed: $name"}
        if(-not $icmp.WaitForExit(35000) -or $null -eq $icmp.ExitCode -or $icmp.ExitCode -ne 0){throw "Own ICMP failed: $name"}
        if($metrics){
            if(-not $metrics.WaitForExit(40000) -or $null -eq $metrics.ExitCode -or $metrics.ExitCode -ne 0){throw "Own WLAN query process failed: $name"}
            $phase.metricsExit=$metrics.ExitCode;$phase.metricsPid=$metrics.Id
            $metrics=$null
        }
        if(-not $trace.WaitForExit(40000) -or $null -eq $trace.ExitCode -or $trace.ExitCode -ne 0){throw "Own trace failed: $name"}
        if($wlan){
            if(-not $wlan.WaitForExit(60000) -or $null -eq $wlan.ExitCode -or $wlan.ExitCode -ne 0){throw "Own WLAN trace failed: $name"}
            $phase.wlanTraceExit=$wlan.ExitCode;$phase.wlanTracePid=$wlan.Id;$wlan=$null
        }
        $phase.endedAt=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
        $phase.testExit=$test.ExitCode;$phase.icmpExit=$icmp.ExitCode;$phase.traceExit=$trace.ExitCode
        $phase|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'phase.json') -Encoding utf8
        $test=$null;$icmp=$null;$trace=$null
        $phases+=$phase
        @{role=$Role;phases=$phases;complete=($name -eq $order[-1]);payloadBytes=$PayloadBytes;patternPath=$PatternPath;order=$order;note=($orderNote+' 120s tests, same ICMP and bounded UDP metadata ETW. '+$trafficNote+' Not randomized; background traffic uncontrolled. No cross-host absolute QPC subtraction.')}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputDirectory 'run.json') -Encoding utf8
        Write-Output "$([DateTimeOffset]::Now.ToString('O')) $name completed"
    }
}finally{
    foreach($p in @($test,$icmp,$metrics)){if($p -and -not $p.HasExited){$p.Kill();$p.WaitForExit()}}
    if($trace -and -not $trace.HasExited){
        # Let the bounded collector finish normally; never kill it before its finally block.
        if(-not $trace.WaitForExit(170000)){
            $own=Get-Content -LiteralPath ($etl+'.active.json') -Raw|ConvertFrom-Json
            if($own.session -match '^VoiceNative-Net-[a-f0-9]{32}$' -and $own.output -eq $etl){& logman stop $own.session -ets}
        }
    }
    if($wlan -and -not $wlan.HasExited){
        # Allow this bounded WPR collector to save itself; never cancel unrelated recordings.
        if(-not $wlan.WaitForExit(190000)){
            $ownWlan=Get-Content -LiteralPath ($wlanEtl+'.active.json') -Raw|ConvertFrom-Json
            if($ownWlan.session -match '^VoiceNative-Wlan-[a-f0-9]{32}$' -and $ownWlan.output -eq $wlanEtl){& wpr -cancel -instancename $ownWlan.session}
        }
    }
}
