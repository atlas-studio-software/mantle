using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// topbar.pingTest / topbar.pingTests (lib/features/topbar.js: checkArgs, the pingtest wiring and the history pushes) on
/// the feature rig: the page's requests are checked first, a start goes to the sidecar's topbar.pingtest (on demand only),
/// each `pingtest` push is forwarded without its "ev", and a finished run joins the history.
/// </summary>
public sealed partial class TopBarFeatureTests
{
    [Fact]
    public void Ping_test_requests_are_checked_first_TOP_141_153_159()
    {
        string? Why(string verb, string json) => TopBarFeature.CheckPingArgs(verb, El(json)).Reason;
        string Args(string verb, string json) => TopBarFeature.CheckPingArgs(verb, El(json)).Args!.ToJsonString();
        Assert.Equal("{\"action\":\"start\"}", Args("pingTest", "{\"action\":\"start\",\"test\":{\"rounds\":1}}"));
        Assert.Equal("Mantle does not know that ping test request", Why("pingTest", "{\"action\":\"explode\"}"));
        Assert.Equal("Mantle does not know that ping test history request", Why("pingTests", "{}"));
        Assert.Equal("{\"action\":\"list\"}", Args("pingTests", "{\"action\":\"list\",\"id\":\"x\"}"));
        Assert.Equal("that test could not be identified; open the history again", Why("pingTests", "{\"action\":\"delete\",\"id\":\"../x\"}"));
        Assert.Equal("{\"action\":\"delete\",\"id\":\"1760000000000-3\"}", Args("pingTests", "{\"action\":\"delete\",\"id\":\"1760000000000-3\"}"));
        Assert.Equal("{\"action\":\"save\",\"id\":\"1-1\",\"name\":\"upstairs\"}", Args("pingTests", "{\"action\":\"save\",\"id\":\"1-1\",\"name\":\" upstairs \"}"));
        Assert.Equal("a name needs 1 to 60 characters", Why("pingTests", "{\"action\":\"rename\",\"id\":\"1-1\",\"name\":\"\"}"));
    }

    [Fact]
    public void A_start_goes_to_the_sidecar_and_a_finished_push_joins_the_history_TOP_141_143_159() => T(async (r, s) =>
    {
        await Up(r, s);
        Assert.Equal(0, Calls(r, "topbar.pingtest"));   // nothing at rest
        r.Platform.Answer("topbar.pingtest", "{\"run\":1,\"phase\":\"starting\",\"running\":true}");
        var st = await r.Bus.CallAsync("topbar.pingTest", new JsonObject { ["action"] = "start", ["extra"] = 1 });
        Assert.Equal("{\"action\":\"start\"}", r.Platform.Calls.Last(c => c.Verb == "topbar.pingtest").ArgsJson);
        Assert.Equal("starting", st.Result.GetProperty("phase").GetString());

        r.Platform.Raise("pingtest", "{\"ev\":\"pingtest\",\"run\":1,\"phase\":\"done\",\"running\":false,\"startedAt\":1760000000000,\"endedAt\":1760000030000,"
            + "\"rows\":[{\"kind\":\"router\",\"avg\":2.4,\"got\":20,\"sent\":20}],\"down\":{\"state\":\"ok\",\"mbps\":62},\"up\":{\"state\":\"ok\",\"mbps\":30}}");
        await Tick(r, 100);
        var run = (JsonElement)r.Events.Published.Last(p => p.Name == "topbar.pingtest").Payload!;
        Assert.False(run.TryGetProperty("ev", out _));
        Assert.Equal("done", run.GetProperty("phase").GetString());
        var hist = (JsonElement)r.Events.Published.Last(p => p.Name == "topbar.pingtests").Payload!;
        Assert.Equal("1760000000000-1", hist.GetProperty("tests")[0].GetProperty("id").GetString());
        // the rig gave the history no folder: kept in memory, and it says so
        Assert.StartsWith("history could not be saved: the ping test history was given no place", hist.GetProperty("problem").GetString());

        // the same run pushed again is not a second entry; save names it and pushes the history
        r.Platform.Raise("pingtest", "{\"run\":1,\"phase\":\"done\",\"startedAt\":1760000000000}");
        await Tick(r, 100);
        var saved = await r.Bus.CallAsync("topbar.pingTests", new JsonObject { ["action"] = "save", ["id"] = "1760000000000-1", ["name"] = "upstairs" });
        Assert.True(saved.Result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, saved.Result.GetProperty("history").GetProperty("tests").GetArrayLength());
        Assert.Equal("upstairs", saved.Result.GetProperty("history").GetProperty("tests")[0].GetProperty("name").GetString());
        var bad = await r.Bus.CallAsync("topbar.pingTests", new JsonObject { ["action"] = "save", ["id"] = "1-1", ["name"] = "x" });
        Assert.Equal("that test is no longer in the history", bad.Result.GetProperty("reason").GetString());
        var refused = await r.Bus.CallAsync("topbar.pingTest", new JsonObject { ["action"] = "frob" });
        Assert.Equal("Mantle does not know that ping test request", refused.Result.GetProperty("reason").GetString());
    });
}
