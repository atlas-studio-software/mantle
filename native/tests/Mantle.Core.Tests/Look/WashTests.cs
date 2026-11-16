using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// look-sync's test/wash.test.js (look-sync 3fa3800 .. 44f9ad5), case for case: the same inputs and the same expected
/// outputs, so native Mantle computes the same tint as Quartz and Helm (CONTRACT "2026-11-11: sharp painted glass +
/// tint override"). Plus the dimAuto key in checkValue and in LookHost (kept on write, set by the Dim slider / Auto).
/// </summary>
public class WashTests
{
    // const glass = (dark, light) => ({ dark: {blurPx 0, dim .30, panel 0, ...dark}, light: {...}, render: 'picture' })
    static JsonObject Glass(string? dark = null, string? light = null)
    {
        JsonObject Side(string? extra)
        {
            var o = new JsonObject { ["blurPx"] = 0, ["dim"] = 0.30, ["panel"] = 0 };
            if (extra != null) foreach (var kv in (JsonObject)JsonNode.Parse(extra)!) o[kv.Key] = kv.Value?.DeepClone();
            return o;
        }
        return new JsonObject { ["dark"] = Side(dark), ["light"] = Side(light), ["render"] = "picture" };
    }
    static JsonObject Look(JsonObject glass) => new() { ["glass"] = glass };
    // const desk = (guard, extra) => ({ rev 4, ok true, id 'snow', still 'C:/x/still.png', guard, ...extra })
    static JsonObject Desk(JsonObject? guard, string? extra = null)
    {
        var d = new JsonObject { ["rev"] = 4, ["ok"] = true, ["id"] = "snow", ["still"] = "C:/x/still.png" };
        if (guard != null) d["guard"] = guard;
        if (extra != null) foreach (var kv in (JsonObject)JsonNode.Parse(extra)!) d[kv.Key] = kv.Value?.DeepClone();
        return d;
    }
    static JsonObject Band(double alpha, double dim, double blurPx, double contrast = 4.4) => new() {
        ["alpha"] = alpha, ["dim"] = dim, ["blurPx"] = blurPx, ["contrast"] = contrast,
        ["region"] = new JsonObject { ["topDip"] = 0, ["heightDip"] = 100 }, ["monitor"] = new JsonArray((JsonNode)1536, (JsonNode)864),
    };
    static JsonObject G(string theme, JsonObject band) => new() { [theme] = band };

    [Fact]
    public void dimAuto_is_optional_true_false_accepted_anything_else_names_the_key()
    {
        Assert.Null(LookSyncCore.CheckValue("glass", Glass("{\"dimAuto\":true}", "{\"dimAuto\":false}")));
        Assert.Matches("glass\\.dark\\.dimAuto must be true or false", LookSyncCore.CheckValue("glass", Glass("{\"dimAuto\":\"yes\"}")));
        Assert.Matches("glass\\.light\\.dimAuto", LookSyncCore.CheckValue("glass", Glass(null, "{\"dimAuto\":1}")));
        Assert.Matches("glass\\.dark\\.dimAuto", LookSyncCore.CheckValue("glass", Glass("{\"dimAuto\":null}")));   // JS: null !== undefined
        Assert.Null(LookSyncCore.CheckValue("glass", Glass()));   // absent = automatic
    }

    [Fact]
    public void automatic_a_valid_guard_above_Dim_raises_the_wash_and_says_so()
    {
        var r = Wash.Alpha(Look(Glass()), Desk(G("dark", Band(0.46, 0.30, 0))), "dark");
        Assert.Equal(0.46, r.Alpha);
        Assert.Equal("auto", r.Mode);
        Assert.True(r.Raised);
        Assert.Equal(4.4, r.Contrast);
        Assert.True(r.Guarded);
        Assert.Equal("Automatic: 0.30, raised to 0.46 so text stays readable over this picture", r.Text);
    }

    [Fact]
    public void automatic_a_valid_guard_at_Dim_leaves_it_with_the_reason()
    {
        var r = Wash.Alpha(Look(Glass()), Desk(G("light", Band(0.30, 0.30, 0))), "light");
        Assert.Equal(0.30, r.Alpha);
        Assert.False(r.Raised);
        Assert.True(r.Guarded, "the band was valid, it just needed no raise");
        Assert.StartsWith("Automatic: 0.30, text already reads over this picture", r.Text);
    }

    [Fact]
    public void automatic_max_dim_guard_a_guard_below_Dim_never_lowers_it()
    {
        var r = Wash.Alpha(Look(Glass("{\"dim\":0.40}")), Desk(G("dark", Band(0.32, 0.40, 0))), "dark");
        Assert.Equal(0.40, r.Alpha);
        Assert.False(r.Raised);
    }

