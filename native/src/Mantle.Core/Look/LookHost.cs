using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Logging;

namespace Mantle.Core.Look;

/// What the look host needs of the theme core: <c>{mode(), resolved(), set(mode)}</c>. Set answers null or why not.
public sealed record LookTheme(Func<string> Mode, Func<string> Resolved, Func<string, string?> Set)
{
    public static LookTheme For(ThemeCore t) => new(() => t.Mode, () => t.Theme, m => t.Set(JsonValue.Create(m)) is { Ok: false } r ? r.Reason ?? "it was refused" : null);
}

/// A look change for listeners: why (apply, status, desktop, theme, glass, accent, sync, guard), whether the vars moved, the view.
public sealed record LookChange(string Why, bool VarsChanged, JsonObject View);

/// <summary>
/// lib/look-host.js: Mantle's side of LOOK SYNC with Helm and Quartz. The local copy (<c>look-sync.json</c>:
/// <c>{lookSync, accent, glass}</c>) is what Mantle shows while sync is off and before the shared file is read;
/// <see cref="Vars"/> turns it into the <c>--lg-*</c> overrides for one theme (EMPTY at the seed values, so the default look is
/// the stylesheet's own); the sync instance shares theme, accent and glass through look.json.
/// Every file it changes goes through the store and the sync instance, so the read-only gate decides.
/// </summary>
public sealed class LookHost : IDisposable
{
    // the stylesheet's dark values (ui/shared/glass-light.css :root[data-theme="dark"]); panel adds to these
    public static readonly (double BlurPx, double Dim, double Panel) Dark = (5, 0.50, 0);
    public static readonly (string Name, int R, int G, int B, double A)[] DarkFills = {
        ("--lg-fill", 19, 20, 21, 0.83), ("--lg-fill-pop", 19, 20, 21, 0.83), ("--lg-card", 40, 41, 43, 0.62), ("--lg-field", 10, 11, 12, 0.46),
    };
    public static readonly int[] DarkBar = { 9, 10, 11 };
    // the stylesheet's light values (ui/shared/glass-light.css :root); the drift test reads the sheet
    public static readonly (double BlurPx, double Dim, double Panel, int Saturate) Light = (12, 0.45, 0, 150);
    public static readonly int[] LightFill = { 255, 255, 255 };
    public static readonly (string Name, int R, int G, int B, double A)[] LightPanels = { ("--lg-fill-strong", 255, 255, 255, 0.72), ("--lg-fill-pop", 255, 255, 255, 0.88) };
    public static readonly (double Lo, double Hi) PanelClamp = (0.30, 0.97);
    public const int GuardGraceMs = 4000;
    static readonly Dictionary<string, string> AppNames = new() { ["helm"] = "Helm", ["quartz"] = "Quartz", ["mantle"] = "Mantle" };

    readonly IStore _store;
    readonly LookTheme _theme;
    readonly ILogSink _log;
    readonly int _guardGraceMs;
    readonly object _lock = new();
    readonly List<Action<LookChange>> _listeners = new();

    // the local copy, in look-host's key order
    bool _lookSync = true;
    JsonObject _accent;
    JsonObject _glass;
    string? _localNote;

    LookStatus _status;
    JsonObject _desktop, _windows, _guard;
    JsonObject? _lock2;
    bool _applying;
    long? _softAt;
    Timer? _softTimer;
    string _lastMode;
    string _lastVars;

    public LookSync Sync { get; }

