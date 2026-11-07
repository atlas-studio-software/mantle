using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Mantle.Core.Safety;
using Mantle.Core.State;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Safety;

// main.js applyLive (shell part), applyEdgeClip, applyTaskbarHide, restoreShell, markRestored/markUnrestored,
// sidecarUnavailable and the sidecar event handlers as the spec; plus the one write gate fed by TwoShells.
public class ShellGuardTests
{
    sealed class Rig
    {
        public readonly FakePlatform P = new();
        public readonly ShellStateHub Hub = new(ShellState.Initial(ShellMode.Live, BarEdge.Bottom));
        public readonly ManualScheduler Host = new();
        public readonly List<string> Log = new();
        public readonly List<string> Stopped = new();
        public bool ScriptOk = true;
        public int ScriptRuns;
        public string? Blocked;
        public ShellGuard G;
        public Rig(bool test = false, bool tbhide = true)
        {
            P.SetStatus(PlatformState.Ok, "sidecar 0.3.0 running");
            G = new ShellGuard(new ShellGuardOptions {
                Platform = P, State = Hub, Host = Host, Test = test, TbHide = tbhide, RecoveryCommand = "RECOVER",
                Log = Log.Add, Blocked = () => Blocked, StopFeatures = w => { Stopped.Add(w); return Task.CompletedTask; },
                RunScript = () => { ScriptRuns++; return ScriptOk ? (true, null) : (false, "exit 1"); },
            });
        }
        public ShellState S => Hub.Current;
    }

    [Fact]
    public async Task SAFE002_SAFE007_live_start_sets_only_autohide_then_the_edge_clip_and_says_what_it_was()
    {
        var r = new Rig();
        r.P.Answer("taskbar", "{\"autohide\":true,\"wasAutohide\":false}").Answer("edgeclip", "{\"applied\":true}").Answer("tbhide", "{\"applied\":true,\"reason\":\"region empty\"}");
        await r.G.ApplyLiveAsync();
        Assert.Equal("taskbar", r.P.Calls[0].Verb);
        Assert.Contains("\"mode\":\"autohide\"", r.P.Calls[0].ArgsJson);
        Assert.True(r.S.Taskbar.Autohide);
        Assert.Contains("it was always visible before", r.S.Taskbar.Reason);
        Assert.True(r.S.EdgeClip.Applied);
        Assert.Contains("2 px short of the bottom row", r.S.EdgeClip.Reason);
        Assert.Equal(new[] { "taskbar", "edgeclip", "tbhide" }, r.P.Calls.Select(c => c.Verb).ToArray());
        Assert.DoesNotContain(r.P.Calls, c => c.Verb is "move" or "hide" or "show");   // never moves or hides an Explorer window
    }

    [Fact]
    public async Task SAFE003_SAFE004_a_refused_tbhide_climbs_the_ladder_and_stops_once_quitting()
    {
        var r = new Rig();
        r.P.Answer("tbhide", "{\"applied\":false,\"reason\":\"Explorer said no\"}");
        await r.G.ApplyTbHideAsync(fresh: true);
        Assert.False(r.S.TbHide.Applied);
        Assert.Contains("trying again in 1 s (attempt 2)", r.S.TbHide.Reason);
        for (int i = 0; i < 10; i++) { r.Host.Advance(1000); await Task.Yield(); }
        Assert.Contains("trying again in 2 s", r.S.TbHide.Reason);
        Assert.Equal(11, r.G.TbHideTries);
        Assert.Equal(1000, ShellGuard.TbHideRetryMs[9]);
        Assert.Equal(120000, ShellGuard.TbHideRetryMs[^1]);
        await r.G.RestoreAsync("quit");
        Assert.False(r.G.TbHideRetrying);
        int calls = r.P.Calls.Count(c => c.Verb == "tbhide");
        r.Host.Advance(300000);
        Assert.Equal(calls, r.P.Calls.Count(c => c.Verb == "tbhide"));
        var test = new Rig(test: true);
        test.P.Answer("tbhide", "{\"applied\":false}");
        await test.G.ApplyTbHideAsync(true);
        Assert.False(test.G.TbHideRetrying);   // never in a test run without --test-tbhide
    }

