using System;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Welcome;

/// <summary>Where the welcome window goes: centred on the primary display's work area, clamped to it.</summary>
public readonly record struct WelcomeRect(int X, int Y, int Width, int Height);

/// <summary>The window's fixed options (lib/welcome.js open): clear, frameless, no resize/min/max/fullscreen, never
/// navigates or opens windows.</summary>
public sealed record WelcomeWindowSpec(WelcomeRect Bounds, string Title, bool Frameless = true, bool Clear = true, bool Resizable = false,
    bool Minimizable = false, bool Maximizable = false, bool Fullscreenable = false, bool MayNavigate = false, bool MayOpenWindows = false);

/// <summary>An open request's answer: opened (with the window spec), already (the same window restored/shown/focused),
/// or not ok with a reason.</summary>
public readonly record struct WelcomeOpen(bool Ok, bool Opened, bool Already, string? Reason, WelcomeWindowSpec? Spec = null);

/// <summary>welcome.state's answer.</summary>
public sealed record WelcomeStateAnswer(bool Ok, string Via, bool Test, bool Packaged, bool FirstRun, string Reason);

/// <summary>welcome.done's answer and whether the host closes the window now (next tick). A failed record keeps it.</summary>
public readonly record struct WelcomeDoneAnswer(WelcomeDone Result, bool CloseWindow);

/// <summary>
/// lib/welcome.js host() without Electron: start, open / already, ready-to-show and the focus hand-off, renderer crash and
/// load failure, closed (by hand = skipped; quitting = pending), and the welcome.state / welcome.done / welcome.show
/// commands (WEL-011, 014..019, 024, 026). The window itself is the App's; this decides what happens to it. Every flag
/// write goes through the <see cref="WelcomeRecorder"/> (and so the data layer's gate). Nothing here touches the shell,
/// default apps, file associations or explorer.exe.
/// </summary>
public sealed class WelcomeHost
{
    /// The focus hand-off waits this long after ready-to-show (lib/welcome.js).
    public const int FocusCheckMs = 200;
    /// Settings > About's row (ui/settings/settings.js about.welcome) and its note when the window is already open.
    public const string SettingsRowId = "about.welcome", SettingsLabel = "Welcome tour", SettingsButton = "Show again", SettingsVia = "Settings";
    public const string SettingsHelp = "The three cards from Mantle’s first start: what changed, the keys, and how to switch back to Windows’ taskbar";
    public const string AlreadyOpenNote = "The welcome is already open";
    public const string ArgError ="--first-run only works with --test (the live shell decides from its own profile whether this is its first start)";

    readonly WelcomeRecorder _rec;
    readonly Action<string> _log;
    readonly bool _packaged;
    bool _started, _answered, _open;
    string? _via;

    public WelcomeHost(WelcomeRecorder recorder, bool packaged = false, Action<string>? log = null)
    {
        _rec = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _packaged = packaged;
        _log = log ?? (_ => { });
    }

    public bool IsOpen => _open;
    public string? Via => _via;

    /// <summary>lib/args.js: <c>--first-run</c> outside <c>--test</c> is an argument error (a flag on the logon task cannot
    /// put the welcome in front of a used profile). Null = fine.</summary>
    public static string? CheckArgs(bool test, bool firstRun) => firstRun && !test ? ArgError : null;

    /// <summary>record(): only once this process holds the single-instance lock, so --quit / --open message processes
    /// never write. Without the lock it writes nothing and says so.</summary>
    public WelcomeWrite Record(bool holdsLock)
    {
        if (!holdsLock) return new(true, "this process does not hold the single-instance lock, so it records nothing", false);
        if (!_rec.MayWrite) return new(true, "a test run without --first-run: nothing is recorded", false);
        return _rec.Record();
    }

    /// <summary>start(): logs the verdict once; if due, the delay (ms) after the bar's page has loaded before Open is
    /// called. Null = nothing to show (or started already).</summary>
    public int? Start()
    {
        if (_started) return null;
        _started = true;
        var v = _rec.Verdict;
        _log(v.Show ? "due: " + v.Reason : "not shown: " + v.Reason);
        return v.Show ? FirstRun.ShowDelayMs : null;
    }

