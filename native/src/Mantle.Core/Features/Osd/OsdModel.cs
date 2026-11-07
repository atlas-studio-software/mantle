using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Mantle.Core.Features.Toasts;

namespace Mantle.Core.Features.Osd;

// Port of ui/osd/osd-model.js: the pure part of the volume / brightness pop-up. The fade clock, what a level
// reads as, the glyphs, the plain-words reasons, the wheel, the per-app list order, and where the pop-up goes.
// The surface (Mantle.App/Surfaces/Osd) draws from these, so the rules are tested here without a window.

/// <summary>The fade clock: fades HOLD_MS after the last thing that happened, never while hovered or held.</summary>
public sealed class OsdFader
{
    public const int HoldMs = 1500;
    readonly IToastClock _clock;
    readonly Action _onFade;
    readonly int _hold;
    IDisposable? _timer;
    bool _hovered, _held, _up;

    public OsdFader(IToastClock clock, Action onFade, int holdMs = HoldMs) { _clock = clock; _onFade = onFade; _hold = holdMs; }

    void Arm()
    {
        _timer?.Dispose(); _timer = null;
        if (!_up || _hovered || _held) return;
        _timer = _clock.Later(_hold, () =>
        {
            _timer = null;
            if (!_up || _hovered || _held) return;
            _up = false;
            try { _onFade(); } catch { /* the surface's fade, not ours */ }
        });
    }

    /// A key, a level change, a click in the panel: restart the 1.5 s.
    public void Poke() { _up = true; Arm(); }
    public void Hover(bool on) { _hovered = on; Arm(); }
    public void Hold(bool on) { _held = on; Arm(); }
    public void Reset() { _up = false; _hovered = false; _held = false; _timer?.Dispose(); _timer = null; }
    public (bool Up, bool Hovered, bool Held, bool Armed) State => (_up, _hovered, _held, _timer != null);
}

/// <summary>A level as the pop-up shows it.</summary>
public sealed record OsdReading(string Value, string Label, string Aria, int Fill, bool Dead, bool Muted = false);

/// <summary>One stroke or fill of a 16x16 glyph, in SVG/XAML path syntax.</summary>
public sealed record GlyphPart(string Data, bool Fill, double Stroke = 0);

public sealed record AudioSession(string Key, string? Name = null, bool Active = false, double Level = 0, bool Muted = false, bool System = false);
public sealed record AppMemo(double FirstAt, double LastActiveAt);

public sealed record OsdRect(double X, double Y, double Width, double Height);
public sealed record OsdSpot(int X, int Y, int Width, int Height, bool Below, int Reach = 0, int Full = 0);

public static class OsdModel
{
    /// An app that stopped playing stays listed this long, so its row does not jump out from under the pointer.
    public const int GraceMs = 4000;
    public const int Notch = 100;

    public const string NoDevice = "No speakers or headphones are connected. Plug some in, or turn a sound device on in Windows’ Sound settings";

    public static int? Pct(double? v) => v is double d && double.IsFinite(d) ? Math.Max(0, Math.Min(100, ToastsCore.Round(d))) : null;

    public static OsdReading Describe(string kind, bool ok, double? level, bool muted = false, string? reason = null)
    {
        var l = Pct(level);
        if (kind == "brightness")
        {
            if (!ok || l == null) return new("—", "Brightness", "Brightness unavailable: " + Plain(reason), 0, true);
            return new(l.Value.ToString(CultureInfo.InvariantCulture), "Brightness", $"Brightness {l}%", l.Value, false);
        }
        if (!ok || l == null) return new("—", "Volume", "Volume unavailable: " + Plain(reason), 0, true);
        if (muted) return new(l.Value.ToString(CultureInfo.InvariantCulture), "Muted", $"Muted (volume {l}%)", l.Value, false, true);
        return new(l.Value.ToString(CultureInfo.InvariantCulture), "Volume", $"Volume {l}%", l.Value, false);
    }

