using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The namespace is Timers, not Timer: a Mantle.Core.Features.Timer namespace would hide System.Threading.Timer from
// every feature next to it (SnapFeature's `Timer? _probeTimer` would name the namespace).
namespace Mantle.Core.Features.Timers;

// lane r10-timer: port of lib/features/_timer-core.js, the timer and stopwatch's RULES. Pure: no clock of its own
// (every call takes `now`, wall-clock ms since 1970), no timers, no files. The saved shape (features\timer.json key
// "clock") is Electron's exactly, so a rollback to Electron and back keeps a running timer:
//   idle              { kind:'idle' }
//   timer running     { kind:'timer', endAt, totalMs, label, startedAt[, of] }
//   timer paused      { kind:'timer', paused:true, leftMs, totalMs, label, startedAt[, of] }
//   stopwatch running { kind:'stopwatch', startAt }            (startAt = now - elapsed)
//   stopwatch paused  { kind:'stopwatch', paused:true, elapsedMs }
// A running timer keeps its END (a wall-clock instant), never a countdown, so a sleep or a restart cannot stretch it.
// Electron has no laps, alarms or pomodoro mode (apps.md note); neither does this.

public enum TimerKind { Idle, Timer, Stopwatch }

/// <summary>One state. Only the fields of its shape mean anything (see the header).</summary>
public sealed record TimerState(TimerKind Kind, bool Paused = false, long EndAt = 0, long LeftMs = 0, long TotalMs = 0,
    string Label = "", long? StartedAt = null, long StartAt = 0, long ElapsedMs = 0, long? Of = null)
{
    public static readonly TimerState Idle = new(TimerKind.Idle);
    public bool Running => (Kind == TimerKind.Timer || Kind == TimerKind.Stopwatch) && !Paused;
    public string KindWord => Kind == TimerKind.Timer ? "timer" : Kind == TimerKind.Stopwatch ? "stopwatch" : "idle";
}

/// <summary>A parse or limit check: ok with whole-second ms and its automatic label, or the reason in words.</summary>
public readonly record struct TimerLength(bool Ok, long Ms, string? Label, string? Reason);

/// <summary>A saved state back after a restart: the state, a timer that ended while Mantle was off, or why it was not usable.</summary>
public sealed record TimerRestore(TimerState State, TimerFinish? Fired, string? Reason);

/// <summary>A finish: what ended and when. WhileOff = it ended while Mantle was off; Asleep = fired over 60 s late.</summary>
public sealed record TimerFinish(string Label, long TotalMs, long EndAt, bool WhileOff, bool Asleep = false, long? Of = null, bool WasHeld = false);

public static class TimerCore
{
    public const long Sec = 1000, Min = 60 * Sec, Hour = 60 * Min;
    public const long MaxMs = 24 * Hour;
    public const long MinMs = Sec;
    public const long StaleMs = 7 * 24 * Hour;    // a saved timer that ended longer ago than this is not "done" news
    public const long SwMax = 366 * 24 * Hour;    // a stopwatch past a year is a broken clock or a hand-edit
    public const string Junk = "Type minutes (12) or hours:minutes (1:30)";
    public const string Zero = "A timer needs at least 1 second";
    public const string Long = "A timer can run up to 24 hours";

    static readonly Regex HMm = new(@"^([0-9]{1,3}):([0-9]{1,2})\z", RegexOptions.CultureInvariant);
    static readonly Regex Bare = new(@"^([0-9]+(\.[0-9]*)?|\.[0-9]+)\z", RegexOptions.CultureInvariant);
    static readonly Regex Part = new(@"\G([0-9]+(?:\.[0-9]*)?|\.[0-9]+)\s*([a-z]*)\s*,?\s*", RegexOptions.CultureInvariant);
    static readonly Regex Bad = new(@"[\x00-\x1f\x7f-\x9f  ‎‏‪-‮⁦-⁩﻿]", RegexOptions.CultureInvariant);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    static long Unit(string u) => u switch {
        "h" or "hr" or "hrs" or "hour" or "hours" => Hour,
        "m" or "min" or "mins" or "minute" or "minutes" => Min,
        "s" or "sec" or "secs" or "second" or "seconds" => Sec,
        _ => 0,
    };

