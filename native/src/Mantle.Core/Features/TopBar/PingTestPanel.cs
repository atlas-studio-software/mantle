using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;

namespace Mantle.Core.Features.TopBar;

// ui/topbar/pingtest.js (TOP-139..159): "Run ping test" and its history, below a separator at the end of the PING
// dropdown. The sidecar runs the test (sidecar/PingTest.cs, topbar.pingtest) and keeps the last run; main keeps the
// history (PingTestHistory). This is the page half as a pure machine: the last run and the history are pushed in, a row
// press is Act, the name box's keys are Key, and Rows() is what the dropdown shows (the surface draws it). Closing the
// dropdown does NOT cancel: the run goes on and its result is there on reopen, with its time, until the next run.

public enum PtKind { Sep, Tabs, Head, Info, Progress, Table, Row2, Go, NameBox, Chips, HistRow }

/// One cell of a table, a tab, a chip or a history row's part. Cls: "h" header, "hi" amber, "rt" the router (bold),
/// "wait", "worse", "better", "on" (the chosen tab / chip), "hid" (a hidden name, ink-3 italic), "b" bold.
public sealed record PtCell(string Text, string Cls = "", string? Tip = null, int Span = 1, string? Badge = null, string? Act = null)
{
    public string Sig() => $"{Text}\u0001{Cls}\u0001{Tip}\u0001{Span}\u0001{Badge}\u0001{Act}";
}

/// One line of the ping test's part. Cls on Info: "bad" (warn glyph + warn ink), "step", "verdict", "key", "net",
/// "link", "hid", "cl", "worse", "better"; on Go: "quiet" (ink-2 words), "armed" (warn words); on Row2: "hi".
public sealed record PtRow(PtKind Kind, string Text = "", string? Value = null, string Cls = "", string? Act = null, string? Tip = null,
    double Progress = 0, IReadOnlyList<PtCell>? Cells = null)
{
    public string Sig()
    {
        var sb = new StringBuilder();
        sb.Append((int)Kind).Append('\u0002').Append(Text).Append('\u0002').Append(Value).Append('\u0002').Append(Cls).Append('\u0002').Append(Act)
          .Append('\u0002').Append(Tip).Append('\u0002').Append(Progress.ToString("R", CultureInfo.InvariantCulture));
        if (Cells != null) foreach (var c in Cells) sb.Append('\u0003').Append(c.Sig());
        return sb.ToString();
    }
}

public sealed class PingTestPanel
{
    // Amber for a bad value. Servers: average >= 100 ms, jitter >= 10 ms, any loss. The router: average >= 20 ms.
    public const double WarnAvg = 100, WarnRouterAvg = 20, WarnJitter = 10, WarnLoss = 0;
    public const double ReadRetryMs = 5000;
    static readonly JsonObject DefaultCaps = new() { ["rounds"] = 20, ["periodMs"] = 750, ["downMs"] = 7000, ["downBytes"] = 150e6, ["upMs"] = 6000, ["upBytes"] = 20e6, ["answerMs"] = 5000 };

    /// One request to main: topbar.pingTest / topbar.pingTests. The answer object (a failed call = {ok: false, reason}).
    public delegate Task<JsonObject> CallFn(string verb, JsonObject args);

    public sealed class EditState { public string Id = ""; public string Mode = "save"; public string Value = ""; public int? A, B; }

    readonly CallFn _call;
    readonly Action _rerender;
    readonly Func<double> _now;
    JsonObject? _st;          // the sidecar's last pingtest state (phase "idle" before any run)
    string? _err;             // a refused start / cancel / read, in words
    bool _asking;             // a state read in flight
    double _readFailedAt;     // the last failed state read (retried at most every 5 s while the dropdown draws)

    // the history (TOP-153..159): main keeps it and pushes topbar.pingtests on every change
    public string Tab { get; private set; } = "test";
    public JsonObject? List { get; private set; }
    public string? Open { get; private set; }
    public EditState? Edit { get; private set; }
    public string? Armed { get; private set; }
    public string? Filter { get; private set; }
    public string? Msg { get; private set; }
    public string? Pick { get; private set; }
    public string[]? Cmp { get; private set; }

    public PingTestPanel(CallFn call, Action rerender, Func<double> now)
    {
        _call = call; _rerender = rerender; _now = now;
    }

    public JsonObject? State => _st;
    public string? Error => _err;

    /// init(): the last run and the history, read once at load.
    public Task Init() => Task.WhenAll(Read(), Hist(new JsonObject { ["action"] = "list" }));

    /// topbar.pingtest: the whole run state.
    public void OnRun(JsonObject? s) { if (s == null) return; _st = s; _err = null; _rerender(); }

    /// topbar.pingtests: the whole history.
    public void OnHistory(JsonObject? v) { if (v?["tests"] is JsonArray) { List = v; _rerender(); } }

    static bool Ok(JsonObject? r) => !(r?["ok"] is JsonValue v && v.GetValueKind() == JsonValueKind.False);
    static string Reason(JsonObject? r) => S(r, "reason") ?? "no reason came back";

    async Task<JsonObject?> Hist(JsonObject args)
    {
        var r = await _call("topbar.pingTests", args);
        if (r["history"] is JsonObject h && h["tests"] is JsonArray) List = (JsonObject)JsJson.Clone(h)!;
        Msg = !Ok(r) ? $"{(S(args, "action") == "list" ? "The history could not be read" : "That did not happen")}: {Reason(r)}" : null;
        _rerender();
        return r;
    }

