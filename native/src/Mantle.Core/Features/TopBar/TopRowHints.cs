using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.TopBar;

// TOP-ROW HINTS (TOP-362..371, 374): lib/toprow-hints.js, Mantle's RECEIVER of the look-sync top-row hints
// (~/look-sync CONTRACT.md, "Top-row hints (v1)"). Quartz and Helm say, over the named pipe \\.\pipe\look-sync-toprow,
// what their windows' top pixel row looks like, so the colour layer under the bar can paint a switch to that window on
// the frame of the switch. One JSON object per line, UTF-8, at most 8 KB a line:
//   {"v":1,"app":"quartz"|"helm","hwnd":"<decimal>","seq":<int>,"t":<ms>,"top":TOP}
//   TOP = {"kind":"wash","rgb":[r,g,b],"alpha":a} | {"kind":"flat","rgb":[r,g,b]} | {"kind":"cols","cols":[[r,g,b],...]} | {"kind":"none"}
// This class checks each line (the JSON, the sender's process through the sidecar's winproc, dark only) and hands what
// it keeps to Forward (the feature sends it on to the sidecar's colour layer, `underhint`). The colour layer, the wash
// base (TOP-375) and the capture verdicts (TOP-373) are the glass / under-colour work, not this class.
// Nothing runs at rest: the pipe waits on an overlapped connect, a client's reads on overlapped reads, no timer unless
// the pipe was busy (then one retry every 10 s). A handler never throws out: a bug is logged once.
public sealed record HintProc(bool Ok, int Pid = 0, string? Path = null, string? Reason = null);
public sealed record HintMeta(long Seq, double T, long RecvAt, string? Why = null);

public sealed class TopRowHintsOptions
{
    /// A bare name gets the \\.\pipe\ prefix. A --test Mantle passes its own so it never takes the live one.
    public string? PipeName { get; init; }
    /// The sidecar's winproc: whose process the window is.
    public Func<string, Task<HintProc>>? ProcOf { get; init; }
    /// Light theme: nothing new is kept or forwarded (forgets still forward null).
    public Func<bool>? Dark { get; init; }
    /// (hwnd, app, top or null = forget the window, meta)
    public Action<string, string, JsonObject?, HintMeta>? Forward { get; init; }
    public Action<string>? Log { get; init; }
    public Func<long>? Now { get; init; }
    /// The user folder the worktree rule is under (tests).
    public string? Home { get; init; }
    /// How often a busy pipe is tried again (tests).
    public int RetryMs { get; init; }
    /// Each time the listener wakes (a connect, a read, a busy-pipe retry, a window check answering): --test logs it,
    /// so a run where nobody connects can show the listener never woke at rest.
    public Action<string>? OnWake { get; init; }
}

public sealed class TopRowHints : IDisposable
{
    public const string DefaultPipe = @"\\.\pipe\look-sync-toprow";
    public const int MaxLine = 8192, MaxColsIn = 4096, MaxColsOut = 480, RecheckMs = 30000, RetryListenMs = 10000, LogEveryMs = 60000;
    public const int MaxClients = 32, MaxHwndsPerClient = 64;
    public static readonly string[] Apps = { "quartz", "helm" };
    static readonly Regex HwndRx = new("^[1-9][0-9]{0,19}$", RegexOptions.Compiled);

    // ------------------------------------------------------------------ pure
    /// '\\.\pipe\x' stays; a bare name 'x' becomes '\\.\pipe\x'; nothing = the live pipe.
    public static string PipePath(string? name) => string.IsNullOrEmpty(name) ? DefaultPipe : name.StartsWith(@"\\", StringComparison.Ordinal) ? name : @"\\.\pipe\" + name;

