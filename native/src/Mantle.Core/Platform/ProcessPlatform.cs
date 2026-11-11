using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Lifecycle;

namespace Mantle.Core.Platform;

/// <summary>Settings for <see cref="ProcessPlatform"/>. The defaults are lib/sidecar.js's numbers.</summary>
public sealed class SidecarOptions
{
    public string ExePath = "";
    /// True when the exe came from MANTLE_SIDECAR_EXE (said in the missing-exe reason).
    public bool ExeOverride;
    /// --passive: a test platform that refuses every shell change.
    public bool Passive;
    /// --state-file=: the recovery note the restore script reads.
    public string? StateFile;
    /// --parent-pid=: the sidecar ends (after its restore) when this process dies.
    public int ParentPid = Environment.ProcessId;
    public int ReadyWaitMs = 8000;
    public int RestartDelayMs = 3000;
    public int StableMs = 60000;
    public int HealthMs = 15000;
    public int HealthTimeoutMs = 3000;
    public int HealthMisses = 3;
    public int StopGraceMs = 1500;
    /// Every line a human should see: (level, message). Client warnings and the sidecar's own log events.
    public Action<string, string> Log = (_, _) => { };
}

/// <summary>
/// IPlatform over today's Mantle.Sidecar.exe, speaking its JSON-lines protocol exactly as
/// lib/sidecar.js does (PLAN.md 3.2, phase 1): ids, ttl, the 8 s default timeout, the ready wait, the
/// 3/6/12 s restart ladder with its 60 s stable reset, the 15 s health ping, the 4 MB line cap, the
/// stderr rate limit, late-answer accounting and the old-exe check. Host thread only: every member and
/// every <see cref="ISidecarSink"/> call runs there, so nothing here locks.
/// </summary>
public sealed class ProcessPlatform : IPlatformControl, ISidecarSink
{
    public const int MaxRestarts = 3;
    public const int MaxLineChars = 4 * 1024 * 1024;
    public const int AbandonedMax = 500;
    public const int ShedReportMs = 30000;
    public const int StderrLinesPerWindow = 20;
    public const int StderrWindowMs = 10000;
    public const int StderrLineMax = 300;
    public const long MaxTimeoutMs = 2147483647;

    readonly SidecarOptions _o;
    readonly ISidecarLauncher _launcher;
    readonly IHostScheduler _host;

    ISidecarProcess? _proc;
    ISidecarProcess? _hungProc;
    PlatformStatus _status = new(PlatformState.Starting, "sidecar has not been started yet");
    long _nextId = 1;
    readonly Dictionary<long, Pending> _pending = new();
    readonly LinkedList<(long Id, Abandoned A)> _abandonedOrder = new();
    readonly Dictionary<long, LinkedListNode<(long Id, Abandoned A)>> _abandoned = new();
    readonly HashSet<long> _unawaited = new();
    readonly List<Waiter> _readyWaiters = new();
    readonly Dictionary<string, List<Action<PlatformEvent>>> _events = new(StringComparer.Ordinal);
    readonly WindowIconCache _winIcons = new();
    bool _winIconRepair;

    /// The iconDiff cache (lib/sidecar.js _winIcons); emptied for every new sidecar process.
    public WindowIconCache WindowIcons => _winIcons;

    /// An hwnd with no icon kept (a lost list) is repaired from a full windows call, re-raised, said once per repair.
    void RepairIcons(List<string> missed)
    {
        _winIconRepair = true;
        _o.Log("warn", $"the window list arrived without the icons of {missed.Count} window(s) this side never got; reading the whole list again");
        var t = CallAsync("windows", new System.Text.Json.Nodes.JsonObject(), TimeSpan.FromMilliseconds(4000));
        void Done(PlatformReply r)
        {
            _winIconRepair = false;
            if (!r.Ok || r.Result.ValueKind != JsonValueKind.Array) return;   // the next push brings the list
            _winIcons.Fill(r.Result, missed);
            Raise("windows", "{\"ev\":\"windows\",\"list\":" + r.Result.GetRawText() + "}");
        }
        if (t.IsCompleted) Done(t.Result);
        else t.AsTask().ContinueWith(x => Done(x.Result), TaskContinuationOptions.ExecuteSynchronously);
    }
    readonly List<Action<PlatformStatus>> _statusHandlers = new();
    readonly List<Action> _readyHandlers = new();
    IDisposable? _restartTimer;
    Health? _health;
    bool _stopping;
    double _startedAt, _readyAt;
    bool _hadReady;
    string _stdoutBuf = "";
    int _scanFrom;
    (string Head, long Chars)? _dropping;
    (double WindowAt, int Used, int Held) _stderr;
    TaskCompletionSource<bool>? _exitWait;

