using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.LauncherIndex;

/// <summary>One built index: the rows in Electron's order (apps, commands, settings, power) and the four sources.</summary>
public sealed record IndexSnapshot(IReadOnlyList<LauncherItem> Items, IReadOnlyList<IndexSource> Sources, DateTimeOffset BuiltAt)
{
    public static readonly IndexSnapshot Empty = new([], [], DateTimeOffset.MinValue);
}

/// <summary>Where the index reads from. <see cref="ForWindows"/> is the real machine; tests pass scratch folders and fakes.</summary>
public sealed class IndexInputs
{
    public required IReadOnlyList<StartMenuRoot> Roots { get; init; }
    public required IReadOnlyList<string> ProfileFiles { get; init; }
    /// powercfg /a (read-only), or a fake.
    public required Func<CancellationToken, Task<SleepStates>> ReadSleep { get; init; }
    /// Get-StartApps, or a fake.
    public required Func<CancellationToken, Task<StoreRead>> ReadStore { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    /// How long a Store read may take before it counts as "not in time" (STORE_TIMEOUT_MS + 5 s).
    public TimeSpan StoreGuard { get; init; } = TimeSpan.FromSeconds(25);

    public static IndexInputs ForWindows()
    {
        string home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new IndexInputs
        {
            Roots = StartMenuRoot.Windows(),
            ProfileFiles = ProfileCommands.Candidates(home, Environment.GetEnvironmentVariable("OneDrive")),
            ReadSleep = WindowsReaders.ReadSleepStates,
            ReadStore = WindowsReaders.ReadStartApps,
        };
    }

    /// The env var a --test run's index reads: a scratch fixture folder in make-golden.cjs's layout.
    public const string TestDirEnv = "MANTLE_TEST_LAUNCHER_DIR";

    /// <summary>
    /// A --test run's inputs: never the machine's Start Menu, profile, Store list or powercfg. With
    /// <paramref name="dir"/> (MANTLE_TEST_LAUNCHER_DIR) it indexes that scratch fixture, laid out as make-golden.cjs
    /// writes it: <c>pd\Microsoft\Windows\Start Menu\Programs</c> (all users), <c>home\AppData\Roaming\Microsoft\Windows\
    /// Start Menu\Programs</c> (you), the profiles under <c>home</c>, <c>powercfg.txt</c> (a fake <c>powercfg /a</c>) and
    /// <c>store.txt</c> (a fake <c>Get-StartApps</c> list, "Name&lt;TAB&gt;AUMID" per line). Without it, only the built-in rows.
    /// <paramref name="why"/> says which, for the log.
    /// </summary>
    public static IndexInputs ForTestRun(string? dir, out string why)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            why = "a test run reads no Start Menu, profile, Store list or powercfg" +
                (string.IsNullOrWhiteSpace(dir) ? " (" + TestDirEnv + " is not set)" : " (" + TestDirEnv + "=" + dir + " does not exist)") +
                ": only the Settings, Mantle and power rows are listed";
            return new IndexInputs
            {
                Roots = [], ProfileFiles = [],
                ReadSleep = _ => Task.FromResult(new SleepStates(false, "a test run does not read powercfg")),
                ReadStore = _ => Task.FromResult(new StoreRead(true, [], "")),
            };
        }
        string home = Path.Combine(dir, "home");
        string sleepFile = Path.Combine(dir, "powercfg.txt"), storeFile = Path.Combine(dir, "store.txt");
        why = "a test run indexes only the fixture in " + dir + " (" + TestDirEnv + ")";
        return new IndexInputs
        {
            Roots = [
                new(Path.Combine(dir, "pd", "Microsoft", "Windows", "Start Menu", "Programs"), "all users"),
                new(Path.Combine(home, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"), "you"),
            ],
            ProfileFiles = ProfileCommands.Candidates(home, null),
            ReadSleep = _ => Task.FromResult(File.Exists(sleepFile) ? PowerRows.ParseSleepStates(File.ReadAllText(sleepFile))
                : new SleepStates(false, "the fixture has no powercfg.txt")),
            ReadStore = _ => Task.FromResult(new StoreRead(true, File.Exists(storeFile)
                ? File.ReadAllLines(storeFile).Select(l => l.Split('\t')).Where(p => p.Length == 2 && p[0].Length > 0).Select(p => new StoreApp(p[0], p[1])).ToList()
                : [], "")),
        };
    }
}

/// <summary>
/// lib/launcher-index.js#build with its process-wide caches (G): the sleep states and the Store list are kept
/// 10 minutes, so an open never runs powercfg or PowerShell; only the very first build waits for the Store
/// list, never longer than the 4 s icon cap; a list that lands later is merged into that build
/// (<see cref="LateMerged"/>), once. Host thread only.
/// </summary>
public sealed class LauncherIndexer
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IconCap = TimeSpan.FromSeconds(4);
    public const string NoIconResolver = "no icon resolver was passed to build()";

