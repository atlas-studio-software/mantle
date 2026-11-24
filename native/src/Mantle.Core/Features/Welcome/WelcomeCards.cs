using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Welcome;

/// <summary>One line on card 1 (ui/welcome/content.js cards[0].items).</summary>
public sealed record WelcomeItem(string Icon, string Name, string Text);

/// <summary>One row on card 2: keycaps, what they do, an optional note.</summary>
public sealed record WelcomeKey(IReadOnlyList<string> Keys, string What, string? Note = null);

/// <summary>One way back on card 3: text, optional keycaps / code, the text after them, and an optional action.</summary>
public sealed record WelcomeWay(string Name, string Text, IReadOnlyList<string>? Keys = null, string? Code = null, string? After = null, string? Action = null);

/// <summary>One card: id, step name (the footer counter and the step marker), title, lead.</summary>
public sealed record WelcomeCard(string Id, string Step, string Title, string? Lead = null);

/// <summary>One line of the hot-corners block: "&lt;Name&gt;:" and its action label; <c>Key</c> is tl/tr/bl/br or "off".</summary>
public sealed record CornerLine(string Key, string Name, string Action);

/// <summary>The hot-corners block as drawn: its lines, the "how" line (null when off) and whether the live corners are used.</summary>
public sealed record CornersView(IReadOnlyList<CornerLine> Lines, string? How, bool Off, bool Live);

/// <summary>The live corners from <c>corners.state</c>: actions per corner and whether corners are on.</summary>
public sealed record CornersState(IReadOnlyDictionary<string, string>? Actions, bool? Enabled);

/// <summary>
/// Every word on the three welcome cards, ui/welcome/content.js byte for byte (WEL-028..041), and the hot-corners block
/// (ui/welcome/welcome.js cornersBlock / cornerView, WEL-032..034). Pure: no surface, no I/O.
/// </summary>
public static class WelcomeCards
{
    public static readonly IReadOnlyList<WelcomeCard> Cards = new[]
    {
        new WelcomeCard("changed", "What changed", "A new shell, with Windows underneath",
            "Mantle takes over the taskbar and Start. Your apps, files and Windows settings are exactly where you left them."),
        new WelcomeCard("keys", "The keys", "The keys you know, answered by Mantle"),
        new WelcomeCard("back", "Switching back", "Want Windows’ taskbar back?",
            "Windows’ taskbar is only hidden while Mantle runs. Going back takes one click."),
    };

    public static int Count => Cards.Count;

    // ---- card 1 ----
    public static readonly IReadOnlyList<WelcomeItem> Items = new[]
    {
        new WelcomeItem("bar", "The bar", "Push the pointer to the bottom edge and it rises: your open apps and the ones pinned to your taskbar."),
        new WelcomeItem("top", "The top bar", "The Mantle menu and the menus of the app in front, with CPU, memory and GPU on the right."),
        new WelcomeItem("launcher", "The launcher", "Mantle’s Start. Tap Win and type to find apps, settings and files."),
    };

    /// Card 1's fine print: what Mantle sends over the network on its own (content.js at 6ca499fc, cards[0].foot).
    public const string NetworkFoot = "On its own, Mantle checks GitHub for updates 2 minutes after it starts and every 6 hours, and the top bar’s PING pings AWS and Akamai/Linode servers every 2 seconds while it shows. Untick either under Features in the tray: Check for Mantle updates, Top menu bar.";

    // ---- card 2 ----
    public static readonly IReadOnlyList<WelcomeKey> Keys = new[]
    {
        new WelcomeKey(new[] { "Win" }, "Open the launcher", "Alt+Space works too"),
        new WelcomeKey(new[] { "Alt", "Tab" }, "Switch windows"),
        new WelcomeKey(new[] { "Win", "Shift", "S" }, "Screenshot an area", "Copied to the clipboard; Save keeps a file"),
        new WelcomeKey(new[] { "Win", "Tab" }, "Task view"),
        new WelcomeKey(new[] { "Win", "I" }, "Mantle Settings"),
    };

    public const string CornersTitle = "Hot corners";
    public const string CornersHow = "Rest the pointer in a corner for a moment.";
    public const string CornersOff = "Hot corners are off. Turn them on in Mantle Settings, under Mantle.";
    public const string KeysFoot = "Every key Mantle answers is listed in Mantle Settings, under Keyboard shortcuts.";