    public int Restarts { get; private set; }
    public bool GaveUp { get; private set; }
    public int SpawnCount { get; private set; }
    public int HealthKills { get; private set; }
    public string? LastLog { get; private set; }
    /// Late answers and shed requests, for the diagnostic report.
    public (int Late, int Expired) Shed => (_shedLate, _shedExpired);
    int _shedLate, _shedExpired, _shedSinceReport;
    double _shedReportedAt = double.NegativeInfinity;

    public ProcessPlatform(SidecarOptions options, ISidecarLauncher launcher, IHostScheduler host)
    {
        _o = options; _launcher = launcher; _host = host;
    }

    public PlatformStatus Status => _status;
    public int? Pid => _proc?.Pid;

    // ------------------------------------------------------------------ status

    void SetStatus(PlatformState state, string reason, PlatformInfo? info = null)
    {
        _status = new PlatformStatus(state, reason, state == PlatformState.Ok ? info ?? _status.Info : (_proc != null ? _status.Info : null));
        if (state == PlatformState.Ok) ReleaseWaiters(null);
        else if (state != PlatformState.Starting) ReleaseWaiters(reason);
        foreach (var h in _statusHandlers.ToArray()) Safe(() => h(_status), "a status handler");
    }

    void ReleaseWaiters(string? failReason)
    {
        if (_readyWaiters.Count == 0) return;
        var all = _readyWaiters.ToArray();
        _readyWaiters.Clear();
        foreach (var w in all) {
            w.Timer?.Dispose();
            if (failReason == null) w.Go(); else w.Fail(failReason);
        }
    }

    void Safe(Action a, string what)
    {
        try { a(); } catch (Exception e) { _o.Log("error", $"{what} failed: {e.GetType().Name}: {e.Message}"); }
    }

    // ------------------------------------------------------------------ start, retry, stop

    public void Start()
    {
        if (_proc != null) return;
        _stopping = false;
        if (!File.Exists(_o.ExePath)) {
            SetStatus(PlatformState.Down, _o.ExeOverride
                ? $"sidecar down: no exe at {_o.ExePath} (the MANTLE_SIDECAR_EXE override names it; Mantle never builds into an override path)"
                : $"sidecar down: no exe at {_o.ExePath} (native Mantle never builds the sidecar; the build or the installer puts Mantle.Sidecar.exe there)");
            return;
        }
        var args = new List<string> { "--parent-pid=" + _o.ParentPid };
        if (_o.StateFile != null) args.Add("--state-file=" + _o.StateFile);
        if (_o.Passive) args.Add("--passive");
        ISidecarProcess p;
        try { p = _launcher.Start(_o.ExePath, args, this); }
        catch (Exception e) { SetStatus(PlatformState.Down, "sidecar would not start: " + e.Message); return; }
        SpawnCount++;
        _proc = p;
        // the pid and its job, every start and every restart (lane native-r7-sidecar; the UI test reads this line)
        _o.Log("info", $"started the sidecar, pid {p.Pid}{(p.Containment is { Length: > 0 } c ? ", " + c : "")}");
        _startedAt = _host.NowMs;
        _readyAt = 0;
        _hadReady = false;
        _stderr = (0, 0, 0);
        _stdoutBuf = "";
        _scanFrom = 0;
        _dropping = null;
        _winIcons.Reset();
        _winIconRepair = false;
        _exitWait = new TaskCompletionSource<bool>();
        SetStatus(PlatformState.Starting, "sidecar started, waiting for its ready line" + (_hungProc != null ? " (restarted because the helper was not answering)" : ""));
    }

    public void Retry()
    {
        if (_stopping && _proc == null) {
            SetStatus(PlatformState.Down, "the sidecar was stopped because Mantle is shutting down; it starts with Mantle");
            return;
        }
        if (_proc != null) return;
        _restartTimer?.Dispose();
        Restarts = 0;
        GaveUp = false;
        Start();
    }

    string _stopWhy = "sidecar stopped because Mantle is shutting down";

