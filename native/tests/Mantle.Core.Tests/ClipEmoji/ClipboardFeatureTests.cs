using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Clipboard;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.ClipEmoji;

// lane r9-clipemoji-2: the [Feature("clipboard")] service (lib/features/clipboard.js) on the feature host's test rig: a
// fake sidecar with made-up items, never the real one, no file and no real clipboard. The pins choice is handed in (no
// profile folder at all). No assertion or log check prints an item's text; the made-up text is checked to be ABSENT.
public class ClipboardFeatureTests
{
    const string Words = "made-up words 5a1";
    const string List = "{\"ok\":true,\"count\":2,\"pinned\":0,\"items\":[{\"id\":\"c2\",\"kind\":\"text\",\"text\":\"" + Words + "\",\"pinned\":false}," +
                        "{\"id\":\"c1\",\"kind\":\"text\",\"text\":\"older " + Words + "\",\"pinned\":false}]}";
    const string Arm = "{\"hwnd\":\"4242\",\"caret\":{\"x\":100,\"y\":200,\"h\":20},\"cursor\":{\"x\":5,\"y\":5}}";

    static void T(Func<Rig, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D(Rig r, bool warm = false, string? pinsFile = null) =>
        FeatureDescriptor.Of(typeof(ClipboardFeature).GetCustomAttribute<FeatureAttribute>()!,
            () => new ClipboardFeature(new ClipboardFeature.Host(() => new ClipPinsChoice(pinsFile, "test: pins as given"), new HostClock(r.Time), warm)));

    static async Task Settle(Rig r, Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    static int Count(Rig r, string verb) => r.Platform.Calls.Count(c => c.Verb == verb);
    static int Sent(Rig r, string ev) => r.Events.Published.Count(p => p.Name == ev);
    static JsonElement LastOf(Rig r, string ev) => (JsonElement)r.Events.Published.Last(p => p.Name == ev).Payload!;
    static bool Piece(Rig r, Func<Mantle.Core.State.FeaturePiece, bool> test) => r.Hub.Current.Features.TryGetValue("clipboard", out var p) && test(p);

    /// Started, with the panel up: the sidecar answers start, arm and list.
    static async Task Up(Rig r, bool warm = false, string mode = "history")
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Platform.Answer("clip.start", "{\"ok\":true,\"mode\":\"" + mode + "\",\"count\":2,\"pinned\":0}");
        r.Platform.Answer("clip.arm", Arm);
        r.Platform.Answer("clip.list", List);
        r.Platform.Answer("clip.stop", "{\"ok\":true,\"mode\":\"off\"}");
        r.Host.Load(new[] { D(r, warm) });
        await r.Host.Installed;
        var ready = await r.Bus.CallAsync("clipboard.ready");
        Assert.True(ready.Ok, ready.Error);
        await Settle(r, () => Piece(r, p => p.Ok));
    }

    static void WinV(Rig r) => r.Platform.Raise("chord", "{\"chord\":\"Win+V\"}");

    [Fact]
    public void The_feature_is_clipboard_on_by_default_rests_for_games_and_claims_only_Win_V()
    {
        var a = typeof(ClipboardFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("clipboard", a.Name);
        Assert.Equal("Clipboard history (Win+V)", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Equal(new[] { "Win+V" }, a.Chords);
        Assert.Empty(a.Observe);
        Assert.DoesNotContain("Ctrl+Alt+Shift+Backspace", a.Chords);   // the kill switch is never a feature's
    }

    // DEVIATION + CLIP-019: with no panel the feature is not healthy, so Win+V stays Windows' own, and nothing is recorded
    [Fact]
    public void With_no_panel_Win_V_stays_Windows_own_and_the_history_does_not_start() => T(async r =>
    {
        r.HookTakesAll();
        r.Platform.Start();
        r.Platform.Answer("clip.start", "{\"ok\":true,\"mode\":\"history\"}");
        r.Host.Load(new[] { D(r) });
        await r.Host.Installed;
        await r.Host.ApplyLiveAsync();
        Assert.Equal(0, Count(r, "clip.start"));
        Assert.True(Piece(r, p => !p.Ok && p.Reason == ClipboardFeature.NoPanel));
        Assert.Equal("Win+V: Windows' own until it is healthy again", r.Hub.Piece("clipboard").KeysReason);

        Assert.True((await r.Bus.CallAsync("clipboard.ready")).Ok);
        await Settle(r, () => Piece(r, p => p.Ok));
        r.Time.AdvanceMs(31);
        Assert.Equal(1, Count(r, "clip.start"));
        Assert.Equal("Win+V: Mantle's", r.Hub.Piece("clipboard").KeysReason);
    }, test: false);

    // CLIP-019, CLIP-020: clip.start gets the pins choice and listens only outside --test; its trouble is the health
    [Fact]
    public void Start_sends_the_pins_choice_and_listen_and_its_failures_are_the_health_in_words() => T(async r =>
    {
        r.Platform.Start();
        r.Platform.Answer("clip.start", "{\"ok\":true,\"mode\":\"off\",\"error\":\"the listener could not start\"}");
        r.Host.Load(new[] { D(r, pinsFile: @"T:\scratch\features\clipboard-pins.dat") });
        await r.Host.Installed;
        await r.Bus.CallAsync("clipboard.ready");
        await Settle(r, () => Count(r, "clip.start") > 0);
        var start = r.Platform.Calls.First(c => c.Verb == "clip.start");
        Assert.Contains("\"listen\":true", start.ArgsJson, StringComparison.Ordinal);
        Assert.Contains("clipboard-pins.dat", start.ArgsJson, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(5000), start.Timeout);
        await Settle(r, () => Piece(r, p => !p.Ok));
        Assert.True(Piece(r, p => p.Reason == "not recording copies: the listener could not start"));

        // the panel reports a crash: the page's trouble comes first
        await r.Bus.CallAsync("clipboard.ready", new JsonObject { ["ok"] = false, ["why"] = "the clipboard panel crashed; reopening it" });
        await Settle(r, () => Piece(r, p => p.Reason == "the clipboard panel crashed; reopening it"));
        Assert.True(Piece(r, p => !p.Ok && p.Reason == "the clipboard panel crashed; reopening it"));
    }, test: false);

    [Fact]
    public void An_older_sidecar_without_clip_start_is_named_as_too_old_and_a_test_run_does_not_listen() => T(async r =>
    {
        r.Platform.Start();
        r.Host.Load(new[] { D(r) });   // no clip.start: the fake answers "unknown command"
        await r.Host.Installed;
        await r.Bus.CallAsync("clipboard.ready");
        await Settle(r, () => Piece(r, p => !p.Ok && p.Reason != ClipboardFeature.NoPanel));
        Assert.True(Piece(r, p => p.Reason == "the sidecar is too old for clipboard history (it has no clip.start)"));
        Assert.Contains("\"listen\":false", r.Platform.Calls.First(c => c.Verb == "clip.start").ArgsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("pinsFile", r.Platform.Calls.First(c => c.Verb == "clip.start").ArgsJson, StringComparison.Ordinal);
    });

    // CLIP-001: Win+V opens (arm first, then the list) and Win+V again closes
    [Fact]
    public void Win_V_opens_with_arm_then_list_and_Win_V_again_closes() => T(async r =>
    {
        await Up(r);
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        int arm = r.Platform.Calls.FindIndex(c => c.Verb == "clip.arm");
        int list = r.Platform.Calls.FindLastIndex(c => c.Verb == "clip.list");
        Assert.True(arm >= 0 && list > arm, "clip.arm goes out before the list");
        Assert.Equal(TimeSpan.FromMilliseconds(800), r.Platform.Calls[arm].Timeout);
        var show = LastOf(r, ClipboardFeature.ShowEvent);
        Assert.Equal(2, show.GetProperty("list").GetProperty("count").GetInt32());
        Assert.Equal("4242", show.GetProperty("arm").GetProperty("hwnd").GetString());

        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.HideEvent) == 1);
        Assert.Equal("toggle", LastOf(r, ClipboardFeature.HideEvent).GetProperty("why").GetString());
        Assert.Equal(1, Sent(r, ClipboardFeature.ShowEvent));
    });

    // CLIP-002: a second Win+V while the first open still waits on the sidecar cancels it; nothing is shown
    [Fact]
    public void A_second_Win_V_while_opening_cancels_the_open_and_nothing_shows() => T(async r =>
    {
        await Up(r);
        bool again = true;
        r.Platform.Handle("clip.arm", _ => { if (again) { again = false; WinV(r); } return PlatformReply.Success(JsonDocument.Parse(Arm).RootElement.Clone()); });
        WinV(r);
        await Settle(r, () => !again);
        r.Time.AdvanceMs(500);
        Assert.Equal(0, Sent(r, ClipboardFeature.ShowEvent));
        Assert.Equal(0, Sent(r, ClipboardFeature.HideEvent));   // it was never shown, so nothing flashes
    });

    // CLIP-003: Esc while the panel is still opening means it never appears
    [Fact]
    public void Esc_while_opening_means_the_panel_never_appears() => T(async r =>
    {
        await Up(r);
        bool esc = true;
        r.Platform.Handle("clip.arm", _ => { if (esc) { esc = false; _ = r.Bus.CallAsync("clipboard.close"); } return PlatformReply.Success(JsonDocument.Parse(Arm).RootElement.Clone()); });
        WinV(r);
        await Settle(r, () => !esc);
        r.Time.AdvanceMs(500);
        Assert.Equal(0, Sent(r, ClipboardFeature.ShowEvent));
    });

    // CLIP-056: a failed arm still opens, and the panel is told in words that choosing will copy, not paste
    [Fact]
    public void A_failed_arm_opens_anyway_and_says_choosing_will_copy_but_not_paste() => T(async r =>
    {
        await Up(r);
        r.Platform.Handle("clip.arm", _ => PlatformReply.Fail(PlatformOutcome.TimedOut, "no answer in 800 ms"));
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        Assert.Equal("Mantle could not tell which window you came from (no answer in 800 ms), so choosing an item will copy it but not paste it.",
            LastOf(r, ClipboardFeature.ShowEvent).GetProperty("arm").GetProperty("error").GetString());
    });

    // CLIP-005, CLIP-038: a copy while open redraws in place; while closed the list is read once, 400 ms after copies stop
    [Fact]
    public void A_copy_while_open_redraws_in_place_and_one_while_closed_is_read_once_after_400_ms() => T(async r =>
    {
        await Up(r, mode: "history");
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        r.Platform.Raise("clip", "{\"what\":\"changed\",\"rev\":9,\"count\":3,\"why\":\"copy\"}");
        await Settle(r, () => Sent(r, ClipboardFeature.ChangedEvent) == 1);
        Assert.Equal(2, LastOf(r, ClipboardFeature.ChangedEvent).GetProperty("list").GetProperty("count").GetInt32());

        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.HideEvent) == 1);
    }, test: false);

