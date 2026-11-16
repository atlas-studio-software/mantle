// DesktopGlass.cs - the shared desktop-still backdrop of the native windows that sit over the desktop (look.md SEE-005).
//
// The user's 2026-11-11 look (look-sync CONTRACT "sharp painted glass + tint override"): each window is a frame over the
// desktop. Behind its own UI it paints look.json's desktop.still SHARP (no blur, no saturate), cover-fit and centred on
// the monitor the window is on, the way the desktop draws it, and offset by the window's position, so the picture stays
// put on the screen and the window moves over it. One tint lies over it, Wash.Alpha's (look-sync washAlpha), the same in
// every state of the window.
//
// Cost: the still is decoded and scaled ONCE per (file, monitor size), on a pool thread, into one bitmap that every window
// on that monitor shares. A move only changes the brush's translation: no decode, no pixel work. Nothing runs at rest:
// the record is heard through look sync's own folder watch, the picture file through one FileSystemWatcher on its folder.
// A hidden window does nothing until it shows again.
//
// No silent failures: a missing or unreadable still gives a plain fill (the window's own) and the log says why.
// MANTLE_TEST_GLASS=electron (a --test run only) keeps native's earlier look for the Electron screenshot pairs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Mantle.Core.Look;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Shell.Glass;

/// What the look says now: the still to paint (or why none) and the tint per theme. Made on the host thread.
internal sealed record GlassLook(string? Still, double Rev, string? Why, WashResult Dark, WashResult Light)
{
    public WashResult For(bool dark) => dark ? Dark : Light;
}

/// One decoded picture: the still cover-fit to a monitor, exactly the monitor's size in physical px.
internal sealed class GlassPic
{
    public required string Key { get; init; }
    public required WriteableBitmap Bitmap { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    public required string Words { get; init; }
    /// The readability guard's need profile of this picture (Core NativeGuard, look.md SEE-017), per theme: worked out once,
    /// on the pool thread right after the decode, from these very pixels; a window takes the worst strip over its rows.
    public required GuardRows GuardDark { get; init; }
    public required GuardRows GuardLight { get; init; }
    public GuardRows Guard(bool dark) => dark ? GuardDark : GuardLight;
}

internal static class DesktopGlass
{
    public const string TestEnv = "MANTLE_TEST_GLASS";
    public const int KeepPics = 2;          // monitor sizes kept decoded (a second monitor, a resolution change)
    public const int FileSettleMs = 150;    // a still being rewritten: one look after its last write event

    static DispatcherQueue? _ui;
    static GlassLook? _look;
    static FileSystemWatcher? _watch;
    static string? _watchPath;
    static DispatcherQueueTimer? _settle;
    static readonly Dictionary<string, Task<GlassPic?>> _pics = new();
    static readonly LinkedList<string> _order = new();
    static readonly Dictionary<string, string> _failed = new();

    /// True when the new look is off for this run: MANTLE_TEST_GLASS=electron in a --test run (the Electron pairs).
    public static bool ElectronLook => Host.Args.Test && string.Equals(Host.Env(TestEnv), "electron", StringComparison.OrdinalIgnoreCase);

    /// The look now (UI thread), null before the host has said.
    public static GlassLook? Look => _look;

    /// The look or the picture file changed (UI thread). Layers re-apply when up, mark themselves stale when down.
    public static event Action<string>? Changed;

