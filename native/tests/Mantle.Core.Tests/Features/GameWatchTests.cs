using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Xunit;

namespace Mantle.Core.Tests.Features;

// lib/features/_game-watch.js and lib/features/game-watch.test.js (the hub, whole() and the follower), ported.
// Game mode itself (the driver) is ported with the features; here a scripted driver stands in for it.
public class GameWatchTests
{
    static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Whole_AppliesTheChangesInOrder_KeepsNoDetail_AndAsksAgainRatherThanGuess()
    {
        var mem = new GameWatchMemory();
        Assert.Null(GameWatch.Whole(mem, J("{\"came\":[],\"gone\":[],\"procsSince\":3,\"procsGen\":4}")));
        GameWatch.Whole(mem, J("{\"procs\":[{\"pid\":1,\"file\":\"a.exe\",\"cpuMs\":5},{\"pid\":2,\"file\":\"b.exe\"}],\"procsGen\":7}"));
        Assert.Equal(7, mem.Gen);
        var w = GameWatch.Whole(mem, J("{\"came\":[{\"pid\":3,\"file\":\"c.exe\"},{\"pid\":1,\"file\":\"z.exe\"}],\"gone\":[2],\"procsSince\":7,\"procsGen\":8,\"fg\":{\"pid\":3}}"))!;
        Assert.Equal("[{\"pid\":3,\"file\":\"c.exe\"},{\"pid\":1,\"file\":\"z.exe\"}]", w["procs"]!.ToJsonString());
        Assert.Equal(3, (int)w["fg"]!["pid"]!);
        Assert.False(w.ContainsKey("came") || w.ContainsKey("gone") || w.ContainsKey("procsSince"));
        Assert.Null(GameWatch.Whole(mem, J("{\"came\":[],\"gone\":[],\"procsSince\":7,\"procsGen\":9}")));   // stale since
        Assert.Null(mem.Count);
        var old = GameWatch.Whole(mem, J("{\"procs\":[{\"pid\":9}]}"))!;   // an old sidecar: no procsGen
        Assert.Single(old["procs"]!.AsArray());
        Assert.Null(mem.Gen);
        GameWatch.Whole(mem, J("{\"procs\":[{\"pid\":1}],\"procsGen\":2,\"procsReason\":\"the list was cut short\"}"));
        Assert.Null(mem.Gen);   // a list with a reason is not kept
    }

    sealed class Sub : IGameWatchSubscriber
    {
        public bool W = true, F; public int? Ms; public bool Throw;
        public readonly List<JsonObject> Got = new();
        public bool Wants() => Throw ? throw new Exception("boom") : W;
        public int? FastMs() => Ms;
        public bool Fed() => F;
        public void Result(JsonObject r) { if (Throw) throw new Exception("boom"); Got.Add(r); }
    }

    sealed class Driver : IGameWatchDriver { public int Changes; public void Changed() => Changes++; }

    [Fact]
    public void TheHub_TellsTheDriverWhatFollowersWant_AndOneThrowingListenerNeverStopsTheRest()
    {
        var hub = new GameWatch();
        var d = new Driver();
        var flips = new List<bool>();
        hub.OnDriver(flips.Add);
        var off = hub.Drive(d);
        Assert.True(hub.Driven);
        var a = new Sub { Ms = 1000, F = true };
        var b = new Sub { Throw = true };
        var c = new Sub { W = false, Ms = 10 };
        var offA = hub.Join(a); hub.Join(b); hub.Join(c);
        Assert.Equal(3, d.Changes);
        Assert.True(hub.Wanted());
        Assert.Equal(1000, hub.FastMs());   // c does not want, so its 10 ms does not count
        Assert.True(hub.Fed());
        hub.Publish(J("{\"procs\":[]}"));
        Assert.Single(a.Got);
        Assert.Empty(c.Got);
        offA.Dispose();
        Assert.Equal(4, d.Changes);
        Assert.False(hub.Wanted());
        Assert.Null(hub.FastMs());
        Assert.False(hub.Fed());   // nobody wants: not "fed"
        var stale = hub.Drive(new Driver());   // a second driver takes over
        off.Dispose();                          // the first one's handle no longer gives it back
        Assert.True(hub.Driven);
        stale.Dispose();
        Assert.False(hub.Driven);
        Assert.Equal(new[] { true, true, false }, flips);
    }

