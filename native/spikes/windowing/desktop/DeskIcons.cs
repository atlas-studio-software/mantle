#nullable enable
// DeskIcons.cs - where the desktop icons are, read from Explorer's own icon list (the SysListView32
// "FolderView" inside SHELLDLL_DefView). Shared: compiled into DeskSpike.exe (its click route keeps
// clicks on an icon Explorer's) and Add-Type'd by check.ps1 (it puts a file on the desktop and
// checks that its icon still draws over the layer). No WinUI here, plain C# + Win32.
//
// The list lives in explorer.exe, so the item rects are read through a buffer allocated in that
// process (VirtualAllocEx + LVM_GETITEMRECT + ReadProcessMemory), the classic cross-process
// ListView read. Coordinates come back in the list's client space and are mapped to the screen.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskIcons
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? c, string? t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr after, string? c, string? t);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref PT p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr n, out IntPtr done);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr n, out IntPtr done);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }

    const uint LVM_GETITEMCOUNT = 0x1004, LVM_GETITEMRECT = 0x100E, LVM_GETITEMSTATE = 0x102C, LVM_GETITEMTEXTW = 0x1073;
    const int LVIS_SELECTED = 0x2;

    /// The icon list: Progman > SHELLDLL_DefView > SysListView32, or (classic) a top-level WorkerW
    /// holding the DefView. Zero when Explorer has no desktop.
    public static IntPtr ListView()
    {
        IntPtr defView = FindWindowExW(FindWindowW("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero) {
            EnumWindows((h, l) => {
                IntPtr d = FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero) { defView = d; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
    }

    public sealed class Icon { public int Index; public string Name = ""; public int X, Y, W, H; public bool Selected; }

    /// Every icon with its SCREEN rect (LVIR_BOUNDS: icon + label) and selection state.
    public static List<Icon> List(out string? error)
    {
        error = null;
        var list = new List<Icon>();
        IntPtr lv = ListView();
        if (lv == IntPtr.Zero) { error = "no desktop icon list (SysListView32 under SHELLDLL_DefView)"; return list; }
        int n = (int)SendMessageW(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        GetWindowThreadProcessId(lv, out uint pid);
        IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400 /* VM_OPERATION READ WRITE QUERY */, false, pid);
        if (proc == IntPtr.Zero) { error = "OpenProcess(explorer " + pid + ") failed: " + Marshal.GetLastWin32Error(); return list; }
        const int bufSize = 4096;
        IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, new IntPtr(bufSize), 0x3000, 0x04);
        try {
            if (remote == IntPtr.Zero) { error = "VirtualAllocEx failed: " + Marshal.GetLastWin32Error(); return list; }
            var buf = new byte[bufSize];
            var origin = new PT(); ClientToScreen(lv, ref origin);
            for (int i = 0; i < n; i++) {
                var ic = new Icon { Index = i };
                // rect: in = RECT.left = LVIR_BOUNDS (0)
                var r = new byte[16];
                WriteProcessMemory(proc, remote, r, new IntPtr(16), out _);
                if (SendMessageW(lv, LVM_GETITEMRECT, new IntPtr(i), remote) != IntPtr.Zero &&
                    ReadProcessMemory(proc, remote, r, new IntPtr(16), out _)) {
                    int l = BitConverter.ToInt32(r, 0), t = BitConverter.ToInt32(r, 4), rr = BitConverter.ToInt32(r, 8), b = BitConverter.ToInt32(r, 12);
                    ic.X = origin.X + l; ic.Y = origin.Y + t; ic.W = rr - l; ic.H = b - t;
                }
                // text: an LVITEMW at remote, its text buffer at remote+256 (x64 layout: pszText at 24, cchTextMax at 32)
                var item = new byte[88];
                BitConverter.GetBytes(0x1).CopyTo(item, 0);                    // mask LVIF_TEXT
                BitConverter.GetBytes(i).CopyTo(item, 4);                      // iItem
                BitConverter.GetBytes(remote.ToInt64() + 256).CopyTo(item, 24); // pszText
                BitConverter.GetBytes(260).CopyTo(item, 32);                   // cchTextMax
                WriteProcessMemory(proc, remote, item, new IntPtr(item.Length), out _);
                int chars = (int)SendMessageW(lv, LVM_GETITEMTEXTW, new IntPtr(i), remote);
                if (chars > 0 && ReadProcessMemory(proc, remote + 256, buf, new IntPtr(Math.Min(520, chars * 2)), out _))
                    ic.Name = Encoding.Unicode.GetString(buf, 0, Math.Min(520, chars * 2));
                ic.Selected = ((int)SendMessageW(lv, LVM_GETITEMSTATE, new IntPtr(i), new IntPtr(LVIS_SELECTED)) & LVIS_SELECTED) != 0;
                list.Add(ic);
            }
        } finally {
            if (remote != IntPtr.Zero) VirtualFreeEx(proc, remote, IntPtr.Zero, 0x8000);
            CloseHandle(proc);
        }
        return list;
    }

    /// Is a screen point on an icon (with a small margin)? For the click route: a click there stays Explorer's.
    public static bool OnIcon(List<Icon> icons, int x, int y, int margin = 2)
    {
        foreach (var i in icons)
            if (i.W > 0 && x >= i.X - margin && x < i.X + i.W + margin && y >= i.Y - margin && y < i.Y + i.H + margin) return true;
        return false;
    }
}
