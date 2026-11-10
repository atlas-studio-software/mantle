using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Mantle.App.Native;
using Mantle.Core.Features.WindowList;

namespace Mantle.App.Surfaces.Bar.Tasks;

/// <summary>
/// The live window previews, drawn by DWM (lib/thumbs.js ported; BAR-308..314): DwmRegisterThumbnail draws each source
/// window, scaled, straight into a rectangle of the preview pop-up's own window, every frame. The destination must be
/// a top-level window of THIS process, which the native pop-up is (Electron's main had to make the call through koffi
/// for the same reason). One set = everything one window draws, keyed by source hwnd; Sync makes the set exactly the
/// list it is given. A registered thumbnail keeps drawing until it is unregistered or its window dies, so every road
/// out of the flyout calls Clear. Nothing fails silently: every entry comes back with ok and a reason in words.
/// </summary>
internal sealed partial class Thumbs
{
    const uint TNP_RECTDESTINATION = 0x1, TNP_OPACITY = 0x4, TNP_VISIBLE = 0x8, TNP_SOURCECLIENTAREAONLY = 0x10;
    const int DWMWA_CLOAKED = 14;

    // dwmapi.h declares it inside pshpack1.h: packed, 45 bytes
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Props
    {
        public uint dwFlags;
        public Win32.RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref Props props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out uint value, int size);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref Win32.POINT pt);

    /// One card's box: physical px, relative to the destination's client area.
    public readonly record struct Entry(string Hwnd, double X, double Y, double W, double H);
    /// Drawn = the rectangle DWM was given (client px); Screen = the same on screen (the UI tests sample it).
    public sealed record Result(string Hwnd, bool Ok, string? Reason, bool Gone = false, bool Minimized = false, int SrcW = 0, int SrcH = 0,
        Win32.RECT Drawn = default, Win32.RECT Screen = default);

    readonly Dictionary<string, (IntPtr Thumb, IntPtr Dest)> _live = new(StringComparer.Ordinal);
    readonly string _where;
    readonly Action<string> _log;

    public Thumbs(string where, Action<string> log) { _where = where; _log = log; }

    public int Count => _live.Count;
    public IEnumerable<string> Shown => _live.Keys;

    static IntPtr Handle(string h) => long.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v != 0 ? new IntPtr(v) : IntPtr.Zero;

    void Drop(string src, string why)
    {
        if (!_live.Remove(src, out var t)) return;
        DwmUnregisterThumbnail(t.Thumb);   // the handle is dead either way: nothing else to do with a failure
        if (Environment.GetEnvironmentVariable("MANTLE_THUMBS_DEBUG") == "1") _log($"bar previews: the preview of {src} unregistered ({why})");
    }

    /// Unregister everything. Safe from any exit road, any number of times.
    public int Clear(string why)
    {
        int n = _live.Count;
        foreach (var k in new List<string>(_live.Keys)) Drop(k, why);
        if (n > 0) _log($"bar previews: {n} live picture{(n == 1 ? "" : "s")} taken down ({why})");
        return n;
    }

    /// lib/thumbs.js#prune: drop the previews whose source window left the window list; returns their hwnds.
    public List<string> Prune(IEnumerable<string> gone)
    {
        var out_ = new List<string>();
        foreach (var h in gone) if (_live.ContainsKey(h)) { Drop(h, "its window left the window list"); out_.Add(h); }
        return out_;
    }

    /// lib/thumbs.js#syncSet: make the set EXACTLY `entries` inside `dest`. Never throws.
    public List<Result> Sync(IntPtr dest, IReadOnlyList<Entry> entries)
    {
        var out_ = new List<Result>();
        if (dest == IntPtr.Zero || !Win32.IsWindow(dest)) {
            Clear($"{_where} has no window");
            foreach (var e in entries) out_.Add(new Result(e.Hwnd, false, $"no live picture: {_where} has no window to draw previews into"));
            return out_;
        }
        var origin = new Win32.POINT();
        ClientToScreen(dest, ref origin);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries) {
            var src = Handle(e.Hwnd);
            if (src == IntPtr.Zero) { out_.Add(new Result(e.Hwnd, false, "the card has no window handle")); continue; }
            if (!(e.W >= 2 && e.H >= 2)) { out_.Add(new Result(e.Hwnd, false, "the card measured no room for a picture")); continue; }
            if (!Win32.IsWindow(src)) { Drop(e.Hwnd, "its window closed"); out_.Add(new Result(e.Hwnd, false, "this window has closed", Gone: true)); continue; }
            // cloaked: DWM registers happily and then draws nothing; an empty card that says nothing is the lie, so ask first
            if (DwmGetWindowAttribute(src, DWMWA_CLOAKED, out uint cloak, 4) == 0 && cloak != 0) {
                Drop(e.Hwnd, "cloaked");
                out_.Add(new Result(e.Hwnd, false, "no picture: " + Previews.CloakWho(cloak)));
                continue;
            }
            if (_live.TryGetValue(e.Hwnd, out var t) && t.Dest != dest) { Drop(e.Hwnd, $"{_where} changed"); t = default; }
            if (t.Thumb == IntPtr.Zero) {
                int hr = DwmRegisterThumbnail(dest, src, out var th);
                if (hr != 0 || th == IntPtr.Zero) { out_.Add(new Result(e.Hwnd, false, "no picture: " + Previews.HrWords(hr))); continue; }
                t = (th, dest);
                _live[e.Hwnd] = t;
            }
            wanted.Add(e.Hwnd);
            int shr = DwmQueryThumbnailSourceSize(t.Thumb, out var size);
            if (shr != 0 || size.cx <= 0 || size.cy <= 0) {
                Drop(e.Hwnd, "no source size"); wanted.Remove(e.Hwnd);
                out_.Add(new Result(e.Hwnd, false, shr == 0 ? "no picture: DWM reports the window as 0 x 0, so there is nothing to draw" : "no picture: " + Previews.HrWords(shr)));
                continue;
            }
            var d = Previews.Letterbox(e.X, e.Y, e.W, e.H, size.cx, size.cy);
            var drawn = new Win32.RECT { Left = d.Left, Top = d.Top, Right = d.Right, Bottom = d.Bottom };
            var p = new Props {
                dwFlags = TNP_RECTDESTINATION | TNP_VISIBLE | TNP_OPACITY | TNP_SOURCECLIENTAREAONLY,
                rcDestination = drawn, opacity = 255, fVisible = 1,
                // the whole window, frame included, the way Windows' taskbar shows it: the title bar is the recognisable part
                fSourceClientAreaOnly = 0,
            };
            int uhr = DwmUpdateThumbnailProperties(t.Thumb, ref p);
            if (uhr != 0) {
                Drop(e.Hwnd, "update refused"); wanted.Remove(e.Hwnd);
                out_.Add(new Result(e.Hwnd, false, "no picture: " + Previews.HrWords(uhr)));
                continue;
            }
            var screen = new Win32.RECT { Left = origin.X + drawn.Left, Top = origin.Y + drawn.Top, Right = origin.X + drawn.Right, Bottom = origin.Y + drawn.Bottom };
            out_.Add(new Result(e.Hwnd, true, null, Minimized: Win32.IsIconic(src), SrcW: size.cx, SrcH: size.cy, Drawn: drawn, Screen: screen));
        }
        foreach (var k in new List<string>(_live.Keys)) if (!wanted.Contains(k)) Drop(k, "not in the flyout any more");
        return out_;
    }
}