    static readonly Regex NoDeviceRe = new("no default playback device", RegexOptions.IgnoreCase);
    static readonly Regex CodeParen = new(@"\s*\((?:HRESULT|error|code)?\s*:?\s*0x[0-9a-f]+\)", RegexOptions.IgnoreCase);
    static readonly Regex CodeBare = new(@"\b(?:HRESULT\s*)?0x[0-9a-f]{6,8}\b", RegexOptions.IgnoreCase);
    static readonly Regex ExName = new(@"\b[A-Z][A-Za-z]*Exception:\s*");
    static readonly Regex TheSidecar = new(@"\bthe sidecar\b", RegexOptions.IgnoreCase);
    static readonly Regex Sidecar = new(@"\bsidecar\b", RegexOptions.IgnoreCase);
    static readonly Regex Runs = new(@"\s{2,}");
    static readonly Regex BeforePunct = new(@"\s+([,.;:)])");

    /// A reason as the user reads it: the no-device one says what to do; others lose codes and exception names.
    public static string Plain(string? reason)
    {
        var s = reason ?? "";
        if (NoDeviceRe.IsMatch(s)) return NoDevice;
        s = CodeParen.Replace(s, "");
        s = CodeBare.Replace(s, "");
        s = ExName.Replace(s, "");
        s = TheSidecar.Replace(s, "Mantle");
        s = Sidecar.Replace(s, "Mantle");
        s = Runs.Replace(s, " ");
        s = BeforePunct.Replace(s, "$1").Trim();
        return s.Length > 0 ? s : "no reason was given";
    }

    /// A mouse notch (|delta| >= 100, or a line/page event) is one step; small deltas add up to a notch.
    public static (int Steps, double Acc) Wheel(double acc, double deltaY, int deltaMode)
    {
        if (!double.IsFinite(deltaY) || deltaY == 0) return (0, acc);
        if (deltaMode == 1 || deltaMode == 2 || Math.Abs(deltaY) >= Notch) return (deltaY < 0 ? 1 : -1, 0);
        double a = acc;
        if (a != 0 && (a < 0) != (deltaY < 0)) a = 0;   // turned round: start the new direction afresh
        a += deltaY;
        int n = 0;
        while (Math.Abs(a) >= Notch) { n += a < 0 ? 1 : -1; a += a < 0 ? Notch : -Notch; }
        return (n, a);
    }

    /// Volume glyph: 0/1/2/3 waves at level 0 / &lt;=33 / &lt;=66 / &gt;66, a cross when muted (the bar's icon).
    public static IReadOnlyList<GlyphPart> VolumeGlyph(double? level, bool muted)
    {
        var l = Pct(level);
        int waves = l == null || muted ? 0 : l <= 0 ? 0 : l <= 33 ? 1 : l <= 66 ? 2 : 3;
        var parts = new List<GlyphPart> { new("M2.2 6.1h2.4L8.1 3.1v9.8L4.6 9.9H2.2z", true) };
        string[] arcs = { "M10 6.2a2.6 2.6 0 0 1 0 3.6", "M11.8 4.9a5 5 0 0 1 0 6.2", "M13.6 3.6a7.4 7.4 0 0 1 0 8.8" };
        for (int i = 0; i < waves; i++) parts.Add(new(arcs[i], false, 1.2));
        if (muted) parts.Add(new("M10.2 5.6l4.6 4.8M14.8 5.6l-4.6 4.8", false, 1.3));
        return parts;
    }

