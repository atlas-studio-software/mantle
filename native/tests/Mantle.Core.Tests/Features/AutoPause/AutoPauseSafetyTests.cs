using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.AutoPause;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.AutoPause;

// Coexistence with the old standalone helper (lib/features/_autopause-coexist.js; AUTO-084..AUTO-087, AUTO-091, AUTO-092) and the
// SAFETY rules, one test each: only the host touches Spotify (AUTO-099), the helper is never touched (AUTO-092), a check that
// throws stays off (AUTO-091), --test never sends a real command (AUTO-131), a passive sidecar's refusal comes back in words
// (AUTO-132). Written, not run.
public class AutoPauseSafetyTests
{
    const string TaskXml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\"><Triggers/><Settings><Enabled>{0}</Enabled></Settings><Actions/></Task>";

    static AutoPauseCoexist.Reads Reads(Func<byte[]> file, JsonObject?[]? pings = null, (bool, string?, string?)? schtasks = null, List<int>? pinged = null) =>
        new(file, () => Task.FromResult(schtasks ?? (true, null, "ERROR: The system cannot find the file specified.")),
            p => { pinged?.Add(p); var i = Ap.Ports.ToList().IndexOf(p); return Task.FromResult(pings != null && i >= 0 && i < pings.Length ? pings[i] : null); }, Ap.Ports);

