using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mantle.Core;

/// <summary>What this start is for, decided from the flags alone (lib/args.js + main.js's flag blocks).</summary>
public enum RunKind
{
    /// --live: the only kind allowed to touch the Windows shell.
    Live,
    /// --test --profile-name=&lt;x&gt;: its own data folder and claim, a passive platform, quiet windows.
    Test,
    /// --user-data=&lt;dir&gt; without --test or --live (native only: the bench and fixtures). Changes
    /// nothing in Windows, exactly like a test run, but its windows are drawn normally.
    Scratch,
    /// --quit: a message to the running instance, never an instance itself.
    Quit,
    /// --open=&lt;target&gt;: a message to the running instance, never an instance itself.
    Open,
    /// Neither --live nor --test: refused with the live-mode line and exit code 2 (CORE-001).
    Refused,
}

/// <summary>
/// The command line of MantleNative.exe. The name rule is Electron Mantle's (lib/args.js): an
/// argument is "--name" or "--name=value"; anything that does not start with "--" (the exe path
/// in GetCommandLineArgs) is ignored, and a value may be wrapped in double quotes. The checks and
/// their sentences are lib/args.js parseArgs's, in its order where one start breaks several rules.
///
/// Electron's flags: --live, --test, --quit, --profile-name=, --screenshot=, --flyout=, --bar-edge=,
/// --open=, --play=, --supervise, --no-supervise, --first-run, --no-desktop, --no-acrylic, --theme=,
/// and the ones main.js reads by itself (--dock, --pill, --flush, --no-edgeclip, --no-tbhide,
/// --os-morph, --media-guard, --media-controls, --log=) plus the --test-only switches (CORE-007).
///
/// Native's own: --bench-out=&lt;dir&gt; (the bench's ready.json/cells.json/native-log.txt),
/// --no-backdrop (no captured backdrop), --user-data=&lt;dir&gt; (a scratch data root; see
/// <see cref="RunKind.Scratch"/>).
/// </summary>
public sealed class LaunchArgs
{
    static readonly Regex ProfileRule = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    static readonly string[] Themes = { "light", "dark", "system" };

    /// Flags main.js reads outside parseArgs: on/off switches, no value.
    static readonly HashSet<string> Switches = new(StringComparer.Ordinal) {
        "dock", "pill", "flush", "no-edgeclip", "no-tbhide", "os-morph", "media-guard", "media-controls",
    };
    /// --test-only switches and probes (CORE-007): ignored, with a log line, without --test.
    static readonly HashSet<string> TestOnly = new(StringComparer.Ordinal) {
        "test-tbhide", "test-live-bar", "test-trace", "test-desktop-hold", "test-apps-icons", "test-click",
        "test-memory", "test-report", "test-tray", "test-crash", "test-unload", "test-chord", "test-key",
        "test-popwatch", "test-popwatch-away",   // lib/popwatch.js: a --test run watches nothing real unless asked
    };

    public string? Theme { get; private set; }
    public string? BenchOut { get; private set; }
    public bool NoBackdrop { get; private set; }
    public bool Test { get; private set; }
    public string? ProfileName { get; private set; }
    public string? UserData { get; private set; }
    public bool Quit { get; private set; }
    public bool Live { get; private set; }
    /// --open's raw value ("" for a bare --open), null when absent.
    public string? Open { get; private set; }
    /// The parsed --open request (with --play files), null when there is no --open.
    public OpenRequest? OpenRequest { get; private set; }
    public string? Screenshot { get; private set; }
    public string? Flyout { get; private set; }
    /// "bottom" (default) or "top".
    public string BarEdge { get; private set; } = "bottom";
    public bool FirstRun { get; private set; }
    public bool Desktop { get; private set; } = true;
    public bool Acrylic { get; private set; } = true;
    public bool Supervise { get; private set; }
    public bool NoSupervise { get; private set; }
    /// --log=&lt;path&gt;: the logon task's log file.
    public string? LogPath { get; private set; }

