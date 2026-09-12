Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Measure-LabelHeight {
    param([single]$Size, [string]$Style, [string]$Text)
    $fs = if ($Style -eq 'b') { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', $Size, $fs)
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.AutoSize = $true
    $lbl.Font = $font
    $lbl.Text = $Text
    $h = $lbl.PreferredSize.Height
    $lbl.Dispose()
    $font.Dispose()
    return $h
}

# ASCII-only probe text; CJK glyph height is driven by the font's line height,
# not the specific characters, so Latin text yields the same PreferredSize.Height.
$sample = 'Ag'

Write-Output ("title20b={0}"   -f (Measure-LabelHeight 20.0 'b' $sample))
Write-Output ("group9={0}"     -f (Measure-LabelHeight 9.0  'r' $sample))
Write-Output ("label9_5={0}"   -f (Measure-LabelHeight 9.5  'r' $sample))
Write-Output ("value10_5b={0}" -f (Measure-LabelHeight 10.5 'b' $sample))
Write-Output ("hint8_5={0}"    -f (Measure-LabelHeight 8.5  'r' $sample))

$mono = New-Object System.Drawing.Font('Consolas', 9.5)
$one = [System.Windows.Forms.TextRenderer]::MeasureText('Ag', $mono).Height
$three = [System.Windows.Forms.TextRenderer]::MeasureText("Ag`nAg`nAg", $mono).Height
Write-Output ("consolas_lineheight={0}" -f (($three - $one) / 2))
$mono.Dispose()

# Non-client vertical overhead for the exact frame MainForm uses.
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::Sizable
$f.MaximizeBox = $true
$f.Size = New-Object System.Drawing.Size(880, 900)
$f.CreateControl()
Write-Output ("nonclient_v={0}" -f (900 - $f.ClientSize.Height))
$f.Dispose()

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
Write-Output ("workingarea={0}x{1}" -f $wa.Width, $wa.Height)
