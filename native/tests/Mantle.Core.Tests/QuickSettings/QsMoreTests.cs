using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.QuickSettings;
using Mantle.Core.Tests.Features;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.QuickSettings;

/// <summary>
/// Quick settings one level down (QsMoreView: qs-more.js and bt.js words; QsService.More: main.js qsMore, qs.wifi.*,
/// qs.audio.devices / default, qs.bt.*). Every write in a --test run is a dry run, and a password never reaches the log.
/// </summary>
public class QsMoreTests
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
        public string Log => string.Join("\n", Lines);
        public string[] Verbs(string verb) => P.Calls.Where(c => c.Verb == verb).Select(c => c.ArgsJson).ToArray();
    }

    [Theory]
    [InlineData("short", "A Wi-Fi password is at least 8 characters.")]
    [InlineData("12345678", null)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeX", "A Wi-Fi password is at most 63 characters (or exactly 64 hex digits).")]
    [InlineData("pässwörd1", "A Wi-Fi password can only use letters, digits, spaces and the usual symbols.")]
    public void The_password_rule_is_the_sidecars(string pw, string? problem) => Assert.Equal(problem, QsMoreView.PasswordProblem(pw));

    [Fact]
    public void Wifi_rows_and_states_say_what_qs_more_says()
    {
        var n = O("{\"ssid\":\"Cafe\",\"secure\":true,\"security\":\"WPA2-Personal\",\"known\":true,\"signal\":40}");
        Assert.Equal("Saved · WPA2-Personal", QsMoreView.WifiSub(n, null, null));
        Assert.Equal("Connecting…", QsMoreView.WifiSub(n, "connect", null));
        Assert.Equal("Couldn’t connect", QsMoreView.WifiSub(n, null, new QsRowFail("connect", "x", "y")));
        Assert.Equal("Connected, secured", QsMoreView.WifiSub(O("{\"connected\":true,\"secure\":true}"), null, null));
        Assert.Equal(2, QsMoreView.Bars(40));
        Assert.Equal(1, QsMoreView.Bars(0));
        Assert.Equal("ssid:Cafe", QsMoreView.NetKey(n));
        Assert.Equal("Wi-Fi is off", QsMoreView.Wifi(O("{\"ok\":true,\"present\":true,\"on\":false}"), false).Title);
        Assert.Equal("No Wi-Fi adapter", QsMoreView.Wifi(O("{\"ok\":true,\"present\":false}"), false).Title);
        Assert.Equal("Scanning for networks…", QsMoreView.Wifi(O("{\"ok\":true,\"networks\":[]}"), true).Why);
        Assert.Equal("list", QsMoreView.Wifi(O("{\"ok\":true,\"networks\":[{}]}"), false).State);
        Assert.Equal("The password looks wrong.", QsMoreView.ConnectLead(O("{\"code\":294932}")));
        Assert.Equal("Couldn’t connect.", QsMoreView.ConnectLead(O("{\"code\":1}")));
        Assert.Equal("Last attempt (12 s ago): Mantle could not connect to “Cafe” — wrong key",
            QsMoreView.WifiLastNote(O("{\"last\":{\"ok\":false,\"ssid\":\"Cafe\",\"agoMs\":12000,\"reason\":\"wrong key\"}}"), false));
        Assert.Equal("", QsMoreView.WifiLastNote(O("{\"last\":{\"ok\":false,\"ssid\":\"Cafe\"}}"), true));
    }

    [Fact]
    public void Bluetooth_rows_follow_bt_js_describe()
    {
        var d = O("{\"id\":\"b1\",\"name\":\"Buds\",\"kind\":\"headphones\",\"connected\":true,\"battery\":55.4,\"canConnect\":true}");
        var x = QsMoreView.BtDescribe(d, null, null, null);
        Assert.Equal(("connected", "Connected", 55, "disconnect", "Disconnect"), (x.State, x.Sub, x.Battery, x.Action, x.ActionLabel));
        Assert.Equal("Buds\nHeadphones · Connected · battery 55%", x.Tip);
        var busy = QsMoreView.BtDescribe(d, "b1", "disconnect", null);
        Assert.Equal(("disconnecting", "Disconnecting…"), (busy.State, busy.ActionLabel));
        var off = QsMoreView.BtDescribe(O("{\"id\":\"m\",\"name\":\" \",\"kind\":\"zzz\",\"connected\":false}"), null, null,
            new Dictionary<string, QsRowFail> { ["m"] = new("connect", "Couldn’t connect.", "out of range") });
        Assert.Equal(("Unnamed device", "other", "failed", "settings", "Mantle has no way to connect this device"), (off.Name, off.Kind, off.State, off.Action, off.ActionWhy));
        Assert.Equal("Bluetooth is off", QsMoreView.Bt(O("{\"ok\":true,\"present\":true,\"on\":false}")).Title);
        Assert.Equal("No paired devices", QsMoreView.Bt(O("{\"ok\":true,\"on\":true,\"devices\":[]}")).Title);
        Assert.Equal("Bluetooth’s on/off state could not be read: denied", QsMoreView.BtRadioNote(O("{\"ok\":true,\"radioReason\":\"denied\"}")));
        Assert.Equal(("b9", "connect"), QsMoreView.BtBusyOf(O("{\"busy\":{\"device\":\"b9\",\"op\":\"connect\"}}"))!.Value);
        Assert.Equal("Mantle is still connecting another device; wait for it to finish",
            QsMoreView.BtPrivateReason("Mantle is still connecting Jo's Buds; wait for it to finish", null));
        Assert.Equal("the device did not answer", QsMoreView.BtPrivateReason("Jo's Buds did not answer", "Jo's Buds"));
    }

    [Fact]
    public void Sound_rows_say_which_device_plays_and_why()
    {
        Assert.Equal(((string?)null, "Speakers is the output (calls still go to another device; click another device and back to move them too)"),
            QsMoreView.Device(O("{\"name\":\"Speakers\",\"isDefault\":true,\"roles\":{\"communications\":false}}"), false));
        Assert.Equal(((string?)"Calls", "Play sound through Headset\n(already the device for calls)"),
            QsMoreView.Device(O("{\"name\":\"Headset\",\"isDefault\":false,\"roles\":{\"communications\":true}}"), false));
        Assert.Equal("Apps appear here while they have sound open on Speakers.", QsMoreView.Mixer(O("{\"ok\":true,\"sessions\":[],\"device\":{\"name\":\"Speakers\"}}")).Why);
        Assert.Equal("System sounds", QsMoreView.AppName(O("{\"system\":true}")));
        Assert.Equal("S", QsMoreView.AppLetter(O("{\"name\":\"#spotify\"}")));
        Assert.Equal("Unmute Spotify", QsMoreView.MuteTip(O("{\"name\":\"Spotify\",\"muted\":true}")));
    }

    [Fact]
    public async Task A_test_runs_connect_is_a_dry_run_and_the_password_never_reaches_the_log()
    {
        var k = new Kit().Make(test: true);
        const string secret = "hunter2-correct-horse";
        var r = await k.Svc.WifiConnectAsync(J("{\"ssid\":\"Cafe\",\"password\":\"" + secret + "\",\"auto\":false}"));
        Assert.False(QsParts.IsOk(r));
        Assert.True(QsParts.IsTrue(r["dryRun"]));
        Assert.Equal(QsService.DryRunWords, QsParts.Str(r["reason"]));
        Assert.Empty(k.Verbs("wlan"));
        Assert.Contains("qs: dry run (--test): a Wi-Fi connect would ask the sidecar wlan connect {\"auto\":false,\"ssid\":\"<4 chars>\"} with a password of 21 characters", k.Log);
        Assert.DoesNotContain(secret, k.Log);
        Assert.DoesNotContain("Cafe", k.Log);
        Assert.DoesNotContain(secret, r.ToJsonString());
        var bad = await k.Svc.WifiConnectAsync(J("{\"ssid\":\"Cafe\",\"password\":\"short\"}"));
        Assert.Equal("A Wi-Fi password is at least 8 characters.", QsParts.Str(bad["reason"]));
        Assert.DoesNotContain("short\"", k.Log);
        Assert.Equal("qs.wifi.connect needs ssid (the network's name) or hex", QsParts.Str((await k.Svc.WifiConnectAsync(J("{}")))["reason"]));
    }

    [Fact]
    public async Task Every_other_write_is_a_dry_run_too_and_reads_still_go_to_the_sidecar()
    {
        var k = new Kit().Make(test: true);
        k.P.Answer("wlan", "{\"present\":true,\"on\":true,\"networks\":[]}");
        Assert.True(QsParts.IsOk(await k.Svc.WifiListAsync()));
        Assert.True(QsParts.IsTrue((await k.Svc.WifiDisconnectAsync())["dryRun"]));
        Assert.True(QsParts.IsTrue((await k.Svc.AudioDefaultAsync(J("{\"device\":\"{0.0.0}.x\"}")))["dryRun"]));
        Assert.True(QsParts.IsTrue((await k.Svc.BtToggleAsync("connect", J("{\"device\":\"b1\"}")))["dryRun"]));
        Assert.True(QsParts.IsTrue((await k.Svc.BtToggleAsync("disconnect", J("{\"device\":\"b1\"}")))["dryRun"]));
        Assert.Equal(new[] { "{\"op\":\"list\"}" }, k.Verbs("wlan"));
        Assert.Empty(k.Verbs("audio"));
        Assert.Empty(k.Verbs("btdevices"));
        Assert.Contains("qs: dry run (--test): the output device would ask the sidecar audio default {\"device\":\"<9 chars>\"}; nothing was sent", k.Log);
        Assert.Contains("qs: dry run (--test): a Bluetooth connect would ask the sidecar btdevices connect", k.Log);
        Assert.Equal("qs.bt.connect needs device: a row's id from qs.bt.list", QsParts.Str((await k.Svc.BtToggleAsync("connect", J("{}")))["reason"]));
    }

    [Fact]
    public async Task Live_the_password_goes_to_the_sidecar_once_and_a_bt_failure_never_names_the_device()
    {
        var k = new Kit().Make(test: false);
        k.P.Answer("wlan", "{\"ok\":false,\"reason\":\"PSK mismatch suspected\",\"code\":294932}");
        var r = await k.Svc.WifiConnectAsync(J("{\"hex\":\"436166\",\"password\":\"abcdefgh\"}"));
        Assert.Equal("PSK mismatch suspected", QsParts.Str(r["reason"]));
        Assert.Equal(new[] { "{\"op\":\"connect\",\"auto\":true,\"hex\":\"436166\",\"password\":\"abcdefgh\"}" }, k.Verbs("wlan"));
        Assert.DoesNotContain("abcdefgh", k.Log);
        k.P.Answer("btdevices", "{\"ok\":false,\"name\":\"Jo's Buds\",\"reason\":\"Jo's Buds did not answer\"}");
        var b = await k.Svc.BtToggleAsync("connect", J("{\"device\":\"b1\"}"));
        Assert.Equal("the device did not answer", QsParts.Str(b["reason"]));
    }

    [Fact]
    public async Task Made_up_lists_are_read_in_place_of_the_sidecar_and_pushes_are_relayed()
    {
        var k = new Kit().Make(test: true, fixture: "{\"wifiList\":{\"present\":true,\"on\":true,\"networks\":[{\"ssid\":\"A\"}]},\"devices\":[{\"id\":\"d1\",\"name\":\"Speakers\",\"isDefault\":true}],\"btList\":{\"ok\":true,\"on\":true,\"devices\":[]}}");
        Assert.Single((JsonArray)(await k.Svc.WifiListAsync())["networks"]!);
        var scan = await k.Svc.WifiScanAsync();
        Assert.True(QsParts.IsOk(scan["scan"] as JsonObject));
        Assert.Single((JsonArray)(await k.Svc.AudioDevicesAsync())["devices"]!);
        Assert.True(QsParts.IsOk(await k.Svc.BtListAsync()));
        Assert.True(QsParts.IsTrue((await k.Svc.BtWatchAsync(true))["watching"]));
        Assert.Empty(k.P.Calls.Where(c => c.Verb is "wlan" or "audio" or "btdevices"));

        var live = new Kit().Make(test: false);
        live.P.Raise("btdevices", "{\"ev\":\"btdevices\",\"ok\":true,\"devices\":[]}");
        var (name, payload) = Assert.Single(live.E.Published, p => p.Name == QsService.BtEvent);
        Assert.False(((JsonElement)payload!).TryGetProperty("ev", out _));
    }
}
