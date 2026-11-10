using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features.Icons;
using Mantle.Core.Features.Pinned;

namespace Mantle.Core.Features.WindowList;

/// <summary>One cell of the strip (main.js#buildPinned's item, the bar.pinned reply). Immutable: handed to the UI.</summary>
public sealed record TaskItem(
    string Id,
    string Name,
    // "pinned" or "running" (running-but-unpinned apps sit after the divider)
    string Kind,
    string? Exe,
    string? Aumid,
    // a PNG data URL, or null with IconReason saying why (the tooltip says it, BAR-304)
    string? Icon,
    string? IconReason,
    IReadOnlyList<TaskWindow> Windows)
{
    /// The pin behind a pinned item (its shortcut, target, args, AUMID): what a click launches from.
    public Pin? Pin { get; init; }

    public bool Running => Windows.Count > 0;
    public bool Active => Windows.Any(RunningWindows.IsActive);
}

/// <summary>The whole strip (the bar.pinned reply): never thrown; ok false carries whatever half worked plus the reason.</summary>
public sealed record TaskList(bool Ok, string? Reason, IReadOnlyList<TaskItem> Items)
{
    public static readonly TaskList Empty = new(true, null, Array.Empty<TaskItem>());
}

/// <summary>main.js#buildPinned (BAR-286..289, BAR-298, BAR-302..304): pins claim their running group by AUMID or exe,
/// then every running app no pin claimed, in Windows' order (or this session's dragged order).</summary>
public static class TaskStripBuilder
{
    /// <summary>
    /// ui/bar/bar.js renderWindows: every windows push re-marks which window is the active one AT ONCE (the accent
    /// line follows the window in front), while the rest of the strip waits for the rate-limited rebuild. Native
    /// also takes each window's minimised flag from the push. The same instance comes back when nothing changed.
    /// </summary>
    public static TaskList MarkActive(TaskList list, IReadOnlyList<WinEntry> windows)
    {
        var by = new Dictionary<string, WinEntry>(StringComparer.Ordinal);
        foreach (var w in windows) by[w.Hwnd] = w;
        bool any = false;
        var items = new List<TaskItem>(list.Items.Count);
        foreach (var it in list.Items) {
            bool changed = false;
            var ws = new TaskWindow[it.Windows.Count];
            for (int k = 0; k < ws.Length; k++) {
                var w = it.Windows[k];
                bool known = by.TryGetValue(w.Hwnd, out var e);
                bool act = known && e!.Foreground && !e.Minimized;
                bool min = known ? e!.Minimized : w.Minimized;
                ws[k] = act == w.Active && min == w.Minimized ? w : w with { Active = act, Minimized = min };
                changed |= !ReferenceEquals(ws[k], w);
            }
            items.Add(changed ? it with { Windows = ws } : it);
            any |= changed;
        }
        return any ? list with { Items = items } : list;
    }

    public sealed class Input
    {
        /// The running windows (already without Mantle's own), or null when they could not be read.
        public IReadOnlyList<WinEntry>? Running;
        public string? RunningProblem;
        public PinnedResult? Pins;
        /// lib/pinned.js did not run at all (for example a test run with no scratch pin folder): said as a note.
        public string? PinsNote;
        public PinOverlay? Overlay;
        public string? OverlayProblem;
        public IconResolver? Icons;
        /// This session's dragged order of the running-only apps (lib/features/_jumplist-pins.js#orderRunning).
        public RunningOrder? RunningOrder;
        /// Native, better than Electron (BAR-289): running-only apps keep their first-seen place for the session.
        public FirstSeenOrder? FirstSeen;
    }

    public static async ValueTask<TaskList> BuildAsync(Input i)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        bool runningRead = i.Running != null;
        if (i.RunningProblem != null) problems.Add(i.RunningProblem);
        var groups = RunningWindows.Group(i.Running ?? Array.Empty<WinEntry>());

