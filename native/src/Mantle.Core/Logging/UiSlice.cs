using System;
using System.Diagnostics;
using System.Globalization;

namespace Mantle.Core.Logging;

/// <summary>
/// Named slices of UI-thread work, for the start-up stall meter (lane native-r8-startup; Mantle.App's
/// Shell/Diagnostics/StallMeter.cs). While the meter is on (a --test run with MANTLE_TEST_STALL=1),
/// each slice run through <see cref="Run"/> is timed and named: a slice of <see cref="SayMs"/> or more is logged as
/// "ui slice: &lt;name&gt; held the UI thread &lt;ms&gt; ms", and the meter's thread reads <see cref="Now"/> while the
/// UI thread is held, so a stall names the work that held it. While the meter is off (every other run, and every run
/// after its first 10 s) a slice is one volatile read and the call: no clock, no string, no log.
/// </summary>
public static class UiSlice
{
    /// Slices this long or longer are logged (ms): a frame and a half at 30 Hz.
    public const double SayMs = 50;

    static volatile Action<string>? _log;
    static volatile string? _now;

    /// True while the meter is on.
    public static bool On => _log != null;

    /// The innermost named slice the UI thread is in now, or null (any thread; read by the meter while the UI thread is held).
    public static string? Now => _now;

    /// The meter is on: slices are timed and logged through <paramref name="log"/>.
    public static void Begin(Action<string> log) => _log = log;

    /// The meter is off: slices are plain calls again.
    public static void End() { _log = null; _now = null; }

    /// Run <paramref name="work"/> as the slice "<paramref name="what"/> (<paramref name="who"/>)" (UI thread).
    public static void Run(string what, string? who, Action work)
    {
        var log = _log;
        if (log == null) { work(); return; }
        string name = who == null ? what : what + " (" + who + ")";
        string? outer = _now;
        _now = name;
        long t0 = Stopwatch.GetTimestamp();
        try { work(); }
        finally {
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _now = outer;
            if (ms >= SayMs) log("ui slice: " + name + " held the UI thread " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms");
        }
    }

    /// <see cref="Run(string, string?, Action)"/> with one argument, so a per-delivery slice needs no closure while the meter is off.
    public static void Run<T>(string what, string? who, Action<T> work, T arg)
    {
        if (_log == null) { work(arg); return; }
        Run(what, who, () => work(arg));
    }
}