    [Fact]
    public void Copies_while_closed_are_read_once_after_they_stop_and_the_next_open_uses_that_list() => T(async r =>
    {
        await Up(r);
        int before = Count(r, "clip.list");
        r.Platform.Raise("clip", "{\"what\":\"changed\"}");
        r.Time.AdvanceMs(200);
        r.Platform.Raise("clip", "{\"what\":\"changed\"}");
        r.Time.AdvanceMs(399);
        Assert.Equal(before, Count(r, "clip.list"));
        r.Time.AdvanceMs(2);
        await Settle(r, () => Count(r, "clip.list") == before + 1);
        Assert.Equal(before + 1, Count(r, "clip.list"));
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        Assert.Equal(before + 1, Count(r, "clip.list"));   // the cached list: no clip.list on the open's path
    }, test: false);

    // CLIP-007: ~4 s after start, arm + list once and the hidden panel draws it; nothing is shown
    [Fact]
    public void The_warm_up_runs_once_4_s_after_start_and_shows_nothing() => T(async r =>
    {
        await Up(r, warm: true);
        r.Time.AdvanceMs(3900);   // Up's settling took a few 5 ms steps at most
        Assert.Equal(0, Sent(r, ClipboardFeature.WarmEvent));
        r.Time.AdvanceMs(200);
        await Settle(r, () => Sent(r, ClipboardFeature.WarmEvent) == 1);
        Assert.Equal(1, Count(r, "clip.arm"));
        Assert.Equal(0, Sent(r, ClipboardFeature.ShowEvent));
        r.Time.AdvanceMs(10000);
        Assert.Equal(1, Sent(r, ClipboardFeature.WarmEvent));
    });

