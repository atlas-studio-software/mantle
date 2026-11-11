using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.OnTop;

/// <summary>
/// KEEP ANY WINDOW ON TOP (lib/features/ontop.js): Win+Ctrl+T flips "on top" for the window in front; the bar's
/// right-click "Keep on top" does it for that app's window (<c>ontop.set</c>); <c>ontop.state</c> {pinned:[{hwnd,name}],
/// test} is pushed on every change and replayed to a late subscriber (the bar's pin badge); every result is pushed as
/// <c>ontop.toast</c>. The rules are <see cref="OnTopCore"/>; the Windows half is the sidecar's WindowTop.cs.
/// Put back: a stop (quit, "Put Windows back", tray off) takes every pin off top; the pins are saved to the setting
/// <c>pins</c> on every change and the next start keeps those still on top (adopt); a rest (a game) takes them off top
/// and the wake puts back the ones that are still the same window. --test: the sidecar is passive, so a real window is a
/// dry run. MANTLE_TEST_ONTOP_FAKE=1 (--test only) answers <c>ontop.set</c> and <c>ontop.check</c> as a made-up Windows
/// that accepts every pin and changes nothing, so the badge and "Stop keeping on top" can be tested.
/// </summary>
[Feature("ontop", "Keep a window on top (Win+Ctrl+T)", true, Chords = new[] { Chord }, Rest = true)]
public sealed class OnTopFeature : IFeature
{
    public const string Chord = "Win+Ctrl+T";
    public const string StateEvent = "ontop.state", ToastEvent = "ontop.toast";
    static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(2);

    // process scope ON PURPOSE: survives a rest's stop -> the next install (the module scope of ontop.js)
    static IReadOnlyList<OnTopPin>? _suspended;

    IFeatureContext _ctx = null!;
    OnTopCore _core = null!;
    Timer? _sweep;
    SynchronizationContext? _host;
    bool _stopped, _stopping, _first = true;
    string _lastSweepErr = "";
    readonly HashSet<string> _fakeTop = new(StringComparer.Ordinal);
    bool _fake;

    JsonObject State() => new() {
        ["pinned"] = new JsonArray(_core.List.Select(p => (JsonNode?)new JsonObject { ["hwnd"] = p.Hwnd, ["name"] = p.Name }).ToArray()),
        ["test"] = _ctx.Flags.Test,
    };

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

