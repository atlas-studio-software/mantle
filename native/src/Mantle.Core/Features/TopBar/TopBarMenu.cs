using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.TopBar;

// ui/topbar/topbar.js's dropdowns (S.open, S.armed, S.edit; activate, move, stepTitle, openSub, refreshLevel, the keydown
// switch), ported as a pure machine: the surface draws what it holds and does what each step says (render, open a
// submenu, call a command, close). No XAML, no IPC, no clock of its own (the caller passes "now"), so every menu and
// keyboard rule is a unit test.

/// One open level of a dropdown (topbar.js S.open.stack[k]).
public sealed class MenuLevel
{
    public IReadOnlyList<TopBarItem> Items = Array.Empty<TopBarItem>();
    public int Sel = -1;
    /// Drawn and seen once in this menu session: later re-renders do not pop in again (TOP-080, topbar.js lv.shown).
    public bool Shown;
    public double X; public double? Y; public double? FlipX;
    /// The app's menu positions down to this level, and the labels along them (the UIA invoke's names).
    public IReadOnlyList<int> Path = Array.Empty<int>();
    public IReadOnlyList<string?> Names = Array.Empty<string?>();
    public string? Info; public bool Bad; public bool More;
    /// Painted once whole: later re-renders do not fade it in again (TOP-080).
    public bool Shown;
    public double Scroll;
}

/// What one menu action calls: the command, its args, and the row's label for a failure note.
public sealed record MenuCall(string Verb, JsonObject? Args, string Label);

public enum MenuDo { Nothing, Render, OpenSub, OpenTitle, Armed, Call, Close, Commit }

/// One step for the surface. <c>Call</c> = run Call, then close with Refocus; <c>Close</c> = close (Note, if any, on the
/// strip); <c>OpenSub</c> = open Level/Index's submenu (SelectFirst: and select its first row); <c>OpenTitle</c> = open
/// title Index (SelectFirst: its first row); <c>Armed</c> = re-render and disarm after ArmMs.
public sealed record MenuStep(MenuDo Do, int Level = 0, int Index = -1, MenuCall? Call = null, bool Refocus = false, bool SelectFirst = false, string? Note = null)
{
    public static readonly MenuStep None = new(MenuDo.Nothing);
    public static readonly MenuStep Render = new(MenuDo.Render);
}

/// The open menu.
public sealed class TopBarMenu
{
    public const int ArmMs = 4000, ArmMinMs = 300, HoverSubMs = 110, WaitMs = 1500;
    public const double DdTop = 30, DdGap = 8, DdMin = 72, SubW = 230, EdgeGap = 4;

    public int Ti;
    public bool IsMore;
    /// A tool's dropdown (the timer): Left / Right never walk into the app's titles (TOP-089).
    public string? Tool;
    /// A stat's dropdown (TOP-100): no rows of the app's; Left / Right walk to the app's titles from the first (TOP-090).
    public string? Stat;
    /// When its details were last asked for (TopBarStatMenu.RenewDue).
    public long DetailAt;
    public bool GaveUp;
    public readonly List<MenuLevel> Stack = new();
    public (TopBarItem Item, long At)? Armed;
    public (TopBarItem Item, string Value)? Edit;

    public MenuLevel Top => Stack[^1];

    /// openMenu: a title's own items, hung under it.
    public static TopBarMenu ForTitle(int ti, TopBarTitle t, double x)
    {
        var m = new TopBarMenu { Ti = ti };
        m.Stack.Add(new MenuLevel { Items = t.Items ?? Array.Empty<TopBarItem>(), X = x, Path = t.Pos is int p ? new[] { p } : Array.Empty<int>(), Names = new string?[] { t.Label } });
        return m;
    }

    /// openStat: a meter's dropdown. Ti is 0, so stepping from it starts at the first title (topbar.js stepTitle).
    public static TopBarMenu ForStat(string key)
    {
        var m = new TopBarMenu { Ti = 0, Stat = key };
        m.Stack.Add(new MenuLevel());
        return m;
    }

