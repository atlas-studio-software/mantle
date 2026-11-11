using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Badges;

/// <summary>
/// [Feature("helmbadge")] (lib/helm-badge.js feature + createWatcher): each source's badge file (Helm's
/// %APPDATA%\Helm\badge.json by default) read into a count the bar draws as a disc on the matching app's cell, pushed as
/// "helmbadge.state" {badges:[{exe, label, count, waiting, errors, reason, tip}]} (replayed).
///
/// WHAT IT COSTS: event-driven only. One FileSystemWatcher on the file's folder (Helm writes by tmp + rename, so the
/// folder is watched, not the file); reads coalesced 120 ms. Native is better than Electron's two 30 s timers: the
/// writer's pid is not re-checked every 30 s while a count shows, its exit is HEARD (Process.Exited: a kernel wait, no
/// poll), and a missing folder is waited for by a watch on its parent, not re-tried every 30 s. The one timer left is
/// the 30 s retry when neither the folder nor its parent exists. Nothing runs while the file does not change.
///
/// --test: only MANTLE_HELM_BADGE_FILE (or the test profile's own <c>sources</c> setting) is read, never Helm's real file.
/// PRIVACY: the log names a source by its place ("source 1"), never the app.
/// </summary>
[Feature("helmbadge", "App counts on the bar (Helm: chats that need you)", true)]
public sealed class HelmBadgeFeature : IFeature
{
    public const string StateEvent = "helmbadge.state";
    public const string FileEnv = "MANTLE_HELM_BADGE_FILE";
    public const int SettleMs = 120, RetryMs = 30000;

