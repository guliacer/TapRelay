<#
.SYNOPSIS
  从 .ico 提取最高分辨率帧并输出为正方形 PNG（默认 256×256），用于按
  《05-UI与组件规范》R7 重新生成已适配应用的彩色 logo。

.EXAMPLE
  powershell -NoProfile -File tools\extract-ico-to-png.ps1 -IcoPath pc-app\TapRelay.ico -PngPath out.png -Size 256
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$IcoPath,
    [Parameter(Mandatory = $true)][string]$PngPath,
    [int]$Size = 256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$bytes = [System.IO.File]::ReadAllBytes($IcoPath)
if ($bytes.Length -lt 6 -or [System.BitConverter]::ToUInt16($bytes, 2) -ne 1) {
    throw "Not a valid ICO file: $IcoPath"
}

$count = [System.BitConverter]::ToUInt16($bytes, 4)
$best = $null
for ($i = 0; $i -lt $count; $i++) {
    $entry = 6 + $i * 16
    $w = $bytes[$entry]; if ($w -eq 0) { $w = 256 }
    $h = $bytes[$entry + 1]; if ($h -eq 0) { $h = 256 }
    $len = [System.BitConverter]::ToInt32($bytes, $entry + 8)
    $off = [System.BitConverter]::ToInt32($bytes, $entry + 12)
    $area = $w * $h
    if ($null -eq $best -or $area -gt $best.Area) {
        $best = [pscustomobject]@{ W = $w; H = $h; Len = $len; Off = $off; Area = $area }
    }
}

# 最大帧若本身就是 PNG（现代 256 图标常见），优先直接解码，避免 GDI+ 对
# 大 PNG 帧的兼容问题。
$isPng = $false
if ($best.Len -ge 8) {
    $isPng = ($bytes[$best.Off] -eq 0x89) -and ($bytes[$best.Off + 1] -eq 0x50) -and ($bytes[$best.Off + 2] -eq 0x4E) -and ($bytes[$best.Off + 3] -eq 0x47)
}

if ($isPng) {
    $frame = New-Object byte[] $best.Len
    [System.Array]::Copy($bytes, $best.Off, $frame, 0, $best.Len)
    $ms = New-Object System.IO.MemoryStream(, $frame)
    $src = [System.Drawing.Image]::FromStream($ms)
} else {
    $ms = New-Object System.IO.MemoryStream(, $bytes)
    $icon = New-Object System.Drawing.Icon($ms, (New-Object System.Drawing.Size($best.W, $best.H)))
    $src = $icon.ToBitmap()
}

$out = New-Object System.Drawing.Bitmap($Size, $Size)
$g = [System.Drawing.Graphics]::FromImage($out)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($src, 0, 0, $Size, $Size)
$g.Dispose()

$dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($PngPath))
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$out.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$out.Dispose(); $src.Dispose(); $ms.Dispose()

Write-Host ("Extracted {0}x{1} frame -> {2} ({3}x{3})" -f $best.W, $best.H, $PngPath, $Size)
