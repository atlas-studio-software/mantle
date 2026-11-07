using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features;

// main.js restMantle / restNow / wakeNow / trimOwn / hoverAfterRest (lib/main-rel.test.js's rest rows), ported.
public class RestCoordinatorTests
{
    sealed class Parts : IRestParts
    {
        public readonly List<string> Open = new() { "bar", "launcher", "desktop", "switcher", "osd" };
        public readonly List<string> Did = new();
        public long Priv = 400, Ws = 900;
        public bool Wallpaper = true;
        public IReadOnlyList<string> Pages() => Open.ToArray();
        public (long Private, long WorkingSet) Memory() => (Priv, Ws);
        public bool LauncherAlive => Open.Contains("launcher");
        public void DestroyLauncher() { Did.Add("destroy launcher"); Open.Remove("launcher"); Priv -= 100; }
        public void MakeLauncher() { Did.Add("make launcher"); Open.Add("launcher"); }
        public bool SwitcherRest(string? why) { Did.Add($"switcher {why ?? "back"}"); if (why != null) { Open.Remove("switcher"); return false; } Open.Add("switcher"); return true; }
        public bool WallpaperRest(string? why) { Did.Add($"wallpaper {why ?? "back"}"); return why != null && Wallpaper; }
        public IReadOnlyList<int> OwnPids() => new[] { 11, 12 };
        public void AfterRest() => Did.Add("after");
    }

    sealed class Kit
    {
        public readonly Rig Rig;
        public readonly Parts Parts = new();
        public readonly RestCoordinator Rest;
        public bool Closing;
        public Kit(ManualTime t, bool test = true, bool withFeatures = true)
        {
            Rig = new Rig(t, test);
            Rig.Platform.SetStatus(PlatformState.Ok, "running");
            Rig.Platform.Answer("gamemode.trim", "{\"trimmed\":2}");
            Rig.Platform.Answer("hotkey", "{\"installed\":true}");
            Rest = new RestCoordinator(new RestOptions
            {
                Platform = Rig.Platform, Events = Rig.Events, State = Rig.Hub, Clock = new HostClock(t), Features = withFeatures ? Rig.Host : null,
                Parts = Parts, Test = test, Closing = () => Closing, Log = Rig.Lines.Add,
            });
        }
    }

    static async Task<RestAnswer> Settle(HostLoop loop, ManualTime t, Task<RestAnswer> a)
    {
        for (var i = 0; i < 40 && !a.IsCompleted; i++) { loop.Drain(); t.AdvanceMs(100); }
        loop.Drain();
        return await a;
    }

