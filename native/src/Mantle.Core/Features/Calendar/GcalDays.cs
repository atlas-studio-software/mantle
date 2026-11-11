using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// The pure half of the calendar's Google events in the bar (ui/bar/gcal-days.js, plus the words of ui/bar/gcal.js).
/// The "gcal.events" push { ver, spans, calendars:[{id,color,name}], events:[{k,cal,title,allDay,s,e}] } is turned into a
/// day index ONCE, when it arrives, so opening the flyout is a lookup per day and never a walk over the events.
/// All-day s/e are 'YYYY-MM-DD' (e exclusive); timed s/e are epoch ms, placed on the PC's local days (<see cref="Zone"/>).
/// </summary>
public sealed class GcalDays
{
    public const int MaxDays = 400;   // one event is never walked over more days than this

    public sealed record Row(string K, string Cal, string Title, bool AllDay, string? SDate, string? EDate, long SMs, long EMs);
    public sealed record Dot(string? Color, bool Many, int N);

    public int Ver { get; }
    public IReadOnlyDictionary<string, List<Row>> Days { get; }
    public IReadOnlyDictionary<string, string?> Colors { get; }
    public IReadOnlyDictionary<string, string> Names { get; }
    public IReadOnlyList<(long From, long To)> Spans { get; }
    /// The time zone the timed events were bucketed in (a Windows zone change re-buckets the same push on the next open).
    public string Zone { get; }
    readonly TimeZoneInfo _tz;

    GcalDays(int ver, Dictionary<string, List<Row>> days, Dictionary<string, string?> colors, Dictionary<string, string> names, List<(long, long)> spans, TimeZoneInfo tz)
    {
        Ver = ver; Days = days; Colors = colors; Names = names; Spans = spans; _tz = tz; Zone = ZoneOf(tz);
    }

    public static string ZoneOf(TimeZoneInfo tz) => tz.Id + "|" + tz.BaseUtcOffset.TotalMinutes.ToString(CultureInfo.InvariantCulture);

