using System;

namespace Mantle.Core;

/// <summary>
/// A TEST INSTANCE PUTS NOTHING ON THE USER'S SCREEN, as Electron's lib/test-quiet.js: in a --test
/// run every native window is drawn at layered alpha 0, click-through, never activated, and kept
/// out of the taskbar and Alt+Tab. MANTLE_TEST_VISIBLE=1 opts out for the checks that need a real
/// on-screen window (the CI runner sets it). The window's own code runs unchanged; only the native
/// window stays quiet.
/// </summary>
public static class TestQuiet
{
    public const string VisibleEnv = "MANTLE_TEST_VISIBLE";

    public static bool Wanted(LaunchArgs args, Func<string, string?> env)
        => args.Test && !args.Live && env(VisibleEnv) != "1";

    /// One line for the log saying what this run does and why.
    public static string Describe(LaunchArgs args, Func<string, string?> env)
    {
        if (!args.Test) return "windows: normal (not a --test run)";
        return Wanted(args, env)
            ? "windows: quiet (--test): layered alpha 0, click-through, never activated, no taskbar or Alt+Tab entry; " + VisibleEnv + "=1 shows them"
            : "windows: visible (--test with " + VisibleEnv + "=1)";
    }
}