    [Fact]
    public void manual_exactly_Dim_the_guard_ignored_even_a_high_one()
    {
        var r = Wash.Alpha(Look(Glass("{\"dim\":0.20,\"dimAuto\":false}")), Desk(G("dark", Band(0.80, 0.20, 0))), "dark");
        Assert.Equal((0.20, "manual", false), (r.Alpha, r.Mode, r.Raised));
        Assert.Equal("Manual: 0.20", r.Text);
        Assert.False(r.Guarded);
        var t = Wash.Alpha(Look(Glass("{\"dimAuto\":false}", "{\"dim\":0.55}")), Desk(G("light", Band(0.70, 0.55, 0))), "light");
        Assert.Equal("auto", t.Mode);   // dimAuto is per theme
        Assert.Equal(0.70, t.Alpha);
    }

    [Fact]
    public void dimAuto_true_behaves_as_absent()
    {
        var a = Wash.Alpha(Look(Glass("{\"dimAuto\":true}")), Desk(G("dark", Band(0.5, 0.3, 0))), "dark");
        var b = Wash.Alpha(Look(Glass()), Desk(G("dark", Band(0.5, 0.3, 0))), "dark");
        Assert.Equal(b, a);
    }

    [Fact]
    public void automatic_falls_back_to_Dim_with_a_reason_for_every_unusable_guard()
    {
        var L = Look(Glass());
        var cases = new (JsonObject? D, string Theme, string Re)[] {
            (null, "dark", "no desktop picture from Mantle"),
            (Desk(null, "{\"ok\":false,\"still\":null,\"reason\":\"slideshow\"}"), "dark", @"Mantle has no desktop picture \(slideshow\)"),
            (Desk(null, "{\"still\":null}"), "dark", "names no picture"),
            (Desk(null), "dark", "has not published a top band"),
            (Desk(null, "{\"guardReason\":\"decode failed\"}"), "dark", @"couldn't work out the top band \(decode failed\)"),
            (Desk(G("dark", Band(0.5, 0.3, 0))), "light", "not published the light top band yet"),
            (Desk(G("dark", Band(1.5, 0.3, 0))), "dark", "is not an alpha"),
            (Desk(G("dark", new JsonObject { ["alpha"] = 0.5, ["blurPx"] = 0 })), "dark", "names no Dim"),
            (Desk(G("dark", new JsonObject { ["alpha"] = 0.5, ["dim"] = 0.3 })), "dark", "names no blur"),
            (Desk(G("dark", Band(0.5, 0.5, 0))), "dark", @"worked out the dark top band for Dim 0\.50 and blur 0 px, not this Dim 0\.30 and blur 0 px"),
            (Desk(G("dark", Band(0.5, 0.3, 5))), "dark", @"for Dim 0\.30 and blur 5 px, not this Dim 0\.30 and blur 0 px"),
            (Desk(G("dark", Band(0.95, 0.3, 0))), "dark", @"above the 0\.88 ceiling"),
        };
        foreach (var (d, theme, re) in cases) {
            var r = Wash.Alpha(L, d, theme);
            Assert.True(r.Alpha == 0.30, re);
            Assert.False(r.Raised, re);
            Assert.Equal("auto", r.Mode);
            Assert.False(r.Guarded, re);
            Assert.Matches(re, r.Reason);
            Assert.StartsWith("Automatic: 0.30, not raised: ", r.Text);
        }
    }

    [Fact]
    public void opts_unusable_the_apps_own_reason_wins_over_a_valid_guard()
    {
        var r = Wash.Alpha(Look(Glass()), Desk(G("dark", Band(0.6, 0.3, 0))), "dark", "Look Sync is off");
        Assert.Equal(0.30, r.Alpha);
        Assert.Equal("Automatic: 0.30, not raised: Look Sync is off", r.Text);
    }

    [Fact]
    public void shapes_shared_entries_unwrap_a_missing_or_bad_glass_uses_the_seed_Dim_and_says_so_never_throws()
    {
        var shared = new JsonObject { ["glass"] = new JsonObject { ["v"] = Glass("{\"dim\":0.35}"), ["rev"] = 3, ["by"] = "mantle" } };
        Assert.Equal(0.35, Wash.Alpha(shared, Desk(G("dark", Band(0.35, 0.35, 0))), "dark").Alpha);
        foreach (var look in new[] { null, "null", "{}", "{\"glass\":7}", "{\"glass\":{\"dark\":{\"dim\":\"x\"}}}" }) {
            var r = Wash.Alpha(look == null ? null : JsonNode.Parse(look), null, "dark");
            Assert.Equal(0.30, r.Alpha);
            Assert.Matches(@"glass\.dark\.dim is missing or outside 0\.20-0\.90, so the default 0\.30", r.Reason);
        }
        var odd = Wash.Alpha(Look(Glass()), JsonNode.Parse("{\"guard\":\"x\",\"still\":\"a\"}"), "nonsense");
        Assert.Equal("auto", odd.Mode);
        Assert.Equal(0.6, Wash.Alpha(Look(Glass(null, "{\"dim\":0.6}")), null, "light").Alpha);
    }

