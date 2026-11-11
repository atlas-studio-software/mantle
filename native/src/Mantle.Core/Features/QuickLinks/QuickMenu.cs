using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Search;

namespace Mantle.Core.Features.QuickLinks;

/// <summary>What one key or one activation in Quick links asks the launcher to do.</summary>
/// <param name="Kind">none | select | open | power | sub | back | page | armed | close</param>
/// <param name="Id">the row id to select (q:&lt;id&gt; / qp:&lt;id&gt;), when there is one</param>
/// <param name="Arg">open: the link id; power: the action; page: search | run</param>
public sealed record QuickStep(string Kind, string? Id = null, string? Arg = null, bool Confirmed = false, bool Activate = false)
{
    public static readonly QuickStep None = new("none");
}

/// <summary>
/// The launcher's Quick links mode (ui/launcher/launcher.js quickRows / quickKey / activateQuick / activatePower / once,
/// lane keysrun), pure: the rows in their groups, the submenu, the underlined-letter keys, ask-first arming (the first
/// press arms, the same key or Enter within <see cref="ArmMs"/> does it; nothing confirms within <see cref="ArmMinMs"/>,
/// so a double click is not two decisions) and the once-per-decision guard (<see cref="FireGapMs"/>). The time is passed
/// in, so the tests never wait. The launcher draws what this returns and sends only a row id.
/// </summary>
public sealed class QuickMenu
{
    public const int ArmMs = 5000, ArmMinMs = 250, FireGapMs = 500;
    public const string Placeholder = "Quick links: press a letter";
    public const string PowerSub = "power";
    public static readonly IReadOnlyDictionary<int, string> GroupLabels = new Dictionary<int, string> { [1] = "Tools", [2] = "Open", [3] = "Power and desktop" };
    public const string SubLabel = "Shut down or sign out";
    static readonly Dictionary<string, string> PowerGlyph = new() { ["signout"] = "signout", ["sleep"] = "sleep", ["hibernate"] = "hibernate", ["shutdown"] = "power", ["restart"] = "restart" };

    /// <summary>"power" while the Shut down or sign out submenu is up, else null.</summary>
    public string? Sub { get; private set; }
    public string? ArmedId { get; private set; }
    long _armedAt;
    string? _inFlight, _lastKey;
    long _lastAt = long.MinValue / 2;
    /// <summary>The line under a row after it ran (a dry run, a fallback, a refusal), until the next move.</summary>
    public (string Id, string Text, bool Ok)? Note { get; private set; }

    /// <summary>A fresh open (openSpecial): the main list, nothing armed, no note.</summary>
    public void Reset() { Sub = null; Disarm(); Note = null; }

    public bool Disarm() { if (ArmedId == null) return false; ArmedId = null; return true; }

    /// <summary>The armed row's time ran out (the launcher's one-shot timer, only while armed).</summary>
    public bool ArmExpired(long now) => ArmedId != null && now - _armedAt >= ArmMs && Disarm();

    /// <summary>The selection moved to another row: an armed row that is not it is disarmed (launcher.js select / move).</summary>
    public void Moved(string? id) { if (ArmedId != null && id != ArmedId) Disarm(); }

    public void SetNote(string id, string text, bool ok) => Note = (id, text, ok);

    /// <summary>The rows now, grouped as Electron's quickRows: Tools / Open / Power and desktop, or the submenu's one group.</summary>
    public LauncherGroups.Result Rows()
    {
        var groups = new List<LauncherGroup>();
        var flat = new List<LauncherRow>();
        string? label = null;
        List<LauncherRow>? cur = null;
        void Add(string l, LauncherItem it)
        {
            if (cur == null || label != l) { cur = new List<LauncherRow>(); label = l; groups.Add(new LauncherGroup("quick" + groups.Count, l, cur, null)); }
            var row = new LauncherRow(it, null);
            cur.Add(row); flat.Add(row);
        }
        if (Sub == PowerSub)
            foreach (var p in WinXLinks.Power)
            {
                var parts = WinXLinks.Parse(p.Label);
                string id = "qp:" + p.Id;
                Add(SubLabel, new LauncherItem
                {
                    Id = id, Kind = "qpower", Name = parts.Text, Path = p.Action, Glyph = PowerGlyph.GetValueOrDefault(p.Action),
                    Confirm = p.Confirm ? ConfirmWords(parts) : null,
                    Subtitle = SubtitleOf(id, ""),
                });
            }
        else
            foreach (var l in WinXLinks.Links)
            {
                string id = "q:" + l.Id;
                Add(GroupLabels[l.Group], new LauncherItem { Id = id, Kind = "quick", Name = WinXLinks.Parse(l.Label).Text, Path = l.Id, Subtitle = SubtitleOf(id, l.Sub ?? "") });
            }
        return new LauncherGroups.Result(groups, flat);
    }

