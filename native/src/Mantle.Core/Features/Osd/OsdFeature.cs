using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features.Toasts;

namespace Mantle.Core.Features.Osd;

// The feature half of lib/features/osd.js: the volume keys (VolumeKeys, part 1) and the commands the pop-up
// surface (Mantle.App/Surfaces/Osd) and the bar ask. The window itself is the surface's: this side decides WHEN
// the pop-up shows and with WHAT, and sends it as events ("osd.show", "osd.level", "osd.hide"); the surface
// draws, fades and says "osd.faded {seq}" when it went, so a fade that crosses a newer show never takes it down.
//
// THE KEY TAKEOVER: the chords below are taken from Windows only by the feature host's hotkey call, which is
// sent ONLY in a --live run (FeatureHost.ApplyAsync, never in --test). In --test a volume key is driven by
// features.fire / --test-chord and steps the volume as a DRY run (VolumeKeys), so the user's volume is never
// touched. The sidecar dying gives the keys back to Windows by itself.
//
// Not ported in this part (said plainly in docs/lanes/osd-toasts.md): brightness (the osd.bright watch that
// parks Windows' brightness flyout), the bar-follow glide of the pane (osd.lift), the click-outside watch of
// the open panel (popwatch) and the "seen on screen" check of osd.mixer.

/// <summary>What the surface is told to show. <c>Seq</c> is echoed back in <c>osd.faded</c>.</summary>
public sealed record OsdShow(string Kind, VolumeState State, bool Expand, bool Below, string Via, int Seq, bool Hold);
/// <summary>A new reading for the pop-up that is already up.</summary>
public sealed record OsdLevel(string Kind, VolumeState State);
/// <summary>Take the pop-up down now (switched off, stopped).</summary>
public sealed record OsdHide(string Why);

[Feature("osd", "Volume and brightness pop-up", true, Chords = new[] { "VolumeUp", "VolumeDown", "VolumeMute" }, Rest = true)]
public sealed class OsdFeature : IFeature, IOsdView
{
    public const int BackstopMs = 60000;   // hidden even if the surface never says it faded (osd.js BACKSTOP_MS)

    IToastClock? _clock;
    IFeatureContext? _ctx;
    VolumeKeys? _keys;
    int _seq;
    bool _shown, _stopped;
    string _shownKind = "volume";
    int _w, _h;
    IDisposable? _backstop, _testT;

    public OsdFeature() { }
    /// For the tests: a clock on manual time.
    public OsdFeature(IToastClock clock) { _clock = clock; }

    public bool Shown => _shown;
    public int Seq => _seq;
    public VolumeKeys? Keys => _keys;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _clock ??= new HostToastClock(new HostClock(null, SynchronizationContext.Current));
        _keys = new VolumeKeys(ctx.Platform, this, (ok, why) => ctx.Health(ok, why), () => ctx.Enabled, ctx.Flags.Test, _clock, ctx.Log);
        foreach (var k in new[] { "VolumeUp", "VolumeDown", "VolumeMute" })
            ctx.OnChord(k, key => _ = _keys.OnKeyAsync(key));
        ctx.Platform.On("volume", e => _keys.OnWatch(e.Body));
        ctx.OnEnabled(on => { if (!on) Hide("switched off in the tray"); });

        ctx.Command("size", Size);
        ctx.Command("faded", Faded);
        ctx.Command("show", c => new(Answer(Show("volume", PageState(c.Args), false, false, Str(c.Args, "via", 40) ?? "bar"))));
        ctx.Command("mixer", _ => {
            var r = Show("volume", Current(), true, false, "bar");
            if (!r.Ok) ctx.Log($"app volumes: not opened from the bar's speaker: {r.Reason}");
            else if (ctx.Flags.Test) ctx.Log("app volumes: opened from the bar's speaker (--test)");
            return new(Answer(r));
        });
        ctx.Command("state", _ => new(CommandResult.Success(StateJson())));
        if (ctx.Flags.Test)
        {
            // --test only: the pop-up with a MADE-UP reading (Electron's testShow). Nothing is read from or
            // written to Windows; held up until testHide or `ms` (default 20 s). Native adds muted / dead / reason so
            // the screenshot pairs can draw every state the mock page draws.
            ctx.Command("testShow", c => {
                var a = c.Args;
                string k = Str(a, "kind", 20) == "brightness" ? "brightness" : "volume";
                double want = Num(a, "level") ?? 62;
                int level = (int)Math.Max(0, Math.Min(100, Math.Round(want)));
                bool dead = Bool(a, "dead");
                var st = dead ? new VolumeState(false, null, false, Str(a, "reason", 200) ?? OsdModel.NoDevice)
                              : new VolumeState(true, level, Bool(a, "muted"), null);
                int ms = (int)Math.Max(1000, Math.Min(60000, Num(a, "ms") ?? 20000));
                var r = Show(k, st, Bool(a, "expand") && k == "volume", true, "testShow", Bool(a, "below"));
                _testT?.Dispose(); _testT = null;
                if (r.Ok) _testT = _clock!.Later(ms, () => { _testT = null; Hide("testShow: its time is up"); });
                return new(Answer(r));
            });
            ctx.Command("testHide", _ => { _testT?.Dispose(); _testT = null; Hide("testHide"); return new(CommandResult.Success(StateJson())); });
        }
        _ = _keys.ProbeAsync();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        Hide(stop.Rest ? "a rest: " + stop.Reason : "stopped: " + stop.Reason);
        _stopped = true;
        _keys?.Stop();
        _testT?.Dispose(); _backstop?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ IOsdView (what VolumeKeys asks)

