using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mantle.Core.Tests;

public class SingleInstanceTests
{
    static string FreshKey() => InstanceKey.For(@"X:\single-instance-test\" + Guid.NewGuid().ToString("N"));

    /// The mutex is per thread, so "another instance" is a claim from another thread.
    static T OnOtherThread<T>(Func<T> f)
    {
        T result = default!;
        var t = new Thread(() => result = f());
        t.Start();
        t.Join();
        return result;
    }

    [Fact]
    public void First_claims_second_is_refused_and_a_release_frees_it()
    {
        string key = FreshKey();
        using var owner = SingleInstance.TryClaim(key, out var why1);
        Assert.NotNull(owner);
        Assert.Contains("claimed", why1);

        var second = OnOtherThread(() => {
            var s = SingleInstance.TryClaim(key, out var why2);
            return (s, why2);
        });
        Assert.Null(second.s);
        Assert.Contains("another MantleNative", second.why2);

        owner!.Dispose();
        var third = OnOtherThread(() => {
            var s = SingleInstance.TryClaim(key, out _);
            bool got = s != null;
            s?.Dispose();
            return got;
        });
        Assert.True(third);
    }

    [Fact]
    public void Different_keys_do_not_collide()
    {
        using var a = SingleInstance.TryClaim(FreshKey(), out _);
        var b = OnOtherThread(() => { var s = SingleInstance.TryClaim(FreshKey(), out _); bool ok = s != null; s?.Dispose(); return ok; });
        Assert.NotNull(a);
        Assert.True(b);
    }

    [Fact]
    public void An_owner_that_died_holding_the_claim_is_taken_over_and_said()
    {
        string key = FreshKey();
        // a thread that takes the mutex and ends without releasing it = an abandoned mutex
        var t = new Thread(() => { var m = new Mutex(false, InstanceKey.MutexName(key)); m.WaitOne(); GC.KeepAlive(m); });
        t.Start(); t.Join();
        using var s = SingleInstance.TryClaim(key, out var why);
        Assert.NotNull(s);
        Assert.NotNull(s!.Note);
        Assert.Contains("without", why);
    }

    [Fact]
    public void A_second_launch_hands_its_args_to_the_owner()
    {
        string key = FreshKey();
        using var owner = SingleInstance.TryClaim(key, out _);
        Assert.NotNull(owner);
        var got = new BlockingCollection<string[]>();
        var errors = new ConcurrentQueue<string>();
        owner!.Listen(a => got.Add(a), e => errors.Enqueue(e));

        bool sent = SingleInstance.SendToOwner(key, new[] { "--test", "--profile-name=x", "--quit" }, TimeSpan.FromSeconds(5), out var why);
        Assert.True(sent, why);
        Assert.True(got.TryTake(out var argv, TimeSpan.FromSeconds(5)), "the owner never got the hand-off");
        Assert.Equal(new[] { "--test", "--profile-name=x", "--quit" }, argv);

        // and again: the listener keeps serving
        Assert.True(SingleInstance.SendToOwner(key, new[] { "--theme=dark" }, TimeSpan.FromSeconds(5), out why), why);
        Assert.True(got.TryTake(out argv, TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "--theme=dark" }, argv);
        Assert.Empty(errors);
    }

    [Fact]
    public void Sending_with_no_owner_fails_in_words_within_the_timeout()
    {
        var t0 = DateTime.UtcNow;
        bool sent = SingleInstance.SendToOwner(FreshKey(), new[] { "--quit" }, TimeSpan.FromMilliseconds(400), out var why);
        Assert.False(sent);
        Assert.Contains("did not answer", why);
        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_stops_the_listener()
    {
        string key = FreshKey();
        var owner = SingleInstance.TryClaim(key, out _);
        owner!.Listen(_ => { }, _ => { });
        await Task.Delay(100);
        owner.Dispose();
        Assert.False(SingleInstance.SendToOwner(key, new[] { "--quit" }, TimeSpan.FromMilliseconds(300), out _));
    }
}
