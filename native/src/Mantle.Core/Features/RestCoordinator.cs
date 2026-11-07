using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Features;

/// <summary>The answer to a rest or a wake (main.js restNow / wakeNow), never thrown.</summary>
public sealed record RestAnswer(bool Ok, string? Reason = null, bool Same = false, string? Why = null, string? Text = null,
    IReadOnlyList<string>? Closed = null, IReadOnlyList<string>? Back = null);

/// <summary>ctx.host.rest(on, why): game mode asks Mantle to rest while a game runs, and to wake after.</summary>
public interface IRestHost
{
    Task<RestAnswer> RestAsync(bool on, string why);
    /// The reason Mantle rests for now, or null.
    string? RestingWhy { get; }
}

/// <summary>What a feature reaches of the host's shared services (a new interface: IFeatureContext is frozen).
/// Every context the host makes implements it: <c>if (ctx is IFeatureHostServices s) s.GameWatch.Drive(...)</c>.</summary>
public interface IFeatureHostServices
{
    /// The one game watch (lib/features/_game-watch.js).
    GameWatch GameWatch { get; }
    /// host.rest(on, why). Never throws; answers in words when there is no rest in this build.
    Task<RestAnswer> RestAsync(bool on, string why);
}

/// <summary>The parts of a rest that belong to other lanes' windows (main.js restNow / wakeNow).</summary>
public interface IRestParts
{
    /// Mantle's open windows by name (bar, launcher, desktop, switcher, "<area> <page>").
    IReadOnlyList<string> Pages();
    /// Mantle's own memory now, in MB (private, working set).
    (long Private, long WorkingSet) Memory();
    bool LauncherAlive { get; }
    /// Hide the launcher at once (no fold, no keyboard hand-back) and destroy it.
    void DestroyLauncher();
    /// Make the launcher again (hidden).
    void MakeLauncher();
    /// The switcher overlay rests (why) or comes back (null; true when it was made again).
    bool SwitcherRest(string? why);
    /// The wallpaper rests (why; true when one was showing) or resumes (null).
    bool WallpaperRest(string? why);
    /// Mantle's own process ids, never the sidecar's (for gamemode.trim).
    IReadOnlyList<int> OwnPids();
    /// After every rest or wake: the hover poll stopped for a game starts again if nothing holds it.
    void AfterRest();
}

/// <summary>Who rests, and how long the steps may take.</summary>
public sealed class RestOptions
{
    public required IPlatform Platform { get; init; }
    public required IEvents Events { get; init; }
    public required IShellStateHub State { get; init; }
    public required HostClock Clock { get; init; }
    public FeatureHost? Features { get; init; }
    public IRestParts? Parts { get; init; }
    public bool Test { get; init; }
    /// MANTLE_NO_LAUNCHER=1: no launcher is made again on a wake.
    public bool NoLauncher { get; init; }
    /// Mantle is shutting down.
    public Func<bool> Closing { get; init; } = () => false;
    public Action<string>? Log { get; init; }
    /// A destroyed window's process takes a moment to exit; the numbers are read after it has.
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan TrimAfter { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TrimTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan HotkeyTimeout { get; init; } = TimeSpan.FromSeconds(4);
}

/// <summary>
/// main.js restMantle / restNow / wakeNow / trimOwn: Mantle at rest for a game. A rest closes what nothing
/// needs in front of a game (every rest:true feature, the launcher, so the Win key is Windows' Start, the
/// switcher overlay, the wallpaper), tells the pages ("mantle.rest"), and hands the idle pages of what is kept
/// back to Windows; a wake brings all of it back and trims again 60 s later. Rests and wakes are serialised:
/// one runs at a time, in the order asked. A rest with a new reason only changes the words. Host thread only.
/// </summary>
public sealed class RestCoordinator : IRestHost
{
    public sealed record RestState(string Why, long Since, IReadOnlyList<string> Closed, string? Text, bool Wallpaper);

    readonly RestOptions _o;
    readonly Action<string> _log;
    Task<RestAnswer> _chain = Task.FromResult(new RestAnswer(true));
    RestState? _rest;
    IDisposable? _trim;

    public RestCoordinator(RestOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        _log = o.Log ?? (_ => { });
        if (o.Features != null) o.Features.RestHost = this;
    }

    public RestState? Resting => _rest;
    public string? RestingWhy => _rest?.Why;
    /// A trim is waiting on its timer.
    public bool TrimPending => _trim != null;

    /// <summary>Rest (on) or wake. Serialised behind the last one asked; never faults.</summary>
    public Task<RestAnswer> RestAsync(bool on, string why)
    {
        var prev = _chain;
        _chain = Next(prev, on, why ?? "no reason given");
        return _chain;
    }

