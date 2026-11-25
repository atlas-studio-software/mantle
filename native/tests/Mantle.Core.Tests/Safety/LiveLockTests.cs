using System;
using System.Collections.Generic;
using System.Threading;
using Mantle.Core.Safety;
using Xunit;

namespace Mantle.Core.Tests.Safety;

/// lane r12-boot: the lock native holds while it is the live shell (Local\Mantle.LiveShell). A named mutex is
/// re-entrant on its owning thread, so every second take here runs on ANOTHER thread. Each test uses its own
/// name, never the real one, so a Mantle running on the same machine is never touched.
public class LiveLockTests
{
    static string Fresh() => @"Local\Mantle.LiveShell.test-" + Guid.NewGuid().ToString("N")[..12];

    /// Run <paramref name="body"/> on its own thread and wait for it (a mutex belongs to a thread).
    static void OnThread(Action body)
    {
        Exception? failed = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { failed = e; } }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "the thread did not finish");
        if (failed != null) throw new Exception("on the other thread: " + failed.Message, failed);
    }

    [Fact]
    public void The_name_is_the_per_session_contract_Electron_will_check()
        => Assert.Equal(@"Local\Mantle.LiveShell", LiveLock.MutexName);

    [Fact]
    public void A_second_take_from_another_thread_is_refused_in_words_and_a_release_lets_the_next_take_in()
    {
        string name = Fresh();
        var first = LiveLock.TryTake(out var why1, name: name);
        Assert.NotNull(first);
        Assert.Null(why1);
        Assert.Null(first!.Note);

        string? refusal = null; bool took = true;
        OnThread(() => { var h = LiveLock.TryTake(out refusal, name: name); took = h != null; h?.Dispose(); });
        Assert.False(took);
        Assert.NotNull(refusal);
        Assert.Contains("another Mantle is already the live shell in this session", refusal);
        Assert.Contains(name, refusal);
        Assert.Contains("--quit", refusal);

        first.Dispose();
        first.Dispose();   // twice is harmless

        string? after = "unset"; bool tookAfter = false; string? note = "unset";
        OnThread(() => { using var h = LiveLock.TryTake(out after, name: name); tookAfter = h != null; note = h?.Note; });
        Assert.True(tookAfter, "after the release the next take must succeed: " + after);
        Assert.Null(after);
        Assert.Null(note);
    }

    [Fact]
    public void An_abandoned_lock_counts_as_taken_by_us_with_a_log_line()
    {
        string name = Fresh();
        LiveLock.Holder? dead = null;
        // the "previous Mantle": takes the lock and its thread ends without letting go
        OnThread(() => { dead = LiveLock.TryTake(out _, name: name); });
        Assert.NotNull(dead);

        var log = new List<string>();
        LiveLock.Holder? mine = null; string? refusal = "unset";
        OnThread(() => {
            mine = LiveLock.TryTake(out refusal, log.Add, name);
            mine?.Dispose();
        });
        Assert.NotNull(mine);
        Assert.Null(refusal);
        Assert.NotNull(mine!.Note);
        Assert.Contains("abandoned", mine.Note);
        Assert.Single(log);
        Assert.Contains("taken over", log[0]);
        dead!.Dispose();   // from a thread that does not own it: no throw
    }
}