    /// <param name="store">look-sync.json's store (Stores.Open("look-sync.json"))</param>
    /// <param name="sync">how the sync instance is made; <c>App</c>, <c>Enabled</c> and <c>Local</c> are set here</param>
    public LookHost(IStore store, LookSyncOptions sync, LookTheme theme, ILogSink log, int guardGraceMs = 0)
    {
        _store = store;
        _theme = theme;
        _log = log;
        _guardGraceMs = guardGraceMs > 0 ? guardGraceMs : GuardGraceMs;
        var seed = LookSyncCore.Seed();
        _accent = (JsonObject)LookSyncCore.Clone(seed["accent"])!;
        _glass = (JsonObject)LookSyncCore.Clone(seed["glass"])!;
        Load();
        Sync = new LookSync(sync with {
            App = "mantle", Enabled = _lookSync,
            Local = () => new JsonObject { ["theme"] = _theme.Mode(), ["accent"] = LookSyncCore.Clone(_accent), ["glass"] = LookSyncCore.Clone(_glass) },
        });
        _status = Sync.CurrentStatus();
        _desktop = (JsonObject)JsJson.Parse("{\"ok\":null,\"text\":\"Not shared yet\",\"reason\":null,\"rev\":null,\"id\":null,\"title\":null,\"type\":null}")!;
        _windows = (JsonObject)JsJson.Parse("{\"ok\":null,\"text\":\"Not checked yet\",\"reason\":null}")!;
        _guard = (JsonObject)JsJson.Parse("{\"ok\":null,\"working\":false,\"text\":\"Not worked out yet\",\"reason\":null,\"alpha\":null,\"contrast\":null}")!;
        _lastMode = _theme.Mode();
        _lastVars = VarsSig();
        Sync.Apply += e => { lock (_lock) ApplyChanges(e); };
        Sync.Status += s => { lock (_lock) { _status = s; Emit("status"); } };
        Sync.Desktop += _ => { lock (_lock) Emit("desktop"); };
    }

    void Say(string line) { try { _log.Line(line); } catch { /* never worth a failure */ } }

    void Load()
    {
        if (_store.State == ReadState.Missing) return;
        if (_store.State != ReadState.Ok) {
            string code = _store.State == ReadState.Corrupt ? "not valid JSON" : (_store.Reason ?? _store.State.ToString());
            _localNote = "Mantle's saved look could not be read (" + code + "), so it starts from the defaults";
            Say("look: " + _localNote);
            return;
        }
        if (_store.TryGet("lookSync", out var ls) && ls.ValueKind is JsonValueKind.True or JsonValueKind.False) _lookSync = ls.GetBoolean();
        if (_store.TryGet("accent", out var a) && JsJson.FromElement(a) is var an && LookSyncCore.CheckValue("accent", an) == null) _accent = (JsonObject)an!;
        if (_store.TryGet("glass", out var g) && JsJson.FromElement(g) is var gn && LookSyncCore.CheckValue("glass", gn) == null) _glass = (JsonObject)gn!;
    }

    /// save(): the whole local copy, one write. -> null | the note (also logged).
    string? Save()
    {
        StoreWrite w;
        try {
            w = _store.UpdateAsync(d => {
                d["lookSync"] = _lookSync;
                d["accent"] = LookSyncCore.Clone(_accent);
                d["glass"] = LookSyncCore.Clone(_glass);
            }).AsTask().GetAwaiter().GetResult();
        } catch (Exception e) { w = StoreWrite.Failed(e.Message); }
        if (w.Ok) { _localNote = null; return null; }
        _localNote = "Mantle's own copy of the look could not be saved (" + w.Reason + ")";
        Say("look: " + _localNote);
        return _localNote;
    }

    /// The local copy as look-host's <c>_local()</c> shows it.
    public JsonObject Local() { lock (_lock) return new JsonObject { ["lookSync"] = _lookSync, ["accent"] = LookSyncCore.Clone(_accent), ["glass"] = LookSyncCore.Clone(_glass) }; }

    // ---- the vars -------------------------------------------------------------------------------

    static string A2(double n) => JsJson.Number(GlassAdapt.JsRound(n * 100) / 100);
    static double D(JsonNode? v) => LookSyncCore.TryNum(v, out var d) ? d : double.NaN;
    static string Rgba(int r, int g, int b, double a) => "rgba(" + r + "," + g + "," + b + "," + A2(a) + ")";

