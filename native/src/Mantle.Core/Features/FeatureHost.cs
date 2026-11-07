using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Keys;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Features;

// The feature host: the native port of lib/features.js createFeatures (lib/features/README.md is the
// spec). Its promises are Electron's:
//
//   ISOLATION  a feature that breaks the contract, throws in InstallAsync, or throws in a chord handler
//              is that feature DOWN with its reason on the strip and in the tray, never a crashed
//              Mantle and never another feature down.
//   KEYS       a chord is taken from Windows only while its feature is ON (tray switch) AND healthy
//              AND Mantle is live AND it is not resting. Anything else and Windows has the key.
//              Observed keys are never taken at all.
//   EXIT       every StopAsync runs on the "put Windows back" road, all at once, each with its own
//              deadline: one that throws or hangs is logged and never holds up the quit.
//
// Threading: host thread only. Construct it on the host thread; timers and replies come back there.

/// <summary>Everything the host needs, handed in (so it never depends on the generated catalog).</summary>
public sealed class FeatureHostOptions
{
    public FeatureFlags Flags { get; init; }
    public required IPlatform Platform { get; init; }
    public required ICommandBus Commands { get; init; }
    public required IEvents Events { get; init; }
    public required IStores Stores { get; init; }
    public required IShellStateHub State { get; init; }
    /// One log line, without the "mantle: " prefix.
    public Action<string>? Log { get; init; }
    public HostClock? Clock { get; init; }
    /// lib/features.js INSTALL_MS.
    public TimeSpan InstallDeadline { get; init; } = TimeSpan.FromSeconds(10);
    /// lib/features.js STOP_MS: per feature; they run at once, so this is also the whole wait.
    public TimeSpan StopDeadline { get; init; } = TimeSpan.FromMilliseconds(2500);
    /// lib/features.js APPLY_COALESCE_MS: several health changes in one turn become ONE hotkey call.
    public TimeSpan ApplyCoalesce { get; init; } = TimeSpan.FromMilliseconds(30);
    /// The hotkey call's own deadline.
    public TimeSpan HotkeyTimeout { get; init; } = TimeSpan.FromMilliseconds(4000);
}

/// <summary>One stop's line in the log.</summary>
public sealed record StopLine(string Name, string Line);

/// <summary>The tray's rows for the features (lib/features.js trayItems).</summary>
public sealed record FeatureTrayRow(string Name, string Label, bool Checked, bool Enabled, string? ReasonLine);

public sealed partial class FeatureHost
{
    readonly FeatureHostOptions _o;
    readonly FeatureFlags _flags;
    readonly HostClock _clock;
    readonly Action<string> _log;
    readonly Dictionary<string, F> _features = new(StringComparer.Ordinal);
    readonly List<string> _order = new();
    readonly Dictionary<string, string> _claims = new(StringComparer.Ordinal);
    readonly IStore _switches;
    readonly List<Action<string, string>> _changeFns = new();
    readonly List<Task> _installs = new();

    // The keyboard side (lib/features.js K).
    bool _live;
    string? _releasedWhy;
    string? _platformDown;
    HashSet<string>? _taken;
    string? _err;
    int _applying;
    string? _lastSent;
    IDisposable? _applyTimer;
    Task<IReadOnlyList<StopLine>>? _stopping;

    public FeatureHost(FeatureHostOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        _flags = o.Flags;
        _clock = o.Clock ?? new HostClock();
        _log = o.Log ?? (_ => { });
        _switches = o.Stores.Open("features.json");
        if (_switches.State is not ReadState.Ok and not ReadState.Missing)
            Log($"features: the saved on/off switches were not used: {_switches.Reason ?? _switches.State.ToString()}; every feature starts at its default");

        o.Platform.On("chord", e => { if (Str(e.Body, "chord") is string c) RouteChord(c, "hook"); });
        o.Platform.On("key", e => { if (Str(e.Body, "key") is string k) RouteKey(k, "hook", null); });
        o.Platform.On("switcher", e => { if (Str(e.Body, "op") == "hook") OnHook(e.Body); });
        o.Platform.OnStatus(OnPlatformStatus);
        o.Platform.OnReady(() =>
        {
            if (!_live || _flags.Test || _stopping != null) return;
            _lastSent = null;   // a new platform process holds no chords, whatever we sent the last one
            ScheduleApply("the link to Windows started again");
        });
    }

