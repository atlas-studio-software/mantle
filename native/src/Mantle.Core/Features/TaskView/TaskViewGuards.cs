using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Mantle.Core.Features.Switcher;

namespace Mantle.Core.Features.TaskView;

/// <summary>A desktop write checked and ready for the sidecar: <c>Verb</c> is the sidecar command (desktops.*).</summary>
public sealed record DeskRequest(string Verb, bool Dry, string? Desktop = null, string? Hwnd = null, string? Name = null);

/// <summary>
/// The main-process guards of lib/features/taskview.js: the page may only name windows and desktops of THIS
/// session, in well-formed ids; a call from an earlier open is ignored; the sidecar client's own failures are
/// said in plain words. Everything is refused BEFORE the sidecar is asked. Also the feature's fixed facts and
/// timings and where the window parks. PURE.
/// </summary>
public static class TaskViewGuards
{
    // lib/features/taskview.js module.exports: the feature row (lib/features.js reads these).
    public const string FeatureName = "taskview";
    public const string FeatureLabel = "Task View (Win+Tab)";
    public const bool DefaultOn = true;
    public const bool Rests = true;
    public static readonly IReadOnlyList<string> Chords = new[] { "Win+Tab" };
    public const string WindowTitle = "Mantle task view";

    public const int ParkGap = 64, FirstBatch = 8, RevealLateMs = 450, KbWaitMs = 350, FoldCapMs = 400, BlurGraceMs = 120;
    public const int ListWaitMs = 25, WarmDelayMs = 4000, LingerMs = 3 * 60 * 1000, ColdCapMs = 2500, RetryMs = 60 * 1000;
    public const int DeskTimeoutMs = 4000, NameMax = 100;

    public const string NoWindow = "that window is no longer in Task View";
    public const string NoDesktop = "that desktop no longer exists";
    public const string UnknownAction = "Task View does not know that desktop action";
    public const string NameNotText = "a desktop name has to be text";
    public const string NameTooLong = "a desktop name can be at most 100 characters";
    public const string HelperRestarting = "Mantle's window helper is restarting; try again in a moment";

    static readonly Regex HwndRe = new("^[1-9][0-9]{0,19}$", RegexOptions.CultureInvariant);
    static readonly Regex DeskRe = new("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant);
    static readonly Regex ClientFailure = new("sidecar|cannot run \"|did not answer \"|ready line", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The sidecar CLIENT's own failures (not running, no answer) in words for the screen; its answers are already plain.</summary>
    public static string Plain(string? msg)
    {
        string m = msg ?? "";
        if (ClientFailure.IsMatch(m)) return HelperRestarting;
        return m.Length > 0 ? m : "no reason was given";
    }

    /// <summary>The page call belongs to the open session (same seq); else it is a stale call, ignored harmlessly.</summary>
    public static bool IsCurrent(long? callSeq, long? sessionSeq) => callSeq is long c && sessionSeq is long s && c == s;

    /// <summary>A window id the page sent: digits, no leading zero, and one this session listed. Null = fine, else the words.</summary>
    public static string? CheckHwnd(string? hwnd, IReadOnlySet<string>? listed) =>
        hwnd is not null && HwndRe.IsMatch(hwnd) && listed is not null && listed.Contains(hwnd) ? null : NoWindow;

    /// <summary>A desktop id the page sent: a GUID this session listed. Null = fine, else the words.</summary>
    public static string? CheckDesk(string? desk, IReadOnlySet<string>? listed) =>
        desk is not null && DeskRe.IsMatch(desk) && listed is not null && listed.Contains(desk) ? null : NoDesktop;

    static string? Verb(string? op) => op switch
    {
        "switch" => "desktops.switch",
        "create" => "desktops.create",
        "remove" => "desktops.remove",
        "rename" => "desktops.rename",
        "move" => "desktops.move",
        _ => null,
    };

    /// <summary>
    /// The page's <c>desk</c> call, in taskview.js's order of checks: the action, the desktop (not for create),
    /// the window (move), the name (rename: text, at most 100). Every write is a dry run in a test run.
    /// </summary>
    public static (DeskRequest? Request, string? Error) Desk(string? op, string? desktop, string? hwnd, object? name,
        IReadOnlySet<string>? hwnds, IReadOnlySet<string>? desks, bool test)
    {
        string? verb = Verb(op);
        if (verb is null) return (null, UnknownAction);
        string? d = null, h = null, n = null;
        if (op != "create")
        {
            string? e = CheckDesk(desktop, desks);
            if (e is not null) return (null, e);
            d = desktop;
        }
        if (op == "move")
        {
            string? e = CheckHwnd(hwnd, hwnds);
            if (e is not null) return (null, e);
            h = hwnd;
        }
        if (op == "rename")
        {
            if (name is not string s) return (null, NameNotText);
            if (s.Length > NameMax) return (null, NameTooLong);
            n = s;
        }
        return (new DeskRequest(verb, test, d, h, n), null);
    }

    /// <summary>
    /// Where the parked window waits: its monitor's own size, just above it, else below, left, right, avoiding
    /// every display; boxed in, -32000,-32000 (taskview.js parkRect: the switcher's rule at the monitor's size).
    /// </summary>
    public static PhysRect ParkRect(PhysRect bounds, IReadOnlyList<PhysRect> displays)
    {
        var p = SwitcherWords.ParkSpot(bounds, bounds.W, bounds.H, displays);
        return new PhysRect(p.X, p.Y, bounds.W, bounds.H);
    }
}