    /// App start (host thread): follow the look host. Every change of the look (the record, the glass, sync) is turned into
    /// a GlassLook on the host thread and posted to the UI thread; nothing is computed while nothing changes.
    public static void Feed(LookHost look, DispatcherQueue ui)
    {
        _ui = ui;
        if (ElectronLook) { Host.Log.Line("glass: " + TestEnv + "=electron: this test run keeps native's earlier look (the Electron screenshot pairs)"); return; }
        // the listener only posts (it may run under the look host's lock); the UI thread reads the look, once per burst
        int pending = 0;
        void Post(string why)
        {
            if (System.Threading.Interlocked.Exchange(ref pending, 1) == 1) return;
            ui.TryEnqueue(() => {
                System.Threading.Interlocked.Exchange(ref pending, 0);
                long t0 = Stopwatch.GetTimestamp();
                GlassLook g;
                try {
                    var s = look.DesktopStill();
                    g = new GlassLook(s.Still, s.Rev, s.Why, look.Wash("dark"), look.Wash("light"));
                } catch (Exception e) { Host.Log.Line("glass: the look could not be read (" + e.Message + ")"); return; }
                double read = Ms(t0);
                int told = Take(g, why);
                // the UI thread's share of a look change, said when the look moved (a theme or wallpaper change in Quartz or
                // Helm rewrites look.json; the windows that are up re-apply here, a new still decodes on the pool thread)
                if (told >= 0) Host.Log.Line(FormattableString.Invariant($"glass: the look change took {Ms(t0):0.0} ms on the UI thread (read {read:0.0} ms; {told} window layer(s) told)"));
            });
        }
        look.OnChange(e => Post(e.Why));
        Post("start");
    }

    static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    /// The look as read now (UI thread): -1 when nothing the windows paint moved, else how many layers were told.
    static int Take(GlassLook g, string why)
    {
        var old = _look;
        _look = g;
        bool stillMoved = old == null || old.Still != g.Still || old.Rev != g.Rev;
        bool tintMoved = old == null || old.Dark != g.Dark || old.Light != g.Light;
        if (!stillMoved && !tintMoved) return -1;
        if (stillMoved) {
            // the cache key carries the file's stamp: a new file decodes anew, a rev bump over the same file does not
            Host.Log.Line(g.Still != null ? $"glass: the desktop still is {g.Still} (record rev {g.Rev}; {why})" : "glass: no desktop still: " + g.Why);
            Watch(g.Still);
            if (g.Still != null) Ahead();
        }
        if (tintMoved) Host.Log.Line($"glass: tint dark {g.Dark.Alpha:0.00} ({g.Dark.Text}); light {g.Light.Alpha:0.00} ({g.Light.Text})");
        var told = Changed;
        told?.Invoke(stillMoved ? "the desktop still changed" : "the tint changed");
        return told?.GetInvocationList().Length ?? 0;
    }

    /// A new still (at start, or the desktop changed it): decode it for the primary monitor now, on the pool thread, so the
    /// first pop-up paints the picture in its first frame instead of its plain fill for the length of a decode. It is the
    /// decode the first window would make anyway (the same key, shared), made once per still; nothing runs at rest.
    static void Ahead()
    {
        try {
            IntPtr mon = Mantle.App.Native.Win32.MonitorFromPoint(new Mantle.App.Native.Win32.POINT { X = 0, Y = 0 }, 1 /* PRIMARY */);
            var mi = new Mantle.App.Native.Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Mantle.App.Native.Win32.MONITORINFO>() };
            if (mon == IntPtr.Zero || !Mantle.App.Native.Win32.GetMonitorInfoW(mon, ref mi)) { Host.Log.Line("glass: the primary monitor could not be read, so the still is decoded at the first window"); return; }
            double scale = (Mantle.App.Native.Win32.GetDpiForMonitor(mon, 0, out uint d, out _) == 0 ? d : 96) / 96.0;
            int w = mi.rcMonitor.Right - mi.rcMonitor.Left, h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            long t0 = Stopwatch.GetTimestamp();
            var t = Pic(w, h, scale, out var why);
            if (why != null) { Host.Log.Line("glass: nothing decoded ahead: " + why); return; }
            if (t.IsCompleted) return;   // this size is decoded already
            _ = t.ContinueWith(r => {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (r.Status == TaskStatus.RanToCompletion && r.Result != null) Host.Log.Line($"glass: decoded ahead for the primary monitor ({w}x{h} at {scale:0.##}) in {ms:0} ms, before any window asked");
            }, TaskScheduler.Default);
        } catch (Exception e) { Host.Log.Line("glass: the still could not be decoded ahead (" + e.Message + "); the first window decodes it"); }
    }