        var pins = new List<Pin>();
        if (i.Pins != null) {
            pins = i.Pins.Items.Select(p => p.Copy()).ToList();
            if (!i.Pins.Ok) problems.Add("the pinned list is incomplete: " + (i.Pins.Reason ?? "lib/pinned.js did not say why"));
            else if (i.Pins.Reason != null) notes.Add(i.Pins.Reason);
        } else if (i.PinsNote != null) notes.Add(i.PinsNote);
        pins = pins.Select((p, k) => (p, k)).OrderBy(x => x.p.Order).ThenBy(x => x.k).Select(x => x.p).ToList();
        if (i.OverlayProblem != null) problems.Add(i.OverlayProblem);
        else if (i.Overlay != null) pins = i.Overlay.Apply(pins);

        var items = new List<TaskItem>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var byAumid = new Dictionary<string, RunGroup>(StringComparer.Ordinal);
        var byExe = new Dictionary<string, RunGroup>(StringComparer.Ordinal);
        foreach (var g in groups) {
            if (g.Aumid != null) byAumid.TryAdd(g.Aumid, g);
            if (g.Exe != null) byExe.TryAdd(g.Exe, g);
        }
        void ClaimA(string? v) { if (v != null) claimed.Add("a:" + v); }
        void ClaimE(string? v) { if (v != null) claimed.Add("e:" + v); }
        bool IsClaimed(RunGroup g) => (g.Aumid != null && claimed.Contains("a:" + g.Aumid)) || (g.Exe != null && claimed.Contains("e:" + g.Exe));

        foreach (var pin in pins) {
            string? pinAumid = TaskJson.Low(pin.Aumid), exe = TaskJson.Low(pin.Exe);
            // one item per app, first pin wins
            if ((pinAumid != null && claimed.Contains("a:" + pinAumid)) || (exe != null && claimed.Contains("e:" + exe))) continue;
            RunGroup? g = (pinAumid != null && byAumid.TryGetValue(pinAumid, out var ga)) ? ga : (exe != null && byExe.TryGetValue(exe, out var ge)) ? ge : null;
            // claim the PIN's names and the MATCHED GROUP's names (Discord: Update.exe pinned, Discord.exe running)
            ClaimA(pinAumid); ClaimE(exe);
            if (g != null) { ClaimA(g.Aumid); ClaimE(g.Exe); }
            (string? Icon, string? Reason) ico = pin.IconPng != null ? (pin.IconPng, null)
                : i.Icons != null ? await i.Icons.ForAsync(new[] { pin.IconPath, pin.Target, pin.Exe, g?.ExeRaw, pin.Aumid != null ? IconResolver.AppsFolder + pin.Aumid : null }, pin.IconIndex).ConfigureAwait(true)
                : (null, "no icon: Mantle's helper is not connected to the bar");
            string id = !string.IsNullOrEmpty(pin.Id) ? pin.Id : pin.LnkPath ?? (pin.Name.Length > 0 ? pin.Name : "pin:" + items.Count);
            string? pinNote = pin.Reason;
            if (pinNote != null && ico.Icon != null) notes.Add(pin.Name + ": " + pinNote);
            items.Add(new TaskItem(id, pin.Name.Length > 0 ? pin.Name : RunningWindows.NameForExe(pin.Exe, pin.LnkPath), "pinned", exe,
                pinAumid ?? g?.Aumid, ico.Icon, ico.Icon != null ? null : string.Join(" -- ", new[] { pinNote, ico.Reason }.Where(s => !string.IsNullOrEmpty(s))),
                g != null ? g.Windows.ToArray() : Array.Empty<TaskWindow>()) { Pin = pin });
        }

