using System;
using System.Text.Json.Nodes;

namespace Mantle.Core.Look.Levitate;

/// <summary>
/// The levitated look's uniform DIM: a black wash at alpha dim over the blurred copy. Default 0.20; a user's saved value
/// (look.json <c>glass.&lt;theme&gt;.dim</c>) wins, clamped to the contract range 0.20..0.90. The legibility check is on
/// when <c>glass.&lt;theme&gt;.dimAuto</c> is absent or true ("Automatic"), off when it is false (manual).
/// </summary>
/// <remarks>
/// Deviation noted in docs/lanes/r10-newlook.md: the prototype's own slider runs 0..0.60 in 5 % steps; this follows the
/// contract range. And unlike <see cref="Wash.Alpha"/> (the 2026-11-11 look), an out-of-range saved value is CLAMPED here,
/// not replaced by the seed. No new look.json key: dim and dimAuto are the existing ones.
/// The only look settings of the new look are Dim and the brightness check (no Blur control). This class only reads; when
/// a writer comes (Settings), a Dim change must never write <c>dimAuto: false</c>: only the brightness-check switch writes
/// dimAuto (look-sync v2 CONTRACT). Nothing new is built on <c>glass.&lt;theme&gt;.blurPx</c> (retired on v2).
/// </remarks>
public static class LevitateDim
{
    /// The dim to paint: the saved value clamped to 0.20..0.90, or 0.20 when there is none (or it is not a finite number).
    public static double Resolve(double? saved) =>
        saved is double d && double.IsFinite(d) ? Math.Clamp(d, LevitateTokens.DimMin, LevitateTokens.DimMax) : LevitateTokens.DimDefault;

    /// The dim and the mode for a theme out of the look values <c>{ glass }</c> (or shared()'s <c>{ glass: { v } }</c>).
    /// <paramref name="theme"/>: "light", anything else is dark. Never throws.
    public static (double Dim, bool Auto) Read(JsonNode? look, string? theme)
    {
        string t = theme == "light" ? "light" : "dark";
        JsonObject? glass = look is JsonObject lo ? lo["glass"] as JsonObject : null;
        if (glass != null && glass["v"] is JsonObject gv && glass[t] is not JsonObject) glass = gv;   // shared() shape
        var g = glass?[t] as JsonObject;
        double? saved = g != null && LookSyncCore.IsFinite(g["dim"], out var d) ? d : null;
        bool auto = !(g != null && LookSyncCore.IsBool(g["dimAuto"], out var a) && !a);
        return (Resolve(saved), auto);
    }
}
