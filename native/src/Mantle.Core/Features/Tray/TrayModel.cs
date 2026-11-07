using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Tray;

// main.js <mainrel:tray> and lib/tray-batch.js, PURE: the tray menu as words and order only. The Win32
// icon (Mantle.App/Shell/Tray) draws whatever this returns and attaches the clicks by id.

/// <summary>One problem as the state's problems() strip gives it: the piece's name and its reason.</summary>
public sealed record TrayProblem(string Piece, string Reason);

public enum TrayItemKind { Normal, Checkbox, Separator, Submenu }

/// <summary>One menu row. <paramref name="Id"/> is where the click attaches (launcher, settings, quit, ...).</summary>
public sealed record TrayItem(TrayItemKind Kind, string Label = "", string? Id = null, bool Enabled = true, bool Checked = false, string? Accelerator = null, IReadOnlyList<TrayItem>? Submenu = null)
{
    public static readonly TrayItem Separator = new(TrayItemKind.Separator);
    public static TrayItem Text(string label) => new(TrayItemKind.Normal, label, Enabled: false);
}

/// <summary>Everything the menu shows (main.js trayTemplate's o).</summary>
public sealed record TrayInputs(
    bool Test, bool Paused, IReadOnlyList<TrayProblem> Problems, string? Recovery,
    string SidecarStatus, bool SidecarGaveUp, bool LauncherKey, bool KillSwitchKey, bool HasSettings,
    IReadOnlyList<TrayItem> FeatureItems, string? Update);

public sealed record TrayHealth(string Head, IReadOnlyList<string> Lines, string Tip);

public static class TrayModel
{
    static readonly Dictionary<string, string> PieceNames = new(StringComparer.Ordinal)
    {
        ["sidecar"] = "Mantle's link to Windows", ["taskbar"] = "Taskbar auto-hide", ["screen space"] = "Room for the bar",
        ["desktop layer"] = "Desktop", ["Win key"] = "Win key", ["tray"] = "Notification-area icons", ["taskbar watch"] = "Keeping the taskbar tucked away",
        ["edge clip"] = "Bottom-edge guard", ["taskbar hiding"] = "Hiding the Windows taskbar", ["window glass"] = "Glass effect",
        ["shortcuts"] = "Keyboard shortcuts", ["notifications"] = "Notifications", ["Alt+Tab"] = "Alt+Tab",
    };

    /// A state piece's name in words ("edge clip" -> "Bottom-edge guard", "bar backdrop" -> "Bar background").
    public static string PieceWords(string? piece)
    {
        var p = piece ?? "";
        if (PieceNames.TryGetValue(p, out var w)) return w;
        var bd = Regex.Match(p, "^(.+) backdrop$");
        if (bd.Success) return char.ToUpperInvariant(bd.Groups[1].Value[0]) + bd.Groups[1].Value[1..] + " background";
        return p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : "Something";
    }

    static readonly (Regex Re, MatchEvaluator To)[] Jargon =
    {
        (new Regex(@"\b(?:the )?sidecar's\b", RegexOptions.IgnoreCase), _ => "Mantle's"),
        (new Regex(@"\b(?:the )?sidecar\b", RegexOptions.IgnoreCase), _ => "Mantle's link to Windows"),
        (new Regex(@"\b(the )?(?:low-level )?keyboard hooks?\b", RegexOptions.IgnoreCase), m => $"{m.Groups[1].Value}keyboard watcher"),
        (new Regex(@"\bWin[- ]key hooks?\b", RegexOptions.IgnoreCase), _ => "Win key watcher"),
        (new Regex(@"\bhooks?\b", RegexOptions.IgnoreCase), _ => "keyboard watcher"),
        (new Regex(@"\s*\((?:HRESULT |error )?0x[0-9a-f]{8}\)", RegexOptions.IgnoreCase), _ => ""),
        (new Regex(@"\b(?:HRESULT )?0x[0-9a-f]{8}\b", RegexOptions.IgnoreCase), _ => "an error"),
        (new Regex(@"\bhwnd\b", RegexOptions.IgnoreCase), _ => "window"),
        (new Regex(@"\brenderer\b", RegexOptions.IgnoreCase), _ => "page"),
        (new Regex(@"\bAUMID\b", RegexOptions.IgnoreCase), _ => "app id"),
        (new Regex(@"\bdryRun\b"), _ => "test run"),
    };

    /// Plain words for every line a person reads on the menu or a balloon (main.js trayWords).
    public static string Words(string? text)
    {
        var s = text ?? "";
        foreach (var (re, to) in Jargon) s = re.Replace(s, to);
        return s;
    }

    /// One short line: the first clause of a reason, capped (main.js shortReason).
    public static string ShortReason(string? text, int max = 70)
    {
        var s = Words(Regex.Replace(text ?? "", @"\s+", " ").Trim());
        var cut = Regex.Match(s, @"(; |\. | -- | \()");
        if (cut.Success && cut.Index > 12) s = s[..cut.Index];
        s = Regex.Replace(s, "[.;:,]$", "");
        if (s.Length <= max) return s;
        return Regex.Replace(s[..(max - 1)], @"\s+\S*$", "") + "…";
    }

