using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Notifications;

// The notification area (BAR-352..369), the tray tone (BAR-363..367) and the panel's words (BAR-821..848, BAR-854), ported.
public class NotificationAreaTests
{
    const string Tray = "[{\"id\":\"n:1\",\"title\":\"Windows Update\",\"exe\":\"C:\\\\WINDOWS\\\\MoNotificationUx.exe\",\"promoted\":true,\"clickable\":true}," +
        "{\"id\":\"n:2\",\"title\":\"Mantle (live)\",\"exe\":\"C:\\\\x\\\\electron.exe\",\"promoted\":true,\"clickable\":true}," +
        "{\"id\":\"n:3\",\"title\":\"Mantle\",\"exe\":\"C:\\\\Users\\\\u\\\\mantle\\\\node_modules\\\\electron\\\\dist\\\\electron.exe\",\"promoted\":true,\"clickable\":true}," +
        "{\"id\":\"n:4\",\"title\":\"Quartz\",\"exe\":\"C:\\\\Users\\\\u\\\\browser\\\\node_modules\\\\electron\\\\dist\\\\electron.exe\",\"promoted\":true,\"clickable\":true}," +
        "{\"id\":\"n:5\",\"title\":\"Tailscale\",\"exe\":\"tailscale-ipn.exe\",\"promoted\":false,\"clickable\":false,\"reason\":\"Tailscale is in Windows' hidden-icons flyout\"}]";

    static (NotificationArea A, FakePlatform P, List<string> Logs, List<JsonElement> Pushed) Make(bool test, string? fixture = null)
    {
        var p = new FakePlatform();
        var logs = new List<string>();
        var ev = new EventHub(logs.Add);
        var pushed = new List<JsonElement>();
        ev.Subscribe<TrayPayload>(NotificationArea.Event, x => pushed.Add(x.Body));
        var a = new NotificationArea(new NotificationAreaOptions { Platform = p, Events = ev, Clock = HostClock.Inline(), Test = test, Log = logs.Add, Fixture = fixture, OwnExe = @"C:\apps\MantleNative.exe" });
        return (a, p, logs, pushed);
    }

    [Fact]
    public void Mantles_own_icon_is_left_out_and_the_users_browser_stays()
    {
        var l = NotificationArea.ForBar(JsonNode.Parse(Tray), @"C:\apps\MantleNative.exe");
        Assert.Equal(new[] { "n:1", "n:4", "n:5" }, l.Select(x => (string)x!["id"]!));
        var own = JsonNode.Parse("[{\"id\":\"n:9\",\"exe\":\"C:\\\\apps\\\\MantleNative.exe\"}]");
        Assert.Empty(NotificationArea.ForBar(own, @"C:\apps\MantleNative.exe"));
    }

    [Fact]
    public async Task A_test_run_draws_the_fixture_and_a_click_is_a_dry_run_an_unclickable_one_refused()
    {
        var (a, p, logs, pushed) = Make(true, Tray);
        a.Start();
        Assert.Single(pushed);
        Assert.Equal(3, pushed[0].GetProperty("items").GetArrayLength());
        var ok = await a.ClickAsync("n:1", "right");
        Assert.True(ok.Ok);
        Assert.Contains(logs, l => l == "tray: dry run (--test): would right-click the notification-area icon n:1; nothing was sent");
        var no = await a.ClickAsync("n:5", "left");
        Assert.False(no.Ok);
        Assert.Equal("Tailscale is in Windows' hidden-icons flyout", no.Reason);
        Assert.DoesNotContain(logs, l => l.Contains("Tailscale") || l.Contains("Windows Update"));   // no tooltip text in the log
        Assert.False((await a.ClickAsync("n:1", "wheel")).Ok);
        Assert.Empty(p.Calls);
    }

    [Fact]
    public void A_test_run_without_a_fixture_shows_nothing_and_says_why_and_live_reads_the_sidecar()
    {
        var (a, _, logs, pushed) = Make(true);
        a.Start();
        Assert.Equal(0, pushed[0].GetProperty("items").GetArrayLength());
        Assert.Contains(logs, l => l.Contains("MANTLE_TEST_TRAY gives made-up ones"));
        var (b, p, _, pushed2) = Make(false);
        p.Answer("tray", Tray);
        p.SetStatus(PlatformState.Ok, "up");
        b.Start();
        Assert.Equal("ok", pushed2.Last().GetProperty("status").GetString());
        Assert.Equal(3, pushed2.Last().GetProperty("items").GetArrayLength());
        Assert.Contains(p.Calls, x => x.Verb == "tray");
        b.Dispose();
    }

    [Fact]
    public async Task Sys_keys_presses_only_three_combos_and_never_in_a_test_run()
    {
        var (a, p, logs, _) = Make(true);
        var no = await a.KeysAsync("alt+f4");
        Assert.Equal("Mantle will not press \"alt+f4\": the bar may only send win+a, win+n, win+d", (string)no["reason"]!);
        Assert.True((bool)(await a.KeysAsync("Win+N"))["dryRun"]!);
        Assert.Contains("sys.keys: dry run (--test): would press win+n; nothing was pressed", logs);
        Assert.Empty(p.Calls);
    }

    static string Sp(string s) => s.Replace(' ', ' ').Replace(' ', ' ');

