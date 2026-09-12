param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [Parameter(Mandatory = $true)]
    [string]$Command,
    [string]$Message = "",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArguments
)

$ErrorActionPreference = "Continue"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$notifier = Join-Path $scriptDirectory "ai-complete-notify.ps1"
$watch = [Diagnostics.Stopwatch]::StartNew()

& $Command @CommandArguments
$exitCode = $LASTEXITCODE
$watch.Stop()

$status = if ($exitCode -eq 0) { "completed" } else { "failed" }
$defaultMessage = if ($exitCode -eq 0) {
    "Task completed: $Command"
} else {
    "Task failed (exit code $exitCode): $Command"
}
if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = $defaultMessage
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notifier `
    -Source $Source `
    -Status $status `
    -Message $Message `
    -Cwd (Get-Location).Path `
    -TaskId ([guid]::NewGuid().ToString())

exit $exitCode
