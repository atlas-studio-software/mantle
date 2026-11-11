using System.Collections.Generic;

namespace Mantle.Core.Features.WindowList;

/// <summary>
/// The counts behind "+N" (ui/bar/helmbadge.js paintMore; BAR-257, BAR-1030): the "+N" button carries the sum of the
/// hidden apps' counts (warn when any of them stopped on an error) and its tooltip gets "Needs you: &lt;app&gt; &lt;n&gt;, ...";
/// apps on the strip are not counted (their own icons show theirs), and a hidden "+N" has no disc and no line.
/// </summary>
public static class OverflowBadge
{
    /// One app behind "+N": its name, its count (0: none), and whether its source has an error.
    public readonly record struct Hidden(string Name, int Count, bool Warn);

    /// The disc's number (0: no disc), its warn colour, and the tooltip line ("" or "\nNeeds you: ...").
    public static (int N, bool Warn, string Line) Sum(IEnumerable<Hidden> hidden, bool moreShown)
    {
        if (!moreShown) return (0, false, "");
        int n = 0;
        bool warn = false;
        var names = new List<string>();
        foreach (var h in hidden) {
            if (h.Count <= 0) continue;
            n += h.Count;
            warn |= h.Warn;
            names.Add((h.Name.Length > 0 ? h.Name : "an app") + " " + h.Count);
        }
        return n > 0 ? (n, warn, "\nNeeds you: " + string.Join(", ", names)) : (0, false, "");
    }

    /// The accessible name of a tooltip (helmbadge.js: aria-label = the title with its line breaks as ". ").
    public static string Spoken(string tip) => tip.Replace("\n", ". ", System.StringComparison.Ordinal);
}
