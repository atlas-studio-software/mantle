using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Notifications;

namespace Mantle.Core.Features.Timers;

// lane r10-timer: port of lib/features/timer.js's state machine without Electron or WinUI: commands, the ONE timeout
// (as a delay the host arms), the monotonic deadline that a clock set back cannot stretch, sleep, saving the three keys
// (clock, last, held) through the feature's store (whose atomic write, .unreadable rule and write gate are the data
// layer's), restore after a restart, the tray switch, games holding the done notice, and the done notice itself as a
// REQUEST (TimerDoneNotice) the host hands to the notifications service. Host thread only. Everything that is refused
// or fails says so in words: a reply reason, a log line, health, or the state's persist / restoreError / done.reason.

/// <summary>What the engine needs from the world. Tests give a fake; TimerFeature gives the feature context.</summary>
public interface ITimerHost
{
    /// Wall-clock ms since 1970 (what is saved and shown).
    long Now { get; }
    /// A monotonic ms count (only differences mean anything).
    long Mono { get; }
    bool Enabled { get; }
    bool Test { get; }
    void Log(string text);
    void Health(bool ok, string? reason);
    /// ONE write of the feature's store; the edit gets the whole document.
    ValueTask<StoreWrite> Save(Action<JsonObject> edit);
    /// The state changed: broadcast <c>timer.state</c>.
    void Changed();
    /// Arm the one timeout for <c>delayMs</c> (replacing any), or none (null). When it fires, call <see cref="TimerEngine.Check"/>.
    void Arm(long? delayMs);
    /// Show the done notice (the request to the notifications service): shown, or not with the reason.
    TimerNoticeResult ShowDone(TimerDoneNotice notice);
    /// Take a shown done notice away (nothing to do when none shows).
    void CloseDone();
    /// One shell beep, the sound of last resort.
    void Beep(string why);
    /// Known = the top bar said whether a game runs; Game = its words while one runs, else null.
    (bool Known, string? Game) Resting { get; }
}

public sealed record TimerNoticeResult(bool Shown, string? Reason)
{
    public static readonly TimerNoticeResult Ok = new(true, null);
}

public sealed record TimerReply(bool Ok, string? Reason)
{
    public static readonly TimerReply Done = new(true, null);
    public static TimerReply No(string reason) => new(false, reason);
}

public sealed record TimerButton(string Id, string Label, string? Tooltip = null, bool Primary = false);

/// <summary>The done notice as words (TIMER-062..066): title, sub line, buttons left to right. Every action names EndAt.</summary>
public sealed record TimerDoneNotice(string Title, string Sub, long EndAt, IReadOnlyList<TimerButton> Buttons)
{
    public const string SnoozeOne = "snooze-1", SnoozeFive = "snooze-5", Again = "again", Dismiss = "dismiss";

    /// <summary>As one of Mantle's own banners (Features/Notifications/OwnBanners). DEVIATION: a banner takes at most
    /// 3 buttons, so Dismiss is the banner's own X there and the three buttons are +1 min, +5 min and Restart.</summary>
    public OwnBannerPost ToBannerPost()
    {
        var actions = new List<OwnButtonPost>();
        foreach (TimerButton b in Buttons) if (b.Id != Dismiss) actions.Add(new OwnButtonPost(b.Id, b.Label, b.Primary));
        return new OwnBannerPost(Title: Title, Body: Sub, App: "Mantle timer", Tone: "info", Actions: actions);
    }
}

/// <summary>The finish being shown or held.</summary>
public sealed class TimerDoneInfo
{
    public TimerDoneInfo(TimerFinish f) { Finish = f; }
    public TimerFinish Finish { get; }
    public string? During { get; set; }
    /// null (not asked yet), "shown", "held" or "failed".
    public string? Popup { get; set; }
    public string? Reason { get; set; }
    public string? Sound { get; set; }
    public string? SoundReason { get; set; }
}