    // ------------------------------------------------------------------ the picture, once per file and monitor size

    /// The still cover-fit to a monitor of <paramref name="w"/> x <paramref name="h"/> physical px at DPI scale
    /// <paramref name="scale"/>: decoded once (pool thread) and its readability guard worked out there too, the bitmap made on
    /// the UI thread, then shared. Null with <paramref name="why"/> when there is none.
    public static Task<GlassPic?> Pic(int w, int h, double scale, out string? why)
    {
        why = null;
        var g = _look;
        if (g?.Still is not { } path) { why = g == null ? "the look has not been read yet" : g.Why ?? "no desktop still"; return Task.FromResult<GlassPic?>(null); }
        if (w <= 0 || h <= 0) { why = "the monitor reports no size"; return Task.FromResult<GlassPic?>(null); }
        string stamp;
        try {
            var fi = new FileInfo(path);
            if (!fi.Exists) { why = "the desktop still is missing: " + path; return Task.FromResult<GlassPic?>(null); }
            stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
        } catch (Exception e) { why = "the desktop still could not be read (" + e.Message + "): " + path; return Task.FromResult<GlassPic?>(null); }
        string key = path + "|" + stamp + "|" + Size(w, h, scale);
        if (_failed.TryGetValue(key, out var bad)) { why = bad; return Task.FromResult<GlassPic?>(null); }
        if (_pics.TryGetValue(key, out var have)) return have;
        var ui = _ui!;
        var t = Make(path, key, w, h, scale, ui);
        _pics[key] = t;
        _order.AddLast(key);
        while (_order.Count > KeepPics) { _pics.Remove(_order.First!.Value); _order.RemoveFirst(); }
        return t;
    }

    static string Size(int w, int h, double scale) => w + "x" + h + "@" + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// Why the still could not be made for a monitor of that size (the decode's own words), or null.
    public static string? FailureFor(int w, int h, double scale)
    {
        foreach (var kv in _failed) if (kv.Key.EndsWith("|" + Size(w, h, scale), StringComparison.Ordinal)) return kv.Value;
        return null;
    }

    static async Task<GlassPic?> Make(string path, string key, int w, int h, double scale, DispatcherQueue ui)
    {
        long t0 = Stopwatch.GetTimestamp();
        byte[] px; int iw, ih; GuardRows gd, gl; double gms;
        try {
            (px, iw, ih, gd, gl, gms) = await Task.Run(() => {
                var (p, a, b) = Decode(path, w, h);
                long g0 = Stopwatch.GetTimestamp();
                var d = NativeGuard.Rows(p, w, h, scale, dark: true);
                var l = NativeGuard.Rows(p, w, h, scale, dark: false);
                return (p, a, b, d, l, (Stopwatch.GetTimestamp() - g0) * 1000.0 / Stopwatch.Frequency);
            });
        }
        catch (Exception e) {
            string why = "the desktop still could not be decoded (" + e.Message + "): " + path;
            _failed[key] = why;
            Host.Log.Line("glass: " + why);
            return null;
        }
        // back on the UI thread (the caller's await): the one copy into the shared bitmap
        long c0 = Stopwatch.GetTimestamp();
        var bmp = new WriteableBitmap(w, h);
        px.CopyTo(bmp.PixelBuffer);
        bmp.Invalidate();
        double ms = Ms(t0), copy = Ms(c0);
        string words = $"{iw}x{ih} cover-fit to {w}x{h}, sharp (no blur, no saturate), decoded once in {ms:0} ms";
        Host.Log.Line("glass: the desktop still " + words + FormattableString.Invariant($" (the copy into the shared bitmap: {copy:0.0} ms on the UI thread)"));
        var (dn, ds) = gd.NeedOver(0, gd.HeightDip); var (ln, ls) = gl.NeedOver(0, gl.HeightDip);
        Host.Log.Line(FormattableString.Invariant($"glass: the readability guard over the sharp still ({gd.Strips} strips of {NativeGuard.StripDip:0} DIP, a {gd.BoxPx} px glyph box, 4.5:1, 8-bit) worked out once in {gms:0} ms: dark needs up to {Math.Min(1, dn):0.00} (at {gd.StripTop(ds) / scale:0} DIP), light up to {Math.Min(1, ln):0.00} (at {gl.StripTop(ls) / scale:0} DIP)"));
        return new GlassPic { Key = key, Bitmap = bmp, W = w, H = h, Words = words, GuardDark = gd, GuardLight = gl };
    }