    /// The corner order the block lists them in.
    public static readonly IReadOnlyList<string> CornerOrder = new[] { "tl", "tr", "bl", "br" };

    /// The defaults a new profile has (lib/features/_corners.js DEFAULTS).
    public static readonly IReadOnlyDictionary<string, string> CornerDefaults = new Dictionary<string, string>
    { ["tl"] = "taskview", ["tr"] = "off", ["bl"] = "off", ["br"] = "desktop" };

    public static readonly IReadOnlyDictionary<string, string> CornerNames = new Dictionary<string, string>
    { ["tl"] = "Top-left", ["tr"] = "Top-right", ["bl"] = "Bottom-left", ["br"] = "Bottom-right" };

    public static readonly IReadOnlyDictionary<string, string> CornerLabels = new Dictionary<string, string>
    {
        ["off"] = "Off", ["taskview"] = "Task view", ["desktop"] = "Show desktop",
        ["launcher"] = "Launcher", ["qs"] = "Quick settings", ["lock"] = "Lock screen",
    };

    // ---- card 3 ----
    public static readonly IReadOnlyList<WelcomeWay> Ways = new[]
    {
        new WelcomeWay("For now", "Open the ••• menu on the bar and choose Restore Windows taskbar. Mantle keeps running: to get its bar back, choose Restart Mantle to resume from Mantle’s tray icon.", Action: "restore"),
        new WelcomeWay("Quit", "Quit Mantle is in the same menu (it asks for a second click), or press", Keys: new[] { "Ctrl", "Alt", "Shift", "Backspace" }, After: "anywhere."),
        new WelcomeWay("For good", "Mantle starts when you sign in, through a task named Mantle in Task Scheduler. Delete that task (or run", Code: "npm run autostart:remove", After: "in Mantle’s folder), then quit Mantle."),
    };

    /// A copy installed by Mantle-Setup.exe (welcome.state packaged): replaces "For good".
    public static readonly WelcomeWay Installed = new("For good", "Uninstall Mantle in Windows Settings, under Apps > Installed apps. It puts Windows back before it removes anything, and takes away Mantle’s start at sign-in if you chose it.");

    public const string RestoreLabel = "Restore Windows taskbar now";
    public const string RestoreBusy = "Restoring…";
    public const string RestoreDone = "Windows’ taskbar is back. To get Mantle’s bar back, choose Restart Mantle to resume from Mantle’s tray icon.";
    public const string RestoreFailed = "Could not restore: ";

    // ---- buttons ----
    public const string Next = "Next", Back = "Back", Skip = "Skip", Finish = "Start using Mantle";
    public const string NextTitle = "Next (Right arrow or Enter)", FinishTitle = "Start using Mantle (Enter)", SkipTitle = "Skip the welcome (Esc)";
    public const string TestChip = "Test run";

    /// <summary>The ways card 3 shows: the source copy's three, or with "For good" swapped for the uninstaller when packaged.</summary>
    public static IReadOnlyList<WelcomeWay> WaysFor(bool packaged) =>
        packaged ? Ways.Select(w => w.Name == Installed.Name ? Installed : w).ToList() : Ways;

    /// <summary>"Top-left:" - the name with any trailing spaces/colons trimmed and exactly one colon added.</summary>
    public static string CornerName(string name) => (name ?? "").TrimEnd(' ', '\t', ':', '：') + ":";

    /// <summary>
    /// The hot-corners block: the live corners when <paramref name="live"/> has actions, else the defaults; only corners
    /// whose action is not Off are listed; corners switched off (or all Off) = the single "off" line. Never throws.
    /// </summary>
    public static CornersView Corners(CornersState? live)
    {
        var liveActions = live?.Actions;
        var actions = liveActions ?? CornerDefaults;
        bool off = live?.Enabled == false;
        var on = CornerOrder.Where(c => actions.TryGetValue(c, out var a) && !string.IsNullOrEmpty(a) && a != "off").ToList();
        if (off || on.Count == 0)
            return new CornersView(new[] { new CornerLine("off", "", CornersOff) }, null, off, liveActions != null);
        var lines = on.Select(c => new CornerLine(c, CornerName(CornerNames.TryGetValue(c, out var n) ? n : c),
            CornerLabels.TryGetValue(actions[c], out var l) ? l : actions[c])).ToList();
        return new CornersView(lines, CornersHow, false, liveActions != null);
    }
}