    static byte[] Px(int n, byte b, byte g, byte r, byte a) => Enumerable.Range(0, n).SelectMany(_ => new[] { b, g, r, a }).ToArray();

    [Fact]
    public void Tone_is_the_alpha_weighted_luminance_with_six_pixels_of_cover()
    {
        Assert.Equal("light", TrayTone.Of(Px(16, 255, 255, 255, 60)));     // faint white dots still read white (Tailscale)
        Assert.Equal("dark", TrayTone.Of(Px(16, 20, 20, 20, 255)));
        Assert.Equal("normal", TrayTone.Of(Px(16, 200, 100, 40, 255)));
        Assert.Equal("normal", TrayTone.Of(Px(5, 255, 255, 255, 255)));     // under 6 px of cover
    }

    [Fact]
    public void The_tone_cache_keeps_48_settled_entries_and_never_drops_one_in_flight()
    {
        var c = new TrayToneCache();
        var got = new List<string>();
        Assert.True(c.Begin("busy", (t, _) => got.Add(t)));
        for (int i = 0; i < 100; i++) { Assert.True(c.Begin("i" + i, (_, _) => { })); c.Settle("i" + i, "normal", null); }
        Assert.True(c.Has("busy"));
        Assert.True(c.Count <= TrayTone.Max + 1);
        Assert.False(c.Begin("busy", (t, _) => got.Add(t + "2")));
        c.Settle("busy", "light", null);
        Assert.Equal(new[] { "light", "light2" }, got);
        Assert.False(c.Begin("busy", (t, _) => got.Add("now " + t)));
        Assert.Equal("now light", got.Last());
    }

    [Fact]
    public void The_strip_signature_moves_only_with_what_is_drawn()
    {
        var a = (JsonArray)JsonNode.Parse(Tray)!;
        string s = TrayTone.Signature(false, a);
        Assert.Equal(s, TrayTone.Signature(false, (JsonArray)JsonNode.Parse(Tray.Replace("tailscale-ipn.exe", "other.exe"))!));   // exe is not drawn
        Assert.NotEqual(s, TrayTone.Signature(true, a));
        Assert.NotEqual(s, TrayTone.Signature(false, (JsonArray)JsonNode.Parse(Tray.Replace("\"Quartz\"", "\"Quartz 2\""))!));
    }

    [Fact]
    public void The_panels_words()
    {
        var c = CultureInfo.GetCultureInfo("en-US");
        var now = new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
        long At(double minutes) => now.AddMinutes(-minutes).ToUnixTimeMilliseconds();
        Assert.Equal("now", NotificationWords.Ago(At(0.5), now, c));
        Assert.Equal("4 min ago", NotificationWords.Ago(At(4.2), now, c));
        Assert.Equal("5 h ago", NotificationWords.Ago(At(5 * 60 + 59), now, c));
        Assert.Equal("8:30 AM", Sp(NotificationWords.Ago(At(6 * 60 + 30), now, c)));
        Assert.Equal("Yesterday 3:00 PM", Sp(NotificationWords.Ago(At(24 * 60), now, c)));
        Assert.Equal("Nov 9, 3:00 PM", Sp(NotificationWords.Ago(At(48 * 60), now, c)));
        var list = ((JsonArray)JsonNode.Parse("[{\"id\":1,\"aumid\":\"A\",\"app\":\"a\"},{\"id\":2,\"app\":\"B\"},{\"id\":3,\"aumid\":\"a\"}]")!).OfType<JsonObject>();
        var g = NotificationWords.Groups(list);
        Assert.Equal(2, g.Count);
        Assert.Equal(new long[] { 1, 3 }, g[0].Items.Select(t => (long)t["id"]!));
        Assert.Equal("9+", NotificationWords.Badge(12));
        Assert.Equal("Notifications — reading…", NotificationWords.BellTip(null));
        Assert.Equal("Notifications — none", NotificationWords.BellTip(new JsonObject { ["ok"] = true, ["toasts"] = new JsonArray(), ["unread"] = 0 }));
        Assert.Equal("Notifications — 2 (1 new)", NotificationWords.BellTip((JsonObject)JsonNode.Parse("{\"ok\":true,\"unread\":1,\"toasts\":[{},{}]}")!));
        Assert.Equal("Notifications unavailable — off", NotificationWords.BellTip((JsonObject)JsonNode.Parse("{\"ok\":false,\"reason\":\"off\"}")!));
        Assert.Equal("Mantle cannot open this one: why", NotificationWords.CardTip((JsonObject)JsonNode.Parse("{\"opens\":\"none\",\"opensWhy\":\"why\"}")!));
        Assert.Equal("Open — goes where X pointed this notification\nPicture not shown — too big",
            NotificationWords.CardTip((JsonObject)JsonNode.Parse("{\"opens\":\"protocol\",\"app\":\"X\",\"imageNotes\":[\"too big\"]}")!));
        Assert.Equal("open", NotificationWords.WinKey("n", false, null, true));
        Assert.Equal("close", NotificationWords.WinKey("n", false, "notifs", true));
        Assert.Equal("none", NotificationWords.WinKey("n", true, "notifs", true));    // a banner click never toggles it shut
        Assert.Equal("windows", NotificationWords.WinKey("n", false, "qs", false));
        Assert.Equal("qs", NotificationWords.WinKey("a", false, null, true));
    }
}