    static bool Fin(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    static double Num(string s) => double.Parse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    static long RoundSec(double ms) => (long)Math.Floor(ms / Sec + 0.5) * Sec;   // JS Math.round: halves go up

    /// <summary>A whole-second duration checked against the limits: the one exit of <see cref="Parse"/>.</summary>
    public static TimerLength Check(double ms)
    {
        if (!Fin(ms) || ms < 0) return new(false, 0, null, Junk);
        if (ms > MaxMs + Hour) return new(false, 0, null, Long);   // before the cast: a huge number stays a number
        long r = RoundSec(ms);
        if (r < MinMs) return new(false, 0, null, Zero);
        if (r > MaxMs) return new(false, 0, null, Long);
        return new(true, r, LabelFor(r), null);
    }

    /// <summary>"12" = 12 min, "2.5" = 2 min 30 s, "1:30" = 1 h 30 min (minutes 0-59), and unit forms "45s", "10 min",
    /// "1h 30m", "1h30" (a bare last number after hours = minutes), "2m30" (after minutes = seconds).</summary>
    public static TimerLength Parse(string? text)
    {
        string s = (text ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return new(false, 0, null, Junk);
        Match m = HMm.Match(s);
        if (m.Success) {
            double mins = Num(m.Groups[2].Value);
            if (mins > 59) return new(false, 0, null, Junk);
            return Check(Num(m.Groups[1].Value) * Hour + mins * Min);
        }
        if (Bare.IsMatch(s)) return Check(Num(s) * Min);
        int at = 0, parts = 0;
        double total = 0;
        long last = long.MaxValue;
        while (at < s.Length) {
            Match r = Part.Match(s, at);
            if (!r.Success || r.Index != at || r.Length == 0) return new(false, 0, null, Junk);
            at = r.Index + r.Length;
            long unit;
            if (r.Groups[2].Length > 0) {
                unit = Unit(r.Groups[2].Value);
                if (unit == 0) return new(false, 0, null, Junk);
            } else {
                if (at < s.Length) return new(false, 0, null, Junk);   // only the LAST number may go bare
                unit = last == Hour ? Min : last == Min ? Sec : 0;
                if (unit == 0) return new(false, 0, null, Junk);
            }
            if (unit >= last) return new(false, 0, null, Junk);        // biggest unit first, each once
            last = unit;
            total += Num(r.Groups[1].Value) * unit;
            parts++;
        }
        if (parts == 0) return new(false, 0, null, Junk);
        return Check(total);
    }

    /// <summary>"25 min", "1 h 30 min", "45 s", "2 min 30 s", "1 h".</summary>
    public static string LabelFor(long ms)
    {
        long t = Math.Max(0, (long)Math.Floor(ms / (double)Sec + 0.5));
        long h = t / 3600, mi = t % 3600 / 60, se = t % 60;
        var sb = new StringBuilder();
        if (h != 0) sb.Append(h).Append(" h");
        if (mi != 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(mi).Append(" min"); }
        if (se != 0 || sb.Length == 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(se).Append(" s"); }
        return sb.ToString();
    }

    /// <summary>Countdown (up false): CEIL to whole seconds, so "0:00" shows only at the end. Stopwatch (up true): FLOOR.
    /// "m:ss" under an hour, else "h:mm:ss".</summary>
    public static string FormatClock(long ms, bool up = false)
    {
        long v = ms > 0 ? ms : 0;
        long t = up ? v / Sec : (v + Sec - 1) / Sec;
        long h = t / 3600, mi = t % 3600 / 60, se = t % 60;
        return h != 0
            ? h.ToString(CultureInfo.InvariantCulture) + ":" + mi.ToString("00", CultureInfo.InvariantCulture) + ":" + se.ToString("00", CultureInfo.InvariantCulture)
            : mi.ToString(CultureInfo.InvariantCulture) + ":" + se.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>"9:14 PM" in <paramref name="zone"/> (default: the PC's own), the bar clock's shape without seconds.</summary>
    public static string TimeOfDay(long ms, TimeZoneInfo? zone = null)
    {
        DateTimeOffset d = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, -62135596800000L, 253402300799999L)), zone ?? TimeZoneInfo.Local);
        int h = d.Hour;
        string mer = h < 12 ? "AM" : "PM";
        h %= 12;
        if (h == 0) h = 12;
        return h.ToString(CultureInfo.InvariantCulture) + ":" + d.Minute.ToString("00", CultureInfo.InvariantCulture) + " " + mer;
    }

    /// <summary>A label as it may be kept, drawn and saved: one line, at most 60 UTF-16 units; control characters,
    /// line/paragraph separators and bidi marks become spaces. "" = none. DEVIATION: a cut never leaves half a
    /// surrogate pair (Electron's slice could; the JSON writer here refuses one).</summary>
    public static string CleanLabel(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length > 400) s = s.Substring(0, 400);
        s = Spaces.Replace(Bad.Replace(s, " "), " ").Trim();
        if (s.Length > 60) s = s.Substring(0, char.IsHighSurrogate(s[59]) ? 59 : 60);
        return s;
    }

