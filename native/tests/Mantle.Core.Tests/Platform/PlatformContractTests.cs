using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Platform;

/// The IPlatform contract compiles, and the fake keeps the client rules lib/sidecar.js keeps.
public class PlatformContractTests
{
    [Fact]
    public async Task A_call_before_start_is_refused_in_plain_words_never_thrown()
    {
        IPlatform p = new FakePlatform();
        Assert.Equal(PlatformState.Starting, p.Status.State);
        var r = await p.CallAsync("tbhide", new JsonObject { ["on"] = true });
        Assert.False(r.Ok);
        Assert.Equal(PlatformOutcome.Refused, r.Outcome);
        Assert.StartsWith("cannot run \"tbhide\": ", r.Error);
        Assert.Equal(JsonValueKind.Undefined, r.Result.ValueKind);
    }

    [Fact]
    public async Task A_ready_platform_answers_with_a_result_and_records_the_call()
    {
        var fake = new FakePlatform().Answer("appbar", "{\"reserved\":true}");
        fake.Start();
        IPlatform p = fake;
        var r = await p.CallAsync("appbar", new JsonObject { ["edge"] = "bottom" }, TimeSpan.FromSeconds(2));
        Assert.True(r.Ok, r.Error);
        Assert.True(r.Result.GetProperty("reserved").GetBoolean());
        var call = Assert.Single(fake.Calls);
        Assert.Equal("appbar", call.Verb);
        Assert.Equal("{\"edge\":\"bottom\"}", call.ArgsJson);
        Assert.Equal(TimeSpan.FromSeconds(2), call.Timeout);
    }

    [Fact]
    public async Task Unknown_verbs_and_reserved_keys_fail_with_the_sidecars_words()
    {
        var fake = new FakePlatform();
        fake.Start();
        var unknown = await fake.CallAsync("nope.verb");
        Assert.Equal(PlatformOutcome.Failed, unknown.Outcome);
        Assert.Equal("unknown command: nope.verb", unknown.Error);
        Assert.Equal(PlatformDefaults.CallTimeout, fake.Calls[0].Timeout);

        var reserved = await fake.CallAsync("tray.click", new JsonObject { ["id"] = 3 });
        Assert.Equal(PlatformOutcome.Refused, reserved.Outcome);
        Assert.Contains("reserved key \"id\"", reserved.Error);
    }

    [Fact]
    public void Events_reach_their_subscribers_until_disposed()
    {
        var fake = new FakePlatform();
        var got = new List<PlatformEvent>();
        IDisposable sub = fake.On("hotkey", got.Add);
        fake.Raise("hotkey", "{\"chord\":\"Win+V\"}");
        fake.Raise("battery", "{\"percent\":50}");
        sub.Dispose();
        fake.Raise("hotkey", "{\"chord\":\"Win+S\"}");
        var e = Assert.Single(got);
        Assert.Equal("hotkey", e.Name);
        Assert.Equal("Win+V", e.Body.GetProperty("chord").GetString());
        Assert.Equal("hotkey", e.Body.GetProperty("ev").GetString());
        Assert.Equal(0, fake.Subscribers);
    }

    [Fact]
    public async Task OnReady_fires_now_when_ready_and_again_after_every_restart()
    {
        var fake = new FakePlatform();
        var statuses = new List<PlatformStatus>();
        int ready = 0;
        fake.OnStatus(statuses.Add);
        fake.OnReady(() => ready++);
        Assert.Equal(0, ready);
        fake.Start();
        Assert.Equal(1, ready);
        await fake.StopAsync("the sidecar exited; restarting in 3 s (attempt 1 of 3)");
        Assert.False(fake.Status.Ready);
        fake.Retry();
        Assert.Equal(2, ready);
        fake.OnReady(() => ready += 10);   // subscribing while ready fires at once
        Assert.Equal(12, ready);
        Assert.Equal(new[] { PlatformState.Ok, PlatformState.Down, PlatformState.Ok }, statuses.ConvertAll(s => s.State));
        Assert.All(statuses, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }

    [Fact]
    public void A_failed_reply_must_say_why()
    {
        Assert.Throws<ArgumentException>(() => PlatformReply.Fail(PlatformOutcome.Failed, " "));
        Assert.Throws<ArgumentException>(() => PlatformReply.Fail(PlatformOutcome.Ok, "fine"));
        Assert.Equal(new[] { "id", "cmd" }, PlatformDefaults.ReservedArgKeys);
        Assert.Equal(8, Enum.GetValues<PlatformLane>().Length);   // the eight lanes of sidecar/Lanes.cs
    }
}