    /// <summary>vars(theme): the <c>--lg-*</c> overrides for one theme, in look-host's order. EMPTY at the seed values.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Vars(string theme)
    {
        lock (_lock) {
            var o = new List<KeyValuePair<string, string>>();
            void Put(string k, string v) { int i = o.FindIndex(p => p.Key == k); if (i >= 0) o[i] = new(k, v); else o.Add(new(k, v)); }
            int? hue = LookSyncCore.IsInt(_accent["hue"], out var h) ? (int)h : null;
            if (theme == "dark") {
                var g = (JsonObject)_glass["dark"]!;
                double blur = D(g["blurPx"]), dim = D(g["dim"]), panel = D(g["panel"]);
                if (blur != Dark.BlurPx) Put("--lg-blur", "blur(" + A2(blur) + "px)");
                if (dim != Dark.Dim) Put("--lg-fill-bar", "rgba(" + string.Join(",", DarkBar) + "," + A2(dim) + ")");
                if (panel != Dark.Panel)
                    foreach (var (k, r, gg, b, al) in DarkFills) Put(k, Rgba(r, gg, b, Math.Min(PanelClamp.Hi, Math.Max(PanelClamp.Lo, al + panel))));
                if (hue != null && Accent.Tokens(hue, "dark") is AccentTokens t) {
                    Put("--lg-acc", t.Acc); Put("--lg-acc-strong", t.Strong); Put("--lg-acc-on", t.On); Put("--lg-acc-soft", t.Soft);
                    Put("--lg-acc-line", t.Line); Put("--lg-acc-select", t.Select);
                    Put("--lg-focus", "0 0 0 1px rgba(20,21,22,.9), 0 0 0 3px rgba(" + t.R + "," + t.G + "," + t.B + ",.55)");
                }
            }
            if (theme == "light") {
                // A set hue is still not applied in light (Mantle's own light accent; unifying it is the user's call).
                var g = (JsonObject)_glass["light"]!;
                double blur = D(g["blurPx"]), dim = D(g["dim"]), panel = D(g["panel"]);
                if (blur != Light.BlurPx) Put("--lg-blur", "blur(" + A2(blur) + "px) saturate(" + Light.Saturate + "%)");
                if (dim != Light.Dim) Put("--lg-fill", "rgba(" + string.Join(",", LightFill) + "," + A2(dim) + ")");
                if (panel != Light.Panel)
                    foreach (var (k, r, gg, b, al) in LightPanels) Put(k, Rgba(r, gg, b, Math.Min(PanelClamp.Hi, Math.Max(PanelClamp.Lo, al + panel))));
            }
            return o;
        }
    }

    /// The vars as a JSON object (the 'theme' payload's <c>look</c>).
    public JsonObject VarsJson(string theme)
    {
        var o = new JsonObject();
        foreach (var (k, v) in Vars(theme)) o[k] = v;
        return o;
    }

    string VarsSig() => "[" + JsJson.Stringify(VarsJson("dark"), 0) + "," + JsJson.Stringify(VarsJson("light"), 0) + "]";

    // ---- events ---------------------------------------------------------------------------------

    void Emit(string why)
    {
        string sig = VarsSig();
        bool moved = sig != _lastVars;
        _lastVars = sig;
        foreach (var fn in _listeners.ToArray()) {
            try { fn(new LookChange(why, moved, View())); } catch (Exception e) { Say("look: a listener threw: " + e.Message); }
        }
    }