    /// Cover-fit and centred, the way the desktop draws the still (and look-sync's guard reads it): scale = max(w/iw, h/ih),
    /// the overflow cut evenly from both sides. Fant scaling, no blur, no saturate. Pool thread; no XAML object.
    internal static (byte[] Px, int Iw, int Ih) Decode(string path, int w, int h)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        var dec = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        int iw = (int)dec.OrientedPixelWidth, ih = (int)dec.OrientedPixelHeight;
        var (tw, th, cx, cy) = Cover(iw, ih, w, h);
        var tf = new Windows.Graphics.Imaging.BitmapTransform {
            ScaledWidth = (uint)tw, ScaledHeight = (uint)th, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
            Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = (uint)cx, Y = (uint)cy, Width = (uint)w, Height = (uint)h },
        };
        var data = dec.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tf,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        var px = data.DetachPixelData();
        if (px.Length != w * h * 4) throw new InvalidDataException($"the decoder gave {px.Length} bytes for {w}x{h}");
        return (px, iw, ih);
    }

    /// The cover size of an iw x ih picture on a w x h monitor and the cut at its top-left (the same rounding the launcher's
    /// wallpaper still uses).
    internal static (int Tw, int Th, int Cx, int Cy) Cover(int iw, int ih, int w, int h)
    {
        double k = Math.Max(w / (double)iw, h / (double)ih);
        int tw = Math.Max(w, (int)Math.Ceiling(iw * k)), th = Math.Max(h, (int)Math.Ceiling(ih * k));
        return (tw, th, (tw - w) / 2, (th - h) / 2);
    }


    // ------------------------------------------------------------------ the file watch (no poll)

    static void Watch(string? path)
    {
        if (path == _watchPath) return;
        _watch?.Dispose(); _watch = null; _watchPath = path;
        if (path == null) return;
        try {
            string? dir = Path.GetDirectoryName(path);
            if (dir == null || !Directory.Exists(dir)) { Host.Log.Line("glass: the still's folder is missing, so a change of the picture is heard only through look.json: " + path); return; }
            var w = new FileSystemWatcher(dir, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler on = (_, _) => _ui?.TryEnqueue(FileMoved);
            w.Changed += on; w.Created += on; w.Deleted += on;
            w.Renamed += (_, _) => _ui?.TryEnqueue(FileMoved);
            w.Error += (_, e) => _ui?.TryEnqueue(() => Host.Log.Line("glass: the still's file watch failed (" + e.GetException().Message + "); a change is heard only through look.json"));
            w.EnableRaisingEvents = true;
            _watch = w;
        } catch (Exception e) { Host.Log.Line("glass: the still's file could not be watched (" + e.Message + "); a change is heard only through look.json"); }
    }

    /// A write event on the still: one look after the writes settle (a one-shot, never a poll).
    static void FileMoved()
    {
        if (_ui == null) return;
        if (_settle == null) {
            _settle = _ui.CreateTimer();
            _settle.Interval = TimeSpan.FromMilliseconds(FileSettleMs);
            _settle.IsRepeating = false;
            _settle.Tick += (t, _) => {
                t.Stop();
                _failed.Clear();   // a file that failed may read now; a good one has a new stamp, so a new key
                Host.Log.Line("glass: the desktop still's file changed: the windows that are up take it again");
                Changed?.Invoke("the still's file changed");
            };
        }
        _settle.Stop();
        _settle.Start();
    }
}
