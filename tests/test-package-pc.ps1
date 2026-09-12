[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) ('taprelay-package-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeRoot | Out-Null

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function dotnet {
    if ($args[0] -eq 'build' -and $global:taprelayPackageProbe.mode -eq 'build-failure') {
        $global:LASTEXITCODE = 1
        return
    }
    if ($args[0] -eq 'publish') {
        $outputPath = $args[($args.IndexOf('-o') + 1)]
        [IO.File]::WriteAllText((Join-Path $outputPath 'TapRelay.exe'), 'new-package')
        if ($global:taprelayPackageProbe.mode -ne 'missing-symbols') {
            [IO.File]::WriteAllText((Join-Path $outputPath 'TapRelay.pdb'), 'new-symbols')
        }
    }
    $global:LASTEXITCODE = 0
}
function Get-Process {
    param($Name, $ErrorAction)
    if ($global:taprelayPackageProbe.running) {
        $process = [pscustomobject]@{ Id = 12345; Path = $global:taprelayPackageProbe.exe; WorkingSet64 = 20MB }
        $process | Add-Member ScriptMethod WaitForExit { param($Timeout) return $true }
        $process
    }
}
function Stop-Process {
    param($Id, [switch]$Force, $ErrorAction)
    $global:taprelayPackageProbe.stops++
    $global:taprelayPackageProbe.running = $false
}
function Start-Process {
    param($FilePath, $WorkingDirectory, $WindowStyle)
    Assert ($WindowStyle -eq 'Hidden') 'Background launcher must be hidden'
    Assert ($FilePath -eq $global:taprelayPackageProbe.exe) 'Started a different package'
    $global:taprelayPackageProbe.starts++
    $global:taprelayPackageProbe.running = $true
}
function Start-Sleep { param($Milliseconds) }
function Invoke-RestMethod {
    param($Uri, $TimeoutSec)
    if ($global:taprelayPackageProbe.mode -eq 'health-failure' -and [IO.File]::ReadAllText($global:taprelayPackageProbe.exe) -eq 'new-package') {
        throw 'Injected health failure'
    }
    [pscustomobject]@{ status = 'ok'; port = 1122 }
}
function robocopy {
    $source = [string]$args[0]
    $destination = [string]$args[1]
    if ($global:taprelayPackageProbe.mode -eq 'copy-failure' -and $source.EndsWith('package-staging')) {
        [IO.File]::WriteAllText((Join-Path $destination 'partial.tmp'), 'partial')
        $global:LASTEXITCODE = 8
        return
    }
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
    }
    $global:LASTEXITCODE = 1
}

try {
    foreach ($mode in @('build-failure', 'missing-symbols', 'copy-failure', 'health-failure', 'success', 'named-backup', 'invalid-backup')) {
        $caseRoot = Join-Path $probeRoot $mode
        $packageRoot = Join-Path $caseRoot 'pc-app\publish'
        $toolsRoot = Join-Path $caseRoot 'tools'
        New-Item -ItemType Directory -Path $packageRoot, $toolsRoot | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\package-pc.ps1') -Destination $toolsRoot
        $global:taprelayPackageProbe = @{ mode = $mode; exe = (Join-Path $packageRoot 'TapRelay.exe'); running = $true; starts = 0; stops = 0 }
        [IO.File]::WriteAllText($global:taprelayPackageProbe.exe, 'old-package')
        [IO.File]::WriteAllText((Join-Path $packageRoot 'old-only.txt'), 'old-data')
        [IO.File]::WriteAllText((Join-Path $packageRoot 'PACKAGE-RULE.md'), 'retained-rule')
        [IO.File]::WriteAllText((Join-Path $caseRoot 'pc-app\TapRelay.csproj'), '<Project />')
        $backupName = if ($mode -eq 'named-backup') { 'before-test' } elseif ($mode -eq 'invalid-backup') { '..\escape' } else { '' }
        $failure = $null
        try { & (Join-Path $toolsRoot 'package-pc.ps1') -BackupName $backupName | Out-Null }
        catch { $failure = $_ }
        $succeeded = $mode -in @('success', 'named-backup')
        Assert (($null -eq $failure) -eq $succeeded) ("Unexpected outcome for $($mode): $failure")
        Assert $global:taprelayPackageProbe.running "Service was left stopped: $mode"
        Assert ([IO.File]::ReadAllText($global:taprelayPackageProbe.exe) -eq $(if ($succeeded) { 'new-package' } else { 'old-package' })) "Wrong package: $mode"
        Assert (Test-Path -LiteralPath (Join-Path $packageRoot 'PACKAGE-RULE.md')) 'Publish rule was removed'
        Assert (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'partial.tmp'))) 'Partial copy was not cleaned'
        if ($mode -in @('build-failure', 'missing-symbols', 'invalid-backup')) {
            Assert ($global:taprelayPackageProbe.stops -eq 0 -and $global:taprelayPackageProbe.starts -eq 0) 'Preflight failure interrupted the service'
        }
        if ($mode -in @('copy-failure', 'health-failure')) {
            Assert ($global:taprelayPackageProbe.starts -ge 1) "Old service was not restarted: $failure"
            Assert (@(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'backups') -Directory).Count -eq 1) 'Recovery copy was not retained'
        }
        if ($succeeded) {
            Assert (Test-Path -LiteralPath (Join-Path $packageRoot 'TapRelay.pdb')) 'Symbols missing'
            Assert (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'old-only.txt'))) 'Old package file leaked into new package'
            $info = Get-Content -LiteralPath (Join-Path $packageRoot 'build-info.json') -Raw | ConvertFrom-Json
            Assert ($info.sha256 -eq (Get-FileHash -LiteralPath $global:taprelayPackageProbe.exe).Hash) 'Build manifest hash mismatch'
            $backups = @(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'backups') -Directory)
            Assert ($backups.Count -eq $(if ($mode -eq 'named-backup') { 1 } else { 0 })) 'Wrong retained backup count'
        }
        Write-Host "PASS $mode"
    }
}
finally {
    $resolvedProbe = [IO.Path]::GetFullPath($probeRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedProbe.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedProbe)).StartsWith('taprelay-package-test-')) {
        throw "Refusing cleanup outside validated temporary test directory: $resolvedProbe"
    }
    Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
}