    readonly IndexInputs _in;
    (DateTimeOffset At, SleepStates States)? _sleep;
    IReadOnlyList<StoreApp>? _storeList;
    bool _storeOk;
    string _storeReason = "";
    DateTimeOffset _storeAt;
    Task? _storePending;

    public LauncherIndexer(IndexInputs inputs) { _in = inputs; }

    /// A build that missed the Store list, with the list merged in once it landed.
    public event Action<IndexSnapshot>? LateMerged;

    DateTimeOffset Now => _in.Clock.GetUtcNow();

    void StoreNow()
    {
        if (_storePending is { IsCompleted: false }) return;
        if (_storeList != null && Now - _storeAt < Ttl) return;
        _storePending = ReadStoreGuarded();
    }

    async Task ReadStoreGuarded()
    {
        StoreRead r;
        try
        {
            using var cts = new CancellationTokenSource();
            var read = Task.Run(() => _in.ReadStore(cts.Token));
            var done = await Task.WhenAny(read, Task.Delay(_in.StoreGuard, _in.Clock)).ConfigureAwait(true);
            if (done == read) r = await read.ConfigureAwait(true);
            else { cts.Cancel(); r = new StoreRead(false, [], "Windows did not list its Store apps in time"); }
        }
        catch (Exception e) { r = new StoreRead(false, [], "Windows did not list its Store apps: " + e.Message); }
        if (r.Ok) { _storeList = r.List; _storeOk = true; _storeReason = ""; }
        else { _storeOk = false; _storeReason = r.Reason.Length > 0 ? r.Reason : "Windows did not list its Store apps"; }
        _storeAt = Now;
        _storePending = null;
    }

    async Task<SleepStates> SleepNow(CancellationToken ct)
    {
        if (_sleep is { } s && Now - s.At < Ttl) return s.States;
        SleepStates states;
        try { states = await _in.ReadSleep(ct).ConfigureAwait(true); }
        catch (Exception e) { states = new SleepStates(false, "powercfg /a failed: " + e.Message.Split('\n')[0].TrimEnd('\r')); }
        _sleep = (Now, states);
        return states;
    }

    /// The newest powercfg /a answer (for Shut down's /hybrid choice).
    public SleepStates? LastSleepStates => _sleep?.States;

