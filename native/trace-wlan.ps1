param(
    [ValidateRange(10,180)][int]$Seconds=170,
    [string]$OutputPath=(Join-Path $PSScriptRoot ".local/reports/wlan-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss')).etl"),
    [string]$ProfilePath=(Join-Path $PSScriptRoot 'TransportProbe/Profiles/VoiceWlan.wprp'),
    [switch]$WithNetwork,
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
$allowed=@{
    14000=@('InterfaceGuid','InterfaceDescription','Connected')
    14008=@('InterfaceGuid','InterfaceDescription','PowerSetting')
    14011=@('InterfaceGuid','InterfaceDescription','PHY','SoftwareState','HardwareState','PHYCount')
    14025=@('InterfaceGuid','InterfaceDescription','State')
    14036=@('Setting');14040=@('InterfaceGuid','InterfaceDescription','ScanType','FlushBSSList')
    14054=@('InterfaceGuid','InterfaceDescription','Enabled')
    14067=@('IsOn');14068=@('IsEnabled');20022=@('InterfaceGuid');20023=@('InterfaceGuid','Status')
    14071=@('InterfaceGuid','ExpeditedScanTrigger');14072=@('InterfaceGuid','DisconnectTrigger')
    30019=@('InterfaceGuid')
}
$provider=Get-WinEvent -ListProvider Microsoft-Windows-WLAN-AutoConfig
foreach($id in $allowed.Keys){
    $event=@($provider.Events|Where-Object Id -eq $id)
    if($event.Count -ne 1){throw "Missing or ambiguous WLAN event $id"}
    if($event[0].Level.Value -gt 4){throw "WLAN event $id exceeds the reviewed provider level."}
    [xml]$template=$event[0].Template
    $names=@($template.template.data|ForEach-Object name)
    if(($names -join ',') -ne ($allowed[$id] -join ',')){throw "Unexpected WLAN schema for $id"}
}
[xml]$profile=Get-Content -LiteralPath $ProfilePath -Raw
$filters=@($profile.WindowsPerformanceRecorder.Profiles.EventProvider.EventFilters.EventId|ForEach-Object {[int]$_.Value})
if($profile.WindowsPerformanceRecorder.Profiles.EventProvider.Name -ne '9580d7dd-0379-4658-9870-d5be7d52d6de' -or
   $profile.WindowsPerformanceRecorder.Profiles.EventProvider.EventFilters.FilterIn -ne 'true') {throw 'Unexpected WLAN provider or exclusion filter'}
if((($filters|Sort-Object) -join ',') -ne (($allowed.Keys|Sort-Object) -join ',')){throw 'Unexpected WLAN profile event filter'}
if($ValidateOnly){Write-Output 'WLAN scan/power/connection-state schemas, levels and event ID filter validated.';return}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator PowerShell required.'}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$ProfilePath=[IO.Path]::GetFullPath($ProfilePath)
if(Test-Path -LiteralPath $OutputPath){throw "Output exists: $OutputPath"}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))|Out-Null
$instance='VoiceNative-Wlan-'+[Guid]::NewGuid().ToString('N')
$started=$false
$captureBefore=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
try{
    & wpr -start ($ProfilePath+'!VoiceWlan.Light') -filemode -instancename $instance 2>&1|Tee-Object -FilePath ($OutputPath+'.start.log')
    if($LASTEXITCODE -ne 0){throw 'Own WLAN trace start failed; existing recordings were not cancelled.'}
    $started=$true
    $captureStarted=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
    @{session=$instance;output=$OutputPath;captureStartedQpc=$captureStarted}|ConvertTo-Json|Set-Content -LiteralPath ($OutputPath+'.active.json') -Encoding utf8
    if($WithNetwork){& (Join-Path $PSScriptRoot 'trace-network.ps1') -Seconds $Seconds -OutputPath ($OutputPath+'.network.etl')}
    else{$watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt $Seconds){Start-Sleep -Milliseconds 250}}
}
finally{
    if($started){
        & wpr -stop $OutputPath -instancename $instance 2>&1|Tee-Object -FilePath ($OutputPath+'.stop.log')
        $stopExit=$LASTEXITCODE
        $captureAfter=[Diagnostics.Stopwatch]::GetTimestamp()/[double][Diagnostics.Stopwatch]::Frequency
        [ordered]@{session=$instance;seconds=$Seconds;stopExitCode=$stopExit;eventIds=@($allowed.Keys|Sort-Object);
            captureBeforeQpc=$captureBefore;captureStartedQpc=$captureStarted;captureAfterQpc=$captureAfter;
            note='Only allowlisted WLAN scan/power/connection state and expected-roaming event IDs. Interface GUID/description/status; no SSID/BSSID, audio or packet payload. Raw QPC required for timing. No events does not prove no firmware scan, roaming or actual NIC power transition.'
        }|ConvertTo-Json|Set-Content -LiteralPath ($OutputPath+'.json') -Encoding utf8
        if($stopExit -ne 0){& wpr -cancel -instancename $instance;throw 'Own WLAN trace save failed; cleanup attempted.'}
    }
}
