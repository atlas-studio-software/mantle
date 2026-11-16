using System;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Features.TopBar;

/// <summary>
/// --test fixture runs only (MANTLE_TEST_TOPBAR set): a made-up ping test, so a test run of the PING dropdown never pings
/// or downloads anything. It plays the sidecar's pingtest states in order (starting, ping, down, up, done), one step per
/// <see cref="StepMs"/>, with ui/topbar/pingtest-shots.js's rows and caps. Odd runs are on "Test Wi-Fi" with a quick
/// router; even runs on "Test Wi-Fi 5G" with a slow one (pingtest-shots' wifi-lag), so two runs give two networks, an
/// amber router and a different verdict. Nothing runs until a start; a cancel ends the run at once.
/// </summary>
public sealed class PingTestFake
{
    public const int StepMs = 350;
    static readonly string[] Phases = { "starting", "ping", "down", "up", "done" };
    static readonly double[] Progress = { 0, 0.3, 0.62, 0.85, 1 };

    int _run, _step;
    JsonObject? _cur;
    double _startedAt;

    public bool Running => _cur != null && _step < Phases.Length - 1 && (string?)_cur["phase"] != "cancelled";

    static JsonObject Caps() => new() { ["rounds"] = 20, ["periodMs"] = 750, ["downMs"] = 8000, ["downBytes"] = 60e6, ["upMs"] = 6000, ["upBytes"] = 25e6, ["answerMs"] = 5000, ["downStreams"] = 4, ["upStreams"] = 3 };

    static JsonObject Server(string name, string host, double avg, double min, double max, double jitter, double loss, bool best = false) => new()
    {
        ["kind"] = "server", ["name"] = name, ["host"] = host, ["via"] = "AWS", ["method"] = "icmp", ["sent"] = 20, ["got"] = Math.Round(20 * (100 - loss) / 100),
        ["avg"] = avg, ["min"] = min, ["max"] = max, ["jitter"] = jitter, ["loss"] = loss, ["best"] = best,
    };
    static JsonObject Router(double avg, double min, double max, double jitter, double loss) => new()
    {
        ["kind"] = "router", ["name"] = "Your router", ["host"] = "192.0.2.1", ["via"] = "Wi-Fi", ["iface"] = "Wi-Fi", ["wifi"] = true, ["method"] = "icmp",
        ["sent"] = 20, ["got"] = 20 - loss / 5, ["avg"] = avg, ["min"] = min, ["max"] = max, ["jitter"] = jitter, ["loss"] = loss,
    };
    static JsonObject[] Servers() => new[]
    {
        Server("US West (N. California)", "dynamodb.us-west-1.amazonaws.com", 15.8, 14, 19, 1.2, 0, true),
        Server("US West (Oregon)", "dynamodb.us-west-2.amazonaws.com", 36.1, 34, 41, 1.9, 0),
        Server("US Central (Texas)", "speedtest.dallas.linode.com", 57.3, 55, 63, 2, 0),
        Server("US Central (Illinois)", "speedtest.chicago.linode.com", 65, 63, 74, 2.6, 5),
        Server("US East (N. Virginia)", "dynamodb.us-east-1.amazonaws.com", 102.4, 77, 131, 12.5, 0),
        Server("US East (Georgia)", "speedtest.atlanta.linode.com", 70.2, 67, 79, 2.2, 0),
    };
    static JsonObject Sp(string state, double? mbps, double bytes, double? live = null, string? server = null) => new()
    {
        ["state"] = state, ["mbps"] = mbps, ["live"] = live, ["bytes"] = bytes, ["ms"] = 0, ["why"] = null, ["server"] = server,
    };
    JsonObject Wifi() => _run % 2 == 1
        ? new() { ["kind"] = "wifi", ["name"] = "Test Wi-Fi", ["why"] = null, ["iface"] = "test",
                  ["link"] = new JsonObject { ["kind"] = "wifi", ["band"] = "5 GHz", ["channel"] = 157, ["signal"] = 70, ["rxMbps"] = 1201, ["txMbps"] = 576, ["rssi"] = -55, ["phy"] = "802.11ax", ["bssid"] = "02:00:5e:00:00:01" } }
        : new() { ["kind"] = "wifi", ["name"] = "Test Wi-Fi 5G", ["why"] = null, ["iface"] = "test",
                  ["link"] = new JsonObject { ["kind"] = "wifi", ["band"] = "2.4 GHz", ["channel"] = 6, ["signal"] = 41, ["rxMbps"] = 144, ["txMbps"] = 72, ["rssi"] = -71, ["phy"] = "802.11n", ["bssid"] = "02:00:5e:00:00:02" } };

