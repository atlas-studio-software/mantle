using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.QuickLinks;

/// <summary>What a row runs: settings (Uri), exec (File), terminal (Admin), mantle (Path) or desktop; null for a page row.</summary>
public sealed record QuickLinkRun(string Kind, string? Uri = null, string? File = null, bool Admin = false, string? Path = null);

/// <summary>A Win+X row: id, label with its "&amp;" letter, group, what it runs or the page it opens, and its sub-line.</summary>
public sealed record QuickLink(string Id, string Label, int Group, QuickLinkRun? Run = null, string? Page = null, string? Sub = null);

/// <summary>A row of the "Shut down or sign out" submenu; <see cref="Confirm"/> rows ask first.</summary>
public sealed record QuickPower(string Id, string Label, string Action, bool Confirm = false);

/// <summary>"Device &amp;Manager" split for drawing: the text, the part before the letter, the letter, after, and the key.</summary>
public sealed record QuickLabel(string Text, string Before, string Letter, string After, string Key);

/// <summary>
/// Windows' Win+X menu as Mantle draws it (lib/quick-links.js; WinXLinks, not QuickLinks, so the class never shadows its namespace): the rows, labels and underlined letters exactly as
/// build 26300's, and the submenu. Pure; the page sends only a row id.
/// </summary>
public static class WinXLinks
{
    public static readonly IReadOnlyList<QuickLink> Links = new QuickLink[]
    {
        new("apps", "Installed a&pps", 1, new("settings", Uri: "ms-settings:appsfeatures")),
        new("mobility", "Mo&bility Center", 1, new("exec", File: "mblctr.exe")),
        new("power", "Power &Options", 1, new("settings", Uri: "ms-settings:powersleep")),
        new("events", "Event &Viewer", 1, new("exec", File: "eventvwr.msc")),
        new("system", "S&ystem", 1, new("settings", Uri: "ms-settings:about")),
        new("devices", "Device &Manager", 1, new("exec", File: "devmgmt.msc")),
        new("network", "Net&work Connections", 1, new("settings", Uri: "ms-settings:network-status")),
        new("disks", "Dis&k Management", 1, new("exec", File: "diskmgmt.msc")),
        new("computer", "Computer Mana&gement", 1, new("exec", File: "compmgmt.msc")),
        new("terminal", "Term&inal", 1, new("terminal")),
        new("terminaladmin", "Terminal (&Admin)", 1, new("terminal", Admin: true)),
        new("taskmgr", "&Task Manager", 2, new("mantle", Path: "mantle:taskmgr"), Sub: "Mantle"),
        new("settings", "Setti&ngs", 2, new("mantle", Path: "mantle:settings"), Sub: "Mantle"),
        new("files", "File &Explorer", 2, new("mantle", Path: "mantle:files"), Sub: "Mantle Files"),
        new("search", "&Search", 2, Page: "search"),
        new("run", "&Run", 2, Page: "run"),
        new("shutdown", "Sh&ut down or sign out", 3, Page: "power"),
        new("desktop", "&Desktop", 3, new("desktop")),
    };

    public static readonly IReadOnlyList<QuickPower> Power = new QuickPower[]
    {
        new("signout", "S&ign out", "signout", true),
        new("sleep", "&Sleep", "sleep"),
        new("hibernate", "&Hibernate", "hibernate"),
        new("shutdown", "Sh&ut down", "shutdown", true),
        new("restart", "&Restart", "restart", true),
    };

    /// <summary>The words for an id no row has.</summary>
    public const string Unknown = "That quick link isn't one Mantle knows.";

    public static QuickLabel Parse(string? label)
    {
        var s = label ?? "";
        int at = s.IndexOf('&');
        if (at < 0 || at == s.Length - 1) return new(s, s, "", "", "");
        var letter = s[at + 1].ToString();
        return new(s[..at] + s[(at + 1)..], s[..at], letter, s[(at + 2)..], letter.ToLowerInvariant());
    }

    /// <summary>The row whose letter is <paramref name="key"/> (one character, any case), or null.</summary>
    public static T? ByKey<T>(IEnumerable<T> list, string? key, Func<T, string> label) where T : class
    {
        var k = (key ?? "").ToLowerInvariant();
        if (k.Length != 1) return null;
        return list.FirstOrDefault(r => Parse(label(r)).Key == k);
    }

    public static QuickLink? ByKey(string? key) => ByKey(Links, key, l => l.Label);
    public static QuickPower? PowerByKey(string? key) => ByKey(Power, key, p => p.Label);
    public static QuickLink? ById(string? id) => Links.FirstOrDefault(l => l.Id == id);
}
