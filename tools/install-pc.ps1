[CmdletBinding()]
param(
    [string]$PackagePath = $PSScriptRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "TapRelay"),
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageRoot = [System.IO.Path]::GetFullPath($PackagePath)
$targetRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$sourceExe = Join-Path $packageRoot "TapRelay.exe"
$targetExe = Join-Path $targetRoot "TapRelay.exe"

if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "TapRelay.exe was not found in package directory: $packageRoot"
}

New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
Get-Process -Name TapRelay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
foreach ($optionalName in @("build-info.json", "PACKAGE-RULE.md", "TapRelay.pdb")) {
    $optionalSource = Join-Path $packageRoot $optionalName
    if (Test-Path -LiteralPath $optionalSource -PathType Leaf) {
        Copy-Item -LiteralPath $optionalSource -Destination (Join-Path $targetRoot $optionalName) -Force
    }
}

$startMenuRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
$shortcutDirectory = Join-Path $startMenuRoot "Programs\TapRelay"
$shortcutPath = Join-Path $shortcutDirectory "TapRelay.lnk"
New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetExe
$shortcut.WorkingDirectory = $targetRoot
$shortcut.Description = "TapRelay PC notification relay"
$shortcut.IconLocation = "$targetExe,0"
$shortcut.Save()

Write-Host "TapRelay installed to: $targetRoot"
Write-Host "Start menu shortcut: $shortcutPath"

if (-not $NoLaunch) {
    Start-Process -FilePath $targetExe -WorkingDirectory $targetRoot
}
