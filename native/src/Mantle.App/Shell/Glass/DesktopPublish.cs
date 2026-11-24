// lane r10-look: native Mantle publishes the look-sync `desktop` record, so Helm and Quartz paint the same wallpaper
// (CONTRACT.md section 4 and the user's 2026-10-10 answer 2: the desktop wallpaper is the one place a picture is chosen). Native has no
// wallpaper library yet, so the desktop's picture is Windows' own wallpaper (SPI_GETDESKWALLPAPER). The publishing itself
// (still first, then the record, keep 3, the guard, the rev and retries) is Mantle.Core's DesktopPublisher; this file is
// only where the wallpaper comes from and when to look again: at start, on WM_SETTINGCHANGE SPI_SETDESKWALLPAPER (the
// bar's frame, where the live glass hears it too) and when the glass changes (the guard is worked out for its dim and blur).
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Look;

namespace Mantle.App.Shell.Glass;

/// The desktop record's publisher for this run (one), fed by the look host.
internal static class DesktopPublish
{
    /// A --test run never reads Windows' wallpaper: it publishes only when this names a picture file (the UI tests write
    /// their own look.json desktop record and must keep it, so by default a test run leaves the record alone).
    public const string TestEnv = "MANTLE_TEST_WALLPAPER";

    static DesktopPublisher? _pub;
    static IDisposable? _off;

    /// App start (host thread), after the look host started: publish once, then on every glass change. Never throws.
    public static void Start(LookHost look, SafeFile files)
    {
        if (_pub != null) return;
        try {
            string? testPicture = null;
            if (Host.Args.Test) {
                testPicture = Host.Env(TestEnv);
                if (string.IsNullOrEmpty(testPicture)) {
                    Host.Log.Line("desktop: this test run leaves look.json's desktop record as it is and never reads Windows' wallpaper (" + TestEnv + " names no picture)");
                    return;
                }
                Host.Log.Line("desktop: this test run publishes " + TestEnv + " as the desktop picture, never Windows' wallpaper");
            }
            var pub = new DesktopPublisher(new DesktopPublisherOptions {
                Sync = look.Sync, Files = files, Source = new WindowsWallpaper(testPicture),
                Glass = () => look.Local()["glass"], Log = line => Host.Log.Line(line),
            });
            _pub = pub;
            // the listener runs under the look host's lock: it only posts
            _off = look.OnChange(e => { if (e.Why is "glass" or "apply" or "sync") Check("the glass changed (" + e.Why + ")"); });
            Check("start");
        } catch (Exception e) { Host.Log.Line("desktop: the desktop record publisher did not start (" + e.GetType().Name + ": " + e.Message + ")"); }
    }

    /// WM_SETTINGCHANGE SPI_SETDESKWALLPAPER (any thread): look at Windows' wallpaper again. Several bars may say it; the
    /// publisher coalesces them, and the same wallpaper again publishes nothing.
    public static void WallpaperChanged() => Check("Windows' wallpaper changed");

    static void Check(string why)
    {
        var p = _pub;
        if (p == null) return;
        _ = Task.Run(() => p.CheckAsync(why));
    }
}

/// Windows' own wallpaper as the desktop's picture: a full-size JPEG still (quality 90, at most the cover size of the primary
/// screen) and the still decoded for the guard the way the glass layers decode it (DesktopGlass.Decode). Pool thread.
internal sealed class WindowsWallpaper : IDesktopSource
{
    const float JpegQuality = 0.9f;
    readonly string? _testPicture;

    /// <param name="testPicture">a --test run's picture (MANTLE_TEST_WALLPAPER) in place of Windows' wallpaper, else null</param>
    public WindowsWallpaper(string? testPicture) => _testPicture = testPicture;

