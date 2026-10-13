# Measures how fast the running TapRelay instance releases port 1122 after being asked
# to exit, and whether the process actually terminates.
#
# Why both numbers matter: the user-visible symptom "the app exited but the port is
# still occupied" is caused by the process surviving its own shutdown. The listening
# socket is released early (measured ~667ms), so reporting only the port number hides
# the real defect -- the orphaned process. Always report the pair.
#
# This drives the same code path as a real second-instance handover:
# TapRelay.ExitSignal -> MainForm.ExitForNewInstance.
#
# PowerShell cannot write to the WorkBuddy sandbox stdout, so the report goes to a file.
# Default: <repo>\.workbuddy-ai\tmp\pc-exit-timing.txt
#
# Usage: powershell -File tools\measure-pc-exit.ps1 [-OutputPath <file>]

param(
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Continue"

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo ".workbuddy-ai\tmp\pc-exit-timing.txt"
}
$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$text) { $lines.Add($text) | Out-Null }
function Save-Report { Set-Content -Path $OutputPath -Value ($lines -join "`r`n") -Encoding UTF8 }
function Get-ListenerCount {
    return @(Get-NetTCPConnection -LocalPort 1122 -State Listen -ErrorAction SilentlyContinue).Count
}

$running = @(Get-Process TapRelay -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) {
    Add-Line "ERROR: no TapRelay process is running"
    Save-Report
    exit 1
}

$oldPid = $running[0].Id
Add-Line ("old pid            : " + $oldPid)
Add-Line ("before: listeners  : " + (Get-ListenerCount))

$exitEvent = $null
try {
    $exitEvent = [System.Threading.EventWaitHandle]::OpenExisting("TapRelay.ExitSignal")
} catch {
    Add-Line ("ERROR: cannot open TapRelay.ExitSignal: " + $_.Exception.Message)
    Save-Report
    exit 1
}

$watch = [System.Diagnostics.Stopwatch]::StartNew()
$exitEvent.Set()
Add-Line "exit signal sent"

$freeAt = -1
$deadAt = -1
while ($watch.ElapsedMilliseconds -lt 20000) {
    if ($freeAt -lt 0 -and (Get-ListenerCount) -eq 0) { $freeAt = $watch.ElapsedMilliseconds }
    if ($deadAt -lt 0 -and -not (Get-Process -Id $oldPid -ErrorAction SilentlyContinue)) {
        $deadAt = $watch.ElapsedMilliseconds
    }
    if ($freeAt -ge 0 -and $deadAt -ge 0) { break }
    Start-Sleep -Milliseconds 40
}
$watch.Stop()

if ($freeAt -ge 0) {
    Add-Line ("port 1122 free at  : " + $freeAt + " ms")
} else {
    Add-Line "port 1122 free at  : NEVER within 20s"
}
if ($deadAt -ge 0) {
    Add-Line ("process gone at    : " + $deadAt + " ms")
} else {
    Add-Line "process gone at    : NEVER within 20s"
}
Add-Line ("after: listeners   : " + (Get-ListenerCount))
Add-Line ("process still alive: " + (@(Get-Process -Id $oldPid -ErrorAction SilentlyContinue).Count -gt 0))

Save-Report
