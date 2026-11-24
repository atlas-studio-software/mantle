// LevitateStill.cs - the levitated look's shared frost (lane r10-newlook): the wallpaper still pre-blurred ONCE per
// wallpaper change into two copies every levitated panel shares, light (sigma 6 px) for small chrome and soft (sigma 15 px)
// for big panels (Mantle.Core/Look/Levitate/Frost.cs, the prototype quartz-home/app.js 2026-11-24).
//
// The still is the one DesktopGlass already follows (look.json desktop.still, its file watch, its change event): this file
// only adds the 512 px-wide decode and the two blurs, on a pool thread, then two small WriteableBitmaps on the UI thread.
// Never live: a panel moving or resizing does no pixel work. Until the copies are ready, or when they fail, a panel shows
// the flat #2c5258 (LevitatePanel); the failure is said in the log once per still.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Mantle.App.Shell.Glass;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Shell.Levitate;

/// The two blurred copies of one still: the bitmaps the panels draw and their pixels (the brightness check samples them).
internal sealed class FrostPair
{
    public required string Key { get; init; }
    public required WriteableBitmap Light { get; init; }
    public required WriteableBitmap Soft { get; init; }
    public required FrostBitmap LightPx { get; init; }
    public required FrostBitmap SoftPx { get; init; }
    public required string Words { get; init; }
    public WriteableBitmap For(bool big) => big ? Soft : Light;
    public FrostBitmap PxFor(bool big) => big ? SoftPx : LightPx;
}

internal static class LevitateStill
{
    static bool _hooked;
    static string? _key, _failedKey, _failedWhy, _said;
    static Task<FrostPair?>? _task;

    /// The still or its file changed (UI thread): panels that are up take the copies again.
    public static event Action<string>? Changed;

    static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        DesktopGlass.Changed += why => Changed?.Invoke(why);
    }

    /// The frost of the look's still now (UI thread): made once per (file, its stamp) and shared; null with
    /// <paramref name="why"/> when there is none.
    public static Task<FrostPair?> Get(out string? why)
    {
        Hook();
        why = null;
        var g = DesktopGlass.Look;
        if (g?.Still is not { } path) { why = g == null ? "the look has not been read yet" : g.Why ?? "no desktop still"; return Task.FromResult<FrostPair?>(null); }
        string stamp;
        try {
            var fi = new FileInfo(path);
            if (!fi.Exists) { why = "the desktop still is missing: " + path; return Task.FromResult<FrostPair?>(null); }
            stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
        } catch (Exception e) { why = "the desktop still could not be read (" + e.Message + "): " + path; return Task.FromResult<FrostPair?>(null); }
        string key = path + "|" + stamp;
        if (key == _failedKey) { why = _failedWhy; return Task.FromResult<FrostPair?>(null); }
        if (key == _key && _task != null) return _task;
        _key = key;
        _task = Make(path, key);
        return _task;
    }

    /// Why the frost of the current still failed, or null.
    public static string? Failure => _failedWhy;

    static async Task<FrostPair?> Make(string path, string key)
    {
        long t0 = Stopwatch.GetTimestamp();
        FrostBitmap light, soft;
        int iw, ih;
        try {
            (light, soft, iw, ih) = await Task.Run(() => {
                var (scaled, a, b) = Decode(path);
                var (l, s) = Frost.Both(scaled);
                return (l, s, a, b);
            });
        }
        catch (Exception e) {
            _failedKey = key;
            _failedWhy = "the frosted still could not be made (" + e.Message + "): " + path;
            Say("levitate: " + _failedWhy + "; the panels show the flat fallback colour");
            return null;
        }
        // back on the UI thread (the caller's await): the two small copies into bitmaps
        var lb = Bitmap(light);
        var sb = Bitmap(soft);
        string words = FormattableString.Invariant(
            $"{iw}x{ih} scaled to {light.W}x{light.H}, blurred once to light (sigma {LevitateTokens.LightBlurPx:0}) and soft (sigma {LevitateTokens.SoftBlurPx:0}) in {(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:0} ms");
        Host.Log.Line("levitate: the frosted still " + words);
        _said = null;
        return new FrostPair { Key = key, Light = lb, Soft = sb, LightPx = light, SoftPx = soft, Words = words };
    }

    /// Said once per reason (a failing still is asked for by every panel that comes up).
    static void Say(string line) { if (line == _said) return; _said = line; Host.Log.Line(line); }

    static WriteableBitmap Bitmap(FrostBitmap f)
    {
        var bmp = new WriteableBitmap(f.W, f.H);
        f.Bgra.CopyTo(bmp.PixelBuffer);
        bmp.Invalidate();
        return bmp;
    }

    /// The still scaled to 512 px wide (height by aspect, Frost.BaseSize), BGRA premultiplied (the still is opaque). Pool
    /// thread; no XAML object. The same decoder calls as DesktopGlass.Decode, without its cover cut.
    internal static (FrostBitmap Scaled, int Iw, int Ih) Decode(string path)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        var dec = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        int iw = (int)dec.OrientedPixelWidth, ih = (int)dec.OrientedPixelHeight;
        var (w, h) = Frost.BaseSize(iw, ih);
        if (w <= 0 || h <= 0) throw new InvalidDataException($"the picture reports {iw}x{ih}");
        var tf = new Windows.Graphics.Imaging.BitmapTransform {
            ScaledWidth = (uint)w, ScaledHeight = (uint)h, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
        };
        var data = dec.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tf,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        var px = data.DetachPixelData();
        if (px.Length != w * h * 4) throw new InvalidDataException($"the decoder gave {px.Length} bytes for {w}x{h}");
        return (new FrostBitmap(px, w, h), iw, ih);
    }
}
