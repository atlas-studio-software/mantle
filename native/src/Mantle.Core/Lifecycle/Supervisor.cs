using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Lifecycle;

/// <summary>One started <c>Mantle.Sidecar.exe --supervise...</c> launcher (a process, or a test double).</summary>
public interface ISupervisorChild
{
    int Pid { get; }
}

/// <summary>Starts the launcher; <paramref name="line"/> gets each stdout line, <paramref name="exited"/> its exit code. Host-thread callbacks.</summary>
public delegate ISupervisorChild SupervisorSpawn(string exe, IReadOnlyList<string> args, Action<string> line, Action<int?> exited);

public sealed class SupervisorOptions
{
    public required string DataRoot { get; init; }
    public required SafeFile Files { get; init; }
    public required IHostScheduler Host { get; init; }
    public required SupervisorSpawn Spawn { get; init; }
    /// The sidecar exe that carries the supervisor (sidecar/Supervisor.cs).
    public required string SidecarExe { get; init; }
    /// What the supervisor starts again after a crash: THIS native exe (the native record), never Electron.
    public required string ExePath { get; init; }
    public IReadOnlyList<string> Argv { get; init; } = Array.Empty<string>();
    public string Cwd { get; init; } = Environment.CurrentDirectory;
    public string Root { get; init; } = AppContext.BaseDirectory;
    public int Pid { get; init; } = Environment.ProcessId;
    public bool Live { get; init; }
    public bool Test { get; init; }
    public bool Supervise { get; init; }
    public bool NoSupervise { get; init; }
    public string? ProfileName { get; init; }
    /// MANTLE_SUPERVISOR_PID: the supervisor that started this process (it keeps watching it).
    public string? AdoptPid { get; init; }
    public Func<int, bool> Alive { get; init; } = DefaultAlive;
    /// supervisor.lock opens freely = no live supervisor holds it.
    public Func<string, bool> LockFree { get; init; } = DefaultLockFree;
    public Action<int> Kill { get; init; } = pid => { try { using var p = Process.GetProcessById(pid); p.Kill(); } catch (Exception) { } };
    public Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Action<string> Log { get; init; } = _ => { };