    /// The run's flags.
    public FeatureFlags Flags => _flags;

    /// How many features are known (loaded or not).
    public int Count => _features.Count;

    /// Settles when every install started by <see cref="Load"/> has settled or passed its deadline.
    public Task Installed => Task.WhenAll(_installs.ToArray());

    /// The names of the features resting for a game right now.
    public IReadOnlyList<string> Resting => _order.Select(n => _features[n]).Where(f => f.Resting != null).Select(f => f.Name).ToArray();

    /// The one game watch every feature shares (lib/features/_game-watch.js).
    public GameWatch GameWatch { get; } = new();

    /// Where host.rest(on, why) goes (the RestCoordinator sets itself here); null = no rest in this build.
    public IRestHost? RestHost { get; set; }

    /// True once StopAllAsync has begun.
    public bool IsStopping => _stopping != null;

    void Log(string line) { try { _log(line); } catch { /* a log that throws must not break the host */ } }

    static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Words(Exception e) => FeatureRules.OneLine(e is AggregateException a && a.InnerException != null ? a.InnerException.Message : e.Message);

    // ---- one feature's record ----

    sealed class F
    {
        public F(string name, string label) { Name = name; Label = label; }
        public readonly string Name;
        public string Label;
        public FeatureDescriptor? D;
        public IReadOnlyList<string> Chords = Array.Empty<string>();
        public IReadOnlyList<string> Observe = Array.Empty<string>();
        public bool Enabled = true;
        public bool Ok = true;
        public string Reason = "starting";
        public bool Running;
        public bool Stopped;
        public bool Rest;
        public string? Resting;
        public Task? RestP;
        public IFeature? Handle;
        public string? Blocked;
        public bool LateInstall;
        public Task<FeatureAnswer>? Restarting;
        public string? KeysAway;
        public int Gen;
        public CancellationTokenSource? Cts;
        public IStore? Store;
        public string? SettingsNote;
        public readonly Dictionary<string, List<Action<string>>> ChordHandlers = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Action<string>>> KeyHandlers = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Action<JsonElement, string>>> OptionHandlers = new(StringComparer.Ordinal);
        public readonly List<Action<bool>> EnabledHandlers = new();
        public readonly List<Action<RestSignal>> RestHandlers = new();
        public readonly List<IDisposable> Offs = new();
        public readonly Dictionary<string, IDisposable> Commands = new(StringComparer.Ordinal);
        public readonly Dictionary<string, IDisposable> Replays = new(StringComparer.Ordinal);
    }

    FeatureFacts Facts(F f) => new(f.Label, f.Enabled, f.Ok, f.Reason, f.Chords, f.Stopped, f.Resting, f.KeysAway, f.SettingsNote);

    KeysView View() => new(_flags.Test, _live, _releasedWhy, _platformDown, _taken, _err, _applying > 0);

    void Publish(F f)
    {
        var piece = FeatureRules.Piece(Facts(f), View());
        var changed = _o.State.Set(s => s.Features.TryGetValue(f.Name, out var cur) && cur == piece ? s : s with { Features = s.Features.SetItem(f.Name, piece) });
        if (changed) Changed("state", f.Name);
    }

    void PublishAll() { foreach (var n in _order) Publish(_features[n]); }

    void Changed(string what, string name)
    {
        foreach (var fn in _changeFns.ToArray())
        {
            try { fn(what, name); } catch (Exception e) { Log($"features: a change listener threw: {Words(e)}"); }
        }
    }

    // ---- the keyboard: what the hook should take, and telling the platform ----

