using System;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Desktop;

/// <summary>One try's answer: null = the try did not run (the layer was destroyed, Windows is put back, or a test run
/// made it without attaching), else whether it went under the icons and why not.</summary>
public sealed record RemakeTry(bool Ok, string? Error = null);

/// <summary>
/// The desktop layer's way back under the icons after Explorer took it (main.js scheduleDesktopAttach / desktopAttachNext,
/// DESK-011/012): try n waits <see cref="DesktopLayer.AttachBackoffMs"/>[n] (1.5, 3, 5, 8, 12, 20 s), a failed try
/// schedules the next, and the sixth failure gives up (the surface closes the layer, never shows it over the apps).
/// Explorer coming back runs a waiting try at once (<see cref="TryNow"/>). The clock is the caller's, so a test moves it
/// by hand. Host thread only.
/// </summary>
public sealed class RemakeLadder
{
    readonly HostClock _clock;
    readonly Func<int, bool, string?, Task<RemakeTry?>> _attempt;
    IDisposable? _timer;
    (int N, bool Make, string? When)? _pending;

    /// <param name="attempt">(n, make, when): make the window when asked, put it under the icons, answer.</param>
    public RemakeLadder(HostClock clock, Func<int, bool, string?, Task<RemakeTry?>> attempt) { _clock = clock; _attempt = attempt; }

    /// (n, when): try n went under the icons.
    public event Action<int, string?>? Done;
    /// (tries, why, when): out of tries.
    public event Action<int, string?, string?>? GaveUp;

    /// A try is waiting for its back-off.
    public bool Pending => _pending != null;

    /// Try n after its back-off (a newer schedule replaces a waiting one).
    public void Schedule(int n, bool make, string? when)
    {
        _timer?.Dispose();
        _pending = (n, make, when);
        int ms = DesktopLayer.AttachBackoffMs[Math.Min(n, DesktopLayer.AttachBackoffMs.Count - 1)];
        _timer = _clock.Later(TimeSpan.FromMilliseconds(ms), () => {
            _timer = null;
            if (_pending is not { } p) return;
            _pending = null;
            _ = Step(p.N, p.Make, p.When);
        });
    }

    /// Explorer is back: the waiting try goes now. False when none was waiting.
    public bool TryNow()
    {
        if (_pending is not { } p) return false;
        _timer?.Dispose(); _timer = null; _pending = null;
        _ = Step(p.N, p.Make, p.When);
        return true;
    }

    /// The layer is gone for good (destroyed, put back): nothing more is tried.
    public void Cancel() { _timer?.Dispose(); _timer = null; _pending = null; }

    /// Try n now (the first try at start has no back-off), then the ladder as usual.
    public Task RunAsync(int n, bool make, string? when)
    {
        Cancel();
        return Step(n, make, when);
    }

    Task Step(int n, bool make, string? when) => StepAsync(n, make, when);

    async Task StepAsync(int n, bool make, string? when)
    {
        var r = await _attempt(n, make, when).ConfigureAwait(true);
        if (r == null) return;
        switch (DesktopLayer.AttachNext(r.Ok, n)) {
            case "done": Done?.Invoke(n, when); return;
            case "retry": Schedule(n + 1, false, when); return;
            default: GaveUp?.Invoke(n + 1, r.Error, when); return;
        }
    }
}
