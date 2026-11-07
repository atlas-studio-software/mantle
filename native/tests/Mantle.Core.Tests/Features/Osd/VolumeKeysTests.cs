using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Features.Osd;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features.Toasts;
using Xunit;

namespace Mantle.Core.Tests.Features.Osd;

// lib/features/osd.test.js, the key half (OSD-001..OSD-017, OSD-058, OSD-059), against a scripted sidecar.
public class VolumeKeysTests
{
    /// A sidecar whose osd.volume replies wait until the test releases them (a slow sidecar), or answer at once.
    sealed class Sidecar : IPlatform
    {
        public bool Hold;
        public double Level = 40; public bool Muted;
        public string? Fail;
        public bool QsOpen;
        public readonly List<(string Verb, string Args)> Calls = new();
        readonly Queue<(TaskCompletionSource<PlatformReply> T, JsonObject A)> _held = new();
        public PlatformStatus Status => new(PlatformState.Ok, "ready");
        public IDisposable On(string ev, Action<PlatformEvent> h) => new Noop();
        public IDisposable OnStatus(Action<PlatformStatus> h) => new Noop();
        public IDisposable OnReady(Action h) => new Noop();
        sealed class Noop : IDisposable { public void Dispose() { } }

        public ValueTask<PlatformReply> CallAsync(string verb, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancel = default)
        {
            Calls.Add((verb, args?.ToJsonString() ?? "{}"));
            if (verb == "osd.qs") return new(Ok($"{{\"qsOpen\":{(QsOpen ? "true" : "false")}}}"));
            if (verb != "osd.volume") return new(PlatformReply.Fail(PlatformOutcome.Failed, $"unknown command: {verb}"));
            if (Hold) { var t = new TaskCompletionSource<PlatformReply>(); _held.Enqueue((t, args!)); return new(t.Task); }
            return new(Answer(args!));
        }

        PlatformReply Answer(JsonObject a)
        {
            if (Fail != null) return PlatformReply.Fail(PlatformOutcome.Failed, Fail);
            string op = (string)a["op"]!;
            bool dry = a["dry"] is JsonValue d && (bool)d;
            double lvl = Level; bool mut = Muted;
            if (a["from"] is JsonObject f) { lvl = (double)f["level"]!; mut = (bool)f["muted"]!; }
            double from = lvl; bool fromMuted = mut;
            if (op == "mute") mut = !mut;
            else { lvl = Math.Max(0, Math.Min(100, (op == "up" ? Math.Floor(lvl / 2) * 2 + 2 : Math.Ceiling(lvl / 2) * 2 - 2))); mut = false; }
            if (!dry) { Level = lvl; Muted = mut; }
            return Ok($"{{\"level\":{lvl},\"muted\":{(mut ? "true" : "false")},\"ms\":3,\"from\":{{\"level\":{from},\"muted\":{(fromMuted ? "true" : "false")}}}}}");
        }

        public int Waiting => _held.Count;
        public void ReleaseOne() { var (t, a) = _held.Dequeue(); t.SetResult(Answer(a)); }
        static PlatformReply Ok(string json) => PlatformReply.Success(JsonDocument.Parse(json).RootElement.Clone());
    }

    sealed class View : IOsdView
    {
        public bool Shown { get; set; }
        public readonly List<string> Did = new();
        public void Show(string kind, VolumeState s, string via) { Shown = true; Did.Add($"show {kind} {s.Level?.ToString() ?? "-"}{(s.Muted ? " muted" : "")}{(s.Ok ? "" : " dead: " + s.Reason)} via {via}"); }
        public void PushLevel(VolumeState s) => Did.Add($"level {s.Level?.ToString() ?? "-"}{(s.Muted ? " muted" : "")}{(s.Ok ? "" : " dead: " + s.Reason)}");
    }

    sealed class Kit
    {
        public readonly Sidecar P = new();
        public readonly View V = new();
        public readonly FakeToastClock Clock = new(1000);
        public readonly List<string> Logs = new();
        public readonly List<(bool Ok, string? Why)> Health = new();
        public bool Enabled = true;
        public readonly VolumeKeys K;
        public Kit(bool test = false) { K = new VolumeKeys(P, V, (ok, why) => Health.Add((ok, why)), () => Enabled, test, Clock, Logs.Add); }
    }

    static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public async Task A_press_shows_at_once_with_the_last_level_then_moves_to_the_new_one()
    {
        var k = new Kit();
        k.K.Seed(new VolumeState(true, 40, false, null));
        k.P.Hold = true;
        var press = k.K.OnKeyAsync("VolumeUp");
        Assert.Equal("show volume 40 via VolumeUp", k.V.Did.Single());   // first paint does not wait on the sidecar
        k.P.ReleaseOne();
        await press;
        Assert.Equal("level 42", k.V.Did.Last());
        Assert.Equal(42, k.P.Level);
        Assert.Contains(k.P.Calls, c => c.Verb == "osd.volume" && c.Args == "{\"op\":\"up\"}");
    }

    [Fact]
    public async Task Down_and_mute_step_as_windows_does()
    {
        var k = new Kit();
        k.P.Level = 33;
        await k.K.OnKeyAsync("VolumeDown");
        Assert.Equal(32, k.P.Level);
        await k.K.OnKeyAsync("VolumeMute");
        Assert.True(k.P.Muted); Assert.Equal(32, k.P.Level);
        Assert.Equal("level 32 muted", k.V.Did.Last());
        await k.K.OnKeyAsync("VolumeUp");                                // up also unmutes
        Assert.False(k.P.Muted); Assert.Equal(34, k.P.Level);
    }

