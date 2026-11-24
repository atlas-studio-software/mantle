using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Snap;

/// <summary>What the snap surface is asked to draw: a picker or Snap Assist, its spot (DIP) and the page's payload (snap.show).</summary>
public sealed record SnapShow(int Seq, string Mode, SnapBox Rect, JsonObject Payload);

/// <summary>A window Snap Assist offers (snap.js#candidates): the exe without its path, the icon a data URL or null.</summary>
public sealed record SnapCandidate(string Hwnd, string? Title, string Exe, string? Icon, bool Minimized);

/// <summary>An empty zone the sidecar named after a snap (answer.assist[i]): the zone's name, grid and cell, and its physical frame.</summary>
public sealed record SnapEmptyZone(string? Name, int[] Grid, int[] Cell, SnapBox Frame);

/// <summary>An open picker or Snap Assist (snap.js S.sess): the window it is for, the zones still to fill, the offers, the windows placed so far.</summary>
public sealed record SnapSession(int Seq, string Mode, SnapBox Rect, string? Target, IReadOnlyList<SnapEmptyZone> Zones,
                                 IReadOnlyList<SnapCandidate> Cands, IReadOnlyList<string> Placed);

/// <summary>What one pick did. Stale = an old seq, ignored. Error = refused before anything moved. Else the reply, any notice, the never-strand check.</summary>
public sealed record SnapPickResult(bool Stale, string? Error, PlatformReply? Reply, SnapNotice? Notice, SnapLanding? Rescue, string? RescueError);

/// <summary>
/// THE WIN+Z PICKER AND SNAP ASSIST, as a model (lib/features/snap.js#picker, #pickZone, #openAssist, #candidates,
/// #pickWindow, #giveFocus, #end, the pick / dismiss commands, #reveal's keyboard check), lane r9-snap. No surface: a
/// panel is a <see cref="SnapShow"/> to draw and a hide by seq, so the surface that lands on the shared style layer
/// stays thin. NOT WIRED YET: SnapFeature does not claim Win+Z until that surface exists (lane notes, Deviations).
/// SAFETY: every window a pick moves (snap.zone) goes through the same never-strand check and put-back as Win+arrow
/// (<see cref="SnapCore"/>.LandedAsync); a window that had to be put back gets no Snap Assist (its zone is not where
/// the empty zones were measured), and a put-back that fails is a notice.
/// </summary>
public sealed class SnapPanel
{
    public static readonly TimeSpan InfoTimeout = TimeSpan.FromMilliseconds(1500);     // snap.js picker: snap.info
    public static readonly TimeSpan ZoneTimeout = TimeSpan.FromMilliseconds(2500);     // snap.zone
    public static readonly TimeSpan WindowsTimeout = TimeSpan.FromMilliseconds(1500);  // candidates: windows
    public static readonly TimeSpan FocusTimeout = TimeSpan.FromMilliseconds(1500);    // giveFocus, reveal's focus ladder
    public const int NoKeysAfterMs = 60;                                               // reveal: ask for the keyboard after

    public const string NotAvailable = "Snap layouts are not available";
    public const string NotOffered = "that window is not one Snap Assist offered";
    public const string NoKeys = "Windows kept the keyboard on another window, so click your choice here.";
    public const string NoFrame = "Windows did not say where the window is.";            // NEW: snap.js would throw here
    public const string NoZoneLeft = "Snap Assist has no empty zone left";               // NEW: cannot happen in snap.js

    readonly SnapCore _core;
    readonly SnapCore.Sender _send;
    readonly Action<string> _log;
    readonly Action<SnapShow> _show;
    readonly Action<int> _hide;
    readonly bool _test;
    readonly Func<SnapBox, SnapBox> _toDip;
    readonly Func<SnapBox, SnapBox?>? _workOf;
    int _seq;

    /// <summary>The open panel, null when none.</summary>
    public SnapSession? Session { get; private set; }

    /// <summary>The last zone picked (snap.js S.lastZone), passed to the next picker as start.</summary>
    public (string Layout, int Zone)? LastZone { get; private set; }

