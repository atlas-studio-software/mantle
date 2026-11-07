using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Data;

namespace Mantle.Core.Look;

/// <summary>look-sync.js's status: <c>State</c> ok | missing | corrupt | newer | off | error, always with plain words when not a clean ok.</summary>
/// <param name="Recovered">'corrupt' on the ok right after a damaged file was moved aside and re-seeded</param>
public sealed record LookStatus(string State, string? Reason, double? Rev, string? By, string? At, IReadOnlyList<LookSkip> Skipped, string? Recovered = null);

/// <summary>An 'apply' event: values another app (or the seed, or turning sync on) asks this app to show.</summary>
/// <param name="Cause">remote | adopt | seed</param>
public sealed record LookApply(JsonObject Changes, string? By, double Rev, string Cause, bool FromSync = true);

/// <summary>The desktop record (or null) and why it cannot be used, delivered even while sync is off.</summary>
public sealed record LookDesktop(JsonObject? Record, string? Reason);

/// <summary>A publish's answer (never thrown): <c>{ok, rev, reason, code}</c>.</summary>
public sealed record LookWrite(bool Ok, double? Rev, string? Reason, string? Code = null, bool Changed = false);

/// <summary>How one sync instance is made (look-sync.js <c>create(opts)</c>). Tests shorten the delays.</summary>
public sealed record LookSyncOptions
{
    public required string App { get; init; }
    /// The shared folder (DataPaths.LookSyncDir for a run). Required: native never guesses the user's folder.
    public required string Dir { get; init; }
    /// The ONLY way this instance changes a file; its gate decides whether the folder may be written at all.
    public required SafeFile Files { get; init; }
    public string? Instance { get; init; }
    public bool Enabled { get; init; } = true;
    /// This app's own values {theme, accent, glass}, asked when seeding from them or rebuilding a lost file.
    public Func<JsonObject?>? Local { get; init; }
    public int[] RetryDelays { get; init; } = { 50, 100, 200 };
    /// Writes are atomic renames: 50 ms coalesces the burst (was 150; the 1 s follow target).
    public int DebounceMs { get; init; } = 50;
    public int CorruptRetryMs { get; init; } = 250;
    public int[] RewatchDelays { get; init; } = { 1000, 5000, 30000 };
    public Func<DateTimeOffset>? Now { get; init; }
    /// The pid written into <c>writer.pid</c> (tests fix it to compare bytes with the JavaScript module).
    public int? Pid { get; init; }
    /// Where a handler that threw is said (the module would have thrown into its own stream).
    public Action<string>? Log { get; init; }
    /// Tests only: each step of the follow path (event, debounce, read, apply), to time a slow follow on a busy runner.
    internal Action<string>? Trace { get; init; }
}

/// <summary>
/// One look-sync instance: lib/look-sync.js's <c>LookSync</c> class, ported line for line (CONTRACT.md sections 2, 3, 5).
/// Reads, publishes, seeds and recoveries run one at a time on a serial queue; at most one check waits in it.
/// Errors never leave the stream: every failure becomes a status with a reason, because each app shows it.
/// Events fire on the queue's thread (a pool thread); the host posts them to its own thread.
/// </summary>
public sealed class LookSync : IDisposable
{
    readonly LookSyncOptions _o;
    readonly SafeFile _files;
    readonly Func<DateTimeOffset> _now;
    readonly int _pid;
    readonly object _lock = new();

    JsonObject? _doc;           // last good doc seen (validated)
    string? _lastHash;          // hash of the bytes last handled
    double _lastDocRev = -1;    // highest doc rev seen, to spot a re-seed (rev went down)
    Dictionary<string, double> _applied = new();   // field -> rev applied (or written) by this instance
    double? _desktopRev;
    bool _readOnly;             // newer schema on disk
    bool _hadFile;
    FileSystemWatcher? _watcher;
    Timer? _debounce, _rewatch;
    int _rewatchStep;
    volatile bool _closed;
    Task _queue = Task.CompletedTask;
    Dictionary<string, object?>? _pendingCtx;
    Task? _pendingP;
    LookStatus _status;

