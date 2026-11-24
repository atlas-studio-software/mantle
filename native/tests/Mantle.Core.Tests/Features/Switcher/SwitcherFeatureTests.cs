using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Switcher;
using Xunit;

namespace Mantle.Core.Tests.Features.Switcher;

// lane r9-switcher: the [Feature("switcher")] service (lib/switcher.js start, applyHook, onEvent, COMMANDS, the
// settings switches) on the feature host's test rig: a fake sidecar and a fake overlay, never the real ones.
// SWI-122, SWI-123 (the store half), SWI-127, SWI-137, SWI-140, SWI-144 and the hand-back with no surface.
public class SwitcherFeatureTests
{
    sealed class Overlay : ISwitcherOverlay
    {
        public readonly List<string> Calls = new();
        public bool Alive => true;
        public string? Place(int x, int y, int w, int h, bool show) { Calls.Add(show ? "show" : "place"); return null; }
        public void Send(string name, object payload) => Calls.Add(name);
        public ThumbResult SyncThumbs(IReadOnlyList<PreviewBox> boxes, double dpr, int originX, int originY) { Calls.Add("thumbs"); return new(true, null, boxes.Count); }
        public void ClearThumbs(string why) => Calls.Add("clear");
        public double ScaleFor(PhysRect mon) => 1;
        public IReadOnlyList<PhysRect> Displays => new[] { new PhysRect(0, 0, 1920, 1080) };
        public PhysRect PrimaryMon => new(0, 0, 1920, 1080);
        public PhysRect PrimaryWork => new(0, 0, 1920, 1040);
    }

    static void T(Func<Rig, Task> body, bool test = false) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D(SwitcherFeature.Host h) =>
        FeatureDescriptor.Of(typeof(SwitcherFeature).GetCustomAttribute<FeatureAttribute>()!, () => new SwitcherFeature(h));

    static async Task Settle(Rig r, Func<bool> done, int rounds = 200)
    {
        for (int i = 0; i < rounds && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    static IEnumerable<string> Asks(Rig r) =>
        r.Platform.Calls.Where(c => c.Verb == "hotkey" && c.ArgsJson.Contains("\"altTab\"", StringComparison.Ordinal)).Select(c => c.ArgsJson);

    static async Task<Mantle.Core.Commands.CommandResult> Call(Rig r, string verb, string args = "{}") =>
        await r.Bus.CallAsync("switcher." + verb, JsonNode.Parse(args) as JsonObject);

    // The overlay rigs come with a bar that opens Win+N / Win+A; the bare one has neither (the app today).
    static SwitcherFeature.Host H(Rig r, Overlay? o = null, Func<bool?>? alt = null)
    {
        var clock = new HostClock(r.Time);
        return new SwitcherFeature.Host(o, () => 4, alt, () => clock.NowMs, clock, OnWinKey: o is null ? null : _ => { });
    }

    static async Task Up(Rig r, SwitcherFeature.Host h)
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"installed\":true,\"altTab\":true,\"winKeys\":true,\"tracking\":true,\"reason\":null}");
        r.Host.Load(new[] { D(h) });
        await r.Host.Installed;
        await Settle(r, () => Asks(r).Any());
    }

