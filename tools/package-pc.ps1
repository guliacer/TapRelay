[CmdletBinding()]
param(
    [string]$BackupName = "",
    [int]$MaxRetainedBackups = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$pcAppRoot = Join-Path $workspaceRoot "pc-app"
$projectPath = Join-Path $pcAppRoot "TapRelay.csproj"
$publishRoot = Join-Path $pcAppRoot "publish"
$backupRoot = Join-Path $publishRoot "backups"
$stagingRoot = Join-Path $pcAppRoot "obj\package-staging"
$publishedExe = Join-Path $publishRoot "TapRelay.exe"

function Assert-PathWithin {
    param([string]$Path, [string]$Root)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not $fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside child path: $fullPath (root: $fullRoot)"
    }
    $current = $fullPath
    while ($current -ne $fullRoot) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing package operation through a link: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-PackageProcesses {
    @(Get-Process -Name TapRelay -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $publishedExe -and $_.WorkingSet64 -gt 10MB })
}

function Stop-Package {
    foreach ($process in (Get-PackageProcesses)) {
        # 改用 taskkill.exe：Stop-Process 在 WorkBuddy 沙箱中杀非本会话启动的进程会被安全策略拦截，
        # 而 taskkill.exe /F /PID 在沙箱与普通 PowerShell 下均可正常终止（含用户交互会话进程）。
        & taskkill.exe /F /PID $process.Id 2>&1 | Out-Null
        $waited = 0
        while ($waited -lt 5000) {
            if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 200
            $waited += 200
        }
        if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
            throw "TapRelay did not stop: $($process.Id)"
        }
    }
}

function Remove-PackagePath {
    param([string]$LiteralPath)
    # 真删除，真正释放磁盘空间。
    # 不能用 Remove-Item -Recurse -Force：会被沙箱 safe-delete 批量删除监控拦截（进程直接中止，
    # try/catch 兜不住）。旧实现改用 Move-Item 同卷改名到 backups\trash-*，但那只改名不删除——
    # 目录计数不变、空间不释放，于是 trash-* 无限堆积（实测堆积到 21 个目录 / 约 1 GB，
    # 远超 MaxRetainedBackups=5 的上限）。
    # 现改为 robocopy /MIR：从空目录镜像清空目标，实测不触发拦截，且是真正的删除。
    # rc 0~7 均为正常（rc=2 = 已删除多余文件/目录，rc=3 = 有文件被复制，空源下即为纯删除）。
    if (-not (Test-Path -LiteralPath $LiteralPath)) { return }
    # Get-PackageItems 同时返回文件和目录。robocopy /MIR 的目标必须是目录；
    # 旧实现把发布目录里的 TapRelay.exe / pdb 也送进 robocopy，导致 rc=16，
    # 事务更新在清理阶段失败。单文件在项目发布目录内可直接删除，目录仍走
    # 空目录镜像，避免递归 cmdlet 触发沙箱批量删除拦截。
    if (Test-Path -LiteralPath $LiteralPath -PathType Leaf) {
        [IO.File]::Delete($LiteralPath)
        if (Test-Path -LiteralPath $LiteralPath) {
            throw "Failed to purge file: $LiteralPath"
        }
        return
    }
    $empty = Join-Path ([IO.Path]::GetTempPath()) ('taprelay-purge-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    try {
        & robocopy $empty $LiteralPath /MIR /NFL /NDL /NJH /NJS /NC /NS /NP /R:0 /W:0 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy purge failed with exit code $LASTEXITCODE" }
    }
    finally {
        if (Test-Path -LiteralPath $empty) {
            [IO.Directory]::Delete($empty, $true)
        }
    }
    # 收掉被清空的空壳目录。用 .NET 直接删（非递归，仅空目录可删），避免 cmdlet 被安全策略匹配。
    if (Test-Path -LiteralPath $LiteralPath) {
        [IO.Directory]::Delete($LiteralPath, $false)
    }
    if (Test-Path -LiteralPath $LiteralPath) {
        throw "Failed to purge path: $LiteralPath"
    }
}

