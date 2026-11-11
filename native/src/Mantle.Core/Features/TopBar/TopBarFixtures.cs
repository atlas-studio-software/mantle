using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.TopBar;

/// <summary>
/// --test only: made-up focus snapshots and stats, the same ones ui/topbar/mock-mantle.js plays for Electron's page
/// (app=npp|chrome|desktop|explorer, stats=ok|nogpu|paused), so every runner and both apps draw the same strip and the
/// screenshot pairs compare like with like. Nothing here is from the user's machine. Chosen by MANTLE_TEST_TOPBAR
/// ("app=npp&amp;stats=ok", Electron's query words) or the test-only command topbar.testFixture.
/// </summary>
public static class TopBarFixtures
{
    /// mock-mantle.js W32(): Notepad++'s menu tree, ids 100 + pos as Electron numbers them (TOP-061..070's rows).
    static JsonArray NppMenu()
    {
        int pos = 0;
        JsonObject It(string label, string key = "", bool enabled = true, bool check = false, bool radio = false)
        {
            pos++;
            return new JsonObject { ["t"] = "item", ["pos"] = pos - 1, ["label"] = label, ["key"] = key, ["mn"] = label[..1].ToLowerInvariant(),
                ["id"] = 100 + pos, ["enabled"] = enabled, ["checked"] = check, ["radio"] = radio, ["items"] = null, ["lazy"] = false };
        }
        JsonObject Sep() => new() { ["t"] = "sep", ["pos"] = pos++ };
        JsonObject Sub(string label, JsonObject[] items, bool lazy = false)
        {
            var x = It(label); x["id"] = null; x["items"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()); x["lazy"] = lazy; pos = 0; return x;
        }
        var file = Sub("File", new[] { It("New", "Ctrl+N"), It("Open…", "Ctrl+O"), Sub("Recent Files", new[] { It("notes.txt"), It("todo.md"), It("build.log") }),
            Sep(), It("Save", "Ctrl+S"), It("Save As…", "Ctrl+Alt+S"), It("Save a Copy As…", enabled: false), Sep(), It("Print…", "Ctrl+P"), Sep(), It("Exit", "Alt+F4") });
        var edit = Sub("Edit", new[] { It("Undo", "Ctrl+Z"), It("Redo", "Ctrl+Y", enabled: false), Sep(), It("Cut", "Ctrl+X"), It("Copy", "Ctrl+C"), It("Paste", "Ctrl+V"),
            Sep(), It("Select All", "Ctrl+A") });
        var search = Sub("Search", new[] { It("Find…", "Ctrl+F"), It("Replace…", "Ctrl+H") });
        var view = Sub("View", new[] { It("Always on Top", check: true), It("Word Wrap", check: true), Sep(),
            It("Tab size 2", radio: true, check: true), It("Tab size 4", radio: true), Sep(), Sub("Zoom", new[] { It("Zoom In", "Ctrl+Num +"), It("Zoom Out", "Ctrl+Num -"), It("Restore Default Zoom", "Ctrl+Num /") }) });
        var items = new[] { file, edit, search, view,
            Sub("Encoding", new[] { It("UTF-8", radio: true, check: true), It("UTF-16 LE", radio: true) }), Sub("Language", Array.Empty<JsonObject>(), lazy: true),
            Sub("Settings", new[] { It("Preferences…") }), Sub("Tools", new[] { It("MD5") }), Sub("Macro", new[] { It("Start Recording") }), Sub("Run", new[] { It("Run…", "F5") }),
            Sub("Plugins", new[] { It("Plugins Admin…") }), Sub("Window", new[] { It("Windows…") }), Sub("?", new[] { It("About Notepad++", "F1") }) };
        for (int i = 0; i < items.Length; i++) items[i]["pos"] = i;
        return new JsonArray(items.Select(i => (JsonNode)i).ToArray());
    }

    static JsonObject Base() => new()
    {
        ["hwnd"] = "4242", ["cls"] = "Notepad++", ["title"] = "todo.md - Notepad++", ["exe"] = @"C:\Program Files\Notepad++\notepad++.exe",
        ["pid"] = 7, ["desc"] = "Notepad++", ["product"] = "Notepad++", ["self"] = false, ["shell"] = false, ["fullscreen"] = false,
        ["zoomed"] = true, ["topmost"] = false, ["enabled"] = true, ["uia"] = "not needed",
    };

    public static JsonObject? Focus(string? app)
    {
        var f = Base();
        switch (app ?? "npp")
        {
            case "npp":
                f["menu"] = new JsonObject
                {
                    ["source"] = "win32", ["hwnd"] = "4242", ["owner"] = false,
                    ["items"] = NppMenu(),
                };
                return f;
            case "chrome":
                f["hwnd"] = "5151"; f["cls"] = "Chrome_WidgetWin_1"; f["title"] = "Inbox - Chrome"; f["exe"] = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
                f["desc"] = "Google Chrome"; f["product"] = "Google Chrome"; f["menuReason"] = "no Win32 menu bar"; return f;
            case "explorer":
                f["hwnd"] = "8888"; f["cls"] = "CabinetWClass"; f["title"] = "Downloads"; f["exe"] = @"C:\Windows\explorer.exe"; f["desc"] = "Windows Explorer";
                f["product"] = null; f["menuReason"] = "no Win32 menu bar"; return f;
            case "desktop":
                f["hwnd"] = "1"; f["cls"] = "Progman"; f["title"] = "Program Manager"; f["exe"] = @"C:\Windows\explorer.exe"; f["desc"] = "Windows Explorer";
                f["product"] = null; f["shell"] = true; return f;
            case "none": return null;
            default: return Focus("npp");
        }
    }

    const double GiB = 1073741824, MiB = 1048576;
    public static JsonObject? Stats(string? which)
    {
        if (which == "paused") return null;
        var s = new JsonObject
        {
            ["t"] = 1,
            ["cpu"] = new JsonObject { ["pct"] = 23.4 },
            ["mem"] = new JsonObject { ["used"] = 9.7 * GiB, ["total"] = 15.7 * GiB },
            ["gpu"] = new JsonObject { ["pct"] = 7.8, ["name"] = "Intel(R) UHD Graphics" },
            ["net"] = new JsonObject { ["down"] = 2.4 * MiB, ["up"] = 88 * 1024.0 },
            ["ping"] = new JsonObject { ["state"] = "ok", ["ms"] = 23, ["label"] = "Valorant", ["server"] = "US West (N. California)" },
        };
        if (which == "nogpu") s["gpu"] = new JsonObject { ["pct"] = null, ["reason"] = @"Windows has no GPU performance counters here (\GPU Engine is missing), so the GPU cannot be measured" };
        return s;
    }

    /// "app=npp&amp;stats=ok" -> (app, stats); null for an empty or missing spec.
    public static (string App, string Stats)? Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        string app = "npp", stats = "ok";
        foreach (var part in spec.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "app") app = kv[1]; else if (kv[0] == "stats") stats = kv[1];
        }
        return (app, stats);
    }
}
