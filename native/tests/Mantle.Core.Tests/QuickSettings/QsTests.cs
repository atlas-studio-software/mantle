using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features.QuickSettings;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.QuickSettings;

/// <summary>
/// Quick settings' host half (Features/QuickSettings): main.js qsNormalize/qsCompose/QS_WRITE/qs.set/qs.get/qs.watch,
/// battery.js describe() footer, bar.js paint() tile words and bt.js tile(). No sidecar process: FakePlatform.
/// </summary>
public class QsTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

    sealed class Kit
    {
        public readonly FakePlatform P = new();
        public readonly TestEvents E = new();
        public readonly ManualScheduler S = new();
        public readonly List<string> Lines = new();
        public QsService Svc = null!;
        public Kit Make(bool test, string? fixture = null)
        {
            P.Start();
            Svc = new QsService(new QsOptions { Platform = P, Events = E, Host = S, Test = test, Log = Lines.Add, Fixture = fixture });
            return this;
        }
        public Kit Sidecar()
        {
            P.Answer("volume", "{\"level\":40,\"muted\":false}");
            P.Answer("wifi", "{\"ok\":true,\"present\":true,\"on\":true,\"connected\":true,\"ssid\":\"Home\",\"band\":\"5 GHz\",\"signal\":80}");
            P.Answer("bluetooth", "{\"ok\":true,\"present\":true,\"on\":false,\"radio\":\"off\",\"canSwitch\":true}");
            P.Answer("brightness", "{\"ok\":true,\"present\":true,\"level\":70}");
            P.Answer("nightlight", "{\"ok\":true,\"present\":true,\"on\":false}");
            P.Answer("power", "{\"ok\":true,\"present\":true,\"saver\":false,\"percent\":72,\"ac\":true,\"charging\":true}");
            return this;
        }
        public string[] Writes => P.Calls.Where(c => c.Verb is "volume" or "wifi" or "bluetooth" or "brightness" or "nightlight" && c.ArgsJson != "{}").Select(c => c.Verb + " " + c.ArgsJson).ToArray();
    }

    [Fact]
    public void Normalize_keeps_the_sidecars_own_ok_and_reason_and_a_bare_reading_is_ok()
    {
        var bare = QsParts.Normalize(O("{\"level\":40,\"muted\":false}"), "the volume");
        Assert.True(QsParts.IsOk(bare));
        Assert.Equal(40, QsParts.Num(bare["level"]));
        var pending = QsParts.Normalize(O("{\"ok\":false,\"pending\":true,\"reason\":\"still reading\",\"on\":false,\"read\":{}}"), "Wi-Fi");
        Assert.False(QsParts.IsOk(pending));
        Assert.Equal("still reading", QsParts.Str(pending["reason"]));
        Assert.False(pending.ContainsKey("read"));
        var noWhy = QsParts.Normalize(O("{\"ok\":false}"), "Wi-Fi");
        Assert.Equal("Wi-Fi cannot be read, and the sidecar gave no reason", QsParts.Str(noWhy["reason"]));
        Assert.Equal("Wi-Fi cannot be read: the sidecar answered with nothing", QsParts.Str(QsParts.Normalize(null, "Wi-Fi")["reason"]));
    }

    [Fact]
    public void Compose_is_ok_only_when_volume_and_power_answered_and_joins_every_dead_reason()
    {
        var parts = new Dictionary<string, JsonObject> { ["volume"] = O("{\"ok\":true,\"level\":3}"), ["power"] = O("{\"ok\":true}") };
        var c = QsParts.Compose(parts);
        Assert.True(QsParts.IsTrue(c["ok"]));
        Assert.Equal("Wi-Fi has not been read yet; Bluetooth has not been read yet; the screen brightness has not been read yet; Night light has not been read yet", QsParts.Str(c["reason"]));
        Assert.True(QsParts.IsTrue(c["wifi"]!["pending"]));
        parts["power"] = O("{\"ok\":false,\"reason\":\"no battery API\"}");
        Assert.False(QsParts.IsTrue(QsParts.Compose(parts)["ok"]));
    }

    [Theory]
    [InlineData("42", null)]
    [InlineData("0", null)]
    [InlineData("100", null)]
    [InlineData("\"42\"", null)]
    [InlineData("101", "volume must be a whole number from 0 to 100")]
    [InlineData("-1", "volume must be a whole number from 0 to 100")]
    [InlineData("4.5", "volume must be a whole number from 0 to 100")]
    [InlineData("0.42", "volume must be a whole number from 0 to 100 (0.42 looks like a 0-1 fraction)")]
    [InlineData("\"loud\"", "volume must be a whole number from 0 to 100")]
    public void Levels_are_whole_percentages_and_a_fraction_is_refused_with_that_reason(string v, string? why)
        => Assert.Equal(why, QsParts.CheckLevel("volume", J(v)));

    [Fact]
    public void A_test_run_set_is_a_dry_run_logged_with_what_it_would_send_and_never_sent() => T(async k =>
    {
        k.Make(test: true).Sidecar();
        await k.Svc.ReadAllAsync();
        var r = await k.Svc.SetAsync("volume", J("25"));
        Assert.True(r.Ok);
        Assert.True(r.DryRun);
        Assert.Equal(25, QsParts.Num(r.Value));
        Assert.Equal(25, QsParts.Num(r.State!["level"]));
        Assert.True(QsParts.IsTrue(r.State["dryRun"]));
        Assert.Empty(k.Writes);   // nothing was sent: the runner's volume is never changed
        Assert.Contains(k.Lines, l => l.Contains("qs: dry run (--test): volume=25 would ask the sidecar volume {\"level\":25}; nothing was sent, Windows was not changed"));
        Assert.Contains(k.Lines, l => l.StartsWith("mantle: qs.set volume=25 -> ok, reads 25 ("));
        var w = await k.Svc.SetAsync("wifi", J("false"));
        Assert.True(w.Ok);
        Assert.False(QsParts.IsTrue(w.State!["on"]));
        Assert.False(QsParts.IsTrue(w.State["connected"]));   // a Wi-Fi switched off is not connected
        Assert.Empty(k.Writes);
        // the panel hears the dry run's reading through "qs" (8 ms after the store)
        k.S.Advance(10);
        var last = (QsSnapshot)k.E.Published.Last(p => p.Name == "qs").Payload!;
        Assert.Equal(25, last.Body.GetProperty("volume").GetProperty("level").GetDouble());
    });

    [Fact]
    public void A_dry_run_refuses_what_the_live_sidecar_would_refuse() => T(async k =>
    {
        k.Make(test: true);
        k.P.Answer("wifi", "{\"ok\":true,\"present\":false,\"on\":false}");
        k.P.Answer("bluetooth", "{\"ok\":false,\"reason\":\"Windows lists no radio\"}");
        var none = await k.Svc.SetAsync("wifi", J("true"));
        Assert.False(none.Ok);
        Assert.Equal("Wi-Fi is still being read, so it cannot be switched yet", none.Reason);
        await k.Svc.ReadAllAsync();
        Assert.Equal("Wi-Fi could not be changed: this PC has none", (await k.Svc.SetAsync("wifi", J("true"))).Reason);
        Assert.Equal("Bluetooth could not be changed: Windows lists no radio", (await k.Svc.SetAsync("bluetooth", J("true"))).Reason);
        Assert.Empty(k.Writes);
        Assert.Contains(k.Lines, l => l.StartsWith("mantle: qs.set wifi=true -> NOT DONE: Wi-Fi could not be changed: this PC has none"));
    });

    [Fact]
    public void Unknown_parts_and_bad_values_are_refused_with_the_words_main_uses() => T(async k =>
    {
        k.Make(test: false).Sidecar();
        Assert.Equal("qs.set does not know the part \"airplane\" (it does volume, mute, wifi, bluetooth, brightness, nightlight)", (await k.Svc.SetAsync("airplane", J("true"))).Reason);
        Assert.Equal("mute must be true or false", (await k.Svc.SetAsync("mute", J("1"))).Reason);
        Assert.Equal("brightness must be a whole number from 0 to 100 (0.5 looks like a 0-1 fraction)", (await k.Svc.SetAsync("brightness", J("0.5"))).Reason);
        Assert.Empty(k.Writes);
    });

    [Fact]
    public void A_live_set_sends_the_verb_and_its_reply_is_the_read_back_and_a_refusal_carries_its_read() => T(async k =>
    {
        k.Make(test: false).Sidecar();
        k.P.Handle("brightness", a => a.TryGetProperty("level", out var l)
            ? PlatformReply.Success(J($"{{\"ok\":true,\"present\":true,\"level\":{l.GetDouble() - 1}}}"))
            : PlatformReply.Success(J("{\"ok\":true,\"present\":true,\"level\":70}")));
        var r = await k.Svc.SetAsync("brightness", J("50"));
        Assert.True(r.Ok);
        Assert.False(r.DryRun);
        Assert.Equal(49, QsParts.Num(r.Value));   // what Windows reads back, not what was asked
        Assert.Contains("brightness {\"level\":50}", k.Writes);
        k.P.Handle("wifi", a => a.TryGetProperty("on", out _)
            ? PlatformReply.Success(J("{\"ok\":false,\"reason\":\"Windows refused to switch the Wi-Fi radio (error 5)\",\"read\":{\"ok\":true,\"present\":true,\"on\":true}}"))
            : PlatformReply.Success(J("{\"ok\":true,\"present\":true,\"on\":true}")));
        var w = await k.Svc.SetAsync("wifi", J("false"));
        Assert.False(w.Ok);
        Assert.Equal("Windows refused to switch the Wi-Fi radio (error 5)", w.Reason);
        Assert.True(QsParts.IsTrue(w.State!["on"]));          // the tile springs back
        Assert.True(QsParts.IsTrue(k.Svc.Latest["wifi"]["on"]));
    });

    [Fact]
    public void Get_is_cached_once_every_part_was_read_and_a_push_newer_than_a_read_wins() => T(async k =>
    {
        k.Make(test: false).Sidecar();
        var first = await k.Svc.GetAsync();
        Assert.False(first.ContainsKey("cached"));
        int calls = k.P.Calls.Count;
        var again = await k.Svc.GetAsync();
        Assert.True(QsParts.IsTrue(again["cached"]));
        Assert.Equal(calls, k.P.Calls.Count);
        k.S.Advance(100);
        k.P.Raise("qs", "{\"ev\":\"qs\",\"part\":\"nightlight\",\"state\":{\"ok\":true,\"present\":true,\"on\":true}}");
        Assert.True(QsParts.IsTrue(k.Svc.Latest["nightlight"]["on"]));
        k.P.Raise("volume", "{\"ev\":\"volume\",\"level\":12,\"muted\":true}");
        Assert.Equal(12, QsParts.Num(k.Svc.Latest["volume"]["level"]));
        Assert.False(k.Svc.Latest["volume"].ContainsKey("ev"));
    });

    [Fact]
    public void The_watch_takes_a_lease_renews_it_and_falls_back_to_a_re_read_when_the_sidecar_cannot_watch() => T(async k =>
    {
        k.Make(test: false).Sidecar();
        k.P.Answer("qswatch", "{\"ok\":true}");
        var w = await k.Svc.WatchAsync(true, "bar opened the panel");
        Assert.Equal("push", QsParts.Str(w["via"]));
        Assert.Contains(k.P.Calls, c => c.Verb == "qswatch" && c.ArgsJson.Contains("\"leaseMs\":60000"));
        int leases = k.P.Calls.Count(c => c.Verb == "qswatch");
        k.S.Advance(QsService.RenewMs + 1);
        await Task.Yield();
        Assert.True(k.P.Calls.Count(c => c.Verb == "qswatch") > leases);
        await k.Svc.WatchAsync(false, "bar closed the panel");
        Assert.Contains(k.P.Calls, c => c.Verb == "qswatch" && c.ArgsJson.Contains("\"on\":false"));
        Assert.False(k.Svc.Watching);

        var old = new Kit().Make(test: false).Sidecar();   // a sidecar without qswatch
        var p = await old.Svc.WatchAsync(true, "bar opened the panel");
        Assert.Equal("poll", QsParts.Str(p["via"]));
        Assert.Contains(old.Lines, l => l.Contains("so the panel re-reads every 2 s instead"));
    });

    [Fact]
    public void A_fixture_replaces_the_sidecar_in_a_test_run_only() => T(async k =>
    {
        const string fx = "{\"wifi\":{\"ok\":true,\"present\":true,\"on\":true,\"connected\":true,\"ssid\":\"Fixture\"},\"volume\":{\"level\":62,\"muted\":false},\"sessions\":[{\"key\":\"a\",\"name\":\"Player\",\"path\":\"C:\\\\p.exe\",\"level\":50}]}";
        k.Make(test: true, fixture: fx);
        Assert.True(k.Svc.UsesFixture);
        var g = await k.Svc.GetAsync();
        Assert.Equal("Fixture", QsParts.Str(g["wifi"]!["ssid"]));
        Assert.True(QsParts.IsTrue(g["bluetooth"]!["pending"]));   // not in the fixture: not read, and said so
        var s = await k.Svc.AudioSessionsAsync();
        Assert.Equal("Player", QsParts.Str(s["sessions"]![0]!["name"]));
        Assert.Equal("no icon: this build does not read app icons for this list yet", QsParts.Str(s["sessions"]![0]!["iconReason"]));
        Assert.Empty(k.P.Calls);   // the sidecar was never asked
        var live = new Kit().Make(test: false, fixture: fx);
        Assert.False(live.Svc.UsesFixture);
    });

    [Fact]
    public void The_commands_are_in_the_generated_catalog_with_electrons_names() => T(async k =>
    {
        k.Make(test: true).Sidecar();
        var bus = new TestBus();
        using var reg = QsCommands.Register(bus, new QsCommands(k.Svc));
        await k.Svc.ReadAllAsync();
        foreach (var n in new[] { "qs.get", "qs.set", "qs.watch", "qs.audio.sessions", "qs.audio.session" }) Assert.True(bus.Has(n), n);
        var ok = await bus.CallAsync("qs.set", O("{\"part\":\"mute\",\"value\":true}"));
        Assert.True(ok.Ok);
        Assert.True(ok.Result.GetProperty("dryRun").GetBoolean());
        var bad = await bus.CallAsync("qs.set", O("{\"part\":\"volume\",\"value\":0.3}"));
        Assert.False(bad.Ok);
        Assert.Equal("volume must be a whole number from 0 to 100 (0.3 looks like a 0-1 fraction)", bad.Error);
        k.P.Answer("audio", "{\"ok\":true,\"sessions\":[{\"key\":\"s1\",\"name\":\"System sounds\",\"system\":true}]}");
        var ss = await bus.CallAsync("qs.audio.sessions");
        Assert.True(ss.Ok);
        Assert.Equal(JsonValueKind.Null, ss.Result.GetProperty("sessions")[0].GetProperty("iconReason").ValueKind);
        var one = await bus.CallAsync("qs.audio.session", O("{\"key\":\"s1\",\"level\":10}"));
        Assert.True(one.Ok);
        Assert.DoesNotContain(k.P.Calls, c => c.Verb == "audio" && c.ArgsJson.Contains("\"session\""));   // a dry run
        Assert.False((await bus.CallAsync("qs.audio.session", O("{}"))).Ok);
    });

    [Theory]
    [InlineData(null, "Reading the battery…")]
    [InlineData("{\"ok\":false,\"reason\":\"the sidecar did not answer (Win32 error 5)\"}", "Battery: Mantle could not get the battery status from Windows. It updates by itself when Windows reports a change")]
    [InlineData("{\"ok\":true,\"present\":false}", "No battery is detected")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":72,\"ac\":true,\"charging\":true}", "72% · charging")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":100,\"ac\":true,\"charging\":false}", "Fully charged")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":80,\"ac\":true,\"charging\":false}", "80% · plugged in, not charging")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":55,\"ac\":false}", "55% · estimating time left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":4,\"ac\":false}", "4% · very low, plug in now")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":255,\"ac\":true}", "Plugged in")]
    // the time left (battery.js secondsLeft / hm): the mock's reading, the older minutesLeft, a whole hour, under a
    // minute, low with a time, and Windows' "unknown" (0 or 100 h and more) still said as pending
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":72,\"ac\":false,\"secondsLeft\":11040,\"minutesLeft\":184}", "72% · 3 hr 4 min left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":72,\"onBattery\":true,\"minutesLeft\":45}", "72% · 45 min left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":60,\"ac\":false,\"secondsLeft\":7200}", "60% · 2 hr left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":60,\"ac\":false,\"secondsLeft\":30}", "60% · under 1 min left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":8,\"ac\":false,\"secondsLeft\":1500}", "8% · low, 25 min left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":55,\"ac\":false,\"secondsLeft\":0}", "55% · estimating time left")]
    [InlineData("{\"ok\":true,\"present\":true,\"percent\":55,\"ac\":false,\"secondsLeft\":360000}", "55% · estimating time left")]
    public void The_footer_words_the_battery_as_battery_js_does(string? part, string footer)
        => Assert.Equal(footer, QsBattery.Describe(part == null ? null : O(part)).Footer);

    [Fact]
    public void The_tiles_say_what_bar_js_and_bt_js_say()
    {
        var w = QsView.Wifi(O("{\"ok\":true,\"present\":true,\"on\":true,\"connected\":true,\"ssid\":\"Home\",\"band\":\"5 GHz\",\"signal\":80}"));
        Assert.Equal(("Home · 5 GHz", "Turn Wi-Fi off", true, false, 3), (w.Sub, w.Tip, w.On, w.Dead, w.Bars));
        Assert.Equal("No adapter", QsView.Wifi(O("{\"ok\":true,\"present\":false}")).Sub);
        Assert.True(QsView.Wifi(O("{\"ok\":true,\"present\":false}")).Dead);
        Assert.Equal("Unknown", QsView.Wifi(null).Sub);
        var dead = QsView.Wifi(O("{\"ok\":false,\"reason\":\"WLAN service is stopped\"}"));
        Assert.Equal(("Unavailable", "Wi-Fi — WLAN service is stopped"), (dead.Sub, dead.Tip));

        var bt = QsView.Bluetooth(O("{\"ok\":true,\"present\":false}"));
        Assert.Equal(("No radio", "ms-settings:bluetooth", false), (bt.Sub, bt.Opens, bt.Live));
        Assert.Equal("No Bluetooth radio: Windows lists no Bluetooth radio on this PC\nClick to open Bluetooth settings.", bt.Tip);
        Assert.Equal("Off · locked", QsView.Bluetooth(O("{\"ok\":true,\"present\":true,\"on\":false,\"canSwitch\":false,\"access\":\"DeniedByUser\"}")).Sub);
        Assert.Equal("ms-settings:privacy-radios", QsView.Bluetooth(O("{\"ok\":true,\"present\":true,\"on\":false,\"canSwitch\":false,\"access\":\"DeniedByUser\"}")).Opens);
        Assert.Equal("Checking…", QsView.Bluetooth(O("{\"ok\":false,\"pending\":true}")).Sub);
        var on = QsView.Bluetooth(O("{\"ok\":true,\"present\":true,\"on\":true,\"canSwitch\":true}"));
        Assert.Equal(("On", "Turn Bluetooth off", true, true), (on.Sub, on.Tip, on.On, on.Live));

        Assert.Equal("Not set up", QsView.NightLight(O("{\"ok\":true,\"present\":false}")).Sub);
        Assert.Equal("Off · Settings", QsView.Saver(O("{\"ok\":true,\"saver\":false}")).Sub);

        var vol = QsView.Slider(O("{\"ok\":true,\"level\":62.4,\"muted\":false}"), brightness: false);
        Assert.Equal((true, "62%"), (vol.Live, vol.Value));
        var br = QsView.Slider(O("{\"ok\":true,\"present\":false}"), brightness: true);
        Assert.Equal((false, "unavailable", "This display does not report a brightness control"), (br.Live, br.Value, br.Why));
    }

    [Fact]
    public void The_notes_name_every_dead_part_and_a_set_note_does_not_double_the_part()
    {
        var parts = new Dictionary<string, JsonObject> {
            ["wifi"] = O("{\"ok\":false,\"reason\":\"no WLAN\"}"), ["volume"] = O("{\"ok\":true,\"level\":1}"),
            ["power"] = O("{\"ok\":true}"), ["nightlight"] = O("{\"ok\":true}"), ["bluetooth"] = O("{\"ok\":true}"),
        };
        Assert.Equal(new[] { "Wi-Fi: no WLAN", "Volume did not change: busy" }, QsView.Problems(parts, new[] { QsView.SetNote("volume", "busy") }));
        Assert.Equal("Night light could not be changed: x", QsView.SetNote("nightlight", "Night light could not be changed: x"));
    }

    static void T(Func<Kit, Task> body) => HostLoop.Run(async (_, _) => await body(new Kit()));
}
