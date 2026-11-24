using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Mantle.Core.Features.Switcher;

/// <summary>One window of an Alt+Tab (lib/switcher.js normItem): the handle as digits, never shown or logged.</summary>
public sealed record SwitcherItem(string Hwnd, string Title, int Pid, bool Minimized, bool Hung);

/// <summary>What registering the previews gave (lib/thumbs.js sync): how many, or why not.</summary>
public sealed record ThumbResult(bool Ok, string? Reason, int Count);

/// <summary>
/// The overlay window as the session controller needs it: the seam a surface (Surfaces/Switcher) implements, so
/// every rule of lib/switcher.js's main-process half is tested without a window. Every member is called on the
/// host thread and must not throw (a failure is words).
/// </summary>
public interface ISwitcherOverlay
{
    /// The window exists and its page can be talked to (Electron's alive()).
    bool Alive { get; }

    /// <summary>One SetWindowPos (lib/switcher.js place): <paramref name="w"/> or <paramref name="h"/> 0 keeps the size;
    /// <paramref name="show"/> puts it at the top of the topmost band and shows it, never activating it. Null when it
    /// went, else plain words.</summary>
    string? Place(int x, int y, int w, int h, bool show);

    /// The page's message (<c>switcher.session</c>, <c>switcher.move</c>, <c>switcher.windows</c>, <c>switcher.end</c>, <c>switcher.thumbs</c>).
    void Send(string name, object payload);

    /// Register the live previews at these boxes (device px = box x dpr, offset by the window's origin).
    ThumbResult SyncThumbs(IReadOnlyList<PreviewBox> boxes, double dpr, int originX, int originY);

    /// Unregister every preview (always FIRST when a session ends: lib/thumbs.js THE LEAK).
    void ClearThumbs(string why);

    /// The monitor's scale (1 at 96 DPI); the primary's when the monitor is unknown.
    double ScaleFor(PhysRect mon);

    /// Every display, physical px.
    IReadOnlyList<PhysRect> Displays { get; }

    /// The primary monitor and its work area, physical px.
    PhysRect PrimaryMon { get; }
    PhysRect PrimaryWork { get; }
}

/// <summary>The session as main sees it (lib/switcher.js onOpen's sess).</summary>
public sealed class SwitcherSession
{
    internal SwitcherSession() { }
    public long Seq { get; internal set; }
    public double KeyAt { get; internal set; }
    public double KeyMs { get; internal set; }
    public bool Tracking { get; internal set; }
    public double Recv { get; internal set; }
    public IReadOnlyList<SwitcherItem> List { get; internal set; } = Array.Empty<SwitcherItem>();
    public int Sel { get; internal set; }
    public SwitcherGeometry Lay { get; internal set; } = SwitcherGeometry.Empty;
    public double Scale { get; internal set; }
    public PhysRect Mon { get; internal set; }
    public PhysRect Work { get; internal set; }
    /// The window's size in DIP: starts as the panel's; a list change can grow it, never shrink it.
    public double WinW { get; internal set; }
    public double WinH { get; internal set; }
    /// Where it comes on screen (centred in the work area), physical px.
    public PhysRect Rect { get; internal set; }
    public ParkPlace Park { get; internal set; }
    public string? Rehearsal { get; internal set; }
    /// Why nothing is drawn for it (no window, page not ready, test mode, could not be sized); the sidecar still switches.
    public string? Undrawn { get; internal set; }
    public bool Resized { get; internal set; }
    public double? Painted { get; internal set; }
    public double DueAt { get; internal set; }
    public bool Due { get; internal set; }
    public double? Shown { get; internal set; }
    public double? Ended { get; internal set; }
    public double? CommitAt { get; internal set; }
    public string? CommitVia { get; internal set; }
    public bool PaintLateLogged { get; internal set; }
    public ThumbResult? Thumbs { get; internal set; }
    public int Reflows { get; internal set; }
    internal double? AltUpSince;
    internal double NextAltPoll;
}

