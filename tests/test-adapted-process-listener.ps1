[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$listenerPath = (Resolve-Path (Join-Path $PSScriptRoot "..\tools\watch-adapted-processes.ps1")).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("taprelay-process-listener-" + [guid]::NewGuid().ToString("N"))
$fakeExecutable = Join-Path $testRoot "zcode.exe"
$unknownExecutable = Join-Path $testRoot "not-adapted.exe"
$outputPath = Join-Path $testRoot "listener.jsonl"
$errorPath = Join-Path $testRoot "listener.err"
$listener = $null
$fakeProcess = $null
$unknownProcess = $null

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe") -Destination $fakeExecutable
    Copy-Item -LiteralPath (Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe") -Destination $unknownExecutable

    $listener = Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $listenerPath,
        "-ScanIntervalSeconds", "10"
    ) -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -WindowStyle Hidden -PassThru

    Start-Sleep -Seconds 2
    $fakeProcess = Start-Process -FilePath $fakeExecutable -ArgumentList @(
        "-NoProfile", "-Command", "Start-Sleep -Seconds 20"
    ) -PassThru
    Start-Sleep -Seconds 3
    $fakeProcess.Refresh()
    if ($fakeProcess.HasExited) {
        throw "temporary zcode process exited before lifecycle assertion"
    }

    $fakePid = $fakeProcess.Id
    $unknownProcess = Start-Process -FilePath $unknownExecutable -ArgumentList @(
        "-NoProfile", "-Command", "Start-Sleep -Seconds 20"
    ) -PassThru
    Start-Sleep -Seconds 1
    $unknownPid = $unknownProcess.Id
    Stop-Process -Id $unknownPid -Force
    Start-Sleep -Seconds 1
    Stop-Process -Id $fakePid -Force
    Start-Sleep -Seconds 3

    $allEvents = @(Get-Content -LiteralPath $outputPath -Encoding UTF8 -ErrorAction Stop | ForEach-Object { $_ | ConvertFrom-Json })
    $monitorEvent = @($allEvents | Where-Object { $_.type -eq "monitor_started" })[0]
    $events = @($allEvents | Where-Object { $null -ne $_.PSObject.Properties["processId"] -and $_.processId -eq $fakePid })
    $started = @($events | Where-Object { $_.type -eq "process_started" })
    $stopped = @($events | Where-Object { $_.type -eq "process_stopped" })
    $unknownEvents = @($allEvents | Where-Object { $null -ne $_.PSObject.Properties["processId"] -and $_.processId -eq $unknownPid })
    if ($started.Count -ne 1 -or $stopped.Count -ne 1) {
        throw "zcode lifecycle mismatch: started=$($started.Count), stopped=$($stopped.Count), pid=$fakePid"
    }
    if ($started[0].appKey -ne "zcode" -or $started[0].listenerState -ne "listening") {
        throw "start event did not establish zcode listener"
    }
    if ($stopped[0].appKey -ne "zcode" -or $stopped[0].listenerState -ne "stopped") {
        throw "stop event did not remove zcode listener"
    }
    if ($unknownEvents.Count -ne 0) {
        throw "unadapted process entered listener output: pid=$unknownPid"
    }

    Write-Output ("Process listener lifecycle: PASS (pid={0}, ignoredPid={1}, eventWatcher={2}, startReason={3}, stopReason={4})" -f $fakePid, $unknownPid, $monitorEvent.eventWatcher, $started[0].reason, $stopped[0].reason)
}
finally {
    if ($null -ne $unknownProcess) {
        try {
            $unknownProcess.Refresh()
            if (-not $unknownProcess.HasExited) { Stop-Process -Id $unknownProcess.Id -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    if ($null -ne $fakeProcess) {
        try {
            $fakeProcess.Refresh()
            if (-not $fakeProcess.HasExited) { Stop-Process -Id $fakeProcess.Id -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    if ($null -ne $listener) {
        try {
            if (-not $listener.HasExited) { Stop-Process -Id $listener.Id -Force -ErrorAction SilentlyContinue }
        }
        catch { }
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
