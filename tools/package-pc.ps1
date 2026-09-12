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
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        if (-not $process.WaitForExit(5000)) { throw "TapRelay did not stop: $($process.Id)" }
    }
}

function Start-HealthyPackage {
    Start-Process -FilePath $publishedExe -WorkingDirectory $publishRoot -WindowStyle Hidden | Out-Null
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        Start-Sleep -Milliseconds 500
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:1122/health" -TimeoutSec 2
            if ($health.status -eq "ok" -and $health.port -eq 1122 -and @(Get-PackageProcesses).Count -gt 0) {
                Write-Host ("TapRelay restarted. Health: " + ($health | ConvertTo-Json -Compress))
                return
            }
        }
        catch { }
    }
    throw "Published TapRelay failed process/health validation on port 1122."
}

function Get-PackageItems {
    @(Get-ChildItem -LiteralPath $publishRoot -Force |
        Where-Object { $_.Name -notin @('backups', 'PACKAGE-RULE.md') })
}

function Trim-PackageBackups {
    $backups = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Force |
        Sort-Object LastWriteTime -Descending)
    foreach ($backup in ($backups | Select-Object -Skip $MaxRetainedBackups)) {
        Assert-PathWithin $backup.FullName $backupRoot
        Remove-Item -LiteralPath $backup.FullName -Recurse -Force
    }
}

function Clear-Package {
    foreach ($item in (Get-PackageItems)) {
        Assert-PathWithin $item.FullName $publishRoot
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
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
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
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
        Remove-Item -LiteralPath $rollbackPath -Recurse -Force
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
