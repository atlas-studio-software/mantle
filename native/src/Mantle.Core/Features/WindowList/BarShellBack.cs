using System.Collections.Generic;

namespace Mantle.Core.Features.WindowList;

/// <summary>
/// What the bar does when Explorer comes back (BAR-158, 161, 163; main.js shellRestarted, sidecar/TrayWatch.cs and
/// sidecar/Taskbar.cs's re-register). Explorer broadcasts TaskbarCreated to every top-level window when it starts; the
/// bar hears it on its own window (no poll, nothing at rest) and, at most once per 3 s (every Mantle window hears the
/// same broadcast, and Explorer can send it twice):
/// <list type="bullet">
/// <item>dock mode: the app bar is asked for again NOW (Electron re-registered only on the next unrelated ask, so a
/// maximised window could sit under the bar until then);</item>
/// <item>while the bar is up and watching the Windows taskbar: the watch is re-made on the new taskbar's thread (the
/// old one died with Explorer);</item>
/// <item>while the bar is up: it is raised over the new taskbar.</item>
/// </list>
/// A hidden bar does nothing: every reveal finds the Windows taskbar afresh. Nothing here changes anything in Windows
/// but the bar's own window; a test run's dock ask goes to its passive helper, which reserves nothing.
/// </summary>
public sealed class BarShellBack
{
    public const int OnceMs = 3000;
    long _at;

    /// How many times Explorer came back this run (repeats inside 3 s not counted).
    public int Count { get; private set; }

    /// True when this TaskbarCreated is acted on; false for a repeat inside 3 s of the last one.
    public bool Take(long nowMs)
    {
        if (Count > 0 && nowMs - _at < OnceMs) return false;
        _at = nowMs;
        Count++;
        return true;
    }

    /// The steps, in order: "dock" (ask for the app bar again), "rewatch" (re-make the taskbar watch), "raise".
    public static IReadOnlyList<string> Steps(string mode, bool up, bool watching)
    {
        var s = new List<string>();
        if (mode == "dock") s.Add("dock");
        if (up && watching) s.Add("rewatch");
        if (up) s.Add("raise");
        return s;
    }

    /// The log line (main.js shellRestarted's words, the bar's own part).
    public static string Line(string how, int count, string mode, bool up, bool watching, bool test)
    {
        var parts = new List<string>();
        foreach (var step in Steps(mode, up, watching))
            parts.Add(step switch {
                "dock" => test ? "asking for the dock slot again (a test run's helper reserves nothing)" : "asking Explorer for the dock slot again",
                "rewatch" => "watching the new Windows taskbar",
                _ => "raising the bar over the new taskbar",
            });
        string what = parts.Count > 0 ? string.Join(", ", parts) : "the bar is hidden, so nothing to do now: its next reveal finds the new Windows taskbar";
        return $"bar: Explorer is back ({how}, {count} this run): {what}";
    }
}