    public string App { get; }
    public string Instance { get; }
    public string Dir { get; }
    public string File { get; }
    public bool Enabled { get; private set; }

    public event Action<LookStatus>? Status;
    public event Action<LookApply>? Apply;
    public event Action<LookDesktop>? Desktop;

    public LookSync(LookSyncOptions o)
    {
        if (o == null || string.IsNullOrEmpty(o.App)) throw new ArgumentException("look-sync: opts.app is required");
        _o = o;
        _files = o.Files ?? throw new ArgumentException("look-sync: a SafeFile is required");
        App = o.App;
        Instance = o.Instance ?? Guid.NewGuid().ToString();
        Dir = o.Dir;
        File = Path.Combine(Dir, LookSyncCore.FileName);
        Enabled = o.Enabled;
        _now = o.Now ?? (() => DateTimeOffset.UtcNow);
        _pid = o.Pid ?? Environment.ProcessId;
        _status = new LookStatus(Enabled ? "missing" : "off", "not started", null, null, null, Array.Empty<LookSkip>());
    }

    public LookStatus CurrentStatus() => _status;

    /// Completes when everything queued so far (checks, publishes, seeds) has run.
    public Task WhenIdle() { lock (_lock) return _queue; }

    public LookDesktop CurrentDesktop()
    {
        var d = _doc?["desktop"] as JsonObject;
        if (d == null) return new LookDesktop(null, "Mantle hasn't shared a desktop wallpaper yet");
        bool ok = LookSyncCore.IsBool(d["ok"], out var b) && b;
        return new LookDesktop((JsonObject)LookSyncCore.Clone(d)!, ok ? null : (LookSyncCore.Str(d["reason"]) is { Length: > 0 } r ? r : "Mantle reported the wallpaper is unavailable"));
    }

    /// <summary>Read-only RIGHT NOW? Re-reads the file instead of trusting the last status, so a newer file that was
    /// just deleted never blocks a change while its watch event is in flight. -> reason | null</summary>
    public string? ReadOnlyNow()
    {
        if (_closed) return null;
        var r = LookSyncCore.Read(Dir);
        if (r.State == "newer") { _readOnly = true; return r.Reason; }
        if (_readOnly || _status.State == "newer") { _readOnly = false; _lastHash = null; Check(new()); }
        return null;
    }

    /// The shared look as last seen ({theme?, accent?, glass?} entries), or null.
    public JsonObject? Shared() => _doc?["look"] is JsonObject l ? (JsonObject)LookSyncCore.Clone(l)! : null;

    void Raise<T>(Action<T>? ev, T arg, string name)
    {
        if (ev == null) return;
        foreach (var d in ev.GetInvocationList()) {
            try { ((Action<T>)d)(arg); }
            catch (Exception e) { try { _o.Log?.Invoke("look-sync: a " + name + " listener threw: " + e.Message); } catch { } }
        }
    }

    void EmitStatus(string state, string? reason, IReadOnlyList<LookSkip>? skipped = null, string? recovered = null)
    {
        var d = _doc;
        var w = d?["writer"] as JsonObject;
        string? by = w != null ? LookSyncCore.Str(w["app"]) is { Length: > 0 } a ? a : null : null;
        string? at = w != null ? LookSyncCore.Str(w["at"]) is { Length: > 0 } t ? t : null : null;
        double? rev = d != null && LookSyncCore.TryNum(d["rev"], out var rv) ? rv : null;
        string st = state;
        string? why = string.IsNullOrEmpty(reason) ? null : reason;
        if (!Enabled && state != "error" && state != "newer") {
            st = "off";
            why = why != null && state != "ok" ? why : "Sync is off; this app keeps its own look";
        }
        var s = new LookStatus(st, why, rev, by, at, skipped ?? Array.Empty<LookSkip>(), recovered);
        _status = s;
        Raise(Status, s, "status");
    }

