// Icons.cs - an app's real icon, read from its exe file, as a XAML bitmap. Cached per exe.
//
// The sidecar (sidecar/Icons.cs) reads the same pixels (PrivateExtractIconsW, straight from the
// file, no shell cache) and then encodes a 64 px PNG data URL for the page to decode. Here the
// pixels go straight into a WriteableBitmap: no PNG, no base64, no second process.
// NOT ported (migration work): Store package icons, .lnk icons, the file-changed re-read.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MantleBar.Native;

internal static class Icons
{
    static readonly Dictionary<string, WriteableBitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static WriteableBitmap? ForApp(AppEntry app, int px)
    {
        string key = app.Key + "@" + px;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        WriteableBitmap? bmp = null;
        IntPtr hIcon = FromFile(app.Exe, px);
        bool own = hIcon != IntPtr.Zero;
        if (hIcon == IntPtr.Zero && app.Windows.Count > 0) hIcon = FromWindow(app.Windows[0]);
        if (hIcon != IntPtr.Zero) {
            try { bmp = ToBitmap(hIcon); } catch (Exception) { bmp = null; }
            if (own) Win32.DestroyIcon(hIcon);
        }
        Cache[key] = bmp;   // a null is cached too: an exe with no icon is not re-read on every rebuild
        return bmp;
    }

    static IntPtr FromFile(string exe, int px)
    {
        var icons = new IntPtr[1]; var ids = new int[1];
        try {
            uint got = Win32.PrivateExtractIconsW(exe, 0, px, px, icons, ids, 1, 0);
            return got >= 1 && got != 0xFFFFFFFF ? icons[0] : IntPtr.Zero;
        } catch (Exception) { return IntPtr.Zero; }
    }

    static IntPtr FromWindow(IntPtr hwnd)
    {
        // WM_GETICON ICON_BIG, then the class icon. These belong to the window: never destroyed here.
        Win32.SendMessageTimeoutW(hwnd, Win32.WM_GETICON, new IntPtr(1), IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 100, out IntPtr h);
        if (h == IntPtr.Zero) h = Win32.GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        return h;
    }

    static WriteableBitmap? ToBitmap(IntPtr hIcon)
    {
        if (!Win32.GetIconInfo(hIcon, out var ii)) return null;
        try {
            if (ii.hbmColor == IntPtr.Zero) return null;
            byte[]? color = ReadBits(ii.hbmColor, out int w, out int h);
            if (color == null) return null;
            bool anyAlpha = false;
            for (int i = 3; i < color.Length; i += 4) if (color[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) {
                // an old icon with no alpha channel: the AND mask says which pixels are see-through
                byte[]? mask = ii.hbmMask != IntPtr.Zero ? ReadBits(ii.hbmMask, out _, out _) : null;
                for (int i = 0; i < color.Length; i += 4) color[i + 3] = mask != null && mask[i] != 0 ? (byte)0 : (byte)255;
            }
            // XAML wants premultiplied BGRA
            for (int i = 0; i < color.Length; i += 4) {
                int a = color[i + 3];
                if (a == 255) continue;
                color[i] = (byte)(color[i] * a / 255); color[i + 1] = (byte)(color[i + 1] * a / 255); color[i + 2] = (byte)(color[i + 2] * a / 255);
            }
            var bmp = new WriteableBitmap(w, h);
            color.CopyTo(bmp.PixelBuffer);
            bmp.Invalidate();
            return bmp;
        } finally {
            if (ii.hbmColor != IntPtr.Zero) Win32.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) Win32.DeleteObject(ii.hbmMask);
        }
    }

    static byte[]? ReadBits(IntPtr hbm, out int w, out int h)
    {
        w = h = 0;
        var bmi = new Win32.BITMAPINFO { bmiColors = new uint[256] };
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>();
        IntPtr dc = Win32.GetDC(IntPtr.Zero);
        try {
            if (Win32.GetDIBits(dc, hbm, 0, 0, null, ref bmi, 0) == 0) return null;   // fills width/height
            w = bmi.bmiHeader.biWidth; h = Math.Abs(bmi.bmiHeader.biHeight);
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;
            bmi.bmiHeader.biBitCount = 32; bmi.bmiHeader.biCompression = 0; bmi.bmiHeader.biHeight = -h; bmi.bmiHeader.biSizeImage = 0;
            var bits = new byte[w * h * 4];
            return Win32.GetDIBits(dc, hbm, 0, (uint)h, bits, ref bmi, 0) == 0 ? null : bits;
        } finally { Win32.ReleaseDC(IntPtr.Zero, dc); }
    }
}
