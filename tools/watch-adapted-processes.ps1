[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$ScanIntervalSeconds = 5,

    [switch]$Once
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
try { [System.Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# Keep this map in sync with NotificationAppsPanel.NotifiableAppKeys. Unknown
# processes are deliberately ignored and never enter the active listener set.
$script:AdaptedApplications = @(
    [pscustomobject]@{ AppKey = "claude"; DisplayName = "Claude"; ProcessNames = @("Claude", "claude") },
    # The current desktop product runs as ChatGPT.exe. Keep codex as a
    # compatibility process name because the CLI/session protocol still uses it.
    [pscustomobject]@{ AppKey = "codex"; DisplayName = "ChatGPT"; ProcessNames = @("ChatGPT", "codex") },
    [pscustomobject]@{ AppKey = "trae"; DisplayName = "TraeCode CN"; ProcessNames = @("Trae CN", "TraeCode CN") },
    [pscustomobject]@{ AppKey = "trae-solo"; DisplayName = "Trae SOLO"; ProcessNames = @("TRAE SOLO CN") },
    [pscustomobject]@{ AppKey = "zcode"; DisplayName = "ZCode"; ProcessNames = @("zcode") },
    [pscustomobject]@{ AppKey = "doubao"; DisplayName = ([string]::Concat([char[]]@([char]0x8c46, [char]0x5305))); ProcessNames = @("Doubao") },
    [pscustomobject]@{ AppKey = "suyan"; DisplayName = ([string]::Concat([char[]]@([char]0x7d20, [char]0x8a00))); ProcessNames = @("suyan", ([string]::Concat([char[]]@([char]0x7d20, [char]0x8a00)))) },
    [pscustomobject]@{ AppKey = "baidunetdisk"; DisplayName = ([string]::Concat([char[]]@([char]0x767e, [char]0x5ea6, [char]0x7f51, [char]0x76d8))); ProcessNames = @("BaiduNetdisk", "BaiduNetdiskHost", "BaiduNetdiskRender") },
    [pscustomobject]@{ AppKey = "quarknetdisk"; DisplayName = ([string]::Concat([char[]]@([char]0x5938, [char]0x514b, [char]0x7f51, [char]0x76d8))); ProcessNames = @("quark_cloud_drive", "QuarkCloudDrive") },
    [pscustomobject]@{ AppKey = "adrive"; DisplayName = "aDrive"; ProcessNames = @("aDrive") },
    [pscustomobject]@{ AppKey = "workbuddy"; DisplayName = "WorkBuddy"; ProcessNames = @("WorkBuddy") }
)

$script:DefinitionByProcessName = @{}
$script:ProcessNames = @()
foreach ($definition in $script:AdaptedApplications) {
    foreach ($processName in $definition.ProcessNames) {
        $normalized = [System.IO.Path]::GetFileNameWithoutExtension($processName).Trim().ToLowerInvariant()
        if (-not $script:DefinitionByProcessName.ContainsKey($normalized)) {
            $script:DefinitionByProcessName[$normalized] = $definition
        }
        $script:ProcessNames += $processName
    }
}
$script:ProcessNames = @($script:ProcessNames | Select-Object -Unique)
$script:ActiveListeners = @{}
$script:StateLock = New-Object object
$script:OutputLock = New-Object object
$script:PendingProcessEvents = New-Object System.Collections.Concurrent.ConcurrentQueue[object]
$script:Watchers = New-Object System.Collections.Generic.List[object]

function Get-NormalizedProcessName {
    param([AllowNull()][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return ""
    }
    return [System.IO.Path]::GetFileNameWithoutExtension($Name.Trim()).ToLowerInvariant()
}

function Get-AdaptedDefinition {
    param([AllowNull()][string]$ProcessName)

    $normalized = Get-NormalizedProcessName -Name $ProcessName
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $null
    }
    if ($script:DefinitionByProcessName.ContainsKey($normalized)) {
        return $script:DefinitionByProcessName[$normalized]
    }
    return $null
}

function Write-JsonEvent {
    param([Parameter(Mandatory = $true)][hashtable]$Event)

    $json = $Event | ConvertTo-Json -Compress -Depth 6
    [System.Threading.Monitor]::Enter($script:OutputLock)
    try {
        [System.Console]::Out.WriteLine($json)
    }
    finally {
        [System.Threading.Monitor]::Exit($script:OutputLock)
    }
}

function Write-DiagnosticError {
    param([Parameter(Mandatory = $true)][string]$Message)

    [System.Console]::Error.WriteLine("watch-adapted-processes: " + $Message)
}

function New-ProcessRecord {
    param(
        [Parameter(Mandatory = $true)]$Definition,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$ProcessName,
        [AllowNull()][string]$StartTime = ""
    )

    return [pscustomobject]@{
        AppKey = $Definition.AppKey
        DisplayName = $Definition.DisplayName
        ProcessId = $ProcessId
        ProcessName = $ProcessName
        StartTime = $StartTime
    }
}

function Get-ProcessKey {
    param([Parameter(Mandatory = $true)]$Record)

    return "{0}|{1}" -f $Record.AppKey, $Record.ProcessId
}

function Publish-ProcessState {
    param(
        [Parameter(Mandatory = $true)][string]$Type,
        [Parameter(Mandatory = $true)]$Record,
        [Parameter(Mandatory = $true)][string]$ListenerState,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    Write-JsonEvent -Event ([ordered]@{
            type = $Type
            time = (Get-Date).ToUniversalTime().ToString("o")
            appKey = $Record.AppKey
            appName = $Record.DisplayName
            processName = $Record.ProcessName
            processId = $Record.ProcessId
            startTime = $Record.StartTime
            listenerState = $ListenerState
            reason = $Reason
        })
}

function Start-TrackedListener {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $key = Get-ProcessKey -Record $Record
    $added = $false
    [System.Threading.Monitor]::Enter($script:StateLock)
    try {
        if (-not $script:ActiveListeners.ContainsKey($key)) {
            $script:ActiveListeners[$key] = $Record
            $added = $true
        }
    }
    finally {
        [System.Threading.Monitor]::Exit($script:StateLock)
    }

    if ($added) {
        Publish-ProcessState -Type "process_started" -Record $Record -ListenerState "listening" -Reason $Reason
    }
}

function Stop-TrackedListener {
    param(
        [Parameter(Mandatory = $true)]$Record,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $key = Get-ProcessKey -Record $Record
    $removed = $false
    [System.Threading.Monitor]::Enter($script:StateLock)
    try {
        if ($script:ActiveListeners.ContainsKey($key)) {
            $script:ActiveListeners.Remove($key)
            $removed = $true
        }
    }
    finally {
        [System.Threading.Monitor]::Exit($script:StateLock)
    }

    if ($removed) {
        Publish-ProcessState -Type "process_stopped" -Record $Record -ListenerState "stopped" -Reason $Reason
    }
}

function Get-ProcessSnapshot {
    $snapshot = @{}
    try {
        # Only adapted names are queried. Unknown processes are never scanned.
        $processes = @(Get-Process -Name $script:ProcessNames -ErrorAction SilentlyContinue)
    }
    catch {
        Write-DiagnosticError -Message ("adapted process scan failed: " + $_.Exception.Message)
        return $snapshot
    }

    foreach ($process in $processes) {
        $definition = Get-AdaptedDefinition -ProcessName ([string]$process.ProcessName)
        if ($null -eq $definition) {
            continue
        }

        $startTime = ""
        try {
            $startTime = $process.StartTime.ToUniversalTime().ToString("o")
        }
        catch {
            # Elevated/system processes may not expose StartTime; PID is enough.
        }

        $record = New-ProcessRecord -Definition $definition -ProcessId ([int]$process.Id) -ProcessName ([string]$process.ProcessName) -StartTime $startTime
        $snapshot[(Get-ProcessKey -Record $record)] = $record
    }
    return $snapshot
}

function Sync-TrackedListeners {
    $current = Get-ProcessSnapshot
    foreach ($key in $current.Keys) {
        Start-TrackedListener -Record $current[$key] -Reason "scan"
    }

    $active = @{}
    [System.Threading.Monitor]::Enter($script:StateLock)
    try {
        foreach ($key in @($script:ActiveListeners.Keys)) {
            $active[$key] = $script:ActiveListeners[$key]
        }
    }
    finally {
        [System.Threading.Monitor]::Exit($script:StateLock)
    }

    foreach ($key in $active.Keys) {
        if (-not $current.ContainsKey($key)) {
            Stop-TrackedListener -Record $active[$key] -Reason "scan"
        }
    }
}

function Convert-EventProcessRecord {
    param(
        [Parameter(Mandatory = $true)]$EventData,
        [Parameter(Mandatory = $true)]$Definition
    )

    $processName = ([string]$EventData.ProcessName) -replace "(?i)\.exe$", ""
    $processId = 0
    try { $processId = [int]$EventData.ProcessID } catch { return $null }
    if ($processId -le 0) { return $null }

    return New-ProcessRecord -Definition $Definition -ProcessId $processId -ProcessName $processName
}

function Handle-ProcessStartEvent {
    param([Parameter(Mandatory = $true)]$EventData)

    try {
        $definition = Get-AdaptedDefinition -ProcessName ([string]$EventData.ProcessName)
        if ($null -eq $definition) { return }
        $record = Convert-EventProcessRecord -EventData $EventData -Definition $definition
        if ($null -ne $record) {
            Start-TrackedListener -Record $record -Reason "process-event"
        }
    }
    catch {
        Write-DiagnosticError -Message ("process start event handling failed: " + $_.Exception.Message)
    }
}

function Handle-ProcessStopEvent {
    param([Parameter(Mandatory = $true)]$EventData)

    try {
        $definition = Get-AdaptedDefinition -ProcessName ([string]$EventData.ProcessName)
        if ($null -eq $definition) { return }
        $record = Convert-EventProcessRecord -EventData $EventData -Definition $definition
        if ($null -ne $record) {
            Stop-TrackedListener -Record $record -Reason "process-event"
        }
    }
    catch {
        Write-DiagnosticError -Message ("process stop event handling failed: " + $_.Exception.Message)
    }
}

function New-WqlProcessNameClause {
    $clauses = foreach ($processName in $script:ProcessNames) {
        $escaped = ([string]$processName).Replace("'", "''") + ".exe"
        "ProcessName = '" + $escaped + "'"
    }
    return ($clauses -join " OR ")
}

function Start-ProcessEventWatchers {
    try {
        Add-Type -AssemblyName System.Management -ErrorAction Stop
        $where = New-WqlProcessNameClause
        $watcherDefinitions = @(
            [pscustomobject]@{ ClassName = "Win32_ProcessStartTrace"; Type = "start" },
            [pscustomobject]@{ ClassName = "Win32_ProcessStopTrace"; Type = "stop" }
        )

        foreach ($item in $watcherDefinitions) {
            $watcher = New-Object System.Management.ManagementEventWatcher
            $watcher.Query = "SELECT * FROM {0} WHERE {1}" -f $item.ClassName, $where
            $sourceIdentifier = "TapRelay.AdaptedProcess." + $item.Type + "." + [guid]::NewGuid().ToString("N")
            $queueMessage = [pscustomobject]@{
                Queue = $script:PendingProcessEvents
                Type = $item.Type
            }
            $subscription = Register-ObjectEvent -InputObject $watcher -EventName EventArrived -SourceIdentifier $sourceIdentifier -MessageData $queueMessage -Action {
                try {
                    $message = $Event.MessageData
                    $message.Queue.Enqueue([pscustomobject]@{
                            Type = $message.Type
                            EventData = $EventArgs.NewEvent
                        })
                }
                catch {
                    [System.Console]::Error.WriteLine("watch-adapted-processes: failed to queue process event: " + $_.Exception.Message)
                }
            }
            $watcher.Start()
            $script:Watchers.Add([pscustomobject]@{
                    Watcher = $watcher
                    SourceIdentifier = $sourceIdentifier
                    SubscriptionId = $subscription.Id
                })
        }
        return $true
    }
    catch {
        Write-DiagnosticError -Message ("Win32 process event watcher unavailable; using scan fallback: " + $_.Exception.Message)
        return $false
    }
}

function Stop-ProcessEventWatchers {
    foreach ($entry in $script:Watchers.ToArray()) {
        try { Unregister-Event -SourceIdentifier $entry.SourceIdentifier -ErrorAction SilentlyContinue } catch { }
        try { Remove-Job -Id $entry.SubscriptionId -Force -ErrorAction SilentlyContinue } catch { }
        try { $entry.Watcher.Stop() } catch { }
        try { $entry.Watcher.Dispose() } catch { }
    }
    $script:Watchers.Clear()
}

function Drain-ProcessEvents {
    $pending = $null
    while ($script:PendingProcessEvents.TryDequeue([ref]$pending)) {
        if ($pending.Type -eq "start") {
            Handle-ProcessStartEvent -EventData $pending.EventData
        }
        elseif ($pending.Type -eq "stop") {
            Handle-ProcessStopEvent -EventData $pending.EventData
        }
        $pending = $null
    }
}

function Publish-Snapshot {
    $active = @()
    [System.Threading.Monitor]::Enter($script:StateLock)
    try {
        foreach ($record in @($script:ActiveListeners.Values)) {
            $active += [ordered]@{
                appKey = $record.AppKey
                appName = $record.DisplayName
                processName = $record.ProcessName
                processId = $record.ProcessId
                startTime = $record.StartTime
                listenerState = "listening"
            }
        }
    }
    finally {
        [System.Threading.Monitor]::Exit($script:StateLock)
    }

    Write-JsonEvent -Event ([ordered]@{
            type = "process_snapshot"
            time = (Get-Date).ToUniversalTime().ToString("o")
            adaptedApplicationCount = $script:AdaptedApplications.Count
            activeProcessCount = $active.Count
            processes = $active
        })
}

function Start-AdaptedProcessMonitor {
    $eventWatcherEnabled = $false
    if (-not $Once) {
        $eventWatcherEnabled = Start-ProcessEventWatchers
    }

    Write-JsonEvent -Event ([ordered]@{
            type = "monitor_started"
            time = (Get-Date).ToUniversalTime().ToString("o")
            scanIntervalSeconds = $ScanIntervalSeconds
            eventWatcher = [bool]$eventWatcherEnabled
            adaptedApplications = @($script:AdaptedApplications | ForEach-Object {
                    [ordered]@{ appKey = $_.AppKey; appName = $_.DisplayName; processNames = @($_.ProcessNames) }
                })
        })

    Sync-TrackedListeners
    Publish-Snapshot

    if ($Once) {
        return
    }

    # WMI provides low-latency changes; the bounded scan is the recovery path for
    # missed events and removes listeners after process termination.
    $nextScan = (Get-Date).AddSeconds($ScanIntervalSeconds)
    while ($true) {
        Drain-ProcessEvents
        if ((Get-Date) -ge $nextScan) {
            Sync-TrackedListeners
            $nextScan = (Get-Date).AddSeconds($ScanIntervalSeconds)
        }
        Start-Sleep -Milliseconds 100
    }
}

try {
    Start-AdaptedProcessMonitor
}
finally {
    Stop-ProcessEventWatchers
}