    /// <summary>Why the last panel ended (snap.js S.ended.why).</summary>
    public string? LastEnded { get; private set; }

    /// <param name="toDip">physical to DIP (screen.screenToDipRect); identity when null.</param>
    /// <param name="workOf">the DIP work area of the display holding a DIP frame (screen.getDisplayMatching); null = the window's monitor from snap.info.</param>
    public SnapPanel(SnapCore.Sender send, Action<string> log, Action<SnapNotice> notice, Action<SnapShow> show, Action<int> hide, bool test,
                     Func<SnapBox, SnapBox>? toDip = null, Func<SnapBox, SnapBox?>? workOf = null)
    {
        _send = send; _log = log; _show = show; _hide = hide; _test = test;
        _toDip = toDip ?? (Func<SnapBox, SnapBox>)(b => b);
        _workOf = workOf;
        // A notice is its own face in snap.js (begin('notice')): it replaces whatever panel is open.
        _core = new SnapCore(send, log, n => { End("replaced"); notice(n); }, test);
    }

    // ------------------------------------------------------------ the arrows (snap.js#arrow's Assist half)

    /// <summary>
    /// A Win+arrow: while a panel is open it acts on the window the panel is for, and ends the panel. A snap into a
    /// half or quarter with an empty zone left opens Snap Assist (unless the window had to be put back on the screen).
    /// </summary>
    public async ValueTask<SnapArrowResult> ArrowAsync(string chord)
    {
        string? target = Session?.Target;
        if (Session != null && SnapCore.Dirs.ContainsKey(chord)) End("another snap key");
        SnapArrowResult r = await _core.ArrowAsync(chord, target).ConfigureAwait(true);
        if (r.Reply is { Ok: true } reply && r.Notice == null && r.Rescue is not { Changed: true }) {
            JsonElement x = reply.Result;
            if (SnapCore.Truthy(x, "ok") && SnapWords.Str(x, "action") == "snap" && !NoFit(x)) {
                List<SnapEmptyZone> zones = EmptyZones(x);
                string? hwnd = SnapWords.Str(x, "hwnd");
                if (zones.Count > 0 && !string.IsNullOrEmpty(hwnd)) await OpenAssistAsync(hwnd, zones, Array.Empty<string>()).ConfigureAwait(true);
            }
        }
        return r;
    }

    // ------------------------------------------------------------ Win+Z

    /// <summary>
    /// Win+Z: closes an open picker (focus back to its window); else asks snap.info (1.5 s) for the panel's window or the
    /// foreground one, refuses a fixed-size or elevated window with a notice, and opens the picker under its maximize button.
    /// </summary>
    public async ValueTask<SnapSession?> PickerAsync()
    {
        if (Session is { Mode: "picker" } open) {
            End("Win+Z again");
            await GiveFocusAsync(open.Target, "Win+Z again").ConfigureAwait(true);
            return null;
        }
        string? target = Session?.Target;
        if (_test && target == null) { _core.Notice(SnapCore.TestTitle, SnapCore.TestText, null, null); return null; }
        var args = new JsonObject();
        if (target != null) args["hwnd"] = target;
        PlatformReply r = await _send("snap.info", args, InfoTimeout).ConfigureAwait(true);
        if (!r.Ok) { _core.Notice(NotAvailable, SnapWords.Cap(r.Error), null, null); return null; }
        JsonElement info = r.Result;
        string app = SnapWords.AppOf(info);
        string[] names = SnapWords.NamesOf(info);
        SnapScreen? mon = MonitorOf(info);
        if (!SnapCore.Truthy(info, "resizable")) {
            _core.Notice("Could not snap " + app, SnapWords.Cap(app + " has a fixed size, so it cannot be resized to fill a zone"), mon?.Work, names);
            return null;
        }
        if (SnapCore.Truthy(info, "elevatedAboveMantle")) {
            _core.Notice("Could not snap " + app, SnapWords.Cap(app + " is running as administrator, so Windows does not let Mantle move it. To snap it, run Mantle as administrator too"), mon?.Work, names);
            return null;
        }
        SnapBox? frame = SnapCore.Box(info, "frame");
        if (frame == null) { _core.Notice(NotAvailable, NoFrame, mon?.Work, names); return null; }
        SnapBox f = _toDip(frame.Value);
        SnapBox work = _workOf?.Invoke(f) ?? (mon != null ? _toDip(mon.Work) : f);
        string? exe = SnapWords.Str(info, "exe");
        string? name = SnapWords.Str(info, "app");
        var payload = new JsonObject {
            ["app"] = new JsonObject { ["exe"] = exe, ["name"] = string.IsNullOrEmpty(name) ? null : name },
            ["start"] = LastZone.HasValue ? new JsonObject { ["layout"] = LastZone.Value.Layout, ["zone"] = LastZone.Value.Zone } : null,
        };
        return Begin("picker", SnapFaces.PickerRect(f, work), payload, SnapWords.Str(info, "hwnd"),
                     Array.Empty<SnapEmptyZone>(), Array.Empty<SnapCandidate>(), Array.Empty<string>());
    }

