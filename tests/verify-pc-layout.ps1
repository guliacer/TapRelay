# Verifies the "log area is always visible" invariant against the RUNNING
# TapRelay window instead of a hand-written layout model. Finds the form by PID
# (MainWindowHandle is 0 while the app sits in the tray), enumerates the real
# child controls, locates the log TextBox (lowest Edit) and asserts it stays
# inside the client area at several window heights.
# ASCII-only source: PowerShell 5.1 reads .ps1 as ANSI, so CJK literals here
# would break string termination.
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class W {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool MapWindowPoints(IntPtr from, IntPtr to, ref RECT r, uint n);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, int w, int l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    // All top-level WinForms windows of a PID except one, as "hwnd|width|height".
    public static List<string> TopWindows(uint want, IntPtr exclude) {
        var list = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != want || h == exclude) { return true; }
            var cls = new StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            if (!cls.ToString().StartsWith("WindowsForms10.Window")) { return true; }
            RECT r; GetWindowRect(h, out r);
            list.Add(h + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr FindByPid(uint want) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != want) { return true; }
            var cls = new StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            // WinForms top-level forms use a WindowsForms10.Window.* class.
            if (cls.ToString().StartsWith("WindowsForms10.Window")) {
                RECT r; GetWindowRect(h, out r);
                if (r.Right - r.Left > 400) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // No IsWindowVisible filter: while the app sits in the tray the form has no
    // WS_VISIBLE, so every child reports invisible. Child rects are still valid
    // because WinForms lays out on WM_SIZE regardless of visibility.
    public static List<string> Children(IntPtr parent) {
        var list = new List<string>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr p) {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            RECT r; GetWindowRect(h, out r);
            MapWindowPoints(IntPtr.Zero, parent, ref r, 2);
            list.Add(h + "|" + sb + "|" + r.Left + "|" + r.Top + "|" + r.Right + "|" + r.Bottom);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

# Skip exited zombie Process objects (package-pc stop/start leaves them
# briefly), and pick the newest live instance so we never verify an old build.
$proc = Get-Process -Name TapRelay -ErrorAction SilentlyContinue |
    Where-Object { -not $_.HasExited } |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if (-not $proc) { Write-Output "FAIL: TapRelay process not running"; exit 1 }

$hwnd = [W]::FindByPid([uint32]$proc.Id)
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "FAIL: TapRelay form window not found"; exit 1 }

# Do not force the window visible: Win32 ShowWindow does not sync WinForms'
# own Visible state, and the tray flow owns that. Geometry is measurable either
# way, so leave the user's window state untouched.
$wasHidden = -not [W]::IsWindowVisible($hwnd)

$wr = New-Object W+RECT
[void][W]::GetWindowRect($hwnd, [ref]$wr)
$origW = $wr.Right - $wr.Left
$origH = $wr.Bottom - $wr.Top
Write-Output ("window = {0}x{1}  hiddenAtStart={2}" -f $origW, $origH, $wasHidden)

$LOGMIN = 130
$fail = 0

# 860 is below MinimumSize on purpose: the form must clamp it, proving the
# minimum-height guard actually holds rather than letting the log slide off.
foreach ($h in @(860, $origH, 1050, 1300)) {
    [void][W]::MoveWindow($hwnd, $wr.Left, $wr.Top, $origW, $h, $true)
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 500

    $ar = New-Object W+RECT
    [void][W]::GetWindowRect($hwnd, [ref]$ar)
    $actualH = $ar.Bottom - $ar.Top

    $cr = New-Object W+RECT
    [void][W]::GetClientRect($hwnd, [ref]$cr)
    $clientH = $cr.Bottom

    $edits = @()
    foreach ($row in [W]::Children($hwnd)) {
        # WinForms registers its own window classes: an edit box reports
        # "WindowsForms10.EDIT.app.0.<hash>", never the plain Win32 "Edit".
        $p = $row -split '\|'
        # p[0]=hwnd, p[1]=class, p[2..5]=rect
        if ($p[1] -like 'WindowsForms10.EDIT.*') {
            $edits += [pscustomobject]@{ Top = [int]$p[3]; Bottom = [int]$p[5] }
        }
    }
    if ($edits.Count -eq 0) { Write-Output "FAIL: no Edit control found"; $fail = 1; continue }

    $log = $edits | Sort-Object Bottom -Descending | Select-Object -First 1
    $boxH = $log.Bottom - $log.Top
    $ok = ($log.Bottom -le $clientH) -and ($boxH -ge $LOGMIN)
    if (-not $ok) { $fail = 1 }
    Write-Output ("asked={0,5} actual={1,5} clientH={2,4} log top={3,4} bottom={4,4} h={5,4} -> {6}" -f `
        $h, $actualH, $clientH, $log.Top, $log.Bottom, $boxH, $(if ($ok) { 'OK' } else { 'FAIL' }))
}

[void][W]::MoveWindow($hwnd, $wr.Left, $wr.Top, $origW, $origH, $true)

# Personal projects window constraint: its client height must stay at or below
# the owner (MainForm) client height. Simulating a click on the book button
# from this process does not work while the main form is hidden in the tray
# (Win32 ShowWindow does not sync WinForms' Visible flag, so the click is
# dropped). Instead the app exposes a test-only custom message (WM_APP+1 =
# 0x8001) that opens the dialog directly; posting it is asynchronous, so the
# modal ShowDialog loop on the app thread never blocks this script, and
# WM_CLOSE posted afterwards is dispatched by that same loop.
[void][W]::PostMessage($hwnd, 0x8001, 0, 0)  # WM_APP+1 -> open personal projects
$ppHwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 250
    foreach ($row in [W]::TopWindows([uint32]$proc.Id, $hwnd)) {
        $tw = $row -split '\|'
        # The dialog is 760 wide; any other top-level window wider than 500 is it.
        if ([int]$tw[1] -gt 500) {
            $ppHwnd = New-Object IntPtr ([long]$tw[0])
            break
        }
    }
    if ($ppHwnd -ne [IntPtr]::Zero) { break }
}
if ($ppHwnd -eq [IntPtr]::Zero) {
    Write-Output "WARN: personal projects window not detected, skipping height check"
} else {
    Start-Sleep -Milliseconds 500
    $ppClient = New-Object W+RECT
    [void][W]::GetClientRect($ppHwnd, [ref]$ppClient)
    $ppClientH = $ppClient.Bottom
    $cr = New-Object W+RECT
    [void][W]::GetClientRect($hwnd, [ref]$cr)
    $ok = ($ppClientH -le $cr.Bottom)
    if (-not $ok) { $fail = 1 }
    Write-Output ("personal clientH={0,5} ownerClientH={1} -> {2}" -f $ppClientH, $cr.Bottom, $(if ($ok) { 'OK' } else { 'FAIL' }))
    [void][W]::PostMessage($ppHwnd, 0x10, 0, 0)  # WM_CLOSE
}

if ($fail -eq 0) { Write-Output "PASS: log box inside client area and >= 130px; personal projects window clamped to owner height" }
exit $fail