    [Fact]
    public void the_guard_republishes_at_blur_0_match_equal_to_2_decimals_as_Mantle_checks_it()
    {
        var r = Wash.Alpha(Look(Glass("{\"dim\":0.3}")), Desk(G("dark", Band(0.464, 0.3000001, 0))), "dark");
        Assert.Equal(0.46, r.Alpha);
        Assert.True(r.Raised);
    }

    [Fact]
    public void a2_of_a_non_number_alpha_is_its_JSON_as_in_the_module()
    {
        // 'Mantle\'s dark top band ' + a2(e.alpha): JSON.stringify for a non-number, "undefined" when absent
        Assert.Contains("top band \"0.5\" is not an alpha", Wash.Alpha(Look(Glass()), Desk(G("dark", new JsonObject { ["alpha"] = "0.5", ["dim"] = 0.3, ["blurPx"] = 0 })), "dark").Reason);
        Assert.Contains("top band undefined is not an alpha", Wash.Alpha(Look(Glass()), Desk(G("dark", new JsonObject { ["dim"] = 0.3, ["blurPx"] = 0 })), "dark").Reason);
    }

    // ---- LookHost: dimAuto read and written, the wash's words ----------------------------------------------------

    sealed class Rig : IDisposable
    {
        public readonly TestDir Dir = new("wash");
        public readonly ListLog Log = new();
        public readonly Stores Stores;
        public readonly LookHost Host;
        public Rig()
        {
            Stores = new Stores(Dir.Path, new SafeFile(WriteGate.Scratch(Dir.Path)), Log);
            var opts = new LookSyncOptions { App = "mantle", Dir = Dir.File("look-sync"), Files = new SafeFile(WriteGate.Scratch(Dir.Path)) };
            Host = new LookHost(Stores.Open("look-sync.json"), opts, new LookTheme(() => "dark", () => "dark", _ => null), Log);
        }
        public void Dispose() { Host.Dispose(); Stores.Dispose(); Dir.Dispose(); }
    }

    [Fact]
    public async Task LookHost_the_Dim_slider_writes_manual_Auto_switches_back_and_dimAuto_survives_other_writes()
    {
        using var rig = new Rig();
        rig.Host.Start();
        await rig.Host.Sync.WhenIdle();
        var w = rig.Host.Wash("dark");
        Assert.Equal("auto", w.Mode);   // absent = automatic
        Assert.Equal("Automatic: 0.50, not raised: no desktop picture from Mantle", w.Text);   // Mantle's own seed Dim (.50) until native seeds the new look

        var a = await rig.Host.SetDimByHandAsync(0.20, "dark");
        Assert.True(a["ok"]!.GetValue<bool>(), a.ToJsonString());
        Assert.Equal("Manual: 0.20", rig.Host.Wash("dark").Text);
        Assert.Equal("auto", rig.Host.Wash("light").Mode);   // per theme
        Assert.Contains("look: dark glass set in Mantle: blur 5 dim 0.2 panel 0 dimAuto false (Manual: 0.20)", string.Join("\n", rig.Log.Lines));

        // another write (the panel) keeps dimAuto: the clone of the theme entry carries it
        await rig.Host.SetGlassAsync(new JsonObject { ["panel"] = 0.1, ["look"] = "dark" });
        Assert.Equal("manual", rig.Host.Wash("dark").Mode);
        string local = File.ReadAllText(rig.Dir.File("look-sync.json"));
        Assert.Matches("\"dimAuto\":\\s*false", local);
        await rig.Host.Sync.WhenIdle();
        string shared = File.ReadAllText(Path.Combine(rig.Dir.File("look-sync"), "look.json"));
        Assert.Matches("\"dimAuto\":\\s*false", shared);   // shared through look.json for Quartz and Helm

        await rig.Host.SetDimAutoAsync("dark");
        var back = rig.Host.Wash("dark");
        Assert.Equal("auto", back.Mode);
        Assert.Equal(0.20, back.Dim);   // the value stays

        var bad = await rig.Host.SetGlassAsync(new JsonObject { ["dimAuto"] = "yes", ["look"] = "dark" });
        Assert.False(bad["ok"]!.GetValue<bool>());
        Assert.Equal("dimAuto must be true or false", bad["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task LookHost_DesktopStill_names_the_records_picture_or_says_why_there_is_none()
    {
        using var rig = new Rig();
        rig.Host.Start();
        await rig.Host.Sync.WhenIdle();
        var none = rig.Host.DesktopStill();
        Assert.Null(none.Still);
        Assert.False(string.IsNullOrEmpty(none.Why));
        var rec = new JsonObject { ["rev"] = 1, ["ok"] = true, ["id"] = "test", ["still"] = "C:/x/still.png" };
        var wr = await rig.Host.Sync.PublishDesktopAsync(rec);
        Assert.True(wr.Ok, wr.Reason);
        var s = rig.Host.DesktopStill();
        Assert.Equal("C:/x/still.png", s.Still);
        Assert.Null(s.Why);
    }
}
