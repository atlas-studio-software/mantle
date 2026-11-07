using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mantle.Core;

/// <summary>
/// The command line of MantleNative.exe. The name rule is Electron Mantle's (lib/args.js): an
/// argument is "--name" or "--name=value"; anything that does not start with "--" (the exe path
/// in GetCommandLineArgs) is ignored, and a value may be wrapped in double quotes.
///
///   --theme=light|dark|system  the island's appearance for this run; it pins the theme (a changed
///                              theme.json is then logged, not applied). Without it the bar follows
///                              Mantle's own theme.json, like the Electron bar.
///   --bench-out=&lt;dir&gt;          write ready.json, cells.json and native-log.txt there (the bench).
///   --no-backdrop              skip the captured, blurred backdrop; the tint carries the island alone.
///   --test                     a test instance: its data lives in %TEMP%\mantle-test-&lt;profile&gt;,
///                              never the user's, and its windows stay off the user's screen unless
///                              MANTLE_TEST_VISIBLE=1. Needs --profile-name.
///   --profile-name=&lt;x&gt;         names the test profile; keys the data folder and the single-instance
///                              claim (letters, digits, - _ . ; up to 64; as lib/args.js).
///   --user-data=&lt;dir&gt;          the data root for tests and fixtures (overrides both of the above).
///   --quit                     ask the running instance on this data root to stop, then exit.
///   --live                     accepted for symmetry with Electron; the native bar changes nothing
///                              in Windows yet, so it is not required. Cannot be combined with --test.
/// </summary>
public sealed class LaunchArgs
{
    static readonly Regex ProfileRule = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    static readonly string[] Themes = { "light", "dark", "system" };

    public string? Theme { get; private set; }
    public string? BenchOut { get; private set; }
    public bool NoBackdrop { get; private set; }
    public bool Test { get; private set; }
    public string? ProfileName { get; private set; }
    public string? UserData { get; private set; }
    public bool Quit { get; private set; }
    public bool Live { get; private set; }

    /// Arguments that were not understood (logged, never fatal: a newer launcher may pass more).
    public IReadOnlyList<string> Unknown => _unknown;
    /// Why this command line cannot run. Empty = it can.
    public IReadOnlyList<string> Errors => _errors;
    /// The arguments as given (after the exe path), for the log header and the hand-off.
    public IReadOnlyList<string> Raw => _raw;
    public bool Ok => _errors.Count == 0;

    readonly List<string> _unknown = new();
    readonly List<string> _errors = new();
    readonly List<string> _raw = new();

    LaunchArgs() { }

    /// The flag's name: "--test" and "--test=x" are "test"; "--test-live" is another flag.
    public static string? NameOf(string? raw)
    {
        if (raw == null || !raw.StartsWith("--", StringComparison.Ordinal)) return null;
        int eq = raw.IndexOf('=');
        return eq < 0 ? raw[2..] : raw[2..eq];
    }

    static string ValueOf(string raw)
    {
        int eq = raw.IndexOf('=');
        if (eq < 0) return "";
        string v = raw[(eq + 1)..];
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v;
    }

    /// <param name="argv">the arguments; an exe path (or anything not starting with "--") is skipped</param>
    public static LaunchArgs Parse(IEnumerable<string> argv)
    {
        var a = new LaunchArgs();
        foreach (var raw in argv) {
            string? name = NameOf(raw);
            if (name == null) continue;
            a._raw.Add(raw);
            string value = ValueOf(raw);
            switch (name) {
                case "theme":
                    string t = value.Trim().ToLowerInvariant();
                    if (Array.IndexOf(Themes, t) < 0) a._errors.Add($"--theme can be light, dark or system, not \"{Clip(value)}\"");
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
                        a._errors.Add($"--user-data needs a full folder path (it was \"{Clip(value)}\")");
                    else a.UserData = value;
                    break;
                case "quit": a.Quit = true; break;
                case "live": a.Live = true; break;
                default: a._unknown.Add(raw); break;
            }
        }

        // the same rules as lib/args.js, in its words where they apply
        if (a.Live && a.Test)
            a._errors.Add("--live and --test are opposites: --live is the user's shell, --test promises to stay out of it. Pick one.");
        if (a.Quit && a.Live)
            a._errors.Add("--quit only asks a running Mantle to stop; it cannot be combined with --live");
        if (a.Test && string.IsNullOrEmpty(a.ProfileName))
            a._errors.Add("--test requires --profile-name=<x> (it keys the data folder and the single-instance claim; without it a test run would collide with the user's own)");
        if (!string.IsNullOrEmpty(a.ProfileName) && (!ProfileRule.IsMatch(a.ProfileName) || a.ProfileName.Contains("..", StringComparison.Ordinal)))
            a._errors.Add($"--profile-name must be letters, digits, \"-\", \"_\" or \".\" (up to 64, starting with a letter or digit), not \"{Clip(a.ProfileName)}\": it becomes a folder name");
        return a;
    }

    static string Clip(string s) => s.Length > 70 ? s[..70] : s;
}