        // everything running no pin claimed, in the order Windows enumerated it (the sidecar's EnumWindows order, i.e.
        // the z-order); FirstSeen below then holds each one where it first appeared (BAR-289, better: than Electron)
        foreach (var g in groups) {
            if (IsClaimed(g)) continue;
            (string? Icon, string? Reason) ico = g.Icon != null ? (g.Icon, null)
                // the sidecar is still reading this app's icon (Windows.cs QueueIcon): none until it arrives, never a guess (BAR-303)
                : g.IconPending ? (null, "still loading")
                : i.Icons != null ? await i.Icons.ForAsync(new[] { g.ExeRaw, g.Aumid != null ? IconResolver.AppsFolder + g.Aumid : null }).ConfigureAwait(true)
                : (null, "no icon: Mantle's helper is not connected to the bar");
            items.Add(new TaskItem("running:" + g.Key, RunningWindows.NameForExe(g.ExeRaw, g.Title), "running", g.Exe, g.Aumid,
                ico.Icon, ico.Icon != null ? null : ico.Reason, g.Windows.ToArray()));
        }

        var said = problems.Concat(notes).ToList();
        if (i.FirstSeen != null) items = i.FirstSeen.Apply(items, runningRead);
        var ordered = i.RunningOrder != null ? i.RunningOrder.Apply(items, it => it.Kind == "running", it => it.Id, prune: runningRead) : items;
        return new TaskList(problems.Count == 0, said.Count > 0 ? string.Join("; ", said) : null, ordered);
    }
}

/// <summary>
/// BAR-289, better than Electron (the user's decision, round 4): main.js#buildPinned lists running-only apps in the
/// sidecar's EnumWindows order, which is the z-order, so in Electron an app jumps to the front of the running-only apps
/// when it is focused and to the back when it is minimised, and a second click on the same spot lands on another app.
/// Native keeps each running-only app where it first appeared this session, as Windows' own taskbar does, so an icon
/// never moves under the pointer. An app that closes loses its place (only when the running list was read cleanly);
/// one that opens goes last. A dragged order (<see cref="RunningOrder"/>) still wins over this.
/// </summary>
public sealed class FirstSeenOrder
{
    readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    long _next;

    public List<TaskItem> Apply(List<TaskItem> items, bool prune)
    {
        if (prune) {
            var here = new HashSet<string>(items.Where(x => x.Kind == "running").Select(x => x.Id), StringComparer.Ordinal);
            foreach (var gone in _seen.Keys.Where(k => !here.Contains(k)).ToList()) _seen.Remove(gone);
        }
        foreach (var it in items) if (it.Kind == "running" && !_seen.ContainsKey(it.Id)) _seen[it.Id] = _next++;
        var run = items.Where(x => x.Kind == "running").OrderBy(x => _seen[x.Id]).ToList();
        int k = 0;
        return items.Select(x => x.Kind == "running" ? run[k++] : x).ToList();
    }
}

/// <summary>What a click on the strip does (main.js 'bar.open' / 'bar.window', BAR-192, BAR-197..202).</summary>
public interface ITaskActions
{
    /// Focus a window (restoring it if minimised). Null = done, else the plain reason.
    ValueTask<string?> FocusAsync(TaskWindow w);
    ValueTask<string?> MinimizeAsync(TaskWindow w);
    ValueTask<string?> CloseAsync(TaskWindow w);
    /// Start the item (a pin's shortcut, else its AUMID); <paramref name="fresh"/> = another copy. Reply as main.js launchItem.
    ValueTask<TaskOpenResult> LaunchAsync(TaskItem item, bool fresh);
}

/// The bar.open / bar.window reply: action list | focus | minimize | close | launched | dry-run.
public sealed record TaskOpenResult(bool Ok, string? Reason, string? Action = null, string? Hwnd = null, IReadOnlyList<TaskWindow>? Windows = null, bool DryRun = false);