    /// gcal-days.js index(): events of an unticked calendar (not in calendars) are left out; each day's list is sorted.
    public static GcalDays Index(JsonNode? payload, TimeZoneInfo? tz = null)
    {
        var z = tz ?? TimeZoneInfo.Local;
        var p = payload as JsonObject ?? new JsonObject();
        var colors = new Dictionary<string, string?>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in p["calendars"] as JsonArray ?? new JsonArray())
            if (c is JsonObject co && GcalCore.Str(co["id"]) is { } id) { colors[id] = GcalCore.Str(co["color"]); names[id] = GcalCore.Str(co["name"]) ?? ""; }
        var days = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        foreach (var n in p["events"] as JsonArray ?? new JsonArray()) {
            if (RowOf(n) is not { } ev || !colors.ContainsKey(ev.Cal)) continue;
            foreach (var k in DaysOf(ev, z)) {
                if (!days.TryGetValue(k, out var l)) days[k] = l = new List<Row>();
                l.Add(ev);
            }
        }
        foreach (var l in days.Values) l.Sort(Cmp);
        var spans = new List<(long, long)>();
        foreach (var s in p["spans"] as JsonArray ?? new JsonArray())
            if (s is JsonObject so && GcalCore.Num(so["from"]) is long f && GcalCore.Num(so["to"]) is long t) spans.Add((f, t));
        return new GcalDays((int)(GcalCore.Num(p["ver"]) ?? 0), days, colors, names, spans, z);
    }

    public static Row? RowOf(JsonNode? n)
    {
        if (n is not JsonObject o || GcalCore.Str(o["k"]) is not { } k || GcalCore.Str(o["cal"]) is not { } cal) return null;
        string title = GcalCore.Str(o["title"]) ?? "(No title)";
        if (GcalCore.Bool(o["allDay"])) {
            if (GcalCore.Str(o["s"]) is not { } s || GcalCore.Str(o["e"]) is not { } e) return null;
            return new Row(k, cal, title, true, s, e, 0, 0);
        }
        if (GcalCore.Num(o["s"]) is not long sm) return null;
        return new Row(k, cal, title, false, null, null, sm, GcalCore.Num(o["e"]) ?? sm);
    }

    static int Cmp(Row a, Row b)
    {
        if (a.AllDay != b.AllDay) return a.AllDay ? -1 : 1;
        if (a.AllDay) { int c = string.CompareOrdinal(a.SDate, b.SDate); return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title) < 0 ? -1 : 1; }
        int d = a.SMs.CompareTo(b.SMs);
        return d != 0 ? d : string.CompareOrdinal(a.Title, b.Title);
    }

    public static string KeyOf(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateTime DateOf(string key) => DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static DateTime LocalDay(long ms, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), tz).Date;
    static DateTime LocalTime(long ms, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), tz).DateTime;

    /// The local days an event covers, as keys. A timed event ending exactly at midnight does not cover the next day;
    /// a zero-length one covers the day it starts.
    public static List<string> DaysOf(Row ev, TimeZoneInfo? tz = null)
    {
        var z = tz ?? TimeZoneInfo.Local;
        DateTime first, last;
        if (ev.AllDay) {
            first = DateOf(ev.SDate!);
            last = DateOf(ev.EDate!).AddDays(-1);
            if (last < first) last = first;
        } else {
            first = LocalDay(ev.SMs, z);
            last = LocalDay(Math.Max(ev.SMs, ev.EMs - 1), z);
        }
        var o = new List<string>();
        for (int i = 0; first <= last && i < MaxDays; first = first.AddDays(1), i++) o.Add(KeyOf(first));
        return o;
    }

    /// The dot under a day: the calendar's colour when every event that day is from one calendar, the neutral ink when
    /// several meet (or the colour is unknown). Null = no dot.
    public Dot? DotOf(string key)
    {
        if (!Days.TryGetValue(key, out var l) || l.Count == 0) return null;
        string cal = l[0].Cal;
        for (int i = 1; i < l.Count; i++) if (l[i].Cal != cal) return new Dot(null, true, l.Count);
        return new Dot(Colors.TryGetValue(cal, out var c) ? c : null, false, l.Count);
    }

    public IReadOnlyList<Row> On(string key) => Days.TryGetValue(key, out var l) ? l : Array.Empty<Row>();

    /// 12-hour, as the bar's clock reads: "7:00 AM".
    public static string Hm(DateTime d)
    {
        int h = d.Hour % 12; if (h == 0) h = 12;
        return h.ToString(CultureInfo.InvariantCulture) + ":" + d.Minute.ToString("00", CultureInfo.InvariantCulture) + (d.Hour < 12 ? " AM" : " PM");
    }

    /// What the row says on day <paramref name="key"/>: "All day", a start time, or for a timed event over several days
    /// "Until 6:00 AM" on its last day and "All day" between.
    public string TimeOf(Row ev, string key)
    {
        if (ev.AllDay) return "All day";
        string startKey = KeyOf(LocalDay(ev.SMs, _tz)), endKey = KeyOf(LocalDay(Math.Max(ev.SMs, ev.EMs - 1), _tz));
        if (key == startKey) return Hm(LocalTime(ev.SMs, _tz));
        if (key == endKey) return "Until " + Hm(LocalTime(ev.EMs, _tz));
        return "All day";
    }

    /// The tooltip's range in plain words (locale short dates: "Fri, Oct 16").
    public string RangeOf(Row ev)
    {
        if (ev.AllDay) {
            var s = DateOf(ev.SDate!); var last = DateOf(ev.EDate!).AddDays(-1);
            return KeyOf(last) == ev.SDate ? "All day" : "All day, " + Words.Short(s) + " to " + Words.Short(last);
        }
        DateTime a = LocalTime(ev.SMs, _tz), b = LocalTime(ev.EMs, _tz);
        if (KeyOf(a.Date) == KeyOf(LocalDay(Math.Max(ev.SMs, ev.EMs - 1), _tz))) return Hm(a) + " to " + Hm(b);
        return Words.Short(a) + " " + Hm(a) + " to " + Words.Short(b) + " " + Hm(b);
    }

    /// Does the cache cover the whole of this month (0-11)?
    public bool Covers(int year, int month)
    {
        var first = new DateTime(year, 1, 1).AddMonths(month);
        long from = GcalCore.LocalMs(first, _tz), to = GcalCore.LocalMs(first.AddMonths(1), _tz);
        return Spans.Any(s => s.From <= from && s.To >= to);
    }

    /// Is the day inside a cached span ("No events" vs "Not loaded yet")?
    public bool CoversDay(string key)
    {
        long t = GcalCore.LocalMs(DateOf(key), _tz);
        return Spans.Any(s => s.From <= t && s.To > t);
    }

    /// The agenda's heading: "Today", "Tomorrow", "Yesterday", else the locale "Weekday, Month D".
    public static string DayTitle(string key, DateTime today)
    {
        if (key == KeyOf(today)) return "Today";
        if (key == KeyOf(today.AddDays(1))) return "Tomorrow";
        if (key == KeyOf(today.AddDays(-1))) return "Yesterday";
        return Words.WeekdayMonthDay(DateOf(key));
    }

    /// <summary>The quiet line under the list (gcal.js statusLine): only when something is not right, and always with why.
    /// <paramref name="asked"/> is this open's state for the shown month: null, "asking", "loading", "done" or "no:&lt;why&gt;".</summary>
    public static string StatusLine(JsonObject s, string? openNote, string? asked, bool monthCovered, DateTime shown, long nowMs)
    {
        if (!string.IsNullOrEmpty(openNote)) return openNote;
        var parts = new List<string>();
        string? reason = GcalCore.Str((s["error"] as JsonObject)?["reason"]);
        long syncedAt = GcalCore.Num(s["syncedAt"]) ?? 0;
        if (GcalCore.Bool(s["needsSignIn"])) parts.Add(!string.IsNullOrEmpty(reason) ? reason : "Google ended Mantle's sign-in; sign in again in Settings");
        else if (!string.IsNullOrEmpty(reason)) parts.Add(reason);
        else if (GcalCore.Bool(s["syncing"]) && syncedAt == 0) return "Syncing with Google…";
        string mname = shown.ToString("MMMM", CultureInfo.CurrentCulture);
        if (parts.Count == 0 && !monthCovered && asked is "loading" or "asking") return "Loading " + mname + " from Google…";
        if (!monthCovered && asked != null && asked.StartsWith("no:", StringComparison.Ordinal)) parts.Add(mname + " is not on this PC yet (" + asked[3..] + ")");
        if (parts.Count == 0) return "";
        return (syncedAt != 0 ? "Last synced " + GcalCore.Ago(syncedAt, nowMs) : "Not synced yet") + " · " + string.Join("; ", parts);
    }

    /// The machine-locale date words Chromium's toLocaleDateString gives, from the culture's own patterns.
    public static class Words
    {
        static DateTimeFormatInfo F => CultureInfo.CurrentCulture.DateTimeFormat;
        /// {weekday:'long', month:'long', day:'numeric', year:'numeric'}: "Wednesday, November 11, 2026".
        public static string Long(DateTime d) => d.ToString(F.LongDatePattern, CultureInfo.CurrentCulture);
        /// {weekday:'long', month:'long', day:'numeric'}: "Wednesday, November 11".
        public static string WeekdayMonthDay(DateTime d) => d.ToString("dddd, " + F.MonthDayPattern, CultureInfo.CurrentCulture);
        /// {weekday:'short', month:'short', day:'numeric'}: "Fri, Oct 16".
        public static string Short(DateTime d) => d.ToString("ddd, " + F.MonthDayPattern.Replace("MMMM", "MMM"), CultureInfo.CurrentCulture);
        /// {month:'long', year:'numeric'}: "November 2026".
        public static string MonthYear(DateTime d) => d.ToString(F.YearMonthPattern, CultureInfo.CurrentCulture);
        /// {weekday:'short'}.slice(0, 2): "Su".
        public static string Dow(DayOfWeek w) { string s = F.GetAbbreviatedDayName(w); return s.Length > 2 ? s[..2] : s; }
    }
}