    /// <summary>Centred on the work area, clamped to it (JS Math.round).</summary>
    public static WelcomeRect Place(WelcomeRect workArea)
    {
        int w = Math.Min(FirstRun.WinW, workArea.Width), h = Math.Min(FirstRun.WinH, workArea.Height);
        return new(JsRound(workArea.X + (workArea.Width - w) / 2.0), JsRound(workArea.Y + (workArea.Height - h) / 2.0), w, h);
    }

    static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>open(why): already open = the same window restored, shown and focused; else a new one (shown on ready).</summary>
    public WelcomeOpen Open(string why, WelcomeRect? workArea = null)
    {
        if (_open) return new(true, false, true, null);
        var spec = new WelcomeWindowSpec(Place(workArea ?? new WelcomeRect(0, 0, 1280, 720)), FirstRun.Title);
        _open = true; _answered = false; _via = why;
        return new(true, true, false, null, spec);
    }

    /// <summary>The App could not make the window: one log line, the welcome stays as it was.</summary>
    public string WindowFailed(string words)
    {
        _open = false; _answered = true;
        var r = "the welcome window could not be made (" + words + ")";
        _log(r);
        return r;
    }

    /// <summary>ready-to-show: shown and focused; returns true if the App should check focus after
    /// <see cref="FocusCheckMs"/> (never in a test: nothing takes focus there).</summary>
    public bool ReadyToShow()
    {
        if (!_open) return false;
        _log("shown (" + _via + ")");
        return !_rec.Test;
    }

    /// <summary>200 ms later: ask the sidecar to hand it the keyboard only if it is still open and not focused.</summary>
    public bool NeedsFocusHelp(bool isFocused) => _open && !_rec.Test && !isFocused;

    /// <summary>The sidecar's answer; a failure is logged, never silent.</summary>
    public void FocusHelpResult(bool ok, string? why)
    {
        if (!ok) _log("the welcome could not take the keyboard (" + (string.IsNullOrEmpty(why) ? "no reason came back" : why) + "); a click in it gives it the keyboard");
    }

    /// <summary>The renderer died: window closed, the welcome stays pending, one log line.</summary>
    public void RendererGone(string? reason)
    {
        if (!_open) return;
        _log("the welcome page's renderer died (" + reason + "); the window is closed and the welcome stays pending");
        _answered = true; _open = false;
    }

    /// <summary>The page did not load (main frame only): window closed, the welcome stays pending.</summary>
    public bool LoadFailed(string? description, bool isMainFrame = true)
    {
        if (!isMainFrame || !_open) return false;
        _log("the welcome page did not load (" + description + "); the welcome stays pending");
        _answered = true; _open = false;
        return true;
    }

    /// <summary>The window closed. Answered (or crashed) = nothing more; quitting = stays as it was; by hand = skipped.</summary>
    public WelcomeDone Closed(bool quitting)
    {
        bool wasAnswered = _answered;
        _open = false;
        if (wasAnswered) return new(true, false, "already answered");
        return _rec.Closed(quitting);
    }

    /// <summary>welcome.state -> {ok, via, test, packaged, firstRun, reason}.</summary>
    public WelcomeStateAnswer State() => new(true, _via ?? "first start", _rec.Test, _packaged, _rec.Verdict.OptIn, _rec.Verdict.Reason);

    /// <summary>welcome.done {how}: records; a failed record keeps the window open and returns the reason.</summary>
    public WelcomeDoneAnswer Done(string? how)
    {
        if (how != "finished" && how != "skipped")
            return new(new WelcomeDone(false, false, "welcome.done takes {how: \"finished\" | \"skipped\"}"), false);
        var r = _rec.Done(how);
        _log(how + " (" + (_via ?? "first start") + ")" + (r.Recorded ? "" : ": " + r.Reason));
        if (!r.Ok) return new(r, false);
        _answered = true;
        return new(r, _open);
    }

    /// <summary>welcome.show {via}: Settings' "Show again"; works on any profile; via is cleaned to 30 word characters.</summary>
    public WelcomeOpen Show(string? via, WelcomeRect? workArea = null) => Open("asked from " + CleanVia(via), workArea);

    public static string CleanVia(string? via)
    {
        if (via == null) return "a page";
        var s = Regex.Replace(via, @"[^A-Za-z0-9_ .-]", "");   // JS \w is ASCII only
        if (s.Length > 30) s = s.Substring(0, 30);
        return s.Length == 0 ? "a page" : s;
    }
}
