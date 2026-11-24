using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.AutoPause;
using Xunit;

namespace Mantle.Core.Tests.Features.AutoPause;

/// <summary>
/// [Feature("autopause")] over the host test kit (lib/features/autopause.js and autopause.test.js): the module, statusOf's
/// words, the attribution (_autopause-attrib.js), the --test ports, the switch, the stop/rest/off hand-back, coexistence,
/// and AUTO-098 on the link. A --test rig with NO ports: nothing binds, every Spotify command is a dry run.
/// Written, not run (no SDK on the laptop).
/// </summary>
public class AutoPauseFeatureTests
{
    const string Sp = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";
    const string Loud = "{\"ok\":true,\"warming\":false,\"sessions\":[{\"pid\":500,\"sys\":false,\"active\":true,\"peak\":0.5,\"chain\":[{\"pid\":500,\"exe\":\"chrome.exe\",\"pkg\":null}]}]}";
    static readonly AutoPauseCoexist.Verdict Free = new(false, "absent", null, null, "");
    static readonly AutoPauseCoexist.Verdict Helper = new(true, "enabled", null, null, AutoPauseCoexist.InChargeWords);

    sealed class Unsub : IDisposable { readonly Action _a; public Unsub(Action a) { _a = a; } public void Dispose() => _a(); }

    sealed class Kit
    {
        public Rig R = null!;
        public ManualTime T = null!;
        public AutoPauseFeature F = null!;
        public JsonObject Media = M(S(Sp, "playing", "Spotify"));
        public Action<JsonObject>? OnMedia;
        public Func<Task<AutoPauseCoexist.Verdict>> Check = () => Task.FromResult(Free);
        public readonly List<int> Binds = new();
        public void Ms(double ms) { for (double t = 0; t < ms; t += 50) T.AdvanceMs(Math.Min(50, ms - t)); }
        public List<JsonObject> Controls => R.Platform.Calls.Where(c => c.Verb == "media").Select(c => JsonNode.Parse(c.ArgsJson)!.AsObject()).ToList();
        public void Play(params JsonObject[] s) { Media = M(s); OnMedia?.Invoke(Media); }
    }

    static JsonObject S(string aumid, string status, string name) => new() { ["aumid"] = aumid, ["status"] = status, ["app"] = new JsonObject { ["name"] = name } };
    static JsonObject M(params JsonObject[] s) => new() { ["ok"] = true, ["sessions"] = new JsonArray(s.Select(x => (JsonNode?)x.DeepClone()).ToArray()) };
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