/// <summary>
/// lib/switcher.js's session half, pure: onOpen, sizeParked, presize, onReflow, switcher.painted, maybeReveal, the
/// stuck-overlay guard, endVisual, onCommit/onCancel/onActivated, move and pick's index mapping. It only DRAWS:
/// the sidecar owns every session (keys, selection, the switch itself), so nothing here can hold, delay or
/// swallow a key, and a slow, hung or missing overlay never stops Alt-up from switching. No timers of its own:
/// the owner calls <see cref="Tick"/> (every 40 ms is plenty) with the QueryPerformanceCounter clock in ms, the
/// same clock the sidecar's <c>keyAt</c> is on. Not thread-safe: the host thread only.
/// </summary>
public sealed class SwitcherSessions
{
    public const int ShowDelayMs = 100, PaintLateMs = 1500, AltPollMs = 200, AltUpGraceMs = 600, PresizeMs = 500;
    public const string WindowTitle = "Mantle switcher";

    readonly ISwitcherOverlay _w;
    readonly Action<string> _log;
    readonly Action<long, int> _grid;
    readonly bool _test;
    string? _monKey;

    /// <param name="grid">Send the sidecar the column count (<c>hotkey {op:'grid', seq, cols}</c>, 1 s) so the arrows move by rows.</param>
    public SwitcherSessions(ISwitcherOverlay overlay, bool test, Action<long, int> grid, Action<string>? log = null)
    {
        _w = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _test = test;
        _log = log ?? (_ => { });
    }

    /// The Alt+Tab on screen or being prepared.
    public SwitcherSession? Current { get; private set; }
    /// The session whose switch is under way, until <c>activated</c>.
    public SwitcherSession? Acting { get; private set; }
    /// Where the window was last put, physical px (null before any place went).
    public (int X, int Y)? At { get; private set; }
    /// The window's size as last set here, physical px.
    public (int W, int H)? Size { get; private set; }
    /// The monitor and work area of the last Alt+Tab: where the next one most likely opens.
    public (PhysRect Mon, PhysRect Work)? Target { get; private set; }
    /// The page has drawn something since it loaded (R7: the next open is warm).
    public bool PageDrew { get; set; }
    /// The last presize failure (logged once per new reason).
    public string? PresizeErr { get; private set; }
    /// A notice is up: presize leaves the window alone.
    public bool NoticeUp { get; set; }

    string Log(string line) { _log(line); return line; }

    string? PlaceTracked(int x, int y, int w, int h, bool show)
    {
        if (!_w.Alive) return "there is no switcher window";
        var how = _w.Place(x, y, w, h, show);
        if (how is null)
        {
            At = (x, y);
            if (w > 0 && h > 0) Size = (w, h);
        }
        return how;
    }

    /// <summary>sizeParked: beside a different monitor from last time Windows DPI-resizes the window inside the
    /// first SetWindowPos; it is on the new DPI then, so the same call again sticks (SWI-044).</summary>
    public string? SizeParked(PhysRect mon, ParkPlace park, int w, int h)
    {
        var how = PlaceTracked(park.X, park.Y, w, h, false);
        string key = $"{mon.X},{mon.Y},{mon.W},{mon.H}";
        if (how is null && _monKey != key)
        {
            how = PlaceTracked(park.X, park.Y, w, h, false);
            _monKey = key;
        }
        return how;
    }

    static double Num(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    static int? Int(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static bool Bool(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>normItem: hwnd as a string (a number or a string on the wire), title or "", pid, the two flags.</summary>
    public static SwitcherItem Item(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object) return new SwitcherItem("undefined", "", 0, false, false);
        string hwnd = !it.TryGetProperty("hwnd", out var h) ? "undefined"
            : h.ValueKind == JsonValueKind.String ? h.GetString() ?? ""
            : h.ValueKind == JsonValueKind.Number ? h.GetRawText()
            : h.ValueKind == JsonValueKind.Null ? "null" : h.GetRawText();
        string title = it.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        int pid = it.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pi) ? pi : 0;
        return new SwitcherItem(hwnd, title, pid, Bool(it, "minimized"), Bool(it, "hung"));
    }

    public static IReadOnlyList<SwitcherItem> Items(JsonElement m)
    {
        var list = new List<SwitcherItem>();
        if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var it in l.EnumerateArray()) list.Add(Item(it));
        return list;
    }

