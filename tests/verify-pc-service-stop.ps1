# Exercises the same MainForm handler as the "Stop service" button. The app
# often runs in the tray, so it uses the local test-only WM_APP entry point.
Add-Type @'
using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

public class ServiceStopProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, int w, int l);

    public static IntPtr FindMain(uint expectedPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != expectedPid) return true;
            var className = new StringBuilder(128); GetClassName(h, className, className.Capacity);
            var title = new StringBuilder(128); GetWindowText(h, title, title.Capacity);
            if (className.ToString().StartsWith("WindowsForms10.Window") && title.ToString() == "TapRelay") {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool CanConnect(int timeoutMs) {
        try {
            using (var client = new TcpClient()) {
                var result = client.BeginConnect("127.0.0.1", 1122, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                client.EndConnect(result);
                return true;
            }
        }
        catch { return false; }
    }
}
'@

$proc = Get-Process -Name TapRelay -ErrorAction SilentlyContinue |
    Where-Object { -not $_.HasExited -and $_.Path -like '*pc-app*publish*TapRelay.exe' } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: published TapRelay process not running'; exit 1 }

$main = [ServiceStopProbe]::FindMain([uint32]$proc.Id)
if ($main -eq [IntPtr]::Zero) { Write-Output 'FAIL: TapRelay main window not found'; exit 1 }
if (-not [ServiceStopProbe]::CanConnect(100)) { Write-Output 'FAIL: service is not listening before test'; exit 1 }

$clientJob = Start-Job -ScriptBlock {
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    try {
        $socket.ConnectAsync([Uri]'ws://127.0.0.1:1122/', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        Start-Sleep -Seconds 20
    }
    finally {
        $socket.Abort()
        $socket.Dispose()
    }
}

try {
    $connected = $false
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 50
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:1122/health' -TimeoutSec 1
            if ($health.clients -ge 1) { $connected = $true; break }
        }
        catch { }
    }
    if (-not $connected) { Write-Output 'FAIL: test WebSocket client did not connect'; exit 1 }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    [void][ServiceStopProbe]::PostMessage($main, 0x8002, 0, 0) # StopServerAsync
    $released = $false
    while ($stopwatch.ElapsedMilliseconds -lt 1500) {
        if (-not [ServiceStopProbe]::CanConnect(50)) { $released = $true; break }
        Start-Sleep -Milliseconds 25
    }
    $stopwatch.Stop()
    Write-Output ("listener released after {0}ms with an active WebSocket" -f $stopwatch.ElapsedMilliseconds)

    # The listener is released before the host has completed cancellation of
    # in-flight WebSocket requests. The real UI keeps Start disabled until the
    # stopped snapshot arrives, so mirror that lifecycle before restoring it.
    Start-Sleep -Milliseconds 600
    [void][ServiceStopProbe]::PostMessage($main, 0x8003, 0, 0) # StartServerAsync
    $restartWatch = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    while ($restartWatch.ElapsedMilliseconds -lt 2500) {
        if ([ServiceStopProbe]::CanConnect(50)) { $started = $true; break }
        Start-Sleep -Milliseconds 25
    }
    $restartWatch.Stop()
    Write-Output ("listener restarted after {0}ms" -f $restartWatch.ElapsedMilliseconds)

    if (-not $released -or -not $started -or $stopwatch.ElapsedMilliseconds -gt 1000) { exit 1 }
    Write-Output 'PASS: stop handler releases the listener within one second with an active WebSocket and restores the service'
}
finally {
    if ($clientJob) {
        Stop-Job -Job $clientJob -ErrorAction SilentlyContinue
        Remove-Job -Job $clientJob -Force -ErrorAction SilentlyContinue
    }
}
