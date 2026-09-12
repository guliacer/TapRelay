param(
    [string]$Source = "agent",
    [ValidateSet("completed", "failed", "waiting")]
    [string]$Status = "completed",
    [string]$Message = "",
    [string]$TaskId = "",
    [string]$Cwd = "",
    [string]$ProjectName = "",
    [long]$DurationMs = 0,
    [switch]$FromHook,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = "Stop"
$logDirectory = Join-Path $env:LOCALAPPDATA "LanNotify"
$logPath = Join-Path $logDirectory "ai-notify.log"

function Write-Diagnostic([string]$text) {
    try {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        Add-Content -LiteralPath $logPath -Value ("{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $text) -Encoding UTF8
    } catch {
        # A notification hook must never break the agent that called it.
    }
}

function Get-PropertyValue($object, [string[]]$names) {
    if ($null -eq $object) {
        return ""
    }

    foreach ($name in $names) {
        $property = $object.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value) {
            $value = [string]$property.Value
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return $value.Trim()
            }
        }
    }

    return ""
}

function ConvertTo-DurationMs($value) {
    $parsed = 0L
    if ($null -eq $value -or
        -not [long]::TryParse(([string]$value).Trim(), [ref]$parsed) -or
        $parsed -le 0 -or
        $parsed -gt 2592000000L) {
        return 0L
    }
    return $parsed
}

function ConvertTo-AgentMarker([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return "" }
    return (($value.Trim().ToLowerInvariant()) -replace '[_\.\-\s]', '')
}

function Test-EmptyAgentMarker($value) {
    if ($null -eq $value) { return $true }
    $normalized = ConvertTo-AgentMarker ([string]$value)
    return $normalized -in @("", "0", "false", "undefined", "null", "none", "unknown", "primary", "main", "user", "normal")
}

function Test-TruthyAgentMarker($value) {
    if ($value -is [bool]) { return [bool]$value }
    $normalized = ConvertTo-AgentMarker ([string]$value)
    return $normalized -in @("true", "1", "yes")
}

function Test-SubagentRole($value) {
    $normalized = ConvertTo-AgentMarker ([string]$value)
    return $normalized -in @("subagent", "sidechain")
}

function Test-IsExplicitSubagent($object, [int]$depth = 0) {
    if ($null -eq $object -or $depth -gt 32) { return $false }
    if ($object -is [string] -or $object.GetType().IsPrimitive) { return $false }

    if ($object -is [System.Collections.IEnumerable] -and
        $object -isnot [System.Management.Automation.PSCustomObject] -and
        $object -isnot [System.Collections.IDictionary]) {
        foreach ($item in @($object)) {
            if (Test-IsExplicitSubagent $item ($depth + 1)) { return $true }
        }
        return $false
    }

    $properties = @($object.PSObject.Properties)
    foreach ($property in $properties) {
        $name = ConvertTo-AgentMarker $property.Name
        if (($name -eq "issidechain" -or $name -eq "issubagent") -and
            (Test-TruthyAgentMarker $property.Value)) {
            return $true
        }
        if ($name -eq "threadsource" -and (Test-SubagentRole $property.Value)) {
            return $true
        }
        if ($name -eq "subagenttype" -and -not (Test-EmptyAgentMarker $property.Value)) {
            return $true
        }
        if ($name -in @("agenttype", "chattype", "messagetype") -and
            (Test-SubagentRole $property.Value)) {
            return $true
        }
        if ($name -eq "source" -and $null -ne $property.Value) {
            $subagentProperty = @($property.Value.PSObject.Properties | Where-Object {
                (ConvertTo-AgentMarker $_.Name) -eq "subagent"
            } | Select-Object -First 1)
            if ($subagentProperty.Count -gt 0 -and -not (Test-EmptyAgentMarker $subagentProperty[0].Value)) {
                return $true
            }
        }
    }

    foreach ($property in $properties) {
        if (Test-IsExplicitSubagent $property.Value ($depth + 1)) { return $true }
    }
    return $false
}