    /// Arguments that were not understood (logged, never fatal: a newer launcher may pass more).
    public IReadOnlyList<string> Unknown => _unknown;
    /// Why this command line cannot run. Empty = it can. The first one is what Electron would throw.
    public IReadOnlyList<string> Errors => _errors;
    /// The arguments as given (after the exe path), for the log header and the hand-off.
    public IReadOnlyList<string> Raw => _raw;
    public bool Ok => _errors.Count == 0;

    readonly List<string> _unknown = new();
    readonly List<string> _errors = new();
    readonly List<string> _raw = new();
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    LaunchArgs() { }

    /// Was the flag given at all (any of the switches or test-only names, by its bare name)?
    public bool Has(string name) => _seen.Contains(name);

    /// The value of a flag main.js reads by itself (--test-chord=..., --log=...), null when absent.
    public string? ValueOf(string name) => _values.TryGetValue(name, out var v) ? v : null;

    /// The test-only switches this run was given that it will ignore (no --test): one log line each.
    public IEnumerable<string> IgnoredTestSwitches()
    {
        if (Test) yield break;
        foreach (var n in TestOnly) if (_seen.Contains(n)) yield return "--" + n;
    }

    /// <summary>What this start is for. Errors win: callers check <see cref="Ok"/> first.</summary>
    public RunKind Kind
        => Quit ? RunKind.Quit
         : Open != null ? RunKind.Open
         : Live ? RunKind.Live
         : Test ? RunKind.Test
         : UserData != null ? RunKind.Scratch
         : RunKind.Refused;

    /// The flag's name: "--test" and "--test=x" are "test"; "--test-live" is another flag.
    public static string? NameOf(string? raw)
    {
        if (raw == null || !raw.StartsWith("--", StringComparison.Ordinal)) return null;
        int eq = raw.IndexOf('=');
        return eq < 0 ? raw[2..] : raw[2..eq];
    }

