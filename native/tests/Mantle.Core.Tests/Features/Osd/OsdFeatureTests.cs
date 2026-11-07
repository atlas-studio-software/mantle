using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Osd;
using Mantle.Core.Features.Toasts;
using Xunit;

namespace Mantle.Core.Tests.Features.Osd;

// The [Feature("osd")] half (lib/features/osd.js commands and its show/hide/faded/backstop rules) on the feature
// host's test rig: a fake sidecar, the test bus and events. The surface (Mantle.App/Surfaces/Osd) is the UI tests'.
public class OsdFeatureTests
{
    static void T(Func<Rig, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D(Rig r, Action<OsdFeature>? made = null)
    {
        var a = typeof(OsdFeature).GetCustomAttribute<FeatureAttribute>()!;
        return FeatureDescriptor.Of(a, () => { var f = new OsdFeature(new HostToastClock(new HostClock(r.Time, SynchronizationContext.Current))); made?.Invoke(f); return f; });
    }

    static Rig Sidecar(Rig r, double level = 40)
    {
        r.Platform.Start();
        r.Platform.Answer("osd.qs", "{\"qsOpen\":false}");
        r.Platform.Handle("osd.volume", a => {
            bool up = a.TryGetProperty("op", out var op) && op.GetString() == "up";
            double from = a.TryGetProperty("from", out var f) && f.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : level;
            double to = up ? Math.Floor(from / 2) * 2 + 2 : Math.Ceiling(from / 2) * 2 - 2;
            return Mantle.Core.Platform.PlatformReply.Success(FeatureOptions.Parse($"{{\"level\":{to},\"muted\":false,\"ms\":3,\"from\":{{\"level\":{from},\"muted\":false}}}}"));
        });
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        return r;
    }

    static OsdShow[] Shows(Rig r) => r.Events.Published.Where(p => p.Name == "osd.show").Select(p => (OsdShow)p.Payload!).ToArray();
    static JsonObject? Args(string json) => JsonNode.Parse(json) as JsonObject;

    [Fact]
    public void The_module_fields_are_Electrons()
    {
        var a = typeof(OsdFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("osd", a.Name);
        Assert.Equal("Volume and brightness pop-up", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Equal(new[] { "VolumeUp", "VolumeDown", "VolumeMute" }, a.Chords);
        Assert.Empty(a.Observe);
    }

    [Fact]
    public void A_test_run_registers_the_surface_commands_and_testShow_and_takes_no_keys_from_Windows() => T(async r =>
    {
        Sidecar(r);
        r.Host.Load(new[] { D(r) });
        await r.Host.Installed;
        Assert.True(r.Hub.Piece("osd").Ok);
        foreach (var c in new[] { "osd.size", "osd.faded", "osd.show", "osd.mixer", "osd.state", "osd.testShow", "osd.testHide" })
            Assert.True(r.Bus.Has(c), c + " is not registered");
        await r.Host.ApplyLiveAsync("the sidecar is ready");
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "hotkey");   // the takeover is a --live run's only
    });

    [Fact]
    public void A_live_run_takes_the_three_volume_keys_through_the_hosts_hotkey_call_and_has_no_test_commands() => T(async r =>
    {
        Sidecar(r);
        r.Host.Load(new[] { D(r) });
        await r.Host.Installed;
        Assert.False(r.Bus.Has("osd.testShow"));
        Assert.False(r.Bus.Has("osd.testHide"));
        await r.Host.ApplyLiveAsync("the sidecar is ready");
        r.Time.AdvanceMs(100);
        var hk = r.Platform.Calls.LastOrDefault(c => c.Verb == "hotkey");
        Assert.NotNull(hk);
        foreach (var k in new[] { "VolumeUp", "VolumeDown", "VolumeMute" }) Assert.Contains(k, hk!.ArgsJson);
    }, test: false);

    [Fact]
    public void testShow_shows_a_made_up_reading_held_up_and_the_fade_race_keeps_a_newer_show() => T(async r =>
    {
        Sidecar(r);
        OsdFeature? f = null;
        r.Host.Load(new[] { D(r, x => f = x) });
        await r.Host.Installed;
        var a = await r.Bus.CallAsync("osd.testShow", Args("{\"level\":62}"));
        Assert.True(a.Ok, a.Error);
        var s = Shows(r).Last();
        Assert.Equal(("volume", 62.0, false, true, "testShow", 1), (s.Kind, s.State.Level!.Value, s.State.Muted, s.Hold, s.Via, s.Seq));
        Assert.True(f!.Shown);
        await r.Bus.CallAsync("osd.testShow", Args("{\"level\":30,\"muted\":true}"));
        Assert.Equal(2, Shows(r).Last().Seq);
        Assert.True(Shows(r).Last().State.Muted);
        // the surface's "faded" for show 1 crossed show 2 in flight: ignored, still shown
        var old = await r.Bus.CallAsync("osd.faded", Args("{\"seq\":1}"));
        Assert.Contains("a newer pop-up (2) is showing", old.Result.GetRawText());
        Assert.True(f.Shown);
        await r.Bus.CallAsync("osd.faded", Args("{\"seq\":2}"));
        Assert.False(f.Shown);
        // dead: the reason reaches the surface
        await r.Bus.CallAsync("osd.testShow", Args("{\"dead\":true}"));
        Assert.False(Shows(r).Last().State.Ok);
        Assert.Equal(OsdModel.NoDevice, Shows(r).Last().State.Reason);
        await r.Bus.CallAsync("osd.testHide");
        Assert.False(f.Shown);
        Assert.Contains(r.Events.Published, p => p.Name == "osd.hide" && ((OsdHide)p.Payload!).Why == "testHide");
    });

    [Fact]
    public void The_backstop_hides_a_popup_the_surface_never_said_faded() => T(async r =>
    {
        Sidecar(r);
        OsdFeature? f = null;
        r.Host.Load(new[] { D(r, x => f = x) });
        await r.Host.Installed;
        await r.Bus.CallAsync("osd.show", Args("{\"state\":{\"level\":50},\"via\":\"bar\"}"));
        Assert.Equal(("bar", 50.0), (Shows(r).Last().Via, Shows(r).Last().State.Level!.Value));
        r.Time.AdvanceMs(OsdFeature.BackstopMs - 10);
        Assert.True(f!.Shown);
        r.Time.AdvanceMs(20);
        Assert.False(f.Shown);
        Assert.Contains(r.Events.Published, p => p.Name == "osd.hide" && ((OsdHide)p.Payload!).Why.StartsWith("backstop", StringComparison.Ordinal));
    });

    [Fact]
    public void A_volume_key_in_a_test_run_is_a_dry_step_shown_on_the_popup() => T(async r =>
    {
        Sidecar(r, level: 40);
        r.Host.Load(new[] { D(r) });
        await r.Host.Installed;
        r.Host.FireChord("VolumeUp", "a test");
        for (int i = 0; i < 20 && Shows(r).Length < 1; i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
        var steps = r.Platform.Calls.Where(c => c.Verb == "osd.volume").Select(c => c.ArgsJson).ToArray();
        Assert.All(steps, j => Assert.Contains("\"dry\":true", j));   // the user's volume is never touched in --test
        Assert.NotEmpty(Shows(r));
        Assert.Contains(r.Lines, l => l.Contains("your volume was not touched", StringComparison.Ordinal));
    });

    [Fact]
    public void size_is_clamped_and_logged_in_a_test_run() => T(async r =>
    {
        Sidecar(r);
        r.Host.Load(new[] { D(r) });
        await r.Host.Installed;
        var a = await r.Bus.CallAsync("osd.size", Args("{\"w\":5000,\"h\":10,\"open\":true,\"at\":\"10,20\"}"));
        Assert.Contains("\"w\":600", a.Result.GetRawText());
        Assert.Contains("\"h\":40", a.Result.GetRawText());
        Assert.Contains(r.Lines, l => l.Contains("page drew 600x40 (panel open) at 10,20", StringComparison.Ordinal));
        var st = await r.Bus.CallAsync("osd.state");
        Assert.Contains("\"size\":{\"w\":600,\"h\":40}", st.Result.GetRawText());
    });
}
