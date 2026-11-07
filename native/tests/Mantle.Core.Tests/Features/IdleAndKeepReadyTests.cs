using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Xunit;

namespace Mantle.Core.Tests.Features;

// lib/features/idle-unload.test.js (unit half) and lib/keep-ready.test.js, ported as the spec.
public class IdleUnloadTests
{
    static (IdleUnloader U, ManualTime T, List<string> Lines) Make(bool test = true, double waitMs = 60)
    {
        var t = new ManualTime();
        var lines = new List<string>();
        return (new IdleUnloader(HostClock.Inline(t), lines.Add, test, TimeSpan.FromMilliseconds(waitMs)), t, lines);
    }

    [Fact]
    public void TheLiveWaitIs10Minutes_AndTheOverrideIsTestOnly()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), IdleUnloader.WaitFor(false, TimeSpan.FromMilliseconds(60)));
        Assert.Equal(TimeSpan.FromMilliseconds(60), IdleUnloader.WaitFor(true, TimeSpan.FromMilliseconds(60)));
        Assert.Equal(TimeSpan.FromMinutes(10), IdleUnloader.WaitFor(true, null));
        Assert.Equal(TimeSpan.FromMinutes(10), IdleUnloader.WaitFor(true, TimeSpan.Zero));
    }

    [Fact]
    public void AUseRestartsTheWait_UnusedForTheWaitIsUnloadedOnce_AndLoggedWithWhy()
    {
        var (u, t, lines) = Make();
        var went = 0;
        var w = u.Watch(new IdleTarget("panel A", _ => { went++; return true; }));
        w.Touch();
        t.AdvanceMs(40);
        w.Touch();
        t.AdvanceMs(40);
        Assert.Equal(0, went);
        t.AdvanceMs(30);
        Assert.Equal(1, went);
        t.AdvanceMs(200);
        Assert.Equal(1, went);
        Assert.Contains(lines, l => l.StartsWith("panel A: unloaded (unused for 60 ms)"));
    }

    [Fact]
    public void BusyIsNeverUnloaded_AndABusyThatThrowsCountsAsBusy()
    {
        var (u, t, lines) = Make();
        var busy = true; var went = 0; var has = true;
        var w = u.Watch(new IdleTarget("panel", _ => { went++; var had = has; has = false; return had; }, () => busy));
        w.Touch();
        t.AdvanceMs(200);
        Assert.Equal(0, went);
        Assert.Equal(1, t.PendingTimers);   // the wait started again
        var thrower = u.Watch(new IdleTarget("thrower", _ => { went++; return true; }, () => throw new InvalidOperationException("no idea")));
        Assert.Empty(u.UnloadAll("now"));
        Assert.Equal(0, went);
        Assert.Contains(lines, l => l.Contains("thrower: could not tell whether it is in use (no idea); kept"));
        busy = false;
        thrower.Stop();
        Assert.Equal(new[] { "panel" }, u.UnloadAll("--test-unload"));
        Assert.Empty(u.UnloadAll("--test-unload"));   // nothing left to unload: not named
    }

    [Fact]
    public void StopEndsTheWait_AndATouchAfterStopArmsNothing()
    {
        var (u, t, _) = Make();
        var went = 0;
        var w = u.Watch(new IdleTarget("x", _ => { went++; return true; }));
        w.Touch();
        w.Stop();
        w.Touch();
        Assert.Equal(0, t.PendingTimers);
        t.AdvanceMs(500);
        Assert.Equal(0, went);
        Assert.Empty(u.UnloadAll("now"));
    }

    [Fact]
    public void AnUnloadThatThrows_IsSaidInWords_TriedAgainLater_AndUnloadAllCarriesOn()
    {
        var (u, t, lines) = Make();
        u.Watch(new IdleTarget("panel B", _ => throw new InvalidOperationException("destroy blew up")));
        u.Watch(new IdleTarget("panel C", _ => true));
        Assert.Equal(new[] { "panel C" }, u.UnloadAll("now"));
        Assert.Contains(lines, l => l.Contains("panel B: could not be unloaded (destroy blew up)"));
        Assert.Equal(1, t.PendingTimers);
    }
}