    [Fact]
    public async Task Presses_run_in_order_and_at_most_six_wait_behind_a_slow_sidecar()
    {
        var k = new Kit();
        k.P.Hold = true;
        var presses = Enumerable.Range(0, 8).Select(_ => k.K.OnKeyAsync("VolumeUp")).ToList();
        Assert.Equal(VolumeKeys.MaxWaiting, k.K.Waiting);
        Assert.Equal(8, k.K.Presses);
        // presses 7 and 8 are dropped; each said once (a shown pop-up in between resets the once-per-reason line, as in osd.js)
        Assert.Equal(2, k.Logs.Count(l => l == "no pop-up: VolumeUp: 6 presses are already waiting on the sidecar, so this one was dropped"));
        // the queue sends one step at a time: each reads what the previous wrote
        Assert.Equal(1, k.P.Waiting);
        for (int i = 0; i < 6; i++)
        {
            for (int w = 0; w < 400 && k.P.Waiting == 0; w++) await Task.Delay(5);   // the next step is sent once the previous lands
            k.P.ReleaseOne();
        }
        await Task.WhenAll(presses);
        Assert.Equal(52, k.P.Level);                                    // 40 + 6 x 2
        Assert.Equal(0, k.K.Waiting);
    }

    [Fact]
    public async Task An_open_quick_settings_panel_means_no_pop_up_but_the_volume_still_changes()
    {
        var k = new Kit();
        k.P.QsOpen = true;
        await k.K.OnKeyAsync("VolumeUp");
        Assert.Empty(k.V.Did); Assert.Equal(42, k.P.Level);
        Assert.Contains(k.Logs, l => l.Contains("the quick-settings panel is open"));
    }

    [Fact]
    public async Task A_test_run_is_a_dry_run_that_never_changes_the_users_volume()
    {
        var k = new Kit(test: true);
        await k.K.OnKeyAsync("VolumeUp");
        await k.K.OnKeyAsync("VolumeUp");
        Assert.Equal(40, k.P.Level);                                    // the real level never moved
        Assert.Equal("level 44", k.V.Did.Last());                       // the pretend level moved from 42 to 44
        Assert.Contains(k.P.Calls, c => c.Args.Contains("\"dry\":true") && c.Args.Contains("\"from\":{\"level\":42"));
        Assert.Contains(k.Logs, l => l.StartsWith("dry run (--test): VolumeUp would set the volume 42 -> 44") && l.EndsWith("your volume was not touched"));
    }

    [Fact]
    public async Task No_playback_device_keeps_the_key_and_says_so_on_the_pop_up()
    {
        var k = new Kit();
        k.P.Fail = "no default playback device (HRESULT 0x80070490)";
        await k.K.OnKeyAsync("VolumeUp");
        Assert.StartsWith("level - dead: the volume key did nothing: no default playback device", k.V.Did.Last());
        Assert.Empty(k.Health);
        await k.K.ProbeAsync();
        Assert.Equal((true, VolumeKeys.Note), k.Health.Last());           // still healthy at the probe
    }

    [Fact]
    public async Task A_failing_step_hands_the_keys_back_and_reprobes_every_30_s()
    {
        var k = new Kit();
        k.K.Seed(new VolumeState(true, 40, false, null));
        k.P.Fail = "the audio lane is busy";
        await k.K.OnKeyAsync("VolumeUp");
        Assert.False(k.K.Healthy);
        Assert.Contains("so the volume keys went back to Windows", k.Health.Last().Why);
        Assert.Equal("level 40 dead: the volume key failed: the audio lane is busy. The keys are Windows’ again", k.V.Did.Last());
        k.P.Fail = null;
        k.Clock.Advance(VolumeKeys.ReprobeMs);
        Assert.True(k.K.Healthy);
        Assert.Contains(k.Logs, l => l.StartsWith("volume keys: Mantle's while on (a dry step answered in 3 ms)"));
    }

    [Fact]
    public async Task An_old_sidecar_without_osd_volume_keeps_the_keys_windows()
    {
        var k = new Kit();
        k.P.Fail = "unknown command: osd.volume";
        await k.K.ProbeAsync();
        Assert.False(k.K.Healthy);
        Assert.Contains("predates osd.volume", k.Health.Last().Why);
    }

    [Fact]
    public async Task Switched_off_or_stopped_a_key_does_nothing()
    {
        var k = new Kit { Enabled = false };
        await k.K.OnKeyAsync("VolumeUp");
        Assert.Empty(k.P.Calls);
        var k2 = new Kit();
        k2.K.Stop();
        await k2.K.OnKeyAsync("VolumeUp");
        Assert.Empty(k2.P.Calls);
    }

    [Fact]
    public async Task The_watch_draws_at_once_outside_a_burst_and_waits_until_a_burst_settles()
    {
        var k = new Kit();
        k.V.Shown = true;
        k.K.OnWatch(J("{\"level\":30,\"muted\":false}"));
        Assert.Equal("level 30", k.V.Did.Last());                        // outside a burst: at once
        await k.K.OnKeyAsync("VolumeUp");                                // lands 32
        int n = k.V.Did.Count;
        k.K.OnWatch(J("{\"level\":30,\"muted\":false}"));                 // a late reading from before the step
        Assert.Equal(n, k.V.Did.Count);                                  // held back: no jump backwards
        k.Clock.Advance(VolumeKeys.SettleMs + 5);
        Assert.Equal("level 30", k.V.Did.Last());                        // the newest has the last word once it settles
        k.K.OnWatch(J("{\"ok\":false}"));
        Assert.False(k.K.Volume!.Ok); Assert.Equal("the volume watch failed without a reason", k.K.Volume.Reason);
    }
}
