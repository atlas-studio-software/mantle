using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.LauncherIndex;

namespace Mantle.Core.Features.Search;

/// <summary>A row the launcher page shows: an index hit, or one of the page's own rows (web, calc).</summary>
public sealed record LauncherRow(LauncherItem Item, int? Score);

/// <summary>The Files group's line when it has no rows or is still looking (never selectable).</summary>
public sealed record GroupStatus(string Kind, string Text);

/// <summary>One labelled group of the launcher's results.</summary>
public sealed record LauncherGroup(string Key, string Label, IReadOnlyList<LauncherRow> Rows, GroupStatus? Status);

/// <summary>What the file search said (ui/launcher/groups.js <c>fileState</c>).</summary>
public sealed record FileState(string Status, string Reason = "");

/// <summary>
/// ui/launcher/groups.js, ported: the search rows laid out in labelled groups (Calculator first when there is
/// one, then Website, Apps, Commands, Settings, Power, Files by each group's best score with that fixed order
/// for ties, then Web always last); caps only when something is typed; the empty launcher puts Commands first.
/// </summary>
public static class LauncherGroups
{
    public sealed record Def(string Key, string Label, string Kind, int Cap);

    public static readonly Def[] Groups =
    [
        new("open", "Website", "url", 1),
        new("apps", "Apps", "app", 6),
        new("commands", "Commands", "command", 4),
        new("settings", "Settings", "setting", 4),
        new("power", "Power", "power", 6),
        new("files", "Files", "file", 6),
    ];

    public const int FileMin = 2;
    public const string EngineName = "Google";

    /// "Search the web for “text”", Google · in your browser, id web:&lt;lowercased text&gt;.
    public static LauncherRow WebRow(string q)
    {
        string text = q.Trim();
        return new LauncherRow(new LauncherItem
        {
            Id = "web:" + Js.Lower(text), Kind = "web", Name = "Search the web for “" + text + "”",
            Subtitle = EngineName + " · in your browser", Path = text,
        }, null);
    }

    public sealed record Result(IReadOnlyList<LauncherGroup> Groups, IReadOnlyList<LauncherRow> Flat);

    sealed class Slot
    {
        public required Def Def;
        public readonly List<LauncherRow> Rows = [];
        public GroupStatus? Status;
        public double Best = double.NegativeInfinity;
    }

    public static Result Build(string? q, IEnumerable<SearchHit> appRows, IEnumerable<LauncherRow>? fileRows = null,
        FileState? fileState = null, bool filePending = false, LauncherRow? calc = null)
    {
        string query = (q ?? "").Trim();
        fileState ??= new FileState("idle");
        var slots = Groups.Select(g => new Slot { Def = g }).ToList();
        void Place(LauncherRow it)
        {
            foreach (var s in slots)
            {
                if (s.Def.Kind != it.Item.Kind) continue;
                if (s.Rows.Count >= s.Def.Cap && query.Length > 0) return;     // the empty launcher keeps its twelve
                s.Rows.Add(it);
                s.Best = Math.Max(s.Best, it.Score ?? 0);
                return;
            }
        }
        foreach (var h in appRows) Place(new LauncherRow(h.Item, h.Score));
        if (query.Length >= FileMin && fileRows != null) foreach (var f in fileRows) Place(f);
        var files = slots.First(s => s.Def.Key == "files");
        if (query.Length >= FileMin)
        {
            if (fileState.Status == "error") files.Status = new("error", fileState.Reason.Length > 0 ? fileState.Reason : "the file search failed and gave no reason");
            else if (filePending && files.Rows.Count == 0) files.Status = new("pending", "Searching your files");
            else if (fileState.Status == "ok" && files.Rows.Count == 0 && !filePending) files.Status = new("empty", "No files match");
            else if (fileState.Reason.Length > 0) files.Status = new("note", fileState.Reason);
            if (filePending && files.Rows.Count > 0) files.Status = new("pending", "Still searching");
        }
        int Fixed(Slot s) => Array.FindIndex(Groups, g => g.Key == s.Def.Key);
        var ranked = Js.StableSort(slots.Where(s => s.Rows.Count > 0 || s.Status != null), (a, b) =>
        {
            double sa = a.Rows.Count > 0 ? a.Best : double.NegativeInfinity, sb = b.Rows.Count > 0 ? b.Best : double.NegativeInfinity;
            if (sa != sb) return sb > sa ? 1 : -1;
            return Fixed(a) - Fixed(b);
        });
        if (query.Length == 0) ranked = Js.StableSort(ranked, (a, b) => (a.Def.Key == "commands" ? 0 : 1) - (b.Def.Key == "commands" ? 0 : 1));
        var outList = new List<LauncherGroup>();
        if (calc != null) outList.Add(new LauncherGroup("calc", "Calculator", [calc], null));
        foreach (var g in ranked) outList.Add(new LauncherGroup(g.Def.Key, g.Def.Label, g.Rows, g.Status));
        if (query.Length > 0) outList.Add(new LauncherGroup("web", "Web", [WebRow(query)], null));
        var flat = outList.SelectMany(g => g.Rows).ToList();
        return new Result(outList, flat);
    }

    /// Tab / Shift+Tab: the first row of the next (dir 1) or previous (dir -1) group with rows, wrapping.
    public static int JumpGroup(IReadOnlyList<LauncherGroup> groups, int sel, int dir)
    {
        var starts = new List<int>();
        int at = 0, cur = 0;
        foreach (var g in groups)
        {
            if (g.Rows.Count == 0) continue;
            if (sel >= at && sel < at + g.Rows.Count) cur = starts.Count;
            starts.Add(at);
            at += g.Rows.Count;
        }
        if (starts.Count == 0) return 0;
        int next = ((cur + dir) % starts.Count + starts.Count) % starts.Count;
        return starts[next];
    }
}