    public IDisposable OnChange(Action<LookChange> fn)
    {
        if (fn == null) throw new ArgumentNullException(nameof(fn), "look.onChange needs a function");
        lock (_lock) _listeners.Add(fn);
        return new Off(() => { lock (_lock) _listeners.Remove(fn); });
    }

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) => _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }

    void ApplyChanges(LookApply e)
    {
        var ch = e.Changes;
        string from = e.By != null && AppNames.TryGetValue(e.By, out var n) ? n : "the shared look";
        var did = new List<string>();
        if (ch.TryGetPropertyValue("accent", out var acc) && LookSyncCore.J(acc) != LookSyncCore.J(_accent)) {
            _accent = (JsonObject)LookSyncCore.Clone(acc)!;
            did.Add("accent " + (_accent["hue"] == null ? "Mantle's own" : "hue " + LookSyncCore.J(_accent["hue"])));
        }
        if (ch.TryGetPropertyValue("glass", out var gl) && LookSyncCore.J(gl) != LookSyncCore.J(_glass)) {
            _glass = (JsonObject)LookSyncCore.Clone(gl)!;
            JsonNode d = _glass["dark"]!, l = _glass["light"]!;
            string P(JsonNode x, string k) => LookSyncCore.J(x[k]);
            did.Add($"glass dark blur {P(d, "blurPx")} dim {P(d, "dim")} panel {P(d, "panel")}, light blur {P(l, "blurPx")} dim {P(l, "dim")} panel {P(l, "panel")}");
        }
        if (did.Count > 0) Save();
        if (ch.TryGetPropertyValue("theme", out var th) && LookSyncCore.Str(th) is string mode && mode != _theme.Mode()) {
            _applying = true;
            try {
                string? no = _theme.Set(mode);
                if (no != null) Say("look: the shared theme " + mode + " could not be applied: " + no);
                else did.Add("theme " + mode);
            } catch (Exception x) { Say("look: applying the shared theme threw: " + x.Message); }
            finally { _applying = false; }
            _lastMode = _theme.Mode();
        }
        if (did.Count > 0) Say($"look: following {from} ({e.Cause}, rev {JsJson.Number(e.Rev)}): {string.Join(", ", did)}");
        Emit("apply");
    }

    // ---- what Mantle's own changes do -----------------------------------------------------------

    /// <summary>Call from the theme core's listener: a theme the user picked in Mantle is shared (never one applied from sync).</summary>
    public async Task ThemeChangedAsync(string? mode)
    {
        Task<LookWrite>? pub = null;
        lock (_lock) {
            if (_applying || string.IsNullOrEmpty(mode) || mode == _lastMode) { Emit("theme"); return; }
            _lastMode = mode;
            if (Sync.Enabled) pub = Sync.PublishAsync("theme", mode);
        }
        if (pub != null) {
            var r = await pub.ConfigureAwait(false);
            lock (_lock) { if (!r.Ok) Say("look: the theme could not be shared: " + r.Reason); Emit("theme"); }
        }
        lock (_lock) Emit("theme");
    }

    /// Read only NOW (a newer-schema file on disk), asked of the file itself, not the last status.
    string? ReadOnly()
    {
        if (!Sync.Enabled) return _status.State == "newer" ? (_status.Reason ?? "The shared look is from a newer version; update Mantle") : null;
        return Sync.ReadOnlyNow();
    }

    async Task<string?> Share(string field, JsonNode value)
    {
        if (!Sync.Enabled) return null;
        var r = await Sync.PublishAsync(field, value).ConfigureAwait(false);
        return r.Ok ? null : "Saved in Mantle, but not shared: " + r.Reason;
    }

    static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };

    string LookNow() => _theme.Resolved() == "light" ? "light" : "dark";

    /// <summary>look.glass <c>{blurPx?, dim?, panel?, look?}</c>: edits the glass of the look Mantle shows now unless <c>look</c> names one.</summary>
    public async Task<JsonObject> SetGlassAsync(JsonObject? patch)
    {
        JsonObject next;
        lock (_lock) {
            if (patch == null) return Fail("the glass change came without values");
            if (patch.TryGetPropertyValue("look", out var lk) && LookSyncCore.Str(lk) is not ("light" or "dark")) return Fail("the glass change named no look (light or dark)");
            string? ro = ReadOnly(); if (ro != null) return Fail(ro);
            string look = LookSyncCore.Str(patch["look"]) ?? LookNow();
            var g = (JsonObject)LookSyncCore.Clone(_glass[look])!;
            foreach (var k in new[] { "blurPx", "dim", "panel" })
                if (patch.TryGetPropertyValue(k, out var v)) g[k] = LookSyncCore.TryNum(v, out var d) ? LookSyncCore.N(d) : LookSyncCore.N(double.NaN);
            next = (JsonObject)LookSyncCore.Clone(_glass)!;
            next[look] = g;
            string? bad = LookSyncCore.CheckValue("glass", next);
            if (bad != null) return Fail(Regex.Replace(bad, @"^glass\.(dark|light)\.", ""));
            _glass = next;
            Save();
            Say($"look: {look} glass set in Mantle: blur {LookSyncCore.J(g["blurPx"])} dim {LookSyncCore.J(g["dim"])} panel {LookSyncCore.J(g["panel"])}");
            Emit("glass");
        }
        string? note = await Share("glass", LookSyncCore.Clone(next)!).ConfigureAwait(false);
        lock (_lock) { Emit("glass"); return WithNote(View(), note); }
    }

    static JsonObject WithNote(JsonObject v, string? note) { if (note != null) v["note"] = note; return v; }

    /// <summary>look.accent: <c>hue</c> null = Mantle's own; else an integer 0..360 (applied in dark only).</summary>
    public async Task<JsonObject> SetAccentAsync(JsonNode? hue)
    {
        JsonObject v;
        lock (_lock) {
            string? ro = ReadOnly(); if (ro != null) return Fail(ro);
            v = new JsonObject { ["hue"] = LookSyncCore.Clone(hue) };
            string? bad = LookSyncCore.CheckValue("accent", v);
            if (bad != null) return Fail(bad);
            _accent = v;
            Save();
            Say("look: accent set in Mantle: " + (v["hue"] == null ? "Mantle's own" : "hue " + LookSyncCore.J(v["hue"])));
            Emit("accent");
        }
        string? note = await Share("accent", LookSyncCore.Clone(v)!).ConfigureAwait(false);
        lock (_lock) { Emit("accent"); return WithNote(View(), note); }
    }

    /// <summary>look.sync: the per-app switch "Match Helm and Quartz", saved in the local copy.</summary>
    public async Task<JsonObject> SetSyncAsync(JsonNode? on)
    {
        if (!LookSyncCore.IsBool(on, out var b)) return Fail("the switch needs on or off");
        string? err;
        lock (_lock) { _lookSync = b; err = Save(); }
        var r = await Sync.SetEnabledAsync(b).ConfigureAwait(false);
        lock (_lock) {
            Say("look: sync with Helm and Quartz turned " + (b ? "on" : "off") + (!r.Ok ? " (" + r.Reason + ")" : ""));
            Emit("sync");
            return WithNote(View(), err != null ? (b ? "On" : "Off") + " now, but " + char.ToLowerInvariant(err[0]) + err[1..] + ", so it goes back at the next start" : null);
        }
    }

    // ---- the view (Mantle Settings) ---------------------------------------------------------------

    string SyncText()
    {
        var s = _status;
        string own = _localNote != null ? ". " + _localNote : "";
        if (!Sync.Enabled || s.State == "off") return "Off: Mantle keeps its own look" + own;
        string when = "";
        if (s.At != null && DateTimeOffset.TryParse(s.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) when = " at " + t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (s.State == "ok") {
            string who = s.By != null ? "last change from " + (AppNames.TryGetValue(s.By, out var n) ? n : s.By) + when : "no change yet";
            return "On · in sync (" + who + ", rev " + (s.Rev is double rv ? JsJson.Number(rv) : "null") + ")" + (s.Reason != null ? ". " + s.Reason : "") + own;
        }
        if (s.State == "missing") return "On · not shared yet" + (s.Reason != null && s.Reason != "Not shared yet" ? ": " + Regex.Replace(s.Reason, @"^Not shared yet:\s*", "") : "");
        if (s.State == "newer") return "On · read only: the shared look is from a newer version";
        if (s.State == "corrupt") return "On · the shared look file was damaged";
        return "On · not shared";
    }

    static JsonNode? S(string? s) => s == null ? null : JsonValue.Create(s);

    /// <summary>look.get: <c>{ok, theme, sync{on,state,text,reason,recovered,rev,by,at}, glass, glassLook, glassBoth, ranges, accent, desktop, windows, lock, guard}</c>.</summary>
    public JsonObject View()
    {
        lock (_lock) {
            var s = _status;
            string st = !Sync.Enabled ? "off" : s.State;
            string? reason = st == "ok" && s.Recovered != null ? (s.Reason ?? _localNote) : st is "ok" or "off" ? _localNote : (s.Reason ?? _localNote);
            string look = LookNow();
            return new JsonObject {
                ["ok"] = true,
                ["theme"] = _theme.Resolved(),
                ["sync"] = new JsonObject {
                    ["on"] = Sync.Enabled, ["state"] = st, ["text"] = SyncText(), ["reason"] = S(reason),
                    ["recovered"] = S(st == "ok" ? s.Recovered : null),
                    ["rev"] = s.Rev is double rv && Math.Floor(rv) == rv ? LookSyncCore.N(rv) : null,
                    ["by"] = S(s.By != null ? (AppNames.TryGetValue(s.By, out var n) ? n : s.By) : null), ["at"] = S(s.At),
                },
                ["glass"] = LookSyncCore.Clone(_glass[look]),
                ["glassLook"] = look,
                ["glassBoth"] = LookSyncCore.Clone(_glass),
                ["ranges"] = LookSyncCore.RangesJson(),
                ["accent"] = LookSyncCore.Clone(_accent),
                ["desktop"] = LookSyncCore.Clone(_desktop),
                ["windows"] = LookSyncCore.Clone(_windows),
                ["lock"] = _lock2 != null ? LookSyncCore.Clone(_lock2) : new JsonObject(),
                ["guard"] = LookSyncCore.Clone(_guard),
            };
        }
    }

    /// lib/look-desktop.js's state for the view: <c>{desktop, windows, lock?, guard?}</c>.
    public void SetDesktop(JsonObject st)
    {
        lock (_lock) {
            if (st["desktop"] is not JsonObject d || st["windows"] is not JsonObject w) return;
            _desktop = (JsonObject)LookSyncCore.Clone(d)!;
            _windows = (JsonObject)LookSyncCore.Clone(w)!;
            _lock2 = st["lock"] is JsonObject l ? (JsonObject)LookSyncCore.Clone(l)! : null;
            if (st["guard"] is JsonObject g) _guard = (JsonObject)LookSyncCore.Clone(g)!;
            Emit("desktop");
        }
    }

    /// <summary>The glass the shared top-band values are worked out for: the SHARED one, else Mantle's own.
    /// -> (dark dim, dark blur, light dim, light blur, from 'shared' | 'mantle')</summary>
    public (double Dim, double BlurPx, double LightDim, double LightBlurPx, string From) GuardGlass()
    {
        lock (_lock) {
            var sh = Sync.Shared()?["glass"]?["v"] as JsonObject;
            var ownL = (JsonObject)_glass["light"]!;
            (double, double) light = sh?["light"] is JsonObject sl && LookSyncCore.TryNum(sl["dim"], out var ld) && LookSyncCore.TryNum(sl["blurPx"], out var lb) ? (ld, lb) : (D(ownL["dim"]), D(ownL["blurPx"]));
            if (sh?["dark"] is JsonObject sd && LookSyncCore.TryNum(sd["dim"], out var dd) && LookSyncCore.TryNum(sd["blurPx"], out var db)) return (dd, db, light.Item1, light.Item2, "shared");
            var own = (JsonObject)_glass["dark"]!;
            return (D(own["dim"]), D(own["blurPx"]), light.Item1, light.Item2, "mantle");
        }
    }

    /// <summary>What the TOP BAR does in <paramref name="theme"/>: dark + sync on = the shared alpha, else its own guard. A soft
    /// failure is held as "being worked out" for the grace period, then said.</summary>
    public GuardUse TopGuard(string theme)
    {
        lock (_lock) {
            JsonObject? record = null;
            try { record = Sync.CurrentDesktop().Record; } catch { record = null; }
            var g = (JsonObject)_glass["dark"]!;
            bool working = LookSyncCore.IsBool(_guard["working"], out var wk) && wk;
            var u = LookGuard.Use(theme == "dark", Sync.Enabled, record, D(g["dim"]), D(g["blurPx"]), working);
            if (!(u.Failure && u.Soft)) { _softAt = null; return u; }
            long t = Environment.TickCount64;
            _softAt ??= t;
            long left = _softAt.Value + _guardGraceMs - t;
            if (left <= 0) return u;
            _softTimer ??= new Timer(_ => { lock (_lock) { _softTimer?.Dispose(); _softTimer = null; Emit("guard"); } }, null, left + 20, Timeout.Infinite);
            return new GuardUse(false, null, null, null, "the shared darkening is being worked out", false, false);
        }
    }

    /// <summary>start(): read the shared look (seed when missing), watch it, and log the switch with the folder NAME only.</summary>
    public LookHost Start()
    {
        // said first: in Electron the read and the seed run after this line, on the next turn of the loop
        Say("look: sync with Helm and Quartz is " + (Sync.Enabled ? "on" : "off") + "; shared folder " + Regex.Replace(Sync.Dir, @"^.*[\\/](look-sync[^\\/]*)$", "$1"));
        Sync.Start();
        return this;
    }

    /// Re-read the shared look: on resume from sleep ("the PC woke from sleep") and on unlock ("the screen was unlocked").
    public void Refresh(string why) { Say("look: re-reading the shared look (" + why + ")"); Sync.Refresh(); }

    public void Dispose()
    {
        Sync.Close();
        lock (_lock) { _listeners.Clear(); _softTimer?.Dispose(); _softTimer = null; }
    }
}
