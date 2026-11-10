using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mantle.Core.Features.QuickSettings;

namespace Mantle.Core.Features.PowerMode;

/// One segment of the power mode switch: what the panel draws for it.
public sealed record PowerModeSeg(string Mode, string Label, bool On, bool Pending, bool Disabled, string Title);

/// What the quick-settings panel draws for the power mode row (ui/bar/powermode.js view(), the modes half; the power
/// plan list and its toggle are not drawn natively yet). Dead: the switch is muted (unavailable, off Balanced, or locked).
public sealed record PowerModeRowView(bool Dead, string Sub, string? Reason, string? Detail, string? Note, IReadOnlyList<PowerModeSeg> Segs, string Aria)
{
    /// The line under the switch (powermode.js paint: why = the reason of a dead switch, as a sentence).
    public string Why => Dead && !string.IsNullOrEmpty(Reason) ? PowerModeView.Sentence(Reason) : "";
}

/// <summary>Pure port of ui/bar/powermode.js view(state, pending, err): no UI, no I/O, unit-tested.</summary>
public static class PowerModeView
{
    public static readonly (string Mode, string Label, string Full)[] Modes =
        { ("efficiency", "Efficiency", "Best power efficiency"), ("balanced", "Balanced", "Balanced"), ("performance", "Performance", "Best performance") };

    static readonly Regex Jargon = new(@"sidecar|\bIPC\b|renderer|hwnd|dryRun|undefined|\bnull\b|\bNaN\b|\[object|HRESULT|0x[0-9a-f]{4,}|Win32|\berror \d|exception|powermode\.\w+|window\.mantle|preload|\.js\b|answered with nothing|timed? ?out|within \d+ ?ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Errno = new(@"\bE[A-Z]{3,}\b|^Error\b", RegexOptions.CultureInvariant);
    static readonly Regex TechBracket = new(@"\s*\([^()]*\b(?:error|HRESULT|0x[0-9a-f]+)\b[^()]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public const string PlainFail = "Mantle could not reach Windows. Try again in a moment";

    public static string FullName(string? mode)
    {
        foreach (var (m, _, f) in Modes) if (m == mode) return f;
        if (mode == "better") return "Better performance (a Windows 10 setting)";
        return string.IsNullOrEmpty(mode) ? "unknown" : "an unrecognised mode";
    }

    public static string Sentence(string? s)
    {
        s ??= "";
        return s.Length > 0 && ".!?…".IndexOf(s[^1]) >= 0 ? s : s + ".";
    }

    /// powermode.js plain(): plain words on screen; the passive test sidecar's refusal is "this is a test run".
    public static string Plain(string? reason)
    {
        var s = (reason ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.StartsWith("refused: sidecar is passive", System.StringComparison.Ordinal)) return "this is a test run, so Windows was not changed";
        s = TechBracket.Replace(s, "");
        return Jargon.IsMatch(s) || Errno.IsMatch(s) ? PlainFail : s;
    }

    /// <param name="s">powermode.get's state (null: not read yet).</param>
    /// <param name="pending">the mode a click asked for, while the set is in flight.</param>
    /// <param name="err">the last refusal's reason (a set that did not take).</param>
    public static PowerModeRowView View(JsonObject? s, string? pending, string? err)
    {
        bool ok = s != null && !QsParts.IsFalse(s["ok"]);
        bool live = ok && !QsParts.IsFalse(s!["available"]);
        // on Balanced but Windows will not take a mode now (Energy saver pins Best power efficiency)
        bool locked = live && QsParts.IsFalse(s!["settable"]);
        string? shown = pending ?? (ok ? QsParts.Str(s!["mode"]) : null);
        string? source = ok ? QsParts.Str(s!["source"]) : null;
        bool saver = ok && QsParts.IsTrue(s!["saver"]);
        string sub; string? reason = null, detail = null;
        if (s == null) sub = "Reading…";
        else if (!ok) {
            sub = "Unavailable";
            reason = NullIfEmpty(Plain(QsParts.Str(s["reason"]))) ?? "Windows did not say which power mode is in use";
        } else if (!live) {
            sub = "Needs the Balanced plan";
            reason = "Power modes work only on the Balanced power plan";
            detail = NullIfEmpty(Plain(QsParts.Str(s["reason"])));
        } else {
            // the choice is made PER POWER SOURCE (Settings: "Power mode when plugged in" / "... on battery")
            sub = source == "battery" ? "When on battery" : source == "ac" ? "When plugged in" : "Power source unknown";
            if (locked && saver) {
                reason = source == "battery" && QsParts.Num(s["saverAt"]) is double at && at >= 100
                    ? "Locked: Energy saver is on whenever this PC is unplugged"
                    : "Locked while Energy saver is on";
                detail = NullIfEmpty(Plain(QsParts.Str(s["reason"])));
            } else if (locked) {
                reason = NullIfEmpty(Plain(QsParts.Str(s["reason"]))) ?? "Windows will not take a power mode change right now";
            } else if (saver) sub += " · Energy saver on";
        }
        string? inForceMode = ok ? QsParts.Str(s!["inForce"]) : null;
        string inForce = ok && !locked && !string.IsNullOrEmpty(inForceMode) && inForceMode != QsParts.Str(s!["mode"])
            ? "; Windows reports " + FullName(inForceMode) + " in effect right now" : "";
        string srcWords = ok && source == "battery" ? " on battery" : ok && source == "ac" ? " when plugged in" : "";
        var segs = new List<PowerModeSeg>();
        foreach (var (m, label, full) in Modes) {
            bool on = shown == m;
            segs.Add(new PowerModeSeg(m, label, on, pending == m, !live || locked,
                live && !locked ? full + (on ? " (current" + srcWords + inForce + ")" : " — use" + srcWords) : full + " — " + Sentence(reason)));
        }
        string? mode = ok ? QsParts.Str(s!["mode"]) : null;
        bool other = ok && live && pending == null && System.Array.TrueForAll(Modes, x => x.Mode != mode);
        string? note = err != null ? Sentence("Power mode did not change: " + (NullIfEmpty(Plain(err)) ?? "Windows gave no reason"))
            : other ? Sentence("Windows reports " + FullName(mode) + "; pick one to replace it") : null;
        string aria = locked ? "Power mode: " + FullName(shown) + ", locked: " + reason
            : live ? "Power mode" + srcWords + ": " + FullName(shown) : "Power mode unavailable: " + reason;
        return new PowerModeRowView(!live || locked, sub, reason, detail, note, segs, aria);
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