    public LookSync Start()
    {
        if (_closed) return this;
        Watch();
        Check(new() { ["startup"] = true });
        return this;
    }

    public void Close()
    {
        _closed = true;
        lock (_lock) {
            _debounce?.Dispose(); _debounce = null;
            _rewatch?.Dispose(); _rewatch = null;
            if (_watcher != null) { try { _watcher.Dispose(); } catch { } _watcher = null; }
        }
    }

    public void Dispose() => Close();

    /// Re-read now: on window focus, on resume from sleep and on unlock (fs watching can miss events across sleep).
    public void Refresh() { if (!_closed) Check(new()); }

    // ---- watching -------------------------------------------------------------------------------

    bool Watch()
    {
        lock (_lock) {
            if (_closed || _watcher != null) return false;
        }
        try {
            string? no = _files.EnsureFolder(Dir);
            if (no != null) { LostWatch(no); return false; }
            var w = new FileSystemWatcher(Dir) {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Changed += (_, e) => OnEvent(e.Name);
            w.Created += (_, e) => OnEvent(e.Name);
            w.Deleted += (_, e) => OnEvent(e.Name);
            w.Renamed += (_, e) => { OnEvent(e.Name); OnEvent(e.OldName); };
            w.Error += (_, e) => LostWatch(!Directory.Exists(Dir) ? "folder was removed" : e.GetException() is Exception x ? SafeFile.Code(x) : "the watch failed");
            w.EnableRaisingEvents = true;
            lock (_lock) {
                if (_closed) { w.Dispose(); return false; }
                _watcher = w;
                _rewatchStep = 0;
            }
            return true;
        } catch (Exception e) {
            LostWatch(SafeFile.Code(e));
            return false;
        }
    }

    void OnEvent(string? name)
    {
        if (_closed) return;
        _o.Trace?.Invoke("event " + name);
        if (name != null && (Path.IsPathRooted(name) || name.Contains('\\') || name.Contains('/'))) { LostWatch("folder was removed"); return; }
        if (name != null && !string.Equals(name, LookSyncCore.FileName, StringComparison.OrdinalIgnoreCase)) return;   // temp files, stills, bad copies
        lock (_lock) {
            if (_closed) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ => { _o.Trace?.Invoke("debounce fired"); Check(new()); }, null, _o.DebounceMs, Timeout.Infinite);
        }
    }

    void LostWatch(string why)
    {
        if (_closed) return;
        _o.Trace?.Invoke("lost watch: " + why);
        int ms;
        lock (_lock) {
            if (_watcher != null) { try { _watcher.Dispose(); } catch { } _watcher = null; }
            _debounce?.Dispose(); _debounce = null;
            if (_rewatch != null) return;   // already scheduled
            var delays = _o.RewatchDelays;
            ms = delays[Math.Min(_rewatchStep, delays.Length - 1)];
            _rewatchStep++;
            _rewatch = new Timer(_ => {
                lock (_lock) { _rewatch?.Dispose(); _rewatch = null; }
                if (_closed) return;
                if (Watch()) Check(new());
            }, null, ms, Timeout.Infinite);
        }
        EmitStatus("error", "Watching " + Dir + " stopped (" + why + "); retrying in " + (ms >= 1000 ? JsJson.Number(ms / 1000.0) + " s" : ms + " ms"));
    }

    // ---- reading + applying ---------------------------------------------------------------------

    /// Coalesce: at most one queued check; it runs on the same queue as writes.
    Task Check(Dictionary<string, object?> ctx)
    {
        if (_closed) return Task.CompletedTask;
        lock (_lock) {
            if (_pendingCtx != null) { foreach (var (k, v) in ctx) _pendingCtx[k] = v; return _pendingP!; }
            _pendingCtx = new(ctx);
            _pendingP = Serial(async () => {
                Dictionary<string, object?> c;
                lock (_lock) { c = _pendingCtx!; _pendingCtx = null; }
                await CheckNow(c).ConfigureAwait(false);
                return true;
            });
            return _pendingP;
        }
    }

