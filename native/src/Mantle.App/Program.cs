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

    [STAThread]
    static int Main(string[] mainArgs)
    {
        var argv = Environment.GetCommandLineArgs();
        var args = LaunchArgs.Parse(argv);
        if (!args.Ok) {
            // no data root is known yet, so nothing is written: the reasons go to stderr only
            try { Console.Error.WriteLine("mantle-native: cannot start: " + string.Join("; ", args.Errors)); } catch (IOException) { }
            return 2;
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
            log.Dispose();
            return sent ? 0 : 3;
        }
        log.Line("single instance: " + claimWhy + (claim.Note != null ? " (" + claim.Note + ")" : ""));
        if (args.Quit) {
            log.Line("--quit: no MantleNative was running on this data root, so there is nothing to quit");
            claim.Dispose();
            log.Dispose();
            return 0;
        }

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