    [Fact]
    public async Task SAFE005_SAFE010_SAFE013_restore_releases_in_order_verifies_and_marks_every_piece()
    {
        var r = new Rig();
        r.P.Answer("edgeclip", "{}").Answer("tbhide", "{\"reason\":\"region cleared\"}").Answer("taskbar", "{\"restored\":true,\"state\":0}").Answer("hotkey", "{}");
        await r.G.RestoreAsync("Mantle was asked to quit");
        Assert.Equal(new[] { "edgeclip", "tbhide", "taskbar", "hotkey" }, r.P.Calls.Select(c => c.Verb).ToArray());
        Assert.Contains("\"on\":false", r.P.Calls[1].ArgsJson);
        Assert.Equal("the Windows taskbar is back the way you had it: Mantle was asked to quit", r.S.Taskbar.Reason);
        Assert.StartsWith("the app bar is gone", r.S.Workarea.Reason);
        Assert.StartsWith("the desktop layer is detached", r.S.Desktop.Reason);
        Assert.StartsWith("the Win key opens the Windows Start menu again", r.S.Hotkey.Reason);
        Assert.Contains(r.Log, l => l.Contains("restored: no app bar was registered; taskbar auto-hide back to always visible; verified"));
        Assert.Equal(new[] { "Mantle was asked to quit" }, r.Stopped);
        var never = new Rig();
        never.P.Answer("edgeclip", "{}").Answer("tbhide", "{}").Answer("taskbar", "{\"restored\":false,\"changedByUs\":false}").Answer("hotkey", "{}");
        await never.G.RestoreAsync("quit");
        Assert.Contains(never.Log, l => l.Contains("taskbar auto-hide left as it was (Mantle never changed it)"));
        var bad = new Rig();
        bad.P.Answer("edgeclip", "{}").Answer("tbhide", "{}").Answer("taskbar", "{\"restored\":false,\"state\":1}").Answer("hotkey", "{}");
        await bad.G.RestoreAsync("quit");
        Assert.Contains("NOT back to auto-hide", bad.S.Taskbar.Reason);
        Assert.EndsWith("Run this and Windows comes back: RECOVER", bad.S.Taskbar.Reason);
    }

    [Fact]
    public async Task SAFE011_SAFE012_a_test_run_restores_nothing_and_a_blocked_sidecar_runs_the_script()
    {
        var t = new Rig(test: true);
        await t.G.RestoreAsync("test quit");
        Assert.Empty(t.P.Calls);
        Assert.Equal("test mode: nothing was changed, so there is nothing to restore", t.S.Taskbar.Reason);
        Assert.Single(t.Stopped);
        var b = new Rig { Blocked = "the sidecar is not answering" };
        await b.G.RestoreAsync("quit");
        Assert.Equal(1, b.ScriptRuns);
        Assert.Contains("tools/restore-windows.ps1 put Windows back and verified it", b.S.Taskbar.Reason);
        var f = new Rig { Blocked = "gone", ScriptOk = false };
        await f.G.RestoreAsync("quit");
        Assert.Contains("did not finish cleanly (exit 1)", f.S.Taskbar.Reason);
        Assert.Contains(f.Log, l => l.Contains("RESTORE NOT VERIFIED. Run this, and if it still looks wrong add -RestartExplorer"));
    }

