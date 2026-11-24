using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Timers;

/// <summary>
/// [Feature("timer")] (lib/features/timer.js), lane r10-timer: the top bar's timer and stopwatch as a service around
/// <see cref="TimerEngine"/>. Commands timer.state/start/stopwatch/pause/resume/cancel/add/dismiss/again/snooze/chime,
/// the state event <c>timer.state</c> (replayed to each new subscriber), the one timeout on the host clock, and the
/// three keys (clock, last, held) in <c>features\timer.json</c> through the feature store: its atomic write, the
/// .unreadable rule and the write gate are the data layer's, and a refused write is a failed save in its words.
/// DEVIATIONS (until the surfaces exist): the done notice goes to <see cref="Host.ShowDone"/> when the app wires one
/// (the notifications service / a pop-up surface) and is ALSO broadcast as <c>timer.done</c>; with none wired it counts
/// as a failed pop-up (logged, beeped, done.popup 'failed' in timer.state, so the top bar can say "Timer done"). The
/// PC waking is the command <c>timer.wake</c> (the app's resume hook calls it, as it calls gcal.resume). The game state
/// comes from <see cref="Host.Resting"/>; without it the state says resting 'unknown' and a notice shows at once.
/// </summary>
[Feature("timer", "Timer and stopwatch (top bar)", true)]
public sealed class TimerFeature : IFeature
{
    public const string StateEvent = "timer.state";
    public const string DoneEvent = "timer.done";
    public const string NoSurface = "no done pop-up is wired in this build yet";
    public const string NoBeep = "this build has no shell beep yet";

    /// <summary>What the app (or a test) gives. Anything null takes the default above.</summary>
    public sealed record Host(HostClock? Clock = null, Func<long>? Now = null, Func<TimerDoneNotice, TimerNoticeResult>? ShowDone = null,
        Action? CloseDone = null, Action<string>? Beep = null, Func<(bool Known, string? Game)>? Resting = null);

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;

    readonly Host? _given;
    public TimerFeature() { }
    public TimerFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    TimerEngine _engine = null!;
    IDisposable? _timeout;
    bool _stopped;

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

    /// The engine, for tests and the app's wiring (game state changes go to <see cref="TimerEngine.OnRestingChanged"/>).
    public TimerEngine Engine => _engine;

    sealed class Adapter : ITimerHost
    {
        readonly TimerFeature _f;
        public Adapter(TimerFeature f) { _f = f; }
        public long Now => _f._h.Now?.Invoke() ?? _f._clock.Time.GetUtcNow().ToUnixTimeMilliseconds();
        public long Mono => _f._clock.NowMs;
        public bool Enabled => _f._ctx.Enabled;
        public bool Test => _f._ctx.Flags.Test;
        public (bool Known, string? Game) Resting => _f._h.Resting?.Invoke() ?? (false, null);
        public void Log(string text) => _f._ctx.Log(text);
        public void Health(bool ok, string? reason) => _f._ctx.Health(ok, reason);
        public ValueTask<StoreWrite> Save(Action<JsonObject> edit) => _f._ctx.Settings.UpdateAsync(edit);
        public void Changed() => _f.Push();
        public void Arm(long? delayMs) => _f.Arm(delayMs);
        public TimerNoticeResult ShowDone(TimerDoneNotice n) => _f.Show(n);
        public void CloseDone() => _f._h.CloseDone?.Invoke();
        public void Beep(string why)
        {
            if (_f._h.Beep is { } b) b(why);
            else _f._ctx.Log($"no beep ({NoBeep}): {why}");
        }
    }

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        _engine = new TimerEngine(new Adapter(this));
        ctx.Health(true, TimerEngine.OkWords);

        IStore store = ctx.Settings;
        string? readError = store.State is ReadState.Ok or ReadState.Missing or ReadState.Newer ? null : store.Reason ?? store.State.ToString();
        if (store.State == ReadState.Newer && store.Reason != null) ctx.Log(store.Reason);
        JsonElement? Key(string k) => store.TryGet(k, out var v) ? v : null;
        _engine.Restore(Key("clock"), Key("held"), Key("last"), readError);

