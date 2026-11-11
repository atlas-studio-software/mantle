using System;
using System.Linq;

namespace Mantle.Core.Features.TopBar;

// The pure rules of lib/features/topbar.js: the bar's size, the app bar's size in the sidecar's units, when a missing
// strip is a failure, the lock screen, and why the bar steps aside (with its words for the strip and for the log,
// which never names an app, game or window: NOTHING PRIVATE IN THE LOG). TopBarFeature holds the state and calls these.

/// <summary>A reserve on its way (asked, not landed): healthy until its grace runs out.</summary>
public sealed record ReservePending(long Since, string Why, int Grace);
/// <summary>The strip as Mantle knows it: reserved or not, why, and a reserve on its way.</summary>
public sealed record ReserveState(bool Reserved, string Reason, ReservePending? Pending = null, int? Size = null);

public static class TopBarRules
{
    public const int BarDip = 28;                 // TOP-002 / TOP-215: --tb-h
    public const string HideChord = "Win+Alt+T";  // TOP-203
    public const string Title = "Mantle top bar"; // TOP-001 / TOP-216: sidecar/MenuBar.cs checks it before removing a stale app bar
    public const int ReserveGraceMs = 6000;       // TOP-245
    public const int ReserveStartGraceMs = 20000;
    public const int WorkCheckMs = 300;           // TOP-243
    public const int ReRegisterMs = 30000;        // TOP-249
    public const string ReRegisterWhy = "registered afresh";

    /// TOP-241: the app bar's size in the SIDECAR's units: physical px when it is DPI-aware, else 28.
    public static int ReserveSize(double scale, bool dpiAware) => dpiAware ? (int)TopBarModel.JsRound(BarDip * (scale > 0 ? scale : 1)) : BarDip;

    /// TOP-245/246 (stripGap): why the missing strip is a failure right now, or null.
    public static string? StripGap(bool live, ReserveState r, bool run, bool hidden, long now)
    {
        if (!live || r.Reserved || run || hidden) return null;
        var p = r.Pending;
        if (p != null && now - p.Since < (p.Grace > 0 ? p.Grace : ReserveGraceMs)) return null;
        if (p != null) return $"the strip was asked for {Math.Round((now - p.Since) / 1000.0, MidpointRounding.AwayFromZero)} s ago ({p.Why}) and has not landed: {r.Reason}";
        return string.IsNullOrEmpty(r.Reason) ? "the strip is not reserved" : r.Reason;
    }

    /// TOP-243 (workAreaCheck): the work area's top must be the bar's bottom, 1 DIP of rounding allowed.
    public static (bool Ok, double Want, double Got) WorkAreaCheck(double boundsTopDip, double workTopDip)
    {
        double want = boundsTopDip + BarDip;
        return (Math.Abs(workTopDip - want) <= 1, want, workTopDip);
    }

    /// TOP-249 (reRegister): a failed check re-registers, once per 30 s, never for the fresh registration itself.
    public static bool ReRegister(bool ok, bool fresh, long? last, long now) => !ok && !fresh && (last == null || now - last.Value > ReRegisterMs);

    /// TOP-266 (lockScreen): "LockApp.exe" / "LogonUI.exe" when the focus snapshot is one of them (by exe or description).
    public static string? LockScreen(string? exe, string? desc, bool self)
    {
        if (self) return null;
        foreach (var s in new[] { exe, desc })
        {
            var file = (s ?? "").Split('\\', '/').Last().ToLowerInvariant();
            if (file == "lockapp.exe") return "LockApp.exe";
            if (file == "logonui.exe") return "LogonUI.exe";
        }
        return null;
    }

    /// TOP-260 (asideKind): hidden by hand > lock screen > known game running > full-screen video > the sidecar's game
    /// verdict (unless overGames) > any other full-screen window in front; null = the bar shows.
    public static string? AsideKind(bool hidden, string? lockExe, bool run, bool video, bool gameVerdict, bool overGames, bool full)
    {
        if (hidden) return "hidden";
        if (lockExe != null) return "lock";
        if (run) return "run";
        if (video) return "video";
        if (gameVerdict && !overGames) return "game";
        if (full && !gameVerdict) return "full";
        return null;
    }

    /// The words the strip / quick settings show for a kind (decide()'s signal).
    public static string? Signal(string? kind, string? lockExe = null, string? runNames = null, bool overGames = false, string? app = null, string? gameSignal = null) => kind switch
    {
        null => null,
        "hidden" => $"hidden by hand: {HideChord} or \"Show top bar\" in quick settings brings it back",
        "lock" => $"{lockExe} is in front (the {(lockExe == "LockApp.exe" ? "lock" : "sign-in")} screen): the bar never draws over it",
        "run" => $"{runNames ?? "the game"} is running (a known game): the bar stays aside with its strip let go until it exits"
                 + (overGames ? " (\"keep the top bar over full-screen games\" does not apply to a known game)" : ""),
        "video" => $"{app} is playing video full screen; the top bar stepped aside until it leaves full screen",
        "full" => $"{app} is full screen; the top bar stepped aside until it leaves full screen",
        _ => gameSignal ?? "the sidecar said so",
    };

    /// The same reason for the log, with no app, game or window named.
    public static string? LogSignal(string? kind, string? signal, string gameWord = "a known game", string videoHow = "exe") => kind switch
    {
        null => null,
        "run" => $"{gameWord} running: the bar stays aside with its strip let go until it exits",
        "video" => $"a full-screen window is playing video (matched by {videoHow}); the top bar stepped aside until it leaves full screen",
        "game" => "a full-screen game has the screen (the sidecar's verdict)",
        "full" => "a full-screen window has the screen; the top bar stepped aside until it leaves full screen",
        _ => signal,
    };

    /// TOP-262: "back (<from>): ..." for the kind it was aside for.
    public static string BackWords(string? wasKind) => wasKind switch
    {
        "lock" => "the lock screen closed",
        "run" => "the game exited",
        "hidden" => "shown by hand",
        "video" => "the video left full screen",
        "full" => "the full-screen window left full screen",
        _ => "the game left the foreground or full screen",
    };

    /// TOP-261: why the stats stop when it steps aside.
    public static string StatsOffWords(string kind) => kind switch
    {
        "lock" => "the screen is locked",
        "hidden" => "the top bar is hidden by hand",
        "video" => "a video has the screen",
        "full" => "a full-screen window has the screen",
        _ => "a game has the screen",
    };
}