public sealed class TimerEngine
{
    public const long DoneMs = 120000;              // the done notice closes itself after this (the surface's timer)
    public const long MaxDelay = 2147483647;        // one timeout at most this long, then re-armed (Electron's setTimeout cap)
    public const long LateMs = 60000;               // fired this late (a sleep): the notice says the PC was asleep
    public const string OkWords = "the top bar's timer and stopwatch";
    public const string OffWords = "The timer is switched off in the tray";
    public const string NoneRunning = "No timer or stopwatch is running";
    public const string Stale = "That timer was already dealt with";
    public const string RestingUnknown = "the top bar has not started, so Mantle cannot tell whether a game runs; a done pop-up shows at once";
    static readonly long[] Snoozes = { 60000, 300000 };

    readonly ITimerHost _h;
    TimerState _st = TimerState.Idle;
    TimerState? _monoFor;
    long _monoEnd;
    long? _lastMs;
    TimerDoneInfo? _done;
    bool _held;
    string? _heldGame;
    bool _persistOk = true;
    string? _persistReason;
    string? _restoreError;
    bool _stopped;

    public TimerEngine(ITimerHost host) { _h = host ?? throw new ArgumentNullException(nameof(host)); }

    public TimerState State => _st;
    public TimerDoneInfo? Done => _done;
    public bool Held => _held;
    public long? LastMs => _lastMs;
    public bool PersistOk => _persistOk;
    public string? PersistReason => _persistReason;
    public string? RestoreError => _restoreError;
    /// Beeps asked for (in --test only counted, never played).
    public int Beeps { get; private set; }

    static string Words(Exception ex) => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>The log names the LENGTH, never the label (a label is free text; the log gets no typed words).</summary>
    public static string Describe(TimerState s, long t) => s.Kind switch {
        TimerKind.Timer => $"{TimerCore.LabelFor(s.TotalMs)} timer ({TimerCore.FormatClock(TimerCore.LeftMs(s, t))} left{(s.Paused ? ", paused" : "")})",
        TimerKind.Stopwatch => $"stopwatch ({TimerCore.FormatClock(TimerCore.ElapsedMs(s, t), true)}{(s.Paused ? ", paused" : "")})",
        _ => "nothing",
    };

    /* ---- start-up ---- */

    /// <summary>Install: what the store holds (absent keys = null), or the words for why it could not be read.</summary>
    public void Restore(JsonElement? clock, JsonElement? held, JsonElement? last, string? readError = null)
    {
        _lastMs = null;
        if (last is { ValueKind: JsonValueKind.Number } l && l.TryGetDouble(out double lv)) {
            TimerLength lc = TimerCore.Check(lv);
            if (lc.Ok) _lastMs = lc.Ms;
        }
        if (readError != null) {
            _restoreError = $"the saved timer could not be read ({readError})";
            _h.Log(_restoreError);
            clock = null;
            held = null;
        }
        RunRestore(clock, held);
    }

    TimerFinish? HeldBack(JsonElement? h, long t)
    {
        if (h is not { ValueKind: JsonValueKind.Object } o) return null;
        if (!o.TryGetProperty("totalMs", out var tm) || tm.ValueKind != JsonValueKind.Number || !tm.TryGetDouble(out double total)) return null;
        TimerLength len = TimerCore.Check(total);
        if (!len.Ok) return null;
        if (!o.TryGetProperty("endAt", out var ea) || ea.ValueKind != JsonValueKind.Number || !ea.TryGetDouble(out double end)
            || double.IsNaN(end) || double.IsInfinity(end) || end > t + 60000 || t - end > TimerCore.StaleMs) return null;
        string label = TimerCore.CleanLabel(o.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String ? lb.GetString() : null);
        bool whileOff = o.TryGetProperty("whileOff", out var wo) && wo.ValueKind == JsonValueKind.True;
        long? of = o.TryGetProperty("of", out var ofe) && ofe.ValueKind == JsonValueKind.Number && ofe.TryGetDouble(out double ofv) && ofv >= TimerCore.MinMs && ofv <= TimerCore.MaxMs ? (long)ofv : null;
        return new TimerFinish(label.Length > 0 ? label : TimerCore.LabelFor(len.Ms), len.Ms, (long)end, whileOff, Of: of, WasHeld: true);
    }

