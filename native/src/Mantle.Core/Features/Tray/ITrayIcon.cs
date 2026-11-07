using System;
using System.Collections.Generic;

namespace Mantle.Core.Features.Tray;

/// <summary>
/// The notification-area icon as the tray controller drives it: a tip, a menu of <see cref="TrayItem"/>s,
/// balloons, and the row the user chose. <see cref="Win32TrayIcon"/> draws it live; a test run uses
/// <see cref="StandInTrayIcon"/>, which keeps the menu but never draws an icon (main.js / lib/test-quiet.js).
/// </summary>
/// <remarks>Every member may be called from the host thread; <see cref="Chosen"/> is raised on the host
/// thread (the icon posts it there). Nothing here throws for a Windows failure: it is said in words.</remarks>
public interface ITrayIcon : IDisposable
{
    /// Put the icon up. Null when it is up, else why it could not be made.
    string? Create(string tip);
    void SetTip(string tip);
    void SetMenu(IReadOnlyList<TrayItem> items);
    /// A Windows balloon from the icon (no sound): warning for a failed action, info otherwise.
    void Balloon(string title, string text, bool warning);
    /// Open the menu now (a left click does the same as a right click).
    void PopUp();
    /// A row was chosen. A checkbox row carries its state BEFORE the click.
    Action<TrayItem>? Chosen { get; set; }
    /// True when this draws a real icon.
    bool Draws { get; }
}

/// <summary>The tray of a test run: the menu is kept (for --test-tray and the tests), no icon is drawn, and a
/// balloon is a log line only.</summary>
public sealed class StandInTrayIcon : ITrayIcon
{
    readonly Action<string> _log;
    public StandInTrayIcon(Action<string>? log = null) { _log = log ?? (_ => { }); }

    public IReadOnlyList<TrayItem> Menu { get; private set; } = Array.Empty<TrayItem>();
    public string Tip { get; private set; } = "";
    public int MenuSets { get; private set; }
    public List<(string Title, string Text, bool Warning)> Balloons { get; } = new();
    public int PopUps { get; private set; }
    public bool Disposed { get; private set; }
    public Action<TrayItem>? Chosen { get; set; }
    public bool Draws => false;

    public string? Create(string tip) { Tip = tip; return null; }
    public void SetTip(string tip) => Tip = tip;
    public void SetMenu(IReadOnlyList<TrayItem> items) { Menu = items; MenuSets++; }
    public void Balloon(string title, string text, bool warning)
    {
        Balloons.Add((title, text, warning));
        _log($"tray (test run, no icon): balloon \"{title}: {text}\"");
    }
    public void PopUp() => PopUps++;

    /// Choose the first row whose id is <paramref name="id"/> (tests; a page in a test run).
    public bool Choose(string id)
    {
        var item = Find(Menu, id);
        if (item == null || !item.Enabled) return false;
        Chosen?.Invoke(item);
        return true;
    }

    internal static TrayItem? Find(IReadOnlyList<TrayItem> items, string id)
    {
        foreach (var i in items)
        {
            if (i.Id == id) return i;
            if (i.Submenu != null && Find(i.Submenu, id) is { } s) return s;
        }
        return null;
    }

    public void Dispose() => Disposed = true;
}
