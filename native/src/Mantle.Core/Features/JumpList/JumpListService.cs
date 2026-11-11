using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Pinned;
using Mantle.Core.Features.WindowList;
using Mantle.Core.Platform;
using PinOverlay = Mantle.Core.Features.Pinned.PinOverlay;

namespace Mantle.Core.Features.JumpList;

/// The bar item a menu acts on (ui/bar/jumplist.js appOf): never its icon. <see cref="Lnk"/> is the pin's own shortcut.
public sealed record JumpApp(string Id, string Name, string Kind, string? Exe, string? Aumid, IReadOnlyList<string> Hwnds, string? Lnk = null)
{
    public static JumpApp Of(TaskItem it) => new(it.Id, it.Name.Length > 0 ? it.Name : it.Exe ?? it.Id, it.Kind, it.Exe ?? it.Pin?.Exe,
        it.Aumid ?? it.Pin?.Aumid, it.Windows.Select(w => w.Hwnd).ToArray(), it.Pin?.LnkPath);
}

public sealed record JumpItem(string Title, string? Tip, string? Icon, string Item, bool Pinned, bool CanRemove, bool Sep);

public sealed record JumpSection(string Kind, string Name, IReadOnlyList<JumpItem> Items);

/// jumplist.get's answer: sections, or <see cref="None"/> / <see cref="Error"/> in words; <see cref="Ok"/> false = not read at all.
public sealed record JumpListReply(bool Ok, bool Off, string? Reason, IReadOnlyList<JumpSection> Sections, string? None, string? Error, string? Note, long MainMs)
{
    /// The quiet lines under the list (ui/bar/jumplist.js fill / open): "No jump list: ...", "Part of ...", the note; warn when a read failed.
    public (IReadOnlyList<string> Lines, bool Warn) Quiet()
    {
        if (Off) return (new[] { "No jump list: " + (Reason ?? "jump lists are switched off") }, false);
        if (!Ok) return (new[] { "The jump list could not be read: " + (Reason ?? "no reason given") }, true);
        var l = new List<string>();
        bool any = Sections.Any(s => s.Items.Any(i => i.Sep || i.Title.Length > 0));
        if (!any) l.Add("No jump list: " + (None ?? Error ?? "Windows gave no reason"));
        if (any && Error != null) l.Add("Part of the jump list could not be read: " + Error);
        if (Note != null) l.Add(Note);
        return (l, Error != null);
    }
}

/// An action's answer: <see cref="Reason"/> in words when it failed, or what a dry run would have done.
public sealed record JumpAct(bool Ok, string? Reason, bool DryRun = false, string? Route = null, string? Did = null, bool Undo = false);