    // CLIP-018: switching off hides the panel and tells the sidecar to stop; a game rest only hides it
    [Fact]
    public void Switching_off_hides_and_stops_the_history_but_a_game_rest_leaves_it_listening() => T(async r =>
    {
        await Up(r);
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        await r.Host.RestAllAsync("a game runs");
        Assert.Equal("rest", LastOf(r, ClipboardFeature.HideEvent).GetProperty("why").GetString());
        Assert.Equal(0, Count(r, "clip.stop"));
        await r.Host.WakeAllAsync("the game exited");
        await r.Host.Installed;
        await r.Bus.CallAsync("clipboard.ready");
        await Settle(r, () => Piece(r, p => p.Ok && !p.Resting));

        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 2);
        r.Host.SetEnabled("clipboard", false, "the tray menu");
        await Settle(r, () => Count(r, "clip.stop") == 1);
        Assert.Equal("off", LastOf(r, ClipboardFeature.HideEvent).GetProperty("why").GetString());
        WinV(r);
        r.Time.AdvanceMs(100);
        Assert.Equal(2, Sent(r, ClipboardFeature.ShowEvent));
    });

    // CLIP-047, CLIP-044, CLIP-039: Clear all, delete and pin reach the sidecar and answer with the list read again
    [Fact]
    public void Clear_all_delete_and_pin_reach_the_sidecar_and_answer_with_a_fresh_list() => T(async r =>
    {
        await Up(r);
        r.Platform.Answer("clip.clear", "{\"ok\":true,\"removed\":2}");
        r.Platform.Answer("clip.delete", "{\"ok\":true}");
        r.Platform.Answer("clip.pin", "{\"ok\":true}");
        int lists = Count(r, "clip.list");

        var clear = await r.Bus.CallAsync("clipboard.clear");
        Assert.True(clear.Ok, clear.Error);
        Assert.Equal(1, Count(r, "clip.clear"));
        Assert.Equal(lists + 1, Count(r, "clip.list"));
        Assert.Equal(2, clear.Result.GetProperty("count").GetInt32());

        Assert.True((await r.Bus.CallAsync("clipboard.delete", new JsonObject { ["item"] = "c2" })).Ok);
        Assert.Contains(r.Platform.Calls, c => c.Verb == "clip.delete" && c.ArgsJson == "{\"item\":\"c2\"}");
        Assert.True((await r.Bus.CallAsync("clipboard.pin", new JsonObject { ["item"] = "c1", ["pinned"] = true })).Ok);
        Assert.Contains(r.Platform.Calls, c => c.Verb == "clip.pin" && c.ArgsJson == "{\"item\":\"c1\",\"pinned\":true}");

        var bad = await r.Bus.CallAsync("clipboard.delete", new JsonObject { ["item"] = "../c1" });
        Assert.False(bad.Ok);
        Assert.Equal("no item was chosen", bad.Error);
        Assert.Equal(1, Count(r, "clip.delete"));
    });

    // CLIP-049, CLIP-057: a paste hides the panel first and, in a test instance, is a dry run aimed at the armed window
    [Fact]
    public void A_paste_hides_first_and_is_a_dry_run_in_a_test_instance() => T(async r =>
    {
        await Up(r);
        r.Platform.Answer("clip.paste", "{\"ok\":true,\"dryRun\":true,\"formats\":[{\"name\":\"CF_UNICODETEXT\",\"bytes\":36}],\"roundTrip\":true}");
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        var res = await r.Bus.CallAsync("clipboard.paste", new JsonObject { ["item"] = "c2" });
        Assert.True(res.Ok, res.Error);
        Assert.True(res.Result.GetProperty("dryRun").GetBoolean());
        Assert.Equal("paste", LastOf(r, ClipboardFeature.HideEvent).GetProperty("why").GetString());
        var call = r.Platform.Calls.Last(c => c.Verb == "clip.paste");
        Assert.Contains("\"hwnd\":\"4242\"", call.ArgsJson, StringComparison.Ordinal);
        Assert.Contains("\"dryRun\":true", call.ArgsJson, StringComparison.Ordinal);
        Assert.True(r.Logged("dry-run paste: CF_UNICODETEXT"));
    });

    // CLIP-053, CLIP-054: a failed paste says "Not pasted"; a copy that could not be typed says "Copied, not pasted"
    [Fact]
    public void A_failed_paste_and_a_copied_not_pasted_one_each_show_a_notice_that_goes_after_6_s() => T(async r =>
    {
        await Up(r);
        r.Platform.Handle("clip.paste", _ => PlatformReply.Fail(PlatformOutcome.Failed, "could not put the item on the clipboard: another app held it"));
        var failed = await r.Bus.CallAsync("clipboard.paste", new JsonObject { ["item"] = "c2" });
        Assert.False(failed.Ok);
        var n = LastOf(r, ClipboardFeature.NoticeEvent);
        Assert.Equal("Not pasted", n.GetProperty("title").GetString());
        Assert.Equal("could not put the item on the clipboard: another app held it", n.GetProperty("text").GetString());
        Assert.Equal(6000, n.GetProperty("ms").GetInt32());

        r.Platform.Answer("clip.paste", "{\"ok\":true,\"copied\":true,\"pasted\":false,\"reason\":\"the window you came from is gone\"}");
        Assert.True((await r.Bus.CallAsync("clipboard.paste", new JsonObject { ["item"] = "c2" })).Ok);
        Assert.Equal("Copied, not pasted", LastOf(r, ClipboardFeature.NoticeEvent).GetProperty("title").GetString());
        int hides = Sent(r, ClipboardFeature.HideEvent);
        r.Time.AdvanceMs(6001);
        Assert.Equal(hides + 1, Sent(r, ClipboardFeature.HideEvent));
        Assert.Equal("notice", LastOf(r, ClipboardFeature.HideEvent).GetProperty("why").GetString());
    }, test: false);

    // CLIP-036, CLIP-030: a busy skip is logged in words; no log line carries an item's text
    [Fact]
    public void A_busy_skip_is_logged_and_no_log_line_carries_copied_text() => T(async r =>
    {
        await Up(r);
        r.Platform.Raise("clip", "{\"what\":\"skipped\",\"reason\":\"busy\"}");
        Assert.True(r.Logged(ClipboardFeature.BusyLine));
        WinV(r);
        await Settle(r, () => Sent(r, ClipboardFeature.ShowEvent) == 1);
        await r.Bus.CallAsync("clipboard.clear");
        Assert.DoesNotContain(r.Lines, l => l.Contains("5a1", StringComparison.Ordinal));
        Assert.True(r.Logged("opened by Win+V"));
    });
}
