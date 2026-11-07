#nullable enable
// DeskProbe.cs - check.ps1's own helpers for the desktop lane (Add-Type; not part of DeskSpike.exe):
// the desktop's window tree in z-order, pixel counts over a grab, and keyboard chords via SendInput.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? c, string? t);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint SendInput(uint n, KI[] inputs, int size);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RC { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
    // INPUT with KEYBDINPUT, padded to the 40-byte x64 INPUT size (MOUSEINPUT is the largest member).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct KI { [FieldOffset(0)] public uint type; [FieldOffset(8)] public ushort vk; [FieldOffset(10)] public ushort scan;
                [FieldOffset(12)] public uint flags; [FieldOffset(16)] public uint time; [FieldOffset(24)] public IntPtr extra; }

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    static string Proc(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName + ":" + pid; } catch { return "pid " + pid; }
    }
    static string Line(int i, IntPtr h, string indent)
    {
        GetWindowRect(h, out var r);
        return indent + i + ". " + ClassOf(h) + " hwnd=" + h.ToInt64() + " " + Proc(h) + (IsWindowVisible(h) ? " visible" : " hidden") +
               " rect=" + r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T) +
               " style=0x" + GetWindowLongPtrW(h, -16).ToInt64().ToString("X8") + " ex=0x" + GetWindowLongPtrW(h, -20).ToInt64().ToString("X8");
    }

    /// Progman's children top to bottom (and each child's own children, one level), then every
    /// top-level Progman / WorkerW in z-order with its index among ALL top-level windows.
    public static string[] Tree()
    {
        var o = new List<string>();
        IntPtr progman = FindWindowW("Progman", null);
        o.Add("Progman " + (progman == IntPtr.Zero ? "MISSING" : Line(0, progman, "")));
        int i = 0;
        for (IntPtr c = GetWindow(progman, 5 /* GW_CHILD */); c != IntPtr.Zero; c = GetWindow(c, 2 /* GW_HWNDNEXT */)) {
            o.Add(Line(++i, c, "  "));
            int j = 0;
            for (IntPtr g = GetWindow(c, 5); g != IntPtr.Zero && j < 6; g = GetWindow(g, 2)) o.Add(Line(++j, g, "      "));
        }
        int k = 0;
        EnumWindows((h, l) => {
            k++;
            string cls = ClassOf(h);
            if (cls == "WorkerW" || cls == "Progman") o.Add("top-level #" + k + " " + Line(k, h, ""));
            return true;
        }, IntPtr.Zero);
        o.Add("top-level windows: " + k);
        return o.ToArray();
    }

    /// Progman's children's handles, top to bottom.
    public static long[] ProgmanChildren()
    {
        var o = new List<long>();
        IntPtr progman = FindWindowW("Progman", null);
        for (IntPtr c = GetWindow(progman, 5); c != IntPtr.Zero; c = GetWindow(c, 2)) o.Add(c.ToInt64());
        return o.ToArray();
    }
    public static long Progman() => FindWindowW("Progman", null).ToInt64();
    public static string ClassOfHandle(long h) => ClassOf(new IntPtr(h));
    public static bool Visible(long h) => IsWindowVisible(new IntPtr(h));

    public static string At(int x, int y)
    {
        IntPtr h = WindowFromPoint(new PT { X = x, Y = y });
        IntPtr root = GetAncestor(h, 2);
        return ClassOf(h) + " (" + h.ToInt64() + ", " + Proc(h) + ") root " + ClassOf(root) + " (" + root.ToInt64() + ")";
    }
    public static string Foreground() { IntPtr h = GetForegroundWindow(); return ClassOf(h) + " (" + h.ToInt64() + ", " + Proc(h) + ")"; }

    /// [near, total]: pixels of a BGRA grab (width w) inside rect x,y,rw,rh within tol (per channel)
    /// of r,g,b, skipping any pixel inside one of the exclude rects (x,y,w,h quads). step = sample stride.
    public static int[] Near(byte[] px, int w, int x, int y, int rw, int rh, int r, int g, int b, int tol, int[] exclude, int step)
    {
        int near = 0, total = 0, h = px.Length / 4 / w;
        for (int yy = y; yy < y + rh && yy < h; yy += step)
            for (int xx = x; xx < x + rw && xx < w; xx += step) {
                bool skip = false;
                for (int e = 0; e + 3 < exclude.Length; e += 4)
                    if (xx >= exclude[e] && xx < exclude[e] + exclude[e + 2] && yy >= exclude[e + 1] && yy < exclude[e + 1] + exclude[e + 3]) { skip = true; break; }
                if (skip) continue;
                int i = (yy * w + xx) * 4;
                total++;
                if (Math.Abs(px[i + 2] - r) <= tol && Math.Abs(px[i + 1] - g) <= tol && Math.Abs(px[i] - b) <= tol) near++;
            }
        return new[] { near, total };
    }

    /// A chord: every key down in order, then up in reverse, in ONE SendInput call (as a person's hands
    /// can not do faster). Returns the number of events Windows took.
    public static uint Chord(params ushort[] vks)
    {
        var a = new KI[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++) {
            a[i].type = 1; a[i].vk = vks[i]; a[i].flags = Ext(vks[i]);
            int j = vks.Length * 2 - 1 - i;
            a[j].type = 1; a[j].vk = vks[i]; a[j].flags = Ext(vks[i]) | 0x2 /* KEYUP */;
        }
        return SendInput((uint)a.Length, a, 40);
    }
    static uint Ext(ushort vk) => vk == 0x5B || vk == 0x5C ? 0x1u /* EXTENDEDKEY */ : 0u;
}