    /// The last run, from the sidecar (it keeps it): on load, and again while drawing if that read failed.
    public async Task Read()
    {
        if (_asking) return;
        _asking = true;
        JsonObject r;
        try { r = await _call("topbar.pingTest", new JsonObject { ["action"] = "state" }); }
        finally { _asking = false; }
        if (!Ok(r)) { _readFailedAt = _now(); _err = $"The last ping test could not be read: {Reason(r)}"; _rerender(); return; }
        if (_readFailedAt != 0) { _readFailedAt = 0; _err = null; _rerender(); }
        if (S(r, "phase") != null && (_st == null || D(r, "run") >= D(_st, "run"))) { _st = r; _err = null; _rerender(); }
    }

    /// A row press whose act starts "pt-".
    public static bool Owns(string? act) => act != null && act.StartsWith("pt-", StringComparison.Ordinal);

    public async Task Act(string name)
    {
        if (!Owns(name)) return;
        int c = name.IndexOf(':');
        string verb = c < 0 ? name : name[..c], arg = c < 0 ? "" : name[(c + 1)..];
        if (verb != "pt-start" && verb != "pt-cancel") { await HistAct(verb, arg); return; }
        string action = verb == "pt-cancel" ? "cancel" : "start";
        if (action == "start")
        {
            var s = _st == null ? new JsonObject() : (JsonObject)JsJson.Clone(_st)!;
            s["phase"] = "starting"; s["running"] = true; s["rows"] = new JsonArray(); s["progress"] = 0; s["run"] = D(_st, "run") + 0.5;
            _st = s; _err = null; _rerender();
        }
        var r = await _call("topbar.pingTest", new JsonObject { ["action"] = action });
        if (!Ok(r))
        {
            _err = $"The ping test {(action == "start" ? "could not start" : "could not be cancelled")}: {Reason(r)}";
            if (action == "start") _ = Read();   // put back what the sidecar really has
        }
        else if (S(r, "phase") != null && (_st == null || D(r, "run") >= Math.Floor(D(_st, "run")))) _st = r;
        _rerender();
    }

    JsonArray Tests() => List?["tests"] as JsonArray ?? new JsonArray();
    JsonObject? Entry(string? id) => id == null ? null : Tests().OfType<JsonObject>().FirstOrDefault(x => S(x, "id") == id);

    async Task HistAct(string verb, string arg)
    {
        if (verb != "pt-del") Armed = null;
        switch (verb)
        {
            case "pt-tab":
                Tab = arg == "history" ? "history" : "test"; Open = null; Edit = null; Msg = null; Pick = null; Cmp = null;
                if (Tab == "history") { _rerender(); await Hist(new JsonObject { ["action"] = "list" }); return; }
                break;
            case "pt-cmp": Pick = arg; Open = null; Cmp = null; Edit = null; Msg = null; break;
            case "pt-cmpto": if (Pick != null && arg != Pick) Cmp = new[] { Pick, arg }; Pick = null; Open = null; break;
            case "pt-uncmp": Pick = null; Cmp = null; break;
            case "pt-open": Open = arg; Edit = null; Msg = null; break;
            case "pt-back": Open = null; Edit = null; Msg = null; Pick = null; Cmp = null; break;
            case "pt-filter":
            {
                var ws = WifiLabels(Tests());
                Filter = arg == "all" ? null : int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k < ws.Count ? ws[k] : null;
                break;
            }
            case "pt-save": case "pt-rename":
            {
                var e = Entry(arg);
                string v = verb == "pt-rename" && e != null && S(e, "name") is { Length: > 0 } nm ? nm : "";
                Edit = new EditState { Id = arg, Mode = verb == "pt-save" ? "save" : "rename", Value = v, A = v.Length, B = v.Length };
                Msg = null;
                break;
            }
            case "pt-commit": await Commit(); return;
            case "pt-uncommit": Edit = null; break;
            case "pt-del":
            {
                if (Armed != arg) { Armed = arg; _rerender(); return; }   // Delete asks once: the second press deletes
                Armed = null;
                var r = await Hist(new JsonObject { ["action"] = "delete", ["id"] = arg });
                if (Ok(r) && Open == arg) { Open = null; _rerender(); }
                return;
            }
        }
        _rerender();
    }

    async Task Commit()
    {
        var ed = Edit;
        if (ed == null) return;
        var r = await Hist(new JsonObject { ["action"] = ed.Mode, ["id"] = ed.Id, ["name"] = ed.Value });
        if (Ok(r) && Edit == ed) { Edit = null; _rerender(); }
    }

    /// The name box's text and selection as typed (kept through every re-render).
    public void Typed(string value, int a, int b) { if (Edit == null) return; Edit.Value = value; Edit.A = a; Edit.B = b; }

    /// A key typed into the name box (TOP-091 routes these first; TOP-154): Enter saves, Escape leaves the box with the
    /// dropdown open; every other key is the box's own. False when no name box is open.
    public bool Key(string key, out Task pending)
    {
        pending = Task.CompletedTask;
        if (Edit == null) return false;
        if (key == "Enter") pending = Commit();
        else if (key == "Escape") { Edit = null; _rerender(); }
        return true;
    }