    /// <summary>A usable snoozed-from length (1 s .. 24 h) or null.</summary>
    public static long? OfMs(long? v) => v is { } x && x >= MinMs && x <= MaxMs ? x : null;

    public static TimerState StartTimer(long now, long ms, string? label, long? of = null)
    {
        string l = CleanLabel(label);
        return new TimerState(TimerKind.Timer, EndAt: now + ms, TotalMs: ms, Label: l.Length > 0 ? l : LabelFor(ms), StartedAt: now, Of: OfMs(of));
    }

    public static TimerState StartStopwatch(long now) => new(TimerKind.Stopwatch, StartAt: now);

    public static long LeftMs(TimerState st, long now) =>
        st.Kind != TimerKind.Timer ? 0 : Math.Max(0, st.Paused ? st.LeftMs : st.EndAt - now);

    public static long ElapsedMs(TimerState st, long now) => st.Kind switch {
        TimerKind.Stopwatch => Math.Max(0, st.Paused ? st.ElapsedMs : now - st.StartAt),
        TimerKind.Timer => Math.Max(0, st.TotalMs - LeftMs(st, now)),
        _ => 0,
    };

    /// <summary>A paused copy; idle or already paused comes back as it was.</summary>
    public static TimerState Pause(TimerState st, long now)
    {
        if (!st.Running) return st;
        if (st.Kind == TimerKind.Timer)
            return new TimerState(TimerKind.Timer, Paused: true, LeftMs: LeftMs(st, now), TotalMs: st.TotalMs, Label: st.Label, StartedAt: st.StartedAt, Of: st.Of);
        return new TimerState(TimerKind.Stopwatch, Paused: true, ElapsedMs: ElapsedMs(st, now));
    }

    /// <summary>A paused timer keeps its time left however long it was paused; a stopwatch carries on from its count.</summary>
    public static TimerState Resume(TimerState st, long now)
    {
        if (!st.Paused) return st;
        if (st.Kind == TimerKind.Timer)
            return new TimerState(TimerKind.Timer, EndAt: now + st.LeftMs, TotalMs: st.TotalMs, Label: st.Label, StartedAt: st.StartedAt, Of: st.Of);
        if (st.Kind == TimerKind.Stopwatch) return new TimerState(TimerKind.Stopwatch, StartAt: now - st.ElapsedMs);
        return st;
    }

    /// <summary>+ms (or -ms) on a timer, running or paused; what is left stays within 0..24 h. An automatic label follows
    /// the new length ("25 min" + 5 min = "30 min"); a custom one stays.</summary>
    public static TimerState AddTime(TimerState st, long now, long ms)
    {
        if (st.Kind != TimerKind.Timer) return st;
        long left = LeftMs(st, now);
        long next = Math.Min(MaxMs, Math.Max(0, left + ms));
        long total = Math.Max(next, st.TotalMs + (next - left));
        string label = st.Label == LabelFor(st.TotalMs) ? LabelFor(total) : st.Label;
        if (st.Paused) return st with { LeftMs = next, TotalMs = total, Label = label };
        return st with { EndAt = now + next, TotalMs = total, Label = label };
    }

    /// <summary>ms until the shown second changes; null when nothing ticks (idle, paused, a timer at 0).</summary>
    public static long? NextTickMs(TimerState st, long now)
    {
        if (!st.Running) return null;
        if (st.Kind == TimerKind.Timer) {
            long left = st.EndAt - now;
            if (left <= 0) return null;
            return left - ((left + Sec - 1) / Sec - 1) * Sec;
        }
        long e = Math.Max(0, now - st.StartAt);
        return Sec - e % Sec;
    }

    /// <summary>The bar's redraw delay (TIMER-006): the next second change + 15 ms, at least 20 ms; null = no tick.</summary>
    public static long? BarRedrawMs(TimerState st, long now) => NextTickMs(st, now) is { } t ? Math.Max(20, t + 15) : null;