    [Fact]
    public void The_feature_is_switcher_on_by_default_rests_for_games_and_takes_no_host_chord()
    {
        var a = typeof(SwitcherFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("switcher", a.Name);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Empty(a.Chords);                                  // Alt+Tab is the sidecar's own switch, not a host chord
        Assert.Empty(a.Observe);
    }

    [Fact]
    public void With_no_surface_and_no_bar_panels_Alt_Tab_and_Win_N_A_are_asked_back_for_Windows() => T(async r =>
    {
        await Up(r, H(r));
        Assert.Equal("{\"altTab\":false,\"winKeys\":false}", Asks(r).Last());
        var st = await Call(r, "status");
        Assert.True(st.Ok);
        Assert.Equal(SwitcherFeature.NoOverlay, st.Result.GetProperty("overlay").GetString());
        Assert.Equal(SwitcherFeature.NoOverlay, st.Result.GetProperty("page").GetProperty("reason").GetString());
        // The page cannot say ready for a window that does not exist.
        await Call(r, "ready");
        await Settle(r, () => false);
        Assert.DoesNotContain("{\"altTab\":true,\"winKeys\":true}", Asks(r));
    });

    [Fact]
    public void A_ready_page_takes_Alt_Tab_and_turning_the_feature_off_hands_both_keys_back() => T(async r =>
    {
        var o = new Overlay();
        await Up(r, H(r, o));
        Assert.Equal("{\"altTab\":false,\"winKeys\":true}", Asks(r).Last());   // page not ready yet
        await Call(r, "ready");
        await Settle(r, () => Asks(r).Last() == "{\"altTab\":true,\"winKeys\":true}");
        Assert.Equal("{\"altTab\":true,\"winKeys\":true}", Asks(r).Last());
        Assert.True(r.Host.TrySetEnabled("switcher", false, "the tray menu").Ok);
        await Settle(r, () => Asks(r).Last() == "{\"altTab\":false,\"winKeys\":false}");
        Assert.Equal("{\"altTab\":false,\"winKeys\":false}", Asks(r).Last());
    });

    [Fact]
    public void A_page_that_went_down_ends_the_session_and_hands_Alt_Tab_back() => T(async r =>
    {
        var o = new Overlay();
        await Up(r, H(r, o));
        await Call(r, "ready");
        await Settle(r, () => Asks(r).Last() == "{\"altTab\":true,\"winKeys\":true}");
        r.Platform.Raise("switcher", "{\"op\":\"open\",\"seq\":5,\"sel\":0,\"list\":[{\"hwnd\":11,\"title\":\"a\"}]}");
        await Settle(r, () => o.Calls.Contains("switcher.session"));
        await Call(r, "down", "{\"reason\":\"the switcher page stopped responding\"}");
        await Settle(r, () => Asks(r).Last() == "{\"altTab\":false,\"winKeys\":true}");
        Assert.Equal("{\"altTab\":false,\"winKeys\":true}", Asks(r).Last());
        Assert.Contains("clear", o.Calls);
        var st = await Call(r, "status");
        Assert.True(st.Result.GetProperty("page").GetProperty("failed").GetBoolean());
    });

    [Fact]
    public void Settings_refuse_an_empty_patch_and_a_change_is_stored_and_asked_for() => T(async r =>
    {
        var o = new Overlay();
        await Up(r, H(r, o));
        await Call(r, "ready");
        var bad = await Call(r, "set", "{\"altTab\":\"no\"}");
        Assert.False(bad.Result.GetProperty("ok").GetBoolean());
        Assert.Equal(SwitcherFeature.EmptyPatch, bad.Result.GetProperty("reason").GetString());
        var off = await Call(r, "set", "{\"altTab\":false}");
        Assert.True(off.Result.GetProperty("ok").GetBoolean());
        Assert.False(off.Result.GetProperty("altTab").GetBoolean());
        Assert.Equal("false", r.Stores.Get("features\\switcher.json").Snapshot().GetProperty("altTab").GetRawText());
        await Settle(r, () => Asks(r).Last() == "{\"altTab\":false,\"winKeys\":true}");
        Assert.Equal("{\"altTab\":false,\"winKeys\":true}", Asks(r).Last());
        Assert.True(r.Logged("Mantle's Alt+Tab off"));
        var same = await Call(r, "set", "{\"altTab\":false}");
        Assert.True(same.Result.GetProperty("ok").GetBoolean());
    });

    [Fact]
    public void A_saved_switch_that_is_not_true_or_false_reads_as_on_with_a_stated_reason()
    {
        var s = new MemoryStore("features\\switcher.json");
        s.SetAsync("altTab", JsonValue.Create("off")).AsTask().Wait();
        s.SetAsync("winKeys", JsonValue.Create(false)).AsTask().Wait();
        var (settings, note) = SwitcherFeature.ReadSettings(s);
        Assert.Equal(new SwitcherSettings(true, false), settings);
        Assert.Equal("the saved altTab was not true or false, so it is at the default (on)", note);
        var p = SwitcherFeature.Patch(JsonDocument.Parse("{\"altTab\":true,\"winKeys\":1}").RootElement);
        Assert.True(p.AltTab);
        Assert.Null(p.WinKeys);
        Assert.Null(p.Error);
        Assert.Equal(SwitcherFeature.EmptyPatch, SwitcherFeature.Patch(JsonDocument.Parse("{}").RootElement).Error);
    }

    [Fact]
    public void A_sidecar_session_is_drawn_and_the_stuck_overlay_guard_takes_it_down_when_the_sidecar_goes_quiet() => T(async r =>
    {
        var o = new Overlay();
        bool alt = true;
        await Up(r, H(r, o, () => alt));
        await Call(r, "ready");
        await Settle(r, () => Asks(r).Last() == "{\"altTab\":true,\"winKeys\":true}");
        r.Platform.Raise("switcher", "{\"op\":\"open\",\"seq\":3,\"sel\":1,\"ms\":200,\"list\":[{\"hwnd\":11,\"title\":\"a\"},{\"hwnd\":12,\"title\":\"b\"}]}");
        await Settle(r, () => o.Calls.Contains("switcher.session"));
        Assert.Contains("switcher.session", o.Calls);
        var st = await Call(r, "status");
        var win = SwitcherLayout.Layout(2, 1920, 1080);
        var painted = await Call(r, "painted", $"{{\"seq\":3,\"vw\":{win.W},\"vh\":{win.H},\"dpr\":1,\"boxes\":[{{\"hwnd\":\"11\",\"x\":1,\"y\":1,\"w\":50,\"h\":30}}]}}");
        Assert.True(painted.Ok);
        Assert.Equal(1, painted.Result.GetProperty("thumbs").GetInt32());
        await Settle(r, () => o.Calls.Contains("show"));
        Assert.Contains("show", o.Calls);
        Assert.Equal(3, st.Result.GetProperty("session").GetProperty("seq").GetInt32());
        // The sidecar never says commit or cancel; Alt goes up.
        alt = false;
        await Settle(r, () => o.Calls.Contains("clear"), rounds: 600);
        Assert.True(r.Logged("Alt has been up for 600 ms and the sidecar never said this Alt+Tab ended"));
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "hotkey" && c.ArgsJson.Contains("\"pick\"", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Lines, l => l.Contains("\"a\"", StringComparison.Ordinal));
    });

    [Fact]
    public void Quitting_ends_the_session_clears_previews_and_asks_Alt_Tab_back() => T(async r =>
    {
        var o = new Overlay();
        await Up(r, H(r, o) with { HandBack = a => r.Platform.CallAsync("hotkey", a) });
        await Call(r, "ready");
        r.Platform.Raise("switcher", "{\"op\":\"open\",\"seq\":4,\"sel\":0,\"list\":[{\"hwnd\":11,\"title\":\"a\"}]}");
        await r.Host.StopAllAsync("Mantle is quitting");
        Assert.Contains("clear", o.Calls);
        Assert.Contains("switcher.end", o.Calls);
        Assert.Equal("{\"altTab\":false,\"winKeys\":false}", Asks(r).Last());
    });
}
