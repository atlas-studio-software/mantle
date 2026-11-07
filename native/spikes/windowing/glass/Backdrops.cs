// Backdrops.cs - the two system backdrops the glass modes use.
//
// TransparentBackdrop (smoke/TransparentBackdrop.cs, the bar spike's approach): a fully see-through
// window background, so the window can be bigger than the pill (room for the light drop shadow above
// it and the 14 DIP lift below it) and show nothing but what XAML draws. The cpu mode uses it.
//
// HostBlurBackdrop (the gpu mode): the same hook, but the brush handed to the system compositor is
// an effect graph over a HOST BACKDROP brush (what DWM shows behind the window, live, every frame):
// Gaussian blur, then CSS saturate() as a colour matrix, masked to the pill's rounded rectangle so
// the rest of the window stays see-through. DWM does all of it on the GPU; this process only builds
// the graph once.
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Effects;

namespace Glass;

internal class TransparentBackdrop : SystemBackdrop
{
    internal static Windows.UI.Composition.Compositor? s_compositor;
    static IntPtr s_dqController;   // kept for the life of the process

    internal static Windows.UI.Composition.Compositor SystemCompositor()
    {
        EnsureSystemDispatcherQueue();
        return s_compositor ??= new Windows.UI.Composition.Compositor();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        target.SystemBackdrop = MakeBrush();
    }

    /// The brush the system compositor draws under the XAML content: fully clear here.
    protected virtual Windows.UI.Composition.CompositionBrush MakeBrush() =>
        SystemCompositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }

    // The brush alone left the window painted white (light) / black (dark) on the runner (spike run 2):
    // the HWND's redirection surface is still erased with the theme colour under the XAML content.
    // Extending the DWM frame over the whole client area and erasing with black (alpha 0 in the
    // frame) makes those pixels see-through, so only what XAML draws shows.
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
    static SubclassProc? s_subclass;   // kept alive: Windows calls it

    public static string MakeSeeThrough(IntPtr hwnd)
    {
        var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
        int hr = DwmExtendFrameIntoClientArea(hwnd, ref m);
        s_subclass ??= (h, msg, wp, lp, id, data) => {
            if (msg == 0x0014 /* WM_ERASEBKGND */) {
                if (GetClientRect(h, out var rc)) FillRect(wp, ref rc, GetStockObject(4 /* BLACK_BRUSH */));
                return new IntPtr(1);
            }
            return DefSubclassProc(h, msg, wp, lp);
        };
        bool sub = SetWindowSubclass(hwnd, s_subclass, new UIntPtr(1), IntPtr.Zero);
        return $"frame extended (hr 0x{hr:X8}), erase subclass {(sub ? "on" : "FAILED")}";
    }

    // Windows.UI.Composition needs a Windows.System dispatcher queue on this thread; WinUI 3 only
    // creates its own (Microsoft.UI.Dispatching) one.
    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }
    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    internal static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || s_dqController != IntPtr.Zero) return;
        var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
        CreateDispatcherQueueController(o, out s_dqController);
    }
}

/// The gpu mode's backdrop: blur(sigma) + saturate(sat) of the live host backdrop, inside the pill only.
/// Sizes are in the window's own pixels; the mask is stretched over whatever area the system draws.
internal sealed class HostBlurBackdrop : TransparentBackdrop
{
    readonly float _sigma, _sat, _winW, _winH, _px, _py, _pw, _ph, _r;
    public string State { get; private set; } = "not connected yet";

    public HostBlurBackdrop(float sigma, float sat, float winW, float winH, float px, float py, float pw, float ph, float r)
    { _sigma = sigma; _sat = sat; _winW = winW; _winH = winH; _px = px; _py = py; _pw = pw; _ph = ph; _r = r; }

    protected override Windows.UI.Composition.CompositionBrush MakeBrush()
    {
        try {
            var c = SystemCompositor();
            IGraphicsEffect fx = new GaussianBlurEffect {
                Name = "Blur", BlurAmount = _sigma, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Quality,
                Source = new Windows.UI.Composition.CompositionEffectSourceParameter("bd"),
            };
            if (Math.Abs(_sat - 1) > 0.001f) fx = new ColorMatrixEffect { Name = "Sat", Source = (IGraphicsEffectSource)fx, ColorMatrix = SaturateMatrix(_sat), ClampOutput = true };
            var eb = c.CreateEffectFactory(fx).CreateBrush();
            eb.SetSourceParameter("bd", c.CreateHostBackdropBrush());

            var geo = c.CreateRoundedRectangleGeometry();
            geo.Offset = new Vector2(_px, _py); geo.Size = new Vector2(_pw, _ph); geo.CornerRadius = new Vector2(_r, _r);
            var shape = c.CreateSpriteShape(geo);
            shape.FillBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            var sv = c.CreateShapeVisual(); sv.Size = new Vector2(_winW, _winH); sv.Shapes.Add(shape);
            var vs = c.CreateVisualSurface(); vs.SourceVisual = sv; vs.SourceSize = new Vector2(_winW, _winH);
            var sb = c.CreateSurfaceBrush(vs); sb.Stretch = Windows.UI.Composition.CompositionStretch.Fill;
            var mb = c.CreateMaskBrush(); mb.Source = eb; mb.Mask = sb;
            State = $"host backdrop brush: blur {_sigma} px, saturate {_sat}, masked to the pill ({_pw}x{_ph} r{_r} at {_px},{_py})";
            return mb;
        } catch (Exception e) {
            State = "FAILED: " + e.GetType().Name + ": " + e.Message;
            return base.MakeBrush();
        }
    }

    /// CSS saturate(s) (the Filter Effects spec matrix) as D2D's 5x4 colour matrix (row vector times matrix).
    static Matrix5x4 SaturateMatrix(float s) => new Matrix5x4 {
        M11 = 0.213f + 0.787f * s, M21 = 0.715f - 0.715f * s, M31 = 0.072f - 0.072f * s,
        M12 = 0.213f - 0.213f * s, M22 = 0.715f + 0.285f * s, M32 = 0.072f - 0.072f * s,
        M13 = 0.213f - 0.213f * s, M23 = 0.715f - 0.715f * s, M33 = 0.072f + 0.928f * s,
        M44 = 1f,
    };
}