    static void T(Func<Kit, Task> body, Action<Kit>? before = null) => HostLoop.Run(async (loop, time) =>
    {
        var k = new Kit { T = time, R = new Rig(time) };
        k.R.Platform.Start();
        k.R.Platform.Answer("media", "{\"ok\":true,\"dryRun\":true}");
        k.R.Platform.Answer("autopause.peaks", Loud);
        before?.Invoke(k);
        var h = new AutoPauseFeature.Host(Clock: new HostClock(time, loop), Check: () => k.Check(),
            Get: (port, route, ms, c) => Task.FromResult(LinkReply.Failed("ECONNREFUSED")),
            Bind: (p, _) => { k.Binds.Add(p); return null; }, Ports: Array.Empty<int>(),
            NowPlaying: new AutoPauseFeature.Media(() => k.Media, fn => { k.OnMedia = fn; return new Unsub(() => k.OnMedia = null); }));
        var a = typeof(AutoPauseFeature).GetCustomAttribute<FeatureAttribute>()!;
        k.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => k.F = new AutoPauseFeature(h), AutoPauseFeature.Options) });
        await k.R.Host.Installed;
        try { await body(k); }
        finally { await k.R.Host.StopAllAsync("the test ended"); }
    });

    static async Task<JsonElement> Call(Kit k, string name, string args = "{}")
    {
        var r = await k.R.Bus.CallAsync(name, JsonNode.Parse(args) as JsonObject);
        Assert.True(r.Ok, name + ": " + r.Error);
        return r.Result;
    }

    /// Spotify playing, then a browser video with sound: one tick later Spotify is held paused (a dry run).
    static void Hold(Kit k)
    {
        k.Ms(300);   // Spotify alone first: the adoption at take-charge takes nothing
        k.Play(S(Sp, "playing", "Spotify"), S("Chrome", "playing", "Google Chrome"));
        k.Ms(1000);
        Assert.Contains(k.Controls, c => Str(c["action"]) == "pause");
    }

    // ---- the module (AUTO-001, AUTO-002)

    [Fact]
    public void The_module_is_Electrons_autopause_on_by_default_resting_with_the_apps_choice()
    {
        var a = typeof(AutoPauseFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("autopause", "Spotify auto-pause", true, true, 0), (a.Name, a.Label, a.DefaultOn, a.Rest, a.Chords.Length));
        var o = Assert.Single(AutoPauseFeature.Options);
        Assert.Equal(("apps", "Pause Spotify while", FeatureOptionKind.Choice, (object?)"media"), (o.Key, o.Label, o.Kind, o.Default));
        Assert.Equal(new[] { "media:Another app plays video or sound", "any:Any other app makes a sound" }, o.Choices.Select(c => c.Value + ":" + c.Label));
        Assert.StartsWith("Like a lesson or video app", o.Help);
    }

    // ---- the words (AUTO-013..024)

    static EngineSnapshot Snap(bool wePaused = false, params HoldView[] holds) => Snap2(wePaused, "", false, 0, holds);
    static EngineSnapshot Snap2(bool wePaused, string err, bool supp, long suppMs, params HoldView[] holds) =>
        new(holds, wePaused, 0, supp, suppMs, err, "", Array.Empty<TrackView>());
    static HoldView H(string label, string kind = "media", bool native = true) => new("k:" + label + kind + native, native, label, kind, 0, 0, false, true);
    static string W(AutoPauseFeature.StatusInput x) => Str(AutoPauseFeature.StatusOf(x)["words"])!;
    static AutoPauseFeature.StatusInput On(EngineSnapshot? s = null, string mode = "media", string why = "", (string, bool)? via = null) => new(true, false, "", mode, why, s ?? Snap(), via);

    [Fact]
    public void Every_state_has_its_plain_words_and_card()
    {
        var off = AutoPauseFeature.StatusOf(new(false, false, "", "media", "", Snap()));
        Assert.Equal(("off", "Off", "", "media"), (Str(off["state"]), Str(off["words"]), Str(off["card"]), Str(off["apps"])));
        var sb = AutoPauseFeature.StatusOf(new(true, true, AutoPauseCoexist.InChargeWords, "media", "", Snap()));
        Assert.Equal(("standby", AutoPauseCoexist.InChargeWords, ""), (Str(sb["state"]), Str(sb["words"]), Str(sb["card"])));
        Assert.Equal("On: Spotify pauses while another app plays video or sound", W(On()));
        Assert.Equal("On: Spotify pauses while any other app makes a sound", W(On(mode: "any")));
        Assert.Equal("On: Spotify pauses while another app plays video or sound", W(On(mode: "anything else")));   // AUTO-003
        Assert.Equal("On: Spotify pauses while another app plays video or sound. App sound levels can\u2019t be read right now (no answer), so Mantle goes by what apps report", W(On(why: "no answer")));
        Assert.Equal("On: Spotify pauses while another app plays video or sound. Quartz is also running and controls Spotify for both", W(On(via: ("quartz", false))));
    }

    [Fact]
    public void Hold_words_name_the_app_the_browser_or_the_count()
    {
        Assert.Equal("Paused while Zoom plays", W(On(Snap(true, H("Zoom")))));
        Assert.Equal("Paused while Edge plays a video", W(On(Snap(true, H("Edge", "browser")))));
        Assert.Equal("Paused while another app plays", W(On(Snap(true, H("")))));
        Assert.Equal("Paused while your browser plays a video", W(On(Snap(true, H("", "browser")))));
        Assert.Equal("Paused while a video plays in your browser", W(On(Snap(true, H("x", "http", native: false)))));
        Assert.Equal("Paused while 2 apps play", W(On(Snap(true, H("Zoom"), H("Teams")))));
        var notUs = AutoPauseFeature.StatusOf(On(Snap(false, H("Zoom"))));
        Assert.Equal(("holding", "Holding Spotify while Zoom plays", ""), (Str(notUs["state"]), Str(notUs["words"]), Str(notUs["card"])));
        var us = AutoPauseFeature.StatusOf(On(Snap(true, H("Zoom"))));
        Assert.Equal("Paused while Zoom plays", Str(us["card"]));
        var client = AutoPauseFeature.StatusOf(On(Snap(false, H("Zoom")), via: ("quartz", true)));
        Assert.Equal(("Paused while Zoom plays (Quartz pauses it)", "Paused while Zoom plays"), (Str(client["words"]), Str(client["card"])));
        Assert.Equal("", Str(AutoPauseFeature.StatusOf(On(Snap(false, H("Zoom")), via: ("quartz", false)))["card"]));
    }

    [Fact]
    public void Backoff_and_error_say_what_happened_and_no_state_carries_jargon()
    {
        var b = AutoPauseFeature.StatusOf(On(Snap2(true, "", true, 270000, H("Zoom"))));
        Assert.Equal(("backoff", "You started Spotify again, so auto-pause leaves it alone for 5 min", "Auto-pause is leaving Spotify alone for now"), (Str(b["state"]), Str(b["words"]), Str(b["card"])));
        Assert.Contains("for 1 min", W(On(Snap2(true, "", true, 1000, H("Zoom")))));
        var e = AutoPauseFeature.StatusOf(On(Snap2(true, "Spotify is not open", false, 0, H("Zoom"))));
        Assert.Equal(("error", "Spotify could not be paused: Spotify is not open", "Couldn\u2019t pause Spotify"), (Str(e["state"]), Str(e["words"]), Str(e["card"])));
        Assert.Equal("idle", Str(AutoPauseFeature.StatusOf(On(Snap2(false, "old", false, 0)))["state"]));   // an error with nothing held is not shown
        var all = new[] { On(), On(mode: "any"), On(why: "no answer"), On(Snap(true, H(""))), On(Snap(false, H("", "browser"))),
            On(Snap2(true, "", true, 0, H("a"))), On(Snap2(true, "x", false, 0, H("a"))), On(via: ("", false)) };
        foreach (var x in all)
            foreach (var bad in new[] { "undefined", "null", "NaN", "sidecar", "aumid", "dryRun", "[object" })
                Assert.DoesNotContain(bad, W(x), StringComparison.OrdinalIgnoreCase);
    }

    // ---- attribution (AUTO-040..052)

    static JsonObject Row(long pid, double peak, string exe, string? pkg = null, bool sys = false, params (long, string)[] up)
    {
        var chain = new JsonArray(new JsonObject { ["pid"] = pid, ["exe"] = exe, ["pkg"] = pkg });
        foreach (var (p, e) in up) chain.Add((JsonNode)new JsonObject { ["pid"] = p, ["exe"] = e });
        return new JsonObject { ["pid"] = pid, ["sys"] = sys, ["peak"] = peak, ["chain"] = chain };
    }
    static JsonObject Peaks(params JsonObject[] rows) => new() { ["ok"] = true, ["sessions"] = new JsonArray(rows.Select(r => (JsonNode?)r).ToArray()) };
    static readonly HashSet<long> Mine = new() { 9 };

    [Fact]
    public void One_source_per_aumid_matched_by_package_exe_table_or_name()
    {
        var media = M(S("Chrome", "paused", "Chrome"), S("Chrome", "playing", "Chrome"), S("Microsoft.Lesson_8wekyb3d8bbwe!App", "playing", "Lesson"),
            S("C:\\Apps\\vlc.exe", "playing", "VLC"), S("PotPlayerMini64", "playing", "PotPlayer"), S("ab", "playing", "Short"));
        var peaks = Peaks(Row(1, 0.2, "chrome.exe"), Row(2, 0.4, "msedgewebview2.exe", "microsoft.lesson_8wekyb3d8bbwe", false, (3, "lesson.exe")),
            Row(4, 0.3, "vlc.exe"), Row(5, 0.1, "potplayermini64.exe"));
        var r = AutoPauseAttrib.Sources(media, peaks, "media", Mine);
        var by = r.Sources.ToDictionary(s => s.Key);
        Assert.Equal(5, r.Sources.Count);                              // two Chrome sessions = one source (AUTO-040)
        Assert.Equal(("playing", "browser", 0.2), (by["smtc:Chrome"].Status, by["smtc:Chrome"].Kind, by["smtc:Chrome"].Peak));
        Assert.Equal(0.4, by["smtc:Microsoft.Lesson_8wekyb3d8bbwe!App"].Peak);   // package family anywhere in the chain (AUTO-041)
        Assert.Equal(0.3, by["smtc:C:\\Apps\\vlc.exe"].Peak);                     // .exe AUMID (AUTO-042)
        Assert.Equal(0.1, by["smtc:PotPlayerMini64"].Peak);                       // by name (AUTO-044)
        Assert.Equal(-1, by["smtc:ab"].Peak);                                     // under 4 chars matches nothing
        Assert.True(AutoPauseAttrib.MatcherFor("308046B0AF4A39CB")(new AutoPauseAttrib.ChainEntry(1, "firefox.exe", null)));   // the table (AUTO-043)
        Assert.False(AutoPauseAttrib.MatcherFor("MicrosoftEdgeWebView2Thing")(new AutoPauseAttrib.ChainEntry(1, "msedgewebview2.exe", null)));   // AUTO-045
        Assert.Equal(-1, AutoPauseAttrib.Sources(media, null, "media", Mine).Sources[0].Peak);   // unreadable meter: -1, never 0 (AUTO-046)
    }

    [Fact]
    public void Any_mode_adds_apps_by_exe_and_media_mode_never_does()
    {
        var peaks = Peaks(Row(7, 0.05, "zoom.exe"), Row(8, 0.09, "zoom.exe"), Row(10, 0.2, "", null));
        var any = AutoPauseAttrib.Sources(M(), peaks, "any", Mine);
        var z = Assert.Single(any.Sources);
        Assert.Equal(("app:zoom.exe", "app", "Zoom", 0.09), (z.Key, z.Kind, z.Label, z.Peak));   // AUTO-048
        Assert.Empty(AutoPauseAttrib.Sources(M(), peaks, "media", Mine).Sources);               // AUTO-049
        Assert.Equal("An app", AutoPauseAttrib.AppLabel(""));
        var named = AutoPauseAttrib.Sources(M(new JsonObject { ["aumid"] = "Foo.Player", ["status"] = "playing" }), null, "media", Mine);
        Assert.Equal("An app", named.Sources[0].Label);                                         // AUTO-050
    }

    [Fact]
    public void SAFETY_Spotify_system_sounds_and_Mantle_itself_never_count()
    {
        var media = M(S(Sp, "playing", "Spotify"), S(Mantle.Core.Features.Media.MediaService.MantleAumid, "playing", "Mantle"),
            S(Mantle.Core.Features.Media.MediaService.MantleAumid.ToUpperInvariant(), "playing", "Mantle test"), S("Mantlelike.Player", "playing", "M"));
        var peaks = Peaks(Row(20, 0.9, "spotify.exe"), Row(21, 0.9, "x.exe", null, true), Row(22, 0.9, "mantlelikeplayer.exe", null, false, (9, "mantle.exe")),
            Row(23, 0.9, "helper.exe", null, false, (24, "Spotify.exe")));
        var r = AutoPauseAttrib.Sources(media, peaks, "any", Mine);
        Assert.Equal((true, Sp, "playing"), (r.Spotify.Found, r.Spotify.Aumid, r.Spotify.State));   // AUTO-053
        Assert.Empty(r.Sources);   // Spotify's tree, sys, Mantle's pids, Mantle's AUMID (every case), a session only Mantle's audio ties to
    }

    // ---- --test ports (AUTO-109)

    [Fact]
    public void A_test_run_never_uses_the_real_ports()
    {
        Assert.Equal(AutoPauseLink.Ports, AutoPauseFeature.PortsFor(false, "48787"));
        Assert.Equal(new[] { 48787 }, AutoPauseFeature.PortsFor(true, "48787"));
        foreach (var bad in new[] { null, "", "1024", "80", "8787.5", "-9000", "x", "70000" })
            Assert.Empty(AutoPauseFeature.PortsFor(true, bad));
    }

    // ---- the feature over the rig

    [Fact]
    public void Installed_it_publishes_its_status_and_answers_status_with_detail_and_test() => T(async k =>
    {
        k.Ms(100);
        Assert.True(k.R.Bus.Has("autopause.status") && k.R.Bus.Has("autopause.switch"));
        Assert.True(k.R.Events.HasReplay("autopause.status"));
        var st = await Call(k, "autopause.status");
        Assert.Equal("idle", st.GetProperty("state").GetString());
        Assert.Equal("host", st.GetProperty("role").GetString());       // no ports: Mantle decides alone
        Assert.True(st.GetProperty("test").GetBoolean());
        Assert.Equal(JsonValueKind.Array, st.GetProperty("detail").GetProperty("holds").ValueKind);
        Assert.Empty(k.Binds);                                           // AUTO-109: nothing bound in --test without a port
    });

    [Fact]
    public void A_video_with_sound_pauses_Spotify_as_a_dry_run_and_the_words_say_so() => T(async k =>
    {
        Hold(k);
        Assert.All(k.Controls, c => Assert.True(Bool(c["dryRun"])));                 // --test: dry runs only
        Assert.Contains(k.R.Platform.Calls, c => c.Verb == "autopause.peaks");       // AUTO-030
        var st = await Call(k, "autopause.status");
        Assert.Equal(("holding", "Paused while Google Chrome plays a video"), (st.GetProperty("state").GetString(), st.GetProperty("words").GetString()));
    });

    [Fact]
    public void Spotify_alone_needs_no_meter_read() => T(k =>
    {
        k.Ms(2000);
        Assert.DoesNotContain(k.R.Platform.Calls, c => c.Verb == "autopause.peaks");   // AUTO-029
        return Task.CompletedTask;
    });

    [Fact]
    public void Switch_refuses_a_non_boolean_and_off_hands_Spotify_back_before_answering() => T(async k =>
    {
        var bad = await Call(k, "autopause.switch", "{\"on\":\"yes\"}");
        Assert.Equal((false, "on must be true or false", true), (bad.GetProperty("ok").GetBoolean(), bad.GetProperty("reason").GetString(), bad.GetProperty("on").GetBoolean()));
        Hold(k);
        var off = await Call(k, "autopause.switch", "{\"on\":false}");
        Assert.Equal((true, false, "off", "Off"), (off.GetProperty("ok").GetBoolean(), off.GetProperty("on").GetBoolean(), off.GetProperty("state").GetString(), off.GetProperty("words").GetString()));
        Assert.Equal("play", Str(k.Controls.Last()["action"]));                      // AUTO-006/080: the hand-back went out first
    });

    [Fact]
    public void SAFETY_stopping_the_feature_resumes_what_Mantle_paused_and_hands_back_every_hold() => T(async k =>
    {
        Hold(k);
        await k.R.Host.StopAllAsync("Mantle is quitting");
        Assert.Equal("play", Str(k.Controls.Last()["action"]));
        Assert.All(k.Controls, c => Assert.True(Bool(c["dryRun"])));
        Assert.Contains(k.R.Lines, l => l.Contains("every hold was handed back", StringComparison.Ordinal));
        Assert.Null(k.OnMedia);                                                       // the NOW PLAYING watch is dropped
    });

    [Fact]
    public void SAFETY_a_coexistence_check_that_throws_stands_by_with_words_and_never_touches_Spotify() => T(async k =>
    {
        k.Ms(3000);
        var st = await Call(k, "autopause.status");
        Assert.Equal(("standby", AutoPauseCoexist.ThrewWords), (st.GetProperty("state").GetString(), st.GetProperty("words").GetString()));
        k.Play(S(Sp, "playing", "Spotify"), S("Chrome", "playing", "Google Chrome"));
        k.Ms(2000);
        Assert.Empty(k.Controls);
        Assert.DoesNotContain(k.R.Platform.Calls, c => c.Verb == "autopause.peaks");
    }, before: k => k.Check = () => throw new InvalidOperationException("the task folder is locked"));

    [Fact]
    public void When_the_helper_comes_back_Mantle_hands_back_first_then_stands_by_and_takes_charge_again_when_it_goes() => T(async k =>
    {
        Hold(k);
        k.Check = () => Task.FromResult(Helper);
        k.Ms(AutoPauseFeature.CoexistMsDefault + 100);                                // AUTO-088: every 15 s
        Assert.Equal("play", Str(k.Controls.Last()["action"]));                      // AUTO-089: handed back
        var st = await Call(k, "autopause.status");
        Assert.Equal(("standby", "off"), (st.GetProperty("state").GetString(), st.GetProperty("role").GetString()));
        int n = k.Controls.Count;
        k.Ms(2000);
        Assert.Equal(n, k.Controls.Count);                                            // never touched while standing by
        k.Check = () => Task.FromResult(Free);
        k.Ms(AutoPauseFeature.CoexistMsDefault + 100);                                // AUTO-090: in charge again, with adoption
        Assert.Contains(k.R.Lines, l => l.Contains("in charge: the standalone helper is off", StringComparison.Ordinal));
        Assert.Equal(n, k.Controls.Count);                                            // what already played is adopted, not paused
    });

    // ---- AUTO-098 (the link)

    [Fact]
    public void A_host_whose_may_host_turns_false_retires_and_hands_Spotify_back() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        bool may = true;
        var a = new LinkApp("mantle", Ap.Ports, sp, net, c, may: () => may);
        await a.Link.Start();
        Assert.Equal("host", a.Link.Role);
        await a.Hold(true);
        Assert.Equal("paused", sp.State);
        may = false;
        await a.Link.Sync();
        await a.Engine.Idle();
        Assert.NotEqual("host", a.Link.Role);
        Assert.Equal("playing", sp.State);
        Assert.False(a.Host.Running);
        await a.Link.Stop("the test ended");
    });
}
