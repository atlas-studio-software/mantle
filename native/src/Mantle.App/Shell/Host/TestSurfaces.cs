using System;
using System.Text.Json;
using Mantle.App.Native;
using Mantle.Core.Lifecycle;
using Mantle.Core.Look;
using Mantle.Core.State;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mantle.App.Shell.Hosting;

// Test stand-ins for the surface host's UI tests (native/tests/Mantle.UiTests/Host). Their names start with
// "test-", so SurfaceSet makes them ONLY in a --test run with MANTLE_TEST_SURFACES=1; a live or scratch run lists
// them as skipped and never makes a window. Each is one small window that uses every part of its context: the log
// prefix, State and OnState, the "theme" event, and a command call answered on its own UI thread.

/// <summary>Made at start, hidden and parked (the launcher's pattern).</summary>
[Surface("test-parked", AtStart = true)]
internal sealed class TestParkedSurface : TestStandIn { }

/// <summary>Made on first use (a show, a toggle, or --open from a second start).</summary>
[Surface("test-lazy")]
internal sealed class TestLazySurface : TestStandIn { }

/// <summary>The stand-in window. Title <c>Mantle surface: &lt;name&gt;</c>; its text has AutomationId <c>surface.&lt;name&gt;</c>.</summary>
internal abstract class TestStandIn : ISurface
{
    ISurfaceContext? _ctx;
    Window? _win;
    Grid? _root;
    TextBlock? _text;
    IDisposable? _state, _theme;
    string _themeNow = "not told yet";
    int _shows;

    public bool Shown => _win?.AppWindow is { } a && a.IsVisible;

    public void Create(ISurfaceContext ctx)
    {
        if (!ctx.Test) throw new InvalidOperationException("a test stand-in surface is made only in a --test run");
        _ctx = ctx;
        _text = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_text, "surface." + ctx.Name);
        _root = new Grid();
        _root.Children.Add(_text);
        _win = new Window { Title = "Mantle surface: " + ctx.Name, Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;   // out of the app lists, quiet or not
        IntPtr h = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        if (Mantle.App.Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;   // TestQuiet: alpha 0, click-through
        Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Mantle.App.Host.Quiet) Win32.SetLayeredWindowAttributes(h, 0, 0, Win32.LWA_ALPHA);
        _win.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(40, 40, 360, 120));

        _state = ctx.OnState(s => Render(s, "a new state"));
        _theme = ctx.On<ThemePayload>("theme", t => {
            _themeNow = t.Theme;
            if (_root != null) _root.RequestedTheme = t.Theme == "dark" ? ElementTheme.Dark : ElementTheme.Light;
            ctx.Log.Line($"theme {t.Theme} reached it on its UI thread: {ctx.Dispatcher.HasThreadAccess}");
            Render(ctx.State, "the theme");
        });
        Render(ctx.State, "made");
        ctx.Log.Line($"made hidden (test stand-in); quiet {Mantle.App.Host.Quiet}; state mode {ctx.State.Mode.ToString().ToLowerInvariant()}");
    }

    void Render(ShellState s, string why)
    {
        if (_text == null || _ctx == null) return;
        _text.Text = $"{_ctx.Name}: sidecar {s.Sidecar.State.ToString().ToLowerInvariant()}; theme {_themeNow}; shows {_shows} ({why})";
    }

    public void Show(string why)
    {
        if (_win == null || _ctx == null) return;
        _shows++;
        _win.AppWindow.Show(false);   // never activated: a stand-in never takes the keyboard
        Render(_ctx.State, "shown");
        _ = AskHostAsync(why);
    }

    /// A command from the UI thread: posted to the host, answered back on this thread (the context's promise).
    async System.Threading.Tasks.Task AskHostAsync(string why)
    {
        var ctx = _ctx!;
        var r = await ctx.CallAsync("state.get");
        string mode = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("mode", out var m) ? m.GetString() ?? "?" : "?";
        ctx.Log.Line($"state.get answered {(r.Ok ? "ok" : "NOT ok: " + r.Error)} (mode {mode}) on its UI thread: {ctx.Dispatcher.HasThreadAccess}; shown for {why}");
    }

    public void Hide(string why)
    {
        _win?.AppWindow.Hide();
        if (_ctx != null) Render(_ctx.State, "hidden");
    }

    public void Destroy(string why)
    {
        _state?.Dispose(); _theme?.Dispose();
        _state = _theme = null;
        try { _win?.Close(); } catch (Exception e) { _ctx?.Log.Line("its window did not close: " + e.Message); }
        _ctx?.Log.Line($"destroyed ({why})");
        _win = null; _root = null; _text = null;
    }
}