    /// Sun glyph: 8 rays growing with the level; the disc filled from 50. The first part is the disc (centre 8,8, r 2.6).
    public static IReadOnlyList<GlyphPart> SunGlyph(double? level)
    {
        var l = Pct(level);
        double len = l == null ? 1.2 : 1.1 + l.Value / 100.0 * 1.3;
        var parts = new List<GlyphPart> { new("M5.4 8a2.6 2.6 0 1 0 5.2 0a2.6 2.6 0 1 0 -5.2 0z", l != null && l >= 50, 1.2) };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4, r0 = 4.4, r1 = r0 + len;
            string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            parts.Add(new($"M{F(8 + Math.Cos(a) * r0)} {F(8 + Math.Sin(a) * r0)}L{F(8 + Math.Cos(a) * r1)} {F(8 + Math.Sin(a) * r1)}", false, 1.2));
        }
        return parts;
    }

    /// The per-app list: apps playing now in first-seen order, plus any that stopped under GRACE_MS ago or are in use.
    public static (IReadOnlyList<AudioSession> Rows, Dictionary<string, AppMemo> Memo) ListApps(IReadOnlyList<AudioSession>? sessions,
        IReadOnlyDictionary<string, AppMemo>? memo, double now, Func<string, bool>? keep = null)
    {
        keep ??= _ => false;
        var next = new Dictionary<string, AppMemo>(StringComparer.Ordinal);
        var rows = new List<(AudioSession S, double FirstAt, int I)>();
        int i = -1;
        foreach (var s in sessions ?? Array.Empty<AudioSession>())
        {
            i++;
            if (s == null || string.IsNullOrEmpty(s.Key)) continue;
            var m = memo != null && memo.TryGetValue(s.Key, out var mm) ? mm : new AppMemo(now, double.NegativeInfinity);
            if (s.Active) m = new AppMemo(m.FirstAt, now);
            bool show = s.Active || now - m.LastActiveAt < GraceMs || keep(s.Key);
            if (m.LastActiveAt > double.NegativeInfinity || keep(s.Key)) next[s.Key] = m;
            if (show) rows.Add((s, m.FirstAt, i));
        }
        var sorted = rows.OrderBy(r => r.FirstAt).ThenBy(r => r.I).Select(r => r.S).ToArray();
        return (sorted, next);
    }

    public static string AppName(AudioSession? s) => s == null ? "An app" : s.System ? "System sounds" : string.IsNullOrEmpty(s.Name) ? "An app" : s.Name!;

    static bool Usable(OsdRect? bar, OsdRect on) => bar != null && bar.Width > 0 && bar.Height > 0 &&
        bar.X < on.X + on.Width && bar.X + bar.Width > on.X && bar.Y < on.Y + on.Height && bar.Y + bar.Height > on.Y;

    /// Bottom-centre of the display, above the bar (or below a top-docked bar); whole DIPs inside the work area.
    public static OsdSpot Place(OsdRect? bar, OsdRect work, double w0, double h0, double gap = 10, OsdRect? bounds = null)
    {
        var on = bounds ?? work;
        int w = ToastsCore.Round(w0), h = ToastsCore.Round(h0);
        double x = ToastsCore.Round(work.X + (work.Width - w) / 2.0), y;
        bool usable = Usable(bar, on);
        bool top = usable && bar!.Y + bar.Height / 2 < work.Y + work.Height / 2;
        if (usable && !top) y = ToastsCore.Round(bar!.Y - gap - h);
        else if (usable && top) y = ToastsCore.Round(bar!.Y + bar.Height + gap);
        else y = ToastsCore.Round(work.Y + work.Height - gap - h - 40);   // no bar: where it would be
        x = Math.Max(work.X, Math.Min(x, work.X + work.Width - w));
        y = Math.Max(work.Y, Math.Min(y, work.Y + work.Height - h));
        return new OsdSpot((int)x, (int)y, w, h, top, 0, h);
    }

    /// The LOW window: its bottom on the work area's bottom; <c>Reach</c> is how far the pane lifts while the bar is up.
    public static OsdSpot PlaceLow(OsdRect? bar, OsdRect work, double w, double h, OsdRect? bounds = null,
        Func<OsdSpot, OsdRect, OsdRect, double>? reachOver = null)
    {
        var p = Place(bar, work, w, h, 0, bounds);
        if (p.Below) return p with { Reach = 0, Full = p.Height };
        int y = (int)Math.Max(work.Y, ToastsCore.Round(work.Y + work.Height - p.Height));
        var low = new OsdSpot(p.X, y, p.Width, p.Height, false);
        int reach = Usable(bar, bounds ?? work) && reachOver != null ? Math.Max(0, ToastsCore.Round(Num(reachOver(low, bar!, work)))) : 0;
        reach = (int)Math.Min(reach, y - work.Y);
        return low with { Reach = reach, Full = low.Height + reach };
    }

    static double Num(double v) => double.IsFinite(v) ? v : 0;
}