    // ------------------------------------------------------------ the page's calls

    /// <summary>
    /// The page's snap.pick: in the picker a zone (<paramref name="layout"/>, <paramref name="zone"/>); in Snap Assist a
    /// window (<paramref name="hwnd"/>, which must be one it offered). An old <paramref name="seq"/> is ignored.
    /// </summary>
    public async ValueTask<SnapPickResult> PickAsync(int seq, string? layout = null, int zone = 0, string? hwnd = null)
    {
        SnapSession? s = Session;
        if (s == null || s.Seq != seq) return new SnapPickResult(true, null, null, null, null, null);
        if (s.Mode == "picker") return await PickZoneAsync(s, layout, zone).ConfigureAwait(true);
        if (hwnd == null || !s.Cands.Any(c => c.Hwnd == hwnd)) return new SnapPickResult(false, NotOffered, null, null, null, null);
        return await PickWindowAsync(s, hwnd).ConfigureAwait(true);
    }

    /// <summary>The page's snap.dismiss: Esc hands the keyboard back to the panel's window; a click-away does not (Windows already moved it). False = stale.</summary>
    public async ValueTask<bool> DismissAsync(int seq, string? why)
    {
        SnapSession? s = Session;
        if (s == null || s.Seq != seq) return false;
        End(why == "esc" ? "Esc" : "click-away");
        if (why == "esc" && s.Target != null) await GiveFocusAsync(s.Target, "Esc").ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// snap.js#reveal's keyboard check, <see cref="NoKeysAfterMs"/> after the panel came on screen: not focused = ask the
    /// sidecar's focus ladder for the panel's own window (1.5 s); still not = the footer warning (returned), logged.
    /// Null = nothing to say (focused, stale, or a test run, which never focuses).
    /// </summary>
    public async ValueTask<string?> NoKeysAsync(int seq, string panelHwnd, Func<bool> focused)
    {
        SnapSession? s = Session;
        if (_test || s == null || s.Seq != seq || focused()) return null;
        PlatformReply a = await _send("focus", new JsonObject { ["hwnd"] = panelHwnd }, FocusTimeout).ConfigureAwait(true);
        if (!ReferenceEquals(Session, s) || focused()) return null;
        _log($"#{seq}: no keyboard for the panel ({(a.Ok ? "focus was asked for and not given" : a.Error)})");
        return NoKeys;
    }

    /// <summary>The feature was switched off: the panel ends (snap.js onEnabled).</summary>
    public void Off() => End("snapping was turned off");

    /// <summary>Mantle stops: the panel ends.</summary>
    public void Stop(string? why = null) => End(why ?? "Mantle is putting Windows back");

    /// <summary>Ends the open panel (the surface hides it by seq), logged with why.</summary>
    public void End(string why)
    {
        SnapSession? s = Session;
        if (s == null) return;
        Session = null;
        _hide(s.Seq);
        LastEnded = why;
        _log($"#{s.Seq} {s.Mode}: ended: {why}");
    }

    // ------------------------------------------------------------ the zone answer

    /// <summary>The empty zones in a snap answer (assist: [{zone:{name, grid, cell}, frame}]); a malformed entry is skipped.</summary>
    public static List<SnapEmptyZone> EmptyZones(JsonElement x)
    {
        var list = new List<SnapEmptyZone>();
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("assist", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in arr.EnumerateArray()) {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("zone", out var z) || z.ValueKind != JsonValueKind.Object) continue;
            SnapBox? frame = SnapCore.Box(a, "frame");
            int[]? grid = Ints(z, "grid", 2), cell = Ints(z, "cell", 4);
            if (frame == null || grid == null || cell == null) continue;
            list.Add(new SnapEmptyZone(SnapWords.Str(z, "name"), grid, cell, frame.Value));
        }
        return list;
    }

