using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Mantle.App.Native;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The launcher's glass around its rows (ui/launcher/launcher.css .bdrop, .panel box-shadow, ui/shared/unfold-live.js):
/// <list type="bullet">
/// <item>the BACKDROP PICTURE: light = a capture of the screen behind the pane taken before the window shows, blurred
/// (blur(12px) saturate(150%), --lg-blur) and faded in over 150 ms (.bdrop.on); dark = the Windows wallpaper under the
/// pane (main.js backdropPicture: never the windows behind), blurred 5 px and dimmed a fifth. No picture: the pane keeps its
/// fill and the footer says "The see-through effect is off: ..." with the reason in its tooltip, as Electron's does.</item>
/// <item>the light DROP SHADOW (--lg-shadow, three layers), drawn once per size into a bitmap that is empty under the pane
/// (a CSS box-shadow is never drawn under its box, and the pane is see-through). Dark has none (launcher.css).</item>
/// <item>the UNFOLD on open and the FOLD on close: the pane is cut to its left half and opens round a centre hinge (size
/// 'pop', 340 ms; the leaf lands at 85 % on cubic-bezier(.4,0,.1,1), the dim on the leaf fading over the rest); the fold is
/// the same backwards on cubic-bezier(.9,0,.6,1). Windows' "Animation effects" off (reduced motion): a 120 ms cross-fade.</item>
/// </list>
/// Pixel work (blur, shadow) runs off the UI thread; only the bitmap copy is on it.
/// </summary>
internal sealed class LauncherLook
{
    public const double PopMs = 340, ReducedMs = 120, Land = 0.85, FadeInMs = 150;
    const double LightSigma = 12, LightSaturate = 1.5, DarkSigma = 5, DarkDim = 0.8;

    readonly DispatcherQueue _q;
    readonly Mantle.Core.Logging.ILogSink _log;
    readonly FrameworkElement _panel, _shadowEl, _dim;
    readonly Border _bdrop;
    readonly Microsoft.UI.Xaml.Controls.Image _shadow;
    readonly ImageBrush _bdropBrush = new() { Stretch = Stretch.Fill };
    WriteableBitmap? _bdropBmp;
    int _shotSeq;
    string? _shadowKey;
    bool _folding;
    CompositionScopedBatch? _foldBatch;

    /// Why there is no picture under the pane now (null: there is one, or it is still coming). The footer shows it.
    public string? NoPicture { get; private set; }
    public event Action? NoPictureChanged;

    public LauncherLook(DispatcherQueue q, Mantle.Core.Logging.ILogSink log, FrameworkElement panel, Border bdrop, Microsoft.UI.Xaml.Controls.Image shadow, FrameworkElement dim)
    {
        _q = q; _log = log; _panel = panel; _bdrop = bdrop; _shadow = shadow; _shadowEl = shadow; _dim = dim;
        _bdrop.Background = _bdropBrush;
        _bdrop.Opacity = 0;
    }

    static bool Reduced()
    {
        try { return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch (Exception) { return false; }
    }

    void SetNoPicture(string? why)
    {
        if (why == NoPicture) return;
        NoPicture = why;
        if (why != null) _log.Line("no see-through picture: " + why);
        NoPictureChanged?.Invoke();
    }

    // ------------------------------------------------------------------ the backdrop picture

    /// Before the window shows (it is hidden, so the capture never contains it). pane = the pane's PHYSICAL rectangle.
    public void Picture(Win32.RECT pane, Win32.RECT screen, double scale, bool dark)
    {
        int seq = ++_shotSeq;
        _bdrop.Opacity = 0;
        if (dark) { DarkPicture(pane, screen, scale, seq); return; }
        Backdrop.Shot? shot;
        try { shot = Backdrop.Grab(pane, screen, LightSigma * scale); }
        catch (Exception e) { shot = null; SetNoPicture("the screen behind could not be read (" + e.Message + ")"); return; }
        if (shot == null) { SetNoPicture("the screen behind could not be read"); return; }
        double sigma = LightSigma * scale;
        _ = Task.Run(() =>
        {
            byte[] px;
            try { px = Backdrop.Blurred(shot, pane, sigma, LightSaturate); }
            catch (Exception e) { _q.TryEnqueue(() => { if (seq == _shotSeq) SetNoPicture("the picture behind could not be blurred (" + e.Message + ")"); }); return; }
            _q.TryEnqueue(() => Put(seq, px, pane.Right - pane.Left, pane.Bottom - pane.Top));
        });
    }

    void Put(int seq, byte[] px, int w, int h)
    {
        if (seq != _shotSeq) return;
        try { _bdropBmp = Backdrop.ToBitmap(px, w, h, _bdropBmp); }
        catch (Exception e) { SetNoPicture("the picture could not be drawn (" + e.Message + ")"); return; }
        _bdropBrush.ImageSource = _bdropBmp;
        SetNoPicture(null);
        Fade(_bdrop, 0, 1, FadeInMs);                                        // .bdrop.on: 150 ms, once
    }

    // dark: the wallpaper under the pane, from one decoded still per wallpaper file and display size
    string? _stillKey;
    Backdrop.Shot? _still;
    Task? _stillTask;

    void DarkPicture(Win32.RECT pane, Win32.RECT screen, double scale, int seq)
    {
        string path = WallpaperPath();
        if (path.Length == 0 || !File.Exists(path)) { SetNoPicture(path.Length == 0 ? "Windows has no wallpaper picture (a solid colour or a slideshow between pictures)" : "the wallpaper file is missing: " + path); return; }
        string key;
        try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + screen.Left + "," + screen.Top + "," + screen.Right + "," + screen.Bottom; }
        catch (Exception e) { SetNoPicture("the wallpaper file could not be read (" + e.Message + ")"); return; }
        if (_stillKey != key || _still == null)
        {
            if (_stillKey != key) { _stillKey = key; _still = null; _stillTask = LoadStill(path, screen); }
            var t = _stillTask!;
            _ = t.ContinueWith(_ => _q.TryEnqueue(() => { if (seq == _shotSeq && _still != null) CutDark(pane, scale, seq); }), TaskScheduler.Default);
            return;
        }
        CutDark(pane, scale, seq);
    }