    static byte[] Utf16(string s) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(s)).ToArray();

    [Fact]
    public void Task_state_from_the_xml_utf16_or_utf8_and_schtasks_as_the_fallback()
    {
        Assert.Equal("enabled", AutoPauseCoexist.TaskStateFromXml(string.Format(TaskXml, "true")));
        Assert.Equal("disabled", AutoPauseCoexist.TaskStateFromXml(string.Format(TaskXml, "false")));
        Assert.Equal("enabled", AutoPauseCoexist.TaskStateFromXml("<Task><Settings></Settings></Task>"));
        Assert.Equal("unknown", AutoPauseCoexist.TaskStateFromXml("not a task"));
        Assert.Equal("disabled", AutoPauseCoexist.TaskStateFromXml(AutoPauseCoexist.Decode(Utf16(string.Format(TaskXml, "false")))));
        Assert.Equal("disabled", AutoPauseCoexist.TaskStateFromXml(AutoPauseCoexist.Decode(Encoding.UTF8.GetBytes(string.Format(TaskXml, "false")))));
        Assert.Equal("disabled", AutoPauseCoexist.TaskStateFromSchtasks(false, "\"\\SpotifyAutoPause\",\"N/A\",\"Disabled\"", null));
        Assert.Equal("enabled", AutoPauseCoexist.TaskStateFromSchtasks(false, "\"\\SpotifyAutoPause\",\"N/A\",\"Ready\"", null));
        Assert.Equal("absent", AutoPauseCoexist.TaskStateFromSchtasks(true, null, "ERROR: The system cannot find the file specified."));
        Assert.Equal("unknown", AutoPauseCoexist.TaskStateFromSchtasks(true, null, "Access is denied."));
        Assert.EndsWith(Path.Combine("System32", "Tasks", "SpotifyAutoPause"), AutoPauseCoexist.TaskFile("C:\\Windows"));
    }

    [Fact]
    public void An_enabled_task_is_in_charge_even_while_its_bridge_is_down_and_nothing_is_pinged() => HostLoop.Run(async (loop, time) =>
    {
        var pinged = new List<int>();
        var v = await AutoPauseCoexist.Check(Reads(() => Encoding.UTF8.GetBytes(string.Format(TaskXml, "true")), pinged: pinged));
        Assert.Equal((true, "enabled", (int?)null), (v.InCharge, v.TaskState, v.BridgePort));
        Assert.Equal("The separate Spotify auto-pause helper on this PC is in charge, so Mantle\u2019s stays off. Turn the helper off to use this one", v.Words);
        Assert.Empty(pinged);
    });

    [Fact]
    public void An_old_bridge_answering_with_no_server_field_is_in_charge_and_a_Mantle_or_Quartz_host_never_is() => HostLoop.Run(async (loop, time) =>
    {
        var pinged = new List<int>();
        var old = new JsonObject { ["ok"] = true, ["app"] = "spotify-autopause", ["version"] = "1.5.3" };
        var v = await AutoPauseCoexist.Check(Reads(() => throw new FileNotFoundException(), new JsonObject?[] { null, old, null, null }, pinged: pinged));
        Assert.Equal((true, "absent", (int?)Ap.Ports[1], "1.5.3"), (v.InCharge, v.TaskState, v.BridgePort, v.BridgeVersion));
        Assert.Equal(Ap.Ports.OrderBy(p => p), pinged.OrderBy(p => p));     // all four, only because the task is not enabled
        var ours = new JsonObject { ["ok"] = true, ["app"] = "spotify-autopause", ["server"] = "quartz", ["protocol"] = 2 };
        var w = await AutoPauseCoexist.Check(Reads(() => Encoding.UTF8.GetBytes(string.Format(TaskXml, "false")), new JsonObject?[] { ours, null, null, null }));
        Assert.Equal((false, "disabled", ""), (w.InCharge, w.TaskState, w.Words));
    });

    [Fact]
    public void An_unreadable_task_file_asks_schtasks_with_a_read_only_query() => HostLoop.Run(async (loop, time) =>
    {
        var v = await AutoPauseCoexist.Check(Reads(() => throw new UnauthorizedAccessException(), schtasks: (false, "\"\\SpotifyAutoPause\",\"N/A\",\"Disabled\"", null)));
        Assert.Equal((false, "disabled"), (v.InCharge, v.TaskState));
        Assert.Equal(new[] { "/query", "/tn", "SpotifyAutoPause", "/fo", "csv", "/nh" }, AutoPauseCoexist.SchtasksArgs);
        Assert.Equal((700, 4000), (AutoPauseCoexist.PingTimeoutMs, AutoPauseCoexist.SchtasksTimeoutMs));
    });

    [Fact]
    public void SAFETY_a_coexistence_check_that_throws_counts_as_in_charge_with_words() => HostLoop.Run(async (loop, time) =>
    {
        var logs = new List<string>();
        var v = await AutoPauseCoexist.CheckSafe(() => throw new InvalidOperationException("boom"), logs.Add);
        Assert.True(v.InCharge);
        Assert.Equal("Mantle could not tell whether the separate auto-pause helper runs, so it stays off to be safe", v.Words);
        Assert.Single(logs);
        Assert.Contains("standing by to be safe", logs[0]);
        var faulted = await AutoPauseCoexist.CheckSafe(() => Task.FromException<AutoPauseCoexist.Verdict>(new IOException("gone")), logs.Add);
        Assert.True(faulted.InCharge);
    });

    [Fact]
    public void SAFETY_the_helper_and_its_task_are_never_touched()
    {
        // Read only by construction: the schtasks call is a /query, and nothing public can start, stop, enable or edit.
        foreach (var bad in new[] { "/change", "/run", "/end", "/create", "/delete", "/enable", "/disable" })
            Assert.DoesNotContain(bad, AutoPauseCoexist.SchtasksArgs, StringComparer.OrdinalIgnoreCase);
        var verbs = typeof(AutoPauseCoexist).GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).ToList();
        Assert.DoesNotContain(verbs, n => new[] { "Start", "Stop", "Enable", "Disable", "Edit", "Kill", "Write", "Delete", "Run" }.Any(b => n.StartsWith(b, StringComparison.Ordinal)));
        Assert.Equal(new[] { "Ping", "Ports", "ReadTaskFile", "Schtasks" }, typeof(AutoPauseCoexist.Reads).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").OrderBy(n => n, StringComparer.Ordinal));
    }

    static FakePlatform Platform(List<JsonObject> sent, Func<PlatformReply>? reply = null)
    {
        var p = new FakePlatform();
        p.SetStatus(PlatformState.Ok, "up");
        p.Handle("media", a => { sent.Add(JsonNode.Parse(a.GetRawText())!.AsObject()); return reply?.Invoke() ?? PlatformReply.Success(System.Text.Json.JsonDocument.Parse("{\"ok\":true,\"sent\":true}").RootElement.Clone()); });
        return p;
    }

    static JsonObject SpotifyPlaying() => JsonNode.Parse("{\"sessions\":[{\"aumid\":\"Spotify.exe\",\"status\":\"Playing\"},{\"aumid\":\"Chrome\",\"status\":\"playing\"}]}")!.AsObject();

    [Fact]
    public void SAFETY_only_the_host_touches_Spotify() => HostLoop.Run(async (loop, time) =>
    {
        var sent = new List<JsonObject>(); var logs = new List<string>();
        bool host = false;
        var s = new AutoPauseSpotify(Platform(sent), () => host, false, () => 0, logs.Add);
        s.FromMedia(SpotifyPlaying());
        Assert.Equal(("playing", "none", "Spotify.exe"), (s.Seen(), s.State(), s.Aumid));   // a client's engine sees no Spotify
        var r = await s.Pause();
        Assert.Equal((false, AutoPauseSpotify.NotHost), (r.Ok, r.Reason));
        Assert.Empty(sent);
        Assert.Contains(logs, l => l.StartsWith("refused to pause Spotify"));
        var engine = new AutoPauseEngine(s, () => 0, logs.Add);
        Assert.Equal("no-spotify", (await engine.AddHold("native:x", true, 0)).Action);
        Assert.Empty(sent);
        host = true;
        Assert.True((await s.Pause()).Ok);
        Assert.Equal("{\"op\":\"control\",\"action\":\"pause\",\"aumid\":\"Spotify.exe\",\"dryRun\":false}", sent.Single().ToJsonString());
        Assert.Equal("paused", s.Seen());                                    // assumed for 2 s until the session catches up
    });

    [Fact]
    public void SAFETY_test_mode_sends_every_command_as_a_dry_run_and_keeps_the_simulated_state() => HostLoop.Run(async (loop, time) =>
    {
        var sent = new List<JsonObject>(); var logs = new List<string>();
        var s = new AutoPauseSpotify(Platform(sent), () => true, true, () => 0, logs.Add);
        s.FromMedia(SpotifyPlaying());
        Assert.True((await s.Pause()).Ok);
        Assert.Equal(true, (bool)sent.Single()["dryRun"]!);
        Assert.Contains("test: would pause Spotify (nothing sent)", logs);
        Assert.Equal("paused", s.Seen());
        s.FromMedia(new JsonObject { ["sessions"] = new JsonArray() });      // Spotify closed: the simulation goes with it
        Assert.Equal("none", s.Seen());
    });

    [Fact]
    public void SAFETY_a_passive_sidecar_refusal_comes_back_in_words_and_is_logged() => HostLoop.Run(async (loop, time) =>
    {
        var sent = new List<JsonObject>(); var logs = new List<string>();
        var s = new AutoPauseSpotify(Platform(sent, () => PlatformReply.Fail(PlatformOutcome.Failed, "refused: sidecar is passive (test mode): media control would change live playback")),
            () => true, false, () => 0, logs.Add);
        s.FromMedia(SpotifyPlaying());
        var r = await s.Pause();
        Assert.False(r.Ok);
        Assert.EndsWith("media control would change live playback", r.Reason);
        Assert.Contains(logs, l => l.EndsWith("media control would change live playback"));
        Assert.Equal("playing", s.Seen());                                   // nothing assumed after a refusal
    });

    [Fact]
    public void Spotify_not_open_and_no_answer_fail_in_words()
    {
        var sent = new List<JsonObject>();
        var s = new AutoPauseSpotify(Platform(sent), () => true, false, () => 0, _ => { });
        Assert.Equal("Spotify is not open", s.Play().GetAwaiter().GetResult().Reason);
        Assert.Empty(sent);
    }
}