    /// The row's second line: its note, else the ask-first words while armed, else its own.
    string SubtitleOf(string id, string own)
    {
        if (Note is { } n && n.Id == id) return n.Text;
        if (ArmedId == id)
        {
            var p = WinXLinks.Power.FirstOrDefault(x => "qp:" + x.Id == id);
            if (p != null) { var parts = WinXLinks.Parse(p.Label); return ConfirmWords(parts); }
        }
        return own;
    }

    /// <summary>quickRows' confirm words: "Press U again to shut down".</summary>
    public static string ConfirmWords(QuickLabel p) => "Press " + p.Letter.ToUpperInvariant() + " again to " + p.Text.ToLowerInvariant();

    /// <summary>The underlined letter of a row's label, for drawing (null for a row with none).</summary>
    public static QuickLabel? LabelOf(LauncherItem it) => it.Kind switch
    {
        "quick" => WinXLinks.ById(it.Path) is { } l ? WinXLinks.Parse(l.Label) : null,
        "qpower" => WinXLinks.Power.FirstOrDefault(p => p.Action == it.Path) is { } p ? WinXLinks.Parse(p.Label) : null,
        _ => null,
    };

    /// <summary>
    /// A key in Quick links (launcher.js specialKey, mode quick). <paramref name="key"/> is "Escape", "Left", "Right", or one
    /// printable character; <paramref name="repeat"/> an auto-repeat (a held letter picks once); <paramref name="selId"/> the
    /// selected row's id. A letter selects its row and activates it (<see cref="QuickStep.Activate"/>).
    /// </summary>
    public QuickStep Key(string key, bool repeat, string? selId)
    {
        if (key == "Escape") return ArmedId == null && Sub != null ? Back() : ArmedId != null ? DisarmStep() : new QuickStep("close");
        if (key == "Left") return Sub != null ? Back() : QuickStep.None;
        if (key == "Right") return Sub == null && selId == "q:shutdown" ? Open() : QuickStep.None;
        if (key.Length != 1) return QuickStep.None;
        if (repeat) return new QuickStep("none", Arg: "held");
        if (Sub == PowerSub)
        {
            var p = WinXLinks.PowerByKey(key);
            return p == null ? QuickStep.None : new QuickStep("select", "qp:" + p.Id, Activate: true);
        }
        var l = WinXLinks.ByKey(key);
        return l == null ? QuickStep.None : new QuickStep("select", "q:" + l.Id, Activate: true);
    }

    QuickStep DisarmStep() { Disarm(); return new QuickStep("back", Arg: "disarmed"); }

    QuickStep Back() { Sub = null; Disarm(); Note = null; return new QuickStep("back"); }

    QuickStep Open() { Sub = PowerSub; Note = null; return new QuickStep("sub", "qp:" + WinXLinks.Power[0].Id); }

    /// <summary>
    /// Enter, a click, or a letter on a row (activateQuick / activatePower, through once()). <paramref name="now"/> in ms.
    /// The answer says what to send: open {link} / power {action, confirmed}; or sub / page / armed / none (held back).
    /// </summary>
    public QuickStep Activate(LauncherItem? it, long now)
    {
        if (it == null) return QuickStep.None;
        if (it.Kind == "quick")
        {
            var l = WinXLinks.ById(it.Path);
            if (l == null) return QuickStep.None;
            if (l.Page == PowerSub) return Open();
            if (l.Page is "search" or "run") return new QuickStep("page", it.Id, l.Page);
            return Once(it.Id, "open", now) ? new QuickStep("open", it.Id, l.Id) : new QuickStep("none", it.Id, "guard");
        }
        if (it.Kind == "qpower")
        {
            bool asks = it.Confirm != null;
            if (asks)
            {
                if (ArmedId != it.Id) { ArmedId = it.Id; _armedAt = now; Note = null; return new QuickStep("armed", it.Id, it.Path); }
                if (now - _armedAt < ArmMinMs) return new QuickStep("none", it.Id, "too soon");   // a double click is not two decisions
                if (now - _armedAt >= ArmMs) { ArmedId = it.Id; _armedAt = now; return new QuickStep("armed", it.Id, it.Path); }
            }
            bool confirmed = asks && ArmedId == it.Id;
            Disarm();
            return Once(it.Id, "power", now) ? new QuickStep("power", it.Id, it.Path, confirmed) : new QuickStep("none", it.Id, "guard");
        }
        return QuickStep.None;
    }

    /// <summary>The answer to a sent action came back: the guard lets the row be sent again (once()'s done).</summary>
    public void Done(string id, string action) { if (_inFlight == id + "|" + action) _inFlight = null; }

    bool Once(string id, string action, long now)
    {
        string key = id + "|" + action;
        if (_inFlight == key) return false;
        if (_lastKey == key && now - _lastAt < FireGapMs) return false;
        _inFlight = key; _lastKey = key; _lastAt = now;
        return true;
    }

    /// <summary>The hint bar's keys in this mode (launcher.js renderHint, mode quick).</summary>
    public IReadOnlyList<(string Key, string Word)> Hint() => [("Letter", "Open"), ("Enter", "Open"), ("Esc", Sub != null ? "Back" : "Close")];
}
