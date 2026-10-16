param(
    [Parameter(Mandatory)][string]$InputPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][int]$ProcessId
)
$ErrorActionPreference = 'Stop'
$InputPath = [IO.Path]::GetFullPath($InputPath)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw "Output exists: $OutputPath" }
$rows = [Collections.Generic.List[object]]::new()
$seen = 0
$filter = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Network'] and (EventID=42 or EventID=43 or EventID=58 or EventID=59)]]"
Get-WinEvent -Path $InputPath -Oldest -FilterXPath $filter | ForEach-Object {
    $event = $_
    $fields = [ordered]@{}
    foreach ($item in ([xml]$event.ToXml()).Event.EventData.Data) { $fields[$item.Name] = $item.InnerText }
    $seen++
    if ([int]$fields['PID'] -eq $ProcessId) {
        $rows.Add([ordered]@{ id = $event.Id; utc = $event.TimeCreated.ToUniversalTime().ToString('O'); fields = $fields })
    }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[ordered]@{ processId = $ProcessId; udpEventsSeen = $seen; rows = $rows;
    note = 'UDP OS metadata for one process. No payload/RTP header. Verify ETW loss/wrap separately before interpreting absent events.'
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Exported $($rows.Count) UDP events for PID $ProcessId out of $seen."