    [Fact]
    public void SAFE006_SAFE014_SAFE015_the_sidecars_own_words_set_state()
    {
        var r = new Rig();
        r.P.Raise("taskbar", "{\"hidden\":true}");
        Assert.Contains("old shape", r.S.Taskbar.Reason);
        Assert.Contains("Mantle.Sidecar.exe next to MantleNative.exe is older", r.S.Taskbar.Reason);
        r.P.Raise("tbhide", "{\"applied\":true,\"reason\":\"re-applied\",\"guard\":\"pid 9\"}");
        Assert.Equal("re-applied; guard pid 9", r.S.TbHide.Reason);
        r.P.Raise("edgeclip", "{\"applied\":false,\"reason\":\"a fullscreen app took the cursor\"}");
        Assert.Contains("the 2 px edge clip is not in force", r.S.EdgeClip.Reason);
        r.P.Raise("restore", "{\"done\":true,\"detail\":\"auto-hide back\"}");
        Assert.Contains("the sidecar put Windows back on its way out: auto-hide back", r.S.Taskbar.Reason);
        r.P.Raise("died", "{\"code\":0}");
        Assert.Contains("the sidecar restored Windows and then exited (code 0)", r.S.Taskbar.Reason);
        var d = new Rig();
        d.P.Raise("died", "{\"code\":3221225477}");
        Assert.Contains("without confirming it put Windows back. Run this and Windows comes back: RECOVER", d.S.Taskbar.Reason);
        var t = new Rig(test: true);
        t.P.Raise("restore", "{\"done\":true,\"detail\":\"nothing\"}");
        Assert.StartsWith("test mode: the Windows taskbar was left exactly as it is", t.S.Taskbar.Reason);
    }

    [Fact]
    public void SAFE016_while_the_sidecar_is_down_every_piece_says_what_windows_does_instead()
    {
        var r = new Rig();
        r.Hub.Set(s => s with { EdgeClip = new EdgeClipPiece(true, true, "x"), TbHide = new TbHidePiece(true, true, "y") });
        r.P.SetStatus(PlatformState.Down, "sidecar gave up");
        Assert.Equal("the Windows taskbar is untouched and still where you put it: sidecar gave up", r.S.Taskbar.Reason);
        Assert.StartsWith("Mantle's bar is not registered as an app bar", r.S.Workarea.Reason);
        Assert.StartsWith("the Win key still opens the Windows Start menu", r.S.Hotkey.Reason);
        Assert.StartsWith("cannot read the notification area", r.S.Tray.Reason);
        Assert.False(r.S.EdgeClip.Applied);
        Assert.StartsWith("the Windows taskbar is visible again (the sidecar's guard clears its region", r.S.TbHide.Reason);
    }

    [Fact]
    public void SAFE038_a_held_lock_is_electron_running_and_a_leftover_0_byte_lockfile_is_not()
    {
        using var d = new TestDir("lock");
        string file = d.Write("lockfile", "");
        Assert.Equal(LockState.Stale, TwoShells.ProbeLock(file).State);
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {   // how Chromium holds it
            Assert.Equal(LockState.Held, TwoShells.ProbeLock(file).State);
            Assert.Equal(LockState.Held, TwoShells.Probe(d.Path, null).Lock);
        }
        Assert.Equal(LockState.Stale, TwoShells.Probe(d.Path, null).Lock);
        Assert.Equal(LockState.Absent, TwoShells.ProbeLock(Path.Combine(d.Path, "none")).State);
        Assert.Equal(0, new FileInfo(file).Length);   // the probe never wrote it
        var none = new ShellStateNote(false, false, null, null, null);
        Assert.Null(TwoShells.Verdict(new TwoShells.Facts(LockState.Stale, Array.Empty<int>(), none, false, null)));
        Assert.Contains("could not be checked", TwoShells.Verdict(new TwoShells.Facts(LockState.Unknown, Array.Empty<int>(), none, false, null, "its lock file could not be checked (X)"))!);
    }