    /// rectOf: <c>{x,y,w,h}</c> with a positive size, else null.
    public static PhysRect? Rect(JsonElement m, string k)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty(k, out var r) || r.ValueKind != JsonValueKind.Object) return null;
        double x = Num(r, "x"), y = Num(r, "y"), w = Num(r, "w"), h = Num(r, "h");
        if (!(w > 0 && h > 0)) return null;
        return new PhysRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
    }

    /// <summary>
    /// The sidecar's <c>open</c> (SWI-045, SWI-066): push aside any session still up, lay out in DIP from the work area over
    /// the monitor's scale, size the PARKED window, hand the page the session and the sidecar the column count, and
    /// set when it may come on screen (100 ms after the key, or at paint if later). Nothing to draw with (no window,
    /// page not ready, a test run): the session is kept undrawn and the sidecar still switches on Alt-up.
    /// </summary>
    public SwitcherSession Open(JsonElement m, double now, bool pageReady, string? pageReason, string? rehearsal = null)
    {
        if (Current is { } old) End(old, rehearsal is not null ? $"the {rehearsal} started" : "a new Alt+Tab started before the last one ended", now);
        var list = Items(m);
        var mon = Rect(m, "mon") ?? _w.PrimaryMon;
        var work = Rect(m, "work") ?? mon;
        double scale = _w.ScaleFor(mon);
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        var lay = SwitcherLayout.Layout(list.Count, work.W / scale, work.H / scale);
        int w = (int)Math.Round(lay.W * scale), h = (int)Math.Round(lay.H * scale);
        var s = new SwitcherSession
        {
            Seq = (long)Num(m, "seq"),
            KeyAt = Num(m, "keyAt"),
            KeyMs = Num(m, "ms"),
            Tracking = !(m.ValueKind == JsonValueKind.Object && m.TryGetProperty("tracking", out var tr) && tr.ValueKind == JsonValueKind.False),
            Recv = now,
            List = list,
            Sel = Int(m, "sel") ?? -1,
            Lay = lay,
            Scale = scale,
            Mon = mon,
            Work = work,
            WinW = lay.W,
            WinH = lay.H,
            Rect = new PhysRect((int)Math.Round(work.X + (work.W - w) / 2.0), (int)Math.Round(work.Y + (work.H - h) / 2.0), w, h),
            Park = SwitcherWords.ParkSpot(mon, w, h, _w.Displays),
            Rehearsal = rehearsal,
        };
        double since = s.KeyAt > 0 ? now - s.KeyAt : s.KeyMs;
        s.DueAt = now + Math.Max(0, ShowDelayMs - since);
        Current = s;
        if (!_w.Alive || !pageReady || (_test && rehearsal is null))
        {
            s.Undrawn = !_w.Alive ? "there is no switcher window" : string.IsNullOrEmpty(pageReason) ? "test mode" : pageReason;
            return s;
        }
        s.Resized = Size is not { } sz || sz.W != w || sz.H != h;
        var how = SizeParked(mon, s.Park, w, h);
        if (how is not null) s.Undrawn = $"the switcher window could not be sized ({how})";
        Target = (mon, work);
        _w.Send("switcher.session", new
        {
            seq = s.Seq, sel = s.Sel, current = Int(m, "current") ?? -1, backwards = Bool(m, "backwards"),
            tracking = s.Tracking, list, lay, win = new { w = s.WinW, h = s.WinH },
        });
        if (lay.Cols > 0 && rehearsal is null) _grid(s.Seq, lay.Cols);
        return s;
    }

    /// <summary>The sidecar's <c>move</c>: only for the session on screen, passed to the page.</summary>
    public bool Move(long seq, int sel, string? key)
    {
        if (Current is not { } s || s.Seq != seq) return false;
        s.Sel = sel;
        _w.Send("switcher.move", new { seq, sel, key });
        return true;
    }

    /// <summary>
    /// <c>switcher.painted</c> for a session (SWI-046, SWI-086, SWI-089): "stale" for another seq, "waiting" while the page's
    /// size is not the window's (pictures would land in the wrong places), else the previews are registered while
    /// the window is still parked and the reveal is allowed. Returns null when taken, else the words.
    /// </summary>
    public string? Painted(long seq, double vw, double vh, IReadOnlyList<PreviewBox?>? boxes, double dpr, double now)
    {
        if (Current is not { } s || s.Seq != seq) return "stale";
        if (!(Math.Abs(vw - s.WinW) <= 1.5) || !(Math.Abs(vh - s.WinH) <= 1.5))
            return $"waiting: the page is {Fmt(vw)}x{Fmt(vh)}, the window is going to {Fmt(s.WinW)}x{Fmt(s.WinH)}";
        if (s.Painted is null) { PageDrew = true; s.Painted = now; }
        if (!s.Due && now >= s.DueAt) s.Due = true;
        (int X, int Y) origin = At ?? (s.Park.X, s.Park.Y);
        var hwnds = new List<string>(s.List.Count);
        foreach (var it in s.List) hwnds.Add(it.Hwnd);
        var res = _w.SyncThumbs(SwitcherWords.CleanBoxes(boxes, hwnds), double.IsFinite(dpr) && dpr > 0 && dpr <= 8 ? dpr : s.Scale, origin.X, origin.Y);
        s.Thumbs = res;
        _w.Send("switcher.thumbs", new { seq = s.Seq, ok = res.Ok, reason = res.Reason, thumbs = res.Count });
        MaybeReveal(s, now);
        return null;
    }

    static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>maybeReveal (SWI-047): due AND painted AND still current AND not shown: ONE place on screen. A rehearsal
    /// stops one step short and a test run never comes on screen. A failed place is logged; Alt-up still switches.</summary>
    void MaybeReveal(SwitcherSession s, double now)
    {
        if (Current != s || s.Shown is not null || !s.Due || s.Painted is null || !_w.Alive) return;
        if (s.Rehearsal is not null || _test) return;
        var how = PlaceTracked(s.Rect.X, s.Rect.Y, 0, 0, true);
        if (how is not null) { Log($"#{s.Seq}: the switcher could not come on screen ({how}); letting go of Alt still switches"); return; }
        s.Shown = now;
        s.AltUpSince = null;
        s.NextAltPoll = now + AltPollMs;
    }

    /// <summary>
    /// The owner's clock tick: the show delay (SWI-047), the late-paint line (SWI-057) and the stuck-overlay guard
    /// (SWI-137): while shown, Alt is looked at every 200 ms and the overlay is taken down once Alt has been up 600 ms
    /// with no word from the sidecar. It only takes the picture down; nothing is switched from here.
    /// <paramref name="altDown"/>: GetAsyncKeyState(VK_MENU) high bit, null when it cannot be read (the guard waits).
    /// </summary>
    public void Tick(double now, Func<bool?> altDown)
    {
        if (Current is not { } s) return;
        if (!s.Due && now >= s.DueAt) { s.Due = true; MaybeReveal(s, now); }
        if (Current != s) return;
        if (!s.PaintLateLogged && s.Painted is null && now - s.Recv >= PaintLateMs)
        {
            s.PaintLateLogged = true;
            Log($"#{s.Seq}: the page has not drawn this Alt+Tab after {PaintLateMs} ms; letting go of Alt still switches (the sidecar does it)");
        }
        if (s.Shown is null || now < s.NextAltPoll) return;
        s.NextAltPoll = now + AltPollMs;
        bool? down;
        try { down = altDown(); } catch { return; }
        if (down is null) return;
        if (down == true) { s.AltUpSince = null; return; }
        if (s.AltUpSince is null) { s.AltUpSince = now; return; }
        if (now - s.AltUpSince.Value >= AltUpGraceMs)
        {
            Log($"#{s.Seq}: Alt has been up for {AltUpGraceMs} ms and the sidecar never said this Alt+Tab ended, so Mantle took the overlay down (nothing was switched from here)");
            End(s, "the sidecar went quiet", now);
        }
    }

    /// <summary>
    /// endVisual (SWI-050, SWI-051, SWI-092): previews FIRST, then park (only if it moved), then the page empties.
    /// Once, and only while it is still THE session: a late call for one already pushed aside takes nothing from
    /// the current one. Returns true when it took the session down.
    /// </summary>
    public bool End(SwitcherSession s, string why, double now)
    {
        if (s.Ended is not null || Current != s) { s.Ended ??= now; return false; }
        Current = null;
        _w.ClearThumbs($"the Alt+Tab ended ({why})");
        if (_w.Alive)
        {
            if (At is not { } at || at.X != s.Park.X || at.Y != s.Park.Y)
            {
                var how = PlaceTracked(s.Park.X, s.Park.Y, 0, 0, false);
                if (how is not null) Log($"#{s.Seq}: the switcher window could not be parked ({why}): {how}");
            }
            _w.Send("switcher.end", new { seq = s.Seq, why });
        }
        s.Ended = now;
        return true;
    }

    /// End whatever is up (the sidecar stopped, Mantle put Windows back, a rest, quitting: SWI-140). Clears the acting one too.
    public bool EndAll(string why, double now)
    {
        Acting = null;
        return Current is { } s && End(s, why, now);
    }

    /// <summary>onCommit: the sidecar is switching; the picture goes at once and the switch never waits for it.</summary>
    public bool Commit(long seq, string? via, double now)
    {
        if (Current is not { } s || s.Seq != seq) return false;
        s.CommitAt = now;
        s.CommitVia = via;
        End(s, $"switching ({via ?? "undefined"})", now);
        Acting = s;
        return true;
    }

    /// <summary>onCancel: closed without a switch; the log line says why (Esc, a click elsewhere, the watchdog...).</summary>
    public void Cancel(long seq, string? why, double now)
    {
        if (Current is not { } s || s.Seq != seq) { Log($"#{seq}: closed without a switch ({why ?? "undefined"})"); return; }
        End(s, string.IsNullOrEmpty(why) ? "cancelled" : why, now);
        Log($"#{seq}: closed without a switch ({why ?? "undefined"}) | {TimingWords(s)}");
    }

    /// <summary>onActivated: the log line (no title, no handle) and whether a notice is due (an empty list is no failure).</summary>
    public bool Activated(ActivatedEvent m)
    {
        var s = Acting is { } a && a.Seq == m.Seq ? a : null;
        if (s is not null) Acting = null;
        Log($"{SwitcherWords.ActivatedLine(m)}{(s is not null ? " | " + TimingWords(s) : "")}");
        return SwitcherWords.NoticeDue(m.Ok, m.Cause, s?.List.Count);
    }

    /// <summary>switcher.pick's index (SWI-077): the click carries its window; if the list changed, it goes where that window
    /// is now; a window no longer listed is refused in words.</summary>
    public (int Index, string? Error) PickIndex(long seq, int index, string? hwnd)
    {
        if (index < 0) return (-1, "switcher.pick needs the seq and index of a tile");
        if (Current is not { } s || s.Seq != seq || hwnd is null) return (index, null);
        for (int i = 0; i < s.List.Count; i++) if (s.List[i].Hwnd == hwnd) return (i, null);
        return (-1, "that window closed just before the click arrived");
    }

    /// <summary>
    /// onReflow (SWI-106, SWI-109, SWI-111): the list changed while it is up. Tiles keep their size; the window grows
    /// (never shrinks) only when the panel no longer fits, keeping its centre and top; a changed column count is
    /// re-sent; the log counts, never names.
    /// </summary>
    public bool Reflow(JsonElement m)
    {
        if (Current is not { } s || s.Seq != (long)Num(m, "seq") || s.Ended is not null || s.Undrawn is not null) return false;
        var list = Items(m);
        var before = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in s.List) before.Add(it.Hwnd);
        var now_ = new HashSet<string>(StringComparer.Ordinal);
        int added = 0;
        foreach (var it in list) { now_.Add(it.Hwnd); if (!before.Contains(it.Hwnd)) added++; }
        int removed = 0;
        foreach (var it in s.List) if (!now_.Contains(it.Hwnd)) removed++;
        int prevCols = s.Lay.Cols;
        var lay = SwitcherLayout.Reflow(list.Count, s.Work.W / s.Scale, s.Work.H / s.Scale, s.Lay);
        bool grew = false;
        if (lay.W > s.WinW + 0.5 || lay.H > s.WinH + 0.5)
        {
            grew = true;
            double ww = Math.Max(s.WinW, lay.W), wh = Math.Max(s.WinH, lay.H);
            int w = (int)Math.Round(ww * s.Scale), h = (int)Math.Round(wh * s.Scale);
            static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(v, Math.Max(lo, hi)));
            int x = Clamp((int)Math.Round(s.Rect.X + (s.Rect.W - w) / 2.0), s.Work.X, s.Work.X + s.Work.W - w);
            int y = Clamp(s.Rect.Y, s.Work.Y, s.Work.Y + s.Work.H - h);
            s.WinW = ww; s.WinH = wh;
            s.Rect = new PhysRect(x, y, w, h);
            string? how;
            if (s.Shown is not null) how = PlaceTracked(x, y, w, h, true);
            else
            {
                s.Park = SwitcherWords.ParkSpot(s.Mon, w, h, _w.Displays);
                how = SizeParked(s.Mon, s.Park, w, h);
            }
            if (how is not null) Log($"#{s.Seq}: the switcher window could not grow for the longer list ({how}); the new tiles may be cut off");
        }
        s.List = list;
        s.Lay = lay;
        s.Sel = Int(m, "sel") ?? -1;
        s.Reflows++;
        _w.Send("switcher.windows", new
        {
            seq = s.Seq, sel = s.Sel, current = Int(m, "current") ?? -1, list, lay, win = new { w = s.WinW, h = s.WinH },
            grew, added, removed,
        });
        if (lay.Cols > 0 && lay.Cols != prevCols && s.Rehearsal is null) _grid(s.Seq, lay.Cols);
        if (added > 0 || removed > 0)
            Log($"#{s.Seq}: the list changed while it was up: {added} opened, {removed} closed, {list.Count} now{(grew ? "; the window grew to fit" : "")}");
        return true;
    }

    /// <summary>presizeTick's rule (SWI-052): no pre-size in a test run, with Alt+Tab off, stopped or resting.</summary>
    public static bool PresizeWanted(bool stopped, bool test, bool altTab, bool resting) => !stopped && !test && altTab && !resting;

    /// <summary>
    /// presize (SWI-052): while no Alt+Tab (and no notice) is up, keep the parked window at the size the NEXT one most
    /// likely needs: <paramref name="n"/> tiles (the bar's window list) on the monitor of the last Alt+Tab. A size
    /// change makes the first frame late; a wrong guess costs what not guessing costs. Null when nothing was done,
    /// else the place's outcome (null words = it went).
    /// </summary>
    public (bool Done, string? How) Presize(int n, bool pageReady)
    {
        if (!_w.Alive || !pageReady || Current is not null || NoticeUp || n < 0) return (false, null);
        (PhysRect Mon, PhysRect Work) t = Target ?? (_w.PrimaryMon, _w.PrimaryWork);
        double scale = _w.ScaleFor(t.Mon);
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        var lay = SwitcherLayout.Layout(n, t.Work.W / scale, t.Work.H / scale);
        int w = (int)Math.Round(lay.W * scale), h = (int)Math.Round(lay.H * scale);
        if (Size is { } sz && sz.W == w && sz.H == h) return (false, null);
        var how = SizeParked(t.Mon, SwitcherWords.ParkSpot(t.Mon, w, h, _w.Displays), w, h);
        if (how is not null && PresizeErr != how) Log($"the switcher window could not be sized ahead of the next Alt+Tab: {how}");
        PresizeErr = how;
        return (true, how);
    }

    /// The window was destroyed (a rest, a crash): its size and place are unknown again.
    public void Forget() { Size = null; At = null; _monKey = null; }

    /// <summary>timingWords: the one line after each Alt+Tab (counts and ms only).</summary>
    public static string TimingWords(SwitcherSession s)
    {
        string K(double? t) => s.KeyAt > 0 && t is double v ? $"{Fmt(R1(v - s.KeyAt))} ms" : "?";
        string toMain = s.KeyAt > 0 ? $"{Fmt(R1(s.Recv - s.KeyAt))} ms" : "?";
        var parts = new List<string> { $"key->open {Fmt(R1(s.KeyMs))} ms", $"->main {toMain}" };
        if (s.Undrawn is not null) parts.Add($"not drawn: {s.Undrawn}");
        else
        {
            parts.Add(s.Painted is not null ? $"->drawn {K(s.Painted)}{(s.Resized ? " (the window changed size)" : "")}" : "never drawn");
            parts.Add(s.Shown is not null ? $"->on screen {K(s.Shown)}" : $"never on screen (a tap: under {ShowDelayMs} ms)");
            if (s.CommitAt is not null) parts.Add($"->let go {K(s.CommitAt)}");
        }
        parts.Add($"{s.List.Count} window{(s.List.Count == 1 ? "" : "s")}, {(s.Tracking ? "most recently used first" : "Windows' stacking order")}");
        return string.Join(", ", parts);
    }

    static double R1(double v) => Math.Round(v * 10) / 10;
}