    async Task CheckNow(Dictionary<string, object?> ctx)
    {
        if (_closed) return;
        var r = LookSyncCore.Read(Dir);
        _o.Trace?.Invoke("check read " + r.State + (r.Hash != null && r.Hash == _lastHash ? " (no change)" : "") + (r.Reason != null ? ": " + r.Reason : ""));
        if (r.State == "corrupt") {
            await Task.Delay(_o.CorruptRetryMs).ConfigureAwait(false);   // a torn read mid-write: retry once
            if (_closed) return;
            r = LookSyncCore.Read(Dir);
        }
        if (_closed) return;
        if (r.Hash != null && r.Hash == _lastHash && r.State == "ok") return;   // no-op event

        if (r.State == "missing") {
            _lastHash = null; _readOnly = false;   // a deleted newer file no longer makes this app read-only
            if (ctx.ContainsKey("startup") && Enabled) { await SeedAsync("first", null).ConfigureAwait(false); return; }
            if (!Directory.Exists(Dir) && _watcher != null) LostWatch("folder was removed");   // Windows may not say it
            if (_hadFile) { EmitStatus("missing", "Not shared yet: file was removed"); return; }
            EmitStatus("missing", "Not shared yet");
            return;
        }
        if (r.State == "error") { EmitStatus("error", r.Reason); return; }
        if (r.State == "newer") {
            _readOnly = true; _lastHash = r.Hash; _hadFile = true;
            EmitStatus("newer", r.Reason);
            return;
        }
        _readOnly = false;
        if (r.State == "corrupt") {
            _lastHash = null;
            if (!Enabled) { EmitStatus("corrupt", "Shared look file is damaged: " + r.Reason); return; }
            await RecoverCorrupt(r.Reason!).ConfigureAwait(false);
            return;
        }
        Take(r, ctx.TryGetValue("cause", out var cause) ? cause as string : null);
    }

    /// A good doc arrived (from disk). Loop guard + per-field apply. Returns the apply it raised (setEnabled reads it).
    LookApply? Take(LookRead r, string? cause)
    {
        var doc = r.Doc!;
        _lastHash = r.Hash;
        _hadFile = true;
        LookSyncCore.TryNum(doc["rev"], out var docRev);
        if (docRev < _lastDocRev) _applied = new();   // the file was re-seeded: shared values win again
        _lastDocRev = docRev;
        _doc = doc;

        // The desktop record is a fact about the screen: delivered even when sync is OFF.
        if (doc["desktop"] is JsonObject d && LookSyncCore.TryNum(d["rev"], out var drev) && drev != _desktopRev) {
            _desktopRev = drev;
            Raise(Desktop, CurrentDesktop(), "desktop");
        }

        var skipped = r.Skipped.Select(s => s with {
            Text = "Ignored bad " + s.Field + " value" + (s.By != null ? " from " + LookSyncCore.AppName(s.By) : "") + ": " + s.Reason
        }).ToList();
        var writer = doc["writer"] as JsonObject;
        bool own = writer != null && LookSyncCore.Str(writer["instance"]) == Instance;
        LookApply? applied = null;
        if (Enabled && (!own || cause == "adopt")) {
            var changes = new JsonObject();
            var look = (JsonObject)doc["look"]!;
            foreach (var f in LookSyncCore.Fields) {
                if (look[f] is not JsonObject e) continue;
                LookSyncCore.TryNum(e["rev"], out var erev);
                if (cause == "adopt" || !_applied.TryGetValue(f, out var had) || erev > had) {
                    _applied[f] = erev;
                    changes[f] = LookSyncCore.Clone(e["v"]);
                }
            }
            if (changes.Count > 0) {
                string? by = writer != null && LookSyncCore.Str(writer["app"]) is { Length: > 0 } a ? a : null;
                applied = new LookApply(changes, by, docRev, cause ?? "remote");
                _o.Trace?.Invoke("apply " + string.Join(",", changes.Select(x => x.Key)) + " by " + by);
                Raise(Apply, applied, "apply");
            }
        }
        string? reason = skipped.Count > 0 ? string.Join("; ", skipped.Select(s => s.Text)) : null;
        EmitStatus("ok", reason, skipped);
        return applied;
    }

