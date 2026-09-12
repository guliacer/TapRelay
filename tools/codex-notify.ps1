param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$notifier = Join-Path $scriptDirectory "ai-complete-notify.ps1"
$eventText = ""

try {
    if ([Console]::IsInputRedirected) {
        $eventText = [Console]::In.ReadToEnd()
    }
} catch {
    $eventText = ""
}

function Invoke-PhoneNotifier {
    if ($eventText) {
        if ($Arguments -and $Arguments.Count -gt 0) {
            $eventText | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notifier `
                -Source codex `
                -FromHook `
                -Arguments $Arguments
        } else {
            $eventText | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notifier `
                -Source codex `
                -FromHook
        }
    } elseif ($Arguments -and $Arguments.Count -gt 0) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notifier `
            -Source codex `
            -FromHook `
            -Arguments $Arguments
    } else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $notifier `
            -Source codex `
            -FromHook
    }
}

Invoke-PhoneNotifier

# Preserve a pre-existing ChatGPT notify command when the installer captured one.
$forwardPath = Join-Path $scriptDirectory "codex-notify-forward.txt"
if (Test-Path -LiteralPath $forwardPath) {
    $line = Get-Content -LiteralPath $forwardPath -Raw -Encoding ASCII
    if ($line -match 'notify\s*=\s*\[\s*"([^"]+)"(?:\s*,\s*"([^"]+)")?') {
        $forwardCommand = $matches[1].Replace('\\', '\')
        $forwardArguments = @()
        if ($matches[2]) {
            $forwardArguments += $matches[2]
        }
        if ($eventText) {
            $eventText | & $forwardCommand @forwardArguments 2>$null | Out-Null
        } elseif ($Arguments -and $Arguments.Count -gt 0) {
            & $forwardCommand @forwardArguments @Arguments 2>$null | Out-Null
        } elseif ($forwardCommand -and (Test-Path -LiteralPath $forwardCommand)) {
            & $forwardCommand @forwardArguments 2>$null | Out-Null
        }
    }
}

exit 0