    [Fact]
    public void SAFE038_one_write_gate_refuses_the_users_files_while_electron_holds_its_lock()
    {
        using var d = new TestDir("gate");
        string lockFile = d.Write("lockfile", "");
        var live = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--live" }), n => n == "APPDATA" ? d.Path : null, Path.GetTempPath());
        double now = 0;
        var other = TwoShells.OtherShell(() => TwoShells.Probe(Path.Combine(d.Path, "mantle"), null), () => now, TimeSpan.FromSeconds(3));
        Directory.CreateDirectory(Path.Combine(d.Path, "mantle"));
        string elock = Path.Combine(d.Path, "mantle", "lockfile");
        File.WriteAllText(elock, "");
        var gate = WriteGate.For(live, LaunchArgs.Parse(new[] { "--live" }), other);
        string theme = Path.Combine(live.DataRoot, "theme.json");
        Assert.Null(gate.Refusal(theme));                     // a leftover lockfile: native is the only shell
        using (new FileStream(elock, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
            Assert.Null(gate.Refusal(theme));                 // cached for 3 s
            now = 3000;
            Assert.Contains("Electron Mantle is running (its single-instance lock is held", gate.Refusal(theme));
        }
        now = 6000;
        Assert.Null(gate.Refusal(theme));
        Assert.Null(typeof(DataPaths).Assembly.GetType("Mantle.Core.Safety.WriteGate"));   // no second gate in Safety
    }

    [Fact]
    public async Task SAFE020_SAFE021_SAFE024_the_supervisor_record_names_native_and_stands_down_first()
    {
        using var d = new TestDir("sup");
        string exe = d.Write("Mantle.Sidecar.exe", "x");
        var host = new ManualScheduler();
        var spawned = new List<IReadOnlyList<string>>();
        Action<string>? say = null; Action<int?>? exit = null;
        bool alive = true;
        var log = new List<string>();
        var sup = new Supervisor(new SupervisorOptions {
            DataRoot = d.Path, Files = new SafeFile(WriteGate.Scratch(d.Path)), Host = host, SidecarExe = exe, ExePath = @"C:\n\MantleNative.exe",
            Argv = new[] { "--live" }, Live = true, Pid = 4242, Alive = _ => alive, Log = log.Add, Now = () => 1000,
            Spawn = (e, a, line, ex) => { spawned.Add(a); say = line; exit = ex; return new Child(7); },
        });
        Assert.Equal((false, "test instance (crash recovery is opt-in there: --supervise)"), Supervisor.Wanted(false, true, false, false));
        Assert.True(Supervisor.Wanted(false, true, true, false).On);
        Assert.False(Supervisor.Wanted(true, false, false, true).On);
        var st = sup.Start();
        Assert.True(st.On);
        Assert.Equal(new[] { "--supervise=4242", "--supervise-dir=" + d.Path, "--supervise-detach" }, spawned[0]);
        var rec = JsonDocument.Parse(File.ReadAllText(Path.Combine(d.Path, Supervisor.MainFile))).RootElement;
        Assert.Equal(@"C:\n\MantleNative.exe", rec.GetProperty("exe").GetString());
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("standDown").ValueKind);
        say!("{\"supervisor\":99,\"detached\":true}");
        exit!(0);
        Assert.Equal(99, sup.WatcherPid);
        Assert.Equal("detached", sup.Via);
        alive = false;
        for (int i = 0; i < 4; i++) { host.Advance(10000); say!("{\"supervisor\":" + (100 + i) + ",\"detached\":true}"); }
        Assert.Contains(log, l => l.Contains("crash recovery is OFF") && l.Contains("stopped 3 times in 10 minutes"));
        var down = sup.StandDown("Mantle was asked to quit");
        Assert.Equal((true, "record"), down);
        rec = JsonDocument.Parse(File.ReadAllText(Path.Combine(d.Path, Supervisor.MainFile))).RootElement;
        Assert.Equal("Mantle was asked to quit", rec.GetProperty("standDown").GetProperty("why").GetString());
        await Task.CompletedTask;
    }

    sealed class Child : ISupervisorChild { public Child(int pid) { Pid = pid; } public int Pid { get; } }

