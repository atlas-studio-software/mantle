using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Corners;

/// <summary>One corner tile in quick settings' Hot corners row.</summary>
public sealed record CornerCell(string Corner, string Label, string Action, string ActionLabel, bool Off, bool Failed)
{
    /// The tile's tooltip (CORN-062).
    public string Tip => $"{Label} corner: {ActionLabel}{(Failed ? " (its last push failed; the reason is below)" : "")}. Click to change.";
}

/// <summary>One line under the grid; a warning line is drawn in the warning ink.</summary>
public sealed record CornerLine(string Text, bool Warn);

/// <summary>What the Hot corners row shows for one <c>corners.state</c> answer (ui/bar/corners.js view()).</summary>
public sealed record CornerRowView(bool Dead, bool Loading, string Sub, IReadOnlyList<CornerCell> Cells,
    IReadOnlyList<(string Id, string Label)> Choices, IReadOnlyList<CornerLine> Lines)
{
    public bool Warn => Lines.Any(l => l.Warn);
}

/// <summary>
/// Quick settings' Hot corners row, its words and states (CORN-061..078), pure. <see cref="View"/> maps a
/// <c>corners.state</c> answer (null = the first answer is on its way; an object with "error" = no answer) to what the
/// row shows; <see cref="Plain"/> swaps anything that reads like a machine wrote it for a fallback in plain words.
/// </summary>
public static class CornerRow
{
    public const string Head = "Hot corners";
    public const string GroupName = "Hot corners: pick what each screen corner does";
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string> {
        ["tl"] = "Top left", ["tr"] = "Top right", ["bl"] = "Bottom left", ["br"] = "Bottom right",
    };
    public static readonly IReadOnlyList<(string Id, string Label)> DefaultChoices =
        CornerRules.Actions.Select(a => (a, CornerRules.ActionName(a))).ToArray();

    static readonly Regex Tech = new(@"sidecar|koffi|\bipc\b|renderer|hwnd|dry ?run|0x[0-9a-f]{4,}|\bE[A-Z]{4,}\b|undefined|\bnull\b|\bNaN\b|\[object|npm |\.(js|cs|exe|dll)\b|hresult|exception|LockWorkStation|Shell\.Application|IShellDispatch|user32|runChord|\bms\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Space = new(@"\s+");

    public static bool Technical(string? s) => s != null && Tech.IsMatch(s);

    /// One line, sentence case, a full stop; empty, over 200 characters or technical -> the fallback.
    public static string Plain(string? s, string fallback)
    {
        var t = Space.Replace(s ?? "", " ").Trim();
        if (t.Length == 0 || t.Length > 200 || Tech.IsMatch(t)) return fallback;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return ".!?)".Contains(t[^1]) ? t : t + ".";
    }

    public static string Label(string corner) => Labels.TryGetValue(corner, out var l) ? l : corner;

    static string NameOf(IReadOnlyList<(string Id, string Label)> choices, string? id)
    {
        foreach (var c in choices) if (c.Id == id) return c.Label;
        return string.IsNullOrEmpty(id) ? "Off" : id;
    }

    static string? S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Is(JsonElement e, string k, JsonValueKind kind) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == kind;
    static JsonElement O(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;

    public static CornerRowView View(JsonElement? state)
    {
        var none = Array.Empty<CornerCell>();
        if (state is not { ValueKind: JsonValueKind.Object } st)
            return new(true, true, "Loading…", none, DefaultChoices, Array.Empty<CornerLine>());
        if (S(st, "error") is { } err) {
            if (Regex.IsMatch(err, "resting while", RegexOptions.IgnoreCase))
                return new(true, false, "Resting · a game runs", none, DefaultChoices,
                    new[] { new CornerLine(Plain(err, "Hot corners rest while a game runs. They are back when it closes."), false) });
            return new(true, false, "Unavailable", none, DefaultChoices,
                new[] { new CornerLine("Hot corners didn't answer: " + Plain(err, "Close quick settings and open it again. If it keeps happening, restart Mantle."), true) });
        }
        var choices = new List<(string, string)>();
        if (O(st, "choices") is { ValueKind: JsonValueKind.Array } ch)
            foreach (var c in ch.EnumerateArray())
                if (S(c, "id") is { } id) choices.Add((id, S(c, "label") ?? id));
        IReadOnlyList<(string Id, string Label)> chs = choices.Count > 0 ? choices : DefaultChoices;
        var acts = O(st, "actions"); var fails = O(st, "fails");
        var cells = new List<CornerCell>(); int n = 0;
        foreach (var c in CornerRules.Corners) {
            string a = S(acts, c) ?? "off";
            if (a != "off") n++;
            cells.Add(new CornerCell(c, Label(c), a, NameOf(chs, a), a == "off", Is(fails, c, JsonValueKind.Object)));
        }
        bool off = Is(st, "enabled", JsonValueKind.False);
        string sub = off ? "Switched off in the tray" : n > 0 ? $"{n} on · push the pointer in" : "All off";
        var lines = new List<CornerLine>();
        if (off) lines.Add(new("Hot corners are switched off in Mantle's tray menu; choices are kept for when they are on.", true));
        var watch = O(st, "watch");
        if (Is(watch, "ok", JsonValueKind.False))
            lines.Add(new("The corners are paused: " + Plain(S(watch, "reason"), "Mantle can't read the mouse right now. They come back on their own."), true));
        foreach (var c in CornerRules.Corners) {
            var f = O(fails, c);
            if (f.ValueKind != JsonValueKind.Object) continue;
            string act = NameOf(chs, S(f, "action"));
            lines.Add(new($"{Label(c)} ({act}): " + Plain(S(f, "said"), $"{act} didn't work last time. Push into the corner again; if it keeps failing, restart Mantle."), true));
        }
        var last = O(st, "last");
        if (Is(last, "blocked", JsonValueKind.True)) {
            string said = S(last, "said") is { Length: > 0 and <= 200 } s && !Tech.IsMatch(s) ? s : "Mantle could not check for a game or a full-screen window in front";
            string who = S(last, "corner") is { } lc && Labels.ContainsKey(lc) ? Label(lc).ToLowerInvariant() : "a corner";
            lines.Add(new($"Last push, {who}: did nothing, {said}", false));
        }
        if (Is(st, "test", JsonValueKind.True)) lines.Add(new("This is a test copy of Mantle: corners only pretend to act.", false));
        return new(false, false, sub, cells, chs, lines);
    }

    /// A choice chip's tooltip (CORN-064).
    public static string ChipTip(CornerCell cell, string choiceLabel, bool current) =>
        current ? $"{cell.Label} corner: {choiceLabel} (now)" : $"Make the {cell.Label.ToLowerInvariant()} corner {choiceLabel.ToLowerInvariant()}";

    /// The warning after a save that did not take (CORN-067): its reason, or "no answer" when nothing came back.
    public static string SaveNote(string corner, string? reason, bool answered) =>
        $"{Label(corner)} could not be set: " + Plain(reason, answered ? "Mantle could not save it. Try again." : "Mantle did not answer. Try again.");
}