    /// <summary>snap.js#zoneWords: a half or quarter by name, a layout zone by its grid and cell, else "this space".</summary>
    public static string ZoneWords(SnapEmptyZone? z)
    {
        if (z == null) return "this space";
        if (z.Name != null && SnapWords.Zones.TryGetValue(z.Name, out var w)) return w;
        foreach (var l in SnapFaces.Layouts)
            for (int i = 0; i < l.Zones.Count; i++)
                if (z.Grid.Length == 2 && l.Cols == z.Grid[0] && l.Rows == z.Grid[1] && l.Zones[i].SequenceEqual(z.Cell)) return l.Names[i];
        return "this space";
    }

    /// <summary>The other windows Snap Assist can offer (the sidecar's windows, 1.5 s) minus <paramref name="first"/> and the placed ones; none in a test run.</summary>
    public async ValueTask<List<SnapCandidate>> CandidatesAsync(string first, IReadOnlyList<string> placed)
    {
        var skip = new HashSet<string>(placed, StringComparer.Ordinal) { first };
        var list = new List<SnapCandidate>();
        if (_test) { _log("Snap Assist offers no window in a test run: a --test instance never lists your windows"); return list; }
        PlatformReply r = await _send("windows", new JsonObject(), WindowsTimeout).ConfigureAwait(true);
        if (!r.Ok || r.Result.ValueKind != JsonValueKind.Array) {
            _log($"Snap Assist has no window list: {(string.IsNullOrEmpty(r.Error) ? "not a list" : r.Error)}");
            return list;
        }
        foreach (var w in r.Result.EnumerateArray()) {
            string hwnd = SnapCore.Js(w, "hwnd");
            if (skip.Contains(hwnd)) continue;
            string exe = (SnapWords.Str(w, "exe") ?? "").Split(new[] { '\\', '/' })[^1];
            string? icon = SnapWords.Str(w, "icon");
            list.Add(new SnapCandidate(hwnd, SnapWords.Str(w, "title"), exe, string.IsNullOrEmpty(icon) ? null : icon, SnapCore.Truthy(w, "minimized")));
        }
        return list;
    }

    // ------------------------------------------------------------ inside

    async ValueTask<SnapSession?> OpenAssistAsync(string first, IReadOnlyList<SnapEmptyZone> zones, IReadOnlyList<string> placed)
    {
        List<SnapCandidate> cands = await CandidatesAsync(first, placed).ConfigureAwait(true);
        if (cands.Count == 0 || zones.Count == 0) {
            await GiveFocusAsync(first, "a snap with nothing to offer").ConfigureAwait(true);
            return null;
        }
        SnapEmptyZone z = zones[0];
        var payload = new JsonObject {
            ["zone"] = new JsonObject { ["name"] = ZoneWords(z) },
            ["candidates"] = CandsJson(cands),
            ["sel"] = 0,
            ["left"] = zones.Count - 1,
        };
        return Begin("assist", _toDip(z.Frame), payload, first, zones, cands, placed);
    }