    void IOsdView.Show(string kind, VolumeState state, string via) => Show(kind, state, false, false, via);

    public void PushLevel(VolumeState state)
    {
        if (_ctx == null || _stopped) return;
        _ctx.Broadcast("osd.level", new OsdLevel("volume", state));
    }

    // ------------------------------------------------------------------ show and hide

    public readonly record struct ShowResult(bool Ok, string? Reason, int Seq);

    public ShowResult Show(string kind, VolumeState state, bool expand, bool hold, string via, bool below = false)
    {
        if (_ctx == null || _stopped) return new(false, "the pop-up is stopped", _seq);
        if (!_ctx.Enabled) return new(false, "the pop-up is switched off in the tray", _seq);
        _shownKind = kind;
        _shown = true;
        _ctx.Broadcast("osd.show", new OsdShow(kind, state, expand, below, via, ++_seq, hold));
        _backstop?.Dispose();
        _backstop = _clock!.Later(BackstopMs, () => { _backstop = null; Hide("backstop: the pop-up never said it faded"); });
        return new(true, null, _seq);
    }

    public void Hide(string? why)
    {
        _backstop?.Dispose(); _backstop = null;
        if (!_shown) return;
        _shown = false;
        if (why != null && _ctx != null) { _ctx.Broadcast("osd.hide", new OsdHide(why)); if (_ctx.Flags.Test) _ctx.Log("hidden: " + why); }
    }

    VolumeState Current() => _keys?.Volume ?? new VolumeState(false, null, false, "the volume has not been read yet");

    // ------------------------------------------------------------------ commands

    ValueTask<CommandResult> Size(CommandCall c)
    {
        _w = (int)Math.Max(80, Math.Min(600, Math.Round(Num(c.Args, "w") ?? 0)));
        _h = (int)Math.Max(40, Math.Min(900, Math.Round(Num(c.Args, "h") ?? 0)));
        bool open = Bool(c.Args, "open");
        if (_ctx!.Flags.Test) _ctx.Log($"page drew {_w}x{_h}{(open ? " (panel open)" : "")}{(Str(c.Args, "at", 40) is string at ? " at " + at : "")}");
        return new(CommandResult.Success(FeatureOptions.Parse(new JsonObject { ["ok"] = true, ["w"] = _w, ["h"] = _h }.ToJsonString())));
    }

    /// THE FADE RACE (osd.js): the surface's "faded" can cross a newer show; it echoes the seq it faded, an older one is ignored.
    ValueTask<CommandResult> Faded(CommandCall c)
    {
        double? n = Num(c.Args, "seq");
        if (n is double s && s < _seq)
            return new(CommandResult.Success(FeatureOptions.Parse(new JsonObject { ["ok"] = true, ["ignored"] = $"a newer pop-up ({_seq}) is showing" }.ToJsonString())));
        Hide(null);
        return new(CommandResult.Success(FeatureOptions.Parse("{\"ok\":true}")));
    }

    /// A caller's state is checked before it reaches the surface: a number 0-100 or the real reading (pageState).
    VolumeState PageState(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("state", out var s) || s.ValueKind != JsonValueKind.Object) return Current();
        if (Num(s, "level") is not double l) return Current();
        bool ok = !(s.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False);
        return new VolumeState(ok, Math.Max(0, Math.Min(100, Math.Round(l))), Bool(s, "muted"), Str(s, "reason", 200));
    }

    JsonElement StateJson()
    {
        var v = _keys?.Volume;
        var o = new JsonObject {
            ["ok"] = true, ["shown"] = _shown, ["kind"] = _shownKind, ["seq"] = _seq,
            ["size"] = new JsonObject { ["w"] = _w, ["h"] = _h },
            ["vol"] = v == null ? null : new JsonObject { ["ok"] = v.Ok, ["level"] = v.Level, ["muted"] = v.Muted, ["reason"] = v.Reason },
            ["healthy"] = _keys?.Healthy ?? false, ["presses"] = _keys?.Presses ?? 0, ["waiting"] = _keys?.Waiting ?? 0,
            ["keys"] = "VolumeUp, VolumeDown, VolumeMute: Mantle’s while this is on and healthy (a --live run only)",
        };
        return FeatureOptions.Parse(o.ToJsonString());
    }

    static CommandResult Answer(ShowResult r)
    {
        var o = new JsonObject { ["ok"] = r.Ok, ["seq"] = r.Seq, ["reason"] = r.Reason };
        return r.Ok ? CommandResult.Success(FeatureOptions.Parse(o.ToJsonString())) : CommandResult.Fail(r.Reason ?? "the pop-up did not show");
    }

    static double? Num(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    static bool Bool(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string? Str(JsonElement a, string k, int max) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() is { } s && s.Length > max ? s[..max] : v.GetString()) : null;
}
