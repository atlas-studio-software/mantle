using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Lifecycle;

/// <summary>
/// TEST RUNS ONLY: what woke the app while it rested (the idle probe, Surfaces/Bar/Frame/IdleProbe.cs, turns it on in a
/// --test run with MANTLE_TEST_IDLE_PROBE). Off, a hit is one volatile read. On, each hit counts one line: a timer that
/// fired (host delay, clock later) or work posted to the host thread, named by the type its delegate belongs to (an
/// async method shows as its state machine), and each sidecar line by event name or call verb.
/// </summary>
public static class IdleTally
{
    static volatile bool _on;
    static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

    public static bool On => _on;

    public static void Start() { Counts.Clear(); _on = true; }

    /// Stops counting and gives every line, the most frequent first.
    public static List<KeyValuePair<string, int>> Stop()
    {
        _on = false;
        return Counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    }

    public static void Hit(string what) { if (_on) Counts.AddOrUpdate(what, 1, static (_, n) => n + 1); }

    public static void Hit(string kind, object? work) { if (_on) Hit(kind + " " + Name(work)); }

    static string Name(object? work) => work switch {
        null => "(none)",
        Delegate d => d.Target?.GetType().FullName ?? "(a static method)",
        _ => work.GetType().FullName ?? work.GetType().Name,
    };
}
