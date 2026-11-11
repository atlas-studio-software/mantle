using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Mantle.Core.Features.Corners;

/// <summary>
/// The live claim table (lib/features/_corners.js setClaims / claims): which corner squares are taken right now.
/// The bottom bar's edge reveal and the top bar's peek (TOP-297) both watch an edge that ends in two corners; an
/// ACTIVE hot corner takes its 6x6 DIP square from both, so a corner and a peek / a reveal never both happen. An Off
/// corner takes nothing; while the corners are stopped, resting or all Off there is no table and every edge reveals
/// exactly as without corners.
///
/// <para>THE API for the edge watchers (any thread; reads are lock-free over an immutable snapshot):</para>
/// <list type="bullet">
/// <item><c>CornerClaims.Live.Claims(pt, display)</c>: the corner id (<c>tl</c>, <c>tr</c>, <c>bl</c>, <c>br</c>)
/// whose square holds <paramref name="pt"/>, or null. Both in DIP; <c>display</c> is the display the edge belongs to
/// (used only when the corners feature has not handed over a display list).</item>
/// <item><c>CornerClaims.Live.Active</c>: is there a table at all.</item>
/// <item><c>CornerClaims.Live.Changed</c>: raised after every Set (on the setter's thread).</item>
/// </list>
/// Only the corners feature calls <see cref="Set(CornerTable?)"/>.
/// </summary>
public sealed class CornerClaims
{
    /// The one table the running Mantle uses.
    public static CornerClaims Live { get; } = new();

    sealed record Table(CornerTable? Actions, IReadOnlyList<DipRect>? Displays);
    Table _t = new(null, null);

    /// Raised after every Set.
    public event Action? Changed;

    /// Is there a table (the corners run and at least one is not Off)?
    public bool Active => Volatile.Read(ref _t).Actions != null;

    /// The active table while the watch runs, null when it stops, rests or is switched off. The table is kept as a
    /// copy (records are immutable). The display list is kept as it was.
    public void Set(CornerTable? actions) => Put(actions, Volatile.Read(ref _t).Displays);

    /// The same, with the display list (DIP) refreshed: an inner corner goes back to the edge.
    public void Set(CornerTable? actions, IReadOnlyList<DipRect>? displays) =>
        Put(actions, displays is { Count: > 0 } ? displays.ToArray() : null);

    void Put(CornerTable? actions, IReadOnlyList<DipRect>? displays)
    {
        Volatile.Write(ref _t, new Table(CornerRules.AnyOn(actions) ? actions : null, displays));
        try { Changed?.Invoke(); } catch { /* a listener never stops the setter */ }
    }

    /// The ACTIVE corner the point is in, or null: that square is the corner's, not the edge's.
    public string? Claims(DipPoint pt, DipRect display)
    {
        var t = Volatile.Read(ref _t);
        if (t.Actions == null) return null;
        string? c = t.Displays != null ? CornerRules.CornerOnScreens(pt, t.Displays) : CornerRules.CornerAt(pt, display);
        return c != null && t.Actions.Of(c) != "off" ? c : null;
    }
}