    /// <summary>What the hook should take and watch now: chords of features on, healthy, running and not
    /// handing their keys back; observed keys of features on and running. Table order.</summary>
    public (string[] Chords, string[] Observe) Wanted()
    {
        var chords = new HashSet<string>(StringComparer.Ordinal);
        var observe = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in _order)
        {
            var f = _features[n];
            if (!f.Running || f.Stopped || !f.Enabled) continue;
            foreach (var k in f.Observe) observe.Add(k);
            if (!f.Ok || f.KeysAway != null) continue;
            foreach (var c in f.Chords) chords.Add(c);
        }
        return (ChordTable.InTableOrder(chords, ChordTable.Chords), ChordTable.InTableOrder(observe, ChordTable.ObservedKeys));
    }

    void ScheduleApply(string why)
    {
        if (_applyTimer != null) return;
        _applyTimer = _clock.Later(_o.ApplyCoalesce, () =>
        {
            _applyTimer = null;
            _ = ApplyAsync(why);
        });
    }

    async Task ApplyAsync(string why)
    {
        try
        {
            if (_flags.Test || !_live || _stopping != null) { PublishAll(); return; }
            var (chords, observe) = Wanted();
            var key = string.Join(",", chords) + "|" + string.Join(",", observe);
            if (key == _lastSent && _err == null) { PublishAll(); return; }
            // Nothing wanted and nothing ever sent to THIS platform process: it holds no chords already.
            if (chords.Length == 0 && observe.Length == 0 && _lastSent == null && _err == null)
            {
                _lastSent = key; _taken = new HashSet<string>(StringComparer.Ordinal); PublishAll(); return;
            }
            var st = _o.Platform.Status;
            if (st.State == PlatformState.Down) { _platformDown = $"the sidecar cannot be asked: {st.Reason}"; PublishAll(); return; }
            _applying++;
            PublishAll();
            var args = new JsonObject
            {
                ["chords"] = new JsonArray(chords.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["observe"] = new JsonArray(observe.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            };
            PlatformReply r;
            try { r = await _o.Platform.CallAsync("hotkey", args, _o.HotkeyTimeout); }
            finally { _applying--; }
            if (r.Ok)
            {
                _lastSent = key;
                _err = null;
                OnHook(r.Result);
                Log($"features: keys {why}: taking {(chords.Length > 0 ? string.Join(", ", chords) : "no chords")}; watching {(observe.Length > 0 ? string.Join(", ", observe) : "no keys")}");
            }
            else
            {
                var m = r.Error ?? "no reason was given";
                _err = m.Contains("needs winTap", StringComparison.Ordinal) ? $"the sidecar exe predates feature chords (it said: {m}): rebuild the sidecar" : m;
                _lastSent = null;
                Log($"features: keys {why}: the sidecar would not take them: {_err}");
            }
            PublishAll();
        }
        catch (Exception e)
        {
            Log($"features: applying the keys threw: {Words(e)}");
        }
    }

    /// The hook's word ({installed, chords, reason}) from a reply or a push.
    void OnHook(JsonElement h)
    {
        if (h.ValueKind != JsonValueKind.Object) return;
        var installed = h.TryGetProperty("installed", out var i) && i.ValueKind == JsonValueKind.True;
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (installed && h.TryGetProperty("chords", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray()) if (c.ValueKind == JsonValueKind.String) set.Add(c.GetString()!);
        _taken = set;
        var reason = Str(h, "reason");
        if (!installed && reason != null && Wanted().Chords.Length > 0 && _live) _err = reason;
        PublishAll();
    }

    void OnPlatformStatus(PlatformStatus st)
    {
        if (st.State == PlatformState.Ok) { _platformDown = null; PublishAll(); return; }
        _platformDown = $"the sidecar is not running ({st.Reason})";
        _taken = null;
        _lastSent = null;
        PublishAll();
    }

    /// <summary>Mantle owns the shell and the platform is up (every platform start): take the keys.</summary>
    public Task ApplyLiveAsync(string? why = null)
    {
        if (_flags.Test) { PublishAll(); return Task.CompletedTask; }
        _live = true;
        _releasedWhy = null;
        _platformDown = null;
        _lastSent = null;
        _applyTimer?.Dispose(); _applyTimer = null;
        return ApplyAsync(why ?? "Mantle took over the shell");
    }

    /// <summary>The restore road already cleared every chord with the hook: the keys are Windows'.</summary>
    public void Released(string why)
    {
        _live = false;
        _releasedWhy = $"Mantle put Windows back: {why}";
        _taken = null;
        _err = null;
        _lastSent = null;
        PublishAll();
    }

    // ---- routing: the hook (or a test) says a chord or key happened ----

    void RunHandler(F f, string what, Action a)
    {
        try { a(); }
        catch (Exception e) { HandlerFailed(f, what, e); }
    }

    void HandlerFailed(F f, string what, Exception e)
    {
        Log($"{f.Name}: its {what} handler threw: {e}");
        // A feature that cannot answer its own key gives the key back to Windows, and the strip says so.
        SetHealth(f, false, $"its {what} handler failed ({Words(e)}), so its keys went back to Windows");
    }

    KeyDelivery RouteChord(string chord, string via)
    {
        if (!_claims.TryGetValue(chord, out var name) || !_features.TryGetValue(name, out var f))
        {
            Log($"features: {chord} arrived ({via}) but no feature claims it");
            return new(false, null, "no feature claims it");
        }
        var why = f.Resting != null ? $"it is {FeatureRules.RestWords(f.Resting)}" : f.Stopped ? "it was stopped" : !f.Running ? "it is not running"
            : !f.Enabled ? "it is turned off" : !f.Ok ? $"it is not healthy ({f.Reason})" : null;
        if (why != null)
        {
            Log($"{f.Name}: {chord} arrived ({via}) but {why}; Windows keeps it from now on");
            if (!_flags.Test) ScheduleApply($"{chord} arrived for a feature that cannot take it");
            return new(false, f.Name, why);
        }
        if (!f.ChordHandlers.TryGetValue(chord, out var hs) || hs.Count == 0)
        {
            Log($"{f.Name}: {chord} arrived ({via}) and it has no OnChord handler for it");
            return new(false, f.Name, "the feature has no handler for it");
        }
        foreach (var fn in hs.ToArray()) RunHandler(f, chord, () => fn(chord));
        // A taken volume key never reaches the hook's observe check (the chord swallowed it), so the
        // features that only OBSERVE that key hear it from here: observing stays "every keydown".
        if (ChordTable.IsObservedKey(chord)) RouteKey(chord, "chord", f.Name);
        return new(true, f.Name, null, hs.Count);
    }

    KeyDelivery RouteKey(string key, string via, string? skip)
    {
        var n = 0;
        foreach (var name in _order)
        {
            var f = _features[name];
            if (f.Name == skip) continue;
            if (!f.Running || f.Stopped || !f.Enabled || !f.Observe.Contains(key)) continue;
            if (!f.KeyHandlers.TryGetValue(key, out var hs)) continue;
            foreach (var fn in hs.ToArray()) { RunHandler(f, key, () => fn(key)); n++; }
        }
        if (n == 0 && via != "hook" && via != "chord") Log($"features: {key} ({via}) reached no handler");
        return new(n > 0, null, n > 0 ? null : "no feature that is on observes it", n);
    }

    /// <summary>A --test run only: deliver a chord as if the hook took it (--test-chord, features.fire).</summary>
    /// <exception cref="InvalidOperationException">In a live run, or for a name that is not a chord.</exception>
    public KeyDelivery FireChord(string chord, string? via = null)
    {
        if (!_flags.Test) throw new InvalidOperationException("FireChord is test-only (--test)");
        if (!ChordTable.IsChord(chord)) throw new InvalidOperationException($"\"{chord}\" is not a chord ({string.Join(", ", ChordTable.Chords)})");
        Log($"features: {chord} fired by {via ?? "a test"} (test mode: the hook never took it)");
        return RouteChord(chord, via ?? "test");
    }

    /// <summary>A --test run only: deliver an observed key (--test-key).</summary>
    public KeyDelivery FireKey(string key, string? via = null)
    {
        if (!_flags.Test) throw new InvalidOperationException("FireKey is test-only (--test)");
        if (!ChordTable.IsObservedKey(key)) throw new InvalidOperationException($"\"{key}\" is not an observed key ({string.Join(", ", ChordTable.ObservedKeys)})");
        Log($"features: {key} fired by {via ?? "a test"} (test mode)");
        return RouteKey(key, via ?? "test", null);
    }

    KeyDelivery RunChordFor(F asker, string chord, string via, bool dryRun)
    {
        if (!ChordTable.IsChord(chord)) return new(false, null, $"\"{chord}\" is not a chord");
        _features.TryGetValue(_claims.GetValueOrDefault(chord) ?? "", out var g);
        var why = g == null ? $"no feature takes {chord}"
            : g.Resting != null ? $"{g.Label} is stopped ({FeatureRules.RestWords(g.Resting)})"
            : g.Stopped ? $"{g.Label} is stopped" : !g.Running ? $"{g.Label} is not running"
            : !g.Enabled ? $"{g.Label} is switched off in the tray" : !g.Ok ? $"{g.Label} is not healthy ({g.Reason})" : null;
        if (why != null) return new(false, g?.Name, why);
        if (!g!.ChordHandlers.TryGetValue(chord, out var hs) || hs.Count == 0) return new(false, g.Name, $"{g.Label} has no handler for {chord}");
        if (dryRun) return new(false, g.Name, null, 0, true);
        Log($"{g.Name}: {chord} run by {asker.Name} ({via})");
        foreach (var fn in hs.ToArray()) RunHandler(g, chord, () => fn(chord));
        return new(true, g.Name, null, hs.Count);
    }

    // ---- health and the switch ----

    void SetHealth(F f, bool ok, string? reason)
    {
        var nextReason = !string.IsNullOrWhiteSpace(reason) ? FeatureRules.OneLine(reason) : ok ? "working" : "not working, and the feature did not say why";
        if (f.Ok == ok && f.Reason == nextReason) return;
        var flipped = f.Ok != ok;
        f.Ok = ok;
        f.Reason = nextReason;
        if (flipped) Log($"{f.Name}: {(ok ? "healthy" : "NOT healthy")}: {nextReason}");
        Publish(f);
        if (flipped && f.Chords.Count > 0) ScheduleApply($"{f.Name} is {(ok ? "healthy" : "not healthy")}");
    }

    /// <summary>The tray's and Mantle Settings' switch, with the reasons a greyed-out checkbox stands for, in words.</summary>
    public FeatureAnswer TrySetEnabled(string name, bool on, string who)
    {
        if (!_features.TryGetValue(name, out var f)) return new(false, $"Mantle has no feature called \"{name}\"");
        if (f.D == null) return new(false, $"{f.Label} did not load, so it cannot be switched ({f.Reason})", Info(f));
        if (f.Stopped && f.Resting == null) return new(false, $"{f.Label} was stopped when Mantle put Windows back; it comes back when Mantle restarts", Info(f));
        SetEnabled(name, on, who);
        var saved = _switches.TryGet(name, out var v) && v.ValueKind == (on ? JsonValueKind.True : JsonValueKind.False);
        var err = f.Enabled == on && saved ? null : "the switch did not move";
        return new(err == null, err, Info(f));
    }

    /// <summary>Flip a feature's switch (no refusal words: <see cref="TrySetEnabled"/> has them).</summary>
    public void SetEnabled(string name, bool on, string who)
    {
        if (!_features.TryGetValue(name, out var f) || f.Enabled == on) return;
        f.Enabled = on;
        var write = _switches.SetAsync(name, JsonValue.Create(on));
        Log($"{name}: turned {(on ? "on" : "off")} by {who}");
        _ = SaidIfNotSaved(write, name);
        foreach (var fn in f.EnabledHandlers.ToArray()) RunHandler(f, "OnEnabled", () => fn(on));
        Publish(f);
        Changed("enabled", name);
        ScheduleApply($"{name} turned {(on ? "on" : "off")}");
    }

    async Task SaidIfNotSaved(ValueTask<StoreWrite> write, string name)
    {
        try
        {
            var w = await write;
            if (!w.Ok) Log($"{name}: the switch was NOT saved for next time: {w.Reason}");
        }
        catch (Exception e) { Log($"{name}: the switch was NOT saved for next time: {Words(e)}"); }
    }

    // ---- options ----

    IStore Store(F f)
    {
        if (f.Store != null) return f.Store;
        var s = _o.Stores.Open($"features\\{f.Name}.json");
        f.Store = s;
        if (s.State is not ReadState.Ok and not ReadState.Missing)
        {
            f.SettingsNote = $"its saved settings were not used: {s.Reason ?? s.State.ToString()}";
            Log($"{f.Name}: {f.SettingsNote}");
        }
        return s;
    }

    static IReadOnlyList<FeatureOption> OptionsOf(F f) => f.D?.Options ?? Array.Empty<FeatureOption>();

    OptionView ViewOf(F f, FeatureOption opt)
    {
        string? note = null;
        var dflt = FeatureOptions.ToElement(opt.Default);
        var value = dflt;
        var isDefault = true;
        if (Store(f).TryGet(opt.Key, out var saved) && saved.ValueKind != JsonValueKind.Null)
        {
            var c = FeatureOptions.Check(opt, saved);
            if (!c.Ok) note = $"the saved value is not valid, so the default is used ({c.Reason})";
            else { value = c.Value; isDefault = value.GetRawText() == dflt.GetRawText(); }
        }
        if (opt.Kind == FeatureOptionKind.Folder && value.ValueKind == JsonValueKind.String)
            note = FolderProblem(value.GetString()!, "that folder does not exist any more") ?? note;
        return new(opt.Key, opt.Label, opt.Help ?? "", FeatureOptions.KindName(opt.Kind), dflt, value, isDefault, note, opt);
    }

    static string? FolderProblem(string path, string missing)
    {
        try
        {
            if (Directory.Exists(path)) return null;
            return File.Exists(path) ? "that path is not a folder" : missing;
        }
        catch (Exception e) { return $"that folder cannot be read ({e.GetType().Name})"; }
    }

    /// <summary>Mantle Settings' write. JSON null = back to the default. Text and folder values are never logged.</summary>
    public async ValueTask<FeatureAnswer> SetOptionAsync(string name, string key, JsonElement value, string who)
    {
        if (!_features.TryGetValue(name, out var f) || f.D == null)
            return new(false, f != null ? $"{f.Label} did not load ({f.Reason})" : $"Mantle has no feature called \"{name}\"");
        var opt = OptionsOf(f).FirstOrDefault(x => x.Key == key);
        if (opt == null) return new(false, $"{f.Label} has no option called \"{key}\"");
        if (_stopping != null) return new(false, "Mantle is putting Windows back; nothing is changed now", null, ViewOf(f, opt));
        var c = FeatureOptions.Check(opt, value);
        if (!c.Ok) return new(false, c.Reason, null, ViewOf(f, opt));
        if (opt.Kind == FeatureOptionKind.Folder && !c.Reset)
        {
            var why = FolderProblem(c.Value.GetString()!, "that folder does not exist");
            if (why != null) return new(false, $"{opt.Label}: {why}", null, ViewOf(f, opt));
        }
        StoreWrite w;
        try { w = c.Reset ? await Store(f).RemoveAsync(key) : await Store(f).SetAsync(key, JsonNode.Parse(c.Value.GetRawText())); }
        catch (Exception e) { return new(false, $"{f.Name}: the setting \"{key}\" could not be saved ({Words(e)})", null, ViewOf(f, opt)); }
        if (!w.Ok) return new(false, $"{f.Name}: the setting \"{key}\" could not be saved ({w.Reason})", null, ViewOf(f, opt));
        var shown = c.Reset ? "reset to its default" : opt.Kind is FeatureOptionKind.Text or FeatureOptionKind.Folder ? "changed" : $"set to {c.Value.GetRawText()}";
        Log($"{f.Name}: option {key} {shown} by {who}");
        var view = ViewOf(f, opt);
        // A running feature hears it now; a resting or stopped one reads it when it next starts.
        var told = 0;
        if (f.Running && !f.Stopped)
        {
            foreach (var k in new[] { key, "*" })
            {
                if (!f.OptionHandlers.TryGetValue(k, out var hs)) continue;
                foreach (var fn in hs.ToArray()) { RunHandler(f, $"option \"{key}\"", () => fn(view.Value, key)); told++; }
            }
        }
        Changed("option", f.Name);
        return new(true, null, Info(f), view, told);
    }

    // ---- the list, the tray ----

    FeatureInfo Info(F f)
    {
        _o.State.Current.Features.TryGetValue(f.Name, out var p);
        return new(f.Name, f.Label, f.Chords, f.Observe, f.Enabled, f.Ok, f.Reason, f.Running, f.Stopped, f.Resting,
            f.Commands.Keys.ToArray(), f.D != null, p?.Keys ?? false, p?.KeysReason ?? "", p?.Broken,
            OptionsOf(f).Select(o => ViewOf(f, o)).ToArray());
    }

    /// <summary>features.list: every feature, loaded or not, in load order.</summary>
    public IReadOnlyList<FeatureInfo> List() => _order.Select(n => Info(_features[n])).ToArray();

    /// <summary>The Features submenu rows: one checkbox per feature, and a grey reason line under one
    /// that is on and failing (cut at 110 characters).</summary>
    public IReadOnlyList<FeatureTrayRow> TrayRows()
    {
        var all = _o.State.Current.Features;
        var out_ = new List<FeatureTrayRow>();
        foreach (var n in _order)
        {
            var f = _features[n];
            all.TryGetValue(n, out var p);
            var line = p != null && p.On && !p.Stopped ? (!p.Ok ? p.Reason : p.Broken) : null;
            if (line != null && line.Length > 110) line = line[..107] + "...";
            out_.Add(new(f.Name, f.Label, f.Enabled, f.D != null && !f.Stopped, line));
        }
        return out_;
    }
}