    /// <summary>The "clock" key's JSON, Electron's shape exactly.</summary>
    public static JsonObject ToJson(TimerState st)
    {
        var o = new JsonObject();
        switch (st.Kind) {
            case TimerKind.Timer:
                o["kind"] = "timer";
                if (st.Paused) { o["paused"] = true; o["leftMs"] = st.LeftMs; } else o["endAt"] = st.EndAt;
                o["totalMs"] = st.TotalMs;
                o["label"] = st.Label;
                o["startedAt"] = st.StartedAt is { } sa ? JsonValue.Create(sa) : null;
                if (OfMs(st.Of) is { } of) o["of"] = of;
                break;
            case TimerKind.Stopwatch:
                o["kind"] = "stopwatch";
                if (st.Paused) { o["paused"] = true; o["elapsedMs"] = st.ElapsedMs; } else o["startAt"] = st.StartAt;
                break;
            default:
                o["kind"] = "idle";
                break;
        }
        return o;
    }

    static bool Get(JsonElement o, string key, out double v)
    {
        v = 0;
        if (!o.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out v)) return false;
        return Fin(v);
    }

    static bool Truthy(JsonElement o, string key) =>
        o.TryGetProperty(key, out var p) && p.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.Number => p.TryGetDouble(out double d) && d != 0 && !double.IsNaN(d),
            JsonValueKind.String => p.GetString()!.Length > 0,
            JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };

    static long? OfFrom(JsonElement o) => Get(o, "of", out double v) && v >= MinMs && v <= MaxMs ? (long)v : null;

    /// <summary>A saved state back after a restart (TIMER-050..052). Nothing saved = idle; junk = idle with the reason;
    /// a timer that ENDED = idle plus Fired (the caller saves idle at once, so a second restore cannot fire again);
    /// one still to run keeps running, a paused one stays paused, a stopwatch carries on.</summary>
    public static TimerRestore Restore(JsonElement? saved, long now)
    {
        static TimerRestore BadR(string why) => new(TimerState.Idle, null, "the saved timer could not be read (" + why + ")");
        if (saved is not { } s || s.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(TimerState.Idle, null, null);
        if (s.ValueKind != JsonValueKind.Object) return BadR("not a timer");
        string? kind = s.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (kind == "idle") return new(TimerState.Idle, null, null);
        if (kind == "timer") {
            if (!Get(s, "totalMs", out double total) || total <= 0 || total > MaxMs + Hour) return BadR("its length is missing");
            long totalMs = (long)total;
            string label = CleanLabel(s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null);
            if (label.Length == 0) label = LabelFor(totalMs);
            long? startedAt = Get(s, "startedAt", out double sa) && Math.Abs(sa) < 1e15 ? (long)sa : null;
            long? of = OfFrom(s);
            if (Truthy(s, "paused")) {
                if (!Get(s, "leftMs", out double left) || left < 0 || left > MaxMs) return BadR("its time left is missing");
                return new(new TimerState(TimerKind.Timer, Paused: true, LeftMs: (long)left, TotalMs: totalMs, Label: label, StartedAt: startedAt, Of: of), null, null);
            }
            if (!Get(s, "endAt", out double end)) return BadR("its end time is missing");
            // an end at 0, negative or weeks back is a corrupt file or a clock that jumped: "done" news of nothing
            if (now - end > StaleMs) return new(TimerState.Idle, null, "the saved timer ended more than 7 days ago (or its end time is wrong), so it was not shown as done");
            if (end <= now) return new(TimerState.Idle, new TimerFinish(label, totalMs, (long)end, WhileOff: true, Of: of), null);
            if (end - now > MaxMs + Hour) return BadR("it ends more than 24 hours from now; the clock may have changed");
            return new(new TimerState(TimerKind.Timer, EndAt: (long)end, TotalMs: totalMs, Label: label, StartedAt: startedAt, Of: of), null, null);
        }
        if (kind == "stopwatch") {
            if (Truthy(s, "paused")) {
                if (!Get(s, "elapsedMs", out double el) || el < 0) return BadR("its time is missing");
                if (el > SwMax) return BadR("its time is more than a year");
                return new(new TimerState(TimerKind.Stopwatch, Paused: true, ElapsedMs: (long)el), null, null);
            }
            if (!Get(s, "startAt", out double st)) return BadR("its start time is missing");
            if (now - st > SwMax) return BadR("it started more than a year ago; the clock may have changed");
            return new(new TimerState(TimerKind.Stopwatch, StartAt: (long)Math.Min(st, now)), null, null);
        }
        string shown = JsonSerializer.Serialize(kind ?? (s.TryGetProperty("kind", out var k2) ? k2.ToString() : "undefined"), OptionJson.Default.String);
        return BadR("unknown kind " + (shown.Length > 40 ? shown.Substring(0, 40) : shown));
    }
}