    JsonObject Snapshot(double now)
    {
        string phase = Phases[_step];
        bool running = _step < Phases.Length - 1;
        var router = _run % 2 == 1 ? Router(2.4, 1.6, 4.1, 0.6, 0) : Router(28.1, 2, 180, 26.4, 0);
        var rows = new JsonArray();
        if (_step >= 1) { rows.Add((JsonNode)router); foreach (var s in Servers()) rows.Add((JsonNode)s); }
        if (_step == 1)
            for (int i = 0; i < rows.Count; i++)
            {
                var r = (JsonObject)rows[i]!;
                if (i == rows.Count - 1) { r["sent"] = 0; r["got"] = 0; r["avg"] = null; r["min"] = null; r["max"] = null; r["jitter"] = null; r["loss"] = null; }   // its first ping is not back yet
                else { r["sent"] = 6; r["got"] = 6; r["loss"] = 0; }
                r["best"] = false;
            }
        var down = _step switch { < 2 => Sp("waiting", null, 0), 2 => Sp("running", null, 18e6, 58.3), _ => Sp("ok", 62, 60e6, server: "Cloudflare") };
        var up = _step switch { < 3 => Sp("waiting", null, 0), 3 => Sp("running", null, 9e6, 27.5), _ => Sp("ok", 30.8, 25e6, server: "Cloudflare") };
        return new JsonObject
        {
            ["run"] = _run, ["phase"] = phase, ["running"] = running, ["why"] = null, ["startedAt"] = _startedAt, ["endedAt"] = running ? null : now,
            ["progress"] = Progress[_step], ["label"] = "Valorant", ["listWhy"] = null, ["routerWhy"] = null, ["rows"] = rows, ["down"] = down, ["up"] = up,
            ["host"] = "speed.cloudflare.com", ["caps"] = Caps(), ["wifi"] = _step >= 1 ? Wifi() : null,
            ["test"] = "a made-up ping test (--test fixture): nothing was pinged or downloaded",
        };
    }

    static JsonObject Copy(JsonObject o) => (JsonObject)JsJson.Clone(o)!;

    /// action "state": the last run, or idle with the caps.
    public JsonObject State() => _cur != null ? Copy(_cur) : new JsonObject { ["phase"] = "idle", ["caps"] = Caps(), ["host"] = "speed.cloudflare.com" };

    /// action "start": the starting snapshot, or why not (the sidecar's own words).
    public (JsonObject? Snap, string? Refusal) Start(double nowEpochMs)
    {
        if (Running) return (null, "a ping test is already running");
        _run++; _step = 0; _startedAt = nowEpochMs;
        _cur = Snapshot(nowEpochMs);
        return (Copy(_cur), null);
    }

    /// The next step's snapshot while a run is on; null when none is running.
    public JsonObject? Step(double nowEpochMs)
    {
        if (!Running) return null;
        _step++;
        _cur = Snapshot(nowEpochMs);
        return Copy(_cur);
    }

    /// action "cancel": the cancelled snapshot (what it had); with no run on, the last state (the sidecar refuses nothing).
    public JsonObject Cancel(double nowEpochMs)
    {
        if (!Running) return State();
        var s = Snapshot(nowEpochMs);
        s["phase"] = "cancelled"; s["running"] = false; s["endedAt"] = nowEpochMs;
        foreach (var k in new[] { "down", "up" }) if (s[k] is JsonObject x && (string?)x["state"] is "waiting" or "running") x["state"] = "cancelled";
        _cur = s;
        return Copy(s);
    }
}
