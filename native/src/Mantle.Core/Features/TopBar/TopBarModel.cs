using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.TopBar;

// ui/topbar/model.js, ported: what the top bar SAYS for a focused window. Pure (no XAML, no IPC), so the
// surface (Mantle.App/Surfaces/TopBar) and the feature share one definition of the app name, the titles, the
// fit and the stat formats. The input is the sidecar's focus snapshot (sidecar/MenuBar.cs Snapshot):
// { hwnd, title, cls, exe, desc, product, self, shell, zoomed, topmost, enabled, menu, menuReason, blocked, uia }.

/// <summary>One menu row (an item, a separator, or a plain-words info row).</summary>
public sealed record TopBarItem(string T, string? Label = null, string? Act = null, bool Enabled = true, bool Checked = false,
    string? Key = null, IReadOnlyList<TopBarItem>? Items = null, string? Power = null, string? Confirm = null, string? Mn = null);

/// <summary>One top-level title on the strip. <c>Glyph</c> = the Mantle glyph; <c>Bold</c> = the app's name.</summary>
public sealed record TopBarTitle(string Key, string Label, bool Glyph = false, bool Bold = false, bool Enabled = true,
    IReadOnlyList<TopBarItem>? Items = null, int? Pos = null, string? Source = null);

/// <summary>One stat cell (model.js statCells): <c>Why</c> set = no reading (drawn "--" in ink-2, the why as tooltip).</summary>
public sealed record TopBarCell(string Key, string Label, string Value, string? Why, bool Warn = false, string? Title = null);

/// <summary>fitTitles: titles [0, Shown) on the strip, the rest in "»"; AppW = the app name's max width or null.</summary>
public readonly record struct TopBarFit(int N, int Shown, double? AppW);

public static class TopBarModel
{
    public const double AppMin = 96;          // topbar.js APP_MIN: the app name ellipsizes down to this first
    public const int PingWarnMs = 100;

    /// "notepad++" from any path, ".exe" dropped.
    public static string Base(string? exe)
    {
        var s = (exe ?? "").Split('\\', '/').LastOrDefault() ?? "";
        return Regex.Replace(s, @"\.exe$", "", RegexOptions.IgnoreCase);
    }