public static class TaskOpen
{
    /// main.js 'bar.open': middle-click (fresh) = a new instance; nothing running = launch; one window = the active one
    /// minimises, any other is focused; two or more = no window touched, action 'list' (the bar's own window list).
    public static async ValueTask<TaskOpenResult> OpenAsync(TaskList list, string? id, bool fresh, ITaskActions act, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(id)) return new TaskOpenResult(false, "bar.open needs the id of an item in the bar");
        var item = list.Items.FirstOrDefault(x => x.Id == id);
        if (item == null) return new TaskOpenResult(false, $"the bar asked to open \"{id}\", which is not in the list Mantle last built");
        var wins = item.Windows;
        // one line per click, never the app name (privacy rule): the kind says enough (BAR-205)
        log?.Invoke($"mantle: bar.open ({item.Kind}): {wins.Count} window(s){(fresh ? ", fresh" : "")}{(wins.Count == 1 ? $", active={(wins[0].Active ? "true" : "false")}, hwnd {wins[0].Hwnd}" : "")}");
        if (!fresh && wins.Count >= 2) return new TaskOpenResult(true, null, "list", Windows: wins);
        if (!fresh && wins.Count == 1) {
            var w = wins[0];
            string action = RunningWindows.IsActive(w) ? "minimize" : "focus";
            string? err = action == "focus" ? await act.FocusAsync(w).ConfigureAwait(true) : await act.MinimizeAsync(w).ConfigureAwait(true);
            if (err == null) return new TaskOpenResult(true, null, action, w.Hwnd);
            return new TaskOpenResult(false, $"{item.Name} is running but Mantle could not {action} it: {err}");
        }
        return await act.LaunchAsync(item, fresh).ConfigureAwait(true);
    }

    /// main.js 'bar.window' {hwnd, action}: focus, minimize, close or toggle of one window (the list and the flyout).
    public static async ValueTask<TaskOpenResult> WindowAsync(TaskList list, string? hwnd, string? action, ITaskActions act)
    {
        if (string.IsNullOrEmpty(hwnd) || !ulong.TryParse(hwnd, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong h) || h == 0)
            return new TaskOpenResult(false, "bar.window needs a window handle (hwnd), a positive whole number");
        if (action is not ("focus" or "minimize" or "close" or "toggle"))
            return new TaskOpenResult(false, $"bar.window does not know the action \"{action}\" (it does focus, minimize, close and toggle)");
        var w = list.Items.SelectMany(i => i.Windows).FirstOrDefault(x => x.Hwnd == hwnd) ?? new TaskWindow(hwnd, 0, "", false, false);
        string act2 = action == "toggle" ? (RunningWindows.IsActive(w) ? "minimize" : "focus") : action;
        string? err = act2 switch {
            "focus" => await act.FocusAsync(w).ConfigureAwait(true),
            "minimize" => await act.MinimizeAsync(w).ConfigureAwait(true),
            _ => await act.CloseAsync(w).ConfigureAwait(true),
        };
        return err == null ? new TaskOpenResult(true, null, action, hwnd) : new TaskOpenResult(false, $"could not {action} that window: {err}");
    }

    /// main.js#launchItem's test-run answer (BAR-201): nothing starts, the reply says what would have.
    public static TaskOpenResult DryRun(TaskItem item, bool fresh, Action<string>? log = null)
    {
        string? lnk = item.Pin?.LnkPath ?? item.Pin?.Target ?? item.Pin?.Exe;
        string? aumid = item.Pin?.Aumid ?? item.Aumid;
        string again = fresh ? " another copy of" : "";
        if (lnk == null && aumid == null) return NothingToStart(item);
        string what = lnk != null ? "open " + lnk : @"start shell:AppsFolder\" + aumid;
        log?.Invoke($"mantle: launch dry run (--test): would{again} {what}");
        return new TaskOpenResult(true, $"dry run: would{again} {what}", "dry-run", DryRun: true);
    }

    public static TaskOpenResult NothingToStart(TaskItem item)
        => new(false, $"{item.Name} is not running and Mantle has nothing to start it from -- no shortcut and no app id (it is a running-apps entry, not a pin)");
}
