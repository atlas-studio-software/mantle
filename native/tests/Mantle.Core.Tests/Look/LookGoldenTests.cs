using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Byte-exact against the JavaScript: Fixtures/look/golden.json was written by the Electron modules themselves
/// (Look/gen-look-golden.js runs lib/look-sync.js, lib/look-host.js, lib/look-guard.js and ui/shared/glass-adapt.js).
/// Mantle publishes look.json for Helm and Quartz, so every byte it writes must be the module's.
/// </summary>
public class LookGoldenTests
{
    static readonly Lazy<JsonObject> Golden = new(() => (JsonObject)JsJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "look", "golden.json")))!);
    static JsonArray G(string k) => (JsonArray)Golden.Value[k]!;
    static string J(JsonNode? n) => n == null ? "null" : JsJson.Stringify(n, 0);
    static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    static double D(JsonNode? n) => n!.GetValue<double>();
    static JsonValue? Str(string? s) => s == null ? null : JsonValue.Create(s);
    static JsonValue? Num(double? d) => d == null ? null : JsonValue.Create(d.Value);
    static string Norm(string? s, string dir) => s == null ? "\u0000null" : Bad(s.Replace(dir, "<dir>"));
    // V8's JSON error words cannot be matched (and may hold parentheses of their own): both sides say "not valid JSON (...)"
    static string Bad(string s) => Regex.Replace(s, @"not valid JSON \(.*\)", "not valid JSON (...)");
    static string NormG(JsonNode? s) => S(s) is string t ? Bad(t) : "\u0000null";
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00.000Z");
    const string LocalJson = "{\"theme\":\"light\",\"accent\":{\"hue\":30},\"glass\":{\"dark\":{\"blurPx\":8,\"dim\":0.6,\"panel\":-0.1},\"light\":{\"blurPx\":12,\"dim\":0.45,\"panel\":0},\"render\":\"picture\"}}";

    [Fact]
    public void Parse_matches_the_module_for_every_text()
    {
        foreach (var c in G("parse")) {
            string text = S(c!["text"])!;
            var p = LookSyncCore.Parse(text.Length > 0 && text[0] == '\ufeff' ? text[1..] : text);
            string at = "parse " + text;
            Assert.True(S(c["state"]) == p.State, at + ": state " + p.State);
            Assert.True(Regex.Replace(NormG(c["reason"]), @"not valid JSON \([^)]*\)", "not valid JSON (...)") == Norm(p.Reason, "\0"), at + ": reason " + p.Reason);
            var sk = new JsonArray(p.Skipped.Select(s => (JsonNode)new JsonObject { ["field"] = s.Field, ["by"] = Str(s.By), ["reason"] = s.Reason }).ToArray());
            Assert.True(J(c["skipped"]) == J(sk), at + ": skipped " + J(sk));
            Assert.True(NormG(c["doc"]) == (p.Doc == null ? "\u0000null" : JsJson.Stringify(p.Doc, 2)), at + ": doc\n" + (p.Doc == null ? "null" : JsJson.Stringify(p.Doc, 2)));
        }
    }

    [Fact]
    public void CheckValue_and_CheckDesktop_give_the_modules_reasons()
    {
        foreach (var c in G("checkValue")) {
            string? r = LookSyncCore.CheckValue(S(c!["field"])!, JsJson.Parse(S(c["value"])!));
            Assert.True(S(c["reason"]) == r, "checkValue " + S(c["field"]) + " " + S(c["value"]) + ": " + r);
        }
        foreach (var c in G("checkDesktop")) {
            string? r = LookSyncCore.CheckDesktop(JsJson.Parse(S(c!["value"])!));
            Assert.True(S(c["reason"]) == r, "checkDesktop " + S(c["value"]) + ": " + r);
        }
    }

    [Fact]
    public async Task Every_sync_scenario_writes_the_same_bytes_and_says_the_same_things()
    {
        foreach (var sc in G("sync")) await RunScenario((JsonObject)sc!);
    }

    static async Task RunScenario(JsonObject sc)
    {
        using var dir = new TestDir("golden-sync");
        string d = dir.Path, file = Path.Combine(d, "look.json");
        if (S(sc["file"]) is string f0) File.WriteAllText(file, f0);
        string app = S(sc["app"])!;
        var events = new List<string>();
        using var s = new LookSync(new LookSyncOptions {
            App = app, Instance = "golden-" + app, Dir = d, Files = new SafeFile(WriteGate.Scratch(d)), Enabled = sc["enabled"]!.GetValue<bool>(),
            Local = () => (JsonObject)JsJson.Parse(LocalJson)!, Now = () => Now, DebounceMs = 600000, CorruptRetryMs = 1,
            RewatchDelays = new[] { 600000 }, RetryDelays = new[] { 1, 1, 1 }, Pid = 4242,
        });
        s.Status += x => { lock (events) events.Add(J(new JsonObject { ["kind"] = "status", ["state"] = x.State, ["reason"] = Str(x.Reason == null ? null : Norm(x.Reason, d)), ["rev"] = Num(x.Rev), ["by"] = Str(x.By), ["at"] = Str(x.At), ["recovered"] = Str(x.Recovered), ["skipped"] = x.Skipped.Count })); };
        s.Apply += e => { lock (events) events.Add(J(new JsonObject { ["kind"] = "apply", ["changes"] = J(e.Changes), ["by"] = Str(e.By), ["rev"] = e.Rev, ["cause"] = e.Cause })); };
        s.Desktop += e => { lock (events) events.Add(J(new JsonObject { ["kind"] = "desktop", ["record"] = J(e.Record), ["reason"] = Str(e.Reason) })); };
        int i = 0;
        foreach (var stepNode in (JsonArray)sc["steps"]!) {
            var step = (JsonObject)stepNode!;
            var op = (JsonArray)JsJson.Parse(S(step["op"])!)!;
            string kind = S(op[0])!;
            lock (events) events.Clear();
            LookWrite? res = null;
            switch (kind) {
                case "start": s.Start(); await s.WhenIdle(); await s.WhenIdle(); break;
                case "publish": res = await s.PublishAsync(S(op[1])!, op[2]); break;
                case "desktop": res = await s.PublishDesktopAsync((JsonObject)op[1]!); break;
                case "enable": res = await s.SetEnabledAsync(op[1]!.GetValue<bool>()); break;
                case "write": File.WriteAllText(file, S(op[1])!); break;
                case "delete": File.Delete(file); break;
                case "refresh": s.Refresh(); await s.WhenIdle(); await s.WhenIdle(); break;
            }
            string at = S(sc["name"]) + ", step " + (++i) + " " + S(step["op"]);
            if (step["res"] is JsonObject r) {
                Assert.NotNull(res);
                Assert.True(r["ok"]!.GetValue<bool>() == res!.Ok, at + ": ok " + res.Ok + " " + res.Reason);
                Assert.True(J(r["rev"]) == J(Num(res.Rev)), at + ": rev " + res.Rev);
                Assert.True(NormG(r["reason"]) == Norm(res.Reason, d), at + ": reason " + res.Reason);
            }
            string[] got; lock (events) got = events.ToArray();
            var want = ((JsonArray)step["events"]!).Select(J).ToArray();
            Assert.True(want.SequenceEqual(got), at + ": events\nwant " + string.Join("\n     ", want) + "\ngot  " + string.Join("\n     ", got));
            string? text = File.Exists(file) ? File.ReadAllText(file) : null;
            Assert.True(S(step["file"]) == text, at + ": look.json\nwant " + S(step["file"]) + "\ngot  " + text);
            var list = Directory.Exists(d) ? Directory.GetFiles(d).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray() : null;
            Assert.True(J(step["list"]) == (list == null ? "null" : J(new JsonArray(list.Select(x => (JsonNode)JsonValue.Create(x!)).ToArray()))), at + ": files " + string.Join(", ", list ?? Array.Empty<string>()));
            Assert.True(S(step["status"]) == s.CurrentStatus().State, at + ": status " + s.CurrentStatus().State);
            Assert.True(NormG(step["readOnly"]) == Norm(s.ReadOnlyNow(), d), at + ": readOnlyNow");
        }
    }

    sealed class Stub
    {
        public string M;
        public Stub(string m) => M = m;
        public LookTheme Theme => new(() => M, () => M == "dark" ? "dark" : "light", x => { M = x; return null; });
    }

    static (LookHost Host, Stores Stores, Stub Theme, ListLog Log) HostFor(TestDir dir, string? local, string mode)
    {
        if (local != null) File.WriteAllText(dir.File("look-sync.json"), local);
        var log = new ListLog();
        var stores = new Stores(dir.Path, new SafeFile(WriteGate.Scratch(dir.Path)), log);
        var stub = new Stub(mode);
        var opts = new LookSyncOptions { App = "mantle", Dir = dir.File("look-sync"), Files = new SafeFile(WriteGate.Scratch(dir.Path)), Pid = 4242 };
        return (new LookHost(stores.Open("look-sync.json"), opts, stub.Theme, log), stores, stub, log);
    }

    [Fact]
    public void Vars_and_the_local_copy_match_look_host()
    {
        foreach (var c in G("vars")) {
            using var dir = new TestDir("golden-vars");
            var (h, stores, _, _) = HostFor(dir, S(c!["local"]), "dark");
            using (stores) using (h) {
                Assert.True(S(c["dark"]) == J(h.VarsJson("dark")), "dark vars for " + S(c["local"]) + ": " + J(h.VarsJson("dark")));
                Assert.True(S(c["light"]) == J(h.VarsJson("light")), "light vars for " + S(c["local"]) + ": " + J(h.VarsJson("light")));
                Assert.True(S(c["saved"]) == J(h.Local()), "local copy for " + S(c["local"]) + ": " + J(h.Local()));
            }
        }
        var k = (JsonObject)Golden.Value["hostConstants"]!;
        Assert.Equal(J(k["PANEL_CLAMP"]), J(new JsonArray(LookHost.PanelClamp.Lo, LookHost.PanelClamp.Hi)));
        var fills = (JsonObject)k["DARK"]!["fills"]!;
        Assert.Equal(fills.Select(p => p.Key + J(p.Value)), LookHost.DarkFills.Select(f => f.Name + J(new JsonArray(f.R, f.G, f.B, f.A))));
        var panels = (JsonObject)k["LIGHT"]!["panels"]!;
        Assert.Equal(panels.Select(p => p.Key + J(p.Value)), LookHost.LightPanels.Select(f => f.Name + J(new JsonArray(f.R, f.G, f.B, f.A))));
        Assert.Equal(D(k["DARK"]!["dim"]), LookHost.Dark.Dim);
        Assert.Equal(D(k["LIGHT"]!["blurPx"]), LookHost.Light.BlurPx);
    }

    [Fact]
    public async Task The_host_scenario_saves_shares_and_answers_like_look_host()
    {
        var g = (JsonObject)Golden.Value["host"]!;
        using var dir = new TestDir("golden-host");
        var (h, stores, _, log) = HostFor(dir, null, "dark");
        using var _s = stores;
        using var _h = h;
        var steps = ((JsonArray)g["steps"]!).Select(x => (JsonObject)x!).ToArray();
        int i = 0;
        void Snap(string what, JsonObject? answer)
        {
            var w = steps[i++];
            Assert.Equal(S(w["what"]), what);
            var v = h.View();
            if (answer != null) {
                var a = (JsonObject)JsJson.Clone(answer)!;
                if (!(a["ok"] is JsonValue ok && ok.GetValue<bool>() == false)) foreach (var key in new[] { "sync", "desktop", "windows", "lock", "guard" }) a.Remove(key);
                Assert.True(S(w["answer"]) == J(a), what + ": answer " + J(a));
            }
            string? local = File.Exists(dir.File("look-sync.json")) ? File.ReadAllText(dir.File("look-sync.json")) : null;
            Assert.True(S(w["local"]) == local, what + ": look-sync.json\n" + local);
            Assert.True(S(w["text"]) == Regex.Replace(S(v["sync"]!["text"])!, @" at \d\d:\d\d", " at HH:MM"), what + ": text " + S(v["sync"]!["text"]));
            Assert.True(S(w["state"]) == S(v["sync"]!["state"]), what + ": state");
            Assert.True(S(w["glass"]) == J(v["glass"]) && S(w["glassLook"]) == S(v["glassLook"]) && S(w["accent"]) == J(v["accent"]), what + ": glass/accent " + J(v));
            string? shared = File.Exists(dir.File(Path.Combine("look-sync", "look.json"))) ? File.ReadAllText(dir.File(Path.Combine("look-sync", "look.json"))) : null;
            Assert.True(J(w["shared"]) == (shared == null ? "null" : J(JsJson.Parse(shared)!["look"])), what + ": shared look " + shared);
        }
        Snap("made", null);
        h.Start(); await h.Sync.WhenIdle(); await h.Sync.WhenIdle();
        Snap("started", null);
        JsonObject P(string json) => (JsonObject)JsJson.Parse(json)!;
        Snap("glass dim .6", await h.SetGlassAsync(P("{\"dim\":0.6}")));
        Snap("glass light blur 20", await h.SetGlassAsync(P("{\"blurPx\":20,\"look\":\"light\"}")));
        Snap("glass bad look", await h.SetGlassAsync(P("{\"dim\":0.6,\"look\":\"blue\"}")));
        Snap("glass out of range", await h.SetGlassAsync(P("{\"panel\":0.5}")));
        Snap("glass text", await h.SetGlassAsync(P("{\"dim\":\"0.5\"}")));
        Snap("glass nothing", await h.SetGlassAsync(null));
        Snap("accent 200", await h.SetAccentAsync(JsonValue.Create(200.0)));
        Snap("accent bad", await h.SetAccentAsync(JsonValue.Create(361.0)));
        Snap("accent own", await h.SetAccentAsync(null));
        Snap("sync bad", await h.SetSyncAsync(JsonValue.Create("on")));
        Snap("sync off", await h.SetSyncAsync(JsonValue.Create(false)));
        Snap("accent while off", await h.SetAccentAsync(JsonValue.Create(90.0)));
        Snap("sync on", await h.SetSyncAsync(JsonValue.Create(true)));
        var want = ((JsonArray)g["lines"]!).Select(x => S(x)!).ToArray();
        var got = log.Lines.Where(l => l.StartsWith("look:", StringComparison.Ordinal)).Select(l => Regex.Replace(l, @" at \d\d:\d\d", " at HH:MM")).ToArray();
        Assert.True(want.SequenceEqual(got), "log\nwant " + string.Join("\n     ", want) + "\ngot  " + string.Join("\n     ", got));

        var u = (JsonObject)Golden.Value["hostUnreadable"]!;
        using var dir2 = new TestDir("golden-host-bad");
        var (h2, stores2, _, log2) = HostFor(dir2, "{\"lookSync\": fals", "light");
        using (stores2) using (h2) {
            Assert.Equal(S(u["text"]), S(h2.View()["sync"]!["text"]));
            Assert.Equal(S(u["reason"]), S(h2.View()["sync"]!["reason"]));
            Assert.Equal(((JsonArray)u["lines"]!).Select(x => S(x)), log2.Lines.Where(l => l.StartsWith("look:", StringComparison.Ordinal)));
        }
    }

    // ---- the guard maths (PEND-005) ---------------------------------------------------------------

    static byte[] Pic(string name, out int w, out int h)
    {
        var p = (JsonObject)Golden.Value["pictures"]![name]!;
        w = (int)D(p["width"]); h = (int)D(p["height"]);
        return ((JsonArray)p["data"]!).Select(x => (byte)D(x)).ToArray();
    }

    static void Close(double want, double got, double tol, string at) => Assert.True(Math.Abs(want - got) <= tol * Math.Max(1, Math.Abs(want)), at + ": want " + want.ToString("R") + " got " + got.ToString("R"));
    static Rgba C(JsonNode? n) => new(D(n!["r"]), D(n["g"]), D(n["b"]), n["a"] == null ? 1 : D(n["a"]));

    [Fact]
    public void Luminance_contrast_colours_and_filters_match_glass_adapt()
    {
        foreach (var c in G("lum")) { var a = (JsonArray)c!["rgb"]!; Close(D(c["lum"]), GlassAdapt.Lum(D(a[0]), D(a[1]), D(a[2])), 1e-14, "lum " + J(a)); }
        foreach (var c in G("contrast")) Close(D(c!["ratio"]), GlassAdapt.Contrast(D(c["a"]), D(c["b"])), 1e-14, "contrast");
        foreach (var c in G("parseColor")) {
            var r = GlassAdapt.ParseColor(S(c!["s"]));
            Assert.True(J(c["c"]) == (r is Rgba x ? J(new JsonObject { ["r"] = x.R, ["g"] = x.G, ["b"] = x.B, ["a"] = x.A }) : "null"), "parseColor " + S(c["s"]));
        }
        foreach (var c in G("parseFilter")) {
            var f = GlassAdapt.ParseFilter(S(c!["s"]));
            Assert.True(J(c["f"]) == J(new JsonObject { ["blur"] = f.Blur, ["saturate"] = f.Saturate, ["brightness"] = f.Brightness }), "parseFilter " + S(c["s"]));
        }
        var rules = (JsonObject)Golden.Value["guardRules"]!;
        Assert.Equal(D(rules["RULE"]!["need"]), LookGuard.Rule.Need);
        Assert.Equal(D(rules["RULE_LIGHT"]!["need"]), LookGuard.RuleLight.Need);
        Assert.Equal(C(rules["RULE"]!["ink"]), LookGuard.Rule.Ink);
        Assert.Equal(C(rules["RULE_LIGHT"]!["ink"]), LookGuard.RuleLight.Ink);
        Assert.Equal(D(rules["BAND_DIP"]), LookGuard.BandDip);
        Assert.Equal(D(rules["GUARD"]!["need"]), GlassAdapt.GuardNeed);
    }

    [Fact]
    public void The_blurred_picture_and_the_tint_match_glass_adapt()
    {
        foreach (var c in G("glassPixels")) {
            var data = Pic(S(c!["picture"])!, out int w, out int h);
            var f = (JsonObject)c["filter"]!;
            var px = GlassAdapt.GlassPixels(data, w, h, D(f["sigma"]), f["saturate"] == null ? 1 : D(f["saturate"]), f["brightness"] == null ? 1 : D(f["brightness"]));
            var want = (JsonArray)c["px"]!;
            Assert.Equal(want.Count, px.Length);
            for (int i = 0; i < px.Length; i++) Close(D(want[i]), px[i], 1e-9, "glassPixels " + S(c["picture"]) + " " + J(f) + " [" + i + "]");
        }
        var pxOf = G("glassPixels").ToDictionary(c => S(c!["picture"]) + J(c!["filter"]), c => ((JsonArray)c!["px"]!).Select(D).ToArray());
        foreach (var c in G("glassTint")) {
            var px = pxOf[S(c!["picture"]) + J(c["filter"])];
            var r = GlassAdapt.GlassTint(px, C(c["ink"]), C(c["tint"]), D(c["need"]), D(c["floor"]), D(c["ceil"]), 0.02);
            Assert.True(J(c["r"]) == J(new JsonObject { ["alpha"] = r.Alpha, ["ratio"] = r.Ratio, ["short"] = r.Short }), "glassTint " + S(c["picture"]) + " " + J(c["filter"]) + " need " + J(c["need"]) + ": " + r);
        }
        var e = GlassAdapt.GlassTint(Array.Empty<double>(), LookGuard.Rule.Ink, LookGuard.Rule.Tint, 4.2, 0.5, 0.88);
        Assert.Equal(J(Golden.Value["glassTintEmpty"]), J(new JsonObject { ["alpha"] = e.Alpha, ["ratio"] = e.Ratio, ["short"] = e.Short }));
        foreach (var c in G("guardRect")) {
            var data = Pic(S(c!["picture"])!, out int w, out int h);
            bool light = c["light"]!.GetValue<bool>();
            var t = LookGuard.Tokens(light ? 0.45 : 0.5, 5, light);
            PicRect? rect = c["rect"] is JsonObject ro ? new PicRect(D(ro["x"]), D(ro["y"]), D(ro["w"]), D(ro["h"])) : null;
            var r = GlassAdapt.GuardRect(GlassAdapt.Picture(data, w, h, t, 0.5), t, rect);
            string got = r == null ? "null" : J(new JsonObject { ["alpha"] = r.Alpha, ["ratio"] = r.Ratio, ["short"] = r.Short, ["engaged"] = r.Engaged, ["tint"] = r.Tint });
            Assert.True(J(c["r"]) == got, "guardRect " + S(c["picture"]) + " " + J(c["rect"]) + " light " + light + ": " + got);
        }
        foreach (var c in G("settle")) {
            var g = c!["g"] as JsonObject;
            var r = GlassAdapt.GlassSettle(g == null ? null : D(g["cur"]), g == null ? 0 : (int)D(g["lower"]), D(c["next"]), c["hold"] == null ? 0 : (int)D(c["hold"]));
            Assert.True(J(c["r"]) == J(new JsonObject { ["cur"] = r.Cur, ["lower"] = r.Lower }), "settle " + J(c));
        }
    }

    [Fact]
    public void The_band_the_solve_the_checks_keys_and_use_match_look_guard()
    {
        foreach (var c in G("bandGeometry")) {
            var a = ((JsonArray)c!["args"]!).Select(D).ToArray();
            var g = LookGuard.Geometry(a[0], a[1], a[2], a[3], a[4]);
            string got = g == null ? "null" : J(new JsonObject { ["scale"] = g.Scale, ["ws"] = g.Ws, ["src"] = new JsonObject { ["x"] = g.SrcX, ["y"] = g.SrcY, ["width"] = g.SrcW, ["height"] = g.SrcH },
                ["out"] = new JsonObject { ["w"] = g.OutW, ["h"] = g.OutH }, ["bandRows"] = g.BandRows, ["band"] = g.Band });
            Assert.True(J(c["g"]) == got, "bandGeometry " + J(c["args"]) + ": " + got);
        }
        foreach (var c in G("fromPixels")) {
            var data = Pic(S(c!["picture"])!, out int w, out int h);
            var o = (JsonObject)c["o"]!;
            var r = LookGuard.FromPixels(data, w, h, D(o["dim"]), D(o["blurPx"]), o["light"]!.GetValue<bool>(), D(o["ws"]), (int)D(o["bandRows"]));
            string got = r is TintAnswer x ? J(new JsonObject { ["alpha"] = x.Alpha, ["contrast"] = x.Ratio, ["short"] = x.Short }) : "null";
            Assert.True(J(c["r"]) == got, "fromPixels " + S(c["picture"]) + " " + J(o) + ": " + got);
        }
        foreach (var c in G("use")) {
            var k = (JsonObject)c!["c"]!;
            var gl = k["glass"] as JsonObject;
            var u = LookGuard.Use(k["dark"]!.GetValue<bool>(), k["syncOn"]!.GetValue<bool>(), k["record"] as JsonObject, gl == null ? null : D(gl["dim"]), gl == null ? null : D(gl["blurPx"]), k["working"]!.GetValue<bool>());
            var w = (JsonObject)c["r"]!;
            string at = "use " + J(k) + ": " + u;
            Assert.True(w["use"]!.GetValue<bool>() == u.Use, at);
            if (u.Use) Assert.True(J(w["alpha"]) == J(Num(u.Alpha)) && J(w["contrast"]) == J(Num(u.Contrast)) && J(w["rev"]) == J(Num(u.Rev)), at);
            else Assert.True(S(w["why"]) == u.Why && w["failure"]!.GetValue<bool>() == u.Failure && (w["soft"]?.GetValue<bool>() ?? false) == u.Soft, at);
        }
        foreach (var c in G("checkGuard")) {
            Assert.True(S(c!["dark"]) == LookGuard.CheckGuard(c["g"]), "checkGuard " + J(c["g"]));
            Assert.True(S(c["light"]) == LookGuard.CheckGuardLight(c["g"]), "checkGuardLight " + J(c["g"]));
            Assert.True(S(c["key"]) == LookGuard.KeyOfGuard(c["g"]), "keyOfGuard " + J(c["g"]) + ": " + LookGuard.KeyOfGuard(c["g"]));
        }
        foreach (var c in G("guardKey")) {
            var a = (JsonArray)c!["args"]!;
            Assert.True(S(c["key"]) == LookGuard.GuardKey(a[0], a[1], a[2], a[3]), "guardKey " + J(a) + ": " + LookGuard.GuardKey(a[0], a[1], a[2], a[3]));
        }
    }
}
