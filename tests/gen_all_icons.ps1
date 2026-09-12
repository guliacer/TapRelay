# Generate Android app icon resources
# Extract latest logo from each app install dir, generate 256x256 color logo and 96x96 white silhouette status bar icon
# Follows 05-UI-Component-Spec.md R7: App icon source and spec fixed

Add-Type -AssemblyName System.Drawing

# Win32 API to extract high-res icon from .exe
if (-not ("IconExtract" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class IconExtract {
    [DllImport("shell32.dll", EntryPoint = "SHExtractIconsW", CharSet = CharSet.Unicode)]
    public static extern uint SHExtractIconsW(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        out IntPtr phicon, out uint piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
"@
}

$destDir = "W:\TapRelay\android-app\app\src\main\res\drawable-nodpi"
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

# App logo source mapping
$apps = @(
    @{Key="claude";         Source=""; Type="none"},
    @{Key="claude-haha";    Source="C:\Program Files\Claude Code Haha\resources\app.asar.unpacked\dist\app-icon.png"; Type="png"},
    @{Key="codex";          Source=""; Type="none"},
    @{Key="trae";           Source="W:\AIphotograph\Trae CN\Trae CN.exe"; Type="exe"},
    @{Key="trae_solo";      Source="W:\AIphotograph\TRAE SOLO CN\TRAE SOLO CN.exe"; Type="exe"},
    @{Key="zcode";          Source="C:\Program Files\ZCode\resources\icon.png"; Type="png"},
    @{Key="doubao";         Source="C:\Users\Admin\AppData\Local\Doubao\Application\app\Logo.png"; Type="png"},
    @{Key="suyan";          Source=""; Type="none"},
    @{Key="baidunetdisk";   Source="D:\Program Files\BaiduNetdisk\netdisk_logo.ico"; Type="ico"},
    @{Key="quarknetdisk";   Source="C:\Program Files (x86)\QuarkCloudDrive\6.5.5.724\VisualElements\Logo.png"; Type="png"}
)

# Apps that need status bar icon (only adapted netdisk/tool apps)
$statApps = @("suyan", "baidunetdisk", "quarknetdisk")

function Get-SourceBitmap($app) {
    if ($app.Type -eq "none" -or -not (Test-Path $app.Source)) {
        return $null
    }

    try {
        if ($app.Type -eq "png") {
            return [System.Drawing.Image]::FromFile($app.Source)
        }
        elseif ($app.Type -eq "ico") {
            $icon = New-Object System.Drawing.Icon($app.Source, 256, 256)
            return $icon.ToBitmap()
        }
        elseif ($app.Type -eq "exe") {
            # Try to extract 256x256 icon from .exe
            $hIcon = [IntPtr]::Zero
            $iconId = [uint32]0
            $result = [IconExtract]::SHExtractIconsW($app.Source, 0, 256, 256, [ref]$hIcon, [ref]$iconId, 1, 0)
            if ($result -gt 0 -and $hIcon -ne [IntPtr]::Zero) {
                $icon = [System.Drawing.Icon]::FromHandle($hIcon)
                $bmp = $icon.ToBitmap()
                $null = [IconExtract]::DestroyIcon($hIcon)
                return $bmp
            }
            # Fallback: try 48x48
            $hIcon = [IntPtr]::Zero
            $iconId = [uint32]0
            $result = [IconExtract]::SHExtractIconsW($app.Source, 0, 48, 48, [ref]$hIcon, [ref]$iconId, 1, 0)
            if ($result -gt 0 -and $hIcon -ne [IntPtr]::Zero) {
                $icon = [System.Drawing.Icon]::FromHandle($hIcon)
                $bmp = $icon.ToBitmap()
                $null = [IconExtract]::DestroyIcon($hIcon)
                return $bmp
            }
            Write-Warning "Cannot extract icon from $($app.Source)"
            return $null
        }
    }
    catch {
        Write-Warning "Failed to read $($app.Source): $_"
        return $null
    }
}

function Save-Logo256($source, $destPath) {
    # Generate 256x256 color logo, keep aspect ratio, centered, transparent background
    $bmp = New-Object System.Drawing.Bitmap(256, 256)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $srcW = $source.Width
    $srcH = $source.Height
    $scale = [Math]::Min(240.0 / $srcW, 240.0 / $srcH)
    $dstW = [int]($srcW * $scale)
    $dstH = [int]($srcH * $scale)
    $x = (256 - $dstW) / 2
    $y = (256 - $dstH) / 2

    $g.DrawImage($source, $x, $y, $dstW, $dstH)
    $g.Dispose()

    $bmp.Save($destPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  logo: $destPath (src: $($source.Width)x$($source.Height))"
}

function Save-StatIcon96($source, $destPath) {
    # Generate 96x96 white silhouette status bar icon
    # Scale color logo to 96x96, replace non-transparent pixels with white
    $bmp = New-Object System.Drawing.Bitmap(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

    # First scale source to 96x96 temp bitmap
    $tempBmp = New-Object System.Drawing.Bitmap(96, 96)
    $tg = [System.Drawing.Graphics]::FromImage($tempBmp)
    $tg.Clear([System.Drawing.Color]::Transparent)
    $tg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $srcW = $source.Width
    $srcH = $source.Height
    $scale = [Math]::Min(88.0 / $srcW, 88.0 / $srcH)
    $dstW = [int]($srcW * $scale)
    $dstH = [int]($srcH * $scale)
    $x = (96 - $dstW) / 2
    $y = (96 - $dstH) / 2
    $tg.DrawImage($source, $x, $y, $dstW, $dstH)
    $tg.Dispose()

    # Replace non-transparent pixels with white
    for ($py = 0; $py -lt 96; $py++) {
        for ($px = 0; $px -lt 96; $px++) {
            $pixel = $tempBmp.GetPixel($px, $py)
            if ($pixel.A -gt 20) {
                $bmp.SetPixel($px, $py, [System.Drawing.Color]::FromArgb($pixel.A, 255, 255, 255))
            }
        }
    }
    $tempBmp.Dispose()
    $g.Dispose()

    $bmp.Save($destPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  stat: $destPath"
}

Write-Output "=== Start generating app icons ==="
$generated = 0
$skipped = 0

foreach ($app in $apps) {
    $logoPath = Join-Path $destDir "ic_$($app.Key)_logo.png"
    $statPath = Join-Path $destDir "ic_stat_$($app.Key).png"

    Write-Output "[$($app.Key)] processing..."

    if ($app.Type -eq "none") {
        Write-Warning "  skip $($app.Key): install path not found, keep existing icon"
        $skipped++
        continue
    }

    if (-not (Test-Path $app.Source)) {
        Write-Warning "  skip $($app.Key): source not found $($app.Source)"
        $skipped++
        continue
    }

    $source = Get-SourceBitmap $app
    if ($source -eq $null) {
        Write-Warning "  skip $($app.Key): cannot read source image"
        $skipped++
        continue
    }

    # Generate 256x256 color logo
    Save-Logo256 $source $logoPath

    # Generate 96x96 status bar icon (only for netdisk/tool apps)
    if ($statApps -contains $app.Key) {
        Save-StatIcon96 $source $statPath
    }

    $source.Dispose()
    $generated++
}

Write-Output ""
Write-Output "=== Done ==="
Write-Output "Generated: $generated app icons"
Write-Output "Skipped: $skipped apps (kept existing icons)"
