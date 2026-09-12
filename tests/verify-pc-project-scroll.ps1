# Verifies the personal-projects FlowLayoutPanel can reach the full final row.
# The visible scrollbar is custom drawn, so the native range is queried directly.
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class ProjectScrollProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct SCROLLINFO { public uint cbSize, fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT r, uint n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, int w, int l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO info);
    public static IntPtr FindMain(uint expectedPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != expectedPid) return true;
            var name = new StringBuilder(128); GetClassName(h, name, name.Capacity);
            if (!name.ToString().StartsWith("WindowsForms10.Window")) return true;
            var title = new StringBuilder(256); GetWindowText(h, title, title.Capacity);
            if (title.ToString() != "TapRelay") return true;
            RECT rect; GetWindowRect(h, out rect);
            if (rect.Right - rect.Left > 400) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static List<IntPtr> TopWindows(uint expectedPid) {
        var windows = new List<IntPtr>();
        EnumWindows((h, p) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == expectedPid) windows.Add(h); return true; }, IntPtr.Zero);
        return windows;
    }
    public static List<IntPtr> Children(IntPtr parent) {
        var children = new List<IntPtr>();
        EnumChildWindows(parent, (h, p) => { children.Add(h); return true; }, IntPtr.Zero);
        return children;
    }
    public static RECT RelativeRect(IntPtr h, IntPtr parent) {
        RECT rect; GetWindowRect(h, out rect); MapWindowPoints(IntPtr.Zero, parent, ref rect, 2); return rect;
    }
    public static string ClassName(IntPtr h) {
        var name = new StringBuilder(128); GetClassName(h, name, name.Capacity); return name.ToString();
    }
    public static bool IsWideForm(IntPtr h) {
        var name = new StringBuilder(128); GetClassName(h, name, name.Capacity);
        RECT rect; GetWindowRect(h, out rect);
        return name.ToString().StartsWith("WindowsForms10.Window") && rect.Right - rect.Left > 700;
    }
    public static string WindowTitle(IntPtr h) {
        var title = new StringBuilder(256); GetWindowText(h, title, title.Capacity); return title.ToString();
    }
    public static SCROLLINFO ScrollInfo(IntPtr h) {
        var info = new SCROLLINFO(); info.cbSize = (uint)Marshal.SizeOf(typeof(SCROLLINFO)); info.fMask = 0x17;
        GetScrollInfo(h, 1, ref info); return info;
    }
}
'@

$proc = Get-Process -Name TapRelay -ErrorAction SilentlyContinue |
    Where-Object {
        -not $_.HasExited -and (
            $_.Path -like '*bin*Release*' -or
            $_.Path -like '*pc-app*publish*TapRelay.exe'
        )
    } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: Release TapRelay process not running'; exit 1 }

$main = [ProjectScrollProbe]::FindMain([uint32]$proc.Id)
if ($main -eq [IntPtr]::Zero) { Write-Output 'FAIL: main window not found'; exit 1 }

[void][ProjectScrollProbe]::PostMessage($main, 0x8001, 0, 0) # WM_APP + 1 test hook
$dialog = [IntPtr]::Zero
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 150
    foreach ($candidate in [ProjectScrollProbe]::TopWindows([uint32]$proc.Id)) {
        if ($candidate -ne $main -and [ProjectScrollProbe]::IsWideForm($candidate) -and [ProjectScrollProbe]::WindowTitle($candidate) -ne 'TapRelay') { $dialog = $candidate; break }
    }
    if ($dialog -ne [IntPtr]::Zero) { break }
}
if ($dialog -eq [IntPtr]::Zero) { Write-Output 'FAIL: project dialog not found'; exit 1 }

$flow = [IntPtr]::Zero
foreach ($candidate in [ProjectScrollProbe]::Children($dialog)) {
    if ([ProjectScrollProbe]::GetParent($candidate) -ne $dialog) { continue }
    $rect = [ProjectScrollProbe]::RelativeRect($candidate, $dialog)
    if ($rect.Left -eq 0 -and $rect.Top -ge 100 -and $rect.Right -ge 700 -and ($rect.Bottom - $rect.Top) -ge 500) { $flow = $candidate; $flowRect = $rect; break }
}
if ($flow -eq [IntPtr]::Zero) {
    foreach ($candidate in [ProjectScrollProbe]::Children($dialog)) {
        if ([ProjectScrollProbe]::GetParent($candidate) -ne $dialog) { continue }
        $rect = [ProjectScrollProbe]::RelativeRect($candidate, $dialog)
        Write-Output ("candidate class={0} rect={1},{2}..{3},{4}" -f [ProjectScrollProbe]::ClassName($candidate), $rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
    }
    Write-Output 'FAIL: project list not found'
    [void][ProjectScrollProbe]::PostMessage($dialog, 0x0010, 0, 0)
    exit 1
}

$before = [ProjectScrollProbe]::ScrollInfo($flow)
[void][ProjectScrollProbe]::SendMessage($flow, 0x8002, [IntPtr]::Zero, [IntPtr]::Zero) # test-only ScrollToOffset(max)
Start-Sleep -Milliseconds 150
$after = [ProjectScrollProbe]::ScrollInfo($flow)

$cards = @()
foreach ($candidate in [ProjectScrollProbe]::Children($flow)) {
    if ([ProjectScrollProbe]::GetParent($candidate) -ne $flow) { continue }
    $rect = [ProjectScrollProbe]::RelativeRect($candidate, $dialog)
    if (($rect.Right - $rect.Left) -ge 300 -and ($rect.Bottom - $rect.Top) -ge 160) { $cards += $rect }
}
$lastCard = $cards | Sort-Object Bottom -Descending | Select-Object -First 1
$nativeMax = [Math]::Max(0, $after.nMax - [int]$after.nPage + 1)
$cardVisible = $lastCard -and $lastCard.Bottom -le $flowRect.Bottom

Write-Output ("flow={0},{1}..{2},{3}" -f $flowRect.Left, $flowRect.Top, $flowRect.Right, $flowRect.Bottom)
Write-Output ("native before=min:{0} max:{1} page:{2} pos:{3}" -f $before.nMin, $before.nMax, $before.nPage, $before.nPos)
Write-Output ("native range=min:{0} max:{1} page:{2} effectiveMax:{3}" -f $after.nMin, $after.nMax, $after.nPage, $nativeMax)
Write-Output ("last card bottom={0}; list bottom={1}; visible={2}" -f $lastCard.Bottom, $flowRect.Bottom, $cardVisible)

[void][ProjectScrollProbe]::PostMessage($dialog, 0x0010, 0, 0) # WM_CLOSE
if (-not $cardVisible) { exit 1 }
Write-Output 'PASS: final project card is fully visible at the native scroll limit'