    JsonObject BaseDoc() => new() {
        ["schema"] = LookSyncCore.N(LookSyncCore.Schema),
        ["rev"] = LookSyncCore.N(Math.Max(_lastDocRev, 0)),
        ["writer"] = new JsonObject(),
        ["look"] = new JsonObject(),
    };

    JsonObject LocalValues()
    {
        JsonObject? l = null;
        try { l = _o.Local?.Invoke(); } catch (Exception e) { _o.Log?.Invoke("look-sync: reading this app's own look threw: " + e.Message); }
        return LookSyncCore.ValidLocal(l);
    }

    async Task SeedAsync(string kind, string? corruptNote)
    {
        // kind 'first': the agreed SEED (Match Windows). 'local': this app's values.
        var vals = kind == "first" ? LookSyncCore.Seed() : LookSyncCore.Assign(LookSyncCore.Seed(), LocalValues());
        var doc = BaseDoc();
        if (_doc?["desktop"] is JsonObject dk) doc["desktop"] = LookSyncCore.Clone(dk);
        LookSyncCore.TryNum(doc["rev"], out var r0);
        double rev = r0 + 1;
        var look = (JsonObject)doc["look"]!;
        foreach (var f in LookSyncCore.Fields)
            look[f] = new JsonObject { ["v"] = LookSyncCore.Clone(vals[f]), ["rev"] = LookSyncCore.N(rev), ["by"] = App };
        var res = await WriteDoc(doc, rev).ConfigureAwait(false);
        if (!res.Ok) { EmitStatus("error", res.Reason); return; }
        foreach (var f in LookSyncCore.Fields) _applied[f] = rev;
        if (kind == "first") {
            // This app adopts the seed so all three start on the same look.
            Raise(Apply, new LookApply((JsonObject)LookSyncCore.Clone(vals)!, App, rev, "seed"), "apply");
            EmitStatus("ok", "Look sync started from the shared defaults (Match Windows), seeded by " + LookSyncCore.AppName(App));
            return;
        }
        if (corruptNote != null) {
            // Say the damage once (apps toast a 'corrupt' fallback), then the TRUE state: the file is healthy again.
            EmitStatus("corrupt", corruptNote);
            EmitStatus("ok", corruptNote, null, "corrupt");
            return;
        }
        EmitStatus("ok", "Look sync started from " + LookSyncCore.AppName(App) + "'s settings");
    }

    async Task RecoverCorrupt(string why)
    {
        string ts = LookSyncCore.Iso(_now()).Replace(':', '-').Replace('.', '-');
        string bad = LookSyncCore.FileName + ".bad-" + ts;
        string? err = _files.MoveAside(File, Path.Combine(Dir, bad));
        bool moved = err == null || err == "ENOENT";
        if (!moved) { EmitStatus("corrupt", "Shared look file is damaged (" + why + ") and couldn't be moved aside; using this app's own look"); return; }
        await SeedAsync("local", "Shared look file was damaged; restarted from " + LookSyncCore.AppName(App) + "'s settings (old copy: " + bad + ")").ConfigureAwait(false);
    }

    /// The bytes the module writes for a doc: JSON.stringify(doc, null, 2) + "\n".
    public static byte[] DocBytes(JsonObject doc) => JsJson.ToFileBytes(doc, 2, true);

