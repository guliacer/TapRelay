# TapRelay public-tunnel self-check.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-tunnel.ps1
# Purpose: pinpoint which layer blocks the Cloudflare Tunnel, so after switching
# network/node you can re-run it and know immediately whether it recovered.

$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'

function Test-Tcp([string]$Host, [int]$Port, [int]$Ms = 3000) {
    $c = New-Object System.Net.Sockets.TcpClient
    $t = $c.ConnectAsync($Host, $Port)
    if ($t.Wait($Ms) -and $c.Connected) { $c.Dispose(); return $true }
    $c.Dispose(); return $false
}

Write-Output "==== TapRelay Cloudflare Tunnel check  $(Get-Date -Format 'HH:mm:ss') ===="

# 1. local origin
try {
    $h = Invoke-RestMethod -Uri 'http://127.0.0.1:1122/health' -TimeoutSec 2
    Write-Output ("[1/5] local origin      : OK (port {0}, clients {1})" -f $h.port, $h.clients)
} catch { Write-Output "[1/5] local origin      : DOWN (TapRelay not listening on 1122)" }

# 2. edge TCP
$tcp7844 = Test-Tcp 'region1.v2.argotunnel.com' 7844
$tcp443  = Test-Tcp 'region1.v2.argotunnel.com' 443
Write-Output ("[2/5] edge TCP 7844     : {0}" -f $(if ($tcp7844) { 'OPEN' } else { 'CLOSED' }))
Write-Output ("[3/5] edge TCP 443      : {0}" -f $(if ($tcp443) { 'OPEN' } else { 'CLOSED' }))

# 3. edge TLS handshake
$tlsOk = $false
try {
    $c = New-Object System.Net.Sockets.TcpClient
    $t = $c.ConnectAsync('region1.v2.argotunnel.com', 7844)
    if ($t.Wait(3000) -and $c.Connected) {
        $s = New-Object System.Net.Security.SslStream($c.GetStream(), $false, { $true })
        $s.AuthenticateAsClient('region1.v2.argotunnel.com')
        $tlsOk = $s.IsAuthenticated
        $s.Dispose()
    }
    $c.Dispose()
} catch { $tlsOk = $false }
Write-Output ("[4/5] edge TLS handshake: {0}" -f $(if ($tlsOk) { 'OK' } else { 'FAILED (SNI/tls interference)' }))

# 4. cloudflared HA state (scan its random metrics port)
$ha = 'n/a'
try {
    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -gt 1024 }
    foreach ($l in $listeners) {
        try {
            $metrics = (Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/metrics" -f $l.Port) -TimeoutSec 2 -UseBasicParsing).Content
            if ($metrics -match 'cloudflared_tunnel_ha_connections (\d+)') { $ha = $Matches[1]; break }
        } catch { }
    }
} catch { }
Write-Output ("[5/5] cloudflared HA     : {0} (0 = not connected to edge)" -f $ha)

# 5. public /health
try {
    $code = (Invoke-WebRequest -Uri 'https://win.guliacer.dpdns.org/health' -TimeoutSec 8 -UseBasicParsing).StatusCode
    Write-Output ("public /health         : HTTP {0}" -f $code)
} catch { Write-Output "public /health         : FAILED" }

Write-Output ""
if (-not $tcp7844 -and -not $tcp443) {
    Write-Output "VERDICT: edge TCP blocked -> switch network or proxy node."
} elseif ($tcp7844 -and -not $tlsOk) {
    Write-Output "VERDICT: TCP OK but TLS reset -> SNI interference, switch network or route argotunnel via a real node."
} elseif ($ha -ne 'n/a' -and [int]$ha -le 0) {
    Write-Output "VERDICT: network reachable but tunnel not registered -> wait 1-2 min or restart Cloudflared service."
} else {
    Write-Output "VERDICT: link looks healthy, check public /health above."
}