public class KeepReadyTests
{
    [Fact]
    public void DefaultsOff_SetSavesAllFourKeys_LogsWithoutPaths_AndTellsListeners() => HostLoop.Run(async (_, _) =>
    {
        var store = new MemoryStore(KeepReady.FileName);
        var lines = new List<string>();
        var k = new KeepReady(store, lines.Add);
        Assert.All(KeepReady.Surfaces, s => Assert.False(k.Get(s.Surface)));
        var heard = new List<string>();
        k.OnChange((s, on) => heard.Add($"{s}={on}"));
        var a = await k.SetAsync("files", true);
        Assert.True(a.Ok && a.On);
        Assert.True(k.Get("files"));
        Assert.Equal(new[] { "files=True" }, heard);
        var doc = store.Snapshot();
        Assert.Equal(4, doc.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.True, doc.GetProperty("keepReady.files").ValueKind);
        Assert.Equal(JsonValueKind.False, doc.GetProperty("keepReady.taskview").ValueKind);
        Assert.Contains(lines, l => l.Contains("Files kept ready in the background"));
        Assert.DoesNotContain(lines, l => l.Contains(":\\"));
        await k.SetAsync("files", true);
        Assert.Single(heard);   // no change, no word
    });

    [Fact]
    public void AnUnknownSurfaceIsRefusedInWords_AndOnlyAnExactTrueCounts() => HostLoop.Run(async (_, _) =>
    {
        var store = new MemoryStore(KeepReady.FileName);
        store.Put("keepReady.settings", JsonValue.Create("yes"));
        var k = new KeepReady(store);
        Assert.False(k.Get("settings"));
        var bad = await k.SetAsync("desktop", true);
        Assert.False(bad.Ok);
        Assert.Contains("not a window Mantle can keep ready", bad.Reason);
    });

    [Fact]
    public void ABrokenFileMeansEveryKeyOff_SaidInWords()
    {
        var store = new MemoryStore(KeepReady.FileName) { State = ReadState.Corrupt, Reason = "it is not valid JSON; kept as keep-ready.json.unreadable" };
        var lines = new List<string>();
        var k = new KeepReady(store, lines.Add);
        Assert.False(k.Get("settings"));
        Assert.Contains("did not hold these switches", k.Reason);
        Assert.Contains(lines, l => l.Contains("keep-ready.json.unreadable"));
    }

    [Fact]
    public void AFailedSaveKeepsTheOldValue_AndSaysSo() => HostLoop.Run(async (_, _) =>
    {
        var store = new MemoryStore(KeepReady.FileName) { FailWrites = "access denied" };
        var a = await new KeepReady(store).SetAsync("taskmgr", true);
        Assert.False(a.Ok);
        Assert.False(a.On);
        Assert.Equal("the switch could not be saved (access denied), so it stays off", a.Reason);
    });

    [Fact]
    public void View_OneRowPerSurface_WithItsWordsAndCost_IncludingTaskView()
    {
        var rows = new KeepReady(new MemoryStore(KeepReady.FileName)).View();
        Assert.Equal(new[] { "settings", "taskmgr", "files", "taskview" }, rows.Select(r => r.Surface));
        Assert.All(rows, r => Assert.Matches("^Keep .+ ready$", r.Label));
        Assert.All(rows, r => Assert.Matches(@"holds about \d+ MB of memory all the time", r.Help));
        Assert.Contains("Win+I opens it instantly", rows[0].Help);
        Assert.Equal("keepReady.taskview", rows[3].Key);
        Assert.Contains("Win+Tab opens it instantly", rows[3].Help);
    }

    [Fact]
    public void NoProfileFolder_EverySurfaceOpensFresh()
    {
        var k = new KeepReady(null);
        Assert.False(k.Get("files"));
        Assert.Contains("no folder", k.Reason);
    }
}
