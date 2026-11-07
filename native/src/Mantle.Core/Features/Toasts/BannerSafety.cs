using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Toasts;

// Port of lib/features/_toasts-safety.js: WHEN THE NOTIFICATION HELPER (the sidecar) IS DOWN, WINDOWS' OWN
// BANNERS STILL COME BACK. The sidecar puts originals back on any exit of its own and main re-applies on the
// next start (a moment of Windows' banner at most). If it stays down past the grace (8 s; a normal restart is
// ~3 s), or gave up, tools/restore-banners.ps1 (hidden PowerShell, no Mantle) puts them back. Main leaving
// while the helper is down runs it synchronously to its end (15 s cap), unless a crash supervisor watches main.

/// <summary>A clock and timers for the toasts code (the host's, or a fake in tests).</summary>
public interface IToastClock
{
    double NowMs { get; }
    IDisposable Later(int ms, Action action);
}

/// <summary>The host clock as an <see cref="IToastClock"/>: epoch ms, timers posted to the host thread.</summary>
public sealed class HostToastClock : IToastClock
{
    readonly HostClock _c;
    public HostToastClock(HostClock clock) { _c = clock; }
    public double NowMs => _c.Time.GetUtcNow().ToUnixTimeMilliseconds();
    public IDisposable Later(int ms, Action action) => _c.Later(TimeSpan.FromMilliseconds(Math.Max(0, ms)), action);
}

public readonly record struct ScriptOutput(int? Code, string Out, string? Error = null);

public sealed record SafetyRun(double At, string How, bool Ok, int Back, int Left, string? Why, bool Nothing = false, bool Sync = false);
public sealed record SafetyDown(string Why, double At, bool Starting);
public sealed record SafetyState(SafetyDown? Down, bool Pending, bool Running, SafetyRun? Last, int GraceMs);

public sealed class BannerSafety
{
    public const int GraceMs = 8000;

    static readonly Regex Counts = new(@"(\d+) put back to Windows' own pop-up, (\d+) left");
    static readonly Regex Verified = new(@"^verified\r?$", RegexOptions.Multiline);
    static readonly Regex Nothing = new("nothing to put back|owns no app|the record is empty");
    static readonly Regex Problem = new(@"^PROBLEM|^NOT VERIFIED");
    static readonly Regex ProblemHead = new(@"^(PROBLEM|NOT VERIFIED)\s*-?\s*");