    Task<LookWrite> WriteDoc(JsonObject doc, double rev)
    {
        doc["rev"] = LookSyncCore.N(rev);
        doc["writer"] = new JsonObject {
            ["app"] = App, ["instance"] = Instance, ["pid"] = LookSyncCore.N(_pid), ["at"] = LookSyncCore.Iso(_now()),
        };
        byte[] bytes = DocBytes(doc);
        var res = WriteAtomic(bytes);
        if (res.Ok) {
            _doc = doc; _lastHash = LookSyncCore.Sha1(bytes); _lastDocRev = rev; _hadFile = true;
        }
        return Task.FromResult(res);
    }

    /// writeAtomic: a tmp <c>look.json.&lt;app&gt;.&lt;pid&gt;.&lt;hex&gt;.tmp</c> renamed over look.json, the rename retried on
    /// EBUSY / EPERM / EACCES after each delay. Failures in the module's words.
    LookWrite WriteAtomic(byte[] bytes)
    {
        string tmp = Path.Combine(Dir, LookSyncCore.FileName + "." + App + "." + _pid + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3)) + ".tmp");
        string? err = _files.WriteAtomic(File, bytes, tmp, _o.RetryDelays);
        if (err == null) return new LookWrite(true, null, null);
        const string wrote = "the new copy could not be written (";
        if (err.StartsWith(wrote, StringComparison.Ordinal)) {
            string code = err[wrote.Length..].TrimEnd(')');
            return new LookWrite(false, null, "Can't write " + Dir + ": " + code, code);
        }
        string first = err.Split(' ')[0];
        if (first.Length > 1 && first[0] == 'E' && first.All(char.IsAsciiLetterUpper))
            return new LookWrite(false, null, "Can't write " + Dir + ": " + first + " (after " + (_o.RetryDelays.Length + 1) + " tries)", first);
        return new LookWrite(false, null, "Can't write " + Dir + ": " + err, null);   // the read-only gate said no
    }

    // ---- writing (serialised per instance) ------------------------------------------------------

    Task<T> Serial<T>(Func<Task<T>> fn)
    {
        lock (_lock) {
            var p = _queue.ContinueWith(_ => fn(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _queue = p.ContinueWith(_ => { }, TaskScheduler.Default);
            return p;
        }
    }

    /// <summary>Publish one look field. Call from the app's setter only when the change did NOT come from sync.</summary>
    public Task<LookWrite> PublishAsync(string field, JsonNode? value) => Serial(() => {
        if (!LookSyncCore.Fields.Contains(field)) return Task.FromResult(new LookWrite(false, null, "unknown field " + field));
        var v = LookSyncCore.Clone(value);
        string? bad = LookSyncCore.CheckValue(field, v);
        if (bad != null) return Task.FromResult(new LookWrite(false, null, bad));
        if (!Enabled) return Task.FromResult(new LookWrite(false, null, "Sync is off"));
        return Mutate(doc => {
            LookSyncCore.TryNum(doc["rev"], out var r);
            ((JsonObject)doc["look"]!)[field] = new JsonObject { ["v"] = LookSyncCore.Clone(v), ["rev"] = LookSyncCore.N(r + 1), ["by"] = App };
        }, rev => _applied[field] = rev);
    });

    /// <summary>Mantle only: the desktop wallpaper record. Published even while sync is OFF.</summary>
    public Task<LookWrite> PublishDesktopAsync(JsonObject record) => Serial(() => {
        if (App != "mantle") return Task.FromResult(new LookWrite(false, null, "only Mantle publishes the desktop record"));
        var rec = LookSyncCore.Assign(new JsonObject(), record);
        rec["rev"] = LookSyncCore.N(0);
        rec["by"] = App;
        string? bad = LookSyncCore.CheckDesktop(rec);
        if (bad != null) return Task.FromResult(new LookWrite(false, null, bad));
        return Mutate(doc => {
            LookSyncCore.TryNum(doc["rev"], out var r);
            rec["rev"] = LookSyncCore.N(r + 1);
            doc["desktop"] = rec;
        }, rev => _desktopRev = rev);
    });

    async Task<LookWrite> Mutate(Action<JsonObject> change, Action<double> after)
    {
        // Re-read so only the one field changes.
        var r = LookSyncCore.Read(Dir);
        JsonObject doc;
        if (r.State == "newer") {
            _readOnly = true;
            EmitStatus("newer", r.Reason);
            return new LookWrite(false, null, r.Reason);
        }
        if (r.State == "error") { EmitStatus("error", r.Reason); return new LookWrite(false, null, r.Reason, r.Code); }
        if (r.State == "ok") {
            doc = r.Doc!;
            LookSyncCore.TryNum(doc["rev"], out var dr);
            if (dr < _lastDocRev) _applied = new();
        } else {
            // missing (re-create from current values) or corrupt (rebuild from the last good doc + local)
            doc = BaseDoc();
            var vals = LookSyncCore.Seed();
            if (_doc?["look"] is JsonObject had)
                foreach (var f in LookSyncCore.Fields) if (had[f] is JsonObject e) vals[f] = LookSyncCore.Clone(e["v"]);
            if (Enabled) LookSyncCore.Assign(vals, LocalValues());
            LookSyncCore.TryNum(doc["rev"], out var b);
            double rev0 = b + 1;
            var look = (JsonObject)doc["look"]!;
            if (Enabled) foreach (var f in LookSyncCore.Fields) look[f] = new JsonObject { ["v"] = LookSyncCore.Clone(vals[f]), ["rev"] = LookSyncCore.N(rev0), ["by"] = App };
            if (_doc?["desktop"] is JsonObject dk) doc["desktop"] = LookSyncCore.Clone(dk);
            doc["rev"] = LookSyncCore.N(rev0);
        }
        change(doc);
        LookSyncCore.TryNum(doc["rev"], out var cur);
        double rev = cur + 1;
        var lk = (JsonObject)doc["look"]!;
        foreach (var f in LookSyncCore.Fields)
            if (lk[f] is JsonObject e && LookSyncCore.TryNum(e["rev"], out var er) && er > cur) e["rev"] = LookSyncCore.N(rev);
        var res = await WriteDoc(doc, rev).ConfigureAwait(false);
        if (!res.Ok) { EmitStatus("error", res.Reason); return new LookWrite(false, null, res.Reason, res.Code); }
        after(rev);
        _readOnly = false;
        EmitStatus("ok", null);
        return new LookWrite(true, rev, null);
    }

    /// <summary>The per-app switch (section 5). Turning ON adopts the shared values (shared wins).</summary>
    public Task<LookWrite> SetEnabledAsync(bool on) => Serial(async () => {
        if (on == Enabled) return new LookWrite(true, null, null, null, false);
        Enabled = on;
        if (!on) { EmitStatus("off", null); return new LookWrite(true, null, null, null, true); }
        var r = LookSyncCore.Read(Dir);
        if (r.State == "missing") { await SeedAsync("local", null).ConfigureAwait(false); return new LookWrite(true, null, null, null, true); }
        if (r.State != "ok") { _lastHash = null; await CheckNow(new()).ConfigureAwait(false); return new LookWrite(true, null, null, null, true); }
        var before = LocalValues();
        _lastHash = null;
        var applied = Take(r, "adopt");
        var notes = new List<string>();
        if (applied != null)
            foreach (var (f, v) in applied.Changes)
                if (!before.TryGetPropertyValue(f, out var b) || LookSyncCore.J(b) != LookSyncCore.J(v)) notes.Add(LookSyncCore.DescribeChange(f, v));
        EmitStatus("ok", "Now matching the others" + (notes.Count > 0 ? " (" + string.Join(", ", notes) + ")" : ""));
        return new LookWrite(true, null, null, null, true);
    });
}
