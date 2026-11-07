using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Mantle.Core;
using Mantle.Core.Data;
using Mantle.Core.Logging;

namespace Mantle.App;

/// <summary>
/// Everything that runs before XAML: the arguments, the data root, the log, the single-instance
/// claim and the first read of Electron's settings. A second launch on the same data root hands its
/// arguments to the running instance and exits here, without creating a window.
/// </summary>
internal static class Host
{
    public static LaunchArgs Args = null!;
    public static DataPaths Paths = null!;
    public static MantleLog Log = null!;
    public static StartupMarks Marks = null!;
    public static MantleData Data = null!;
    public static SingleInstance Instance = null!;
    public static bool Quiet;
    public static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}

internal static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    static extern void XamlCheckProcessRequirements();

    /// A line for whoever started us (a console, a redirected pipe); a WinExe with neither just drops it.
    static void Say(TextWriter w, string line) { try { w.WriteLine(line); w.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } }

    [STAThread]
    static int Main(string[] mainArgs)
    {
        var argv = Environment.GetCommandLineArgs();
        var args = LaunchArgs.Parse(argv);
        if (!args.Ok) {
            // no data root is known yet, so nothing is written: the reason goes to stderr only (lib/args.js ArgError)
            Say(Console.Error, "mantle: " + args.Errors[0] + (args.Errors.Count > 1 ? $" (and {args.Errors.Count - 1} more: {string.Join("; ", System.Linq.Enumerable.Skip(args.Errors, 1))})" : ""));
            return ExitCodes.BadArgs;
        }
        if (args.Kind == RunKind.Refused) {
            Say(Console.Out, StartLines.LiveGate);   // CORE-001: before any window or platform exists
            return ExitCodes.BadArgs;
        }
        if (args.BenchOut != null) {
            try { Directory.CreateDirectory(args.BenchOut); } catch (Exception e) { Console.Error.WriteLine("mantle-native: --bench-out cannot be made (" + e.Message + ")"); }
        }

        var paths = DataPaths.Resolve(args, Host.Env, Path.GetTempPath());
        var log = new MantleLog(paths.LogFile, new LogOptions {
            Scrub = LogScrubber.ForThisMachine(),
            MirrorFile = args.BenchOut != null ? Path.Combine(args.BenchOut, "native-log.txt") : null,
        });
        log.Start($"===== mantle-native start {DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)} pid {Environment.ProcessId}: {string.Join(' ', args.Raw)}");
        var marks = new StartupMarks(log);
        marks.Add("main");
        log.Line($"data root: {paths.DataRoot} ({paths.Why}); shared look: {paths.LookSyncDir}");
        foreach (var u in args.Unknown) log.Line("argument not understood (ignored): " + u);

        string key = InstanceKey.For(paths.DataRoot);
        var claim = SingleInstance.TryClaim(key, out string claimWhy);
        if (claim == null) {
            bool sent = SingleInstance.SendToOwner(key, args.Raw, TimeSpan.FromSeconds(5), out string sendWhy);
            log.Line(sent
                ? $"single instance: another MantleNative already runs on this data root; {sendWhy}; this one exits"
                : $"single instance: another MantleNative holds this data root but the hand-off failed: {sendWhy}; this one exits");
            int handoff = ExitCodes.Ok;
            if (args.Kind == RunKind.Quit) {
                if (sent) Say(Console.Out, "mantle: asked the running Mantle to put Windows back and quit.");
                handoff = sent ? ExitCodes.Ok : ExitCodes.Failed;
            } else if (args.Kind == RunKind.Open) {
                var req = args.OpenRequest;
                if (req is not { Ok: true }) { Say(Console.Error, req?.Line ?? "mantle: --open needs a value"); handoff = ExitCodes.OpenRefused; }
                else if (sent) Say(Console.Out, $"mantle: asked the running Mantle ({paths.DataRoot}) to open its {req.Label}.");
                else { Say(Console.Error, OpenRequest.NotRunningLine(req)); handoff = ExitCodes.OpenNotRunning; }
            } else {
                Say(Console.Error, $"mantle: another instance is already using {paths.DataRoot}; this one is exiting");
            }
            log.Dispose();
            return handoff;
        }
        log.Line("single instance: " + claimWhy + (claim.Note != null ? " (" + claim.Note + ")" : ""));
        if (args.Kind == RunKind.Quit) {
            // CORE-009: a message, not an instance; nothing answered on this data root
            string none = $"mantle: no Mantle is running (nothing answered on {paths.DataRoot}), so there is nothing to quit.";
            log.Line(none);
            Say(Console.Out, none);
            claim.Dispose();
            log.Dispose();
            return ExitCodes.Failed;
        }
        if (args.Kind == RunKind.Open) {
            string line = OpenRequest.NotRunningLine(args.OpenRequest);
            log.Line(line);
            Say(Console.Error, line);
            claim.Dispose();
            log.Dispose();
            return ExitCodes.OpenNotRunning;
        }
        if (args.Kind == RunKind.Live) {
            // NEVER TWO SHELLS (SAFE-038): Electron Mantle running live owns the taskbar, the hooks and the app bar
            string electron = System.IO.Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mantle");
            string? refusal = Mantle.Core.Safety.TwoShells.Verdict(Mantle.Core.Safety.TwoShells.Probe(electron, Mantle.Core.Safety.TwoShells.InstalledExe(Host.Env)));
            if (refusal != null) {
                log.Line("mantle: " + refusal);
                Say(Console.Error, "mantle: " + refusal);
                claim.Dispose();
                log.Dispose();
                return ExitCodes.Failed;
            }
            foreach (var l in StartLines.LiveLines(System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "restore-windows.ps1"), System.IO.Path.Combine(paths.DataRoot, "shell-state.json"))) { log.Line(l); Say(Console.Out, l); }
            var (_, countdown) = StartWake.Countdown(true, console: false);   // a WinExe has no console to press Ctrl+C in
            log.Line("mantle: " + countdown);
        }
        foreach (var t in args.IgnoredTestSwitches()) log.Line($"{t} is ignored: it works only with --test");

        Host.Args = args; Host.Paths = paths; Host.Log = log; Host.Marks = marks; Host.Instance = claim;
        Host.Quiet = TestQuiet.Wanted(args, Host.Env);
        log.Line(TestQuiet.Describe(args, Host.Env));

        var data = new MantleData(paths, log);
        data.Load();
        Host.Data = data;
        marks.Add("data-read");

        int code = 0;
        try {
            XamlCheckProcessRequirements();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(p => {
                var ctx = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(ctx);
                _ = new MantleApp();
            });
            log.Line("exit: the app loop ended");
        } catch (Exception e) {
            log.Line("exit: FAILED: " + e);
            code = 1;
        } finally {
            data.Dispose();
            claim.Dispose();
            log.Dispose();
        }
        return code;
    }
}