        ctx.Replay(StateEvent, () => El(_engine.View()));
        ctx.Command("state", _ => Reply(TimerReply.Done));
        ctx.Command("start", c => Reply(_engine.StartTimer(Str(c.Args, "text"), Num(c.Args, "ms"), Str(c.Args, "label"))));
        ctx.Command("stopwatch", _ => Reply(_engine.Stopwatch()));
        ctx.Command("pause", _ => Reply(_engine.Pause()));
        ctx.Command("resume", _ => Reply(_engine.Resume()));
        ctx.Command("cancel", _ => Reply(_engine.Cancel()));
        ctx.Command("add", c => Reply(_engine.Add(Num(c.Args, "ms"))));
        ctx.Command("dismiss", _ => {
            bool had = _engine.Dismiss();
            JsonObject v = _engine.View();
            v["ok"] = true;
            v["dismissed"] = had;
            return ValueTask.FromResult(CommandResult.Success(El(v)));
        });
        ctx.Command("again", c => Reply(_engine.Again(EndAt(c.Args))));
        ctx.Command("snooze", c => Reply(_engine.Snooze(Num(c.Args, "ms"), EndAt(c.Args))));
        ctx.Command("chime", c => Reply(_engine.Chime(Bool(c.Args, "ok"), Bool(c.Args, "muted"), Str(c.Args, "reason"))));
        ctx.Command("wake", _ => { _engine.OnWake(); return Reply(TimerReply.Done); });
        ctx.OnEnabled(on => { if (!_stopped) _engine.OnEnabled(on); });
        return ValueTask.CompletedTask;
    }

    ValueTask<CommandResult> Reply(TimerReply r)
    {
        if (!r.Ok) return ValueTask.FromResult(CommandResult.Fail(r.Reason ?? "the timer refused without saying why"));
        JsonObject v = _engine.View();
        v["ok"] = true;
        return ValueTask.FromResult(CommandResult.Success(El(v)));
    }

    static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static double? Num(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d) ? d : null;

    static bool Bool(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.True;

    /// Absent = an older caller (no stale check); present but not a number = NaN (always stale).
    static double? EndAt(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("endAt", out var p)) return null;
        return p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d) ? d : double.NaN;
    }

    void Push()
    {
        if (_stopped) return;
        try { _ctx.Broadcast(StateEvent, El(_engine.View())); }
        catch (Exception ex) { _ctx.Log($"the state push failed: {ex.GetType().Name}"); }
    }

    void Arm(long? delayMs)
    {
        _timeout?.Dispose();
        _timeout = null;
        if (_stopped || delayMs is not { } ms) return;
        _timeout = _clock.Later(TimeSpan.FromMilliseconds(ms), () => { _timeout = null; if (!_stopped) _engine.Check(); });
    }

    TimerNoticeResult Show(TimerDoneNotice n)
    {
        try {
            var o = new JsonObject { ["title"] = n.Title, ["sub"] = n.Sub, ["endAt"] = n.EndAt };
            var buttons = new JsonArray();
            foreach (TimerButton b in n.Buttons) buttons.Add(new JsonObject { ["id"] = b.Id, ["label"] = b.Label, ["tooltip"] = b.Tooltip, ["primary"] = b.Primary });
            o["buttons"] = buttons;
            _ctx.Broadcast(DoneEvent, El(o));
        } catch (Exception ex) { _ctx.Log($"the done event could not be sent: {ex.GetType().Name}"); }
        if (_h.ShowDone is { } show) return show(n);
        return new TimerNoticeResult(false, NoSurface);
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        _timeout?.Dispose();
        _timeout = null;
        _engine?.Stop();
        return ValueTask.CompletedTask;
    }
}