    static bool DefaultAlive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (Exception) { return false; } }
    static bool DefaultLockFree(string file)
    {
        try { using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { } return true; }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// Crash recovery's watcher (lib/supervisor.js, SAFE-020..024): a detached <c>Mantle.Sidecar.exe
/// --supervise=&lt;pid&gt;</c> outside this process tree restarts native Mantle after any death that was
/// not a deliberate stand-down. Its record, <c>supervisor-main.json</c>, names MantleNative.exe, so a crash
/// restarts native, never Electron; it is written through the ONE write gate (SafeFile). Host thread.
/// </summary>
public sealed class Supervisor
{
    public const string MainFile = "supervisor-main.json";
    public const string StateFile = "supervisor-state.json";
    public const string LockFile = "supervisor.lock";
    public static readonly TimeSpan AdoptPoll = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DetachLine = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RespawnWindow = TimeSpan.FromMinutes(10);
    public const int RespawnMax = 3;

    readonly SupervisorOptions _o;
    readonly string _main, _state, _lock;
    JsonObject? _rec;
    int? _pid;
    string _via = "none";
    bool _pending, _stopped, _on;
    string _offWhy = "not started";
    IDisposable? _poll, _late;
    readonly List<long> _respawns = new();

    public Supervisor(SupervisorOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        _main = Path.Combine(o.DataRoot, MainFile);
        _state = Path.Combine(o.DataRoot, StateFile);
        _lock = Path.Combine(o.DataRoot, LockFile);
    }

    public bool On => _on;
    public int? WatcherPid => _pid;
    public string Via => _via;
    public string? OffWhy => _on ? null : _offWhy;
    public JsonObject? Record => _rec;

    /// SAFE-020: live always (unless --no-supervise); test only with --supervise.
    public static (bool On, string Why) Wanted(bool live, bool test, bool supervise, bool noSupervise)
    {
        if (noSupervise) return (false, "started with --no-supervise");
        if (test && supervise) return (true, "test instance started with --supervise");
        if (test) return (false, "test instance (crash recovery is opt-in there: --supervise)");
        if (live) return (true, "live run");
        return (false, "not a live run");
    }

    string? WriteRec()
    {
        var bytes = Encoding.UTF8.GetBytes(_rec!.ToJsonString());
        return _o.Files.WriteAtomic(_main, bytes, _main + ".tmp-" + _o.Pid);
    }

    JsonObject? ReadJson(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null; } catch (Exception) { return null; }
    }

    /// <summary>Write the record, then adopt the supervisor that started us or start a new one.</summary>
    public (bool On, string Via, string Why) Start()
    {
        var w = Wanted(_o.Live, _o.Test, _o.Supervise, _o.NoSupervise);
        if (!w.On) { _offWhy = w.Why; _o.Log($"crash recovery is off: {w.Why}"); return (false, "none", w.Why); }
        var prev = ReadJson(_main);
        long told = prev != null && prev["told"] is JsonValue tv && tv.TryGetValue(out long t) ? t : 0;
        var args = new JsonArray();
        foreach (var a in _o.Argv) args.Add(a);
        _rec = new JsonObject {
            ["v"] = 1, ["pid"] = _o.Pid, ["at"] = _o.Now(), ["exe"] = _o.ExePath, ["args"] = args, ["cwd"] = _o.Cwd, ["root"] = _o.Root,
            ["standDown"] = null, ["crashNote"] = null, ["told"] = told, ["native"] = true,
        };
        if (WriteRec() is { } err) {
            _offWhy = $"its record could not be written ({err})";
            _o.Log($"crash recovery is OFF: {_offWhy}");
            return (false, "none", _offWhy);
        }
        if (int.TryParse(_o.AdoptPid, out int adopt) && adopt > 0 && _o.Alive(adopt)) {
            _pid = adopt; _via = "adopted"; _on = true;
            WatchDetached(adopt);
            _o.Log($"crash recovery on: supervisor pid {adopt} started this main process (pid {_o.Pid}) and keeps watching it");
            return (true, "adopted", w.Why);
        }
        if (!string.IsNullOrEmpty(_o.AdoptPid)) _o.Log($"crash recovery: the supervisor that started this process (pid {_o.AdoptPid}) is gone; starting a new one");
        SpawnOne();
        return (_on, _on ? _via : "none", _on ? w.Why : _offWhy);
    }

    void SpawnOne()
    {
        var args = new List<string> { "--supervise=" + _o.Pid, "--supervise-dir=" + _o.DataRoot };
        if (_o.Test) { args.Add("--supervise-test"); args.Add("--profile-name=" + (_o.ProfileName ?? "")); }
        args.Add("--supervise-detach");
        if (!File.Exists(_o.SidecarExe)) {
            _on = false; _offWhy = "the sidecar program is missing, so there is no supervisor to start";
            _o.Log($"crash recovery is OFF: {_offWhy}");
            return;
        }
        ISupervisorChild child;
        try {
            child = _o.Spawn(_o.SidecarExe, args, OnLine, OnExit);
        } catch (Exception e) {
            _on = false; _offWhy = $"the supervisor would not start ({e.Message})";
            _o.Log($"crash recovery is OFF: {_offWhy}");
            return;
        }
        _pid = child.Pid; _via = "spawned"; _pending = true; _on = true;
        _late = _o.Host.Delay(DetachLine, () => {
            _late = null;
            if (_pending) _o.Log($"crash recovery: the supervisor has not answered --supervise-detach in {DetachLine.TotalSeconds:0} s (a slow start, or a sidecar program from before it), so it may still be inside this process tree until it does");
        });
    }

    void OnLine(string line)
    {
        if (!_pending) return;
        try {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("supervisor", out var s) && s.TryGetInt32(out int pid)
                && r.TryGetProperty("detached", out var dt) && dt.ValueKind == JsonValueKind.True) {
                _pending = false; _late?.Dispose(); _late = null;
                _pid = pid; _via = "detached";
                _o.Log($"crash recovery on: supervisor pid {pid} watches this process from outside its process tree");
                WatchDetached(pid);
                return;
            }
        } catch (JsonException) { }
        _o.Log("crash recovery: the supervisor's answer to --supervise-detach was not readable: " + (line.Length > 200 ? line.Substring(0, 200) : line));
    }

    void OnExit(int? code)
    {
        if (!_pending) return;   // the launcher exits after its line: expected
        _pending = false; _late?.Dispose(); _late = null;
        _pid = null;
        Lost($"it exited (code {(code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}) before saying where it runs");
    }

    /// SAFE-024: poll the watcher; its supervisor.lock opening freely while supervisor-state.json names it = gone.
    void WatchDetached(int pid)
    {
        _poll?.Dispose();
        _poll = _o.Host.Delay(AdoptPoll, () => {
            _poll = null;
            if (_stopped || _pid != pid) return;
            bool there = _o.Alive(pid);
            if (there && !LockSaysGone(pid)) { WatchDetached(pid); return; }
            _pid = null;
            Lost(there ? $"it is no longer running (its supervisor.lock is free, so pid {pid} belongs to another process now)" : "it is no longer running");
        });
    }

    bool LockSaysGone(int pid)
    {
        var st = ReadJson(_state);
        if (st == null || !(st["supervisorPid"] is JsonValue p && p.TryGetValue(out int sp) && sp == pid)) return false;
        if (!(st["lock"] is JsonValue l && l.TryGetValue(out bool held) && held)) return false;
        return _o.LockFree(_lock);
    }

    void Lost(string how)
    {
        if (_stopped) return;
        long t = _o.Now();
        _respawns.RemoveAll(x => t - x >= (long)RespawnWindow.TotalMilliseconds);
        if (_respawns.Count >= RespawnMax) {
            _on = false; _offWhy = $"the supervisor stopped {RespawnMax + 1} times in 10 minutes";
            _o.Log($"crash recovery is OFF: the supervisor {how}, and it has stopped {_respawns.Count} times in 10 minutes, so it is not started again; a crash now leaves Mantle down until it is started by hand");
            return;
        }
        _respawns.Add(t);
        _o.Log($"crash recovery: the supervisor {how}; starting a new one");
        SpawnOne();
    }

    /// <summary>SAFE-021: every deliberate exit says so first, so the supervisor starts nothing.</summary>
    public (bool Ok, string Via) StandDown(string why)
    {
        _stopped = true;
        _poll?.Dispose(); _poll = null;
        if (_rec == null) return (true, "none");
        bool first = _rec["standDown"] == null;
        _rec["standDown"] = new JsonObject { ["why"] = string.IsNullOrWhiteSpace(why) ? "no reason was given" : why, ["at"] = _o.Now() };
        string? err = WriteRec();
        if (err == null) {
            if (first) _o.Log($"crash recovery stands down: {why} (the supervisor will not start Mantle again)");
            return (true, "record");
        }
        bool killed = false;
        if (_pid is int pid) { try { _o.Kill(pid); killed = true; } catch (Exception) { killed = false; } }
        _o.Log($"crash recovery stands down: {why}; the record could not be written ({err}), so the supervisor{(_pid is int k ? $" (pid {k}) was {(killed ? "stopped" : "NOT stopped")}" : " was not running")}");
        return (killed || _pid == null, "kill");
    }

    /// <summary>Note the crash in the record (the supervisor's dialog and the next start's notice read it).</summary>
    public void CrashNote(string what)
    {
        if (_rec == null || _rec["standDown"] != null) return;
        _rec["crashNote"] = new JsonObject { ["what"] = what ?? "", ["at"] = _o.Now() };
        _ = WriteRec();   // the exit code still says it crashed
    }
}
