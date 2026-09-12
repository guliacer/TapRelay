param(
    [switch]$ClaudeOnly,
    [Alias("CodexOnly")]
    [switch]$ChatGPTOnly
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$notifier = Join-Path $scriptDirectory "ai-complete-notify.ps1"
$codexNotifier = Join-Path $scriptDirectory "codex-notify.ps1"
$marker = "ai-complete-notify.ps1"

function Read-JsonObject([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{}
    }

    $raw = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [pscustomobject]@{}
    }

    return $raw | ConvertFrom-Json
}

function Ensure-Property($object, [string]$name, $value) {
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property) {
        $object | Add-Member -NotePropertyName $name -NotePropertyValue $value
    } else {
        $property.Value = $value
    }
}

function Install-ClaudeHook {
    $settingsPath = Join-Path $env:USERPROFILE ".claude\settings.json"
    $settings = Read-JsonObject $settingsPath
    $hooks = $settings.PSObject.Properties["hooks"]
    if ($null -eq $hooks -or $null -eq $hooks.Value) {
        Ensure-Property $settings "hooks" ([pscustomobject]@{})
        $hooksObject = $settings.hooks
    } else {
        $hooksObject = $hooks.Value
    }

    # Remove only this integration's old command, leaving all user hooks intact.
    foreach ($eventProperty in @($hooksObject.PSObject.Properties)) {
        $eventName = $eventProperty.Name
        $blocks = @($eventProperty.Value)
        $nextBlocks = New-Object System.Collections.Generic.List[object]
        foreach ($block in $blocks) {
            if ($null -eq $block) {
                continue
            }
            $blockHooksProperty = $block.PSObject.Properties["hooks"]
            if ($null -eq $blockHooksProperty) {
                $nextBlocks.Add($block)
                continue
            }

            $remaining = @($blockHooksProperty.Value | Where-Object {
                # Shell form puts the script path in `command`; exec form puts it
                # in `args`, so both have to be inspected to recognize our hook.
                $text = [string]$_.command + " " + (@($_.args) -join " ")
                $text -notmatch [regex]::Escape($marker)
            })
            if ($remaining.Count -gt 0) {
                Ensure-Property $block "hooks" $remaining
                $nextBlocks.Add($block)
            }
        }

        if ($nextBlocks.Count -eq 0) {
            $hooksObject.PSObject.Properties.Remove($eventName)
        } else {
            $hooksObject.PSObject.Properties[$eventName].Value = @($nextBlocks)
        }
    }

    # Exec form (command + args) avoids every cmd/PowerShell quoting pitfall on
    # Windows paths, and no -ExecutionPolicy Bypass is needed because CurrentUser
    # is RemoteSigned and this script is local.
    function New-NotifyHookBlock([string]$status) {
        return [pscustomobject]@{
            hooks = @([pscustomobject]@{
                type = "command"
                command = "powershell.exe"
                args = @(
                    "-NoProfile",
                    "-File",
                    $notifier,
                    "-Source",
                    "claude",
                    "-Status",
                    $status,
                    "-FromHook"
                )
                timeout = 10
            })
        }
    }

    $waitingBlock = New-NotifyHookBlock "waiting"
    Ensure-Property $waitingBlock "matcher" "permission_prompt|idle_prompt|agent_needs_input"
    Ensure-Property $hooksObject "Notification" @($waitingBlock)
    Ensure-Property $hooksObject "Stop" @((New-NotifyHookBlock "completed"))
    Ensure-Property $hooksObject "StopFailure" @((New-NotifyHookBlock "failed"))
    Ensure-Property $settings "hooks" $hooksObject

    New-Item -ItemType Directory -Path (Split-Path -Parent $settingsPath) -Force | Out-Null
    if (Test-Path -LiteralPath $settingsPath) {
        Copy-Item -LiteralPath $settingsPath -Destination ($settingsPath + ".lan-notify.bak") -Force
    }
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Write-Output "Claude Notification/Stop/StopFailure hooks installed: $settingsPath"
}

function Install-ChatGPTNotify {
    $configPath = Join-Path $env:USERPROFILE ".codex\config.toml"
    if (-not (Test-Path -LiteralPath $configPath)) {
        Write-Output "ChatGPT config not found, skipped: $configPath"
        return
    }

    $lines = @(Get-Content -LiteralPath $configPath -Encoding UTF8)
    $existing = ($lines | Where-Object { $_ -match '^\s*notify\s*=' } | Select-Object -First 1)
    $forwardFile = Join-Path $scriptDirectory "codex-notify-forward.txt"
    if ($existing -and $existing -notmatch "codex-notify\.ps1") {
        $existing | Set-Content -LiteralPath $forwardFile -Encoding UTF8
    }

    $notifyLine = 'notify = [ "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "' + ($codexNotifier -replace '\\', '\\') + '" ]'
    if ($existing) {
        $lines = @($lines | ForEach-Object {
            if ($_ -match '^\s*notify\s*=') { $notifyLine } else { $_ }
        })
    } else {
        $lines = @($notifyLine) + $lines
    }
    Copy-Item -LiteralPath $configPath -Destination ($configPath + ".lan-notify.bak") -Force
    $lines | Set-Content -LiteralPath $configPath -Encoding UTF8
    Write-Output "ChatGPT notify installed: $configPath"
    if ($existing) {
        Write-Output "Original notify line backed up to: $forwardFile"
    }
}

if (-not $ChatGPTOnly) {
    Install-ClaudeHook
}
if (-not $ClaudeOnly) {
    Install-ChatGPTNotify
}

Write-Output "Universal notifier: $notifier"
Write-Output "Trae, Zcode, and other tools can call the notifier or use run-agent-with-notify.ps1."