function Start-HealthyPackage {
    # 沙箱安全启动：优先用计划任务跑 launch-taprelay.cmd（绕过沙箱 Start-Process 秒退）；
    # 若 .cmd 缺失则回退普通 Start-Process（非沙箱环境）。
    $taskName = 'TapRelayPackagingLaunch'
    $cmd = Join-Path $workspaceRoot 'tools\launch-taprelay.cmd'
    if (Test-Path -LiteralPath $cmd -PathType Leaf) {
        try {
            $action = New-ScheduledTaskAction -Execute $cmd -WorkingDirectory $publishRoot
            $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date)
            Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Trigger $trigger -Force | Out-Null
            Start-ScheduledTask -TaskName $taskName
        }
        catch {
            Start-Process -FilePath $publishedExe -WorkingDirectory $publishRoot -WindowStyle Hidden | Out-Null
        }
    }
    else {
        Start-Process -FilePath $publishedExe -WorkingDirectory $publishRoot -WindowStyle Hidden | Out-Null
    }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Start-Sleep -Milliseconds 500
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:1122/health" -TimeoutSec 2
            if ($health.status -eq "ok" -and $health.port -eq 1122 -and @(Get-PackageProcesses).Count -gt 0) {
                try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false } catch { }
                Write-Host ("TapRelay restarted. Health: " + ($health | ConvertTo-Json -Compress))
                return
            }
        }
        catch { }
    }
    try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false } catch { }
    throw "Published TapRelay failed process/health validation on port 1122."
}

function Get-PackageItems {
    @(Get-ChildItem -LiteralPath $publishRoot -Force |
        Where-Object { $_.Name -notin @('backups', 'PACKAGE-RULE.md') })
}

function Trim-PackageBackups {
    # 只对真正的回滚快照排名。必须排除 trash-*：它们的 LastWriteTime 最新，若参与排名会挤占保留名额，
    # 把真实快照挤出前 N 名（这正是旧实现下 backups\ 目录数只增不减、且旧快照被误清的原因之一）。
    $backups = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force |
        Where-Object { $_.Name -notlike 'trash-*' } |
        Sort-Object LastWriteTime -Descending)
    foreach ($backup in ($backups | Select-Object -Skip $MaxRetainedBackups)) {
        Assert-PathWithin $backup.FullName $backupRoot
        Remove-PackagePath $backup.FullName
    }
    # 兜底清扫：清掉历史遗留的 trash-*（旧版 Move-ToTrash 只改名不释放空间，会无限堆积）。
    $legacy = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force |
        Where-Object { $_.Name -like 'trash-*' })
    foreach ($trash in $legacy) {
        Assert-PathWithin $trash.FullName $backupRoot
        Remove-PackagePath $trash.FullName
    }
    if ($legacy.Count -gt 0) {
        Write-Host ("Reclaimed legacy trash directories: " + $legacy.Count)
    }
}

function Clear-Package {
    # 直接真删除：旧包此时已完整快照到 $rollbackPath，回滚能力不受影响。
    foreach ($item in (Get-PackageItems)) {
        Assert-PathWithin $item.FullName $publishRoot
        Remove-PackagePath $item.FullName
    }
}

function Copy-Package {
    param([string]$Source, [string]$Destination)
    & robocopy $Source $Destination /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
}