    public async Task<IndexSnapshot> BuildAsync(CancellationToken ct = default)
    {
        var lnk = StartMenuApps.Build(_in.Roots);
        var commands = ProfileCommands.Build(_in.ProfileFiles);
        var settings = SettingsTable.Build();
        var powerTask = SleepNow(ct);
        StoreNow();
        var pending = _storePending;
        if (_storeList == null && pending is { IsCompleted: false })
            await Task.WhenAny(pending, Task.Delay(IconCap, _in.Clock)).ConfigureAwait(true);
        var lnkNames = new HashSet<string>(lnk.Items.Select(i => Js.Lower(i.Name)), StringComparer.Ordinal);
        var storeList = StoreApps.Items(_storeList, lnkNames);
        var power = PowerRows.Build(await powerTask.ConfigureAwait(true));

        var appItems = Js.StableSort(lnk.Items.Concat(storeList), (a, b) => Js.LocaleCompare(a.Name, b.Name));
        string appsStatus = lnk.Status == "ok" || storeList.Count > 0 ? "ok" : lnk.Status;
        string appsReason = lnk.Status == "ok" ? lnk.Reason : (storeList.Count > 0 ? "" : lnk.Reason);
        // Part 1 has no icon resolver yet: Electron's build() says the same words when it is given none
        string iconsPart = appsStatus == "ok" ? "icons unavailable: " + NoIconResolver : "";
        var snap = Assemble(appItems, appsStatus, appsReason, iconsPart, commands, settings, power, _storeList != null);

        if (_storeList == null && _storePending is { IsCompleted: false })
        {
            var landing = _storePending;
            _ = landing.ContinueWith(_ =>
            {
                if (_storeList == null) return;
                var late = StoreApps.Items(_storeList, lnkNames);
                if (late.Count == 0) return;
                var merged = Js.StableSort(appItems.Concat(late), (a, b) => Js.LocaleCompare(a.Name, b.Name));
                string baseReason = lnk.Status == "ok" ? lnk.Reason : "";
                LateMerged?.Invoke(Assemble(merged, "ok", baseReason, iconsPart, commands, settings, power, true));
            }, CancellationToken.None, TaskContinuationOptions.None, TaskSchedulerExt.FromCurrentSynchronizationContextOrDefault());
        }
        return snap;
    }

    IndexSnapshot Assemble(List<LauncherItem> apps, string appsStatus, string appsReason, string iconsPart,
        SourceResult commands, SourceResult settings, SourceResult power, bool haveStore)
    {
        // a Mantle row that took Windows' shortcut's place in Apps is listed once, there
        var inApps = new HashSet<string>(apps.Where(i => !string.IsNullOrEmpty(i.MantleApp)).Select(i => i.Path), StringComparer.Ordinal);
        var items = new List<LauncherItem>(apps.Count + commands.Items.Count + settings.Items.Count + power.Items.Count);
        items.AddRange(apps);
        items.AddRange(commands.Items);
        items.AddRange(settings.Items.Where(i => !inApps.Contains(i.Path)));
        items.AddRange(power.Items);
        string storePart = StoreApps.Part(haveStore, _storeOk, _storeReason, _storePending is { IsCompleted: false });
        var sources = new List<IndexSource>
        {
            new("apps", apps.Count, appsStatus, string.Join("; ", new[] { appsReason, storePart, iconsPart }.Where(s => s.Length > 0))),
            new("commands", commands.Items.Count, commands.Status, commands.Reason),
            new("settings", settings.Items.Count, settings.Status, settings.Reason),
            new("power", power.Items.Count, power.Status, power.Reason),
        };
        return new IndexSnapshot(items, sources, Now);
    }
}

internal static class TaskSchedulerExt
{
    public static TaskScheduler FromCurrentSynchronizationContextOrDefault() =>
        SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;
}

/// <summary>The two read-only Windows questions the index asks (never asked by a unit test).</summary>
public static class WindowsReaders
{
    static string Sys32 => Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32");

    static async Task<(bool Ok, string Out, string Err, bool TimedOut)> Run(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e) { return (false, "", e.Message, false); }
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            string partial = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            return (false, partial, "timed out", true);
        }
        string o = await outTask.ConfigureAwait(false);
        string err = await errTask.ConfigureAwait(false);
        return (p.ExitCode == 0, o, err, false);
    }

    public static async Task<SleepStates> ReadSleepStates(CancellationToken ct)
    {
        var r = await Run(Path.Combine(Sys32, "powercfg.exe"), ["/a"], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!r.Ok && r.Out.Length == 0) return new SleepStates(false, "powercfg /a failed: " + (r.Err.Split('\n')[0].Trim() is { Length: > 0 } e ? e : "it gave no reason"));
        return PowerRows.ParseSleepStates(r.Out);
    }

    public static async Task<StoreRead> ReadStartApps(CancellationToken ct)
    {
        var r = await Run(Path.Combine(Sys32, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", StoreApps.Script], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (r.Out.Trim().Length == 0 && (!r.Ok || r.TimedOut))
            return new StoreRead(false, [], "Windows did not list its Store apps" + (r.TimedOut ? " in time" : ""));
        return new StoreRead(true, StoreApps.Parse(r.Out), "");
    }
}