    [Fact]
    public void ARest_ClosesWhatAGameDoesNotNeed_GivesTheWinKeyBack_TellsThePages_AndTrims() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t, test: false);
        k.Rig.Host.Load(new[] { k.Rig.D("snap", rest: true), k.Rig.D("files", rest: false) });
        await k.Rig.Host.Installed;
        var a = await Settle(loop, t, k.Rest.RestAsync(true, "VALORANT runs"));
        Assert.True(a.Ok);
        Assert.Equal("VALORANT runs", k.Rest.RestingWhy);
        Assert.Equal(new[] { "launcher", "switcher" }, a.Closed);
        Assert.Contains("destroy launcher", k.Parts.Did);
        Assert.Equal("after", k.Parts.Did[^1]);   // the hover poll is asked after every rest
        Assert.Contains(k.Rig.Platform.Calls, c => c.Verb == "hotkey" && c.ArgsJson.Contains("\"winTap\":false"));
        Assert.Equal("the Win key opens Windows' Start while Mantle rests (VALORANT runs); Mantle's launcher is back when it exits", k.Rig.Hub.Current.Hotkey.Reason);
        Assert.True(k.Rig.Hub.Current.Hotkey.Resting);
        Assert.Contains(k.Rig.Events.Published, e => e.Name == "mantle.rest" && e.Payload is RestSignal { On: true, Why: "VALORANT runs" });
        Assert.Contains(k.Rig.Platform.Calls, c => c.Verb == "gamemode.trim" && c.ArgsJson.Contains("[11,12]"));
        Assert.True(k.Rig.Hub.Piece("snap").Resting);
        Assert.False(k.Rig.Hub.Piece("files").Resting);
        Assert.StartsWith("closed 2 windows (launcher, switcher), wallpaper unloaded; Mantle's memory 400 MB -> 300 MB (working set 900 MB -> 900 MB); idle pages of 2 Mantle processes handed back to Windows; kept: bar, desktop, osd", a.Text);
        Assert.True(k.Rig.Logged("rest: resting while VALORANT runs in "));
        t.AdvanceMs(30000); loop.Drain();
        Assert.True(k.Rig.Logged("rest: 30 s later: Mantle's working set 900 MB (private 300 MB)"));

        var again = await Settle(loop, t, k.Rest.RestAsync(true, "Elden Ring runs"));   // a new reason: only the words change
        Assert.True(again.Same);
        Assert.Equal("Elden Ring runs", k.Rest.RestingWhy);
        Assert.Contains("switcher Elden Ring runs", k.Parts.Did);
        Assert.Equal(1, k.Rig.Platform.Calls.Count(c => c.Verb == "gamemode.trim"));
    });

    [Fact]
    public void AWake_BringsItAllBack_ReArmsTheWinKey_AndTrimsAMinuteLater() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t, test: false);
        k.Rig.Host.Load(new[] { k.Rig.D("snap", rest: true) });
        await k.Rig.Host.Installed;
        Assert.Equal("Mantle was not resting", (await Settle(loop, t, k.Rest.RestAsync(false, "nothing"))).Text);
        await Settle(loop, t, k.Rest.RestAsync(true, "VALORANT runs"));
        t.AdvanceMs(5 * 60000);
        var w = await Settle(loop, t, k.Rest.RestAsync(false, "VALORANT exited"));
        Assert.True(w.Ok);
        Assert.Null(k.Rest.RestingWhy);
        Assert.Equal(new[] { "snap", "switcher", "launcher" }, w.Back);
        Assert.Equal("back after 5 min (VALORANT exited): snap, switcher, launcher, wallpaper", w.Text);
        Assert.Contains(k.Rig.Platform.Calls, c => c.Verb == "hotkey" && c.ArgsJson.Contains("\"winTap\":true"));
        Assert.Equal("a tap of the Win key opens the Mantle launcher", k.Rig.Hub.Current.Hotkey.Reason);
        Assert.True(k.Rig.Hub.Current.Hotkey.WinTap);
        Assert.Contains(k.Rig.Events.Published, e => e.Name == "mantle.rest" && e.Payload is RestSignal { On: false });
        Assert.True(k.Rest.TrimPending);
        var trims = k.Rig.Platform.Calls.Count(c => c.Verb == "gamemode.trim");
        t.AdvanceMs(60000); loop.Drain();
        Assert.Equal(trims + 1, k.Rig.Platform.Calls.Count(c => c.Verb == "gamemode.trim"));
        Assert.True(k.Rig.Logged("trim (60 s after the wake): idle pages of 2 Mantle processes handed back to Windows; working set 900 MB -> 900 MB"));
    });

    [Fact]
    public void RestsAndWakesRunOneAtATime_InOrder_AndAClosingMantleSaysSo() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        var r1 = k.Rest.RestAsync(true, "a game runs");
        var w1 = k.Rest.RestAsync(false, "the game exited");
        var r2 = k.Rest.RestAsync(true, "another game runs");
        Assert.False(w1.IsCompleted);
        await Settle(loop, t, r2);
        Assert.True(r1.IsCompleted && w1.IsCompleted);
        Assert.Equal("back after 0 min (the game exited): switcher, launcher, wallpaper", (await w1).Text);
        Assert.Equal("another game runs", k.Rest.RestingWhy);
        Assert.DoesNotContain(k.Rig.Platform.Calls, c => c.Verb == "hotkey");   // a test run never touches the hook
        k.Closing = true;
        Assert.Equal("Mantle is closing", (await Settle(loop, t, k.Rest.RestAsync(false, "quit"))).Reason);
        Assert.Null(k.Rest.RestingWhy);
        Assert.Equal("Mantle is closing", (await Settle(loop, t, k.Rest.RestAsync(true, "late"))).Reason);
    });

    [Fact]
    public void ATrim_IsSkippedWhileResting_AndSaysNotDoneWhenTheLinkIsDown() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t, withFeatures: false);
        k.Rest.TrimSoon("60 s after start");
        k.Rest.TrimSoon("60 s after start");   // a newer ask replaces the waiting one
        t.AdvanceMs(60000); loop.Drain();
        Assert.Equal(1, k.Rig.Platform.Calls.Count(c => c.Verb == "gamemode.trim"));
        k.Rig.Platform.SetStatus(PlatformState.Down, "it exited 3 times in a minute");
        Assert.Equal("trim (by hand): NOT done: Mantle's link to Windows is not running (it exited 3 times in a minute)", await k.Rest.TrimAsync("by hand"));
        var rest = await Settle(loop, t, k.Rest.RestAsync(true, "a game runs"));
        Assert.Contains("its idle pages were NOT handed back to Windows (Mantle's link to Windows is not running: it exited 3 times in a minute)", rest.Text);
        Assert.Equal("skipped", await k.Rest.TrimAsync("while resting"));
    });

    [Fact]
    public void AFeatureAsksForTheRest_ThroughItsContext() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        IFeatureContext? ctx = null;
        k.Rig.Host.Load(new[] { k.Rig.D("gamemode", f => f.Install = c => { ctx = c; return ValueTask.CompletedTask; }) });
        await k.Rig.Host.Installed;
        var a = await Settle(loop, t, ((IFeatureHostServices)ctx!).RestAsync(true, "VALORANT runs"));
        Assert.True(a.Ok);
        Assert.Equal("VALORANT runs", k.Rest.RestingWhy);
        Assert.Same(k.Rest, k.Rig.Host.RestHost);
    });
}
