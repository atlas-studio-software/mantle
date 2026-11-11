using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Media;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Media;

/// <summary>
/// The media back end against a fake sidecar (main.js mediaRead / mediaPush / mediaPollArm / media.select /
/// media.control): BAR-944..947, BAR-954..964, BAR-1010..1025. Every control in a --test run is a dry run.
/// </summary>
public class MediaServiceTests
{
    const string Spot = "{\"aumid\":\"Spotify.exe\",\"app\":{\"name\":\"Spotify\",\"icon\":null},\"title\":\"Song\",\"artist\":\"Band\",\"status\":\"playing\",\"playing\":true,\"current\":true,\"can\":{\"toggle\":true,\"next\":true,\"previous\":false}}";
    const string Acel = "{\"aumid\":\"Lessons.App\",\"app\":{\"name\":\"Lessons\",\"icon\":null},\"title\":\"Lesson\",\"status\":\"paused\",\"playing\":false,\"current\":false}";
    const string Own = "{\"aumid\":\"COM.owensilva.mantle\",\"app\":{\"name\":\"Electron\"},\"title\":\"Clip\",\"status\":\"paused\",\"playing\":false,\"current\":false}";

    sealed class Kit : IDisposable
    {
        public readonly FakePlatform P = new();
        public readonly TestEvents E = new();
        public readonly ManualScheduler S = new();
        public readonly List<string> Lines = new();
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "mantle-media-" + Guid.NewGuid().ToString("N"));
        public MediaService Svc = null!;
        public string[] Sessions = { Spot, Acel };
        public string? Current = Spot;
        public bool Events = true;
        public Kit() { Directory.CreateDirectory(Dir); }
        public Kit Sidecar()
        {
            P.Handle("media", a => {
                string op = a.GetProperty("op").GetString()!;
                if (op == "watch") return PlatformReply.Success(J(Events ? "{\"ok\":true}" : "{\"ok\":false,\"reason\":\"no events\"}"));
                if (op == "control") return PlatformReply.Success(J("{\"ok\":true}"));
                if (a.TryGetProperty("aumid", out var au)) {
                    var one = Sessions.FirstOrDefault(s => s.Contains("\"" + au.GetString() + "\"", StringComparison.OrdinalIgnoreCase));
                    return PlatformReply.Success(J(one != null ? "{\"ok\":true,\"session\":" + one + "}" : "{\"ok\":true,\"session\":null,\"sessionReason\":\"gone\"}"));
                }
                return PlatformReply.Success(J("{\"ok\":true,\"present\":" + (Sessions.Length > 0 ? "true" : "false") + ",\"rev\":" + P.Calls.Count +
                    ",\"current\":" + (Current ?? "null") + ",\"sessions\":[" + string.Join(",", Sessions) + "],\"events\":" + (Events ? "true" : "false") +
                    ",\"eventsReason\":" + (Events ? "null" : "\"the change events could not be hooked\"") + "}"));
            });
            return this;
        }
        public Kit Make(bool test = false, string? fixture = null, bool guardFlag = false, bool controlsFlag = false)
        {
            Svc = new MediaService(new MediaOptions { Platform = P, Events = E, Host = S, Test = test, Log = Lines.Add, DataDir = Dir, Fixture = fixture, FlagGuard = guardFlag, FlagControls = controlsFlag });
            P.Start();
            S.Advance();
            return this;
        }
        public JsonObject Last => (JsonObject)JsonNode.Parse(((JsonElement)E.Published.Last(p => p.Name == "media").Payload!).GetRawText())!;
        public int Gets => P.Calls.Count(c => c.Verb == "media" && c.ArgsJson.Contains("\"get\""));
        public void Guard(string json) => File.WriteAllText(Path.Combine(Dir, MediaGuard.FileName), json);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
    }
    static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
    static void T(Func<Kit, Task> body) => HostLoop.Run(async (_, _) => { using var k = new Kit(); await body(k); });

    [Fact]
    public void A_ready_sidecar_is_watched_then_read_and_the_payload_has_the_shown_session_and_the_list() => T(async k => {
        k.Sessions = new[] { Spot, Own };
        k.Sidecar().Make();
        await Task.Yield();
        Assert.Contains(k.P.Calls, c => c.ArgsJson.Contains("\"watch\""));
        var p = k.Last;
        Assert.True((bool)p["ok"]!);
        Assert.Equal("the sidecar is ready", (string?)p["why"]);
        Assert.Equal("Spotify.exe", (string?)p["shown"]!["aumid"]);
        Assert.True((bool)p["shown"]!["controls"]!["ok"]!);
        Assert.Equal("current", (string?)p["pick"]!["by"]);
        var rows = (JsonArray)p["sessions"]!;
        Assert.Equal(2, rows.Count);
        // the list rows carry only these fields (BAR-1011); Mantle's own session is Mantle Player (BAR-1013)
        Assert.Equal(new[] { "aumid", "app", "title", "artist", "status", "playing", "current", "protectedApp", "label" }, ((JsonObject)rows[0]!).Select(kv => kv.Key).ToArray());
        Assert.Equal("Mantle Player", (string?)rows[1]!["app"]!["name"]);
        Assert.True(k.E.HasReplay("media"));
    });

    [Fact]
    public void The_same_rev_is_ignored_and_timeline_only_changes_are_read_only_while_the_card_is_open() => T(async k => {
        k.Sidecar().Make();
        int before = k.Gets;
        var rev = k.Last["rev"]!.ToJsonString();
        k.P.Raise("media", "{\"rev\":" + rev + ",\"reasons\":[\"playback\"]}");
        Assert.Equal(before, k.Gets);
        k.P.Raise("media", "{\"rev\":9999,\"reasons\":[\"timeline\"]}");
        Assert.Equal(before, k.Gets);
        k.Svc.CardOpen = true;
        k.P.Raise("media", "{\"rev\":9999,\"reasons\":[\"timeline\"]}");
        await Task.Yield();
        Assert.Equal(before + 1, k.Gets);
        Assert.Equal("media changed (timeline)", (string?)k.Last["why"]);
    });

    [Fact]
    public void Without_change_events_it_polls_every_10_s_and_a_failed_read_retries_every_5_s_but_an_old_exe_never() => T(async k => {
        k.Events = false;
        k.Sidecar().Make();
        Assert.False((bool)k.Last["events"]!);
        int n = k.Gets;
        k.S.Advance(9999); Assert.Equal(n, k.Gets);
        k.S.Advance(1); Assert.Equal(n + 1, k.Gets);
        Assert.Equal("the slow media poll (no change events)", (string?)k.Last["why"]);

        using var k2 = new Kit();
        k2.P.Handle("media", _ => PlatformReply.Fail(PlatformOutcome.Timeout, "did not answer media within 8s"));
        k2.Make();
        Assert.Equal("the sessions could not be read: did not answer media within 8s", (string?)k2.Last["reason"]);
        int m = k2.Gets;
        k2.S.Advance(5000);
        Assert.Equal(m + 1, k2.Gets);
        Assert.Equal("a retry after a failed media read", (string?)k2.Last["why"]);

        using var k3 = new Kit();
        k3.Make();   // no handler: "unknown command: media"
        Assert.Equal(MediaService.OldExeRead, (string?)k3.Last["reason"]);
        Assert.False((bool)k3.Last["retry"]!);
        int o = k3.Gets;
        k3.S.Advance(60000);
        Assert.Equal(o, k3.Gets);
        await Task.CompletedTask;
    });

    [Fact]
    public void A_pick_is_held_while_its_session_is_away_and_dropped_on_time_by_its_own_timer() => T(async k => {
        k.Sidecar().Make();
        var r = await k.Svc.Select("lessons.app");
        Assert.Equal("you", (string?)r!["pick"]!["by"]);
        Assert.Equal("Lessons.App", (string?)r["shown"]!["aumid"]);
        k.Sessions = new[] { Spot };
        await k.Svc.Push("test");
        Assert.Equal("waiting", (string?)k.Last["pick"]!["by"]);
        Assert.Equal("current", (string?)k.Last["pick"]!["auto"]);
        k.S.Advance(60250);
        Assert.Equal("current", (string?)k.Last["pick"]!["by"]);
        Assert.StartsWith("Lessons's media session was gone for 60 s", (string?)k.Last["pick"]!["dropped"]!["reason"]);
        Assert.Contains(k.Lines, l => l.StartsWith("mantle: media pick dropped: ", StringComparison.Ordinal));
        var auto = await k.Svc.Select(null);
        Assert.Equal("the media card went back to automatic", (string?)auto!["why"]);
    });

    [Fact]
    public void Under_the_guard_a_protected_session_has_no_controls_and_main_refuses_its_buttons_whatever_is_sent() => T(async k => {
        k.Guard("{\"apps\":[\"Lessons\"],\"label\":\"(school)\"}");
        k.Current = Acel;
        k.Sidecar().Make();
        // automatic shows the player rather than the protected app
        Assert.Equal("Spotify.exe", (string?)k.Last["shown"]!["aumid"]);
        Assert.Equal("protected-rule", (string?)k.Last["pick"]!["by"]);
        Assert.Equal("(school)", (string?)((JsonArray)k.Last["sessions"]!)[1]!["label"]);
        await k.Svc.Select("Lessons.App");
        Assert.False((bool)k.Last["shown"]!["controls"]!["ok"]!);
        Assert.Equal("Lessons (school) · controls off so a stray click can't pause it", (string?)k.Last["shown"]!["controls"]!["reason"]);
        var c = await k.Svc.Control("toggle", "LESSONS.app", null);
        Assert.False((bool)c["ok"]!);
        Assert.StartsWith("Lessons (school) · controls off", (string?)c["reason"]);
        Assert.DoesNotContain(k.P.Calls, x => x.ArgsJson.Contains("\"control\""));
        // only protected apps: no chip at all
        k.Sessions = new[] { Acel };
        await k.Svc.Push("test");
        Assert.Null(k.Last["shown"]);
        Assert.Equal("protected-only", (string?)k.Last["pick"]!["by"]);
        Assert.Contains(k.Lines, l => l.StartsWith("mantle: protected media apps: Lessons, rule on (config)", StringComparison.Ordinal));
    });

    [Fact]
    public void The_controls_flag_wins_over_the_file() => T(async k => {
        k.Guard("{\"apps\":[\"Lessons\"]}");
        k.Sidecar().Make(guardFlag: true, controlsFlag: true);
        var c = await k.Svc.Control("toggle", "Lessons.App", null);
        Assert.True((bool)c["ok"]!);
        Assert.False((bool)k.Last["guard"]!["on"]!);
    });

    [Fact]
    public void Control_refuses_an_unknown_action_or_a_missing_AUMID_and_a_live_send_reads_again() => T(async k => {
        k.Sidecar().Make();
        Assert.Equal("media.control does not know the action \"stop\" (it does play, pause, toggle, next, previous, seek)", (string?)(await k.Svc.Control("stop", "Spotify.exe", null))["reason"]);
        Assert.StartsWith("media.control needs the AUMID", (string?)(await k.Svc.Control("next", "", null))["reason"]);
        int n = k.Gets;
        var r = await k.Svc.Control("seek", "Spotify.exe", 1234);
        Assert.True((bool)r["ok"]!);
        Assert.Contains(k.P.Calls, x => x.ArgsJson == "{\"op\":\"control\",\"action\":\"seek\",\"aumid\":\"Spotify.exe\",\"positionMs\":1234}");
        Assert.Equal(n + 1, k.Gets);
        Assert.Equal("media seek was sent", (string?)k.Last["why"]);
    });

    [Fact]
    public void In_a_test_run_every_control_is_a_dry_run_and_a_fixture_session_follows_it() => T(async k => {
        string fx = "{\"sessions\":[" + Spot + "]}";
        k.Make(test: true, fixture: fx);
        Assert.Contains(k.Lines, l => l.StartsWith("media: a --test run with MANTLE_TEST_MEDIA", StringComparison.Ordinal));
        Assert.True((bool)k.Last["shown"]!["playing"]!);
        var r = await k.Svc.Control("toggle", "Spotify.exe", null);
        Assert.True((bool)r["dryRun"]!);
        Assert.Contains(k.Lines, l => l == "media: dry run (--test): toggle for Spotify.exe would ask the sidecar media {\"op\":\"control\",\"action\":\"toggle\",\"aumid\":\"Spotify.exe\"}; nothing was sent");
        k.S.Advance();
        Assert.False((bool)k.Last["shown"]!["playing"]!);
        Assert.Equal("paused", (string?)k.Last["shown"]!["status"]);
        Assert.Empty(k.P.Calls.Where(c => c.Verb == "media"));

        using var k2 = new Kit();
        k2.Sidecar().Make(test: true);
        await k2.Svc.Control("next", "Spotify.exe", null);
        Assert.DoesNotContain(k2.P.Calls, x => x.ArgsJson.Contains("\"control\""));
    });

    [Fact]
    public void A_fixture_can_fail_the_read_and_the_payload_says_why() => T(async k => {
        k.Make(test: true, fixture: "{\"fail\":\"the sessions could not be read: test\"}");
        Assert.False((bool)k.Last["ok"]!);
        Assert.Equal("the sessions could not be read: test", (string?)k.Last["reason"]);
        Assert.Empty((JsonArray)k.Last["sessions"]!);
        await Task.CompletedTask;
    });
}