    async ValueTask<PlatformReply> Send(string verb, JsonObject args)
    {
        if (_fake && verb == "ontop.set") {
            string h = args["hwnd"]?.ToString() ?? "";
            bool on = args["on"]?.GetValue<bool>() ?? false;
            bool was = _fakeTop.Contains(h);
            if (on) _fakeTop.Add(h); else _fakeTop.Remove(h);
            _ctx.Log($"test fixture (MANTLE_TEST_ONTOP_FAKE): would {(on ? "keep" : "stop keeping")} window {h} on top; nothing in Windows changed");
            return PlatformReply.Success(El(new JsonObject { ["hwnd"] = h, ["was"] = was, ["topmost"] = on, ["changed"] = was != on, ["tries"] = 1 }));
        }
        if (_fake && verb == "ontop.info") {
            // the made-up Windows knows no pid (0 = not checked) and only its own "on top" marks
            string h = args["hwnd"]?.ToString() ?? "0";
            return PlatformReply.Success(El(new JsonObject { ["hwnd"] = h, ["pid"] = 0, ["name"] = "", ["topmost"] = _fakeTop.Contains(h) }));
        }
        if (_fake && verb == "ontop.check") {
            var ws = new JsonArray();
            foreach (var w in args["hwnds"] as JsonArray ?? new JsonArray()) {
                string h = w?["hwnd"]?.ToString() ?? "";
                ws.Add((JsonNode)new JsonObject { ["hwnd"] = h, ["alive"] = true, ["topmost"] = _fakeTop.Contains(h) });
            }
            return PlatformReply.Success(El(new JsonObject { ["windows"] = ws }));
        }
        return await _ctx.Platform.CallAsync(verb, args, TimeSpan.FromMilliseconds(verb == "ontop.set" ? 6000 : 4000)).ConfigureAwait(true);
    }

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _host = SynchronizationContext.Current;
        _fake = ctx.Flags.Test && Environment.GetEnvironmentVariable("MANTLE_TEST_ONTOP_FAKE") == "1";
        if (_fake) ctx.Log("a --test run with MANTLE_TEST_ONTOP_FAKE=1: keep on top answers as a made-up Windows; no window is changed");
        ctx.Health(true, "Win+Ctrl+T or right-click an app on the bar keeps its window on top");
        _core = new OnTopCore(Send, ctx.Log, l => {
            var arr = new JsonArray(l.Select(p => (JsonNode?)new JsonObject { ["hwnd"] = p.Hwnd, ["pid"] = p.Pid, ["name"] = p.Name }).ToArray());
            _ = ctx.Settings.SetAsync("pins", arr);
        }, ctx.Flags.Test);
        _core.Changed += _ => { if (!_stopped) ctx.Broadcast(StateEvent, El(State())); if (!_stopping) Arm(); };
        ctx.Replay(StateEvent, () => El(State()));
        ctx.OnChord(Chord, c => _ = ChordAsync());
        ctx.Command("set", c => SetAsync(c.Args));
        ctx.Command("state", _ => {
            var s = State(); s["ok"] = true; s["suspended"] = _suspended?.Count ?? 0;
            return ValueTask.FromResult(CommandResult.Success(El(s)));
        });
        ctx.OnEnabled(on => { if (!on) _ = _core.ReleaseAllAsync("switched off in the tray"); });
        ctx.Platform.OnReady(() => _ = ReadyAsync());
        return ValueTask.CompletedTask;
    }

    void Arm()
    {
        if (_core.Count > 0 && _sweep == null && !_stopped) {
            var host = _host;
            _sweep = new Timer(_ => {
                void Tick() => _ = SweepTick();
                if (host != null) host.Post(_ => Tick(), null); else Tick();
            }, null, SweepEvery, SweepEvery);
        } else if (_core.Count == 0 && _sweep != null) { _sweep.Dispose(); _sweep = null; }
    }

    async Task SweepTick()
    {
        if (_stopped) return;
        var r = await _core.SweepAsync().ConfigureAwait(true);
        string e = r.Error ?? "";
        if (e.Length > 0 && e != _lastSweepErr) _ctx.Log($"could not check the windows kept on top ({e}); will keep trying");
        _lastSweepErr = e;
    }

    void Toast(OnTopResult r, string hint = "")
    {
        if (_stopped) return;
        var p = r.ToJson();
        p["hint"] = r.Did == "pinned" ? Chord + " again to stop" : hint;
        _ctx.Broadcast(ToastEvent, El(p));
    }

    async ValueTask<OnTopFacts> Facts(string? hwnd)
    {
        var r = hwnd != null ? await Send("ontop.info", new JsonObject { ["hwnd"] = hwnd }).ConfigureAwait(true)
            : await Send("ontop.front", new JsonObject()).ConfigureAwait(true);
        return r.Ok ? OnTopFacts.Parse(r.Result) : new OnTopFacts("0", 0, 0, "", "", false, r.Error ?? "no answer");
    }

    async Task ChordAsync()
    {
        if (!_ctx.Enabled) return;
        Toast(await _core.ToggleAsync(await Facts(null).ConfigureAwait(true), Chord).ConfigureAwait(true));
    }

    async ValueTask<CommandResult> SetAsync(JsonElement a)
    {
        static string? S(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
        OnTopResult res;
        if (!_ctx.Enabled) res = new OnTopResult(false, "off", "Keep on top is off", "keep on top is switched off in the tray", null, "warn");
        else if (S(a, "hwnd") is not { Length: > 0 } hwnd) res = new OnTopResult(false, "refused", "Nothing to keep on top", "no window was named, so nothing changed", null, "warn");
        else {
            var t = await Facts(hwnd).ConfigureAwait(true);
            int want = int.TryParse(S(a, "pid"), out int w) ? w : 0;
            if (want != 0 && t.Refuse == null && t.Pid != 0 && want != t.Pid && want != t.AppPid) {
                res = new OnTopResult(false, "refused", "That window has closed", "its handle now belongs to another program", null, "warn");
                _ctx.Log($"refused (menu): {res.Text}: {res.Reason}");
                Toast(res);
                return CommandResult.Success(El(res.ToJson()));
            }
            // the bar's name is the one the user sees (a browser's exe can be a bare electron.exe)
            if (S(a, "name") is { Length: > 0 } nm) t = t with { Name = nm.Length > 48 ? nm[..48] : nm };
            bool? on = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("on", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False ? o.ValueKind == JsonValueKind.True : null;
            res = on is bool b ? await _core.SetAsync(t, b, "menu").ConfigureAwait(true) : await _core.ToggleAsync(t, "menu").ConfigureAwait(true);
        }
        Toast(res);
        return CommandResult.Success(El(res.ToJson()));
    }

    async Task ReadyAsync()
    {
        if (!_first) return;   // a sidecar restart changes nothing in Windows: the pins stand
        _first = false;
        if (_suspended is { } saved) {
            _suspended = null;
            var r = await _core.ResumeAsync(saved, "the game").ConfigureAwait(true);
            foreach (var s in r.Skipped)
                if (!s.Contains("already on top", StringComparison.Ordinal) && !s.Contains("closed", StringComparison.Ordinal))
                    Toast(new OnTopResult(false, "failed", "Not back on top", s, null, "warn"));
            return;
        }
        var left = new List<OnTopPin>();
        if (_ctx.Settings.TryGet("pins", out var pins) && pins.ValueKind == JsonValueKind.Array)
            foreach (var p in pins.EnumerateArray()) {
                if (p.ValueKind != JsonValueKind.Object) continue;
                string h = p.TryGetProperty("hwnd", out var hv) ? hv.ToString() : "";
                int pid = p.TryGetProperty("pid", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out int n) ? n : 0;
                string name = p.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "";
                left.Add(new OnTopPin(h, pid, name));
            }
        await _core.AdoptAsync(left).ConfigureAwait(true);
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        _stopping = true;
        _sweep?.Dispose(); _sweep = null;
        if (stop.Rest) _suspended = (await _core.ReleaseAllAsync("a game runs: " + stop.Reason).ConfigureAwait(true)).ToList();
        else await _core.ReleaseAllAsync(stop.Reason).ConfigureAwait(true);
        _stopped = true;
    }
}