    /// TOP-070 openMore: one row per hidden title; a row opens that title's menu as a submenu.
    public static TopBarMenu ForMore(IReadOnlyList<TopBarTitle> titles, int first, double x)
    {
        var rows = titles.Skip(first).Select((t, k) => new TopBarItem("item", t.Label, Enabled: t.Enabled, Items: t.Items, Pos: t.Pos, Id: t.Id,
            Lazy: t.Lazy, Source: t.Source, Ti: first + k)).ToArray();
        var m = new TopBarMenu { Ti = first, IsMore = true };
        m.Stack.Add(new MenuLevel { Items = rows, X = x, More = true });
        return m;
    }

    public static bool Selectable(TopBarItem? it) => it != null && it.T != "sep" && it.T != "info" && it.Enabled;

    /// TOP-083 move: the next enabled row in that direction, skipping separators and disabled rows, wrapping around.
    public bool Move(int level, int dir)
    {
        var lv = Stack[level];
        int n = lv.Items.Count;
        for (int k = 1; k <= n; k++)
        {
            int j = (((lv.Sel < 0 ? (dir > 0 ? -1 : 0) : lv.Sel) + dir * k) % n + n * 2) % n;
            if (Selectable(lv.Items[j])) { lv.Sel = j; return true; }
        }
        return false;
    }

    /// TOP-084/085 stepTitle: the title dir steps away from cur, wrapping through n (the titles on the bar, then »).
    public static int StepTitle(int cur, int dir, int n) => n <= 0 ? -1 : ((cur + dir) % n + n) % n;

    /// TOP-088: the first enabled row whose mnemonic is k, else whose label starts with it; -1 = none.
    public static int Mnemonic(IReadOnlyList<TopBarItem> items, char key)
    {
        string k = char.ToLowerInvariant(key).ToString();
        for (int j = 0; j < items.Count; j++)
        {
            var it = items[j];
            if (!Selectable(it)) continue;
            if (it.Mn == k || (string.IsNullOrEmpty(it.Mn) && (it.Label ?? "").StartsWith(k, StringComparison.OrdinalIgnoreCase))) return j;
        }
        return -1;
    }

    /// TOP-062/051: what a row shows: the confirm words while armed, the owner-drawn and untitled words, the check column
    /// (✓ checked, ● radio), the key column, ▸ for a submenu, and whether it is dimmed (not selectable).
    public static (string Label, string Check, string Key, string Arrow, bool Off) Row(TopBarItem it, bool armed)
    {
        bool off = !it.Enabled || (it.OwnerDraw && string.IsNullOrEmpty(it.Label));
        string label = armed ? it.Confirm ?? it.Label ?? "" : !string.IsNullOrEmpty(it.Label) ? it.Label! : it.OwnerDraw ? "(the app draws this item itself)" : "(untitled)";
        return (label, it.Checked ? (it.Radio ? "●" : "✓") : "", it.Key ?? "", it.Items != null ? "▸" : "", off);
    }

    public const string Empty = "This menu is empty.";
    public const string Asking = "Asking the app for this menu…";
    public const string StayedEmpty = "The app fills this menu only when its own menu opens, and it stayed empty.";

