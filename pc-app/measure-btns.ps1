Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Drawing.Font('Microsoft YaHei UI', 9.5, [System.Drawing.FontStyle]::Bold)
foreach ($t in @('启动服务', '隐藏 IP', '停止服务', '二维码', '网络设置')) {
    $s = [System.Windows.Forms.TextRenderer]::MeasureText($t, $f)
    Write-Output ("{0}: {1}px" -f $t, $s.Width)
}
$f.Dispose()