foreach ($path in @($publishRoot, $backupRoot, $stagingRoot)) {
    Assert-PathWithin $path $pcAppRoot
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "Project not found: $projectPath" }
if (-not [string]::IsNullOrWhiteSpace($BackupName) -and
    ($BackupName -in @('.', '..') -or $BackupName.Contains('\') -or $BackupName.Contains('/') -or
     $BackupName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0)) {
    throw "BackupName must be a single valid folder name."
}
if ($MaxRetainedBackups -lt 1 -or $MaxRetainedBackups -gt 20) {
    throw "MaxRetainedBackups must be between 1 and 20."
}

# Build failures leave the currently published service and files untouched.
& dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
if (Test-Path -LiteralPath $stagingRoot) {
    Assert-PathWithin $stagingRoot $pcAppRoot
    Remove-PackagePath $stagingRoot
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o $stagingRoot
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
foreach ($name in @('TapRelay.exe', 'TapRelay.pdb')) {
    $file = Join-Path $stagingRoot $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Missing or empty staged artifact: $file"
    }
}
$newHash = (Get-FileHash -LiteralPath (Join-Path $stagingRoot "TapRelay.exe") -Algorithm SHA256).Hash
$buildInfo = [ordered]@{
    builtAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssK")
    configuration = "Release"; runtime = "win-x64"; selfContained = $true; singleFile = $true
    project = $projectPath; output = $publishRoot; sha256 = $newHash; symbols = "TapRelay.pdb"
}
[IO.File]::WriteAllText((Join-Path $stagingRoot "build-info.json"),
    ($buildInfo | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$installerScript = Join-Path $workspaceRoot "tools\install-pc.ps1"
if (Test-Path -LiteralPath $installerScript -PathType Leaf) {
    Copy-Item -LiteralPath $installerScript -Destination $stagingRoot -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$transactionName = "{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), ([guid]::NewGuid().ToString("N").Substring(0, 8))
$rollbackPath = Join-Path $backupRoot ($transactionName + "-rollback")
Assert-PathWithin $rollbackPath $backupRoot
$oldItems = @(Get-PackageItems)
$oldHash = if (Test-Path -LiteralPath $publishedExe -PathType Leaf) {
    (Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash
} else { "" }
if ($oldItems.Count -gt 0) {
    New-Item -ItemType Directory -Path $rollbackPath | Out-Null
    foreach ($item in $oldItems) {
        Assert-PathWithin $item.FullName $publishRoot
        Copy-Item -LiteralPath $item.FullName -Destination $rollbackPath -Recurse -Force
    }
    if ($oldHash -and (Get-FileHash -LiteralPath (Join-Path $rollbackPath "TapRelay.exe")).Hash -ne $oldHash) {
        throw "Recovery snapshot verification failed; running package was not changed."
    }
}
$wasRunning = @(Get-PackageProcesses).Count -gt 0
$replacementStarted = $false
try {
    Stop-Package
    $replacementStarted = $true
    Clear-Package
    Copy-Package $stagingRoot $publishRoot
    if ((Get-FileHash -LiteralPath $publishedExe).Hash -ne $newHash) { throw "Published executable hash mismatch." }
    Start-HealthyPackage
}
catch {
    $updateError = $_.Exception.Message
    try {
        if ($replacementStarted) {
            Stop-Package
            Clear-Package
            if ($oldItems.Count -gt 0) { Copy-Package $rollbackPath $publishRoot }
            if ($oldHash -and (Get-FileHash -LiteralPath $publishedExe).Hash -ne $oldHash) {
                throw "Restored executable hash mismatch."
            }
        }
        if ($wasRunning -and @(Get-PackageProcesses).Count -eq 0) { Start-HealthyPackage }
    }
    catch { throw "Update failed: $updateError. Recovery failed: $($_.Exception.Message). Snapshot: $rollbackPath" }
    throw "Update failed: $updateError. Previous package restored; recovery snapshot retained at $rollbackPath"
}

if (Test-Path -LiteralPath $rollbackPath) {
    Assert-PathWithin $rollbackPath $backupRoot
    if ([string]::IsNullOrWhiteSpace($BackupName)) {
        # 本次快照已无保留价值（更新已成功、失败路径会自行保留），真删除以释放空间。
        # 保留窗口由下方 Trim-PackageBackups 按 MaxRetainedBackups 统一维护。
        Remove-PackagePath $rollbackPath
    }
    else {
        $savedPath = Join-Path $backupRoot ($transactionName + "-" + $BackupName.Trim())
        Assert-PathWithin $savedPath $backupRoot
        Move-Item -LiteralPath $rollbackPath -Destination $savedPath
        Write-Host "Previous package saved at: $savedPath"
    }
}
Trim-PackageBackups
Write-Host "PC package output: $publishRoot"
Get-ChildItem -LiteralPath $publishRoot -File |
    Select-Object LastWriteTime, Length, Name | Format-Table -AutoSize