    /// TOP-064/065/069/081 and the Mantle / app menus' rows: what a pick calls. Null = nothing can run it.
    public static MenuCall? CallFor(TopBarItem it, MenuLevel lv, bool confirmed, JsonObject? focus, JsonObject? src)
    {
        string label = it.Label ?? "";
        static JsonObject O(params (string K, JsonNode? V)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }
        switch (it.Act)
        {
            case "layoutRestore": return new("layouts.restore", O(("name", it.Layout), ("via", "the Mantle menu")), label);
            case "layoutSave": return new("layouts.save", O(("name", it.Layout), ("replace", true)), label);
            case "layoutDelete": return new("layouts.delete", O(("name", it.Layout)), label);
            case "power": return new("topbar.power", O(("action", it.Power), ("confirmed", confirmed)), label);
            case "about" or "settings" or "winSettings": return new("topbar." + it.Act, null, label);
            case "overGames": return new("topbar.overGames", O(("on", !it.Checked)), label);
            case "hide": return new("topbar.hidden", O(("on", true), ("via", "the Mantle menu")), label);
            case "show": return new("topbar.hidden", O(("on", false), ("via", "the Mantle menu, while peeking")), label);
            case "newWindow": return new("topbar.newWindow", O(("exe", it.Exe), ("args", new JsonArray((it.Args ?? Array.Empty<string>()).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()))), label);
            case null: break;
            default: return new("topbar.window", O(("hwnd", Str(focus, "hwnd")), ("action", it.Act)), label);
        }
        var source = Str(src, "source");
        if (source == "win32" && it.Id is int id) return new("topbar.invoke", O(("hwnd", Str(src, "hwnd")), ("item", id)), label);
        if (source == "uia" && it.Pos is int pos)
        {
            var path = lv.Path.Append(pos).ToArray();
            var names = lv.Names.Append(it.Label).ToArray();
            return new("topbar.uiaInvoke", O(("hwnd", Str(src, "hwnd")), ("path", new JsonArray(path.Select(p => (JsonNode)JsonValue.Create(p)).ToArray())),
                ("names", new JsonArray(names.Select(n => (JsonNode?)(n == null ? null : JsonValue.Create(n))).ToArray()))), label);
        }
        return null;
    }
    static string? Str(JsonObject? o, string k) => o != null && o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    /// activate (TOP-048/051/064/065/069/081, TOP-040/042): a pick on row i of level.
    public MenuStep Activate(int level, int i, long now, JsonObject? focus, JsonObject? src)
    {
        if (level < 0 || level >= Stack.Count) return MenuStep.None;
        var lv = Stack[level];
        var it = i >= 0 && i < lv.Items.Count ? lv.Items[i] : null;
        if (it == null || it.T == "sep" || it.T == "info" || !it.Enabled) return MenuStep.None;
        if (it.Items != null) return new(MenuDo.OpenSub, level, i);
        if (Edit is { } ed && ReferenceEquals(ed.Item, it)) return MenuStep.None;   // a click in the name box
        if (it.Act is "layoutNew" or "layoutRename")
        {
            Armed = null;
            Edit = (it, it.Act == "layoutRename" ? it.Layout ?? "" : "");
            lv.Sel = i; lv.Info = null; lv.Bad = false;
            return MenuStep.Render;
        }
        if (it.Confirm != null)
        {
            if (Armed is not { } a || !ReferenceEquals(a.Item, it)) { Armed = (it, now); return new(MenuDo.Armed, level, i); }
            if (now - a.At < ArmMinMs) return MenuStep.None;   // a double-click is not two decisions
        }
        bool confirmed = it.Confirm != null && Armed is { } b && ReferenceEquals(b.Item, it);
        var call = CallFor(it, lv, confirmed, focus, src);
        if (call == null) return new(MenuDo.Close, Refocus: true, Note: $"{it.Label}: this item has nothing to run it with");
        return new(MenuDo.Call, level, i, call, Refocus: true);
    }

    /// The armed row's 4 s ran out (TOP-051): true when it was still that row (re-render).
    public bool Disarm(TopBarItem it) { if (Armed is { } a && ReferenceEquals(a.Item, it)) { Armed = null; return true; } return false; }

    /// commitEdit (TOP-040/042): the name box's Enter.
    public MenuCall? Commit()
    {
        if (Edit is not { } ed) return null;
        Edit = null;
        bool renaming = ed.Item.Act == "layoutRename";
        return renaming
            ? new("layouts.rename", new JsonObject { ["from"] = ed.Item.Layout, ["to"] = ed.Value }, "Rename " + ed.Item.Layout)
            : new("layouts.save", new JsonObject { ["name"] = ed.Value }, "Save layout");
    }