    async ValueTask<SnapPickResult> PickZoneAsync(SnapSession s, string? layoutId, int zi)
    {
        SnapLayout? lay = SnapFaces.ById(layoutId);
        if (lay == null || zi < 0 || zi >= lay.Zones.Count)
            return new SnapPickResult(false, $"no zone {zi} in layout {layoutId ?? "undefined"}", null, null, null, null);
        List<SnapRestZone> rest = SnapFaces.Rest(lay.Id, zi);
        End("a zone was picked");
        var restJson = new JsonArray();
        foreach (var z in rest) restJson.Add((JsonNode)new JsonObject { ["grid"] = Arr(z.Cols, z.Rows), ["cell"] = Arr(z.Cell) });
        var args = new JsonObject { ["grid"] = Arr(lay.Cols, lay.Rows), ["cell"] = Arr(lay.Zones[zi]), ["rest"] = restJson };
        if (s.Target != null) args["hwnd"] = s.Target;
        PlatformReply r = await _send("snap.zone", args, ZoneTimeout).ConfigureAwait(true);
        JsonElement? x = r.Ok && r.Result.ValueKind == JsonValueKind.Object ? r.Result : null;
        _log($"Win+Z {lay.Id}/{lay.Names[zi]}: {Line(x, r)}");
        if (x is not { } xe || !SnapCore.Truthy(xe, "ok"))
            return new SnapPickResult(false, null, r, Refused(x, r, "this window"), null, null);
        LastZone = (lay.Id, zi);                       // the picker opens on it next time (snap.js: "Win+Z, Enter repeats it")

        var (landing, err, fail) = await _core.LandedAsync("Win+Z", xe).ConfigureAwait(true);
        if (fail != null || landing is { Changed: true }) {
            await GiveFocusAsync(s.Target, "Win+Z").ConfigureAwait(true);
            return new SnapPickResult(false, null, r, fail, landing, err);
        }
        SnapNotice? n = null;
        if (NoFit(xe)) {
            n = _core.Notice(NoFitTitle(SnapWords.AppOf(xe), lay.Names[zi]), SnapWords.Cap(SnapWords.Str(xe, "fitReason")), MonitorOf(xe)?.Work, SnapWords.NamesOf(xe));
        } else {
            List<SnapEmptyZone> zones = EmptyZones(xe);
            string first = s.Target ?? SnapWords.Str(xe, "hwnd") ?? "";
            if (zones.Count > 0 && first.Length > 0 && await OpenAssistAsync(first, zones, Array.Empty<string>()).ConfigureAwait(true) != null)
                return new SnapPickResult(false, null, r, null, landing, null);
        }
        await GiveFocusAsync(s.Target, "Win+Z").ConfigureAwait(true);
        return new SnapPickResult(false, null, r, n, landing, null);
    }

    async ValueTask<SnapPickResult> PickWindowAsync(SnapSession s, string hwnd)
    {
        if (s.Zones.Count == 0) return new SnapPickResult(false, NoZoneLeft, null, null, null, null);
        SnapEmptyZone z = s.Zones[0];
        End("a window was picked");
        var args = new JsonObject { ["hwnd"] = hwnd, ["grid"] = Arr(z.Grid), ["cell"] = Arr(z.Cell) };
        if (s.Target != null) args["of"] = s.Target;   // the zone is on the FIRST window's monitor
        PlatformReply r = await _send("snap.zone", args, ZoneTimeout).ConfigureAwait(true);
        JsonElement? x = r.Ok && r.Result.ValueKind == JsonValueKind.Object ? r.Result : null;
        _log($"Snap Assist: {Line(x, r)}");
        if (x is not { } xe || !SnapCore.Truthy(xe, "ok"))
            return new SnapPickResult(false, null, r, Refused(x, r, "that window"), null, null);

        var (landing, err, fail) = await _core.LandedAsync("Snap Assist", xe).ConfigureAwait(true);
        if (fail != null || landing is { Changed: true }) {
            await GiveFocusAsync(hwnd, "Snap Assist").ConfigureAwait(true);
            return new SnapPickResult(false, null, r, fail, landing, err);
        }
        var placed = s.Placed.Concat(new[] { hwnd }).ToList();
        var left = s.Zones.Skip(1).ToList();
        if (left.Count > 0 && s.Target != null && await OpenAssistAsync(s.Target, left, placed).ConfigureAwait(true) != null)
            return new SnapPickResult(false, null, r, null, landing, null);
        await GiveFocusAsync(hwnd, "Snap Assist").ConfigureAwait(true);
        return new SnapPickResult(false, null, r, null, landing, null);
    }

