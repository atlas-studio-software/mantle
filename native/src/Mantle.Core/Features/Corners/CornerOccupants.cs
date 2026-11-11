using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Corners;

/// <summary>One thing in the bottom-right corner: its window in screen DIP (rounded), how far it rises with the bar
/// up (<paramref name="Lift"/>) and with the bar hidden (<paramref name="Rest"/>, never more than Lift).</summary>
public sealed record CornerOccupant(string Key, int X, int Y, int Width, int Height, int Lift, int Rest);

/// <summary>
/// lib/corner.js: the shared registry of what occupies the bottom-right corner (the screenshot card, the clips record
/// panel), so the notification banners stack above whatever is there. NOT hot corners (same word only).
/// Thread: the host thread (every caller is), like Electron's module.
/// </summary>
public sealed class CornerOccupants
{
    /// The one registry the running Mantle uses.
    public static CornerOccupants Live { get; } = new();

    readonly Dictionary<string, CornerOccupant> _spots = new(StringComparer.Ordinal);
    readonly List<Action<string>> _fns = new();

    static CornerOccupant? Norm(string key, double? x, double? y, double? w, double? h, double? lift, double? rest)
    {
        if (x is not { } X || y is not { } Y || w is not { } W || h is not { } H) return null;
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(W) || !double.IsFinite(H)) return null;
        int rw = (int)Math.Round(W, MidpointRounding.AwayFromZero), rh = (int)Math.Round(H, MidpointRounding.AwayFromZero);
        if (rw <= 0 || rh <= 0) return null;
        int up = Math.Max(0, (int)Math.Round(lift is { } l && double.IsFinite(l) ? l : 0, MidpointRounding.AwayFromZero));
        int down = Math.Min(up, Math.Max(0, (int)Math.Round(rest is { } r && double.IsFinite(r) ? r : 0, MidpointRounding.AwayFromZero)));
        return new CornerOccupant(key, (int)Math.Round(X, MidpointRounding.AwayFromZero), (int)Math.Round(Y, MidpointRounding.AwayFromZero), rw, rh, up, down);
    }

    /// Add or move <paramref name="key"/>; a null or zero / broken rect clears it. True when anything changed
    /// (the same rect again is no change); listeners hear "&lt;key&gt; is up" / "&lt;key&gt; went".
    public bool Set(string? key, (double X, double Y, double Width, double Height)? rect, double? lift = null, double? rest = null)
    {
        string k = string.IsNullOrEmpty(key) ? "unnamed" : key;
        var r = rect is { } v ? Norm(k, v.X, v.Y, v.Width, v.Height, lift, rest) : null;
        _spots.TryGetValue(k, out var had);
        if (had == r) return false;
        if (r != null) _spots[k] = r; else _spots.Remove(k);
        Emit($"{k} {(r != null ? "is up" : "went")}");
        return true;
    }

    public IReadOnlyList<CornerOccupant> List() => _spots.Values.ToArray();

    /// handler(why) on every real change; the returned handle unsubscribes.
    public IDisposable OnChange(Action<string> fn) { _fns.Add(fn); return new Off(() => _fns.Remove(fn)); }

    /// Something outside the registry moved (a bar pop-up opened / closed): banners re-place at once.
    public void Changed(string? why) => Emit(string.IsNullOrEmpty(why) ? "something moved" : why);

    public void Reset() { _spots.Clear(); _fns.Clear(); }

    void Emit(string why)
    {
        foreach (var fn in _fns.ToArray()) { try { fn(why); } catch { /* one listener never stops the rest */ } }
    }

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) { _a = a; }
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }
}