    void RunRestore(JsonElement? saved, JsonElement? savedHeld)
    {
        long t = _h.Now;
        TimerRestore r = TimerCore.Restore(saved, t);
        TimerFinish? back = HeldBack(savedHeld, t);
        if (savedHeld is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } && back == null)
            _h.Log("a done pop-up saved during a game could not be read or is over 7 days old: not shown");
        _st = r.State;
        if (r.Reason != null) { _restoreError = r.Reason; _h.Log($"{r.Reason}; starting with no timer"); }
        if (!_h.Enabled && _st.Kind != TimerKind.Idle) {
            _h.Log($"switched off in the tray: the saved {Describe(_st, t)} was cancelled");
            _st = TimerState.Idle;
        }
        Save();     // at once: a timer that fired below is idle on disk before it shows
        Arm();
        TimerFinish? fire = r.Fired ?? back;   // a fresh finish wins over an older held one
        if (fire != null) {
            if (_h.Enabled) Finish(fire);
            else _h.Log($"a {TimerCore.LabelFor(fire.TotalMs)} timer ended while Mantle was off, but the timer is switched off in the tray: not shown");
        } else if (_st.Kind != TimerKind.Idle) _h.Log($"picked up the {Describe(_st, t)}");
        _h.Changed();
    }

    /* ---- saving ---- */

    void Edit(JsonObject o)
    {
        o["clock"] = TimerCore.ToJson(_st);
        o["last"] = _lastMs is { } lm ? JsonValue.Create(lm) : null;
        if (_held && _done != null) {
            TimerFinish f = _done.Finish;
            var h = new JsonObject { ["label"] = f.Label, ["totalMs"] = f.TotalMs, ["endAt"] = f.EndAt, ["whileOff"] = f.WhileOff };
            if (TimerCore.OfMs(f.Of) is { } of) h["of"] = of;
            o["held"] = h;
        } else o["held"] = null;
    }

    void Save(Action? then = null)
    {
        ValueTask<StoreWrite> task;
        try { task = _h.Save(Edit); }
        catch (Exception ex) { Persisted(StoreWrite.Failed(Words(ex))); then?.Invoke(); return; }
        if (task.IsCompleted) {
            StoreWrite w;
            try { w = task.Result; } catch (Exception ex) { w = StoreWrite.Failed(Words(ex)); }
            Persisted(w);
            then?.Invoke();
        } else _ = SaveLater(task, then);
    }

    async Task SaveLater(ValueTask<StoreWrite> task, Action? then)
    {
        StoreWrite w;
        try { w = await task.ConfigureAwait(true); } catch (Exception ex) { w = StoreWrite.Failed(Words(ex)); }
        Persisted(w);
        then?.Invoke();
    }

    void Persisted(StoreWrite w)
    {
        if (w.Ok) {
            if (!_persistOk) {
                _persistOk = true;
                _persistReason = null;
                _h.Health(true, OkWords);
                _h.Log("the timer can be kept across a restart again");
                _h.Changed();
            }
            return;
        }
        string reason = string.IsNullOrWhiteSpace(w.Reason) ? "the store did not say why" : w.Reason!;
        if (_persistOk || _persistReason != reason) _h.Log($"could not save the timer ({reason}); it keeps running but will not survive a restart");
        bool changed = _persistOk || _persistReason != reason;
        _persistOk = false;
        _persistReason = reason;
        _h.Health(false, $"the timer cannot be kept across a restart: {reason}");
        if (changed) _h.Changed();
    }

    /* ---- the one timeout ---- */

    void Arm()
    {
        if (_stopped || _st.Kind != TimerKind.Timer || _st.Paused) { _monoFor = null; _h.Arm(null); return; }
        // a NEW running state takes its monotonic deadline from the wall clock once; an early re-arm of the same state
        // keeps it, so a clock set back cannot stretch the timer
        if (!ReferenceEquals(_monoFor, _st)) { _monoFor = _st; _monoEnd = _h.Mono + (_st.EndAt - _h.Now); }
        long delay = Math.Min(MaxDelay, Math.Max(0, Math.Min(_st.EndAt - _h.Now, _monoEnd - _h.Mono)));
        _h.Arm(delay);
    }

    /// <summary>The timeout fired, or the PC woke (<see cref="OnWake"/>): end the timer if its time is up, else re-arm.</summary>
    public void Check()
    {
        if (_stopped || _st.Kind != TimerKind.Timer || _st.Paused) { _h.Arm(null); return; }
        long t = _h.Now;
        bool byWall = t >= _st.EndAt, byMono = ReferenceEquals(_monoFor, _st) && _h.Mono >= _monoEnd;
        if (byWall || byMono) {
            if (!byWall) _h.Log("the PC clock was set back while the timer ran; it ended on time by Mantle's own count");
            long endAt = Math.Min(_st.EndAt, t);
            Finish(new TimerFinish(_st.Label, _st.TotalMs, endAt, false, Asleep: t - endAt > LateMs, Of: _st.Of));
        } else Arm();   // early (a capped delay, or the clock moved): wait for the rest
    }

    /// <summary>The PC woke from sleep: a running timer is checked at once (it fires once, with "while the PC was asleep").</summary>
    public void OnWake() => Check();

    /// <summary>A new wall-clock source (tests): a new monotonic deadline, as Electron's test hook does.</summary>
    public void ClockReplaced() { _monoFor = null; Arm(); }

    /* ---- at zero ---- */

    void Beep(string why)
    {
        Beeps++;
        if (_h.Test) { _h.Log($"would beep (muted in --test): {why}"); return; }
        try { _h.Beep(why); } catch (Exception ex) { _h.Log($"the beep failed ({Words(ex)})"); }
    }

    void Finish(TimerFinish f)
    {
        if (_st.Kind == TimerKind.Timer && !f.WhileOff && !f.WasHeld) _st = TimerState.Idle;
        Arm();
        CloseDone();
        _held = false;
        var d = new TimerDoneInfo(f with { Of = TimerCore.OfMs(f.Of) }) { During = f.WasHeld ? "a game ran" : null };
        _done = d;
        if (f.WasHeld) _h.Log($"a {TimerCore.LabelFor(f.TotalMs)} timer's pop-up waited for a game when Mantle restarted");
        else _h.Log($"done ({TimerCore.LabelFor(f.TotalMs)}){(f.WhileOff ? ", while Mantle was off" : f.Asleep ? ", late: the PC was asleep" : "")}");
        (bool known, string? game) = _h.Resting;
        if (known && !string.IsNullOrEmpty(game)) {
            _held = true;
            _heldGame = game;
            d.Popup = "held";
            d.Reason = $"the pop-up waits for the game to end ({game})";
            Save();   // idle AND the held notice, both on disk before anything else can happen
            if (!f.WasHeld) {
                Beep("the timer ended during a game");
                _h.Log($"done during a game ({game}): beeped once; the pop-up waits for the game to end");
            }
            _h.Changed();
            return;
        }
        Save(() => ShowNotice(d));   // idle on disk BEFORE the notice: a restart now cannot fire it a second time
    }

    /// <summary>The words of the notice for one finish (TIMER-062, TIMER-063, TIMER-064, TIMER-066).</summary>
    public static TimerDoneNotice NoticeFor(TimerDoneInfo d, TimeZoneInfo? zone = null)
    {
        TimerFinish f = d.Finish;
        string len = TimerCore.LabelFor(f.TotalMs);
        string label = f.Label.Length > 0 ? f.Label : len;
        string pre = f.Of != null ? "Snoozed " + len : f.Label.Length > 0 && f.Label != len ? len + " timer" : "";
        string sub = (pre.Length > 0 ? pre + " · ended at " : "Ended at ") + TimerCore.TimeOfDay(f.EndAt, zone);
        if (f.WhileOff) sub += ", while Mantle was off";
        else if (d.During != null) sub += ", while " + d.During;
        else if (f.Asleep) sub += ", while the PC was asleep";
        string again = TimerCore.LabelFor(f.Of ?? f.TotalMs);
        return new TimerDoneNotice("Timer done: " + label, sub, f.EndAt, new[] {
            new TimerButton(TimerDoneNotice.SnoozeOne, "+1 min"),
            new TimerButton(TimerDoneNotice.SnoozeFive, "+5 min"),
            new TimerButton(TimerDoneNotice.Again, "Restart", $"Run the {again} timer again"),
            new TimerButton(TimerDoneNotice.Dismiss, "Dismiss", Primary: true),
        });
    }

    void ShowNotice(TimerDoneInfo d)
    {
        if (_stopped || !ReferenceEquals(_done, d) || _held) return;
        TimerNoticeResult r;
        try { r = _h.ShowDone(NoticeFor(d)); } catch (Exception ex) { r = new TimerNoticeResult(false, Words(ex)); }
        if (r.Shown) { d.Popup = "shown"; d.Reason = null; }
        else Failed(d, r.Reason);
        _h.Changed();
    }

    void Failed(TimerDoneInfo d, string? reason)
    {
        d.Popup = "failed";
        d.Reason = string.IsNullOrWhiteSpace(reason) ? "the notice did not say why" : reason;
        _h.Log($"the done pop-up failed ({d.Reason}); beeped once, and the top bar says the timer is done");
        Beep("the done pop-up failed");
    }

    /// <summary>The notice failed after it was asked for (crash, could not be told): TIMER-076.</summary>
    public void NoticeFailed(long endAt, string reason)
    {
        if (_done is not { } d || d.Finish.EndAt != endAt || d.Popup == "failed") return;
        Failed(d, reason);
        _h.Changed();
    }

    void CloseDone()
    {
        try { _h.CloseDone(); } catch (Exception ex) { _h.Log($"could not take the done pop-up away ({Words(ex)})"); }
    }

    /// <summary>The top bar's game state changed: a held notice shows when the game ends ("while &lt;game&gt; ran").</summary>
    public void OnRestingChanged()
    {
        if (_stopped) return;
        (bool known, string? game) = _h.Resting;
        if (known && !string.IsNullOrEmpty(game)) { _h.Changed(); return; }
        if (_held && _done is { } d) {
            _held = false;
            string was = _heldGame ?? "a game";
            d.During = was + " ran";
            Save();   // the held notice leaves the disk: it is being shown now
            _h.Log($"the game ended ({was}); showing the done pop-up that waited");
            ShowNotice(d);
            return;
        }
        _h.Changed();
    }

    /* ---- commands ---- */

    TimerReply Begin(TimerState next, string what)
    {
        long t = _h.Now;
        if (_st.Kind != TimerKind.Idle && (_st.Kind != next.Kind || _st.Kind == TimerKind.Timer)) _h.Log($"{what} replaces the {Describe(_st, t)}");
        else if (_st.Kind == TimerKind.Stopwatch) _h.Log($"{what} restarts the {Describe(_st, t)}");
        else _h.Log($"{what} started");
        CloseDone();
        _done = null; _held = false; _restoreError = null;
        _st = next;
        Save(); Arm(); _h.Changed();
        return TimerReply.Done;
    }

    /// <summary><c>timer.start {text}</c> or <c>{ms}</c> [label] (TIMER-035). Also the Quick settings Focus seam.</summary>
    public TimerReply StartTimer(string? text, double? ms, string? label = null)
    {
        if (!_h.Enabled) return TimerReply.No(OffWords);
        TimerLength r = text != null ? TimerCore.Parse(text) : ms is { } m ? TimerCore.Check(m) : new TimerLength(false, 0, null, TimerCore.Junk);
        if (!r.Ok) return TimerReply.No(r.Reason!);
        string l = TimerCore.CleanLabel(label);
        _lastMs = r.Ms;
        return Begin(TimerCore.StartTimer(_h.Now, r.Ms, l.Length > 0 ? l : r.Label), $"a {r.Label} timer");
    }

    /// <summary><c>timer.stopwatch</c>: starts, or restarts from 0 ("Reset to 0:00"); replaces a timer (TIMER-036, TIMER-020).</summary>
    public TimerReply Stopwatch()
    {
        if (!_h.Enabled) return TimerReply.No(OffWords);
        return Begin(TimerCore.StartStopwatch(_h.Now), "a stopwatch");
    }

    public TimerReply Pause()
    {
        if (_st.Kind == TimerKind.Idle) return TimerReply.No(NoneRunning);
        if (_st.Paused) return TimerReply.No($"The {_st.KindWord} is already paused");
        _st = TimerCore.Pause(_st, _h.Now);
        _h.Log($"paused the {Describe(_st, _h.Now)}");
        Save(); Arm(); _h.Changed();
        return TimerReply.Done;
    }

    public TimerReply Resume()
    {
        if (!_h.Enabled) return TimerReply.No(OffWords);
        if (_st.Kind == TimerKind.Idle) return TimerReply.No(NoneRunning);
        if (!_st.Paused) return TimerReply.No($"The {_st.KindWord} is not paused");
        _st = TimerCore.Resume(_st, _h.Now);
        _h.Log($"resumed the {Describe(_st, _h.Now)}");
        Save(); Arm(); _h.Changed();
        return TimerReply.Done;
    }

    public TimerReply Cancel()
    {
        if (_st.Kind == TimerKind.Idle) return TimerReply.No(NoneRunning);
        _h.Log($"cancelled the {Describe(_st, _h.Now)}");
        _st = TimerState.Idle;
        Save(); Arm(); _h.Changed();
        return TimerReply.Done;
    }

    public TimerReply Add(double? ms)
    {
        if (!_h.Enabled) return TimerReply.No(OffWords);
        if (ms is not { } m || double.IsNaN(m) || double.IsInfinity(m) || m == 0) return TimerReply.No("Say how much time to add (ms)");
        if (_st.Kind != TimerKind.Timer) return TimerReply.No("Only a running or paused timer can take more time");
        long add = (long)Math.Clamp(m, -2.0 * TimerCore.MaxMs, 2.0 * TimerCore.MaxMs);
        long before = TimerCore.LeftMs(_st, _h.Now);
        _st = TimerCore.AddTime(_st, _h.Now, add);
        long after = TimerCore.LeftMs(_st, _h.Now);
        _h.Log($"{(m > 0 ? "added" : "took off")} {TimerCore.LabelFor(Math.Abs(after - before))}: {Describe(_st, _h.Now)}");
        Save(); Arm(); _h.Changed();
        return TimerReply.Done;
    }

    /// <summary>Dismiss, or Esc from any app (how = "Esc"): the notice closes, the finish is cleared, a held one leaves the disk.</summary>
    public bool Dismiss(string? how = null)
    {
        bool had = _done != null, wasHeld = _held;
        CloseDone();
        _done = null; _held = false;
        if (wasHeld) Save();
        if (had) _h.Log(how != null ? $"the done pop-up was dismissed ({how})" : "the done pop-up was dismissed");
        _h.Changed();
        return had;
    }

    /// <summary>endAt: null = an older caller (the current finish); NaN = a value that was not a number (always stale).</summary>
    TimerReply? StaleClick(double? endAt)
    {
        if (endAt is not { } e) return null;
        if (_done == null || double.IsNaN(e) || _done.Finish.EndAt != e) return TimerReply.No(Stale);
        return null;
    }

    /// <summary>Restart: the finished timer's ORIGINAL length again (not a snooze), same label (TIMER-066).</summary>
    public TimerReply Again(double? endAt = null)
    {
        if (!_h.Enabled) return TimerReply.No(OffWords);
        if (StaleClick(endAt) is { } stale) return stale;
        long? ms = _done != null ? (_done.Finish.Of ?? _done.Finish.TotalMs) : _lastMs;
        if (ms is not { } len || len <= 0) return TimerReply.No("There is no finished timer to run again");
        string label = _done != null && _done.Finish.Label.Length > 0 ? _done.Finish.Label : TimerCore.LabelFor(len);
        _lastMs = len;
        return Begin(TimerCore.StartTimer(_h.Now, len, label), $"again: a {TimerCore.LabelFor(len)} timer");
    }

    /// <summary>"+1 min" / "+5 min": a new timer of that length, same label, remembering the snoozed length (TIMER-065).</summary>
    public TimerReply Snooze(double? ms, double? endAt = null)
    {
        if (!_h.Enabled) return TimerReply.No(OffWords);
        if (_done == null) return TimerReply.No("There is no finished timer to snooze");
        if (StaleClick(endAt) is { } stale) return stale;
        if (ms is not { } m || Array.IndexOf(Snoozes, m == Math.Floor(m) && Math.Abs(m) <= TimerCore.MaxMs ? (long)m : -1L) < 0) return TimerReply.No("A snooze is 1 or 5 minutes");
        long of = _done.Finish.Of ?? _done.Finish.TotalMs;
        return Begin(TimerCore.StartTimer(_h.Now, (long)m, _done.Finish.Label, of), $"a {TimerCore.LabelFor((long)m)} snooze of the {TimerCore.LabelFor(of)} timer");
    }

    /// <summary><c>timer.chime {ok, muted, reason}</c>: a chime that could not play gets one beep instead (TIMER-075).</summary>
    public TimerReply Chime(bool ok, bool muted, string? reason)
    {
        if (_done == null) return TimerReply.No("There is no done pop-up");
        if (muted) { _done.Sound = "muted"; _done.SoundReason = string.IsNullOrWhiteSpace(reason) ? "muted" : reason; }
        else if (ok) { _done.Sound = "played"; _done.SoundReason = null; }
        else {
            _done.Sound = "failed";
            _done.SoundReason = string.IsNullOrWhiteSpace(reason) ? "the chime did not say why" : reason;
            _h.Log($"the chime did not play ({_done.SoundReason})");
            Beep("the chime did not play");
        }
        _h.Changed();
        return TimerReply.Done;
    }

    /// <summary>The tray switch: off cancels whatever runs, takes the notice away and saves idle (TIMER-054).</summary>
    public void OnEnabled(bool on)
    {
        if (!on) {
            if (_st.Kind != TimerKind.Idle) _h.Log($"switched off in the tray: cancelled the {Describe(_st, _h.Now)}");
            _st = TimerState.Idle;
            CloseDone();
            _done = null; _held = false;
            Save(); Arm();
        }
        _h.Changed();
    }

    /// <summary>The feature stops: no more timeouts, saves or notices (what is saved stays saved).</summary>
    public void Stop()
    {
        _stopped = true;
        _h.Arm(null);
    }

    /* ---- the view (timer.state) ---- */

    /// <summary><c>timer.state</c> in Electron's field names (TIMER-041).</summary>
    public JsonObject View()
    {
        long t = _h.Now;
        bool timer = _st.Kind == TimerKind.Timer, sw = _st.Kind == TimerKind.Stopwatch;
        (bool known, string? game) = _h.Resting;
        JsonNode? done = null;
        if (_done is { } d) {
            TimerFinish f = d.Finish;
            done = new JsonObject {
                ["label"] = f.Label, ["totalMs"] = f.TotalMs, ["endAt"] = f.EndAt, ["whileOff"] = f.WhileOff, ["asleep"] = f.Asleep,
                ["of"] = f.Of is { } of ? JsonValue.Create(of) : null, ["during"] = d.During, ["popup"] = d.Popup, ["reason"] = d.Reason,
                ["sound"] = d.Sound, ["soundReason"] = d.SoundReason,
            };
        }
        return new JsonObject {
            ["kind"] = _st.KindWord,
            ["paused"] = _st.Paused,
            ["endAt"] = timer && !_st.Paused ? JsonValue.Create(_st.EndAt) : null,
            ["leftMs"] = timer ? JsonValue.Create(TimerCore.LeftMs(_st, t)) : null,
            ["totalMs"] = timer ? JsonValue.Create(_st.TotalMs) : null,
            ["label"] = timer ? JsonValue.Create(_st.Label) : null,
            ["snoozeOf"] = timer && _st.Of is { } so ? JsonValue.Create(so) : null,
            ["startAt"] = sw && !_st.Paused ? JsonValue.Create(_st.StartAt) : null,
            ["elapsedMs"] = sw ? JsonValue.Create(TimerCore.ElapsedMs(_st, t)) : null,
            ["lastMs"] = _lastMs is { } lm ? JsonValue.Create(lm) : null,
            ["done"] = done,
            ["held"] = _held,
            ["resting"] = !known ? JsonValue.Create("unknown") : string.IsNullOrEmpty(game) ? JsonValue.Create(false) : JsonValue.Create(game),
            ["restingReason"] = known ? null : RestingUnknown,
            ["persist"] = new JsonObject { ["ok"] = _persistOk, ["reason"] = _persistReason },
            ["restoreError"] = _restoreError,
            ["on"] = _h.Enabled,
            ["test"] = _h.Test,
            ["now"] = t,
        };
    }
}