    /// The first row, the rows under it, and the icon's hover text (main.js trayHealth).
    public static TrayHealth Health(IReadOnlyList<TrayProblem>? problems, bool test, bool paused, string sidecarStatus, bool gaveUp)
    {
        var list = problems ?? Array.Empty<TrayProblem>();
        var name = test ? "Mantle (test run)" : "Mantle";
        if (paused) return new("Paused: Windows is back to normal", new[] { "Restart Mantle to use it again" }, $"{name}\nPaused: Windows is back to normal");
        if (list.Count == 0) return new("Mantle is running normally", Array.Empty<string>(), $"{name}\nRunning normally");
        // ONE cause, one line: while the link to Windows is down, every piece that waits on it says so.
        var root = list.FirstOrDefault(p => p.Piece == "sidecar");
        var rootWhy = root?.Reason ?? "";
        var waiting = root != null && rootWhy.Length > 0 ? list.Where(p => p != root && (p.Reason ?? "").EndsWith(rootWhy, StringComparison.Ordinal)).ToList() : new List<TrayProblem>();
        var rest = list.Where(p => p != root && !waiting.Contains(p)).ToList();
        string? rootLine = null;
        if (root != null)
        {
            var state = sidecarStatus == "starting" ? "is starting" : gaveUp ? "stopped" : "stopped; trying again";
            rootLine = $"Mantle's link to Windows {state}" + (waiting.Count > 0 ? $" ({waiting.Count} {(waiting.Count == 1 ? "thing waits" : "things wait")} on it)" : "");
        }
        var count = (root != null ? 1 : 0) + rest.Count;
        var head = $"{count} {(count == 1 ? "thing needs" : "things need")} a look";
        var shown = new List<string>();
        if (rootLine != null) shown.Add(rootLine);
        shown.AddRange(rest.Take(rootLine != null ? 3 : 4).Select(p => $"{PieceWords(p.Piece)}: {ShortReason(p.Reason, 64)}"));
        var hidden = count - shown.Count;
        if (hidden > 0) shown.Add($"…and {hidden} more, listed in Mantle Settings");
        var names = string.Join(", ", (root != null ? new[] { "Mantle's link to Windows" } : Array.Empty<string>()).Concat(rest.Select(p => PieceWords(p.Piece))));
        var tip = $"{name}\n{head}: {names}";
        return new(head, shown, tip.Length > 120 ? tip[..119] + "…" : tip);
    }

    /// <summary>The whole menu (main.js trayTemplate). There is deliberately never a "hide the Windows taskbar" item.</summary>
    public static IReadOnlyList<TrayItem> Template(TrayInputs o)
    {
        var h = Health(o.Problems, o.Test, o.Paused, o.SidecarStatus, o.SidecarGaveUp);
        var out_ = new List<TrayItem> { TrayItem.Text(h.Head) };
        foreach (var l in h.Lines) out_.Add(TrayItem.Text($"    {l}"));
        if (!o.Paused && o.SidecarGaveUp) out_.Add(new(TrayItemKind.Normal, "Reconnect Mantle to Windows", "retry"));
        if (o.Recovery != null) out_.Add(TrayItem.Text($"    {o.Recovery}"));
        if (o.Update != null) out_.Add(new(TrayItemKind.Normal, o.Update, "update"));
        out_.Add(TrayItem.Separator);
        out_.Add(new(TrayItemKind.Normal, "Open launcher", "launcher", Accelerator: o.LauncherKey ? "Alt+Space" : null));
        if (o.HasSettings) out_.Add(new(TrayItemKind.Normal, "Open Mantle Settings", "settings", Accelerator: "Super+I"));
        out_.Add(new(TrayItemKind.Normal, "Wallpapers…", "wallpapers"));
        var feats = Plain(o.FeatureItems ?? Array.Empty<TrayItem>());
        if (feats.Count > 0) out_.Add(new(TrayItemKind.Submenu, "Features", Submenu: feats));
        out_.Add(TrayItem.Separator);
        out_.Add(new(TrayItemKind.Normal, "Copy diagnostic report", "report"));
        out_.Add(new(TrayItemKind.Normal, o.Paused ? "Restart Mantle to resume" : "Restart Mantle", "restart"));
        if (!o.Paused) out_.Add(new(TrayItemKind.Normal, "Put Windows back (keep Mantle open)", "restore"));
        out_.Add(TrayItem.Separator);
        out_.Add(new(TrayItemKind.Normal, "Quit Mantle", "quit", Accelerator: o.KillSwitchKey ? "Ctrl+Alt+Shift+Backspace" : null));
        return out_;
    }