    static string ValueOf(string raw, bool unquote = true)
    {
        int eq = raw.IndexOf('=');
        if (eq < 0) return "";
        string v = raw[(eq + 1)..];
        if (unquote && v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v;
    }

    /// <param name="argv">the arguments; an exe path (or anything not starting with "--") is skipped</param>
    public static LaunchArgs Parse(IEnumerable<string> argv)
    {
        var a = new LaunchArgs();
        var all = new List<string>();
        foreach (var raw in argv) {
            string? name = NameOf(raw);
            if (name == null) continue;
            all.Add(raw);
            a._raw.Add(raw);
            a._seen.Add(name);
            string value = ValueOf(raw);
            switch (name) {
                case "theme":
                    string t = value.Trim().ToLowerInvariant();
                    if (Array.IndexOf(Themes, t) < 0) a._errors.Add($"--theme must be \"dark\", \"light\" or \"system\", got \"{Clip(value, 40)}\"");
                    else a.Theme = t;
                    break;
                case "bench-out":
                    if (value.Length == 0) a._errors.Add("--bench-out needs a folder (--bench-out=<dir>)");
                    else a.BenchOut = value;
                    break;
                case "no-backdrop": a.NoBackdrop = true; break;
                case "test": a.Test = true; break;
                case "profile-name": a.ProfileName = value; break;
                case "user-data":
                    if (value.Length == 0 || !System.IO.Path.IsPathFullyQualified(value))
                        a._errors.Add($"--user-data needs a full folder path (it was \"{Clip(value, 70)}\")");
                    else a.UserData = value;
                    break;
                case "quit": a.Quit = true; break;
                case "live": a.Live = true; break;
                case "open": a.Open = ValueOf(raw, unquote: false); break;
                case "play": break;   // read with --open (OpenRequest.PlayFiles)
                case "screenshot": a.Screenshot = value; break;
                case "flyout": a.Flyout = value; break;
                case "bar-edge":
                    if (value != "top" && value != "bottom") a._errors.Add($"--bar-edge must be \"top\" or \"bottom\", got \"{value}\"");
                    else a.BarEdge = value;
                    break;
                case "supervise": a.Supervise = true; break;
                case "no-supervise": a.NoSupervise = true; break;
                case "first-run": a.FirstRun = true; break;
                case "no-desktop": a.Desktop = false; break;
                case "no-acrylic": a.Acrylic = false; break;
                case "log":
                    if (value.Length > 0) a.LogPath = value;
                    break;
                default:
                    if (Switches.Contains(name) || TestOnly.Contains(name)) a._values[name] = value;
                    else a._unknown.Add(raw);
                    break;
            }
        }
        if (a.Open != null) a.OpenRequest = global::Mantle.Core.OpenRequest.From(all);

        // lib/args.js parseArgs, rule for rule and in its order
        if (a.Live && a.Test)
            a._errors.Add("--live and --test are opposites: --live changes the Windows shell, --test promises not to. Pick one.");
        if (a.Quit && a.Live)
            a._errors.Add("--quit only asks a running Mantle to stop; it cannot be combined with --live");
        if (a.Open != null && a.Live)
            a._errors.Add("--open only asks a running Mantle to open a window; it cannot be combined with --live (run it without --live)");
        if (a.Open != null && a.Quit)
            a._errors.Add("--open and --quit ask a running Mantle for opposite things; pick one");
        if (a.Test && string.IsNullOrEmpty(a.ProfileName))
            a._errors.Add("--test requires --profile-name=<x> (it keys the userData dir and the single-instance lock; without it a test run would collide with the live shell)");
        if (!string.IsNullOrEmpty(a.ProfileName) && (!ProfileRule.IsMatch(a.ProfileName) || a.ProfileName.Contains("..", StringComparison.Ordinal)))
            a._errors.Add($"--profile-name must be letters, digits, \"-\", \"_\" or \".\" (up to 64, starting with a letter or digit), not \"{Clip(a.ProfileName, 70)}\": it becomes a folder name");
        if (a.Screenshot != null && !a.Test)
            a._errors.Add("--screenshot only works with --test (it quits the app when it is done, which would take the live shell down with it)");
        if (a.Flyout != null && !a.Test)
            a._errors.Add("--flyout only works with --test (it opens the bar's flyout by itself, which the live shell must never do)");
        // Electron: "--theme only works with --test". Native also lets a --user-data scratch run pin it
        // (the bench does); a bare start is refused by the live gate anyway, so only --live is left.
        if (a.Theme != null && a.Live)
            a._errors.Add("--theme only works with --test (the live shell takes its appearance from Mantle Settings > Personalization > Colors)");
        if (a.FirstRun && !a.Test)
            a._errors.Add("--first-run only works with --test (the live shell decides from its own profile whether this is its first start)");
        if (a.Screenshot == "")
            a._errors.Add("--screenshot needs a directory: --screenshot=<dir>");
        if (a.Live && a.UserData != null)
            a._errors.Add("--user-data only works without --live (the live shell's data is the user's own profile, never a folder named on the command line)");
        return a;
    }

    static string Clip(string s, int n) => s.Length > n ? s[..n] : s;
}

/// <summary>Exit codes, the same as Electron Mantle's (CORE-029).</summary>
public static class ExitCodes
{
    /// A normal quit or restart; a delivered --quit or --open; a second start that handed off.
    public const int Ok = 0;
    /// --quit found nothing running; an uncaught exception; a refused live start (another shell owns Windows).
    public const int Failed = 1;
    /// A bad flag, or a start with neither --live nor --test.
    public const int BadArgs = 2;
    /// --open with no running instance to open it.
    public const int OpenNotRunning = 3;
    /// --open with a value not on the allow-list.
    public const int OpenRefused = 4;
}

/// <summary>What a bare start (neither --live nor --test) and a live start print first (main.js).</summary>
public static class StartLines
{
    public const string LiveGate = "mantle: live mode changes your Windows taskbar. Run with --live to accept, or --test --profile-name=<x> for a harmless test window.";
    public const string LiveBanner = "mantle: starting LIVE. Windows will be put back on quit, on crash, and on kill.";

    /// The recovery command for a restore script path.
    public static string RecoveryCommand(string restoreScript) => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{restoreScript}\"";

    /// The live banner's lines, in main.js's order (before anything touches the shell).
    public static IReadOnlyList<string> LiveLines(string restoreScript, string stateFile) => new[] {
        LiveBanner,
        "mantle: if anything goes wrong, run this and Windows comes back:\n    " + RecoveryCommand(restoreScript),
        "mantle: the original shell state is recorded in " + stateFile,
    };
}
