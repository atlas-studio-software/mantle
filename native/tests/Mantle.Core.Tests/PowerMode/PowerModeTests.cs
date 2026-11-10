using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.PowerMode;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.PowerMode;

/// <summary>[Feature("powermode")] (lib/features/powermode.js): the module fields, the three commands, the dry run of
/// a --test run, and the refusals in Electron's words. FakePlatform stands in for the sidecar.</summary>
public class PowerModeTests
{
    const string State = "{\"ok\":true,\"mode\":\"balanced\",\"inForce\":\"balanced\",\"available\":true,\"settable\":true,\"source\":\"ac\",\"saver\":false,\"plan\":{\"guid\":\"381b4222-f694-41f0-9685-ff5bb260df2e\",\"name\":\"Balanced\"},\"reason\":null}";

    static void T(Func<Rig, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));
    static FeatureDescriptor D() => FeatureDescriptor.Of(typeof(PowerModeFeature).GetCustomAttribute<FeatureAttribute>()!, () => new PowerModeFeature());
    static JsonObject? A(string json) => JsonNode.Parse(json) as JsonObject;

    static Rig Sidecar(Rig r)
    {
        r.Platform.Start();
        r.Platform.Answer("powermode.get", State);
        r.Platform.Answer("powermode.watch", "{\"ok\":true,\"state\":" + State + "}");
        r.Platform.Handle("powermode.set", a => {
            string mode = a.GetProperty("mode").GetString()!;
            bool dry = a.TryGetProperty("dryRun", out var d) && d.GetBoolean();
            return PlatformReply.Success(JsonDocument.Parse(dry
                ? $"{{\"ok\":true,\"dryRun\":true,\"same\":{(mode == "balanced" ? "true" : "false")},\"mode\":\"{mode}\",\"state\":{State},\"reason\":null}}"
                : $"{{\"ok\":true,\"mode\":\"{mode}\",\"state\":{State.Replace("\"mode\":\"balanced\"", $"\"mode\":\"{mode}\"")}}}").RootElement.Clone());
        });
        return r;
    }

    [Fact]
    public void The_module_fields_are_Electrons()
    {
        var a = typeof(PowerModeFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("powermode", "Power mode switch (quick settings)", true), (a.Name, a.Label, a.DefaultOn));
        Assert.Empty(a.Chords);
        Assert.False(a.Rest);
    }

    [Fact]
    public void A_test_run_sends_every_set_as_a_dry_run_and_a_change_is_not_ok() => T(async r =>
    {
        Sidecar(r);
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        foreach (var c in new[] { "powermode.get", "powermode.set", "powermode.plan" }) Assert.True(r.Bus.Has(c), c);
        var got = await r.Bus.CallAsync("powermode.get");
        Assert.Equal("balanced", got.Result.GetProperty("mode").GetString());
        var set = await r.Bus.CallAsync("powermode.set", A("{\"mode\":\"performance\"}"));
        Assert.True(set.Ok);
        Assert.False(set.Result.GetProperty("ok").GetBoolean());
        Assert.Equal(PowerModeFeature.TestRun, set.Result.GetProperty("reason").GetString());
        Assert.All(r.Platform.Calls.Where(c => c.Verb == "powermode.set"), c => Assert.Contains("\"dryRun\":true", c.ArgsJson));
        var same = await r.Bus.CallAsync("powermode.set", A("{\"mode\":\"balanced\"}"));
        Assert.True(same.Result.GetProperty("ok").GetBoolean());   // nothing to change: a plain ok
        Assert.True(r.Logged("set performance (dry run): refused (this is a test run, so Windows was not changed)"));
    });

    [Fact]
    public void A_live_set_is_sent_as_it_is_and_bad_arguments_are_refused_in_plain_words() => T(async r =>
    {
        Sidecar(r);
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        var set = await r.Bus.CallAsync("powermode.set", A("{\"mode\":\"efficiency\"}"));
        Assert.True(set.Result.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "powermode.set" && c.ArgsJson.Contains("dryRun"));
        var bad = await r.Bus.CallAsync("powermode.set", A("{\"mode\":\"turbo\"}"));
        Assert.Equal("that is not a power mode Windows has", bad.Result.GetProperty("reason").GetString());
        var plan = await r.Bus.CallAsync("powermode.plan", A("{\"plan\":\"not-a-guid\"}"));
        Assert.Equal("that is not a power plan on this PC", plan.Result.GetProperty("reason").GetString());
    }, test: false);

    [Fact]
    public void Every_push_is_the_state_event() => T(async r =>
    {
        Sidecar(r);
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        r.Platform.Raise("powermode", "{\"ev\":\"powermode\",\"ok\":true,\"mode\":\"performance\"}");
        var ev = r.Events.Published.Last(p => p.Name == PowerModeFeature.StateEvent);
        var body = (JsonElement)ev.Payload!;
        Assert.Equal("performance", body.GetProperty("mode").GetString());
        Assert.False(body.TryGetProperty("ev", out _));
    });
}