    static IReadOnlyList<TrayItem> Plain(IReadOnlyList<TrayItem> items) =>
        items.Select(i => i with { Label = Words(i.Label), Submenu = i.Submenu == null ? null : Plain(i.Submenu) }).ToArray();

    /// <summary>The feature host's rows as the Features submenu: a checkbox each, and a grey reason line
    /// under one that is on and failing (FeatureHost.TrayRows). Ids are "feature:&lt;name&gt;".</summary>
    public static IReadOnlyList<TrayItem> FeatureItems(IEnumerable<FeatureTrayRow> rows)
    {
        var out_ = new List<TrayItem>();
        foreach (var r in rows)
        {
            out_.Add(new(TrayItemKind.Checkbox, r.Label, $"feature:{r.Name}", r.Enabled, r.Checked));
            if (r.ReasonLine != null) out_.Add(TrayItem.Text($"      {r.ReasonLine}"));
        }
        return out_;
    }

    /// What the menu LOOKS like: equal signatures need no rebuild (main.js menuSignature).
    public static string Signature(IReadOnlyList<TrayItem> items)
    {
        var sb = new StringBuilder();
        void Add(IReadOnlyList<TrayItem> list)
        {
            foreach (var i in list)
            {
                sb.Append((int)i.Kind).Append('|').Append(i.Label).Append('|').Append(i.Id).Append('|').Append(i.Enabled).Append('|').Append(i.Checked).Append('|').Append(i.Accelerator);
                if (i.Submenu != null) { sb.Append('['); Add(i.Submenu); sb.Append(']'); }
                sb.Append('\n');
            }
        }
        Add(items);
        return sb.ToString();
    }

    /// The --test-tray lines: <c>label [accelerator] (text) (N inside)</c>, nested rows indented.
    public static IReadOnlyList<string> Describe(IReadOnlyList<TrayItem> items, string indent = "")
    {
        var out_ = new List<string>();
        foreach (var i in items)
        {
            if (i.Kind == TrayItemKind.Separator) { out_.Add($"{indent}---"); continue; }
            var line = indent + i.Label.Trim() + (i.Accelerator != null ? $" [{i.Accelerator}]" : "") + (!i.Enabled && i.Id == null ? " (text)" : "")
                + (i.Kind == TrayItemKind.Checkbox ? (i.Checked ? " (on)" : " (off)") : "") + (i.Submenu != null ? $" ({i.Submenu.Count} inside)" : "");
            out_.Add(line);
            if (i.Submenu != null) out_.AddRange(Describe(i.Submenu, indent + "  "));
        }
        return out_;
    }
}

/// <summary>lib/tray-batch.js: the menu is rebuilt at most once per window, and only when its inputs moved.</summary>
public sealed class TrayBatch
{
    readonly HostClock _clock;
    readonly TimeSpan _window;
    readonly Func<object?[]> _key;
    readonly Action _run;
    readonly Action<Exception, string> _onError;
    IDisposable? _timer;
    long _lastRun = long.MinValue / 2;
    object?[]? _lastKey;

    public TrayBatch(HostClock clock, Func<object?[]> key, Action run, Action<Exception, string>? onError = null, TimeSpan? window = null)
    {
        _clock = clock; _key = key; _run = run; _onError = onError ?? ((_, _) => { }); _window = window ?? TimeSpan.FromMilliseconds(250);
    }

    public int Requests { get; private set; }
    public int Runs { get; private set; }
    public int Skipped { get; private set; }
    public int Errors { get; private set; }
    public bool Pending => _timer != null;

    /// Element by element, by identity for objects and by value for strings and numbers.
    public static bool SameKey(object?[]? a, object?[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (x is string or ValueType || y is string or ValueType) { if (!Equals(x, y)) return false; }
            else if (!ReferenceEquals(x, y)) return false;
        }
        return true;
    }

    object?[]? ReadKey()
    {
        try { return _key(); } catch (Exception e) { _onError(e, "key"); return null; }
    }

    void Build(object?[]? k)
    {
        try { _run(); _lastKey = k; Runs++; }
        catch (Exception e) { _lastKey = null; Errors++; _onError(e, "run"); }
    }

    void Fire()
    {
        _timer = null;
        _lastRun = _clock.NowMs;
        var k = ReadKey();
        if (k != null && SameKey(k, _lastKey)) { Skipped++; return; }
        Build(k);
    }

    /// A state change asks; the rebuild comes on a later turn, never inside the call that asked.
    public void Request()
    {
        Requests++;
        if (_timer != null) return;
        var wait = Math.Max(0, (long)_window.TotalMilliseconds - (_clock.NowMs - _lastRun));
        _timer = _clock.Later(TimeSpan.FromMilliseconds(wait), Fire);
    }

    /// Rebuild now (a click, a pause), folding any pending batch into it.
    public void Force()
    {
        _timer?.Dispose(); _timer = null;
        _lastRun = _clock.NowMs;
        Build(ReadKey());
    }

    public void Cancel() { _timer?.Dispose(); _timer = null; }
}