    /// <summary>What the feature is given (tests: a manual clock, a pid check).</summary>
    public sealed record Host(HostClock? Clock = null, Func<string, string?>? Env = null);

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;
    readonly Host? _given;
    public HelmBadgeFeature() { }
    public HelmBadgeFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    readonly List<Watch> _watches = new();
    List<BadgeSource> _sources = new();
    bool _stopped;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        Func<string, string?> env = _h.Env ?? Environment.GetEnvironmentVariable;
        bool custom = ctx.Settings.TryGet("sources", out var raw) && raw.ValueKind != JsonValueKind.Null;
        if (custom) _sources = HelmBadge.CleanSources(raw);
        else if (ctx.Flags.Test && string.IsNullOrEmpty(env(FileEnv)))
            ctx.Log($"test run: {FileEnv} is not set, so no app's count is read (a test never reads Helm's real file)");
        else _sources = HelmBadge.DefaultSources(env).ToList();
        if (ctx.Flags.Test && !string.IsNullOrEmpty(env(FileEnv)) && !custom) ctx.Log($"test run: {FileEnv} is the badge file");
        for (int i = 0; i < _sources.Count; i++) _watches.Add(new Watch(this, i, _sources[i].File));
        ctx.Replay(StateEvent, () => El(State()));
        ctx.OnEnabled(_ => Publish());
        if (custom && _sources.Count == 0) ctx.Health(false, "its sources setting has no usable entry (exe + absolute file)");
        else ctx.Health(true);
        foreach (var w in _watches) w.Start();
        Publish();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        foreach (var w in _watches) w.Close();
        return ValueTask.CompletedTask;
    }

    JsonObject State()
    {
        bool on = _ctx.Enabled;
        var badges = new JsonArray();
        for (int i = 0; i < _sources.Count; i++) {
            var s = _sources[i];
            var st = (i < _watches.Count ? _watches[i].Cur : null) ?? new BadgeReading(0, 0, 0, "");
            badges.Add((JsonNode)new JsonObject {
                ["exe"] = s.Exe, ["label"] = s.Label, ["count"] = on ? st.Count : 0, ["waiting"] = st.Waiting, ["errors"] = st.Errors,
                ["reason"] = on ? st.Reason : "turned off", ["tip"] = on ? HelmBadge.Describe(st) : "",
            });
        }
        return new JsonObject { ["badges"] = badges };
    }

    void Publish() { if (!_stopped) _ctx.Broadcast(StateEvent, El(State())); }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }

    /// The default exit watch: Process.Exited (the runtime waits on the process handle; no poll).
    static IDisposable? ExitWatch(int pid, Action exited)
    {
        try {
            var p = Process.GetProcessById(pid);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => exited();
            if (p.HasExited) { p.Dispose(); exited(); return null; }
            return p;
        } catch (Exception) { return null; }
    }

    /// One source's watch (createWatcher).
    sealed class Watch
    {
        readonly HelmBadgeFeature _f;
        readonly int _i;
        readonly string _file, _dir, _base;
        FileSystemWatcher? _fsw, _parent;
        IDisposable? _settle, _retry, _exit;
        int _exitPid;
        bool _closed;
        public BadgeReading? Cur;

        public Watch(HelmBadgeFeature f, int i, string file)
        {
            _f = f; _i = i; _file = file;
            _dir = Path.GetDirectoryName(file) ?? "";
            _base = Path.GetFileName(file).ToLowerInvariant();
        }

        public void Start() { Arm(); Read(); }

        void Post(Action a) => _f._clock.Post(() => { if (!_closed && !_f._stopped) a(); });

        /// The folder's watch, else (missing folder) its parent's, else a 30 s retry.
        void Arm()
        {
            if (_closed || _fsw != null) return;
            try {
                if (Directory.Exists(_dir)) {
                    var w = new FileSystemWatcher(_dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    FileSystemEventHandler on = (_, e) => Hit(e.Name);
                    w.Created += on; w.Changed += on; w.Deleted += on;
                    w.Renamed += (_, e) => { Hit(e.Name); Hit(e.OldName); };
                    w.Error += (_, _) => Post(() => { Drop(); _f._ctx.Log($"source {_i + 1}: the folder watch stopped; looking again"); Arm(); Read(); });
                    w.EnableRaisingEvents = true;
                    _fsw = w;
                    _parent?.Dispose(); _parent = null;
                    _retry?.Dispose(); _retry = null;
                    return;
                }
                var up = Path.GetDirectoryName(_dir);
                if (_parent == null && up != null && Directory.Exists(up)) {
                    var name = Path.GetFileName(_dir);
                    var p = new FileSystemWatcher(up) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.DirectoryName, Filter = name };
                    p.Created += (_, _) => Post(() => { Arm(); Read(); });
                    p.Renamed += (_, _) => Post(() => { Arm(); Read(); });
                    p.EnableRaisingEvents = true;
                    _parent = p;
                    return;
                }
            } catch (Exception ex) { _f._ctx.Log($"source {_i + 1}: the folder cannot be watched ({ex.GetType().Name})"); Drop(); }
            if (_parent == null && _retry == null)
                _retry = _f._clock.Later(TimeSpan.FromMilliseconds(RetryMs), () => { _retry = null; if (!_closed) { Arm(); Read(); } });
        }

        void Drop() { _fsw?.Dispose(); _fsw = null; }

        void Hit(string? name)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (n.Length > 0 && n != _base && n != _base + ".tmp") return;
            Post(() => {
                if (_settle != null) return;
                _settle = _f._clock.Later(TimeSpan.FromMilliseconds(SettleMs), () => { _settle = null; if (!_closed) Read(); });
            });
        }

        public void Read()
        {
            if (_closed) return;
            string? text;
            try { text = File.ReadAllText(_file); }
            catch (FileNotFoundException) { text = null; }
            catch (DirectoryNotFoundException) { text = null; }
            catch (Exception) { text = "\0"; }
            var next = HelmBadge.Parse(text, Alive);
            WatchExit(next.Count > 0 ? PidOf(text) : 0);
            bool changed = Cur == null || Cur != next;
            Cur = next;
            if (!changed) return;
            _f._ctx.Log($"source {_i + 1}: count {next.Count}{(next.Reason.Length > 0 ? $" ({next.Reason})" : "")}");
            _f.Publish();
        }

        /// While a count shows, the writer's exit is heard (a dead pid hides the count), never polled.
        void WatchExit(int pid)
        {
            if (pid == _exitPid) return;
            _exit?.Dispose(); _exit = null; _exitPid = pid;
            if (pid <= 0) return;
            _exit = ExitWatch(pid, () => Post(() => { _exitPid = 0; Read(); }));
        }

        static int PidOf(string? text)
        {
            try { using var d = JsonDocument.Parse(text ?? ""); return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out var n) ? n : 0; }
            catch (JsonException) { return 0; }
        }

        public void Close()
        {
            _closed = true;
            _settle?.Dispose(); _retry?.Dispose(); _exit?.Dispose();
            _settle = _retry = _exit = null;
            _fsw?.Dispose(); _parent?.Dispose(); _fsw = _parent = null;
        }
    }

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}