    /// TOP-083..088 and TOP-082/089: the keydown switch. Keys: "Escape", "ArrowDown", "ArrowUp", "ArrowRight",
    /// "ArrowLeft", "Enter", " ", or one character.
    public MenuStep Key(string key, long now, JsonObject? focus, JsonObject? src)
    {
        if (Edit != null)
        {
            if (key == "Enter") return new(MenuDo.Commit);
            if (key == "Escape") { Edit = null; return MenuStep.Render; }   // the box closes, the menu stays
            return MenuStep.None;   // every other key types into the box
        }
        if (Stat != null)   // TOP-090: a stat dropdown has no rows of its own; Escape closes it, Left / Right walk the titles
            return key switch
            {
                "Escape" => new(MenuDo.Close, Refocus: true),
                "ArrowRight" => new(MenuDo.OpenTitle, Index: +1, SelectFirst: true),
                "ArrowLeft" => new(MenuDo.OpenTitle, Index: -1, SelectFirst: true),
                _ => MenuStep.None,
            };
        int level = Stack.Count - 1;
        var lv = Stack[level];
        var sel = lv.Sel >= 0 && lv.Sel < lv.Items.Count ? lv.Items[lv.Sel] : null;
        switch (key)
        {
            case "Escape":
                if (level > 0) { Stack.RemoveAt(level); return MenuStep.Render; }
                return new(MenuDo.Close, Refocus: true);
            case "ArrowDown": return Move(level, 1) ? MenuStep.Render : MenuStep.None;
            case "ArrowUp": return Move(level, -1) ? MenuStep.Render : MenuStep.None;
            case "ArrowRight":
                if (sel?.Items != null) return new(MenuDo.OpenSub, level, lv.Sel, SelectFirst: true);
                return Tool != null ? MenuStep.None : new(MenuDo.OpenTitle, Index: +1, SelectFirst: true);
            case "ArrowLeft":
                if (level > 0) { Stack.RemoveAt(level); return MenuStep.Render; }
                return Tool != null ? MenuStep.None : new(MenuDo.OpenTitle, Index: -1, SelectFirst: true);
            case "Enter" or " ": return lv.Sel >= 0 ? Activate(level, lv.Sel, now, focus, src) : MenuStep.None;
        }
        if (key.Length != 1) return MenuStep.None;
        int j = Mnemonic(lv.Items, key[0]);
        if (j < 0) return MenuStep.None;
        lv.Sel = j;
        return lv.Items[j].Items != null ? new(MenuDo.OpenSub, level, j) : Activate(level, j, now, focus, src);
    }

    /// openSub's state half (TOP-067/070/072): pushes row i's submenu. The row's box (DIP, the window's) places it 2 px
    /// right of the row and 6 px above its top, with a flip to the parent's left. Returns what to do next: Title = a
    /// lazy UIA title hidden in » (open it as the bar would), Refresh = ask the app for this level's items (a lazy
    /// Win32 submenu, or a Win32 title opened from »).
    public (bool Pushed, int? Title, bool Refresh) PushSub(int level, int i, double rowLeft, double rowRight, double rowTop, string? srcKind)
    {
        if (level < 0 || level >= Stack.Count) return (false, null, false);
        var lv = Stack[level];
        var it = i >= 0 && i < lv.Items.Count ? lv.Items[i] : null;
        if (lv.More && it is { Source: "uia", Lazy: true, Enabled: true } && it.Ti is int ti) return (false, ti, false);
        if (it?.Items == null || !it.Enabled) return (false, null, false);
        Stack.RemoveRange(level + 1, Stack.Count - level - 1);
        var path = it.Pos is int p ? lv.Path.Append(p).ToArray() : lv.Path.ToArray();
        var sub = new MenuLevel
        {
            Items = it.Items, X = Math.Round(rowRight + 2), FlipX = Math.Round(rowLeft - 2 - SubW), Y = Math.Round(rowTop - 6),
            Path = path, Names = lv.Names.Append(it.Label).ToArray(),
        };
        Stack.Add(sub);
        bool refresh = false;
        if (it.Lazy && srcKind == "win32") { sub.Info = Asking; refresh = true; }
        else if (lv.More && it.Source == "win32" && srcKind == "win32") refresh = true;
        return (true, null, refresh);
    }

