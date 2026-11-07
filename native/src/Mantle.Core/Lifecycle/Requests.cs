using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Lifecycle;

/// <summary>
/// Windows sign-out / restart / shut down (CORE-028; PLAN.md 3.6): WM_QUERYENDSESSION is answered yes with
/// one log line however often it is asked, and pages are not reloaded for the next 60 s; WM_ENDSESSION
/// (wParam TRUE) runs the ONE shutdown road with "Windows is ending the session (&lt;reasons&gt;)".
/// The window that hears the messages lives in the app; this is the decision.
/// </summary>
public sealed class SessionEnd
{
    public const uint ENDSESSION_CLOSEAPP = 0x1, ENDSESSION_CRITICAL = 0x40000000, ENDSESSION_LOGOFF = 0x80000000;
    public static readonly TimeSpan NoReloads = TimeSpan.FromSeconds(60);

    readonly Action<string> _log;
    readonly Action<string> _shutdown;
    readonly Func<double> _clockMs;
    bool _asked;
    double _askedAt = double.NegativeInfinity;

    public SessionEnd(Action<string> log, Action<string> shutdown, Func<double> clockMs) { _log = log; _shutdown = shutdown; _clockMs = clockMs; }

    /// Electron's reasons array: 'close-app', 'critical', 'logoff' (empty = shut down or restart).
    public static IReadOnlyList<string> Reasons(ulong lParam)
    {
        var r = new List<string>();
        if ((lParam & ENDSESSION_CLOSEAPP) != 0) r.Add("close-app");
        if ((lParam & ENDSESSION_CRITICAL) != 0) r.Add("critical");
        if ((lParam & ENDSESSION_LOGOFF) != 0) r.Add("logoff");
        return r;
    }

    static string Words(IReadOnlyList<string> r) => r.Count == 0 ? "shutdown or restart" : string.Join(", ", r);

    /// <summary>WM_QUERYENDSESSION: always yes (TRUE); logged once.</summary>
    public bool Query(ulong lParam)
    {
        _askedAt = _clockMs();
        if (!_asked) { _asked = true; _log($"mantle: Windows asked whether the session may end ({Words(Reasons(lParam))}); yes. No page reloads for the next 60 s."); }
        return true;
    }

    /// <summary>True for 60 s after the question: a page that dies then is not reloaded (Windows is ending).</summary>
    public bool HoldReloads => _clockMs() - _askedAt < NoReloads.TotalMilliseconds;

    /// <summary>WM_ENDSESSION: TRUE = the session ends now; FALSE = another app cancelled it.</summary>
    public void End(bool ending, ulong lParam)
    {
        if (!ending) { _asked = false; _askedAt = double.NegativeInfinity; _log("mantle: the session end was cancelled by another app; carrying on"); return; }
        _shutdown($"Windows is ending the session ({Words(Reasons(lParam))})");
    }
}

/// <summary>What a second launch asks of the running instance (main.js app.on('second-instance')).</summary>
public enum SecondAsk { Plain, Quit, Open, Click, Memory, Report, Tray, Crash, Unload, Chord, Key, Surface, Theme }

/// <summary>
/// The second-launch router (CORE-007/009/010): <c>--quit</c> and <c>--open=</c> always; the test probes
/// (<c>--test-click=</c>, <c>--test-memory=</c>, <c>--test-report</c>, <c>--test-tray</c>, <c>--test-crash</c>,
/// <c>--test-unload</c>, <c>--test-chord=</c>, <c>--test-key=</c>) only when THIS instance runs <c>--test</c>,
/// in main.js's order. A live instance treats a probe as a plain second start.
/// </summary>
public static class SecondLaunch
{
    public static (SecondAsk Ask, string? Value) Route(IReadOnlyList<string> argv, bool test)
    {
        string? With(string prefix) => argv.FirstOrDefault(a => a != null && a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
        bool Has(string flag) => argv.Any(a => a == flag);
        if (Has("--quit")) return (SecondAsk.Quit, null);
        if (With("--open=") is { } open) return (SecondAsk.Open, open);
        if (!test) return (SecondAsk.Plain, null);
        if (With("--test-click=") is { } click) return (SecondAsk.Click, click);
        if (With("--test-memory=") is { } mem) return (SecondAsk.Memory, mem.Length > 0 ? mem : "asked");
        if (Has("--test-report")) return (SecondAsk.Report, null);
        if (Has("--test-tray")) return (SecondAsk.Tray, null);
        if (Has("--test-crash")) return (SecondAsk.Crash, null);
        if (Has("--test-unload")) return (SecondAsk.Unload, null);
        if (With("--test-surface=") is { } surface) return (SecondAsk.Surface, surface);
        if (With("--test-theme=") is { } theme) return (SecondAsk.Theme, theme);   // theme.set {mode} as a page calls it   // <show|hide|toggle>:<name>, through surface.<verb>
        foreach (var a in argv) {
            if (a == null) continue;
            if (a.StartsWith("--test-chord=", StringComparison.Ordinal)) return (SecondAsk.Chord, a.Substring(13));
            if (a.StartsWith("--test-key=", StringComparison.Ordinal)) return (SecondAsk.Key, a.Substring(11));
        }
        return (SecondAsk.Plain, null);
    }
}
