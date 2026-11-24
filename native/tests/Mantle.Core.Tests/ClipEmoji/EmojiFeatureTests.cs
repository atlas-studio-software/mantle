using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Emoji;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.ClipEmoji;

// lane r9-clipemoji-2: the [Feature("emoji")] service (lib/features/emoji.js) on the feature host's test rig: a fake
// sidecar, made-up windows, settings in memory (MemoryStores), never a file, the real keyboard or the clipboard.
public class EmojiFeatureTests
{
    const string Pick = "\U0001F600";
    const string Arm = "{\"hwnd\":\"5151\",\"app\":\"notes.exe\",\"elevated\":false,\"selfElevated\":false,\"caret\":{\"x\":10,\"y\":20,\"h\":18}}";

    static void T(Func<Rig, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D(Rig r) =>
        FeatureDescriptor.Of(typeof(EmojiFeature).GetCustomAttribute<FeatureAttribute>()!, () => new EmojiFeature(new EmojiFeature.Host(new HostClock(r.Time))), EmojiFeature.Options);

    static async Task Settle(Rig r, Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    static int Count(Rig r, string verb) => r.Platform.Calls.Count(c => c.Verb == verb);
    static int Sent(Rig r, string ev) => r.Events.Published.Count(p => p.Name == ev);
    static JsonElement LastOf(Rig r, string ev) => (JsonElement)r.Events.Published.Last(p => p.Name == ev).Payload!;
    static bool Piece(Rig r, Func<Mantle.Core.State.FeaturePiece, bool> test) => r.Hub.Current.Features.TryGetValue("emoji", out var p) && test(p);

    static async Task Up(Rig r, string arm = Arm)
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Platform.Answer("emoji.verdict", "{\"ok\":true}");
        r.Platform.Answer("emoji.arm", arm);
        r.Platform.Answer("emoji.watch", "{\"ok\":true}");
        r.Platform.Answer("emoji.windows", "{\"sent\":4}");
        r.Platform.Answer("emoji.type", "{\"dryRun\":true,\"units\":2}");
        r.Host.Load(new[] { D(r) });
        await r.Host.Installed;
        Assert.True((await r.Bus.CallAsync("emoji.ready", new JsonObject { ["panel"] = "7070" })).Ok);
        await Settle(r, () => Piece(r, p => p.Ok));
    }

    static void Chord(Rig r, string c = "Win+.") => r.Platform.Raise("chord", "{\"chord\":\"" + c + "\"}");

    [Fact]
    public void The_feature_is_emoji_on_by_default_rests_for_games_and_claims_Win_period_and_Win_semicolon()
    {
        var a = typeof(EmojiFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("emoji", a.Name);
        Assert.Equal("Emoji picker (Win+.)", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Equal(new[] { "Win+.", "Win+;" }, a.Chords);
        Assert.Empty(a.Observe);
        var o = Assert.Single(EmojiFeature.Options);
        Assert.Equal("closeAfterInsert", o.Key);
        Assert.Equal(FeatureOptionKind.Bool, o.Kind);
        Assert.False((bool)o.Default!);
    }

    // DEVIATION + health: no panel, not healthy; an old sidecar exe without emoji.* is named
    [Fact]
    public void With_no_panel_or_an_old_sidecar_the_chords_stay_Windows_own() => T(async r =>
    {
        r.Platform.Start();
        r.Host.Load(new[] { D(r) });   // no emoji.verdict: "unknown command"
        await r.Host.Installed;
        await Settle(r, () => Count(r, "emoji.verdict") > 0);
        Assert.True(Piece(r, p => !p.Ok && p.Reason == EmojiFeature.NoPanel));
        await r.Bus.CallAsync("emoji.ready", new JsonObject { ["panel"] = "7070" });
        await Settle(r, () => Piece(r, p => p.Reason == EmojiFeature.TooOld));
        Assert.True(Piece(r, p => !p.Ok && p.Reason == EmojiFeature.TooOld));
    });

    [Fact]
    public void Win_period_opens_inactive_with_the_key_watch_leased_and_renewed_and_again_closes() => T(async r =>
    {
        await Up(r);
        Assert.True(Piece(r, p => p.Reason == EmojiFeature.ReadyWords));
        Chord(r);
        await Settle(r, () => Count(r, "emoji.watch") == 1);
        Assert.Equal(TimeSpan.FromMilliseconds(150), r.Platform.Calls.First(c => c.Verb == "emoji.arm").Timeout);
        var show = LastOf(r, EmojiFeature.ShowEvent);
        Assert.Equal("5151", show.GetProperty("arm").GetProperty("hwnd").GetString());
        Assert.Equal(JsonValueKind.Null, show.GetProperty("notice").ValueKind);
        var watch = r.Platform.Calls.First(c => c.Verb == "emoji.watch").ArgsJson;
        Assert.Contains("\"on\":true", watch, StringComparison.Ordinal);
        Assert.Contains("\"hwnd\":\"5151\"", watch, StringComparison.Ordinal);
        Assert.Contains("\"panel\":\"7070\"", watch, StringComparison.Ordinal);
        Assert.Contains("\"leaseMs\":6000", watch, StringComparison.Ordinal);
        r.Time.AdvanceMs(2001);
        Assert.Equal(2, Count(r, "emoji.watch"));

        r.Platform.Raise("emoji", "{\"what\":\"key\",\"key\":\"ArrowRight\",\"shift\":false}");
        Assert.Equal("ArrowRight", LastOf(r, EmojiFeature.KeyEvent).GetProperty("key").GetString());

        Chord(r, "Win+;");
        await Settle(r, () => Sent(r, EmojiFeature.HideEvent) == 1);
        Assert.Equal("again", LastOf(r, EmojiFeature.HideEvent).GetProperty("why").GetString());
        Assert.Contains("\"on\":false", r.Platform.Calls.Last(c => c.Verb == "emoji.watch").ArgsJson, StringComparison.Ordinal);
        int n = Count(r, "emoji.watch");
        r.Time.AdvanceMs(5000);
        Assert.Equal(n, Count(r, "emoji.watch"));   // no renewal after the close
    }, test: false);

    [Fact]
    public void Over_an_administrator_window_the_press_goes_to_Windows_panel_with_a_notice() => T(async r =>
    {
        await Up(r, "{\"hwnd\":\"5151\",\"app\":\"regedit.exe\",\"elevated\":true,\"selfElevated\":false}");
        Chord(r);
        await Settle(r, () => Sent(r, EmojiFeature.NoticeEvent) == 1);
        Assert.Equal(1, Count(r, "emoji.windows"));
        Assert.Equal(0, Sent(r, EmojiFeature.ShowEvent));
        Assert.Contains("runs as administrator, so Mantle can't type into it. Windows' emoji panel opened instead.",
            LastOf(r, EmojiFeature.NoticeEvent).GetProperty("text").GetString(), StringComparison.Ordinal);
    }, test: false);

    [Fact]
    public void A_pick_in_a_test_instance_is_a_dry_run_remembered_in_recents_and_never_logged() => T(async r =>
    {
        await Up(r);
        Chord(r);
        await Settle(r, () => Sent(r, EmojiFeature.ShowEvent) == 1);
        Assert.Equal(0, Count(r, "emoji.arm"));     // a test never reads the real foreground
        Assert.Equal(0, Count(r, "emoji.watch"));   // nor takes the keyboard
        var res = await r.Bus.CallAsync("emoji.insert", new JsonObject { ["ch"] = Pick, ["via"] = "Enter" });
        Assert.True(res.Ok, res.Error);
        Assert.True(res.Result.GetProperty("dryRun").GetBoolean());
        Assert.False(res.Result.GetProperty("typed").GetBoolean());
        Assert.Equal(Pick, res.Result.GetProperty("recents")[0].GetString());
        var type = r.Platform.Calls.Last(c => c.Verb == "emoji.type").ArgsJson;
        Assert.Contains("\"hwnd\":\"7070\"", type, StringComparison.Ordinal);
        Assert.Contains("\"dryRun\":true", type, StringComparison.Ordinal);
        Assert.Equal(0, Sent(r, EmojiFeature.HideEvent));   // Enter types and the picker stays open
        Assert.True(r.Logged("pick: dry run by Enter"));
        Assert.DoesNotContain(r.Lines, l => l.Contains(Pick, StringComparison.Ordinal));

        var empty = await r.Bus.CallAsync("emoji.insert", new JsonObject { ["ch"] = "" });
        Assert.Equal("nothing to type", empty.Result.GetProperty("reason").GetString());
    });

    [Fact]
    public void A_lost_watch_closes_the_picker_and_switching_off_closes_it_too() => T(async r =>
    {
        await Up(r);
        Chord(r);
        await Settle(r, () => Sent(r, EmojiFeature.ShowEvent) == 1);
        r.Platform.Raise("emoji", "{\"what\":\"lost\"}");
        Assert.Equal("lost", LastOf(r, EmojiFeature.HideEvent).GetProperty("why").GetString());
        Chord(r);
        await Settle(r, () => Sent(r, EmojiFeature.ShowEvent) == 2);
        r.Host.SetEnabled("emoji", false, "the tray menu");
        await Settle(r, () => Sent(r, EmojiFeature.HideEvent) == 2);
        Assert.Equal("off", LastOf(r, EmojiFeature.HideEvent).GetProperty("why").GetString());
    }, test: false);
}
