using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features;

// lib/features/_idle-unload.js: a hidden window that is RARELY used goes after 10 min unused and is made
// again on its next use, through the feature's own create function (the same path as a wake after a
// game). The always-warm set (switcher, OSD, launcher, Task view, snap, screenshot overlays, clipboard)
// never uses this: each answers a key with no warning and would open too slowly cold.

/// <summary>What one idle watch needs (lib/features/_idle-unload.js watchIdle's o).</summary>
/// <param name="What">The window's name in the log ("screenshot card").</param>
/// <param name="Unload">Destroy the window; true when there was one to destroy.</param>
/// <param name="Busy">True while it is in use (then the wait starts again); a throw counts as busy.</param>
public sealed record IdleTarget(string What, Func<string, bool> Unload, Func<bool>? Busy = null);

/// <summary>One idle watch: <see cref="Touch"/> after every use restarts the wait.</summary>
public sealed class IdleWatch
{
    readonly IdleUnloader _owner;
    readonly IdleTarget _t;
    IDisposable? _timer;
    bool _stopped;

    internal IdleWatch(IdleUnloader owner, IdleTarget t) { _owner = owner; _t = t; }

    public string What => _t.What;

    public void Touch()
    {
        if (_stopped) return;   // a late use after Stop must not arm a timer for a stopped feature
        _timer?.Dispose();
        _timer = _owner.Clock.Later(_owner.Wait, Fire);
    }

    public void Stop()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;
        _owner.Forget(this);
    }

    bool IsBusy()
    {
        if (_t.Busy == null) return false;
        try { return _t.Busy(); }
        catch (Exception e) { _owner.Say($"{_t.What}: could not tell whether it is in use ({e.Message}); kept"); return true; }
    }

    /// <summary>Unload now unless it is busy or stopped; true when a window went.</summary>
    public bool UnloadNow(string why)
    {
        if (_stopped || IsBusy()) return false;
        _timer?.Dispose();
        _timer = null;
        bool went;
        try { went = _t.Unload(why); }
        catch (Exception e)
        {
            _owner.Say($"{_t.What}: could not be unloaded ({e.Message}); it stays, and is tried again after the next wait");
            Touch();
            return false;
        }
        if (!went) return false;
        _owner.Say($"{_t.What}: unloaded ({why}); it is made again on its next use");
        return true;
    }

    void Fire()
    {
        _timer = null;
        if (_stopped) return;
        if (IsBusy()) { Touch(); return; }
        var ms = (long)_owner.Wait.TotalMilliseconds;
        UnloadNow($"unused for {(ms >= 60000 ? $"{Math.Round(ms / 60000.0)} min" : $"{ms} ms")}");
    }
}

/// <summary>Every idle watch of the run, and the wait (10 min; a --test run may shorten it).</summary>
public sealed class IdleUnloader
{
    /// lib/features/_idle-unload.js IDLE_UNLOAD_MS.
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(10);

    readonly List<IdleWatch> _watches = new();
    readonly Action<string> _log;

    /// <param name="testWait">MANTLE_IDLE_UNLOAD_MS: honoured only in a --test run.</param>
    public IdleUnloader(HostClock clock, Action<string>? log, bool test, TimeSpan? testWait = null)
    {
        Clock = clock;
        _log = log ?? (_ => { });
        Wait = WaitFor(test, testWait);
    }

    /// The wait: the override only in --test, and only when it is above zero.
    public static TimeSpan WaitFor(bool test, TimeSpan? testWait) => test && testWait is TimeSpan t && t > TimeSpan.Zero ? t : DefaultWait;

    internal HostClock Clock { get; }
    public TimeSpan Wait { get; }

    internal void Say(string line) { try { _log(line); } catch { /* a log that throws must not break the watch */ } }
    internal void Forget(IdleWatch w) => _watches.Remove(w);

    public IdleWatch Watch(IdleTarget t)
    {
        var w = new IdleWatch(this, t ?? throw new ArgumentNullException(nameof(t)));
        _watches.Add(w);
        return w;
    }

    /// <summary>Every idle-unloadable window now (the --test-unload hook): the names that went.</summary>
    public IReadOnlyList<string> UnloadAll(string why) => _watches.ToArray().Where(w => w.UnloadNow(why)).Select(w => w.What).ToArray();
}