function Read-JsonCandidates {
    $candidates = New-Object System.Collections.Generic.List[string]

    try {
        if ([Console]::IsInputRedirected) {
            # Windows PowerShell 5.1 decodes [Console]::In with the console OEM
            # code page (936/GBK here), which corrupts the UTF-8 bytes Claude
            # Code writes on stdin and makes any payload containing Chinese fail
            # to parse. Decode the raw stdin stream as UTF-8 instead.
            $stdinStream = [Console]::OpenStandardInput()
            $stdinEncoding = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
            $stdinReader = New-Object -TypeName System.IO.StreamReader -ArgumentList @($stdinStream, $stdinEncoding)
            try {
                $stdin = $stdinReader.ReadToEnd()
            } finally {
                $stdinReader.Dispose()
            }
            if (-not [string]::IsNullOrWhiteSpace($stdin)) {
                $candidates.Add($stdin.Trim())
            }
        }
    } catch {
        Write-Diagnostic ("stdin read failed: " + $_.Exception.Message)
    }

    if ($Arguments) {
        foreach ($argument in $Arguments) {
            if (-not [string]::IsNullOrWhiteSpace($argument)) {
                $candidates.Add($argument.Trim())
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (-not $candidate.StartsWith("{")) {
            continue
        }

        try {
            return $candidate | ConvertFrom-Json
        } catch {
            if ($candidate -notmatch '(?i)(?:^|[,{\s])type:agent-turn-(?:complete|failed)(?:[,}\s]|$)') {
                Write-Diagnostic "ignored invalid JSON event"
            }
        }
    }

    return $null
}

function Get-PlainChatGPTEvent {
    if (-not $Arguments) {
        return $null
    }

    $raw = ($Arguments -join " ").Trim()
    if ($raw -notmatch '(?i)(?:^|[,{\s])type:(agent-turn-complete|task_complete|turn-complete|agent-turn-failed|task_failed|turn-failed)(?:[,}\s]|$)') {
        return $null
    }

    $eventType = $matches[1]
    $turnId = ""
    $cwd = ""
    if ($raw -match '(?i)(?:^|[,{\s])turn-id:([^,}\s]+)') {
        $turnId = $matches[1]
    }
    if ($raw -match '(?i)(?:^|[,{\s])cwd:([^,}]+)') {
        $cwd = $matches[1].Trim()
    }

    return [pscustomobject]@{
        type = $eventType
        turn_id = $turnId
        cwd = $cwd
    }
}

function Get-PlainEventName {
    $plainChatGPTEvent = Get-PlainChatGPTEvent
    if ($null -ne $plainChatGPTEvent) {
        return [string]$plainChatGPTEvent.type
    }

    if (-not $Arguments) {
        return ""
    }

    foreach ($argument in $Arguments) {
        if ([string]::IsNullOrWhiteSpace($argument)) {
            continue
        }

        $value = $argument.Trim()
        if ($value.StartsWith("--")) {
            continue
        }

        return $value
    }

    return ""
}

function Trim-Message([string]$value) {    if ([string]::IsNullOrWhiteSpace($value)) {
        return "Task completed"
    }

    $clean = $value.Trim()
    if ($clean.Length -gt 460) {
        return $clean.Substring(0, 460) + " [truncated]"
    }

    return $clean
}

function Get-Sha256([string]$value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($value)
    $hash = [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    return ([BitConverter]::ToString($hash) -replace "-", "").ToLowerInvariant()
}

# The Codex turn-ended hook has no authoritative success/failure result.
# Resolve the matching task_complete record from the rollout instead. This
# keeps the hook and transcript parser on the same turn id and status.
# If the record is not visible yet, return Found=$false so the transcript
# parser can publish it without an optimistic completion notification.
function Resolve-CodexTurnStatus([string]$threadId, [string]$turnId) {
    $result = @{ Found = $false; IsSubagent = $false; Status = "completed"; Message = ""; DurationMs = 0L }
    if ([string]::IsNullOrWhiteSpace($turnId)) { return $result }
    $sessRoot = Join-Path $env:USERPROFILE ".codex\sessions"
    if (-not (Test-Path -LiteralPath $sessRoot)) { return $result }

    # Read only the tail: task_complete is the last event in the rollout.
    # Seeking avoids the cost of loading very long JSONL lines.
    function Read-TailText([string]$path, [int]$maxBytes) {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $len = $fs.Length
            $start = [Math]::Max(0, $len - $maxBytes)
            [void]$fs.Seek($start, [System.IO.SeekOrigin]::Begin)
            $count = [int]($len - $start)
            $buf = New-Object byte[] $count
            $read = $fs.Read($buf, 0, $count)
            $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
            if ($start -gt 0) { $nl = $text.IndexOf("`n"); if ($nl -ge 0) { $text = $text.Substring($nl + 1) } }
            return $text
        } finally { $fs.Dispose() }
    }

    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $files = @()
        try {
            if (-not [string]::IsNullOrWhiteSpace($threadId)) {
                $files = @(Get-ChildItem -Path $sessRoot -Recurse -Filter "*$threadId*.jsonl" -ErrorAction SilentlyContinue)
            }
            if (-not $files -or $files.Count -eq 0) {
                $files = @(Get-ChildItem -Path $sessRoot -Recurse -Filter "*.jsonl" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 2)
            }
        } catch { $files = @() }

        foreach ($f in $files) {
            $text = $null
            try { $text = Read-TailText $f.FullName 1048576 } catch { continue }
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $lines = $text -split "`n"
            for ($i = $lines.Count - 1; $i -ge 0; $i--) {
                $ln = $lines[$i]
                if ($ln.IndexOf('task_complete') -lt 0) { continue }
                if ($ln.IndexOf($turnId) -lt 0) { continue }
                $obj = $null
                try { $obj = $ln | ConvertFrom-Json } catch { continue }
                $p = $obj
                if ($null -ne $obj.PSObject.Properties["payload"] -and $null -ne $obj.payload) { $p = $obj.payload }
                if ("$($p.type)" -ne "task_complete") { continue }
                if ("$($p.turn_id)" -ne $turnId) { continue }

                $sessionMeta = $null
                try {
                    $stream = [System.IO.File]::Open(
                        $f.FullName,
                        [System.IO.FileMode]::Open,
                        [System.IO.FileAccess]::Read,
                        [System.IO.FileShare]::ReadWrite)
                    $encoding = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
                    $reader = New-Object -TypeName System.IO.StreamReader -ArgumentList @($stream, $encoding)
                    try {
                        while (-not $reader.EndOfStream) {
                            $metaLine = $reader.ReadLine()
                            if ([string]::IsNullOrWhiteSpace($metaLine)) { continue }
                            try { $sessionMeta = $metaLine | ConvertFrom-Json } catch { $sessionMeta = $null }
                            break
                        }
                    } finally { $reader.Dispose() }
                } catch { $sessionMeta = $null }
                if ($null -ne $sessionMeta -and (Test-IsExplicitSubagent $sessionMeta)) {
                    return @{ Found = $true; IsSubagent = $true; Status = "completed"; Message = ""; DurationMs = 0L }
                }

                $errObj = $null
                if ($p.PSObject.Properties["error"]) { $errObj = $p.error }
                $errInfo = ""
                if ($p.PSObject.Properties["codex_error_info"]) { $errInfo = [string]$p.codex_error_info }
                $isFail = $false
                if ($null -ne $errObj) {
                    if ($errObj -is [string]) { $isFail = -not [string]::IsNullOrWhiteSpace($errObj) }
                    else { $isFail = ($errObj.PSObject.Properties.Count -gt 0) }
                }
                if (-not $isFail -and -not [string]::IsNullOrWhiteSpace($errInfo)) { $isFail = $true }
                $resolvedDurationMs = ConvertTo-DurationMs (Get-PropertyValue $p @("duration_ms", "durationMs"))

                if ($isFail) {
                    $msg = ""
                    if ($null -ne $errObj) {
                        if ($errObj -isnot [string] -and $errObj.PSObject.Properties["message"]) { $msg = [string]$errObj.message }
                        else { $msg = [string]$errObj }
                    }
                    return @{ Found = $true; IsSubagent = $false; Status = "failed"; Message = $msg; DurationMs = $resolvedDurationMs }
                }
                $done = ""
                if ($p.PSObject.Properties["last_agent_message"]) { $done = [string]$p.last_agent_message }
                return @{ Found = $true; IsSubagent = $false; Status = "completed"; Message = $done; DurationMs = $resolvedDurationMs }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $result
}

function Get-ClaudeEventId($payload, [string]$message, [string]$eventName, [string]$status) {
    $sessionId = Get-PropertyValue $payload @("session_id", "sessionId")
    $promptId = Get-PropertyValue $payload @("prompt_id", "promptId")
    if ([string]::IsNullOrWhiteSpace($sessionId)) {
        return ""
    }

    # Notification events repeat verbatim: every tool-permission prompt in a
    # session carries the same `message` text, so a message fingerprint would
    # collide and the PC server would drop the second prompt as a duplicate.
    # Waiting events are therefore keyed by wall-clock time instead.
    if ($status -eq "waiting") {
        $identity = @($sessionId, $eventName, $message, [DateTimeOffset]::Now.ToString("O")) -join "|"
        return $sessionId + ":waiting:" + (Get-Sha256 $identity).Substring(0, 24)
    }

    # A session can contain many turns. The session id alone is therefore not
    # a valid deduplication key. Prefer the final response fingerprint so the
    # hook and transcript fallback converge on the same event id.
    if (-not [string]::IsNullOrWhiteSpace($message)) {
        return $sessionId + ":" + (Get-Sha256 ($sessionId + "|" + $message))
    }
    if (-not [string]::IsNullOrWhiteSpace($promptId)) {
        return $sessionId + ":" + $promptId
    }

    $timestamp = Get-PropertyValue $payload @("timestamp", "stop_time", "stopTime")
    if (-not [string]::IsNullOrWhiteSpace($timestamp)) {
        return $sessionId + ":" + $timestamp
    }

    return $sessionId + ":" + $eventName
}

try {
    $event = Read-JsonCandidates
    if ($null -eq $event -and $Source -eq "codex") {
        $event = Get-PlainChatGPTEvent
    }
    $eventPayload = $event
    if ($null -ne $event -and $null -ne $event.PSObject.Properties["payload"] -and $null -ne $event.payload) {
        $eventPayload = $event.payload
    }

    $plainEventName = Get-PlainEventName

    $eventSource = Get-PropertyValue $eventPayload @("source", "tool", "provider")
    if ([string]::IsNullOrWhiteSpace($eventSource)) {
        $eventSource = Get-PropertyValue $event @("source", "tool", "provider")
    }
    if ($Source -eq "agent" -and $eventSource) {
        $Source = $eventSource
    }
    if ([string]::IsNullOrWhiteSpace($Source)) {
        $Source = "agent"
    }
    $Source = $Source.Trim().ToLowerInvariant()
    if ($Source.Length -gt 40) {
        $Source = $Source.Substring(0, 40)
    }

    $eventName = Get-PropertyValue $eventPayload @("hook_event_name", "hookEventName", "event", "type", "status")
    if ([string]::IsNullOrWhiteSpace($eventName)) {
        $eventName = Get-PropertyValue $event @("hook_event_name", "hookEventName", "event", "type", "status")
    }
    if ([string]::IsNullOrWhiteSpace($eventName)) {
        $eventName = $plainEventName
    }
    if (($Source -eq "claude" -and (ConvertTo-AgentMarker $eventName) -eq "subagentstop") -or
        (Test-IsExplicitSubagent $event)) {
        Write-Diagnostic ("ignored subagent hook source=" + $Source + " event=" + $eventName)
        exit 0
    }
    $eventStatus = $eventName
    if ([string]::IsNullOrWhiteSpace($eventStatus)) {
        $eventStatus = Get-PropertyValue $event @("status", "event", "type")
    }
    if ($Source -eq "codex" -and $eventName -match "complete|finish|success") {
        $Status = "completed"
    } elseif ($Status -eq "completed" -and $eventStatus -match "fail|error|abort|cancel") {
        $Status = "failed"
    } elseif ($Status -eq "completed" -and $eventStatus -match "wait|approval|input") {
        $Status = "waiting"
    }

    if ([string]::IsNullOrWhiteSpace($Cwd)) {
        $Cwd = Get-PropertyValue $eventPayload @("cwd", "workdir", "working_directory", "directory")
    }
    if ([string]::IsNullOrWhiteSpace($Cwd)) {
        $Cwd = Get-PropertyValue $event @("cwd", "workdir", "working_directory", "directory")
    }
    if ($DurationMs -le 0) {
        $DurationMs = ConvertTo-DurationMs (Get-PropertyValue $eventPayload @("durationMs", "duration_ms"))
    }
    if ($DurationMs -le 0) {
        $DurationMs = ConvertTo-DurationMs (Get-PropertyValue $event @("durationMs", "duration_ms"))
    }

    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = Get-PropertyValue $eventPayload @(
            "last_assistant_message",
            "lastAssistantMessage",
            "message",
            "last_agent_message",
            "summary",
            "title",
            "text",
            "error"
        )
    }
    if ([string]::IsNullOrWhiteSpace($Message)) {
        $Message = Get-PropertyValue $event @(
            "last_assistant_message",
            "lastAssistantMessage",
            "message",
            "last_agent_message",
            "summary",
            "title",
            "text",
            "error"
        )
    }

    if ([string]::IsNullOrWhiteSpace($TaskId)) {
        $TaskId = Get-PropertyValue $eventPayload @("taskId", "task_id", "turn_id", "turn-id", "conversation_id")
    }
    if ([string]::IsNullOrWhiteSpace($TaskId)) {
        $TaskId = Get-PropertyValue $event @("taskId", "task_id", "turn_id", "turn-id", "conversation_id")
    }
    if ($Source -eq "claude" -and [string]::IsNullOrWhiteSpace($TaskId)) {
        $TaskId = Get-ClaudeEventId $eventPayload $Message $eventName $Status
    }

    # The turn-ended hook does not distinguish success from failure. Resolve
    # the matching task_complete and use its turn id so the transcript parser
    # and hook converge on one deduplicated notification.
    if ($Source -eq "codex" -and $FromHook -and $Status -ne "waiting") {
        $codexThread = Get-PropertyValue $eventPayload @("thread_id", "thread-id", "threadId", "conversation_id", "session_id", "sessionId")
        if ([string]::IsNullOrWhiteSpace($codexThread)) {
            $codexThread = Get-PropertyValue $event @("thread_id", "thread-id", "threadId", "conversation_id", "session_id", "sessionId")
        }
        $codexTurn = Get-PropertyValue $eventPayload @("turn_id", "turn-id", "turnId")
        if ([string]::IsNullOrWhiteSpace($codexTurn)) {
            $codexTurn = Get-PropertyValue $event @("turn_id", "turn-id", "turnId")
        }
        if ([string]::IsNullOrWhiteSpace($codexTurn)) {
            # A plain `turn-ended` callback has no task identity and is not
            # proof that a turn completed. Never manufacture a random task id.
            Write-Diagnostic "codex hook deferred to transcript parser (missing turn_id)"
            exit 0
        }

        $resolved = Resolve-CodexTurnStatus $codexThread $codexTurn
        if ($resolved.Found) {
            if ($resolved.IsSubagent) {
                Write-Diagnostic ("ignored codex subagent hook turnId=" + $codexTurn)
                exit 0
            }
            $Status = $resolved.Status
            $TaskId = $codexTurn
            $DurationMs = ConvertTo-DurationMs $resolved.DurationMs
            if (-not [string]::IsNullOrWhiteSpace($resolved.Message)) { $Message = $resolved.Message }
        } else {
            Write-Diagnostic ("codex hook deferred to transcript parser (task_complete unresolved) turnId=" + $codexTurn)
            exit 0
        }
    }

    # Non-hook integrations may still provide a plain event name. Keep that
    # compatibility fallback, but only after Codex Hook validation above.
    if ($Source -eq "codex" -and [string]::IsNullOrWhiteSpace($TaskId)) {
        $identity = @(
            $Source,
            $eventName,
            ($Arguments -join ([string][char]31)),
            $Cwd,
            [DateTimeOffset]::Now.ToString("O")
        ) -join "|"
        $TaskId = "codex-hook-" + (Get-Sha256 $identity).Substring(0, 20)
    }

    if ([string]::IsNullOrWhiteSpace($Message) -and $Source -eq "codex") {
        $Message = "ChatGPT task completed"
    }
    if ($Source -eq "codex" -and $Message -eq "ChatGPT task completed") {
        $Message = "ChatGPT task completed"
    }
    $Message = Trim-Message $Message
    $prefix = ""
    if ($Source -eq "claude") {
        if ($Status -eq "failed") {
            $prefix = "Claude task failed: "
        } elseif ($Status -eq "completed") {
            $prefix = "Claude task completed: "
        }
    } elseif ($Status -eq "failed") {
        $prefix = "Task failed: "
    }

    if ($Status -eq "failed" -and $Message -notmatch "fail|error") {
        $Message = $prefix + $Message
    } elseif ($Status -eq "waiting" -and $Message -notmatch "wait|approval|input") {
        $Message = "Waiting for input: " + $Message
    } elseif ($Status -eq "completed" -and $Message -notmatch "complete|finished|done") {
        if ([string]::IsNullOrWhiteSpace($prefix)) {
            $prefix = "Task completed: "
        }
        $Message = $prefix + $Message
    }

    $payload = [ordered]@{
        message = $Message
        source = $Source
        status = $Status
    }
    if (-not [string]::IsNullOrWhiteSpace($TaskId)) {
        $payload.taskId = $TaskId.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($Cwd)) {
        $payload.cwd = $Cwd.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($ProjectName)) {
        $payload.projectName = $ProjectName.Trim()
    }
    if ($DurationMs -gt 0) {
        $payload.durationMs = $DurationMs
    }
    if ($Source -eq "claude") {
        $payload.event = $eventName
        $payload.sessionId = Get-PropertyValue $eventPayload @("session_id", "sessionId")
        $payload.promptId = Get-PropertyValue $eventPayload @("prompt_id", "promptId")
    }

    $json = $payload | ConvertTo-Json -Compress
    $body = [Text.Encoding]::UTF8.GetBytes($json)
    $response = Invoke-RestMethod `
        -Uri "http://127.0.0.1:1122/send" `
        -Method Post `
        -ContentType "application/json; charset=utf-8" `
        -Body $body `
        -TimeoutSec 5

    $resultStatus = Get-PropertyValue $response @("status")
    if ([string]::IsNullOrWhiteSpace($resultStatus)) { $resultStatus = "sent" }
    $clientCount = Get-PropertyValue $response @("clients")
    Write-Diagnostic ("send result source=" + $Source + " status=" + $Status + " event=" + $eventName + " taskId=" + $TaskId + " result=" + $resultStatus + " clients=" + $clientCount)
} catch {
    Write-Diagnostic ("send failed source=" + $Source + ": " + $_.Exception.Message)
}

exit 0