    /// readOut: what the script's output says (its exit code, its "N put back ..., M left", "verified").
    public static (bool Ok, int Back, int Left, string? Why) ReadOut(int? code, string? output)
    {
        var text = output ?? "";
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var m = Counts.Match(text);
        int back = m.Success ? int.Parse(m.Groups[1].Value) : 0, left = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        if (code == 0 && (Verified.IsMatch(text) || Nothing.IsMatch(text))) return (true, back, left, null);
        var problem = lines.FirstOrDefault(l => Problem.IsMatch(l)) ?? lines.LastOrDefault();
        var why = problem != null ? Cut(ProblemHead.Replace(problem, ""), 300) : $"the put-back script ended with code {(code?.ToString() ?? "null")}";
        return (false, back, left, why);
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
    static string ErrWords(string? e) => Cut((e ?? "").Split('\n')[0], 200);

    readonly Func<int> _owned;
    readonly Func<Task<ScriptOutput>> _run;
    readonly Func<ScriptOutput> _sync;
    readonly Action<string> _log;
    readonly Action? _onChange;
    readonly IToastClock _clock;
    readonly int _grace;
    SafetyDown? _down;
    IDisposable? _timer;
    Task<SafetyRun>? _running;
    SafetyRun? _last;
    bool _disposed;

    public BannerSafety(Func<int> owned, Func<Task<ScriptOutput>> run, Func<ScriptOutput> sync, IToastClock clock,
        Action<string>? log = null, Action? onChange = null, int? graceMs = null)
    {
        _owned = owned; _run = run; _sync = sync; _clock = clock;
        _log = log ?? (_ => { }); _onChange = onChange;
        _grace = graceMs is int g && g >= 0 ? g : GraceMs;
    }

    void Changed() { try { _onChange?.Invoke(); } catch { /* a status repaint only */ } }

    Task<SafetyRun> Run(string how)
    {
        if (_running != null) return _running;
        if (!(_owned() > 0))
        {
            _last = new SafetyRun(_clock.NowMs, how, true, 0, 0, null, Nothing: true);
            Changed();
            return Task.FromResult(_last);
        }
        _log($"Windows' own pop-ups are being put back ({how})");
        _running = RunCore(how);
        return _running;
    }

    async Task<SafetyRun> RunCore(string how)
    {
        (bool Ok, int Back, int Left, string? Why) r;
        try { var x = await _run(); r = ReadOut(x.Code, x.Out); }
        catch (Exception ex) { r = (false, 0, 0, $"the put-back script would not run ({ErrWords(ex.Message)})"); }
        _running = null;
        _last = new SafetyRun(_clock.NowMs, how, r.Ok, r.Back, r.Left, r.Why);
        _log(r.Ok ? $"Windows' own pop-ups are back ({how}): {r.Back} put back, {r.Left} left as they were"
            : $"Windows' own pop-ups could NOT all be put back ({how}): {r.Why}");
        Changed();
        return _last;
    }

    /// The helper stopped: wait the grace, then put back (unless it comes up, or a put-back since covers it).
    public void Down(string? why, bool starting = false)
    {
        if (_disposed) return;
        if (_down == null) _down = new SafetyDown(why ?? "it stopped", _clock.NowMs, starting);
        else _down = _down with { Why = why ?? _down.Why, Starting = starting && _down.Starting };
        if (_timer != null || _running != null || (_last != null && _last.At >= _down.At)) { Changed(); return; }
        _timer = _clock.Later(_grace, () => { _timer = null; if (_down != null && !_disposed) _ = Run("Mantle's notification helper stopped"); });
        Changed();
    }

    /// The helper gave up restarting: put back at once (a good put-back since it went down is enough).
    public Task<SafetyRun?> GaveUp(string? why)
    {
        if (_disposed) return Task.FromResult<SafetyRun?>(null);
        _down ??= new SafetyDown(why ?? "it stopped", _clock.NowMs, false);
        _timer?.Dispose(); _timer = null;
        if (_last != null && _last.At >= _down.At && _last.Ok) return Task.FromResult<SafetyRun?>(_last);
        return Run("Mantle's notification helper stopped and is not coming back on its own").ContinueWith(t => (SafetyRun?)t.Result,
            TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Up()
    {
        _timer?.Dispose(); _timer = null;
        if (_down != null) { _down = null; Changed(); }
    }

    public Task Idle() => _running ?? Task.CompletedTask;

    /// Main leaving: run the script synchronously to its end when the helper is down and something is owned.
    public bool Leave(string how)
    {
        _timer?.Dispose(); _timer = null;
        if (!(_owned() > 0)) return false;
        if (_running != null) _log($"a put-back was still running as Mantle left ({how}); running it once more to its end");
        else if (_last != null && _down != null && _last.At >= _down.At && _last.Ok) return false;   // already back
        (bool Ok, int Back, int Left, string? Why) r;
        try
        {
            var x = _sync();
            r = x.Error != null ? (false, 0, 0, $"the put-back script would not run ({ErrWords(x.Error)})") : ReadOut(x.Code, x.Out);
        }
        catch (Exception ex) { r = (false, 0, 0, $"the put-back script would not run ({ErrWords(ex.Message)})"); }
        _last = new SafetyRun(_clock.NowMs, how, r.Ok, r.Back, r.Left, r.Why, Sync: true);
        _log(r.Ok ? $"Windows' own pop-ups are back as Mantle leaves ({how}): {r.Back} put back, {r.Left} left as they were"
            : $"Windows' own pop-ups could NOT all be put back as Mantle leaves ({how}): {r.Why}");
        return r.Ok;
    }

    public void Dispose() { _disposed = true; _timer?.Dispose(); _timer = null; }

    public SafetyState State() => new(_down, _timer != null, _running != null, _last, _grace);
}

/// <summary>scriptRunner: tools/restore-banners.ps1 in a hidden PowerShell, async (60 s cap) or to its end (sync).</summary>
public sealed class RestoreBannersScript
{
    readonly string _exe, _script, _record;
    readonly string? _settingsKey;
    readonly bool _test;
    readonly int _timeoutMs, _syncTimeoutMs;

    public RestoreBannersScript(string script, string recordFile, bool test, string? settingsKey = null, string exe = "powershell.exe",
        int timeoutMs = 60000, int syncTimeoutMs = 15000)
    {
        _script = script; _record = recordFile; _test = test; _settingsKey = settingsKey; _exe = exe;
        _timeoutMs = timeoutMs; _syncTimeoutMs = syncTimeoutMs;
    }

    public IReadOnlyList<string> Args()
    {
        var a = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", _script, "-RecordFile", _record };
        if (!string.IsNullOrEmpty(_settingsKey)) { a.Add("-SettingsKey"); a.Add(_settingsKey!); }
        if (_test) a.Add("-TestOnly");
        return a;
    }

    ProcessStartInfo Info()
    {
        var psi = new ProcessStartInfo(_exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in Args()) psi.ArgumentList.Add(a);
        return psi;
    }

    public async Task<ScriptOutput> RunAsync()
    {
        using var p = Process.Start(Info()) ?? throw new InvalidOperationException("PowerShell would not start");
        var outT = p.StandardOutput.ReadToEndAsync();
        var errT = p.StandardError.ReadToEndAsync();
        var exit = p.WaitForExitAsync();
        if (await Task.WhenAny(exit, Task.Delay(_timeoutMs)) != exit)
        {
            try { p.Kill(false); } catch { /* gone */ }
            throw new TimeoutException($"the put-back script did not finish in {_timeoutMs / 1000} s");
        }
        return new ScriptOutput(p.ExitCode, await outT + await errT);
    }

    public ScriptOutput RunSync()
    {
        try
        {
            using var p = Process.Start(Info());
            if (p == null) return new ScriptOutput(null, "", "PowerShell would not start");
            var outT = p.StandardOutput.ReadToEndAsync();
            var errT = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(_syncTimeoutMs))
            {
                try { p.Kill(false); } catch { /* gone */ }
                return new ScriptOutput(null, "", $"the put-back script did not finish in {_syncTimeoutMs / 1000} s");
            }
            p.WaitForExit();
            return new ScriptOutput(p.ExitCode, outT.Result + errT.Result);
        }
        catch (Exception ex) { return new ScriptOutput(null, "", ex.Message); }
    }
}