    // ------------------------------------------------------------------ JSON reads, as JS does them
    static JsonNode? G(JsonObject? o, string k) => o != null && o.TryGetPropertyValue(k, out var v) ? v : null;
    static string? S(JsonObject? o, string k) => G(o, k) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
    static double? N(JsonObject? o, string k) => Number(G(o, k));
    /// A JSON number as a double, whatever CLR type the node holds (an in-memory JsonValue made from an int throws on
    /// GetValue&lt;double&gt;), else null.
    public static double? Number(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    static double D(JsonObject? o, string k) => N(o, k) ?? 0;
    static bool T(JsonObject? o, string k) => G(o, k) switch
    {
        null => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length > 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Number(v) is double d && d != 0 && !double.IsNaN(d),
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => false,
        _ => true,
    };
    static string Js(double v) => JsJson.Number(v);
    /// JS's String(v) for a JSON value: a number as JS prints it, a string as is.
    static string Str(JsonNode? v) => v is JsonValue x ? x.GetValueKind() switch
    {
        JsonValueKind.Number => Js(Number(x) ?? 0), JsonValueKind.String => x.GetValue<string>(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => "null",
    } : v == null ? "null" : v.ToJsonString();

    // ------------------------------------------------------------------ numbers in words
    static double JsRound(double v) => Math.Floor(v + 0.5);
    public static string Num(double? v) => v is not double d ? "--" : d < 10 ? (JsRound(d * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture) : Js(JsRound(d));
    public static string Ms(double? v) => v == null ? "--" : Num(v) + " ms";
    static string MbpsN(double? v) => v is not double d ? "--" : d >= 100 ? Js(JsRound(d)) : (JsRound(d * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture);
    public static string Mbps(double? v) => v == null ? "--" : MbpsN(v) + " Mbps";
    static string Mb(double b) => Js(JsRound(b / 1e6)) + " MB";
    static DateTime Local(double t) => DateTimeOffset.FromUnixTimeMilliseconds((long)t).ToLocalTime().DateTime;
    /// toLocaleTimeString([], {hour: 'numeric', minute: '2-digit'}).
    public static string Clock(double t) { try { return Local(t).ToString("t", CultureInfo.CurrentCulture); } catch (ArgumentOutOfRangeException) { return ""; } }
    /// toLocaleString([], {month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit'}).
    public static string Stamp(double t)
    {
        try { var d = Local(t); return d.ToString("MMM d", CultureInfo.CurrentCulture) + ", " + d.ToString("t", CultureInfo.CurrentCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    // ------------------------------------------------------------------ pure: what is bad, and what it means
    public static (bool Avg, bool Jitter, bool Loss) Bad(JsonObject? row)
    {
        bool router = S(row, "kind") == "router";
        return (N(row, "avg") is double a && a >= (router ? WarnRouterAvg : WarnAvg),
                N(row, "jitter") is double j && j >= WarnJitter,
                N(row, "loss") is double l && l > WarnLoss);
    }
    public static bool AnyBad(JsonObject? row) { var b = Bad(row); return b.Avg || b.Jitter || b.Loss; }

    /// One line: is the lag at home (the router is slow) or out on the internet? null when the data cannot say.
    public static (bool Bad, string Text)? Verdict(JsonObject? s)
    {
        var rows = (G(s, "rows") as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var router = rows.FirstOrDefault(r => S(r, "kind") == "router");
        var servers = rows.Where(r => S(r, "kind") == "server" && D(r, "got") > 0).ToList();
        if (router == null || !T(router, "got") || servers.Count == 0) return null;
        var best = servers.FirstOrDefault(r => T(r, "best")) ?? servers.OrderBy(r => D(r, "avg")).First();
        string home = T(router, "wifi") ? "your Wi-Fi" : "your home network";
        if (AnyBad(router))
        {
            var b = Bad(router); var how = new List<string>();
            if (b.Avg) how.Add($"takes {Ms(N(router, "avg"))} on average");
            if (b.Jitter) how.Add($"jumps by {Ms(N(router, "jitter"))}");
            if (b.Loss) how.Add($"lost {Str(G(router, "loss"))}% of the pings");
            return (true, $"The lag starts at {home}: your own router {string.Join(", ", how)}.");
        }
        if (AnyBad(best)) return (true, $"Your router answers in {Ms(N(router, "avg"))}, so {home} is fine: the lag is out on the internet.");
        return (false, $"No lag found: your router answers in {Ms(N(router, "avg"))} and the closest server in {Ms(N(best, "avg"))}.");
    }

    JsonObject Caps() => G(_st, "caps") as JsonObject ?? DefaultCaps;

    string StepWords(JsonObject s)
    {
        int n = (G(s, "rows") as JsonArray)?.Count ?? 0;
        var d = G(s, "down") as JsonObject; var u = G(s, "up") as JsonObject;
        return S(s, "phase") switch
        {
            "starting" => "Finding your router and the servers…",
            "ping" => $"Pinging {n} place{(n == 1 ? "" : "s")}, {Str(G(Caps(), "rounds"))} times each…",
            "down" => $"Download speed{(T(d, "server") ? " from " + S(d, "server") : "")}… {(N(d, "live") is double dl ? Mbps(dl) : "")}",
            "up" => $"Upload speed{(T(u, "server") ? " to " + S(u, "server") : "")}… {(N(u, "live") is double ul ? Mbps(ul) : "")}",
            _ => "",
        };
    }

    static PtRow? Table(JsonObject s)
    {
        var rows = (G(s, "rows") as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (rows.Count == 0) return null;
        string label = S(s, "label") is { Length: > 0 } l ? l : "Ping";
        var cells = new List<PtCell>
        {
            new("Place", "h"), new("Avg", "h"), new("Best–worst", "h", "The fastest and the slowest reply"),
            new("Jitter", "h", "How much the ping jumps from one reply to the next"), new("Loss", "h", "Pings that never came back"),
        };
        foreach (var r in rows)
        {
            var b = Bad(r);
            bool router = S(r, "kind") == "router";
            bool dead = !T(r, "got") && D(r, "sent") > 0;
            string name = router ? "Your router" : Str(G(r, "name"));
            string tip = router ? $"{Str(G(r, "host"))}{(T(r, "iface") ? " on " + Str(G(r, "iface")) : "")}"
                : $"{label} {Str(G(r, "name"))}: {Str(G(r, "host"))}{(T(r, "via") ? " (" + Str(G(r, "via")) + ")" : "")}{(S(r, "method") == "tcp" ? ", by connection time" : "")}";
            cells.Add(new(name, router ? "rt" : "", tip, 1, T(r, "best") ? "best" : null));
            if (dead || (T(r, "why") && !T(r, "got")))
            {
                string why = S(r, "why") ?? "";
                cells.Add(new(why.Contains("look up", StringComparison.Ordinal) ? "name not found" : "no reply", "hi", why, 4));
                continue;
            }
            if (!T(r, "sent")) { cells.Add(new("waiting…", "wait", null, 4)); continue; }
            cells.Add(new(Ms(N(r, "avg")), b.Avg ? "hi" : ""));
            cells.Add(new(N(r, "min") != null ? Num(N(r, "min")) + "–" + Num(N(r, "max")) : "--"));
            cells.Add(new(Ms(N(r, "jitter")), b.Jitter ? "hi" : ""));
            cells.Add(new(N(r, "loss") != null ? Str(G(r, "loss")) + "%" : "--", b.Loss ? "hi" : ""));
        }
        return new PtRow(PtKind.Table, Cells: cells);
    }

    static string SpeedWord(JsonObject x, bool running) => S(x, "state") switch
    {
        "ok" => Mbps(N(x, "mbps")),
        "running" => N(x, "live") is double lv ? Mbps(lv) : "measuring…",
        "waiting" => running ? "next" : "not run",
        "cancelled" => "cancelled",
        "skipped" => "skipped",
        _ => "failed",
    };

    static void Speed(List<PtRow> o, JsonObject s, bool running)
    {
        var d = G(s, "down") as JsonObject ?? new JsonObject(); var u = G(s, "up") as JsonObject ?? new JsonObject();
        if (!T(d, "state") || !T(u, "state") || (S(d, "state") == "waiting" && S(u, "state") == "waiting")) return;   // before any run, or before the speed phase
        o.Add(new PtRow(PtKind.Head, "Speed"));
        foreach (var (n, x) in new[] { ("Download", d), ("Upload", u) })
            o.Add(new PtRow(PtKind.Row2, n, SpeedWord(x, running), S(x, "state") is "failed" or "skipped" ? "hi" : ""));
        foreach (var (n, x) in new[] { ("Download", d), ("Upload", u) })
        {
            // which server measured it, and why a fallback was used (Cloudflare can answer 429)
            if (S(x, "state") != "ok" || !T(x, "server")) continue;
            o.Add(new PtRow(PtKind.Info, $"{n}: {Mbps(N(x, "mbps"))} ({Str(G(x, "server"))}{(T(x, "note") ? "; " + Str(G(x, "note")) : "")})", Tip: T(x, "pick") ? "Region: " + Str(G(x, "pick")) : null));
        }
    }

    /// The servers the speed test used, by name: "Linode Fremont and Cloudflare".
    static string UsedWith(JsonObject s)
    {
        var names = new List<string>();
        foreach (var x in new[] { G(s, "down") as JsonObject, G(s, "up") as JsonObject })
            if (x != null && T(x, "bytes") && (T(x, "server") || T(x, "host"))) { string n = T(x, "server") ? Str(G(x, "server")) : Str(G(x, "host")); if (!names.Contains(n)) names.Add(n); }
        return names.Count > 0 ? string.Join(" and ", names) : S(s, "host") is { Length: > 0 } h ? h : "speed.cloudflare.com";
    }

    /// The network a test ran on, in words; short = for a history row. Never a blank.
    public static string NetWords(JsonObject? w, bool shortForm)
    {
        if (w == null) return shortForm ? "network not recorded" : "The network was not recorded for this test.";
        string kind = S(w, "kind") ?? "";
        if (T(w, "name")) { string nm = Str(G(w, "name")); return shortForm ? nm : kind == "wifi" ? $"On Wi-Fi “{nm}”" : kind == "ethernet" ? "On Ethernet (a cable)" : $"On {nm}"; }
        if (shortForm) return kind == "wifi" ? "Wi-Fi (name hidden)" : "no network";
        return T(w, "why") ? Str(G(w, "why")) : kind == "wifi" ? "On Wi-Fi: Windows did not give its name" : "No network was found";
    }

    static readonly Dictionary<string, string> LinkNames = new()
    {
        ["bssid"] = "access point", ["signal"] = "signal", ["rssi"] = "RSSI", ["band"] = "band", ["channel"] = "channel",
        ["phy"] = "Wi-Fi standard", ["rxMbps"] = "receive rate", ["txMbps"] = "send rate",
    };

    /// The link a test ran on. Wi-Fi: "5 GHz · ch 157 · 70% · 1201/576 Mbps · -55 dBm · PHY · access point bssid" (short:
    /// band and signal); Ethernet: "cable · 1000 Mbps". A link Windows would not give: its reason. "" = no link recorded.
    public static string LinkWords(JsonObject? l, bool shortForm)
    {
        if (l == null) return "";
        if (T(l, "why")) return shortForm ? "" : Str(G(l, "why"));
        if (S(l, "kind") == "ethernet") return $"cable · {Str(G(l, "linkMbps"))} Mbps";
        bool hasRx = G(l, "rxMbps") != null, hasTx = G(l, "txMbps") != null;
        string? rate = hasRx || hasTx ? $"{(hasRx ? Str(G(l, "rxMbps")) : "?")}/{(hasTx ? Str(G(l, "txMbps")) : "?")} Mbps" : null;
        string? band = T(l, "band") ? Str(G(l, "band")) : null;
        string? sig = G(l, "signal") != null ? Str(G(l, "signal")) + "%" : null;
        if (shortForm) return string.Join(" ", new[] { band, sig }.Where(x => !string.IsNullOrEmpty(x)));
        var parts = new[] { band, G(l, "channel") != null ? "ch " + Str(G(l, "channel")) : null, sig, rate,
            G(l, "rssi") != null ? Str(G(l, "rssi")) + " dBm" : null, T(l, "phy") ? Str(G(l, "phy")) : null, T(l, "bssid") ? "access point " + Str(G(l, "bssid")) : null };
        string t = string.Join(" · ", parts.Where(x => !string.IsNullOrEmpty(x)));
        if (G(l, "missing") is JsonObject miss && miss.Count > 0)
            t += ". Not given: " + string.Join(", ", miss.Select(kv => (LinkNames.TryGetValue(kv.Key, out var nm) ? nm : kv.Key) + " (" + Str(kv.Value) + ")"));
        return t;
    }

    public static List<string> WifiLabels(JsonArray tests)
    {
        var seen = new List<string>();
        foreach (var t in tests.OfType<JsonObject>()) { var k = NetWords(G(t, "wifi") as JsonObject, true); if (!seen.Contains(k)) seen.Add(k); }
        return seen;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static PtRow Go(string act, string label, string kb = "", string cls = "") => new(PtKind.Go, label, kb, cls, act);
    static PtRow Info(string text, string cls = "", string? tip = null) => new(PtKind.Info, text, Cls: cls, Tip: tip);

    /// A finished (or running) test's body: the network, the rows, the router line, the speeds, every reason.
    static void Result(List<PtRow> o, JsonObject s, bool running)
    {
        string phase = S(s, "phase") ?? "";
        bool ran = !running && phase != "idle" && phase != "starting";
        var wifi = G(s, "wifi") as JsonObject;
        if (wifi != null || ran) o.Add(Info(NetWords(wifi, false), "net" + (wifi != null && !T(wifi, "name") ? " hid" : "")));
        var link = G(wifi, "link") as JsonObject;
        string lw = wifi != null ? LinkWords(link, false) : "";
        if (lw.Length > 0) o.Add(Info(lw, "link" + (T(link, "why") ? " hid" : ""), T(link, "rssiFrom") ? "RSSI from " + Str(G(link, "rssiFrom")) : null));
        if (T(s, "wifiChanged")) o.Add(Info(Cap(Str(G(s, "wifiChanged"))), "bad"));
        if (T(s, "linkChanged")) o.Add(Info(Cap(Str(G(s, "linkChanged"))), "bad"));
        if (T(s, "why")) o.Add(Info(Str(G(s, "why")), "bad"));
        if (Table(s) is { } tbl) o.Add(tbl);
        if (T(s, "routerWhy")) o.Add(Info(Str(G(s, "routerWhy")), "bad"));
        if (T(s, "listWhy")) o.Add(Info(Str(G(s, "listWhy")), "bad"));
        var rows = (G(s, "rows") as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        foreach (var r in rows.Where(r => !T(r, "got") && T(r, "why") && (!running || T(r, "gaveUp"))))
            o.Add(Info($"{(S(r, "kind") == "router" ? "Your router" : Str(G(r, "name")))}: {Str(G(r, "why"))}", "bad"));
        if (!running && phase != "ping" && Verdict(s) is { } v) o.Add(Info(v.Text, "verdict" + (v.Bad ? " bad" : "")));
        if (rows.Count > 0 && (ran || phase is "down" or "up"))
            o.Add(Info($"Jitter: how much the ping jumps between replies. Loss: pings that never came back. Amber: {Js(WarnAvg)}+ ms (router {Js(WarnRouterAvg)}+), jitter {Js(WarnJitter)}+ ms, any loss.", "key"));
        Speed(o, s, running);
        foreach (var x in new[] { G(s, "down") as JsonObject, G(s, "up") as JsonObject }) if (T(x, "why")) o.Add(Info($"Speed test, {Str(G(x, "why"))}", "bad"));
        var dn = G(s, "down") as JsonObject; var up = G(s, "up") as JsonObject;
        if (ran && (T(dn, "bytes") || T(up, "bytes")))
            o.Add(Info($"Used {Mb(D(dn, "bytes") + D(up, "bytes"))} of data with {UsedWith(s)}."));
    }

    /// The name box (save / rename), or the Save… / Rename… / Delete rows, for one history entry.
    void EntryActions(List<PtRow> o, JsonObject e, bool full)
    {
        string id = S(e, "id") ?? "";
        if (Edit != null && Edit.Id == id)
        {
            o.Add(new PtRow(PtKind.NameBox, "Name this test, e.g. upstairs", Edit.Value));
            o.Add(Go("pt-commit", Edit.Mode == "save" ? "Save" : "Rename", "Enter"));
            o.Add(Go("pt-uncommit", "Cancel", "Esc", "quiet"));
            return;
        }
        if (!T(e, "saved")) o.Add(Go($"pt-save:{id}", "Save…", "name it, keep it for good"));
        else o.Add(Go($"pt-rename:{id}", "Rename…"));
        if (full) o.Add(Go($"pt-del:{id}", Armed == id ? "Delete: click again to confirm" : "Delete", "", Armed == id ? "armed" : "quiet"));
    }

    void TestRows(List<PtRow> o)
    {
        if (_readFailedAt != 0 && !_asking && _now() - _readFailedAt > ReadRetryMs) _ = Read();   // e.g. the sidecar was not up yet at load
        var s = _st ?? new JsonObject { ["phase"] = "idle" };
        var c = Caps();
        double total = JsRound((D(c, "downBytes") + D(c, "upBytes")) / 1e6);
        double secs = JsRound((D(c, "rounds") * D(c, "periodMs") + D(c, "downMs") + D(c, "upMs")) / 1000 / 5) * 5;
        bool running = T(s, "running");
        string phase = S(s, "phase") ?? "";
        bool ran = !running && phase != "idle" && phase != "starting";
        string when = T(s, "endedAt") ? Clock(D(s, "endedAt")) : T(s, "startedAt") ? Clock(D(s, "startedAt")) : "";
        string? id = T(s, "startedAt") && T(s, "run") ? PingTestHistory.Id(G(s, "startedAt"), G(s, "run")) : null;
        var kept = id != null && List != null ? Entry(id) : null;
        o.Add(new PtRow(PtKind.Head, "Ping test" + (running ? "" : phase == "done" ? " · " + when : phase == "cancelled" ? " · cancelled " + when : phase == "failed" ? " · failed " + when : "")));
        if (kept != null && T(kept, "saved")) o.Add(Info($"Saved as “{Str(G(kept, "name"))}”."));
        if (running)
        {
            o.Add(new PtRow(PtKind.Progress, Progress: JsRound(Math.Max(0, Math.Min(1, D(s, "progress"))) * 1000) / 1000));
            o.Add(Info(StepWords(s), "step"));
        }
        Result(o, s, running);
        if (phase == "cancelled") o.Add(Info("Cancelled: what it measured before that is above."));
        if (T(s, "test")) o.Add(Info(Str(G(s, "test"))));
        if (_err != null) o.Add(Info(_err, "bad"));
        if (Msg != null) o.Add(Info(Msg, "bad"));
        if (running) o.Add(Go("pt-cancel", "Cancel"));
        else
        {
            if (kept != null) EntryActions(o, kept, false);
            o.Add(Go("pt-start", ran ? "Run again" : "Run ping test", $"about {Js(secs)} s · up to {Js(total)} MB"));
            if (!ran)
                o.Add(Info($"Pings every {(S(s, "label") is { Length: > 0 } lb ? lb : "Valorant")} location and your router {Str(G(c, "rounds"))} times, then measures download and upload speed with {(S(s, "host") is { Length: > 0 } h ? h : "speed.cloudflare.com")} (download from a nearby Linode server if Cloudflare is busy). Uses up to {Js(total)} MB of data."));
        }
    }

    // ------------------------------------------------------------------ compare two tests (A = older, B = newer)
    /// -1 better, 1 worse, 0 about the same, null unknown. lower: lower is better.
    public static int? Judge(double? a, double? b, bool lower, double same)
    {
        if (a is not double x || b is not double y) return null;
        double d = y - x;
        if (Math.Abs(d) <= same) return 0;
        return (d < 0) == lower ? -1 : 1;
    }
    static string JCls(int? j) => j == 1 ? "worse" : j == -1 ? "better" : "";

    static PtRow CmpLine(string label, double? a, double? b, string unit, bool lower, double same, string extra = "")
    {
        var j = Judge(a, b, lower, same);
        double? d = a != null && b != null ? Math.Abs(JsRound((b.Value - a.Value) * 10) / 10) : null;
        string word = j == null ? "not measured in both" : j == 0 ? "about the same"
            : $"{(unit == "Mbps" ? MbpsN(d) : Num(d))}{(unit == "%" ? " points" : " " + unit)} {(j == -1 ? "better" : "worse")}";
        string V(double? x) => x == null ? "--" : unit == "Mbps" ? MbpsN(x) : Num(x);
        return Info($"{label}: {V(a)} → {V(b)} {unit}, {word}{(extra.Length > 0 ? " " + extra : "")}.", ("cl " + JCls(j)).Trim());
    }

    static string Nm(JsonObject t) => T(t, "saved") ? "“" + Str(G(t, "name")) + "”" : Stamp(D(t, "at"));

    static void Compare(List<PtRow> o, JsonObject x, JsonObject y)
    {
        var (a, b) = D(x, "at") <= D(y, "at") ? (x, y) : (y, x);
        o.Add(new PtRow(PtKind.Head, "Compare"));
        o.Add(Info($"A (before): {Nm(a)}{(T(a, "saved") ? " · " + Stamp(D(a, "at")) : "")}\nB (after): {Nm(b)}{(T(b, "saved") ? " · " + Stamp(D(b, "at")) : "")}"));
        string wa = NetWords(G(a, "wifi") as JsonObject, true), wb = NetWords(G(b, "wifi") as JsonObject, true);
        o.Add(Info($"Wi-Fi: {wa}{(wa == wb ? " both times" : " → " + wb)}.", "cl"));
        var la = G(G(a, "wifi") as JsonObject, "link") as JsonObject; var lb = G(G(b, "wifi") as JsonObject, "link") as JsonObject;
        static string Lw(JsonObject? l) { if (l == null) return "not recorded"; var t = LinkWords(l, false); int k = t.IndexOf(". Not given: ", StringComparison.Ordinal); return k >= 0 ? t[..k] : t; }
        o.Add(Info($"Link: {Lw(la)} → {Lw(lb)}.", "cl"));
        if (la != null && lb != null && !T(la, "why") && !T(lb, "why") && S(la, "kind") == "wifi" && S(lb, "kind") == "wifi")
        {
            o.Add(CmpLine("Signal", N(la, "signal"), N(lb, "signal"), "%", false, 4));
            o.Add(CmpLine("Receive rate", N(la, "rxMbps"), N(lb, "rxMbps"), "Mbps", false, Math.Max(1, 0.05 * D(la, "rxMbps"))));
            o.Add(CmpLine("Send rate", N(la, "txMbps"), N(lb, "txMbps"), "Mbps", false, Math.Max(1, 0.05 * D(la, "txMbps"))));
        }
        static string Srv(JsonObject? p, JsonObject? q)
        {
            string? s1 = T(p, "server") ? Str(G(p, "server")) : null, s2 = T(q, "server") ? Str(G(q, "server")) : null;
            return s1 != null && s2 != null ? (s1 == s2 ? $"({s1} both times)" : $"({s1} → {s2}: different servers, so compare with care)") : "";
        }
        var ad = G(a, "down") as JsonObject; var bd = G(b, "down") as JsonObject; var au = G(a, "up") as JsonObject; var bu = G(b, "up") as JsonObject;
        o.Add(CmpLine("Download", N(ad, "mbps"), N(bd, "mbps"), "Mbps", false, Math.Max(0.5, 0.05 * D(ad, "mbps")), Srv(ad, bd)));
        o.Add(CmpLine("Upload", N(au, "mbps"), N(bu, "mbps"), "Mbps", false, Math.Max(0.5, 0.05 * D(au, "mbps")), Srv(au, bu)));
        static string Key(JsonObject r) => S(r, "kind") == "router" ? "\u0000router" : Str(G(r, "name"));
        var ra = (G(a, "rows") as JsonArray)?.OfType<JsonObject>().ToList() ?? new(); var rb = (G(b, "rows") as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var names = new List<string>();
        foreach (var r in ra.Concat(rb)) if (!names.Contains(Key(r))) names.Add(Key(r));
        if (names.Count > 0)
        {
            var cells = new List<PtCell> { new("Place", "h"), new("Avg ms", "h"), new("Change", "h"), new("Jitter ms", "h"), new("Loss %", "h") };
            static string Fmt(double? v) => v == null ? "--" : Num(v);
            foreach (var k in names)
            {
                var pa = ra.FirstOrDefault(r => Key(r) == k) ?? new JsonObject(); var pb = rb.FirstOrDefault(r => Key(r) == k) ?? new JsonObject();
                double? avA = T(pa, "got") ? N(pa, "avg") : null, avB = T(pb, "got") ? N(pb, "avg") : null;
                var jAvg = Judge(avA, avB, true, Math.Max(1, 0.05 * (avA ?? 0)));
                double? jiA = T(pa, "got") ? N(pa, "jitter") : null, jiB = T(pb, "got") ? N(pb, "jitter") : null;
                var jJit = Judge(jiA, jiB, true, 0.5);
                var jLoss = Judge(N(pa, "loss"), N(pb, "loss"), true, 0);
                double? d = avA != null && avB != null ? Math.Abs(JsRound((avB.Value - avA.Value) * 10) / 10) : null;
                string change = jAvg == null ? "n/a" : jAvg == 0 ? "same" : $"{Num(d)} ms {(jAvg == -1 ? "faster" : "slower")}";
                cells.Add(new(k == "\u0000router" ? "Your router" : k));
                cells.Add(new($"{Fmt(avA)} → {Fmt(avB)}", JCls(jAvg)));
                cells.Add(new(change, JCls(jAvg)));
                cells.Add(new($"{Fmt(jiA)} → {Fmt(jiB)}", JCls(jJit)));
                cells.Add(new($"{(G(pa, "loss") != null ? Str(G(pa, "loss")) : "--")} → {(G(pb, "loss") != null ? Str(G(pb, "loss")) : "--")}", JCls(jLoss)));
            }
            o.Add(new PtRow(PtKind.Table, "compare", Cells: cells));
        }
        o.Add(Info("Amber: worse in B than in A. Small moves (under about 5%, 1 ms, or 0.5 ms of jitter) count as about the same.", "key"));
    }

    void HistoryRows(List<PtRow> o)
    {
        var v = List ?? new JsonObject { ["tests"] = new JsonArray(), ["problem"] = null, ["max"] = 50 };
        var tests = (G(v, "tests") as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        string max = T(v, "max") ? Str(G(v, "max")) : "50";
        if (T(v, "problem")) o.Add(Info(Cap(Str(G(v, "problem"))), "bad"));
        if (Msg != null) o.Add(Info(Msg, "bad"));
        var e = Open != null ? tests.FirstOrDefault(x => S(x, "id") == Open) : null;
        if (Open != null && e == null) Open = null;
        if (e != null)
        {   // one test, in full
            o.Add(new PtRow(PtKind.Head, (T(e, "saved") ? Str(G(e, "name")) + " · " : "") + Stamp(D(e, "at"))));
            var full = (JsonObject)JsJson.Clone(e)!; if (!full.ContainsKey("phase")) full["phase"] = "done";
            Result(o, full, false);
            EntryActions(o, e, true);
            if (tests.Count > 1) o.Add(Go($"pt-cmp:{S(e, "id")}", "Compare with…", "pick another test"));
            o.Add(Go("pt-back", "Back to history", "", "quiet"));
            return;
        }
        if (Cmp != null)
        {
            var a = tests.FirstOrDefault(x => S(x, "id") == Cmp[0]); var b = tests.FirstOrDefault(x => S(x, "id") == Cmp[1]);
            if (a != null && b != null) { Compare(o, a, b); o.Add(Go("pt-uncmp", "Back to history", "", "quiet")); return; }
            Cmp = null;
        }
        var picking = Pick != null ? tests.FirstOrDefault(x => S(x, "id") == Pick) : null;
        if (Pick != null && picking == null) Pick = null;
        if (tests.Count == 0) { o.Add(Info($"No tests yet. Every finished test is kept here: the last {max}, and every one you save, for good.")); return; }
        var arr = new JsonArray(tests.Select(t => (JsonNode)JsJson.Clone(t)!).ToArray());
        var ws = WifiLabels(arr);
        if (Filter != null && !ws.Contains(Filter)) Filter = null;
        if (ws.Count > 1)
        {
            var chips = new List<PtCell> { new("All", Filter == null ? "on" : "", Act: "pt-filter:all") };
            for (int i = 0; i < ws.Count; i++) chips.Add(new(ws[i], Filter == ws[i] ? "on" : "", ws[i], Act: "pt-filter:" + i.ToString(CultureInfo.InvariantCulture)));
            o.Add(new PtRow(PtKind.Chips, Cells: chips));
        }
        var shown = Filter != null ? tests.Where(t => NetWords(G(t, "wifi") as JsonObject, true) == Filter).ToList() : tests;
        if (picking != null) o.Add(Info($"Pick the test to compare with {(T(picking, "saved") ? "“" + Str(G(picking, "name")) + "”" : Stamp(D(picking, "at")))}."));
        static string Sp(JsonObject? x) => N(x, "mbps") is double m ? MbpsN(m) : "--";
        foreach (var t in shown)
        {
            if (picking != null && S(t, "id") == S(picking, "id")) continue;
            var b = G(t, "best") as JsonObject;
            var w = G(t, "wifi") as JsonObject;
            string lk = w != null ? LinkWords(G(w, "link") as JsonObject, true) : "";
            bool saved = T(t, "saved");
            o.Add(new PtRow(PtKind.HistRow, Act: $"{(picking != null ? "pt-cmpto" : "pt-open")}:{S(t, "id")}", Tip: Stamp(D(t, "at")), Cells: new List<PtCell>
            {
                new(saved ? Str(G(t, "name")) : Stamp(D(t, "at")), saved ? "b" : "", Badge: saved ? "saved" : null),
                new(NetWords(w, true) + (lk.Length > 0 ? " · " + lk : ""), w != null && T(w, "name") ? "" : "hid", w != null ? LinkWords(G(w, "link") as JsonObject, false) : ""),
                new(b != null ? Ms(N(b, "avg")) : "--", b != null && AnyBad(b) ? "hi" : ""),
                new($"↓{Sp(G(t, "down") as JsonObject)} ↑{Sp(G(t, "up") as JsonObject)}"),
            }));
        }
        if (picking != null) { o.Add(Go("pt-uncmp", "Cancel", "", "quiet")); return; }
        int unsaved = tests.Count(t => !T(t, "saved"));
        o.Add(Info($"Click a test to see it in full, save it under a name, compare it with another, or delete it. Kept: the last {max} unsaved ({unsaved} now) and every saved one."));
    }

    /// The ping test's part of the PING dropdown: a separator, two tabs (Test, History · N), then the tab.
    public IReadOnlyList<PtRow> Rows()
    {
        int n = (List?["tests"] as JsonArray)?.Count ?? 0;
        var o = new List<PtRow>
        {
            new(PtKind.Sep),
            new(PtKind.Tabs, Cells: new List<PtCell>
            {
                new("Test", Tab == "test" ? "on" : "", Act: "pt-tab:test"),
                new("History" + (n > 0 ? " · " + n.ToString(CultureInfo.InvariantCulture) : ""), Tab == "history" ? "on" : "", Act: "pt-tab:history"),
            }),
        };
        if (Tab == "history") HistoryRows(o); else TestRows(o);
        return o;
    }

    public static string Sig(IReadOnlyList<PtRow> rows) => string.Join("\u0004", rows.Select(r => r.Sig()));
}