/// <summary>The argument checks and words of lib/features/jumplist.js (pure; unit-tested).</summary>
public static class JumpRules
{
    public const int HwndsMax = 64;
    public const string FilesAumid = "mantle.files", ExplorerAppId = "Microsoft.Windows.Explorer";
    static readonly Regex HwndRe = new(@"^(?:\d{1,20}|0x[0-9a-f]{1,16})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ItemRe = new(@"^(?:app|c:\d{1,4}:\d{1,5}|a:\d{1,10})$", RegexOptions.CultureInvariant);
    static readonly Regex ExeRe = new("^[a-z]:\\\\[^<>\"|?*\\x00-\\x1f]{1,1000}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex AumidRe = new("^[^<>\"|?*:/\\x00-\\x1f]{1,256}$", RegexOptions.CultureInvariant);

    public static bool IsFiles(JumpApp? a) => a?.Aumid != null && a.Aumid.Equals(FilesAumid, StringComparison.OrdinalIgnoreCase);

    /// A token jumplist.get gave out (app / c:N:N / a:N); "app" is never removable.
    public static bool ItemOk(string? item, bool remove = false) => item != null && ItemRe.IsMatch(item) && !(remove && item == "app");

    /// The sidecar's words made fit for the menu (BAR-318): no hex codes, no "error 5", no internal names.
    public static string? Plain(string? s)
    {
        if (s == null) return null;
        s = Regex.Replace(s, @"\s*\((?:0x[0-9a-f]{8}|error -?\d+)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bthe sidecar\b", "Mantle's helper", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bsidecar\b", "Mantle's helper", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bDestList\b", "list");
        s = Regex.Replace(s, @"\bhwnd\b", "window", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// The window handles, all valid, unique and at most 64, or a thrown plain reason (BAR-331).
    public static List<string> HwndsOf(IEnumerable<string?>? list)
    {
        var o = new List<string>();
        foreach (var h in list ?? Array.Empty<string?>()) {
            if (string.IsNullOrEmpty(h)) continue;
            if (!HwndRe.IsMatch(h)) throw new ArgumentException("Mantle was sent a window it does not know, so nothing was done");
            if (!o.Contains(h)) o.Add(h);
        }
        if (o.Count > HwndsMax) throw new ArgumentException($"that is more than {HwndsMax} windows at once, so nothing was done");
        return o;
    }

    static string Cut(string v, int n) => v.Length <= n ? v : v[..n];

    /// How the sidecar names the app: its window first (the taskbar's own resolver), then its pin, then its AUMID (targetOf).
    public static JsonObject TargetOf(JumpApp? app)
    {
        if (app == null) throw new ArgumentException("jumplist: no app was given");
        if (IsFiles(app)) return new JsonObject { ["app"] = ExplorerAppId };
        if (app.Exe != null && (!ExeRe.IsMatch(app.Exe) || app.Exe.Contains("..", StringComparison.Ordinal)))
            throw new ArgumentException($"Mantle was sent a program it cannot start ({(app.Name.Length > 0 ? Cut(app.Name, 60) : "it")} is not a program on this PC), so nothing was done");
        if (app.Aumid != null && (!AumidRe.IsMatch(app.Aumid) || app.Aumid.Contains("..", StringComparison.Ordinal)))
            throw new ArgumentException("Mantle was sent an app it does not know, so nothing was done");
        var a = new JsonObject();
        var hwnds = HwndsOf(app.Hwnds);
        if (hwnds.Count > 0) a["hwnd"] = hwnds[0];
        if (app.Lnk != null && app.Lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(app.Lnk)) a["lnk"] = app.Lnk;
        if (a["hwnd"] == null && a["lnk"] == null && app.Aumid != null) a["app"] = app.Aumid;
        if (app.Exe != null) a["exe"] = app.Exe;
        if (a.Count == 0) throw new ArgumentException($"Mantle does not know which program {(app.Name.Length > 0 ? app.Name : "this")} is");
        return a;
    }

    /// An item's own actions: why "Pin to this list" / "Unpin from this list" cannot be done (ui/bar/jumplist.js toggleSub, BAR-213).
    public static string SubWhy(string sectionKind, bool pinned) =>
        (pinned ? "Unpin from this list" : "Pin to this list") + ": " + (sectionKind == "tasks" ? "tasks belong to the app and cannot be pinned or removed"
            : sectionKind == "custom" ? "this list belongs to the app: only the app can change it"
            : "Windows has no public way for another program to " + (pinned ? "unpin" : "pin") + " a jump list item");

    /// The sidecar's jumplist.get result as a reply (sections with their items, the words made plain).
    public static JumpListReply Parse(JsonElement r, long ms)
    {
        static string? S(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        static bool B(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        var secs = new List<JumpSection>();
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("sections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray()) {
                var items = new List<JumpItem>();
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array)
                    foreach (var i in its.EnumerateArray()) {
                        if (B(i, "sep")) { items.Add(new JumpItem("", null, null, "", false, false, true)); continue; }
                        if (S(i, "title") is not { } t || S(i, "item") is not { } tok) continue;
                        items.Add(new JumpItem(t, S(i, "tip"), S(i, "icon"), tok, B(i, "pinned"), B(i, "canRemove"), false));
                    }
                secs.Add(new JumpSection(S(s, "kind") ?? "custom", S(s, "name") ?? "", items));
            }
        return new JumpListReply(true, false, null, secs, Plain(S(r, "none")), Plain(S(r, "error")), Plain(S(r, "note")), ms);
    }
}

public sealed class JumpListOptions
{
    public IPlatform Platform = null!;
    /// Mantle's own pin list (features\jumplist.json), the store the strip's drag order writes through (the ONE gate).
    public IStore? Overlay;
    public bool Test;
    /// A --test run's made-up jump lists (MANTLE_TEST_JUMPLIST): {"apps": {"&lt;exe file name or app name&gt;" | "*": &lt;get result&gt;}}.
    public string? Fixture;
    public Action<string> Log = _ => { };
    public Func<long> Now = () => Environment.TickCount64;
}

/// <summary>
/// The host side of the bar's right-click menu (lib/features/jumplist.js): get, open, remove, close, pin. Every open,
/// remove and close in a --test run is a dry run with a log line, and the log carries tokens and counts only, never an
/// app's or an item's name (BAR-332). Pin and unpin write Mantle's OWN pin list in this profile (a test profile's is its
/// own), never Windows' pins; the undo of an unpin is kept 10 minutes, this run only, at most 20 (BAR-285).
/// </summary>
public sealed class JumpListService
{
    public const int ReadMs = 6000;
    public static readonly TimeSpan UndoFor = TimeSpan.FromMinutes(10);
    public const int UndoMax = 20;

    readonly JumpListOptions _o;
    readonly Dictionary<string, (PinOverlay Before, long At)> _unpins = new(StringComparer.Ordinal);
    readonly List<string> _unpinOrder = new();
    JsonElement? _fixture;
    string? _fixtureWhy;

    public JumpListService(JumpListOptions o)
    {
        _o = o;
        if (o.Test && !string.IsNullOrEmpty(o.Fixture)) {
            try { using var d = JsonDocument.Parse(File.ReadAllText(o.Fixture)); _fixture = d.RootElement.Clone(); o.Log("bar tasks: jump list: a --test run with MANTLE_TEST_JUMPLIST: made-up jump lists, no sidecar"); }
            catch (Exception e) { _fixtureWhy = "the test's made-up jump lists could not be read (" + e.Message + ")"; o.Log("bar tasks: jump list: " + _fixtureWhy); }
        }
    }

    bool Fixtured => _fixture != null || _fixtureWhy != null;

    JsonElement? FixtureFor(JumpApp app)
    {
        if (_fixture is not { } f || !f.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Object) return null;
        string exe = app.Exe != null ? Path.GetFileName(app.Exe).ToLowerInvariant() : "";
        foreach (var key in new[] { exe, app.Name.ToLowerInvariant(), "*" })
            if (key.Length > 0 && apps.TryGetProperty(key, out var v)) return v;
        return null;
    }

    async ValueTask<PlatformReply> Send(string verb, JsonObject args) =>
        await _o.Platform.CallAsync(verb, args, TimeSpan.FromMilliseconds(ReadMs)).ConfigureAwait(true);

    static string Said(PlatformReply r) => r.Error ?? "no answer";

    public async ValueTask<JumpListReply> GetAsync(JumpApp app)
    {
        long t0 = _o.Now();
        JsonObject target;
        try { target = JumpRules.TargetOf(app); }
        catch (ArgumentException e) { return new JumpListReply(false, false, e.Message, Array.Empty<JumpSection>(), null, null, null, 0); }
        JumpListReply rep;
        if (Fixtured) {
            if (_fixtureWhy != null) return new JumpListReply(false, false, _fixtureWhy, Array.Empty<JumpSection>(), null, null, null, 0);
            var f = FixtureFor(app);
            if (f is { } e && e.TryGetProperty("fail", out var fail)) return new JumpListReply(false, false, JumpRules.Plain(fail.GetString()), Array.Empty<JumpSection>(), null, null, null, _o.Now() - t0);
            rep = f is { } g ? JumpRules.Parse(g, _o.Now() - t0) : new JumpListReply(true, false, null, Array.Empty<JumpSection>(), "this app doesn't publish one", null, null, _o.Now() - t0);
        } else {
            var r = await Send("jumplist.get", target).ConfigureAwait(true);
            if (!r.Ok) {
                string why = Said(r);
                bool old = Regex.IsMatch(why, "unknown command|not a command", RegexOptions.IgnoreCase);
                return new JumpListReply(false, false, old ? "Mantle's helper is out of date: restart Mantle to finish its update" : JumpRules.Plain(why), Array.Empty<JumpSection>(), null, null, null, _o.Now() - t0);
            }
            rep = JumpRules.Parse(r.Result, _o.Now() - t0);
        }
        // counts and timings only: item titles are the user's file names and never reach the log (nor the app's name)
        if (_o.Test) {
            string what = string.Join(" ", rep.Sections.Select(s => $"{s.Kind}:{s.Items.Count}"));
            _o.Log($"bar tasks: jump list: get: {(what.Length > 0 ? what : $"none ({(rep.Error != null ? "a read error" : "empty")})")} in {rep.MainMs} ms");
        }
        return rep;
    }

    async ValueTask<JumpAct> Run(string verb, JumpApp app, string? item, string? title, string what)
    {
        JsonObject target;
        try { target = JumpRules.TargetOf(app); }
        catch (ArgumentException e) { return new JumpAct(false, e.Message); }
        if (!JumpRules.ItemOk(item, remove: verb == "remove")) return new JumpAct(false, "Mantle lost track of that item: open the menu again");
        target["item"] = item;
        if (title != null) target["title"] = title.Length > 520 ? title[..520] : title;
        if (_o.Test) {
            string route;
            if (Fixtured) route = "the test's made-up jump list";
            else {
                target["dry"] = true;
                var d = await Send("jumplist." + verb, target).ConfigureAwait(true);
                if (!d.Ok) return new JumpAct(false, JumpRules.Plain(Said(d)));
                route = d.Result.TryGetProperty("route", out var rt) ? rt.ToString() : "the sidecar";
            }
            _o.Log($"bar tasks: jump list: dry run (--test): would {what} {item} via {route}");
            return new JumpAct(true, $"dry run: would {what} it via {route}", DryRun: true, Route: route);
        }
        var r = await Send("jumplist." + verb, target).ConfigureAwait(true);
        if (!r.Ok) return new JumpAct(false, JumpRules.Plain(Said(r)));
        string via = r.Result.TryGetProperty("route", out var rr) ? rr.ToString() : "?";
        _o.Log($"bar tasks: jump list: {what} {item} via {via}");
        return new JumpAct(true, null, Route: via);
    }

    /// Run a task / open a recent item; <c>item "app"</c> = a new window of the app (BAR-319, BAR-322).
    public ValueTask<JumpAct> OpenAsync(JumpApp app, string? item, string? title) => Run("open", app, item, title, item == "app" ? "start a new window of" : "open");

    /// "Remove from this list" (Recent / Frequent only; never "app"), BAR-321.
    public ValueTask<JumpAct> RemoveAsync(JumpApp app, string? item, string? title) => Run("remove", app, item, title, "remove from the list");

    /// "Close window" / "Close all windows": WM_CLOSE to each (the app may still ask to save), never a kill (BAR-326).
    public async ValueTask<JumpAct> CloseAsync(IEnumerable<string?>? raw)
    {
        List<string> hwnds;
        try { hwnds = JumpRules.HwndsOf(raw); } catch (ArgumentException e) { return new JumpAct(false, e.Message); }
        if (hwnds.Count == 0) return new JumpAct(false, "there is no window to close");
        if (_o.Test) {
            _o.Log($"bar tasks: jump list: dry run (--test): would send WM_CLOSE to {hwnds.Count} window(s): {string.Join(", ", hwnds)}");
            return new JumpAct(true, $"dry run: would close {hwnds.Count} window(s)", DryRun: true);
        }
        var failed = new List<string>();
        foreach (var h in hwnds) {
            var r = await _o.Platform.CallAsync("close", new JsonObject { ["hwnd"] = h }, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
            if (!r.Ok) failed.Add(JumpRules.Plain(Said(r)) ?? "Windows did not say why");
        }
        _o.Log($"bar tasks: jump list: WM_CLOSE to {hwnds.Count} window(s){(failed.Count > 0 ? $"; {failed.Count} refused" : "")}");
        if (failed.Count == 0) return new JumpAct(true, null);
        string which = hwnds.Count == 1 ? "The window did not close" : $"{failed.Count} of {hwnds.Count} windows did not close";
        return new JumpAct(false, which + ": " + string.Join("; ", failed.Distinct()));
    }

    PinOverlay CurrentOverlay()
    {
        if (_o.Overlay is JsonStore js && js.DiskStamp != js.LoadedStamp) js.Load();
        return _o.Overlay != null && _o.Overlay.TryGet("pins", out var cur) ? PinOverlay.Norm(cur) : PinOverlay.Empty;
    }

    /// "Pin to taskbar" / "Unpin from taskbar" / the undo of an unpin, in Mantle's own list (BAR-216, BAR-281, BAR-285, BAR-324).
    public async ValueTask<JumpAct> PinAsync(JumpApp app, bool pin, bool undo = false)
    {
        if (app.Id.Length == 0 || app.Id.Length > 1024) return new JumpAct(false, "Mantle lost track of that app: open the menu again");
        try { JumpRules.TargetOf(app); } catch (ArgumentException e) { return new JumpAct(false, e.Message); }
        if (_o.Overlay == null) return new JumpAct(false, "Mantle's own pin list is not open in this run");
        undo = pin && undo;
        try {
            var cur = CurrentOverlay();
            var one = new PinOverlayEdit.App(app.Id, app.Name, app.Exe, app.Aumid);
            PinOverlayEdit.Result next;
            string line;
            if (undo && _unpins.TryGetValue(app.Id, out var u) && _o.Now() - u.At < (long)UndoFor.TotalMilliseconds) {
                next = PinOverlayEdit.UndoUnpin(cur, u.Before, one);
                _unpins.Remove(app.Id); _unpinOrder.Remove(app.Id);
                line = "undo unpin: the app is back in its place";
            } else {
                string? shortcut = null;
                if (pin && !JumpRules.IsFiles(app) && !_o.Test) {
                    // the shortcut Windows itself would start it from (a browser that is a bare electron.exe)
                    var r = await Send("jumplist.shortcut", JumpRules.TargetOf(app)).ConfigureAwait(true);
                    if (r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("shortcut", out var sc) && sc.ValueKind == JsonValueKind.String) shortcut = sc.GetString();
                }
                next = pin ? PinOverlayEdit.Pin(cur, one with { Shortcut = shortcut }) : PinOverlayEdit.Unpin(cur, one);
                line = $"{(undo ? "undo unpin (nothing kept to undo from), pin" : pin ? "pin" : "unpin")}: {next.Did}";
            }
            var w = await _o.Overlay.SetAsync("pins", PinOverlayEdit.ToJson(next.Overlay)).ConfigureAwait(true);
            if (!w.Ok) return new JumpAct(false, JumpRules.Plain(w.Reason) ?? "the pin list was not saved");
            bool canUndo = !pin && next.Did != "already";
            if (canUndo) {
                _unpins[app.Id] = (cur, _o.Now());
                _unpinOrder.Remove(app.Id); _unpinOrder.Add(app.Id);
                while (_unpinOrder.Count > UndoMax) { _unpins.Remove(_unpinOrder[0]); _unpinOrder.RemoveAt(0); }
            }
            // what happened, never the app's name or its id (a shortcut's file name)
            _o.Log($"bar tasks: jump list: {line}{(_o.Test ? " (in this test profile)" : "")}");
            return new JumpAct(true, null, Did: next.Did, Undo: canUndo);
        } catch (Exception e) {
            return new JumpAct(false, JumpRules.Plain(e.Message));
        }
    }
}