    [Fact]
    public void CORE028_session_end_answers_yes_once_and_ending_runs_the_one_shutdown()
    {
        var log = new List<string>(); var downs = new List<string>(); double now = 0;
        var se = new SessionEnd(log.Add, downs.Add, () => now);
        Assert.True(se.Query(SessionEnd.ENDSESSION_LOGOFF));
        Assert.True(se.Query(SessionEnd.ENDSESSION_LOGOFF));
        Assert.Single(log);
        Assert.True(se.HoldReloads);
        now = 61000;
        Assert.False(se.HoldReloads);
        se.End(true, SessionEnd.ENDSESSION_LOGOFF | SessionEnd.ENDSESSION_CRITICAL);
        Assert.Equal(new[] { "Windows is ending the session (critical, logoff)" }, downs);
        se.End(true, 0);
        Assert.Equal("Windows is ending the session (shutdown or restart)", downs[1]);
    }

    [Fact]
    public void CORE007_second_launch_probes_route_only_in_a_test_instance()
    {
        Assert.Equal((SecondAsk.Quit, (string?)null), SecondLaunch.Route(new[] { "x.exe", "--quit" }, false));
        Assert.Equal((SecondAsk.Open, "wallpapers"), SecondLaunch.Route(new[] { "--open=wallpapers" }, false));
        Assert.Equal(SecondAsk.Plain, SecondLaunch.Route(new[] { "--test-tray" }, false).Ask);
        Assert.Equal(SecondAsk.Tray, SecondLaunch.Route(new[] { "--test-tray" }, true).Ask);
        Assert.Equal(SecondAsk.Report, SecondLaunch.Route(new[] { "--test-report" }, true).Ask);
        Assert.Equal((SecondAsk.Chord, "Win+V"), SecondLaunch.Route(new[] { "--test-chord=Win+V" }, true));
        Assert.Equal((SecondAsk.Key, "VolumeUp"), SecondLaunch.Route(new[] { "--test-key=VolumeUp" }, true));
        Assert.Equal((SecondAsk.Memory, "asked"), SecondLaunch.Route(new[] { "--test-memory=" }, true));
        Assert.Equal((SecondAsk.Click, "start"), SecondLaunch.Route(new[] { "--test-click=start" }, true));
        Assert.Equal(SecondAsk.Crash, SecondLaunch.Route(new[] { "--test-crash" }, true).Ask);
        Assert.Equal(SecondAsk.Unload, SecondLaunch.Route(new[] { "--test-unload" }, true).Ask);
    }

