using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features;

// lib/features.test.js, ported as the spec (the unit half; the Electron e2e half needs a running
// Mantle and is covered once the host is wired into the app). Each test names the parity row it proves.
public class FeatureHostTests
{
    static void T(Func<Rig, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static JsonElement J(string json) => FeatureOptions.Parse(json);

    // ---- FEAT-001 / FEAT-010 / KEY-022: loading and the contract ----

    [Fact]
    public void Load_StartsGoodFeatures_InNameOrder_AndBadOnesAreDownWithTheReason() => T(async r =>
    {
        var up = r.Host.Load(new[]
        {
            r.D("zeta"),
            r.D("alpha"),
            r.D("Bad-Name"),
            r.D("tray"),
            r.D("beta", chords: new[] { "Win+Q" }),
        });
        await r.Host.Installed;
        Assert.Equal(new[] { "alpha", "zeta" }, up);
        Assert.Equal(new[] { "Bad-Name", "alpha", "beta", "tray", "zeta" }.OrderBy(x => x, StringComparer.Ordinal), r.Host.List().Select(f => f.Name));
        var bad = r.Hub.Piece("Bad-Name");
        Assert.False(bad.Ok);
        Assert.Contains("is not lower-case letters and digits", bad.Reason);
        Assert.Contains("one of Mantle's own command prefixes", r.Hub.Piece("tray").Reason);
        Assert.Contains("chords names \"Win+Q\", which is not one of", r.Hub.Piece("beta").Reason);
        Assert.True(r.Logged("NOT started: alpha") == false && r.Logged("features: started alpha, zeta; NOT started:"));
    });

    [Fact]
    public void Load_ASecondClaimOnAChordIsRefusedAndNamed_AndANameClashingWithACommand() => T(async r =>
    {
        r.Bus.Register(new CommandRegistration("clip.open", "Mantle", _ => new(CommandResult.Success())));
        r.Host.Load(new[] { r.D("aaa", chords: new[] { "Win+V" }), r.D("bbb", chords: new[] { "Win+V" }), r.D("clip") });
        await r.Host.Installed;
        Assert.True(r.Hub.Piece("aaa").Ok);
        Assert.Equal("Win+V is already taken by aaa", r.Hub.Piece("bbb").Reason);
        Assert.Equal("its name clashes with Mantle's own command \"clip.open\"", r.Hub.Piece("clip").Reason);
        var restart = await r.Host.RestartAsync("bbb", "a test");
        Assert.False(restart.Ok);
        Assert.Contains("cannot start: Win+V is already taken by aaa", restart.Reason);
    });

    [Fact]
    public void Validate_RefusesTheSameKeyAsChordAndObserved_AndAcceptsTheVolumeKeysAsChords()
    {
        var both = FeatureDescriptor.Of(new FeatureAttribute("osd", "OSD", true) { Chords = new[] { "VolumeUp" }, Observe = new[] { "VolumeUp" } }, () => new ScriptFeature());
        Assert.Contains("as a chord AND an observed key", FeatureRules.Validate(both));
        var vol = FeatureDescriptor.Of(new FeatureAttribute("osd", "OSD", true) { Chords = new[] { "VolumeUp", "VolumeDown", "VolumeMute" } }, () => new ScriptFeature());
        Assert.Null(FeatureRules.Validate(vol));
        var twice = FeatureDescriptor.Of(new FeatureAttribute("x", "X", true) { Chords = new[] { "Win+V", "Win+V" } }, () => new ScriptFeature());
        Assert.Equal("chords lists the same name twice", FeatureRules.Validate(twice));
    }

    [Fact]
    public void Load_DefaultOnFalseStartsOff_AndASavedSwitchWins() => T(async r =>
    {
        r.Stores.Get("features.json").Put("bbb", JsonValue.Create(false));
        r.Host.Load(new[] { r.D("aaa", defaultOn: false), r.D("bbb") });
        await r.Host.Installed;
        Assert.False(r.Hub.Piece("aaa").On);
        Assert.False(r.Hub.Piece("bbb").On);
        Assert.True(r.Host.List().All(f => f.Running));   // installed even while off: it must honour the switch itself
    });

    [Fact]
    public void Load_CorruptSwitchesAreSaid_WithoutAPath_AndEveryFeatureStartsAtItsDefault() => T(async r =>
    {
        var s = r.Stores.Get("features.json");
        s.State = ReadState.Corrupt;
        s.Reason = "features.json is not valid JSON; kept as features.json.unreadable";
        r.Host.Load(new[] { r.D("aaa") });
        await r.Host.Installed;
        var line = r.Lines.First(l => l.Contains("switches were not used"));
        Assert.Contains("kept as features.json.unreadable", line);
        Assert.DoesNotContain(":\\", line);
        Assert.True(r.Hub.Piece("aaa").On);
    });

    // ---- FEAT-003: the context ----

    [Fact]
    public void Context_FlagsCommandsChordsAndKeysFollowTheContract() => T(async r =>
    {
        IFeatureContext? ctx = null;
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c =>
        {
            ctx = c;
            c.Command("ping", call => new(CommandResult.Success(J("{\"pong\":true}"))));
            return ValueTask.CompletedTask;
        }, chords: new[] { "Win+V" }) });
        await r.Host.Installed;
        Assert.True(ctx!.Flags.Test);
        Assert.NotNull(((IFeatureHostAccess)ctx).Test);
        var res = await r.Bus.CallAsync("aaa.ping");
        Assert.True(res.Ok);
        Assert.Throws<InvalidOperationException>(() => ctx.Command("ping", _ => new(CommandResult.Success())));
        Assert.Throws<ArgumentException>(() => ctx.OnChord("Win+S", _ => { }));
        Assert.Throws<ArgumentException>(() => ctx.OnKey("VolumeUp", _ => { }));
        Assert.Throws<ArgumentException>(() => ctx.Broadcast("other.thing", 1));
    });

    [Fact]
    public void Context_ScopedPlatformDefaultsTo4s_NeverThrows_AndEndsWithTheFeature() => T(async r =>
    {
        r.Platform.Start();
        r.Platform.Answer("clip.list", "{\"items\":[]}");
        IFeatureContext? ctx = null;
        var heard = 0;
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { ctx = c; c.Platform.On("clip", _ => heard++); return ValueTask.CompletedTask; }) });
        await r.Host.Installed;
        var ok = await ctx!.Platform.CallAsync("clip.list");
        Assert.True(ok.Ok);
        Assert.Equal(PlatformDefaults.FeatureCallTimeout, r.Platform.Calls.Last().Timeout);
        var unknown = await ctx.Platform.CallAsync("nope");
        Assert.Equal(PlatformOutcome.Failed, unknown.Outcome);
        r.Platform.Raise("clip");
        Assert.Equal(1, heard);
        var stopping = r.Host.StopAllAsync("Mantle is quitting");
        await stopping;
        r.Platform.Raise("clip");
        Assert.Equal(1, heard);
        var after = await ctx.Platform.CallAsync("clip.list");
        Assert.Equal(PlatformOutcome.Refused, after.Outcome);
        Assert.True(ctx.Stopping.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => ctx.Command("late", _ => new(CommandResult.Success())));
    });

    [Fact]
    public void Context_ReplayAndBroadcastGoThroughTheEvents() => T(async r =>
    {
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { c.Replay("aaa.state", () => 7); c.Broadcast("aaa.hello", "hi"); return ValueTask.CompletedTask; }) });
        await r.Host.Installed;
        Assert.Equal(7, r.Events.ReplayValue("aaa.state"));
        Assert.Contains(r.Events.Published, p => p.Name == "aaa.hello" && (string?)p.Payload == "hi");
    });

    // ---- KEY-030 / KEY-011: test mode keys ----

    [Fact]
    public void TestMode_FireChordReachesItsHandler_FireKeyEveryOnObserver_AndTheHookIsNeverAsked() => T(async r =>
    {
        r.Platform.Start();
        var chords = 0; var keysA = 0; var keysB = 0;
        r.Host.Load(new[]
        {
            r.D("aaa", f => f.Install = c => { c.OnChord("Win+V", _ => chords++); c.OnKey("VolumeUp", _ => keysA++); return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }, observe: new[] { "VolumeUp" }),
            r.D("beta", f => f.Install = c => { c.OnKey("VolumeUp", _ => keysB++); return ValueTask.CompletedTask; }, observe: new[] { "VolumeUp" }, defaultOn: false),
        });
        await r.Host.Installed;
        var d = r.Host.FireChord("Win+V");
        Assert.True(d.Delivered);
        Assert.Equal("aaa", d.Feature);
        Assert.Equal(1, chords);
        r.Host.FireKey("VolumeUp");
        Assert.Equal((1, 0), (keysA, keysB));
        Assert.Throws<InvalidOperationException>(() => r.Host.FireChord("Win+Q"));
        r.Time.AdvanceMs(100);
        Assert.Equal(0, r.HotkeyCalls);
        Assert.Contains("test mode never hooks the keyboard", r.Hub.Piece("aaa").KeysReason);
        Assert.Null(r.Hub.Piece("aaa").Broken);
    });

    [Fact]
    public void Volume_ATakenKeyStillReachesObservers_EveryPress() => T(async r =>
    {
        var taken = 0; var observed = 0;
        r.Host.Load(new[]
        {
            r.D("osd", f => f.Install = c => { c.OnChord("VolumeUp", _ => taken++); return ValueTask.CompletedTask; }, chords: new[] { "VolumeUp", "VolumeDown", "VolumeMute" }),
            r.D("watch", f => f.Install = c => { c.OnKey("VolumeUp", _ => observed++); return ValueTask.CompletedTask; }, observe: new[] { "VolumeUp" }),
        });
        await r.Host.Installed;
        for (var i = 0; i < 3; i++) r.Host.FireChord("VolumeUp");   // a held key is one event per repeat
        Assert.Equal((3, 3), (taken, observed));
    });

    [Fact]
    public void Live_FireChordRefuses_AndHookEventsAreRoutedLikeFireChordAndFireKey() => T(async r =>
    {
        var chords = 0; var keys = 0;
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { c.OnChord("Win+V", _ => chords++); c.OnKey("VolumeMute", _ => keys++); return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }, observe: new[] { "VolumeMute" }) });
        await r.Host.Installed;
        Assert.Throws<InvalidOperationException>(() => r.Host.FireChord("Win+V"));
        r.Platform.Raise("chord", "{\"chord\":\"Win+V\"}");
        r.Platform.Raise("key", "{\"key\":\"VolumeMute\"}");
        Assert.Equal((1, 1), (chords, keys));
    }, test: false);

    // ---- KEY-023 / KEY-024 / KEY-025: the hook, live ----

    [Fact]
    public void Live_NothingIsSentBeforeMantleOwnsTheShell_ThenOneHotkeyCallForOnAndHealthyOnly() => T(async r =>
    {
        r.HookTakesAll();
        r.Platform.Start();
        r.Host.Load(new[]
        {
            r.D("aaa", chords: new[] { "Win+V" }, observe: new[] { "VolumeUp" }),
            r.D("bbb", chords: new[] { "Win+S" }, defaultOn: false),
            r.D("ccc", f => f.Install = c => { c.Health(false, "no clipboard access"); return ValueTask.CompletedTask; }, chords: new[] { "Win+Tab" }),
        });
        await r.Host.Installed;
        r.Time.AdvanceMs(100);
        Assert.Equal(0, r.HotkeyCalls);
        Assert.Contains("Mantle has not taken the shell yet", r.Hub.Piece("aaa").KeysReason);
        await r.Host.ApplyLiveAsync();
        Assert.Equal(1, r.HotkeyCalls);
        Assert.Equal("{\"chords\":[\"Win+V\"],\"observe\":[\"VolumeUp\"]}", r.LastHotkeyJson);
        Assert.Equal(TimeSpan.FromSeconds(4), r.LastHotkey.Timeout);
        var a = r.Hub.Piece("aaa");
        Assert.True(a.Keys);
        Assert.Equal("Win+V: Mantle's", a.KeysReason);
        Assert.Equal("Win+S: Windows' own (turned off in the tray menu)", r.Hub.Piece("bbb").KeysReason);
        Assert.Equal("Win+Tab: Windows' own until it is healthy again", r.Hub.Piece("ccc").KeysReason);
        Assert.Equal("no clipboard access", r.Hub.Piece("ccc").Reason);
    }, test: false);

    [Fact]
    public void Live_HealthChangesInOneTurnAreOneCoalescedCall_AndHealthTrueTakesThemAgain() => T(async r =>
    {
        r.HookTakesAll();
        r.Platform.Start();
        IFeatureContext? a = null, b = null;
        r.Host.Load(new[]
        {
            r.D("aaa", f => f.Install = c => { a = c; return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }),
            r.D("bbb", f => f.Install = c => { b = c; return ValueTask.CompletedTask; }, chords: new[] { "Win+S" }),
        });
        await r.Host.Installed;
        await r.Host.ApplyLiveAsync();
        var before = r.HotkeyCalls;
        a!.Health(false, "broken a"); b!.Health(false, "broken b");
        r.Time.AdvanceMs(29);
        Assert.Equal(before, r.HotkeyCalls);
        r.Time.AdvanceMs(2);
        Assert.Equal(before + 1, r.HotkeyCalls);
        Assert.Equal("{\"chords\":[],\"observe\":[]}", r.LastHotkeyJson);
        a.Health(true);
        r.Time.AdvanceMs(31);
        Assert.Equal("{\"chords\":[\"Win+V\"],\"observe\":[]}", r.LastHotkeyJson);
        Assert.True(r.Hub.Piece("aaa").Keys);
        Assert.Equal("working", r.Hub.Piece("aaa").Reason);
    }, test: false);

    [Fact]
    public void Live_ARefusedHookIsBrokenWithTheReason_AndAnOldExeIsNamedAsSuch() => T(async r =>
    {
        r.Platform.Start();
        r.Platform.Handle("hotkey", _ => PlatformReply.Fail(PlatformOutcome.Failed, "hotkey needs winTap"));
        r.Host.Load(new[] { r.D("aaa", chords: new[] { "Win+V" }) });
        await r.Host.Installed;
        await r.Host.ApplyLiveAsync();
        var p = r.Hub.Piece("aaa");
        Assert.False(p.Keys);
        Assert.Contains("the sidecar exe predates feature chords", p.Broken);
        r.Platform.Handle("hotkey", _ => PlatformReply.Success(J("{\"installed\":false,\"reason\":\"SetWindowsHookEx failed\"}")));
        await r.Host.ApplyLiveAsync();
        Assert.Equal("Win+V: still Windows' own: SetWindowsHookEx failed", r.Hub.Piece("aaa").Broken);
    }, test: false);

    [Fact]
    public void Live_PlatformDownIsThePlatformsRow_AndARestartResendsTheChords() => T(async r =>
    {
        r.HookTakesAll();
        r.Platform.Start();
        r.Host.Load(new[] { r.D("aaa", chords: new[] { "Win+V" }) });
        await r.Host.Installed;
        await r.Host.ApplyLiveAsync();
        var calls = r.HotkeyCalls;
        r.Platform.SetStatus(PlatformState.Down, "it crashed");
        var p = r.Hub.Piece("aaa");
        Assert.False(p.Keys);
        Assert.Null(p.Broken);
        Assert.Contains("the sidecar is not running (it crashed)", p.KeysReason);
        r.Platform.Start();
        r.Time.AdvanceMs(31);
        Assert.Equal(calls + 1, r.HotkeyCalls);
        Assert.True(r.Hub.Piece("aaa").Keys);
    }, test: false);

    [Fact]
    public void Live_ReleasedMeansWindowsIsBack_NotBroken_AndStoppingNeverReasks() => T(async r =>
    {
        r.HookTakesAll();
        r.Platform.Start();
        IFeatureContext? a = null;
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { a = c; return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }) });
        await r.Host.Installed;
        await r.Host.ApplyLiveAsync();
        r.Host.Released("the kill switch");
        var p = r.Hub.Piece("aaa");
        Assert.Equal("Win+V: Windows' own (Mantle put Windows back: the kill switch)", p.KeysReason);
        Assert.Null(p.Broken);
        var calls = r.HotkeyCalls;
        var stop = r.Host.StopAllAsync("quit");
        a!.Health(false, "late");
        r.Time.AdvanceMs(100);
        await stop;
        Assert.Equal(calls, r.HotkeyCalls);
    }, test: false);

    [Fact]
    public void AChordHandlerThatThrows_TakesTheFeatureDown_ItsKeysGoBack_AndHealthTrueBringsItBack() => T(async r =>
    {
        IFeatureContext? a = null;
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { a = c; c.OnChord("Win+V", _ => throw new InvalidOperationException("boom")); return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }) });
        await r.Host.Installed;
        r.Host.FireChord("Win+V");
        var p = r.Hub.Piece("aaa");
        Assert.False(p.Ok);
        Assert.Equal("its Win+V handler failed (boom), so its keys went back to Windows", p.Reason);
        var again = r.Host.FireChord("Win+V");
        Assert.False(again.Delivered);
        Assert.StartsWith("it is not healthy", again.Reason);
        a!.Health(true);
        Assert.True(r.Hub.Piece("aaa").Ok);
        Assert.Equal(new[] { "Win+V" }, r.Host.Wanted().Chords);
    });

    // ---- FEAT-004: the switch ----

    [Fact]
    public void SetEnabled_HandlersHearIt_StateAndWantedFollow_AndItIsSavedToFeaturesJson() => T(async r =>
    {
        var heard = new System.Collections.Generic.List<bool>();
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { c.OnEnabled(heard.Add); return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }) });
        await r.Host.Installed;
        var ans = r.Host.TrySetEnabled("aaa", false, "the tray menu");
        Assert.True(ans.Ok);
        Assert.Equal(new[] { false }, heard);
        Assert.False(r.Hub.Piece("aaa").On);
        Assert.Empty(r.Host.Wanted().Chords);
        Assert.True(r.Stores.Get("features.json").TryGet("aaa", out var v) && v.ValueKind == JsonValueKind.False);
        Assert.True(r.Logged("aaa: turned off by the tray menu"));
        Assert.False(r.Host.FireChord("Win+V").Delivered);
        Assert.False(r.Host.TrySetEnabled("nope", true, "x").Ok);
    });

    [Fact]
    public void SetEnabled_RefusedInWordsForAFeatureThatDidNotLoadOrWasStopped() => T(async r =>
    {
        r.Host.Load(new[] { r.D("aaa"), r.D("Bad") });
        await r.Host.Installed;
        Assert.Contains("did not load, so it cannot be switched", r.Host.TrySetEnabled("Bad", false, "x").Reason);
        var stop = r.Host.StopAllAsync("quit");
        await stop;
        Assert.Equal("AAA was stopped when Mantle put Windows back; it comes back when Mantle restarts", r.Host.TrySetEnabled("aaa", false, "x").Reason);
    });

    [Fact]
    public void SetEnabled_AWriteThatFailsIsSaid() => T(async r =>
    {
        r.Host.Load(new[] { r.D("aaa") });
        await r.Host.Installed;
        r.Stores.Get("features.json").FailWrites = "the disk is full";
        r.Host.SetEnabled("aaa", false, "the tray menu");
        Assert.True(r.Logged("aaa: the switch was NOT saved for next time: the disk is full"));
    });

    // ---- FEAT-006 / FEAT-007: failed and slow installs ----

    [Fact]
    public void AnInstallThatThrows_IsDownWithTheReason_AndEverythingItRegisteredIsReleased() => T(async r =>
    {
        r.Platform.Start();
        _ = r.Host;
        var subs = r.Platform.Subscribers;
        r.Host.Load(new[]
        {
            r.D("aaa", f => f.Install = c =>
            {
                c.Command("ping", _ => new(CommandResult.Success()));
                c.Replay("aaa.state", () => 1);
                c.Platform.On("clip", _ => { });
                throw new InvalidOperationException("no clipboard");
            }),
            r.D("bbb"),
        });
        await r.Host.Installed;
        var p = r.Hub.Piece("aaa");
        Assert.False(p.Ok);
        Assert.Equal("it could not start: install() threw no clipboard", p.Reason);
        Assert.False(r.Bus.Has("aaa.ping"));
        Assert.False(r.Events.HasReplay("aaa.state"));
        Assert.Equal(subs, r.Platform.Subscribers);
        Assert.True(r.Hub.Piece("bbb").Ok);
    });

    [Fact]
    public void AnAsyncInstallThatFailsLater_IsDownWithItsWords_AndLeaksNothing() => T(async r =>
    {
        var gate = new TaskCompletionSource();
        r.Host.Load(new[] { r.D("aaa", f => f.Install = async c => { c.Command("ping", _ => new(CommandResult.Success())); await gate.Task; throw new InvalidOperationException("late failure"); }) });
        Assert.True(r.Bus.Has("aaa.ping"));
        gate.SetResult();
        await r.Host.Installed;
        Assert.Equal("it could not start: install() failed: late failure", r.Hub.Piece("aaa").Reason);
        Assert.False(r.Bus.Has("aaa.ping"));
    });

    [Fact]
    public void ASlowInstall_IsNotHealthyAtTheDeadline_AndHealthyOnceItFinishes() => T(async r =>
    {
        var gate = new TaskCompletionSource();
        r.Host.Load(new[] { r.D("aaa", f => f.Install = async _ => await gate.Task) });
        r.Time.Advance(TimeSpan.FromSeconds(10));
        await r.Host.Installed;
        Assert.False(r.Hub.Piece("aaa").Ok);
        Assert.Equal("install() did not finish within 10 s", r.Hub.Piece("aaa").Reason);
        gate.SetResult();
        r.Time.AdvanceMs(1);
        Assert.True(r.Hub.Piece("aaa").Ok);
        Assert.Equal("working", r.Hub.Piece("aaa").Reason);
    });

    [Fact]
    public void AnInstallThatFinishesAfterTheStop_IsStoppedAtOnce() => T(async r =>
    {
        var gate = new TaskCompletionSource();
        r.Host.Load(new[] { r.D("aaa", f => f.Install = async _ => await gate.Task) });
        var stop = r.Host.StopAllAsync("quit");
        await stop;
        gate.SetResult();
        r.Time.AdvanceMs(1);
        Assert.Single(r.Last("aaa").Stops);
        Assert.Equal("it finished starting after Mantle stopped it", r.Last("aaa").Stops[0].Reason);
    });

    // ---- FEAT-008: restart ----

    [Fact]
    public void Restart_BringsADownFeatureBack_WithANewInstance_AndRefusesInWords() => T(async r =>
    {
        var tries = 0;
        r.Host.Load(new[]
        {
            r.D("aaa", f => f.Install = c => { if (++tries == 1) throw new InvalidOperationException("flaky"); c.Command("ping", _ => new(CommandResult.Success())); return ValueTask.CompletedTask; }),
        });
        await r.Host.Installed;
        Assert.False(r.Hub.Piece("aaa").Ok);
        var ans = await r.Host.RestartAsync("aaa", "Mantle Settings");
        Assert.True(ans.Ok);
        Assert.True(r.Hub.Piece("aaa").Ok);
        Assert.True((await r.Bus.CallAsync("aaa.ping")).Ok);
        Assert.Equal(2, r.Made["aaa"].Count);
        Assert.Contains("Mantle has no feature called", (await r.Host.RestartAsync("nope", "x")).Reason);
    });

    [Fact]
    public void Restart_ARunningUnhealthyFeatureIsStoppedThenInstalledAgain_AndConcurrentRestartsShareOneTask() => T(async r =>
    {
        IFeatureContext? first = null;
        var stopGate = new TaskCompletionSource();
        r.Host.Load(new[] { r.D("aaa", f => { f.Install = c => { first ??= c; c.Command("ping", _ => new(CommandResult.Success())); return ValueTask.CompletedTask; }; f.Stop = async _ => await stopGate.Task; }) });
        await r.Host.Installed;
        first!.Health(false, "stuck");
        var t1 = r.Host.RestartAsync("aaa", "x");
        var t2 = r.Host.RestartAsync("aaa", "y");
        Assert.Same(t1, t2);
        stopGate.SetResult();
        var ans = await t1;
        Assert.True(ans.Ok);
        Assert.Single(r.Made["aaa"][0].Stops);
        Assert.Throws<InvalidOperationException>(() => first.Command("again", _ => new(CommandResult.Success())));
        Assert.True(r.Bus.Has("aaa.ping"));
    });

    [Fact]
    public void Restart_AfterStopAllIsRefusedInWords() => T(async r =>
    {
        r.Host.Load(new[] { r.D("aaa") });
        await r.Host.Installed;
        await r.Host.StopAllAsync("quit");
        Assert.Contains("was stopped when Mantle put Windows back", (await r.Host.RestartAsync("aaa", "x")).Reason);
    });

    // ---- FEAT-009: stop all ----

    [Fact]
    public void StopAll_RunsEveryStopAtOnce_AbandonsAHangAtItsDeadline_AndAThrowStopsNoOther() => T(async r =>
    {
        var hang = new TaskCompletionSource();
        r.Host.Load(new[]
        {
            r.D("aaa", f => f.Stop = _ => ValueTask.CompletedTask),
            r.D("bbb", f => f.Stop = async _ => await hang.Task),
            r.D("ccc", f => f.Stop = _ => throw new InvalidOperationException("stop blew up")),
            r.D("ddd", f => f.Install = c => { c.Command("ping", _ => new(CommandResult.Success())); return ValueTask.CompletedTask; }),
        });
        await r.Host.Installed;
        var s1 = r.Host.StopAllAsync("Mantle is quitting");
        Assert.Same(s1, r.Host.StopAllAsync("again"));
        Assert.False(s1.IsCompleted);
        r.Time.AdvanceMs(2500);
        var lines = await s1;
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Name == "bbb" && l.Line.StartsWith("stop() did not finish within 2500 ms"));
        Assert.Contains(lines, l => l.Name == "ccc" && l.Line.StartsWith("stop() threw: stop blew up"));
        Assert.True(r.Made.Values.All(m => m[0].Stops.Count == 1));
        var p = r.Hub.Piece("aaa");
        Assert.True(p.Stopped);
        Assert.True(p.Ok);
        var cmd = await r.Bus.CallAsync("ddd.ping");
        Assert.Equal("DDD is stopped (Mantle put Windows back)", cmd.Error);
    });

    // ---- FEAT-011: the piece and the list ----

    [Fact]
    public void List_CarriesKeysLoadedCommandsAndOptions() => T(async r =>
    {
        var opts = new[] { new FeatureOption("auto", "Start by itself", FeatureOptionKind.Bool, true) };
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { c.Command("ping", _ => new(CommandResult.Success())); return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }, options: opts) });
        await r.Host.Installed;
        var info = r.Host.List().Single();
        Assert.True(info.Loaded && info.Running && info.Enabled && info.Ok);
        Assert.Equal(new[] { "aaa.ping" }, info.Commands);
        Assert.Contains("Win+V", info.KeysReason);
        var o = info.Options.Single();
        Assert.Equal("bool", o.Type);
        Assert.True(o.IsDefault);
        Assert.Equal(JsonValueKind.True, o.Value.ValueKind);
    });

    [Fact]
    public void Tray_OneCheckboxPerFeature_AndTheReasonSitsUnderOneThatIsOnAndFailing() => T(async r =>
    {
        r.Host.Load(new[] { r.D("aaa"), r.D("bbb", f => f.Install = _ => throw new InvalidOperationException("no luck")), r.D("ccc", defaultOn: false) });
        await r.Host.Installed;
        var rows = r.Host.TrayRows();
        Assert.Equal(new[] { true, true, false }, rows.Select(x => x.Checked));
        Assert.Null(rows[0].ReasonLine);
        Assert.Equal("it could not start: install() threw no luck", rows[1].ReasonLine);
        Assert.Null(rows[2].ReasonLine);
    });

    // ---- FEAT-005: options ----

    [Fact]
    public void SetOption_ValidatesSavesInTheFeaturesOwnStore_TellsTheFeature_AndNeverLogsTextOrFolders() => T(async r =>
    {
        var opts = new FeatureOption[]
        {
            new("mode", "Mode", FeatureOptionKind.Choice, "a") { Choices = new[] { new FeatureChoice("a", "Alpha"), new FeatureChoice("b", "Beta") } },
            new("wait", "Wait", FeatureOptionKind.Number, 5.0) { Min = 1, Max = 60, Unit = "s" },
            new("note", "Note", FeatureOptionKind.Text, null),
            new("dir", "Folder", FeatureOptionKind.Folder, null),
        };
        var heard = new System.Collections.Generic.List<string>();
        IFeatureContext? ctx = null;
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { ctx = c; c.OnOption("*", (v, k) => heard.Add($"{k}={v.GetRawText()}")); return ValueTask.CompletedTask; }, options: opts) });
        await r.Host.Installed;
        var ok = await r.Host.SetOptionAsync("aaa", "mode", J("\"b\""), "Mantle Settings");
        Assert.True(ok.Ok);
        Assert.False(ok.Option!.IsDefault);
        Assert.Equal(new[] { "mode=\"b\"" }, heard);
        Assert.True(ctx!.Settings.TryGet("mode", out var m) && m.GetString() == "b");
        Assert.Same(r.Stores.Get("features\\aaa.json"), ctx.Settings);
        Assert.True(r.Logged("aaa: option mode set to \"b\" by Mantle Settings"));
        Assert.Equal("Mode: must be one of Alpha, Beta", (await r.Host.SetOptionAsync("aaa", "mode", J("\"c\""), "x")).Reason);
        Assert.Equal("Wait: must be at least 1 s", (await r.Host.SetOptionAsync("aaa", "wait", J("0"), "x")).Reason);
        Assert.True((await r.Host.SetOptionAsync("aaa", "wait", J("\"7\""), "x")).Ok);
        Assert.Equal(7, r.Host.List()[0].Options.Single(o => o.Key == "wait").Value.GetDouble());
        Assert.Equal("Folder: that folder does not exist", (await r.Host.SetOptionAsync("aaa", "dir", J("\"C:\\\\no\\\\such\\\\folder-mantle\""), "x")).Reason);
        Assert.Contains("must be a full folder path", (await r.Host.SetOptionAsync("aaa", "dir", J("\"relative\""), "x")).Reason);
        var dir = System.IO.Path.GetTempPath().TrimEnd('\\');
        Assert.True((await r.Host.SetOptionAsync("aaa", "dir", FeatureOptions.ToElement(dir), "x")).Ok);
        Assert.True((await r.Host.SetOptionAsync("aaa", "note", J("\"secret words\""), "x")).Ok);
        Assert.DoesNotContain(r.Lines, l => l.Contains(dir) || l.Contains("secret words"));
        Assert.True((await r.Host.SetOptionAsync("aaa", "mode", J("null"), "x")).Ok);
        Assert.False(ctx.Settings.TryGet("mode", out _));
        Assert.Contains("has no option called", (await r.Host.SetOptionAsync("aaa", "nope", J("1"), "x")).Reason);
        Assert.Contains("Mantle has no feature called", (await r.Host.SetOptionAsync("nope", "mode", J("1"), "x")).Reason);
        Assert.Throws<ArgumentException>(() => ctx.OnOption("undeclared", (_, _) => { }));
    });

    [Fact]
    public void Options_ValidateRefusesBrokenLists()
    {
        Assert.Null(FeatureOptions.Validate(new[] { new FeatureOption("auto", "Auto", FeatureOptionKind.Bool, true) }));
        Assert.Equal("option \"auto\" has no default", FeatureOptions.Validate(new[] { new FeatureOption("auto", "Auto", FeatureOptionKind.Bool, null) }));
        Assert.Equal("option \"x\": a choice needs choices", FeatureOptions.Validate(new[] { new FeatureOption("x", "X", FeatureOptionKind.Choice, "a") }));
        Assert.Equal("option \"x\" is listed twice", FeatureOptions.Validate(new[] { new FeatureOption("x", "X", FeatureOptionKind.Bool, true), new FeatureOption("x", "X", FeatureOptionKind.Bool, true) }));
        Assert.Contains("its default is not valid", FeatureOptions.Validate(new[] { new FeatureOption("n", "N", FeatureOptionKind.Number, 0.0) { Min = 1 } }));
    }

    [Fact]
    public void ABrokenSettingsFileIsSaid_OnTheFeaturesLine_WithoutAFullPath() => T(async r =>
    {
        var s = r.Stores.Get("features\\aaa.json");
        s.State = ReadState.Corrupt;
        s.Reason = "aaa.json is not valid JSON; kept as aaa.json.unreadable, starting from defaults";
        r.Host.Load(new[] { r.D("aaa", f => f.Install = c => { _ = c.Settings; return ValueTask.CompletedTask; }) });
        await r.Host.Installed;
        Assert.Contains("its saved settings were not used", r.Hub.Piece("aaa").Broken);
        Assert.DoesNotContain(r.Lines, l => l.Contains(":\\"));
    });

    // ---- FEAT-016 / FEAT-017: rest and wake (the host's half) ----

    [Fact]
    public void Rest_StopsOnlyRestFeatures_TellsTheOthers_AndWakeInstallsThemAgain() => T(async r =>
    {
        r.HookTakesAll();
        r.Platform.Start();
        var told = new System.Collections.Generic.List<RestSignal>();
        r.Host.Load(new[]
        {
            r.D("clip", f => f.Install = c => { c.Command("open", _ => new(CommandResult.Success())); c.Replay("clip.list", () => 1); return ValueTask.CompletedTask; }, chords: new[] { "Win+V" }, rest: true),
            r.D("files", f => f.Install = c => { ((IFeatureHostAccess)c).OnRest(told.Add); return ValueTask.CompletedTask; }, chords: new[] { "Win+E" }),
        });
        await r.Host.Installed;
        await r.Host.ApplyLiveAsync();
        Assert.True(r.Hub.Piece("clip").Keys && r.Hub.Piece("files").Keys);
        var rested = await r.Host.RestAllAsync("VALORANT runs");
        Assert.Equal(new[] { "clip" }, rested);
        Assert.True(r.Last("clip").Stops.Single().Rest);
        Assert.Equal(new[] { new RestSignal(true, "VALORANT runs") }, told);
        r.Time.AdvanceMs(31);
        Assert.Equal("{\"chords\":[\"Win+E\"],\"observe\":[]}", r.LastHotkeyJson);
        var p = r.Hub.Piece("clip");
        Assert.True(p.Ok && p.Resting && !p.Stopped);
        Assert.Equal("resting while VALORANT runs", p.Reason);
        Assert.Equal("Win+V: Windows' own while Mantle rests (VALORANT runs)", p.KeysReason);
        Assert.Equal("CLIP is resting while VALORANT runs; it is back when the game exits", (await r.Bus.CallAsync("clip.open")).Error);
        Assert.False(r.Events.HasReplay("clip.list"));
        Assert.Equal("VALORANT runs", r.Host.List()[0].Resting);
        Assert.Empty(await r.Host.RestAllAsync("VALORANT runs"));
        Assert.Contains("resting while VALORANT runs", (await r.Host.RestartAsync("clip", "x")).Reason);
        var back = await r.Host.WakeAllAsync("VALORANT exited");
        Assert.Equal(new[] { "clip" }, back);
        Assert.Equal(2, r.Made["clip"].Count);
        Assert.Equal(new RestSignal(false, "VALORANT exited"), told[^1]);
        Assert.True((await r.Bus.CallAsync("clip.open")).Ok);
        r.Time.AdvanceMs(31);
        Assert.True(r.Hub.Piece("clip").Keys);
        Assert.False(r.Hub.Piece("clip").Resting);
        Assert.Equal("working", r.Hub.Piece("clip").Reason);
    }, test: false);

    [Fact]
    public void Rest_OptionsSetWhileRestingAreSaved_AndTheWokenInstanceReadsThemFromTheSameStore() => T(async r =>
    {
        var opts = new[] { new FeatureOption("auto", "Auto", FeatureOptionKind.Bool, true) };
        r.Host.Load(new[] { r.D("clip", rest: true, options: opts) });
        await r.Host.Installed;
        await r.Host.RestAllAsync("a game runs");
        var ans = await r.Host.SetOptionAsync("clip", "auto", J("false"), "x");
        Assert.True(ans.Ok);
        Assert.Equal(0, ans.Told);
        await r.Host.WakeAllAsync("it exited");
        await r.Host.Installed;
        Assert.True(r.Last("clip").Ctx!.Settings.TryGet("auto", out var v) && v.ValueKind == JsonValueKind.False);
    });

    [Fact]
    public void StopAllWhileResting_StaysDown_AndIsNeverInstalledAgain() => T(async r =>
    {
        r.Host.Load(new[] { r.D("clip", rest: true) });
        await r.Host.Installed;
        await r.Host.RestAllAsync("a game runs");
        await r.Host.StopAllAsync("quit");
        Assert.Empty(await r.Host.WakeAllAsync("it exited"));
        Assert.Single(r.Made["clip"]);
        Assert.True(r.Hub.Piece("clip").Stopped);
    });

    // ---- IFeatureHostAccess: RunChord, KeysAway ----

    [Fact]
    public void RunChord_RunsTheOwnersHandler_AndKeysAwayHandsTheChordsBackWhileHealthy() => T(async r =>
    {
        var opened = 0;
        IFeatureContext? corners = null, clips = null;
        r.Host.Load(new[]
        {
            r.D("taskview", f => f.Install = c => { c.OnChord("Win+Tab", _ => opened++); return ValueTask.CompletedTask; }, chords: new[] { "Win+Tab" }),
            r.D("corners", f => f.Install = c => { corners = c; return ValueTask.CompletedTask; }),
            r.D("clips", f => f.Install = c => { clips = c; return ValueTask.CompletedTask; }, chords: new[] { "Win+G" }),
        });
        await r.Host.Installed;
        var more = (IFeatureHostAccess)corners!;
        Assert.True(more.RunChord("Win+Tab", "the top-left corner", dryRun: true).DryRun);
        Assert.Equal(0, opened);
        Assert.True(more.RunChord("Win+Tab", "the top-left corner").Delivered);
        Assert.Equal(1, opened);
        Assert.Equal("no feature takes Win+S", more.RunChord("Win+S", "x").Reason);
        ((IFeatureHostAccess)clips!).KeysAway("a full-screen app is in front");
        Assert.DoesNotContain("Win+G", r.Host.Wanted().Chords);
        Assert.True(r.Hub.Piece("clips").Ok);
        Assert.Equal("Win+G: Windows' own for now (a full-screen app is in front)", r.Hub.Piece("clips").KeysReason);
    });
}