    async Task<RestAnswer> Next(Task<RestAnswer> prev, bool on, string why)
    {
        try { await prev; } catch { /* the last one answered for itself */ }
        RestAnswer a;
        try { a = on ? await RestNow(why) : await WakeNow(why); }
        catch (Exception e) { a = new RestAnswer(false, e.Message); }
        try { _o.Parts?.AfterRest(); } catch (Exception e) { _log($"rest: the hover poll could not be started again ({e.Message})"); }
        return a;
    }

    string? Blocked() => _o.Platform.Status.Ready ? null : _o.Platform.Status.Reason;

    static string Mb(long n) => n >= 1024 ? $"{n / 1024.0:0.0} GB" : $"{n} MB";

    (long Private, long WorkingSet) Mem()
    {
        try { return _o.Parts?.Memory() ?? (0, 0); } catch { return (0, 0); }
    }

    IReadOnlyList<string> Pages()
    {
        try { return _o.Parts?.Pages() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    async Task<RestAnswer> RestNow(string why)
    {
        if (_o.Closing()) return new RestAnswer(false, "Mantle is closing");
        if (_rest != null)
        {
            if (_rest.Why != why)
            {
                _rest = _rest with { Why = why };
                if (_o.Features != null) await _o.Features.RestAllAsync(why);
                Part(() => _o.Parts?.SwitcherRest(why), "the switcher");
            }
            return new RestAnswer(true, Same: true, Why: _rest.Why, Text: _rest.Text, Closed: _rest.Closed);
        }
        var t0 = _o.Clock.NowMs;
        var before = Mem();
        var pagesBefore = Pages();
        _rest = new RestState(why, t0, Array.Empty<string>(), null, false);
        var mine = _rest;
        if (_o.Features != null) await _o.Features.RestAllAsync(why);
        if (_o.Parts is { LauncherAlive: true } parts)
        {
            Part(parts.DestroyLauncher, "the launcher");
            if (!_o.Test && Blocked() == null)
                _ = _o.Platform.CallAsync("hotkey", new JsonObject { ["winTap"] = false }, _o.HotkeyTimeout).AsTask();
            _o.State.Set(s => s with { Hotkey = new HotkeyPiece(false, $"the Win key opens Windows' Start while Mantle rests ({why}); Mantle's launcher is back when it exits", true) });
        }
        Part(() => _o.Parts?.SwitcherRest(why), "the switcher");
        var wp = false;
        Part(() => wp = _o.Parts?.WallpaperRest(why) ?? false, "the wallpaper");
        _o.Events.Publish("mantle.rest", new RestSignal(true, why));   // the desktop page stops its clock, numbers and Mission Control
        await Delay(_o.Settle);
        var pagesAfter = Pages();
        var left = pagesAfter.ToList();
        var closed = pagesBefore.Where(n => { var i = left.IndexOf(n); if (i < 0) return true; left.RemoveAt(i); return false; }).ToArray();
        var after = Mem();
        string trimWords;
        if (Blocked() is { } blocked) trimWords = $"; its idle pages were NOT handed back to Windows (Mantle's link to Windows is not running: {blocked})";
        else
        {
            var r = await Trim();
            trimWords = r.Ok
                ? $"; idle pages of {r.Trimmed} Mantle process{(r.Trimmed == 1 ? "" : "es")} handed back to Windows"
                : $"; its idle pages were NOT handed back to Windows ({r.Error})";
        }
        var text = $"closed {closed.Length} window{(closed.Length == 1 ? "" : "s")} ({(closed.Length > 0 ? string.Join(", ", closed) : "none")})"
            + $"{(wp ? ", wallpaper unloaded" : "")}; Mantle's memory {Mb(before.Private)} -> {Mb(after.Private)}"
            + $" (working set {Mb(before.WorkingSet)} -> {Mb(after.WorkingSet)}){trimWords}; kept: {(pagesAfter.Count > 0 ? string.Join(", ", pagesAfter) : "no window")}, the link to Windows and the host";
        _rest = (_rest ?? mine) with { Closed = closed, Text = text, Wallpaper = wp };
        mine = _rest;
        _log($"rest: resting while {why} in {_o.Clock.NowMs - t0} ms: {text}");
        _o.Clock.Later(TimeSpan.FromSeconds(30), () =>
        {
            if (ReferenceEquals(_rest, mine)) { var m = Mem(); _log($"rest: 30 s later: Mantle's working set {Mb(m.WorkingSet)} (private {Mb(m.Private)})"); }
        });
        return new RestAnswer(true, Why: _rest.Why, Text: text, Closed: closed);
    }

    async Task<RestAnswer> WakeNow(string why)
    {
        if (_rest == null) return new RestAnswer(true, Same: true, Text: "Mantle was not resting");
        if (_o.Closing()) { _rest = null; return new RestAnswer(false, "Mantle is closing"); }
        var t0 = _o.Clock.NowMs;
        var was = _rest;
        _rest = null;
        var back = new List<string>();
        Part(() => _o.Parts?.WallpaperRest(null), "the wallpaper");
        _o.Events.Publish("mantle.rest", new RestSignal(false, why));
        if (_o.Features != null) back.AddRange(await _o.Features.WakeAllAsync(why));
        var sw = false;
        Part(() => sw = _o.Parts?.SwitcherRest(null) ?? false, "the switcher");
        if (sw) back.Add("switcher");
        if (_o.Parts is { LauncherAlive: false } parts && !_o.NoLauncher)
        {
            Part(parts.MakeLauncher, "the launcher");
            back.Add("launcher");
            if (_o.Test) _o.State.Set(s => s with { Hotkey = new HotkeyPiece(false, "test mode: the Win key hook was not installed") });
            else if (Blocked() == null)
            {
                var r = await _o.Platform.CallAsync("hotkey", new JsonObject { ["winTap"] = true }, _o.HotkeyTimeout);
                if (r.Ok)
                {
                    var installed = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("installed", out var i) && i.ValueKind == JsonValueKind.True;
                    _o.State.Set(s => s with { Hotkey = new HotkeyPiece(installed, installed ? "a tap of the Win key opens the Mantle launcher" : "the sidecar did not install the Win key hook and did not say why") });
                }
                else _o.State.Set(s => s with { Hotkey = new HotkeyPiece(false, $"the Win key still opens the Windows Start menu: {r.Error}") });
            }
        }
        var min = (long)Math.Round((_o.Clock.NowMs - was.Since) / 60000.0, MidpointRounding.AwayFromZero);
        var text = $"back after {min} min ({why}): {(back.Count > 0 ? string.Join(", ", back) : "nothing to bring back")}{(was.Wallpaper ? ", wallpaper" : "")}";
        _log($"rest: {text} in {_o.Clock.NowMs - t0} ms");
        TrimSoon($"{(long)_o.TrimAfter.TotalSeconds} s after the wake");
        return new RestAnswer(true, Text: text, Back: back);
    }

    void Part(Action a, string what)
    {
        try { a(); } catch (Exception e) { _log($"rest: {what} did not follow ({e.Message})"); }
    }

    Task Delay(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        _o.Clock.Later(t, () => tcs.TrySetResult());
        return tcs.Task;
    }

    async Task<(bool Ok, int Trimmed, string? Error)> Trim()
    {
        IReadOnlyList<int> pids;
        try { pids = _o.Parts?.OwnPids() ?? new[] { Environment.ProcessId }; } catch { pids = new[] { Environment.ProcessId }; }
        var r = await _o.Platform.CallAsync("gamemode.trim", new JsonObject { ["pids"] = new JsonArray(pids.Select(p => (JsonNode)p).ToArray()) }, _o.TrimTimeout);
        if (!r.Ok) return (false, 0, r.Error);
        var n = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("trimmed", out var t) && t.TryGetInt32(out var x) ? x : 0;
        return (true, n, null);
    }

    /// <summary>FEAT-022: hand Mantle's own idle pages back to Windows after <see cref="RestOptions.TrimAfter"/>
    /// (60 s after start, and after every wake). A newer ask replaces a waiting one.</summary>
    public void TrimSoon(string why)
    {
        _trim?.Dispose();
        _trim = _o.Clock.Later(_o.TrimAfter, () => { _trim = null; _ = TrimAsync(why); });
    }

    /// <summary>One trim now (skipped while closing or resting: a rest trims already). Never faults.</summary>
    public async Task<string> TrimAsync(string why)
    {
        if (_o.Closing() || _rest != null) return "skipped";
        if (Blocked() is { } blocked)
        {
            var w = $"trim ({why}): NOT done: Mantle's link to Windows is not running ({blocked})";
            _log(w);
            return w;
        }
        var before = Mem();
        string line;
        try
        {
            var r = await Trim();
            var after = Mem();
            line = r.Ok
                ? $"trim ({why}): idle pages of {r.Trimmed} Mantle processes handed back to Windows; working set {Mb(before.WorkingSet)} -> {Mb(after.WorkingSet)} (private {Mb(after.Private)}, unchanged by a trim)"
                : $"trim ({why}): NOT done: {r.Error}";
        }
        catch (Exception e) { line = $"trim ({why}): NOT done: {e.Message}"; }
        _log(line);
        return line;
    }
}