    void CutDark(Win32.RECT pane, double scale, int seq)
    {
        var still = _still!;
        double sigma = DarkSigma * scale;
        _ = Task.Run(() =>
        {
            byte[] px;
            try
            {
                px = Backdrop.Blurred(still, pane, sigma, 1);
                for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(px[i] * DarkDim); px[i + 1] = (byte)(px[i + 1] * DarkDim); px[i + 2] = (byte)(px[i + 2] * DarkDim); }
            }
            catch (Exception e) { _q.TryEnqueue(() => { if (seq == _shotSeq) SetNoPicture("the wallpaper could not be blurred (" + e.Message + ")"); }); return; }
            _q.TryEnqueue(() => Put(seq, px, pane.Right - pane.Left, pane.Bottom - pane.Top));
        });
    }

    /// The wallpaper decoded at the display's size, cut to cover it (Windows' default "Fill"; the other fit modes are
    /// drawn as Fill too: the log says so once).
    async Task LoadStill(string path, Win32.RECT screen)
    {
        int sw = screen.Right - screen.Left, sh = screen.Bottom - screen.Top;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            double k = Math.Max(sw / (double)dec.PixelWidth, sh / (double)dec.PixelHeight);
            uint tw = (uint)Math.Max(sw, Math.Ceiling(dec.PixelWidth * k)), th = (uint)Math.Max(sh, Math.Ceiling(dec.PixelHeight * k));
            var tf = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = tw, ScaledHeight = th, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
                Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = (uint)((tw - sw) / 2), Y = (uint)((th - sh) / 2), Width = (uint)sw, Height = (uint)sh },
            };
            var data = await dec.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tf,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();
            _still = new Backdrop.Shot { Px = px, X0 = screen.Left, Y0 = screen.Top, W = sw, H = sh };
            _log.Line($"the wallpaper still is ready ({dec.PixelWidth}x{dec.PixelHeight} cut to fill {sw}x{sh}; drawn as Windows' Fill whatever the fit setting)");
        }
        catch (Exception e)
        {
            _q.TryEnqueue(() => SetNoPicture("the wallpaper could not be decoded (" + e.Message + ")"));
        }
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

    public void ClearPicture() { ++_shotSeq; _bdrop.Opacity = 0; }

    // ------------------------------------------------------------------ the light drop shadow

    /// The shadow bitmap for this window size (physical px) and pane radius, made once off the UI thread. Dark: none.
    public void Shadow(bool dark, int winW, int winH, double scale, double inset, double radius)
    {
        _shadowEl.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;
        if (dark) return;
        string key = winW + "x" + winH + "@" + scale + "/" + radius;
        if (key == _shadowKey) return;
        _shadowKey = key;
        _ = Task.Run(() =>
        {
            byte[] px = ShadowPixels(winW, winH, scale, inset, radius);
            _q.TryEnqueue(() =>
            {
                if (key != _shadowKey) return;
                try { _shadow.Source = Backdrop.ToBitmap(px, winW, winH); }
                catch (Exception e) { _log.Line("the light drop shadow could not be drawn (" + e.Message + ")"); }
            });
        });
    }

    /// --lg-shadow (light): 0 1px 1px a.05, 0 10px 28px -12px a.22, 0 26px 56px -28px a.30, colour rgb(14,18,38). A CSS
    /// blur radius B is a Gaussian of sigma B/2; the layers stack (alpha over alpha); nothing under the pane itself.
    internal static byte[] ShadowPixels(int w, int h, double scale, double inset, double radius)
    {
        (double Y, double Blur, double Spread, double A)[] layers = [(1, 1, 0, .05), (10, 28, -12, .22), (26, 56, -28, .30)];
        var keep = new float[w * h];
        for (int i = 0; i < keep.Length; i++) keep[i] = 1;
        double px0 = inset * scale, py0 = inset * scale, pw = w - 2 * inset * scale, ph = h - 2 * inset * scale, pr = radius * scale;
        foreach (var l in layers)
        {
            var m = new float[w * h];
            double sp = l.Spread * scale;
            RoundRect(m, w, h, px0 - sp, py0 - sp + l.Y * scale, pw + 2 * sp, ph + 2 * sp, Math.Max(0, pr + sp));
            BoxBlur3(m, w, h, l.Blur / 2 * scale);
            for (int i = 0; i < m.Length; i++) keep[i] *= 1 - (float)(m[i] * l.A);
        }
        var under = new float[w * h];
        RoundRect(under, w, h, px0, py0, pw, ph, pr);
        var o = new byte[w * h * 4];
        for (int i = 0; i < keep.Length; i++)
        {
            double a = (1 - keep[i]) * (1 - under[i]);
            byte A = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
            o[i * 4] = (byte)Math.Round(38 * A / 255.0); o[i * 4 + 1] = (byte)Math.Round(18 * A / 255.0); o[i * 4 + 2] = (byte)Math.Round(14 * A / 255.0); o[i * 4 + 3] = A;
        }
        return o;
    }

    /// Coverage (0..1, anti-aliased over one pixel) of a rounded rectangle.
    static void RoundRect(float[] m, int w, int h, double x, double y, double rw, double rh, double r)
    {
        r = Math.Min(r, Math.Min(rw, rh) / 2);
        double cx = x + rw / 2, cy = y + rh / 2, hx = rw / 2 - r, hy = rh / 2 - r;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                double dx = Math.Max(Math.Abs(i + .5 - cx) - hx, 0), dy = Math.Max(Math.Abs(j + .5 - cy) - hy, 0);
                double d = Math.Sqrt(dx * dx + dy * dy) - r;                  // signed distance to the edge (inside < 0)
                m[j * w + i] = (float)Math.Clamp(.5 - d, 0, 1);
            }
    }

    /// Three box passes each way (a Gaussian of the given sigma, near enough), edges clamped.
    static void BoxBlur3(float[] m, int w, int h, double sigma)
    {
        if (sigma < 0.5) return;
        int r = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));   // 3 boxes of 2r+1: sigma^2 = (( 2r+1)^2-1)/4
        var tmp = new float[Math.Max(w, h)];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int j = 0; j < h; j++) Line(m, j * w, 1, w, r, tmp);
            for (int i = 0; i < w; i++) Line(m, i, w, h, r, tmp);
        }
    }

    static void Line(float[] m, int start, int step, int n, int r, float[] tmp)
    {
        for (int k = 0; k < n; k++) tmp[k] = m[start + k * step];
        double sum = 0; float inv = 1f / (2 * r + 1);
        for (int k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, n - 1)];
        for (int k = 0; k < n; k++)
        {
            m[start + k * step] = (float)(sum * inv);
            sum += tmp[Math.Min(k + r + 1, n - 1)] - tmp[Math.Max(k - r, 0)];
        }
    }

    // ------------------------------------------------------------------ the unfold and the fold

    /// The open: from the left half to the whole pane round a centre hinge (or the reduced-motion fade). Nothing moves inside.
    public void Unfold(double paneW, double inset)
    {
        CancelFold();
        var root = ElementCompositionPreview.GetElementVisual(_panel);
        var comp = root.Compositor;
        var shadowVis = ElementCompositionPreview.GetElementVisual(_shadowEl);
        var dimVis = ElementCompositionPreview.GetElementVisual(_dim);
        if (Reduced())
        {
            root.Clip = null; shadowVis.Clip = null; dimVis.Opacity = 0;
            Fade(_panel, 0, 1, ReducedMs, linear: true);
            Fade(_shadowEl, 0, 1, ReducedMs, linear: true);
            return;
        }
        root.Opacity = 1; shadowVis.Opacity = 1;
        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(.1f, 1));
        var dur = TimeSpan.FromMilliseconds(PopMs * Land);
        var clip = comp.CreateInsetClip(); root.Clip = clip;
        var sclip = comp.CreateInsetClip(); shadowVis.Clip = sclip;
        clip.StartAnimation("RightInset", Inset(comp, (float)(paneW / 2), 0, ease, dur));
        sclip.StartAnimation("RightInset", Inset(comp, (float)(paneW / 2 + inset), 0, ease, dur));
        var d = comp.CreateScalarKeyFrameAnimation();
        d.InsertKeyFrame(0, 1); d.InsertKeyFrame((float)Land, 1); d.InsertKeyFrame(1, 0);
        d.Duration = TimeSpan.FromMilliseconds(PopMs);
        dimVis.StartAnimation("Opacity", d);
    }

    /// The close: the unfold backwards; done() runs when it has landed (the window hides then). A new open cancels it.
    public void Fold(double paneW, double inset, Action done)
    {
        CancelFold();
        var root = ElementCompositionPreview.GetElementVisual(_panel);
        var comp = root.Compositor;
        var shadowVis = ElementCompositionPreview.GetElementVisual(_shadowEl);
        var dimVis = ElementCompositionPreview.GetElementVisual(_dim);
        _folding = true;
        var batch = comp.CreateScopedBatch(CompositionBatchTypes.Animation);
        _foldBatch = batch;
        if (Reduced())
        {
            root.Clip = null; shadowVis.Clip = null;
            var f = comp.CreateScalarKeyFrameAnimation(); f.InsertKeyFrame(0, 1); f.InsertKeyFrame(1, 0, comp.CreateLinearEasingFunction());
            f.Duration = TimeSpan.FromMilliseconds(ReducedMs);
            root.StartAnimation("Opacity", f); shadowVis.StartAnimation("Opacity", f);
        }
        else
        {
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(.9f, 0), new Vector2(.6f, 1));
            var dur = TimeSpan.FromMilliseconds(PopMs);
            var clip = comp.CreateInsetClip(); root.Clip = clip;
            var sclip = comp.CreateInsetClip(); shadowVis.Clip = sclip;
            clip.StartAnimation("RightInset", Inset(comp, 0, (float)(paneW / 2), ease, dur));
            sclip.StartAnimation("RightInset", Inset(comp, 0, (float)(paneW / 2 + inset), ease, dur));
            var d = comp.CreateScalarKeyFrameAnimation(); d.InsertKeyFrame(0, 0); d.InsertKeyFrame(1, 1); d.Duration = dur;
            dimVis.StartAnimation("Opacity", d);
            // the pane goes off over the last eighth, so the half that is left does not pop off (unfold-live.js)
            var off = comp.CreateScalarKeyFrameAnimation(); off.InsertKeyFrame(0, 1); off.InsertKeyFrame(.875f, 1); off.InsertKeyFrame(1, 0); off.Duration = dur;
            root.StartAnimation("Opacity", off); shadowVis.StartAnimation("Opacity", off);
        }
        batch.End();
        void Landed()
        {
            if (!ReferenceEquals(_foldBatch, batch)) return;               // an open came first, or it already landed
            _folding = false; _foldBatch = null;
            done();
        }
        batch.Completed += (_, _) => Landed();
        // unfold-live.js: it also resolves on a timer (duration + 150 ms) if no frame comes, so a close never hangs
        DispatcherQueueTimerLike.After(_q, (int)(PopMs + 150), Landed);
    }

    public bool Folding => _folding;

    void CancelFold()
    {
        _foldBatch = null; _folding = false;
        foreach (var el in new[] { _panel, _shadowEl })
        {
            var v = ElementCompositionPreview.GetElementVisual(el);
            v.StopAnimation("Opacity"); v.Opacity = 1;
        }
    }

    /// Back to rest (no clip, no dim) when hidden without a fold (a --test hide, a theme change).
    public void Rest()
    {
        CancelFold();
        foreach (var el in new[] { _panel, _shadowEl }) ElementCompositionPreview.GetElementVisual(el).Clip = null;
        var dv = ElementCompositionPreview.GetElementVisual(_dim); dv.StopAnimation("Opacity"); dv.Opacity = 0;
    }

    static ScalarKeyFrameAnimation Inset(Compositor c, float from, float to, CompositionEasingFunction ease, TimeSpan dur)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, from); a.InsertKeyFrame(1, to, ease);
        a.Duration = dur;
        return a;
    }

    static void Fade(UIElement el, float from, float to, double ms, bool linear = false)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, linear ? c.CreateLinearEasingFunction() : c.CreateCubicBezierEasingFunction(new Vector2(.2f, 0), new Vector2(0, 1)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
        // XAML's own Opacity is the resting value (a StartAnimation does not change it)
        if (el is FrameworkElement fe) fe.Opacity = 1;
    }
}
