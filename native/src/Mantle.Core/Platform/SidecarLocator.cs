using System;
using System.IO;

namespace Mantle.Core.Platform;

/// <summary>
/// Which Mantle.Sidecar.exe this run talks to (main.js SIDECAR_EXE_DEFAULT / SIDECAR_EXE_OVERRIDE):
/// MANTLE_SIDECAR_EXE in a test run (never in a live one: ignored with a line), else the exe next to
/// MantleNative.exe (the installed layout), else <c>sidecar\bin\Mantle.Sidecar.exe</c> of the checkout
/// the app was built in (a clone, a CI runner). A path naming no file is reported, never "fixed".
/// </summary>
public static class SidecarLocator
{
    public const string ExeName = "Mantle.Sidecar.exe";
    public const string OverrideEnv = "MANTLE_SIDECAR_EXE";

    public sealed record Choice(string Path, bool Override, string Line);

    public static Choice Resolve(LaunchArgs args, Func<string, string?> env, string appDir)
    {
        string fallback = Default(appDir);
        string? raw = env(OverrideEnv);
        if (!string.IsNullOrEmpty(raw)) {
            if (args.Kind == RunKind.Live)
                return new Choice(fallback, false, $"mantle: {OverrideEnv}={raw} is ignored: it is honoured only with --test, and this is a live run");
            string abs = System.IO.Path.GetFullPath(raw, appDir);
            return new Choice(abs, true, $"mantle: test run using the sidecar at {abs} ({OverrideEnv}), not {fallback}");
        }
        return new Choice(fallback, false, $"mantle: the sidecar is {fallback}{(File.Exists(fallback) ? "" : " (not there: the platform will say so)")}");
    }

    static string Default(string appDir)
    {
        string beside = System.IO.Path.Combine(appDir, ExeName);
        if (File.Exists(beside)) return beside;
        for (var d = new DirectoryInfo(appDir); d != null; d = d.Parent) {
            string repo = System.IO.Path.Combine(d.FullName, "sidecar", "bin", ExeName);
            if (File.Exists(repo)) return repo;
        }
        return beside;
    }
}