    public DesktopNow Current()
    {
        string path = _testPicture ?? WallpaperPath();
        if (path.Length == 0) return DesktopNow.None("Windows shows a plain colour, not a picture, so there is no wallpaper picture to share");
        FileInfo fi;
        try { fi = new FileInfo(path); } catch (Exception e) { return DesktopNow.None("Windows' wallpaper could not be read (" + e.Message + "): " + path); }
        if (!fi.Exists) return DesktopNow.None("Windows' wallpaper file is missing: " + path);
        // the id follows the picture, not only its path: Windows rewrites TranscodedWallpaper in place for a new picture
        string stamp = fi.Length.ToString(CultureInfo.InvariantCulture) + ":" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        string id = "windows:" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(fi.FullName.ToLowerInvariant() + "|" + stamp)))[..12];
        string name = Path.GetFileNameWithoutExtension(fi.Name);
        string title = name.Length == 0 || string.Equals(name, "TranscodedWallpaper", StringComparison.OrdinalIgnoreCase) ? "Windows wallpaper" : name;
        return new DesktopNow(new DesktopPick(id, "picture", title, fi.FullName), null);
    }

    public DesktopShot Still(DesktopPick pick)
    {
        if (pick.File is not { Length: > 0 } file) return DesktopShot.Fail("the wallpaper has no file");
        try {
            var (sw, sh, _) = Primary();
            var sf = global::Windows.Storage.StorageFile.GetFileFromPathAsync(file).AsTask().GetAwaiter().GetResult();
            using var stream = sf.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            var dec = global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            int iw = (int)dec.OrientedPixelWidth, ih = (int)dec.OrientedPixelHeight;
            var (w, h) = DesktopPublisher.CoverSize(iw, ih, sw, sh);
            if (w <= 0 || h <= 0) return DesktopShot.Fail("the picture has no size");
            var tf = new global::Windows.Graphics.Imaging.BitmapTransform {
                ScaledWidth = (uint)w, ScaledHeight = (uint)h, InterpolationMode = global::Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
            };
            var data = dec.GetPixelDataAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, tf,
                global::Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, global::Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            byte[] px = data.DetachPixelData();
            if (px.Length != w * h * 4) return DesktopShot.Fail($"the decoder gave {px.Length} bytes for {w}x{h}");
            using var mem = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            var opts = new global::Windows.Graphics.Imaging.BitmapPropertySet {
                { "ImageQuality", new global::Windows.Graphics.Imaging.BitmapTypedValue(JpegQuality, global::Windows.Foundation.PropertyType.Single) },
            };
            var enc = global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, mem, opts).AsTask().GetAwaiter().GetResult();
            enc.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, px);
            enc.FlushAsync().AsTask().GetAwaiter().GetResult();
            var bytes = new byte[mem.Size];
            using (var r = new global::Windows.Storage.Streams.DataReader(mem.GetInputStreamAt(0))) {
                r.LoadAsync((uint)mem.Size).AsTask().GetAwaiter().GetResult();
                r.ReadBytes(bytes);
            }
            return bytes.Length > 0 ? new DesktopShot(true, bytes, w, h, null) : DesktopShot.Fail("the picture would not encode as a JPEG");
        } catch (Exception e) {
            return DesktopShot.Fail("the picture could not be made into a still (" + e.GetType().Name + ": " + e.Message + ")");
        }
    }

    public DesktopPicture Picture(string stillFile)
    {
        var (w, h, scale) = Primary();
        var (px, _, _) = DesktopGlass.Decode(stillFile, w, h);
        return new DesktopPicture(px, w, h, scale);
    }

    /// The primary monitor in physical pixels and its scale (the desktop layer's monitor; the guard's band is on it).
    static (int W, int H, double Scale) Primary()
    {
        IntPtr mon = Mantle.App.Native.Win32.MonitorFromPoint(new Mantle.App.Native.Win32.POINT { X = 0, Y = 0 }, 1 /* PRIMARY */);
        var mi = new Mantle.App.Native.Win32.MONITORINFO { cbSize = Marshal.SizeOf<Mantle.App.Native.Win32.MONITORINFO>() };
        if (mon == IntPtr.Zero || !Mantle.App.Native.Win32.GetMonitorInfoW(mon, ref mi)) throw new InvalidOperationException("the primary monitor could not be read");
        double scale = (Mantle.App.Native.Win32.GetDpiForMonitor(mon, 0, out uint d, out _) == 0 ? d : 96) / 96.0;
        return (mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top, scale);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    static extern unsafe bool SystemParametersInfo(uint action, uint param, char* buf, uint winIni);

    static unsafe string WallpaperPath()
    {
        const uint SPI_GETDESKWALLPAPER = 0x0073;
        char* buf = stackalloc char[520];
        if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, 520, buf, 0)) return "";
        return new string(buf).Trim();
    }
}