    [Fact]
    public void AFollower_ReadsOnItsOwnOnlyWhileNothingDrives_AndHearsEveryAnswerWhileItDoes() => HostLoop.Run(async (loop, t) =>
    {
        var hub = new GameWatch();
        var seen = new List<string>();
        var running = false;
        var f = new GameWatchFollower(hub, new GameWatchFollowerHooks
        {
            Active = () => true, Running = () => running, EveryMs = 1000, Clock = new HostClock(t),
            Read = () => Task.FromResult<JsonObject?>(J("{\"own\":true}")),
            Seen = r => { seen.Add(r?.ToJsonString() ?? "null"); return Task.CompletedTask; },
        });
        f.Start();
        for (var i = 0; i < 5; i++) { t.AdvanceMs(1000); loop.Drain(); }
        Assert.Equal(5, f.OwnReads);
        var d = new Driver();
        var off = hub.Drive(d);
        var before = d.Changes;
        for (var i = 0; i < 5; i++) { t.AdvanceMs(1000); loop.Drain(); }
        Assert.Equal(5, f.OwnReads);   // driven: no reads of its own
        hub.Publish(J("{\"from\":\"game mode\"}"));
        loop.Drain();
        Assert.Equal("{\"from\":\"game mode\"}", seen[^1]);
        running = true;                // a game started: the driver hears it and goes to every 1 s
        hub.Publish(J("{\"n\":1}"));
        loop.Drain();
        Assert.True(d.Changes > before);
        Assert.Equal(1000, hub.FastMs());
        off.Dispose();                 // game mode stopped: the follower's own loop again
        t.AdvanceMs(1000); loop.Drain();
        Assert.Equal(6, f.OwnReads);
        f.Stop();
        t.AdvanceMs(5000); loop.Drain();
        Assert.Equal(6, f.OwnReads);
        Assert.False(hub.Wanted());
    });

    [Fact]
    public void AnAnswerThatArrivesWhileACheckRuns_WaitsAndIsNotLost() => HostLoop.Run(async (loop, t) =>
    {
        var hub = new GameWatch();
        var gate = new GameWatchGate();
        var seen = new List<string>();
        var slow = new TaskCompletionSource();
        var f = new GameWatchFollower(hub, new GameWatchFollowerHooks
        {
            Active = () => true, Running = () => false, EveryMs = 1000, Clock = new HostClock(t), Gate = gate,
            Read = () => Task.FromResult<JsonObject?>(null),
            Seen = async r => { seen.Add(r?.ToJsonString() ?? "null"); if (seen.Count == 1) await slow.Task; },
        });
        hub.Drive(new Driver());
        f.Start();
        hub.Publish(J("{\"n\":1}"));
        loop.Drain();
        Assert.True(gate.Busy);
        hub.Publish(J("{\"n\":2}"));
        hub.Publish(J("{\"n\":3}"));   // only the newest waiting answer is kept
        loop.Drain();
        Assert.Single(seen);
        slow.SetResult();
        await Task.Delay(1);
        loop.Drain();
        Assert.Equal(new[] { "{\"n\":1}", "{\"n\":3}" }, seen);
        Assert.False(gate.Busy);
    });

    [Fact]
    public void AFeatureReachesTheOneWatchAndTheRest_ThroughItsContext() => HostLoop.Run(async (loop, t) =>
    {
        var rig = new Rig(t);
        IFeatureContext? ctx = null;
        rig.Host.Load(new[] { rig.D("gamemode", f => f.Install = c => { ctx = c; return ValueTask.CompletedTask; }) });
        await rig.Host.Installed;
        var s = Assert.IsAssignableFrom<IFeatureHostServices>(ctx);
        Assert.Same(rig.Host.GameWatch, s.GameWatch);
        var a = await s.RestAsync(true, "VALORANT runs");
        Assert.False(a.Ok);
        Assert.Equal("resting for a game is not part of this build yet", a.Reason);
    });
}
