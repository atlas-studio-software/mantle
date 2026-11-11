using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Media;

/// <summary>A session as the pick rule sees it (one row of main.js mediaRead's <c>sessions</c>).</summary>
public sealed record MediaPickSession(string? Aumid, string? AppName, bool Playing, bool ProtectedApp);

/// <summary>The user's pick in the card's source list (lib/media-pick.js makePick's shape).</summary>
public sealed record MediaPickHeld(string Aumid, string? Name, bool ProtectedApp, double At, double? GoneSince);

/// <summary>A held pick whose session is away: how long, and the card's words.</summary>
public sealed record MediaPickWaiting(string Aumid, string? Name, double GoneMs, double GraceMs, double LeftMs, string Reason);

/// <summary>A pick that was let go, with its reason.</summary>
public sealed record MediaPickDropped(string Aumid, string? Name, string Reason);

/// <summary>choose()'s answer: the pick to keep, the AUMID to show (null = Windows' current), and by which rule.</summary>
public sealed record MediaPickChoice(MediaPickHeld? Pick, bool ProtectedOnly, string? Want, string? By, string? Auto, MediaPickWaiting? Waiting, MediaPickDropped? Dropped);

/// <summary>
/// lib/media-pick.js: which session the card shows. The user's pick sticks while its session exists ('you'); a pick
/// whose session blinks out is HELD for 60 s of continuous absence ('waiting', the automatic choice shown meanwhile)
/// and applies again the moment it is back; after that it drops with a reason. Automatic = Windows' current session,
/// except that under the protected media apps rule another app wins (a playing one first); with no current session,
/// the first. With the rule on and only protected apps publishing, nothing is shown ('protected-only') and a pick of
/// a protected app is dropped at once. AUMIDs compare case-insensitively. Pure.
/// </summary>
public static class MediaPick
{
    public const double GraceMs = 60000;

    public static bool SameAumid(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string NameOf(MediaPickHeld p) => p.Name ?? p.Aumid ?? "the picked app";

    public static MediaPickHeld? Make(string? aumid, MediaPickSession? session, double now) =>
        string.IsNullOrEmpty(aumid) ? null : new MediaPickHeld(aumid, session?.AppName, session?.ProtectedApp ?? false, now, null);

    static MediaPickWaiting WaitingOf(MediaPickHeld p, double now, double grace)
    {
        double gone = Math.Max(0, now - (p.GoneSince ?? now));
        long secs = (long)Math.Round(grace / 1000, MidpointRounding.AwayFromZero);
        return new MediaPickWaiting(p.Aumid, p.Name, gone, grace, Math.Max(0, grace - gone),
            $"{NameOf(p)}'s media session is missing, so the card shows the automatic choice until it's back (your pick is kept for up to {secs} s)");
    }

    public static MediaPickChoice Choose(MediaPickHeld? pick, IReadOnlyList<MediaPickSession> sessions, MediaPickSession? current, bool guardOn, double now, double? graceMs = null)
    {
        double grace = graceMs ?? GraceMs;
        MediaPickDropped? dropped = null;
        var here = pick != null ? sessions.FirstOrDefault(s => SameAumid(s.Aumid, pick.Aumid)) : null;
        if (here != null) {
            pick = pick! with { GoneSince = null, ProtectedApp = here.ProtectedApp, Name = string.IsNullOrEmpty(here.AppName) ? pick!.Name : here.AppName };
        } else if (pick != null) {
            if (pick.GoneSince == null) pick = pick with { GoneSince = now };
            double gone = now - pick.GoneSince!.Value;
            if (gone >= grace) {
                dropped = new MediaPickDropped(pick.Aumid, pick.Name,
                    $"{NameOf(pick)}'s media session was gone for {(long)Math.Round(gone / 1000, MidpointRounding.AwayFromZero)} s, so the card went back to automatic");
                pick = null;
            }
        }
        if (guardOn && sessions.Count > 0 && sessions.All(s => s.ProtectedApp)) {
            if (pick is { ProtectedApp: true }) {
                dropped = new MediaPickDropped(pick.Aumid, pick.Name,
                    $"{NameOf(pick)} was picked, and a pick of a protected media app does not outlive the other player (the protected media apps rule)");
                pick = null;
            }
            return new MediaPickChoice(pick, true, null, "protected-only", null, pick != null ? WaitingOf(pick, now, grace) : null, dropped);
        }
        if (here != null) return new MediaPickChoice(pick, false, pick!.Aumid, "you", null, null, dropped);
        var others = sessions.Where(s => !s.ProtectedApp).ToList();
        string? want = null, by = null;
        if (current != null && !(guardOn && current.ProtectedApp)) by = "current";
        else if (guardOn && others.Count > 0) { want = (others.FirstOrDefault(s => s.Playing) ?? others[0]).Aumid; by = "protected-rule"; }
        else if (current != null) by = "current";
        else if (sessions.Count > 0) { want = sessions[0].Aumid; by = "first"; }
        if (pick != null) return new MediaPickChoice(pick, false, want, "waiting", by, WaitingOf(pick, now, grace), dropped);
        return new MediaPickChoice(null, false, want, by, null, null, dropped);
    }
}
