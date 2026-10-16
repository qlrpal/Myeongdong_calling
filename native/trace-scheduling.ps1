param(
    [ValidateRange(10,180)][int]$Seconds=170,
    [string]$OutputPath=(Join-Path $PSScriptRoot ".local/reports/scheduling-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss')).etl"),
    [string]$ProfilePath=(Join-Path $PSScriptRoot 'TransportProbe/Profiles/VoiceScheduling.wprp')
)
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator PowerShell required.'
}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
$ProfilePath=[IO.Path]::GetFullPath($ProfilePath)
if (Test-Path -LiteralPath $OutputPath) { throw "Output exists: $OutputPath" }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))|Out-Null
$instance='VoiceNative-Scheduling-'+[Guid]::NewGuid().ToString('N')
$started=$false
try {
    & wpr -start ($ProfilePath+'!VoiceScheduling.Light') -filemode -instancename $instance 2>&1 | Tee-Object -FilePath ($OutputPath+'.start.log')
    if($LASTEXITCODE -ne 0){throw 'WPR start failed; existing recordings were not cancelled.'}
    $started=$true
    & (Join-Path $PSScriptRoot 'trace-network.ps1') -Seconds $Seconds -OutputPath ($OutputPath+'.network.etl')
}
finally {
    if($started){
        & wpr -stop $OutputPath -instancename $instance 2>&1 | Tee-Object -FilePath ($OutputPath+'.stop.log')
        $stopExit=$LASTEXITCODE
        [ordered]@{session=$instance;seconds=$Seconds;stopExitCode=$stopExit;
            note='DPC/ISR/context-switch/ready-thread/image/process/thread and network metadata. Includes process/module paths; no audio or packet payload. File-mode collection can affect timing. Use raw QPC; inspect event loss before analysis.'
        }|ConvertTo-Json|Set-Content -LiteralPath ($OutputPath+'.json') -Encoding utf8
        if($stopExit -ne 0){
            & wpr -cancel -instancename $instance 2>&1 | Tee-Object -FilePath ($OutputPath+'.cancel.log')
            throw 'Own WPR session save failed; cleanup was attempted. Do not analyze as a completed trace.'
        }
    }
}
