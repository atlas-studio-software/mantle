using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Mantle.Core.Logging;

/// <summary>
/// Startup marks: named points on the way to the first frame, each logged as
///   "mark &lt;name&gt; +&lt;ms&gt; ms after process start (+&lt;ms&gt; after Main)"
/// and kept for ready.json. Process start comes from the OS (Process.StartTime), so the time the
/// loader and the runtime took before Main is in the number too.
/// </summary>
public sealed class StartupMarks
{
    readonly ILogSink _log;
    readonly long _mainTicks = Stopwatch.GetTimestamp();
    readonly List<Mark> _marks = new();
    readonly object _gate = new();

    public readonly record struct Mark(string Name, long UnixMs, double SinceStartMs, double SinceMainMs);

    /// Unix ms of the process start, as the OS recorded it.
    public long ProcessStartUnixMs { get; }
    /// Unix ms when this object was made (first thing in Main).
    public long MainUnixMs { get; }

    public StartupMarks(ILogSink log, DateTimeOffset? processStart = null)
    {
        _log = log;
        MainUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long ps;
        try { ps = new DateTimeOffset((processStart ?? new DateTimeOffset(Process.GetCurrentProcess().StartTime)).UtcDateTime).ToUnixTimeMilliseconds(); }
        catch (Exception) { ps = MainUnixMs; }
        ProcessStartUnixMs = ps;
    }

    public Mark Add(string name)
    {
        double sinceMain = Stopwatch.GetElapsedTime(_mainTicks).TotalMilliseconds;
        long now = MainUnixMs + (long)Math.Round(sinceMain);
        var m = new Mark(name, now, now - ProcessStartUnixMs, sinceMain);
        lock (_gate) _marks.Add(m);
        _log.Line($"mark {name} +{m.SinceStartMs.ToString("0", CultureInfo.InvariantCulture)} ms after process start (+{sinceMain.ToString("0", CultureInfo.InvariantCulture)} after Main)");
        return m;
    }

    public IReadOnlyList<Mark> All { get { lock (_gate) return _marks.ToArray(); } }
}