    public ValueTask StopAsync(string why)
    {
        _stopping = true;
        if (!string.IsNullOrWhiteSpace(why)) _stopWhy = why;
        HealthStop();
        _restartTimer?.Dispose();
        var p = _proc;
        if (p == null) { if (_status.State != PlatformState.Down) SetStatus(PlatformState.Down, _stopWhy); return ValueTask.CompletedTask; }
        long quitId = TakeId();
        _unawaited.Add(quitId);
        p.TryWriteLine("{\"id\":" + quitId + ",\"cmd\":\"quit\"}", _ => { }, out _);
        p.EndInput();
        var wait = _exitWait!;
        _host.Delay(TimeSpan.FromMilliseconds(_o.StopGraceMs), () => { if (!p.HasExited) p.Kill(); });
        _host.Delay(TimeSpan.FromMilliseconds(_o.StopGraceMs + 2000), () => wait.TrySetResult(false));
        return new ValueTask(wait.Task);
    }

    // ------------------------------------------------------------------ the process's output

    public void Exited(ISidecarProcess from, int? code)
    {
        if (_proc != from) return;
        FlushStderrHeld();
        HealthStop();
        string how = _hungProc == from
            ? $"was stopped because the helper is not answering (no reply to {_o.HealthMisses} pings in a row)"
            : code == null ? "ended (its exit code could not be read)" : $"exited with code {code}";
        if (_hadReady && _host.NowMs - _readyAt >= _o.StableMs) Restarts = 0;
        FailAllPending($"sidecar {how} before answering");
        _proc = null;
        _exitWait?.TrySetResult(true);
        if (_stopping) { SetStatus(PlatformState.Down, _stopWhy); return; }
        Raise("died", "{\"ev\":\"died\",\"code\":" + (code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + "}");
        string tail = LastLog != null ? $" last it said: \"{Clip(LastLog, StderrLineMax)}\"" : "";
        if (Restarts < MaxRestarts) {
            Restarts++;
            int delay = _o.RestartDelayMs * (1 << (Restarts - 1));
            SetStatus(PlatformState.Down, $"sidecar {how}; restarting in {FmtMs(delay)} (attempt {Restarts} of {MaxRestarts}).{tail}");
            _restartTimer = _host.Delay(TimeSpan.FromMilliseconds(delay), Start);
        } else {
            GaveUp = true;
            SetStatus(PlatformState.Down, $"sidecar {how} and gave up after {MaxRestarts} restarts in a row; try again (retry) or restart Mantle; {PlatformWords.KeepsStopping}.{tail}");
        }
    }

    public void Stderr(ISidecarProcess from, string chunk)
    {
        if (_proc != from) return;
        double now = _host.NowMs;
        foreach (var raw in chunk.Split('\n')) {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            LastLog = Clip(line, StderrLineMax);
            if (now - _stderr.WindowAt >= StderrWindowMs) { FlushStderrHeld(); _stderr = (now, 0, 0); }
            if (_stderr.Used >= StderrLinesPerWindow) { _stderr.Held++; continue; }
            _stderr.Used++;
            _o.Log("error", LastLog);
        }
    }

    void FlushStderrHeld()
    {
        if (_stderr.Held == 0) return;
        int n = _stderr.Held;
        _stderr.Held = 0;
        _o.Log("error", $"the sidecar printed {n} more error line(s) in {StderrWindowMs / 1000} s; not logged one by one (the last: \"{LastLog}\")");
    }

    public void Stdout(ISidecarProcess from, string chunk)
    {
        if (_proc != from) return;
        if (_dropping is { } d) {
            int end = chunk.IndexOf('\n');
            if (end < 0) { _dropping = (d.Head, d.Chars + chunk.Length); return; }
            _dropping = null;
            OnOversized(d.Head, d.Chars + end);
            chunk = chunk[(end + 1)..];
        }
        _stdoutBuf += chunk;
        int from0 = _scanFrom, nl;
        while ((nl = _stdoutBuf.IndexOf('\n', from0)) >= 0) {
            from0 = 0;
            string line = _stdoutBuf[..nl].Trim();
            _stdoutBuf = _stdoutBuf[(nl + 1)..];
            if (line.Length == 0) continue;
            OnLine(line);
            if (_proc != from) return;   // a handler stopped it
        }
        _scanFrom = _stdoutBuf.Length;
        if (_stdoutBuf.Length > MaxLineChars) {
            _dropping = (_stdoutBuf[..64], _stdoutBuf.Length);
            _stdoutBuf = "";
            _scanFrom = 0;
        }
    }

    void OnLine(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) {
            string what = line[0] == '{' ? $"a broken protocol line ({line.Length} chars{DescribeHead(line)}; its content is not logged)" : Clip(line, 200);
            _o.Log("warn", "sidecar printed a line that is not JSON: " + what);
            return;
        }
        using (doc) {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object) {
                _o.Log("warn", $"sidecar sent an object with neither id nor ev ({line.Length} chars, keys: (none); its content is not logged)");
                return;
            }
            if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Null) {
                LastLog = "the sidecar could not read something we sent: " + (Str(m, "error") ?? "no reason given");
                _o.Log("warn", LastLog);
                return;
            }
            if (id.ValueKind == JsonValueKind.Number) { OnReply(id.GetDouble(), m); return; }
            if (Str(m, "ev") is { } ev) { OnEvent(ev, m); return; }
            if (Str(m, "event") is { } evn && Regex.IsMatch(evn, "^[A-Za-z0-9_.:-]{1,40}$")) { OnEvent(evn, m); return; }
            _o.Log("warn", $"sidecar sent an object with neither id nor ev ({line.Length} chars, keys: {DescribeKeys(m)}; its content is not logged)");
        }
    }

    void OnOversized(string head, long chars)
    {
        string mb = (chars / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string cap = $"over the {MaxLineChars / (1024 * 1024)} MB line cap";
        var idm = Regex.Match(head, @"^\s*\{""id"":(\d+)[,}]");
        if (idm.Success && long.TryParse(idm.Groups[1].Value, out long id)) {
            if (_pending.Remove(id, out var p)) {
                p.Done(PlatformReply.Fail(PlatformOutcome.Failed, $"the sidecar's answer to \"{p.Cmd}\" was {mb} MB, {cap}, so Mantle dropped it"));
                return;
            }
            var gone = TakeAbandoned(id);
            _o.Log("warn", $"sidecar sent an answer of {mb} MB (id {id}{(gone != null ? $", \"{gone.Cmd}\", whose caller had already timed out" : "")}), {cap}; dropped it (its content is not logged)");
            return;
        }
        _o.Log("warn", $"sidecar sent a line of {mb} MB{DescribeHead(head)}, {cap}; dropped it (its content is not logged)");
    }

    void OnReply(double idNum, JsonElement m)
    {
        long id = (long)idNum;
        bool ok = m.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!_pending.Remove(id, out var p)) {
            if (_unawaited.Remove(id)) return;
            var gone = TakeAbandoned(id);
            if (gone != null) {
                if (!ok && Str(m, "reason") == "expired") {
                    _shedExpired++;
                    _shedSinceReport++;
                    double now = _host.NowMs;
                    if (now - _shedReportedAt >= ShedReportMs) {
                        _o.Log("warn", $"the sidecar skipped {_shedSinceReport} request(s) whose caller had already timed out (latest: \"{gone.Cmd}\"); it is shedding load rather than falling behind");
                        _shedReportedAt = now;
                        _shedSinceReport = 0;
                    }
                    return;
                }
                _shedLate++;
                _o.Log("warn", $"the sidecar answered \"{gone.Cmd}\" (id {id}) {Math.Round(_host.NowMs - gone.GaveUpAt)} ms after its {gone.TimeoutMs} ms timeout; the answer was dropped");
                return;
            }
            _o.Log("warn", $"sidecar answered id {id}, which nobody asked for");
            return;
        }
        if (ok) {
            var result = m.TryGetProperty("result", out var r) ? r.Clone() : default;
            p.Done(PlatformReply.Success(result));
            return;
        }
        string error = Str(m, "error") is { Length: > 0 } e ? e : $"sidecar refused \"{p.Cmd}\" without saying why";
        var outcome = Str(m, "reason") switch {
            "expired" => PlatformOutcome.Expired,
            "busy" => PlatformOutcome.Busy,
            "cancelled" => PlatformOutcome.Cancelled,
            _ => PlatformOutcome.Failed,
        };
        p.Done(PlatformReply.Fail(outcome, error));
    }

    void OnEvent(string ev, JsonElement m)
    {
        if (ev == "ready") {
            _readyAt = _host.NowMs;
            _hadReady = true;
            GaveUp = false;
            int pid = m.TryGetProperty("pid", out var pe) && pe.TryGetInt32(out int pv) ? pv : 0;
            bool passive = m.TryGetProperty("passive", out var pa) && pa.ValueKind == JsonValueKind.True;
            string version = Str(m, "version") ?? "";
            var info = new PlatformInfo(pid, passive, version);
            SetStatus(PlatformState.Ok, $"sidecar {(version.Length > 0 ? version : "(no version)")} running as pid {pid}{(passive ? ", passive: it will refuse to change the shell" : "")}", info);
            HealthStart(_proc);
        } else if (ev == "log") {
            LastLog = Clip(Str(m, "message") ?? "", StderrLineMax);
            _o.Log(Str(m, "level") ?? "info", Str(m, "message") ?? "");
        }
        if (ev == "windows" && m.TryGetProperty("iconDiff", out var idf) && idf.ValueKind == JsonValueKind.True) {
            var (full, missed) = _winIcons.Full(m);
            m = full;
            if (missed.Count > 0 && !_winIconRepair) RepairIcons(missed);
        }
        var body = new PlatformEvent(ev, m.Clone());
        if (_events.TryGetValue(ev, out var hs)) foreach (var h in hs.ToArray()) Safe(() => h(body), $"the \"{ev}\" event handler");
        if (ev == "ready") foreach (var h in _readyHandlers.ToArray()) Safe(h, "a ready handler");
    }

    void Raise(string ev, string json)
    {
        if (!_events.TryGetValue(ev, out var hs) || hs.Count == 0) return;
        using var d = JsonDocument.Parse(json);
        var body = new PlatformEvent(ev, d.RootElement.Clone());
        foreach (var h in hs.ToArray()) Safe(() => h(body), $"the \"{ev}\" event handler");
    }

    // ------------------------------------------------------------------ calls

    /// Why a call cannot be sent right now, or null.
    public string? BlockedReason()
    {
        if (_proc == null) return _status.Reason.Length > 0 ? _status.Reason : "the sidecar is not running";
        if (_status.State != PlatformState.Ok) return _status.Reason.Length > 0 ? _status.Reason : "the sidecar has not finished starting";
        return null;
    }

    public ValueTask<PlatformReply> CallAsync(string verb, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        double ms = timeout?.TotalMilliseconds ?? PlatformDefaults.CallTimeout.TotalMilliseconds;
        if (!(ms > 0) || double.IsNaN(ms) || double.IsInfinity(ms) && ms < 0) ms = PlatformDefaults.CallTimeout.TotalMilliseconds;
        if (ms > MaxTimeoutMs) ms = MaxTimeoutMs;
        bool starting = _proc != null && _status.State == PlatformState.Starting;
        if (!starting && BlockedReason() is { } blocked) return Done(PlatformOutcome.Refused, $"cannot run \"{verb}\": {blocked}");
        if (args != null) {
            foreach (var k in PlatformDefaults.ReservedArgKeys)
                if (args.ContainsKey(k))
                    return Done(PlatformOutcome.Refused, $"cannot run \"{verb}\": its arguments use the reserved key \"{k}\", which would overwrite the request's own; rename it (trayClick takes icon)");
        }
        string argsJson;
        try { argsJson = ArgsBody(args); }
        catch (Exception e) { return Done(PlatformOutcome.Refused, $"cannot run \"{verb}\": its arguments are not JSON ({e.Message})"); }
        if (cancel.IsCancellationRequested) return Done(PlatformOutcome.Cancelled, $"\"{verb}\" was cancelled before it was sent");
        if (starting) return CallWhenReady(verb, argsJson, ms, cancel);
        return Send(verb, argsJson, ms, ms, cancel);
    }

    static ValueTask<PlatformReply> Done(PlatformOutcome o, string why) => new(PlatformReply.Fail(o, why));

    /// The arguments as the inside of a JSON object ("" for none), written once, before the call returns.
    static string ArgsBody(JsonObject? args)
    {
        if (args == null || args.Count == 0) return "";
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf)) {
            w.WriteStartObject();
            foreach (var kv in args) {
                w.WritePropertyName(kv.Key);
                if (kv.Value == null) w.WriteNullValue(); else kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        string s = Encoding.UTF8.GetString(buf.ToArray());
        return s.Length > 2 ? s[1..^1] : "";
    }

    ValueTask<PlatformReply> CallWhenReady(string verb, string argsJson, double total, CancellationToken cancel)
    {
        bool callerIsShorter = total <= _o.ReadyWaitMs;
        double since = _host.NowMs;
        var tcs = new TaskCompletionSource<PlatformReply>();
        var w = new Waiter();
        w.Go = () => {
            double left = total - (_host.NowMs - since);
            if (left <= 0) { tcs.TrySetResult(PlatformReply.Fail(PlatformOutcome.TimedOut, $"the sidecar did not answer \"{verb}\" within {FmtMs(total)}: it only finished starting as the time ran out")); return; }
            var t = Send(verb, argsJson, total, left, cancel);
            if (t.IsCompleted) tcs.TrySetResult(t.Result);
            else t.AsTask().ContinueWith(x => tcs.TrySetResult(x.Result), TaskContinuationOptions.ExecuteSynchronously);
        };
        w.Fail = reason => tcs.TrySetResult(PlatformReply.Fail(PlatformOutcome.Refused, $"cannot run \"{verb}\": {reason}"));
        w.Timer = _host.Delay(TimeSpan.FromMilliseconds(callerIsShorter ? total : _o.ReadyWaitMs), () => {
            if (!_readyWaiters.Remove(w)) return;
            if (callerIsShorter) { tcs.TrySetResult(PlatformReply.Fail(PlatformOutcome.TimedOut, $"the sidecar did not answer \"{verb}\" within {FmtMs(total)}: it had not finished starting (no ready line yet)")); return; }
            string tail = LastLog != null ? $" last it said: \"{LastLog}\"" : "";
            tcs.TrySetResult(PlatformReply.Fail(PlatformOutcome.Refused, $"cannot run \"{verb}\": the sidecar was started {FmtMs(_host.NowMs - _startedAt)} ago and has still not printed its ready line, so it may be hung starting up.{tail}"));
        });
        if (cancel.CanBeCanceled) cancel.Register(() => _host.Post(() => {
            if (_readyWaiters.Remove(w)) { w.Timer?.Dispose(); tcs.TrySetResult(PlatformReply.Fail(PlatformOutcome.Cancelled, $"\"{verb}\" was cancelled while the sidecar was starting")); }
        }));
        _readyWaiters.Add(w);
        return new ValueTask<PlatformReply>(tcs.Task);
    }

    ValueTask<PlatformReply> Send(string verb, string argsJson, double timeoutMs, double leftMs, CancellationToken cancel)
    {
        long id = TakeId();
        long ttl = (long)Math.Max(1, Math.Round(leftMs));
        string line = "{\"id\":" + id + ",\"cmd\":" + JsonQuote(verb) + ",\"ttl\":" + ttl + (argsJson.Length > 0 ? "," + argsJson : "") + "}";
        var p = new Pending(verb);
        var proc = _proc;
        if (proc == null) return Done(PlatformOutcome.Refused, $"could not send \"{verb}\" to the sidecar: it is not running (stopped, or restarting)");
        _pending[id] = p;
        p.Timer = _host.Delay(TimeSpan.FromMilliseconds(leftMs), () => {
            if (!_pending.Remove(id)) return;
            Abandon(id, new Abandoned(verb, _host.NowMs, (long)Math.Round(timeoutMs)));
            p.Done(PlatformReply.Fail(PlatformOutcome.TimedOut, $"the sidecar did not answer \"{verb}\" within {Math.Round(timeoutMs / 1000)}s"));
        });
        if (cancel.CanBeCanceled) p.CancelReg = cancel.Register(() => _host.Post(() => {
            if (!_pending.Remove(id)) return;
            Abandon(id, new Abandoned(verb, _host.NowMs, (long)Math.Round(timeoutMs)));
            p.Done(PlatformReply.Fail(PlatformOutcome.Cancelled, $"\"{verb}\" was cancelled while waiting for the sidecar; it may still run"));
        }));
        if (!proc.TryWriteLine(line, err => {
                if (!_pending.Remove(id)) return;   // answered or timed out already
                p.Done(PlatformReply.Fail(PlatformOutcome.Refused, $"could not send \"{verb}\" to the sidecar: {err}"));
            }, out string? why)) {
            _pending.Remove(id);
            p.Done(PlatformReply.Fail(PlatformOutcome.Refused, $"could not send \"{verb}\" to the sidecar: {why}"));
        }
        return new ValueTask<PlatformReply>(p.Tcs.Task);
    }

    long TakeId()
    {
        if (!(_nextId >= 1 && _nextId < 9007199254740991)) _nextId = 1;   // Number.MAX_SAFE_INTEGER
        long id = _nextId++;
        while (_pending.ContainsKey(id) || _unawaited.Contains(id)) id = _nextId++;
        return id;
    }

    void Abandon(long id, Abandoned a)
    {
        _abandoned[id] = _abandonedOrder.AddLast((id, a));
        while (_abandoned.Count > AbandonedMax) {
            var first = _abandonedOrder.First!;
            _abandonedOrder.RemoveFirst();
            _abandoned.Remove(first.Value.Id);
        }
    }

    Abandoned? TakeAbandoned(long id)
    {
        if (!_abandoned.Remove(id, out var node)) return null;
        _abandonedOrder.Remove(node);
        return node.Value.A;
    }

    void FailAllPending(string reason)
    {
        var all = _pending.Values.ToArray();
        _pending.Clear();
        _abandoned.Clear();
        _abandonedOrder.Clear();
        foreach (var p in all) p.Done(PlatformReply.Fail(PlatformOutcome.Failed, $"{reason} (command \"{p.Cmd}\")"));
    }

    /// <summary>lib/sidecar.js protocolAge(): does this exe speak the safety protocol (its ping reports dpiAware)?</summary>
    public async ValueTask<(bool Current, bool? DpiAware, bool Answered, string Reason)> ProtocolAgeAsync()
    {
        var r = await CallAsync("ping", null, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(false);
        if (!r.Ok) return (false, null, false, "the sidecar would not answer ping, so Mantle cannot tell how old it is: " + r.Error);
        if (r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("dpiAware", out var d)) {
            bool aware = d.ValueKind == JsonValueKind.True;
            string version = Str(r.Result, "version") ?? "(no version)";
            return (true, aware, true, $"sidecar {version} speaks the current protocol and is {(aware ? "DPI-aware, so its rectangles are physical pixels" : "DPI-unaware, so its rectangles are already DIP on the primary display")}");
        }
        return (false, null, true, "sidecar exe is older than the protocol: " + PlatformWords.Rebuild + " (its ping does not report dpiAware, so it does not know the auto-hide or app-bar commands, and Mantle will not guess at what it would do instead)");
    }

    /// lib/sidecar.js isOldExeError: the words an exe uses for a verb it does not know.
    public static bool IsOldExeError(string? error)
    {
        string m = (error ?? "").ToLowerInvariant();
        return m.Contains("unknown command") || m.Contains("unknown cmd") || m.Contains("needs show:true") || m.Contains("no such command");
    }

    // ------------------------------------------------------------------ subscriptions

    public IDisposable On(string ev, Action<PlatformEvent> handler)
    {
        if (!_events.TryGetValue(ev, out var list)) _events[ev] = list = new List<Action<PlatformEvent>>();
        list.Add(handler);
        return new Unsub(() => list.Remove(handler));
    }

    public IDisposable OnStatus(Action<PlatformStatus> handler)
    {
        _statusHandlers.Add(handler);
        return new Unsub(() => _statusHandlers.Remove(handler));
    }

    public IDisposable OnReady(Action handler)
    {
        _readyHandlers.Add(handler);
        if (_status.Ready) Safe(handler, "a ready handler");
        return new Unsub(() => _readyHandlers.Remove(handler));
    }

    // ------------------------------------------------------------------ health

    void HealthStart(ISidecarProcess? proc)
    {
        HealthStop();
        _hungProc = null;
        if (!(_o.HealthMs > 0) || proc == null) return;
        var h = new Health(proc) { LastTickAt = _host.NowMs };
        _health = h;
        h.Timer = _host.Delay(TimeSpan.FromMilliseconds(_o.HealthMs), () => HealthTick(h));
    }

    void HealthStop()
    {
        var h = _health;
        if (h == null) return;
        _health = null;
        h.Timer?.Dispose();
        h.MissTimer?.Dispose();
    }

    void HealthTick(Health h)
    {
        if (_health != h) return;
        if (_stopping || _proc != h.Proc) { HealthStop(); return; }
        h.Timer = _host.Delay(TimeSpan.FromMilliseconds(_o.HealthMs), () => HealthTick(h));
        double now = _host.NowMs, gap = now - h.LastTickAt;
        h.LastTickAt = now;
        if (gap > _o.HealthMs * 3) { h.Misses = 0; h.Counted = true; h.Gaps++; return; }   // a sleep, not a hang
        if (_status.State != PlatformState.Ok) return;
        if (h.InFlight) { h.Counted = true; HealthMiss(h); return; }   // never two in flight
        HealthPing(h);
    }

    void HealthPing(Health h)
    {
        int seq = ++h.Seq;
        double sentAt = _host.NowMs;
        double limit = _o.HealthMs * (_o.HealthMisses + 1) + _o.HealthTimeoutMs;
        h.InFlight = true;
        h.Counted = false;
        h.MissTimer?.Dispose();
        h.MissTimer = _host.Delay(TimeSpan.FromMilliseconds(_o.HealthTimeoutMs), () => {
            if (_health != h || h.Seq != seq || !h.InFlight || h.Counted) return;
            h.Counted = true;
            if (_host.NowMs - sentAt > _o.HealthTimeoutMs * 3) return;
            HealthMiss(h);
        });
        void Answered(PlatformReply r)
        {
            if (h.Seq != seq) return;
            h.InFlight = false;
            h.MissTimer?.Dispose();
            bool counts = r.Ok || (r.Outcome == PlatformOutcome.Busy) ||
                (_host.NowMs - sentAt < limit - 50 && _proc == h.Proc && _hungProc != h.Proc && !h.Proc.HasExited
                 && r.Outcome != PlatformOutcome.TimedOut && !(r.Error ?? "").Contains("could not send", StringComparison.Ordinal));
            if (counts) h.Misses = 0;
        }
        var t = CallAsync("ping", null, TimeSpan.FromMilliseconds(limit));
        if (t.IsCompleted) Answered(t.Result);
        else t.AsTask().ContinueWith(x => Answered(x.Result), TaskContinuationOptions.ExecuteSynchronously);
    }

    void HealthMiss(Health h)
    {
        h.Misses++;
        if (h.Misses >= _o.HealthMisses) HealthKill(h);
    }

    void HealthKill(Health h)
    {
        var proc = h.Proc;
        HealthStop();
        if (_proc != proc || _stopping) return;
        _hungProc = proc;
        HealthKills++;
        _o.Log("warn", $"the sidecar (pid {proc.Pid}) did not answer {h.Misses} pings in a row ({FmtMs(_o.HealthMs)} apart, {FmtMs(_o.HealthTimeoutMs)} limit each): it is running but not answering, so Mantle is ending it and the usual restart follows");
        SetStatus(PlatformState.Down, $"the helper is not answering (no reply to {h.Misses} pings in a row); stopping it so it restarts");
        proc.Kill();
        _host.Delay(TimeSpan.FromMilliseconds(5000), () => {
            if (_proc == proc && !proc.HasExited)
                SetStatus(PlatformState.Down, $"the helper is not answering and Mantle could not end it (pid {proc.Pid}); restart Mantle, or end Mantle.Sidecar.exe in Task Manager");
        });
    }

    // ------------------------------------------------------------------ words

    public static string FmtMs(double ms) => ms < 1000 ? $"{Math.Max(0, Math.Round(ms))} ms" : $"{Math.Round(ms / 1000)}s";
    static string Clip(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
    static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string JsonQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s) {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// What a dropped or broken line was, without its content: only the protocol head ({"id":N or {"ev":"x").
    static string DescribeHead(string head)
    {
        var m = Regex.Match(head, @"^\s*\{""(id|ev)"":(\d+|""[A-Za-z0-9_.:-]{1,40}"")");
        return m.Success ? $", starting {m.Value.TrimStart()}" : "";
    }

    static string DescribeKeys(JsonElement m)
    {
        var keys = m.EnumerateObject().Select(p => p.Name).Take(8).Select(k => Clip(k, 24)).ToList();
        return keys.Count == 0 ? "(none)" : string.Join(", ", keys);
    }

    sealed class Pending
    {
        public readonly string Cmd;
        public readonly TaskCompletionSource<PlatformReply> Tcs = new();
        public IDisposable? Timer;
        public CancellationTokenRegistration CancelReg;
        public Pending(string cmd) { Cmd = cmd; }
        public void Done(PlatformReply r)
        {
            Timer?.Dispose();
            CancelReg.Dispose();
            Tcs.TrySetResult(r);
        }
    }

    sealed record Abandoned(string Cmd, double GaveUpAt, long TimeoutMs);

    sealed class Waiter
    {
        public Action Go = () => { };
        public Action<string> Fail = _ => { };
        public IDisposable? Timer;
    }

    sealed class Health
    {
        public readonly ISidecarProcess Proc;
        public Health(ISidecarProcess p) { Proc = p; }
        public IDisposable? Timer, MissTimer;
        public int Misses, Seq, Gaps;
        public bool InFlight, Counted;
        public double LastTickAt;
    }

    sealed class Unsub : IDisposable
    {
        Action? _undo;
        public Unsub(Action undo) { _undo = undo; }
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