    /// refreshLevel (TOP-061/067): the app's fresh reply for one level (topbar.menus {menu, ok, reason, initNote}).
    public static void Refreshed(MenuLevel lv, JsonObject? reply)
    {
        var fresh = reply?["menu"] is JsonObject m ? FindByPath(m["items"] as JsonArray, lv.Path) : null;
        if (fresh?["items"] is JsonArray items) { lv.Items = TopBarModel.FromJson(items); lv.Info = null; }
        else if (reply != null && reply.TryGetPropertyValue("ok", out var ok) && ok is JsonValue v && v.TryGetValue<bool>(out var b) && !b) { lv.Info = Str(reply, "reason"); lv.Bad = true; }
        if (Str(reply, "initNote") is { } note && lv.Items.Count == 0) lv.Info = note;
        if (lv.Items.Count == 0 && lv.Info == null) lv.Info = StayedEmpty;
    }

    /// The JSON row at a path of menu positions.
    public static JsonObject? FindByPath(JsonArray? items, IReadOnlyList<int> path)
    {
        JsonObject? cur = null; var list = items;
        foreach (int pos in path)
        {
            cur = list?.OfType<JsonObject>().FirstOrDefault(i => i.TryGetPropertyValue("pos", out var p) && p is JsonValue pv && pv.TryGetValue<int>(out var pi) && pi == pos);
            if (cur == null) return null;
            list = cur["items"] as JsonArray;
        }
        return cur;
    }

    // ---- geometry (DIP, the window's own coordinates)

    /// TOP-072: a dropdown whose right edge would pass the visible width - 4 moves: a submenu flips to its parent's left,
    /// a top-level one is pulled in to 4 px from the edge; never left of 4.
    public static double Left(int level, double left, double width, double visible, double? flipX)
        => left + width > visible - EdgeGap ? Math.Max(EdgeGap, level > 0 && flipX is double f ? f : visible - EdgeGap - width) : left;

    /// TOP-073 fitDown: limit = the screen's work-area bottom (in the window) - 8. A submenu that would pass it moves up
    /// (never above 30); one still too tall is capped (at least 72) and scrolls inside. Returns (top, max height or null).
    public static (double Top, double? MaxH) FitDown(int level, double top, double natural, double screenBottom)
    {
        double limit = screenBottom - DdGap;
        if (level > 0 && top + natural > limit) top = Math.Max(DdTop, limit - natural);
        return (top, top + natural > limit ? Math.Max(DdMin, limit - top) : null);
    }

    /// TOP-074 notifyMain: the window height the menus need: the lowest dropdown's bottom + 8, at least the 28 DIP bar.
    public static int WindowHeight(IEnumerable<double> bottoms)
    {
        double b = TopBarRules.BarDip;
        foreach (var x in bottoms) b = Math.Max(b, x + DdGap);
        return (int)Math.Ceiling(b);
    }

    /// TOP-079 keepSelVisible: the scroll that keeps the selected row in view with a 5 px margin (null = no change).
    public static double? KeepVisible(double rowTop, double rowH, double scroll, double viewH, double contentH)
    {
        if (contentH <= viewH) return null;
        if (rowTop < scroll) return Math.Max(0, rowTop - 5);
        if (rowTop + rowH > scroll + viewH) return rowTop + rowH - viewH + 5;
        return null;
    }

    /// TOP-081: the strip's words after a pick's reply (null = nothing to say).
    public static string? ResultNote(string label, bool ok, string? reason, bool dryRun)
    {
        if (!ok) return $"{label}: {(string.IsNullOrEmpty(reason) ? "it did not happen, and no reason came back" : reason)}";
        return dryRun && !string.IsNullOrEmpty(reason) ? reason : null;
    }

    /// TOP-076: the strip's words when the window never grew to the menu.
    public static string CutOff(double need, double have) => $"The menu is cut off: it needs {Math.Round(need)} px and the bar's window stayed {Math.Round(have)} px tall";
    /// TOP-074: the strip's words when main refused the taller window.
    public static string CannotShow(string? reason) => "The menu cannot be shown in full: " + (string.IsNullOrEmpty(reason) ? "the bar's window did not grow" : reason);
}