    /// A JSON number that is a whole number (JS Number.isInteger), else null.
    static double? Whole(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && !double.IsInfinity(d) && Math.Floor(d) == d ? d : null;
    static double? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsInfinity(d) ? d : null;
    static int[]? Rgb(JsonNode? n)
    {
        if (n is not JsonArray a || a.Count != 3) return null;
        var o = new int[3];
        for (int i = 0; i < 3; i++) { if (Whole(a[i]) is not double d || d < 0 || d > 255) return null; o[i] = (int)d; }
        return o;
    }
    static JsonArray Arr(IEnumerable<int> v) { var a = new JsonArray(); foreach (var x in v) a.Add((JsonNode)JsonValue.Create(x)); return a; }

    /// cols [[r,g,b] x N] -> a flat [r,g,b,...] of at most max entries, averaged by area (resampleCols).
    public static int[] ResampleCols(IReadOnlyList<int[]> cols, int max = MaxColsOut)
    {
        int lim = max > 0 ? max : MaxColsOut, n = cols.Count;
        if (n <= lim) { var flat = new int[n * 3]; for (int i = 0; i < n; i++) { flat[i * 3] = cols[i][0]; flat[i * 3 + 1] = cols[i][1]; flat[i * 3 + 2] = cols[i][2]; } return flat; }
        var o = new int[lim * 3];
        double step = (double)n / lim;
        for (int i = 0; i < lim; i++)
        {
            double a = i * step, b = (i + 1) * step, r = 0, g = 0, bl = 0, ws = 0;
            for (int j = (int)Math.Floor(a); j < Math.Ceiling(b) && j < n; j++)
            {
                double w = Math.Min(b, j + 1) - Math.Max(a, j);
                if (w <= 0) continue;
                r += cols[j][0] * w; g += cols[j][1] * w; bl += cols[j][2] * w; ws += w;
            }
            o[i * 3] = JsRound(r / ws); o[i * 3 + 1] = JsRound(g / ws); o[i * 3 + 2] = JsRound(bl / ws);
        }
        return o;
    }
    static int JsRound(double v) => (int)Math.Floor(v + 0.5);   // Math.round: halves go up

    /// A TOP -> the normalised top, or the reason it is bad (normaliseTop). kind none -> {kind: none}.
    public static (JsonObject? Top, string? Bad) NormaliseTop(JsonNode? top)
    {
        if (top is not JsonObject t) return (null, "bad top");
        string? kind = t["kind"] is JsonValue kv && kv.GetValueKind() == JsonValueKind.String ? kv.GetValue<string>() : null;
        switch (kind)
        {
            case "wash":
            {
                var rgb = Rgb(t["rgb"]);
                if (rgb == null || Num(t["alpha"]) is not double al || !(al >= 0 && al <= 1)) return (null, "bad top (wash)");
                return (new JsonObject { ["kind"] = "wash", ["rgb"] = Arr(rgb), ["alpha"] = al }, null);
            }
            case "flat":
            {
                var rgb = Rgb(t["rgb"]);
                if (rgb == null) return (null, "bad top (flat)");
                return (new JsonObject { ["kind"] = "flat", ["rgb"] = Arr(rgb) }, null);
            }
            case "cols":
            {
                if (t["cols"] is not JsonArray c || c.Count < 1 || c.Count > MaxColsIn) return (null, "bad top (cols)");
                var list = new List<int[]>(c.Count);
                foreach (var e in c) { if (Rgb(e) is not { } x) return (null, "bad top (cols)"); list.Add(x); }
                return (new JsonObject { ["kind"] = "cols", ["cols"] = Arr(ResampleCols(list)) }, null);
            }
            case "none": return (new JsonObject { ["kind"] = "none" }, null);
            default: return (null, "bad top");
        }
    }

    public sealed record Msg(string App, string Hwnd, long Seq, double T, JsonObject Top);

    /// A parsed message -> the message or the reason it is ignored (checkMessage). Seq order is the caller's.
    public static (Msg? Msg, string? Bad) CheckMessage(JsonNode? m)
    {
        if (m is not JsonObject o) return (null, "not valid JSON");
        if (Num(o["v"]) is not 1.0) return (null, "not protocol v1");
        string? app = o["app"] is JsonValue av && av.GetValueKind() == JsonValueKind.String ? av.GetValue<string>() : null;
        if (app == null || Array.IndexOf(Apps, app) < 0) return (null, "unknown app");
        string? hwnd = o["hwnd"] is JsonValue hv && hv.GetValueKind() == JsonValueKind.String ? hv.GetValue<string>() : null;
        if (hwnd == null || !HwndRx.IsMatch(hwnd)) return (null, "bad hwnd");
        if (Whole(o["seq"]) is not double seq || seq < 0 || seq > 9007199254740991) return (null, "bad seq");
        if (Num(o["t"]) is not double t) return (null, "bad t");
        var (top, bad) = NormaliseTop(o["top"]);
        if (bad != null) return (null, bad);
        return (new Msg(app, hwnd, (long)seq, t, top!), null);
    }

    /// Does exe `p` belong to `app`? Quartz = Quartz.exe, or electron.exe under <home>\browser\ or a worktree
    /// <home>\browser-*\ ; Helm = Helm.exe, or electron.exe under <home>\helm\ or <home>\helm-*\ (appOwns).
    public static bool AppOwns(string app, string? p, string home)
    {
        if (string.IsNullOrEmpty(p)) return false;
        string norm = p.Replace('/', '\\').ToLowerInvariant();
        string bas = norm[(norm.LastIndexOf('\\') + 1)..];
        string? exe = app == "quartz" ? "quartz.exe" : app == "helm" ? "helm.exe" : null;
        if (exe == null) return false;
        if (bas == exe) return true;
        if (bas != "electron.exe") return false;
        string h = home.Replace('/', '\\').ToLowerInvariant().TrimEnd('\\') + "\\";
        if (!norm.StartsWith(h, StringComparison.Ordinal)) return false;
        string top = norm[h.Length..].Split('\\')[0];
        string dir = app == "quartz" ? "browser" : "helm";
        return top == dir || (top.StartsWith(dir + "-", StringComparison.Ordinal) && top.Length > dir.Length + 1);
    }

    // ------------------------------------------------------------------ the receiver
    /// One client connection (a pipe instance; the tests make them with Connect()).
    public sealed class Conn
    {
        internal readonly List<byte> Parts = new();
        internal bool Discard, Closed;
        internal readonly HashSet<string> Hwnds = new(), Seen = new();
        internal Action? Shut;
    }
    sealed class Hint { public required string App; public required JsonObject Top; public required HintMeta Meta; public required Conn From; }
    sealed class Check { public string State = "pending"; public long At; public int? Pid; public string? Why, Detail; public Pending? Latest; public bool Bg; }
    sealed record Pending(Msg Msg, Conn From, long RecvAt);

    readonly object _gate = new();
    readonly string _pipe, _home;
    readonly Func<string, Task<HintProc>> _procOf;
    readonly Func<long> _now;
    readonly int _retryMs;
    readonly TopRowHintsOptions _o;
    readonly HashSet<Conn> _conns = new();
    readonly Dictionary<string, Hint> _hints = new();
    readonly Dictionary<string, (Conn From, long Seq)> _seqs = new();
    readonly Dictionary<string, Check> _checks = new();
    readonly Dictionary<string, int> _ignored = new();
    readonly Dictionary<string, long> _lastLogged = new();
    readonly Dictionary<string, (string? Mismatch, long? Since)> _apps = new() { ["quartz"] = (null, null), ["helm"] = (null, null) };
    readonly HashSet<string> _mismatchLogged = new();
    bool _bugLogged, _listening, _stopped;
    long _wakes;
    /// How often the listener woke (see TopRowHintsOptions.OnWake). 0 while nobody connects.
    public long Wakes => Interlocked.Read(ref _wakes);
    void Woke(string what) { Interlocked.Increment(ref _wakes); try { _o.OnWake?.Invoke(what); } catch { /* a log must never break us */ } }
    string? _reason = "not started";
    CancellationTokenSource? _cts;
    Task<(bool Ok, string? Reason)>? _startT;

    public TopRowHints(TopRowHintsOptions o)
    {
        _o = o;
        _pipe = PipePath(o.PipeName);
        _procOf = o.ProcOf ?? (_ => Task.FromResult(new HintProc(false, Reason: "no window check was given")));
        _now = o.Now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _home = o.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _retryMs = o.RetryMs > 0 ? o.RetryMs : RetryListenMs;
    }

    public string Pipe => _pipe;
    bool IsDark() { try { return _o.Dark?.Invoke() ?? true; } catch { return false; } }
    void Say(string m) { try { _o.Log?.Invoke("top-row hints: " + m); } catch { /* a log must never break us */ } }
    void Bug(string where, Exception e) { if (!_bugLogged) { _bugLogged = true; Say($"a bug in {where} was caught ({e.Message}); further ones are not logged"); } }
    void Count(string why, string? app = null)
    {
        _ignored[why] = _ignored.TryGetValue(why, out var n) ? n + 1 : 1;
        long t = _now();
        if (!_lastLogged.TryGetValue(why, out var last) || t - last >= LogEveryMs)
        {
            _lastLogged[why] = t;
            Say($"ignored a message{(app != null ? " from " + app : "")}: {why} ({_ignored[why]} so far; logged at most once a minute)");
        }
    }
    void Fwd(string hwnd, string app, JsonObject? top, HintMeta meta) { try { _o.Forward?.Invoke(hwnd, app, top, meta); } catch (Exception e) { Bug("forward", e); } }
    bool Drop(string hwnd, string why)
    {
        if (!_hints.Remove(hwnd, out var h)) return false;
        Fwd(hwnd, h.App, null, new HintMeta(0, 0, _now(), why));
        return true;
    }

    // ---- the window check: once per (app, hwnd), cached
    void RunCheck(string key, string app, string hwnd, Check c)
    {
        Task<HintProc> p;
        try { p = _procOf(hwnd) ?? Task.FromResult(new HintProc(false, Reason: "no answer")); }
        catch (Exception e) { p = Task.FromResult(new HintProc(false, Reason: e.Message)); }
        p.ContinueWith(t =>
        {
            Woke("a window check answered");
            var r = t.IsCompletedSuccessfully ? t.Result : new HintProc(false, Reason: t.Exception?.InnerException?.Message ?? "the check was cancelled");
            lock (_gate)
            {
                try
                {
                    if (!_checks.TryGetValue(key, out var cur) || cur != c) return;   // forgotten meanwhile
                    int? prevPid = c.Pid;
                    c.At = _now(); c.Bg = false;
                    if (r == null || !r.Ok) { c.State = "fail"; c.Why = "window check failed"; c.Detail = r?.Reason ?? "no answer"; }
                    else if (!AppOwns(app, r.Path, _home)) { c.State = "fail"; c.Why = "window is not that app's"; c.Detail = r.Path; c.Pid = r.Pid; }
                    else { c.State = "ok"; c.Pid = r.Pid; c.Why = null; }
                    if (c.State == "fail")
                    {
                        if (_hints.TryGetValue(hwnd, out var h) && h.App == app) Drop(hwnd, c.Why!);
                        Count(c.Why!, app);
                    }
                    else if (prevPid != null && prevPid != c.Pid) Say($"window {hwnd} of {app} now belongs to process {c.Pid} (was {prevPid}); still {app}'s");
                    var l = c.Latest; c.Latest = null;
                    if (l != null && c.State == "ok") Accept(l);
                }
                catch (Exception e) { Bug("the window check", e); }
            }
        }, TaskScheduler.Default);
    }

    void Accept(Pending l)
    {
        if (l.From.Closed || !_conns.Contains(l.From)) return;
        if (!IsDark()) { Count("light theme", l.Msg.App); return; }
        var st = l.From;
        if (!st.Hwnds.Contains(l.Msg.Hwnd) && st.Hwnds.Count >= MaxHwndsPerClient) { Count("too many windows on one client", l.Msg.App); return; }
        st.Hwnds.Add(l.Msg.Hwnd);
        var meta = new HintMeta(l.Msg.Seq, l.Msg.T, l.RecvAt);
        _hints[l.Msg.Hwnd] = new Hint { App = l.Msg.App, Top = l.Msg.Top, Meta = meta, From = st };
        Fwd(l.Msg.Hwnd, l.Msg.App, l.Msg.Top, meta);
    }

    void HandleLine(byte[] buf, Conn st)
    {
        long recvAt = _now();
        string s = Encoding.UTF8.GetString(buf).Trim().Trim('\uFEFF').Trim();
        if (s.Length == 0) return;
        JsonNode? m;
        try { m = JsonNode.Parse(s); } catch (Exception) { Count("not valid JSON"); return; }
        Msg? msg; string? bad; string? from = null;
        try
        {
            (msg, bad) = CheckMessage(m);
            if (bad != null && m is JsonObject mo && mo["app"] is JsonValue av && av.GetValueKind() == JsonValueKind.String && Array.IndexOf(Apps, av.GetValue<string>()) >= 0) from = av.GetValue<string>();
        }
        catch (Exception) { msg = null; bad = "not valid JSON"; }   // e.g. a duplicate key
        if (bad != null || msg == null) { Count(bad ?? "not valid JSON", from); return; }
        if (_seqs.TryGetValue(msg.Hwnd, out var sq) && sq.From == st && msg.Seq <= sq.Seq) { Count("seq not increasing", msg.App); return; }
        _seqs[msg.Hwnd] = (st, msg.Seq);
        st.Seen.Add(msg.Hwnd);

        if ((string?)msg.Top["kind"] == "none")
        {
            if (_hints.TryGetValue(msg.Hwnd, out var h) && h.From == st) Drop(msg.Hwnd, "the app said none");
            if (_checks.TryGetValue(msg.App + "|" + msg.Hwnd, out var c0) && c0.Latest != null) c0.Latest = null;
            return;
        }
        if (!IsDark())
        {
            // light theme: keep nothing new; a kept hint for this window is now stale, so it goes
            if (_hints.TryGetValue(msg.Hwnd, out var h) && h.From == st) Drop(msg.Hwnd, "light theme");
            Count("light theme", msg.App);
            return;
        }
        string key = msg.App + "|" + msg.Hwnd;
        var l = new Pending(msg, st, recvAt);
        _checks.TryGetValue(key, out var c);
        if (c is { State: "ok" })
        {
            Accept(l);
            if (!c.Bg && _now() - c.At >= RecheckMs) { c.Bg = true; RunCheck(key, msg.App, msg.Hwnd, c); }
            return;
        }
        if (c is { State: "pending" }) { c.Latest = l; return; }
        if (c is { State: "fail" } && _now() - c.At < RecheckMs) { Count(c.Why!, msg.App); return; }
        c = new Check { State = "pending", At = _now(), Latest = l, Pid = c?.Pid };
        _checks[key] = c;
        RunCheck(key, msg.App, msg.Hwnd, c);
    }

    // ---- line framing, 8 KB cap
    /// The bytes a client sent (the pipe reader calls this; so do the tests).
    public void Feed(Conn st, ReadOnlySpan<byte> chunk)
    {
        lock (_gate)
        {
            try
            {
                if (st.Closed) return;
                int start = 0;
                for (; ; )
                {
                    int idx = chunk[start..].IndexOf((byte)10);
                    if (idx < 0)
                    {
                        if (st.Discard) return;
                        var rest = chunk[start..];
                        if (rest.Length == 0) return;
                        if (st.Parts.Count + rest.Length > MaxLine + 1)   // +1: room for a CR that ends up before the newline
                        {
                            st.Parts.Clear(); st.Discard = true;
                            Count("line over 8 KB");
                            return;
                        }
                        foreach (var b in rest) st.Parts.Add(b);
                        return;
                    }
                    var piece = chunk.Slice(start, idx);
                    start += idx + 1;
                    if (st.Discard) { st.Discard = false; continue; }   // the end of an over-long line
                    byte[] line;
                    if (st.Parts.Count > 0) { foreach (var b in piece) st.Parts.Add(b); line = st.Parts.ToArray(); }
                    else line = piece.ToArray();
                    st.Parts.Clear();
                    int len = line.Length;
                    if (len > 0 && line[len - 1] == 13) len--;
                    if (len > MaxLine) { Count("line over 8 KB"); continue; }
                    try { HandleLine(len == line.Length ? line : line[..len], st); } catch (Exception e) { Bug("a message", e); }
                }
            }
            catch (Exception e) { Bug("the pipe reader", e); }
        }
    }

    /// A new client (the pipe calls this; so do the tests). null = refused (too many clients, or stopped).
    public Conn? Connect(Action? shut = null)
    {
        lock (_gate)
        {
            if (_stopped || _conns.Count >= MaxClients) { if (!_stopped) Count("too many clients"); return null; }
            var st = new Conn { Shut = shut };
            _conns.Add(st);
            return st;
        }
    }

    /// A client went away: its hints are dropped (forwarded as null), its seqs and checks forgotten.
    public void Disconnect(Conn st)
    {
        lock (_gate)
        {
            try
            {
                st.Closed = true;
                if (!_conns.Remove(st)) return;
                foreach (var hw in st.Hwnds) if (_hints.TryGetValue(hw, out var h) && h.From == st) Drop(hw, "the app disconnected");
                foreach (var hw in st.Seen)
                {
                    if (_seqs.TryGetValue(hw, out var sq) && sq.From == st) _seqs.Remove(hw);
                    foreach (var a in Apps)
                        if (_checks.TryGetValue(a + "|" + hw, out var c) && (c.State != "pending" || c.Latest?.From == st)) _checks.Remove(a + "|" + hw);
                }
                st.Parts.Clear();
            }
            catch (Exception e) { Bug("a disconnect", e); }
        }
    }

    // ---- listening (a real pipe; the first instance is FILE_FLAG_FIRST_PIPE_INSTANCE, so a pipe someone else holds is
    // "busy", as Node's EADDRINUSE; CurrentUserOnly: only this user's programs can connect)
    public Task<(bool Ok, string? Reason)> StartAsync()
    {
        lock (_gate)
        {
            if (_startT != null) return _startT;
            _stopped = false;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _startT = Task.Run<(bool Ok, string? Reason)>(() =>
            {
                var r = ListenOnce(ct);
                lock (_gate)
                {
                    if (_stopped) return (false, "stopped");
                    if (r.Ok) { _listening = true; _reason = null; Say("listening on " + _pipe); return (true, null); }
                    _listening = false; _reason = r.Reason; Say(r.Reason!);
                }
                _ = RetryLoop(ct);
                return (false, r.Reason);
            });
            return _startT;
        }
    }

    (bool Ok, string? Reason) ListenOnce(CancellationToken ct)
    {
        NamedPipeServerStream first;
        try { first = NewServer(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, $"another program (most likely another Mantle) is already listening on {_pipe}, so this Mantle cannot take the top-row hints; trying again every {_retryMs / 1000.0:0.###} s");
        }
        catch (Exception e) { return (false, $"the pipe {_pipe} could not listen ({e.Message}); trying again every {_retryMs / 1000.0:0.###} s"); }
        _ = AcceptLoop(first, ct);
        return (true, null);
    }

    NamedPipeServerStream NewServer(bool first) => new(_pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? _pipe[@"\\.\pipe\".Length..] : _pipe, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    async Task RetryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_retryMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            Woke("the busy pipe is tried again");
            var r = ListenOnce(ct);
            lock (_gate)
            {
                if (_stopped) return;
                if (r.Ok) { _listening = true; _reason = null; Say($"listening on {_pipe} (it was busy before)"); return; }
                _reason = r.Reason;
            }
        }
    }

    async Task AcceptLoop(NamedPipeServerStream server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await server.WaitForConnectionAsync(ct).ConfigureAwait(false); }
            catch (Exception e)
            {
                server.Dispose();
                if (ct.IsCancellationRequested) return;   // stopped: not a wake at rest
                Woke("the connect wait failed: " + e.Message);
                lock (_gate) { _listening = false; _reason = $"the pipe {_pipe} stopped listening ({e.Message}); trying again every {_retryMs / 1000.0:0.###} s"; Say("the pipe stopped listening: " + _reason); }
                _ = RetryLoop(ct);
                return;
            }
            Woke("a client connected");
            var mine = server;
            NamedPipeServerStream next;
            try { next = NewServer(false); }
            catch (Exception e)
            {
                lock (_gate) { _listening = false; _reason = $"the pipe {_pipe} could not take another client ({e.Message}); trying again every {_retryMs / 1000.0:0.###} s"; Say(_reason); }
                _ = Serve(mine, ct);
                _ = RetryLoop(ct);
                return;
            }
            _ = Serve(mine, ct);
            server = next;
        }
        server.Dispose();
    }

    async Task Serve(NamedPipeServerStream s, CancellationToken ct)
    {
        var st = Connect(() => { try { s.Dispose(); } catch { /* gone */ } });
        if (st == null) { s.Dispose(); return; }
        var buf = new byte[4096];
        try
        {
            for (; ; )
            {
                int n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
                Woke($"a read ended ({n} bytes)");
                if (n <= 0) break;
                Feed(st, buf.AsSpan(0, n));
            }
        }
        catch (Exception) { /* a client vanishing is normal */ }
        finally { Disconnect(st); try { s.Dispose(); } catch { /* gone */ } }
    }

    public void Stop()
    {
        Conn[] conns;
        lock (_gate)
        {
            _stopped = true;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var hw in _hints.Keys.ToArray()) Drop(hw, "Mantle stopped listening");
            conns = _conns.ToArray();
            foreach (var c in conns) c.Closed = true;
            _conns.Clear(); _seqs.Clear(); _checks.Clear();
            _listening = false; _reason = "stopped"; _startT = null;
        }
        foreach (var c in conns) { try { c.Shut?.Invoke(); } catch { /* gone */ } }
    }
    public void Dispose() => Stop();

    public JsonObject View()
    {
        lock (_gate)
        {
            int q = _hints.Values.Count(h => h.App == "quartz"), hl = _hints.Values.Count(h => h.App == "helm");
            var ign = new JsonObject(); foreach (var kv in _ignored) ign[kv.Key] = kv.Value;
            var apps = new JsonObject();
            foreach (var kv in _apps) apps[kv.Key] = new JsonObject { ["mismatch"] = kv.Value.Mismatch, ["since"] = kv.Value.Since };
            return new JsonObject
            {
                ["listening"] = _listening, ["wakes"] = Wakes, ["reason"] = _reason, ["pipe"] = _pipe, ["clients"] = _conns.Count,
                ["hints"] = new JsonObject { ["quartz"] = q, ["helm"] = hl }, ["ignored"] = ign, ["apps"] = apps,
            };
        }
    }

    /// The parent forgets a window (destroyed, say). Does NOT forward: the parent asked.
    public void Forget(string hwnd) { lock (_gate) { _hints.Remove(hwnd); foreach (var a in Apps) _checks.Remove(a + "|" + hwnd); } }

    /// Capture disagreed with the hint. Logged once per app per reason.
    public void Mismatch(string app, string hwnd, string? why)
    {
        lock (_gate)
        {
            if (!_apps.TryGetValue(app, out var a)) return;
            string w = string.IsNullOrEmpty(why) ? "capture disagreed with the hint" : why;
            if (a.Mismatch != w) _apps[app] = (w, _now());
            if (_mismatchLogged.Add(app + "|" + w)) Say($"{app}'s hint for window {hwnd} disagreed with the capture, so the capture wins: {w} (logged once)");
        }
    }
    public void Confirmed(string app) { lock (_gate) { if (_apps.ContainsKey(app)) _apps[app] = (null, null); } }

    /// Every kept hint again (a new sidecar, a look change in dark).
    public void ResendAll() { lock (_gate) foreach (var kv in _hints.ToArray()) Fwd(kv.Key, kv.Value.App, kv.Value.Top, kv.Value.Meta); }
}