    [Fact]
    public void LOG015_LOG016_every_state_change_is_broadcast_and_replayed_on_subscribe()
    {
        var hub = new ShellStateHub(ShellState.Initial(ShellMode.Test, BarEdge.Bottom));
        var events = new EventHub(_ => { });
        var log = new List<string>(); int tray = 0;
        using var b = new StateBroadcast(hub, events, log.Add, () => tray++);
        var seen = new List<ShellState>();
        using var sub = events.Subscribe<ShellState>("state", seen.Add);
        Assert.Single(seen);                                   // replay on subscribe
        Assert.Same(hub.Current, seen[0]);
        hub.Set(s => s with { Taskbar = new TaskbarPiece(false, null, "changed") });
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, tray);
        Assert.Equal("changed", seen[1].Taskbar.Reason);
        Assert.False(events.HasReplay("no-such-name"));        // unknown name: {has:false}
    }

    [Fact]
    public void SC_iconDiff_lists_get_their_icons_back_in_place_and_a_miss_is_counted()
    {
        var c = new WindowIconCache();
        var first = c.Full(JsonDocument.Parse("{\"ev\":\"windows\",\"iconDiff\":true,\"list\":[{\"hwnd\":1,\"title\":\"a\",\"icon\":\"data:A\",\"iconFrom\":\"exe\"}]}").RootElement);
        Assert.Empty(first.Missed);
        var next = c.Full(JsonDocument.Parse("{\"ev\":\"windows\",\"iconDiff\":true,\"list\":[{\"hwnd\":1,\"title\":\"b\",\"iconFrom\":\"exe\"},{\"hwnd\":2,\"title\":\"c\"}]}").RootElement);
        Assert.Equal("{\"ev\":\"windows\",\"list\":[{\"hwnd\":1,\"title\":\"b\",\"icon\":\"data:A\",\"iconFrom\":\"exe\"},{\"hwnd\":2,\"title\":\"c\",\"icon\":null}]}", next.Message.GetRawText());
        Assert.Equal(new[] { "2" }, next.Missed);
        c.Fill(JsonDocument.Parse("[{\"hwnd\":2,\"icon\":\"data:C\"}]").RootElement, next.Missed);
        var third = c.Full(JsonDocument.Parse("{\"ev\":\"windows\",\"iconDiff\":true,\"list\":[{\"hwnd\":2}]}").RootElement);
        Assert.Contains("data:C", third.Message.GetRawText());
        Assert.Equal(1, c.Count);                              // a closed window's icon goes with it
        c.Reset();
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public async Task GEN_the_generated_catalog_registers_the_host_and_feature_commands_without_reflection()
    {
        Assert.Contains(Catalogs.Commands, x => x.Name == "state.get" && x.Owner == "HostCommands" && !x.Static);
        Assert.Contains(Catalogs.Commands, x => x.Name == "features.list" && x.Owner == "FeatureCommands");
        var hub = new ShellStateHub(ShellState.Initial(ShellMode.Test, BarEdge.Bottom));
        var quits = new List<string>();
        var bus = new CommandBus();
        using (Catalogs.Register(bus, new HostCommands(hub, _ => Task.CompletedTask, quits.Add, () => "asked"))) {
            Assert.True(bus.Has("shell.quit"));
            var r = await bus.CallAsync("shell.quit", null, "the tray");
            Assert.True(r.Ok);
            Assert.Equal("Mantle was asked to quit (the tray)", quits.Single());
            var dup = Assert.Throws<InvalidOperationException>(() => Catalogs.Register(bus, new HostCommands(hub, _ => Task.CompletedTask, quits.Add, () => "")));
            Assert.Contains("already registered by host", dup.Message);
            Assert.True(bus.Has("state.get"));                 // a refused batch leaves the first one whole
        }
        Assert.False(bus.Has("shell.quit"));
        Assert.NotNull(Catalogs.Features);
    }

    [Fact]
    public void LOG001_the_log_file_is_appended_with_a_blank_line_between_runs_and_every_line_reaches_the_terminal()
    {
        using var d = new TestDir("tee");
        string file = Path.Combine(d.Path, "sub", "native.log");
        var said = new List<string>();
        using (var a = new Mantle.Core.Logging.MantleLog(file, new Mantle.Core.Logging.LogOptions { Echo = said.Add })) { a.Start("===== run 1"); a.Line("one"); }
        using (var b = new Mantle.Core.Logging.MantleLog(file, new Mantle.Core.Logging.LogOptions { Echo = said.Add })) { b.Start("===== run 2"); b.Line("two"); }
        var lines = File.ReadAllLines(file);
        Assert.Contains(lines, l => l.EndsWith("===== run 1"));
        Assert.Contains("", lines);                            // the blank line between runs
        Assert.Contains(lines, l => l.EndsWith("mantle-native: two"));
        Assert.Equal(4, said.Count);
        Assert.EndsWith("mantle-native: one", said[1]);
    }

    [Fact]
    public void DEV6_the_native_build_names_its_own_fix_never_npm()
    {
        Assert.DoesNotContain("npm", PlatformWords.Rebuild);
        Assert.DoesNotContain("npm", PlatformWords.KeepsStopping);
        Assert.Contains(@"sidecar\bin\Mantle.Sidecar.exe", PlatformWords.Rebuild);
    }
}