    static readonly Regex Generic = new(@"^(electron|application frame host|windows explorer|host process for windows services|java\(tm\) platform se binary|node\.js)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Dictionary<string, string> Rename = new(StringComparer.Ordinal) { ["explorer"] = "File Explorer", ["applicationframehost"] = "App" };

    static string? S(JsonObject? f, string k) => f != null && f.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
    static bool B(JsonObject? f, string k) => f != null && f.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

    /// TOP-020: the desktop is "Desktop"; explorer "File Explorer"; ApplicationFrameHost "App"; else FileDescription unless
    /// generic, then ProductName, then the exe name capitalised; no exe at all is "Window".
    public static string AppName(JsonObject? f)
    {
        if (f == null) return "";
        if (B(f, "shell")) return "Desktop";
        var b = Base(S(f, "exe"));
        if (Rename.TryGetValue(b.ToLowerInvariant(), out var r)) return r;
        var d = (S(f, "desc") ?? "").Trim();
        if (d.Length > 0 && !Generic.IsMatch(d)) return d;
        var p = (S(f, "product") ?? "").Trim();
        if (p.Length > 0 && !Generic.IsMatch(p)) return p;
        if (b.Length == 0) return "Window";
        return char.ToUpperInvariant(b[0]) + b[1..];
    }

    static readonly Dictionary<string, string[]?> NewWindow = new(StringComparer.Ordinal)
    {
        ["chrome"] = new[] { "--new-window" }, ["msedge"] = new[] { "--new-window" }, ["brave"] = new[] { "--new-window" },
        ["vivaldi"] = new[] { "--new-window" }, ["opera"] = new[] { "--new-window" }, ["firefox"] = new[] { "-new-window" },
        ["code"] = new[] { "--new-window" }, ["explorer"] = Array.Empty<string>(), ["windowsterminal"] = null, ["notepad"] = Array.Empty<string>(),
    };
    /// "New window" only for apps that open one from the command line (never a synthetic Ctrl+N).
    public static string[]? NewWindowArgs(string? exe) => NewWindow.TryGetValue(Base(exe).ToLowerInvariant(), out var a) && a != null ? (string[])a.Clone() : null;

    /// The bold app menu: what every window can do, menu bar or not.
    public static IReadOnlyList<TopBarItem> AppMenu(JsonObject? f)
    {
        var name = AppName(f);
        var items = new List<TopBarItem>();
        if (f == null || B(f, "shell")) { items.Add(new("item", "New File Explorer window", "newWindow")); return items; }
        if (NewWindowArgs(S(f, "exe")) != null) { items.Add(new("item", "New window", "newWindow")); items.Add(new("sep")); }
        items.Add(new("item", "Minimize", "minimize"));
        items.Add(new("item", B(f, "zoomed") ? "Restore size" : "Zoom", "zoom"));
        items.Add(new("item", "Always on top", "ontop", Checked: B(f, "topmost")));
        items.Add(new("sep"));
        items.Add(new("item", "Close window", "close"));
        items.Add(new("item", "Quit " + name, "quit"));
        return items;
    }

    static readonly TopBarItem[] NotRunning = { new("item", "Window layouts are not running", Enabled: false) };
    /// model.js MANTLE_MENU (the glyph's menu); "Keep the top bar over full-screen games" is checked from the state.
    public static IReadOnlyList<TopBarItem> MantleMenu(bool overGames = false, bool peeking = false) => new TopBarItem[]
    {
        new("item", "About Mantle", "about"),
        new("item", "Mantle Settings…", "settings", Key: "Win+I"),
        new("item", "Windows Settings…", "winSettings"),
        new("item", "Keep the top bar over full-screen games", "overGames", Checked: overGames),
        peeking ? new("item", "Show top bar", "show", Key: "Win+Alt+T") : new("item", "Hide top bar", "hide", Key: "Win+Alt+T"),
        new("sep"),
        new("item", "Layouts", "layouts", Items: NotRunning, Mn: "y"),
        new("item", "Save layout…", "layoutsSave", Items: NotRunning, Mn: "v"),
        new("sep"),
        new("item", "Lock", "power", Power: "lock"),
        new("item", "Sleep", "power", Power: "sleep"),
        new("item", "Restart…", "power", Power: "restart", Confirm: "Click again to restart"),
        new("item", "Shut down…", "power", Power: "shutdown", Confirm: "Click again to shut down"),
    };

    /// Separators trimmed at both ends and never doubled (model.js cleanItems), on the snapshot's JSON rows.
    public static List<JsonObject> CleanItems(JsonArray? items)
    {
        var o = new List<JsonObject>();
        foreach (var n in items ?? new JsonArray())
        {
            if (n is not JsonObject it) continue;
            if (S(it, "t") == "sep") { if (o.Count > 0 && S(o[^1], "t") != "sep") o.Add(it); continue; }
            o.Add(it);
        }
        while (o.Count > 0 && S(o[^1], "t") == "sep") o.RemoveAt(o.Count - 1);
        return o;
    }

    /// TOP-018..023: the Mantle glyph, then (with a focus) the bold app name, then its own menus, or "Window" for an
    /// app without any (never for the desktop). Never an empty bar.
    public static IReadOnlyList<TopBarTitle> Titles(JsonObject? f, JsonObject? menu, bool overGames = false, bool peeking = false)
    {
        var o = new List<TopBarTitle> { new("mantle", "Mantle", Glyph: true, Items: MantleMenu(overGames, peeking)) };
        if (f == null) return o;
        o.Add(new("app", AppName(f), Bold: true, Items: AppMenu(f)));
        var src = menu?["items"] as JsonArray;
        if (src is { Count: > 0 })
        {
            var clean = CleanItems(src);
            for (int i = 0; i < clean.Count; i++)
            {
                var it = clean[i];
                if (S(it, "t") != "item") continue;
                var label = S(it, "label");
                bool en = !(it.TryGetPropertyValue("enabled", out var e) && e is JsonValue ev && ev.TryGetValue<bool>(out var eb) && !eb);
                int? pos = it.TryGetPropertyValue("pos", out var p) && p is JsonValue pv && pv.TryGetValue<int>(out var pi) ? pi : null;
                o.Add(new("m" + i.ToString(CultureInfo.InvariantCulture), string.IsNullOrEmpty(label) ? "(untitled)" : label, Enabled: en, Pos: pos, Source: S(menu, "source")));
            }
        }
        else if (!B(f, "shell"))
            o.Add(new("window", "Window", Items: AppMenu(f).Where(i => i.Act is "minimize" or "zoom" or "ontop").ToArray()));
        return o;
    }

    /// TOP-024/025 (model.js fitTitles): w[0] the glyph, w[1] the app name, w[2..] its menus (natural widths); the app
    /// name gives way first (down to appMin), then menus go into "»" from the right, and room left over goes back to
    /// the app name. A 1 px margin absorbs 125 % / 150 % rounding.
    public static TopBarFit FitTitles(IReadOnlyList<double> w, double moreW, double avail, double appMin = AppMin)
    {
        int n = w.Count; double sum = 0, room = avail - 1;
        for (int i = 0; i < n; i++) sum += w[i];
        if (n < 2 || sum <= room) return new(n, n, null);
        double menus = sum - w[0] - w[1], minApp = Math.Min(w[1], appMin);
        if (w[0] + minApp + menus <= room) return new(n, n, Math.Floor(room - w[0] - menus));
        double used = w[0] + minApp + moreW; int shown = 2;
        while (shown < n && used + w[shown] <= room) { used += w[shown]; shown++; }
        double appW = Math.Floor(Math.Max(24, Math.Min(w[1], minApp + (room - used))));
        return new(n, shown, appW < w[1] ? appW : null);
    }

    // ---- stats: fixed decimals, so a figure never changes width
    static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
    /// JS Math.round (halves up, toward +inf).
    public static double JsRound(double v) => Math.Floor(v + 0.5);
    /// JS toFixed for these magnitudes: half away from zero on the decimal string.
    static string Fixed(double v, int dec) => Math.Round(v, dec, MidpointRounding.AwayFromZero).ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static string Pct(double? n) => n is double v ? JsRound(Math.Max(0, Math.Min(100, v))).ToString(CultureInfo.InvariantCulture) + "%" : "--";
    public static string Gb(double? bytes) => bytes is double b ? Fixed(b / 1073741824, 1) : "--";
    public static string Rate(double? bps)
    {
        if (bps is not double b || b < 0) return "--";
        if (b < 1000 * 1024) return Fixed(b / 1024, 0) + " KB/s";
        if (b < 100 * 1048576) return Fixed(b / 1048576, 1) + " MB/s";
        return Fixed(b / 1048576, 0) + " MB/s";
    }
    public static string PingMs(double? ms)
    {
        if (ms is not double m || m < 0) return "--";
        var r = JsRound(m);
        return r < 1 ? "<1 ms" : r.ToString(CultureInfo.InvariantCulture) + " ms";
    }

    /// CPU, RAM, GPU, NET, PING (model.js statCells), from one topbarstats sample.
    public static IReadOnlyList<TopBarCell> StatCells(JsonObject? s)
    {
        var cpu = s?["cpu"] as JsonObject; var mem = s?["mem"] as JsonObject; var gpu = s?["gpu"] as JsonObject; var net = s?["net"] as JsonObject;
        string? R(JsonObject? o, string dflt) => S(o, "reason") ?? dflt;
        double? cp = Num(cpu?["pct"]), mu = Num(mem?["used"]), gp = Num(gpu?["pct"]), nd = Num(net?["down"]);
        return new[]
        {
            new TopBarCell("cpu", "CPU", Pct(cp), cp == null ? R(cpu, "no CPU reading yet") : null),
            new TopBarCell("mem", "RAM", mu == null ? "--" : Gb(mu) + "/" + Gb(Num(mem?["total"])) + " GB", mu == null ? R(mem, "no memory reading yet") : null),
            new TopBarCell("gpu", "GPU", Pct(gp), gp == null ? R(gpu, "no GPU reading yet") : null),
            new TopBarCell("net", "NET", nd == null ? "--" : "↓" + Rate(nd) + " ↑" + Rate(Num(net?["up"])), nd == null ? R(net, "no network reading yet") : null),
            PingCell(s?["ping"] as JsonObject),
        };
    }

    public static TopBarCell PingCell(JsonObject? p)
    {
        var ms = Num(p?["ms"]);
        if (S(p, "state") == "ok" && ms is double m && m >= 0)
        {
            var v = PingMs(m); bool hi = JsRound(m) >= PingWarnMs;
            var name = ((S(p, "label") is { Length: > 0 } l ? l + " " : "") + (S(p, "server") ?? "")).Trim();
            return new("ping", "PING", v, null, hi, (name.Length > 0 ? name : "Ping") + ", " + v + (hi ? " (high)" : ""));
        }
        return new("ping", "PING", "--", S(p, "reason") ?? (S(p, "state") != null ? "no reply yet" : "no ping reading yet"));
    }

    /// The notes button's tooltip and marks from notes.state {open, count, saved:{ok, reason}} (model.js notesButton).
    public static (bool Open, bool Bad, string Title) NotesButton(JsonObject? st)
    {
        var saved = st?["saved"] as JsonObject;
        bool bad = saved != null && saved["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var okb) && !okb;
        int count = st?["count"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0;
        string title = bad ? "Quick notes: NOT saved: " + (S(saved, "reason") ?? "no reason came back")
            : "Quick notes (Win+Alt+N)" + (count > 0 ? " · " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " note" : " notes") : "");
        return (B(st, "open"), bad, title);
    }
}