    SnapSession Begin(string mode, SnapBox rect, JsonObject payload, string? target, IReadOnlyList<SnapEmptyZone> zones,
                      IReadOnlyList<SnapCandidate> cands, IReadOnlyList<string> placed)
    {
        End("replaced");
        var s = new SnapSession(++_seq, mode, rect, target, zones, cands, placed);
        Session = s;
        payload["seq"] = s.Seq;
        payload["mode"] = mode;
        _show(new SnapShow(s.Seq, mode, rect, payload));
        return s;
    }

    /// <summary>snap.js#giveFocus: the window the person chose gets the keyboard (focus verb, 1.5 s); a test never focuses; a failure is logged.</summary>
    async ValueTask GiveFocusAsync(string? hwnd, string why)
    {
        if (string.IsNullOrEmpty(hwnd)) return;
        if (_test) { _log($"would give the focus to the snapped window ({why}); a test never focuses anything"); return; }
        PlatformReply r = await _send("focus", new JsonObject { ["hwnd"] = hwnd }, FocusTimeout).ConfigureAwait(true);
        if (!r.Ok) _log($"could not give the focus back after {why}: {r.Error}");
    }

    SnapNotice Refused(JsonElement? x, PlatformReply r, string fallback)
    {
        if (x is { } e) return _core.Notice("Could not snap " + SnapWords.AppOf(e), SnapWords.Why(e), MonitorOf(e)?.Work, SnapWords.NamesOf(e));
        return _core.Notice("Could not snap " + fallback, SnapWords.Cap(r.Error), null, null);
    }

    /// The log line after a pick: never an app name (unname), the sidecar's error when there is no answer.
    static string Line(JsonElement? x, PlatformReply r)
    {
        if (x is not { } e) return string.IsNullOrEmpty(r.Error) ? "no answer" : r.Error;
        string refused = SnapCore.Truthy(e, "ok") ? "" : " refused: " + SnapWords.Unname(SnapWords.Str(e, "reason"), SnapWords.NamesOf(e));
        return $"a window -> {SnapCore.Js(e, "to")}{refused}, {SnapCore.Js(e, "moves")} move(s), sidecar {SnapCore.Js(e, "ms")} ms";
    }

    static bool NoFit(JsonElement x) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty("fit", out var fit) && fit.ValueKind == JsonValueKind.False;

    /// "<App> does not fit the <zone name>": cap() then the last character cut, as snap.js#pickZone.
    static string NoFitTitle(string app, string zoneName)
    {
        string c = SnapWords.Cap(app);
        if (c.Length > 0) c = c.Substring(0, c.Length - 1);
        return c + " does not fit the " + zoneName;
    }

    static SnapScreen? MonitorOf(JsonElement x) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty("monitor", out var m) ? SnapCore.Screen(m) : null;

    static int[]? Ints(JsonElement o, string key, int n)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != n) return null;
        var r = new int[n];
        int i = 0;
        foreach (var e in v.EnumerateArray()) {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out r[i])) return null;
            i++;
        }
        return r;
    }

    static JsonArray Arr(params int[] v)
    {
        var a = new JsonArray();
        foreach (int i in v) a.Add((JsonNode)i);
        return a;
    }

    static JsonArray CandsJson(IEnumerable<SnapCandidate> cands)
    {
        var a = new JsonArray();
        foreach (var c in cands)
            a.Add((JsonNode)new JsonObject { ["hwnd"] = c.Hwnd, ["title"] = c.Title, ["exe"] = c.Exe, ["icon"] = c.Icon, ["minimized"] = c.Minimized });
        return a;
    }
}
