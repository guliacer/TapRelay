"""Capture a Windows top-level window to PNG using only ctypes + the stdlib.

Why this exists: the sandbox blocks PowerShell `Add-Type`, so a P/Invoke-based
PowerShell capture script cannot run there. Pure-Python ctypes needs no runtime
compilation, and writing the PNG by hand (zlib + struct) avoids needing Pillow,
which cannot be installed because the sandbox HTTP proxy returns 502 for PyPI.

IMPORTANT LIMITATION: this only works for windows that live in *your* window
station. A TapRelay launched from a background agent task ends up in a different
window station, so `--list` will not even find it and PrintWindow returns a blank
image. For layout verification prefer the in-process renderer instead:
`TapRelay.Reliability.Tests.exe --ui-snapshot <dir> [case]` (see UiSnapshot.cs).
Use this script when the app was started interactively and you want a real
on-screen capture, including the tray-hidden case (find it by --pid).

Usage:
    python capture_window.py --list
    python capture_window.py --pid 1234 --out shot.png
    python capture_window.py --title-contains TapRelay --out shot.png
    python capture_window.py --title-contains TapRelay --out shot.png --downsample 2

Notes:
  * Uses PrintWindow(PW_RENDERFULLCONTENT) so it works even when the window is
    occluded.
  * Sets per-monitor DPI awareness first, otherwise GetWindowRect reports
    virtualised (scaled) pixels and the bitmap comes out blurry / cropped.
"""

import argparse
import ctypes
import struct
import sys
import zlib
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

PW_RENDERFULLCONTENT = 0x00000002
BI_RGB = 0

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def write_png(path, width, height, bgra):
    """Encode a top-down BGRA byte buffer as a truecolour+alpha PNG."""
    stride = width * 4
    raw = bytearray()
    append = raw.append
    for y in range(height):
        append(0)  # PNG filter type 0 (None) for every scanline
        raw += bgra[y * stride:(y + 1) * stride]
        # BGRA -> RGBA
        start = len(raw) - stride
        row = raw[start:]
        row[0::4], row[2::4] = row[2::4], row[0::4]

    def chunk(tag, payload):
        return (
            struct.pack(">I", len(payload))
            + tag
            + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    with open(path, "wb") as handle:
        handle.write(b"\x89PNG\r\n\x1a\n")
        handle.write(chunk(b"IHDR", header))
        handle.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        handle.write(chunk(b"IEND", b""))


def set_dpi_awareness():
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # per-monitor aware
        return "per-monitor"
    except Exception:
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
        return "system"
    except Exception:
        return "none"


def window_text(hwnd):
    length = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buf, length + 1)
    return buf.value


def window_class(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def window_pid(hwnd):
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def enum_windows():
    found = []

    def cb(hwnd, _lparam):
        found.append(hwnd)
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found


def window_rect(hwnd):
    rect = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise OSError("GetWindowRect failed")
    return rect


def capture_raw(hwnd):
    rect = window_rect(hwnd)
    width = rect.right - rect.left
    height = rect.bottom - rect.top
    if width <= 0 or height <= 0:
        raise ValueError("window has empty rect %dx%d" % (width, height))

    hdc = user32.GetWindowDC(hwnd)
    if not hdc:
        raise OSError("GetWindowDC failed")
    memdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, width, height)
    try:
        gdi32.SelectObject(memdc, bmp)
        ok = user32.PrintWindow(hwnd, memdc, PW_RENDERFULLCONTENT)

        info = BITMAPINFO()
        info.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        info.bmiHeader.biWidth = width
        info.bmiHeader.biHeight = -height  # negative => top-down scanlines
        info.bmiHeader.biPlanes = 1
        info.bmiHeader.biBitCount = 32
        info.bmiHeader.biCompression = BI_RGB

        buffer = ctypes.create_string_buffer(width * height * 4)
        scanned = gdi32.GetDIBits(memdc, bmp, 0, height, buffer, ctypes.byref(info), 0)
        if scanned == 0:
            raise OSError("GetDIBits returned 0 rows")
        return width, height, buffer.raw, bool(ok)
    finally:
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(memdc)
        user32.ReleaseDC(hwnd, hdc)


def downsample_bgra(width, height, bgra, factor):
    if factor <= 1:
        return width, height, bgra
    out_w = max(1, width // factor)
    out_h = max(1, height // factor)
    out = bytearray(out_w * out_h * 4)
    for oy in range(out_h):
        sy = oy * factor
        src_row = sy * width * 4
        dst_row = oy * out_w * 4
        for ox in range(out_w):
            si = src_row + ox * factor * 4
            di = dst_row + ox * 4
            out[di:di + 4] = bgra[si:si + 4]
    return out_w, out_h, bytes(out)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pid", type=int)
    parser.add_argument("--title-contains")
    parser.add_argument("--class-contains")
    parser.add_argument("--out")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--visible-only", action="store_true")
    parser.add_argument("--downsample", type=int, default=1)
    args = parser.parse_args()

    print("dpi_awareness=%s" % set_dpi_awareness())

    entries = []
    for hwnd in enum_windows():
        entries.append(
            (
                hwnd,
                window_pid(hwnd),
                window_text(hwnd),
                window_class(hwnd),
                bool(user32.IsWindowVisible(hwnd)),
            )
        )

    def matches(entry):
        _hwnd, pid, title, cls, visible = entry
        if args.pid is not None and pid != args.pid:
            return False
        if args.title_contains and args.title_contains.lower() not in title.lower():
            return False
        if args.class_contains and args.class_contains.lower() not in cls.lower():
            return False
        if args.visible_only and not visible:
            return False
        return True

    selected = [entry for entry in entries if matches(entry)]

    if args.list or not args.out:
        for hwnd, pid, title, cls, visible in selected:
            print("hwnd=%s pid=%s visible=%s class=%r title=%r" % (hwnd, pid, visible, cls, title))
        print("total_windows=%d matched=%d" % (len(entries), len(selected)))
        return 0

    if not selected:
        print("NO_MATCH: %d top-level windows, none matched" % len(entries))
        return 1

    def area(entry):
        rect = window_rect(entry[0])
        return (rect.right - rect.left) * (rect.bottom - rect.top)

    selected.sort(key=area, reverse=True)
    hwnd, pid, title, cls, visible = selected[0]
    width, height, bgra, print_ok = capture_raw(hwnd)
    out_w, out_h, bgra = downsample_bgra(width, height, bgra, args.downsample)
    write_png(args.out, out_w, out_h, bgra)
    print(
        "SAVED=%s size=%sx%s pid=%s visible=%s printwindow_ok=%s class=%r title=%r"
        % (args.out, out_w, out_h, pid, visible, print_ok, cls, title)
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
