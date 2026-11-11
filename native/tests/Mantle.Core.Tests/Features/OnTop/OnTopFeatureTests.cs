using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.OnTop;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.OnTop;

// The [Feature("ontop")] half (lib/features/ontop.js; BAR-335): Win+Ctrl+T flips "keep on top" for the window in front
// (sidecar ontop.front), default on, switchable in the tray (switched off: every pin released). On the feature host's
// test rig: a fake sidecar that is a made-up Windows, never the real one.
public class OnTopFeatureTests
{
    static void T(Func<Rig, Task> body, bool test) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D() => FeatureDescriptor.Of(typeof(OnTopFeature).GetCustomAttribute<FeatureAttribute>()!, () => new OnTopFeature());

    /// A made-up Windows: one window in front ("77", Notes), its "on top" mark kept here.
    static HashSet<string> Windows(Rig r)
    {
        var top = new HashSet<string>(StringComparer.Ordinal);
        static JsonElement J(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Platform.Handle("ontop.front", _ => PlatformReply.Success(J(new JsonObject { ["hwnd"] = "77", ["pid"] = 5, ["name"] = "Notes", ["exe"] = "notes.exe", ["topmost"] = top.Contains("77") })));
        r.Platform.Handle("ontop.info", a => {
            string h = a.TryGetProperty("hwnd", out var x) ? x.ToString() : "0";
            return PlatformReply.Success(J(new JsonObject { ["hwnd"] = h, ["pid"] = 5, ["name"] = "Notes", ["exe"] = "notes.exe", ["topmost"] = top.Contains(h) }));
        });
        r.Platform.Handle("ontop.set", a => {
            string h = a.GetProperty("hwnd").ToString();
            bool on = a.GetProperty("on").GetBoolean(), was = top.Contains(h);
            if (on) top.Add(h); else top.Remove(h);
            return PlatformReply.Success(J(new JsonObject { ["hwnd"] = h, ["was"] = was, ["topmost"] = on, ["changed"] = was != on, ["tries"] = 1 }));
        });
        r.Platform.Handle("ontop.check", a => {
            var ws = new JsonArray();
            if (a.TryGetProperty("hwnds", out var arr))
                foreach (var w in arr.EnumerateArray()) {
                    string h = w.TryGetProperty("hwnd", out var x) ? x.ToString() : "";
                    ws.Add((JsonNode)new JsonObject { ["hwnd"] = h, ["alive"] = true, ["topmost"] = top.Contains(h) });
                }
            return PlatformReply.Success(J(new JsonObject { ["windows"] = ws }));
        });
        return top;
    }

    static JsonElement[] Toasts(Rig r) => r.Events.Published.Where(p => p.Name == OnTopFeature.ToastEvent).Select(p => (JsonElement)p.Payload!).ToArray();
    static JsonElement? LastState(Rig r) => r.Events.Published.Where(p => p.Name == OnTopFeature.StateEvent).Select(p => (JsonElement?)p.Payload).LastOrDefault();
    static int Pinned(Rig r) => LastState(r) is { } s && s.TryGetProperty("pinned", out var p) ? p.GetArrayLength() : 0;

    static async Task Settle(Rig r, Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    [Fact]
    public void The_tooltip_line_says_kept_on_top_or_why_the_bar_cannot_tell_never_nothing_for_an_app_with_windows()
    {
        Assert.Equal("\nKept on top · right-click or Win+Ctrl+T to stop", OnTopWords.TipLine(true, true, null));
        Assert.Equal("\nKept on top · right-click or Win+Ctrl+T to stop", OnTopWords.TipLine(true, true, OnTopWords.Cant));
        Assert.Equal("\nKeep on top: the bar can't show which windows are kept on top right now (restart Mantle to fix it)", OnTopWords.TipLine(false, true, OnTopWords.Cant));
        Assert.Equal("", OnTopWords.TipLine(false, false, OnTopWords.Cant));   // a pin with no window has nothing to keep on top
        Assert.Equal("", OnTopWords.TipLine(false, true, null));
        Assert.Equal("Keep on top: " + OnTopWords.Cant, OnTopWords.MenuCant(OnTopWords.Cant));
    }

    [Fact]
    public void The_result_pop_up_reads_the_reason_or_the_hint_and_holds_a_refusal_longer()
    {
        static JsonElement P(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Assert.Equal(396, OnTopToast.WindowW);
        Assert.Equal(80, OnTopToast.WindowH);
        Assert.Equal(4300, OnTopToast.BackstopMs);
        Assert.Equal(30000, OnTopToast.IdleMs);
        var pin = OnTopToast.Read(P("{\"text\":\"Kept on top: Notes\",\"reason\":null,\"tone\":\"ok\",\"did\":\"pinned\",\"hint\":\"Win+Ctrl+T again to stop\"}"))!;
        Assert.Equal("Win+Ctrl+T again to stop", pin.Line);
        Assert.True(pin.LineIsHint);
        Assert.Equal(1800, pin.HoldMs);
        var no = OnTopToast.Read(P("{\"text\":\"Can't keep it on top\",\"reason\":\"it is Windows' own\",\"tone\":\"warn\",\"did\":\"refused\",\"hint\":\"\"}"))!;
        Assert.Equal("it is Windows' own", no.Line);
        Assert.False(no.LineIsHint);
        Assert.Equal(3400, no.HoldMs);
        Assert.Null(OnTopToast.Read(P("{\"text\":\"\"}")));
    }

    [Fact]
    public void It_is_on_by_default_with_its_chord_in_the_tray_label()
    {
        var a = typeof(OnTopFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("ontop", a.Name);
        Assert.Equal("Keep a window on top (Win+Ctrl+T)", a.Label);
        Assert.True(a.DefaultOn);
        Assert.Contains("Win+Ctrl+T", a.Chords);
    }

    [Fact]
    public void Win_Ctrl_T_flips_the_window_in_front_and_switching_it_off_releases_every_pin() => T(async r =>
    {
        var top = Windows(r);
        r.Host.Load(new[] { D() });
        await r.Host.Installed;

        // the chord from the keyboard hook: the window in front goes on top
        r.Platform.Raise("chord", "{\"chord\":\"Win+Ctrl+T\"}");
        await Settle(r, () => Toasts(r).Length >= 1);
        Assert.Contains(r.Platform.Calls, c => c.Verb == "ontop.front");
        Assert.Contains(r.Platform.Calls, c => c.Verb == "ontop.set" && c.ArgsJson.Contains("\"on\":true", StringComparison.Ordinal));
        Assert.Contains("77", top);
        var t = Toasts(r).Last();
        Assert.Equal("Win+Ctrl+T again to stop", t.GetProperty("hint").GetString());
        await Settle(r, () => Pinned(r) == 1);
        Assert.Equal(1, Pinned(r));

        // again: it comes off
        r.Platform.Raise("chord", "{\"chord\":\"Win+Ctrl+T\"}");
        await Settle(r, () => Toasts(r).Length >= 2 && Pinned(r) == 0);
        Assert.DoesNotContain("77", top);
        Assert.Equal(0, Pinned(r));

        // on once more, then switched off in the tray: every pin is released
        r.Platform.Raise("chord", "{\"chord\":\"Win+Ctrl+T\"}");
        await Settle(r, () => Pinned(r) == 1);
        Assert.Contains("77", top);
        r.Host.SetEnabled("ontop", false, "the tray");
        await Settle(r, () => !top.Contains("77"));
        Assert.DoesNotContain("77", top);
        Assert.Contains(r.Lines, l => l.Contains("switched off in the tray", StringComparison.Ordinal));
        // switched off, the chord does nothing
        int sets = r.Platform.Calls.Count(c => c.Verb == "ontop.set");
        r.Platform.Raise("chord", "{\"chord\":\"Win+Ctrl+T\"}");
        await Settle(r, () => false);
        Assert.Equal(sets, r.Platform.Calls.Count(c => c.Verb == "ontop.set"));
        Assert.DoesNotContain("77", top);
    }, test: false);

    [Fact]
    public void In_a_test_run_the_chord_is_a_dry_run_that_changes_no_window() => T(async r =>
    {
        var top = Windows(r);
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        r.Host.FireChord(OnTopFeature.Chord, "a test");
        await Settle(r, () => Toasts(r).Length >= 1);
        Assert.Contains(r.Platform.Calls, c => c.Verb == "ontop.front");
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "ontop.set");
        Assert.Empty(top);
        Assert.Equal("Dry run: would keep Notes on top", Toasts(r).Last().GetProperty("text").GetString());
    }, test: true);
}
