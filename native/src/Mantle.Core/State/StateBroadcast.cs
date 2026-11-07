using System;
using System.Collections.Generic;
using Mantle.Core.Commands;

namespace Mantle.Core.State;

/// <summary>
/// LOG-015/016: every ShellState change goes to EVERY surface as the <c>state</c> event, with current-value
/// replay on subscribe (main.js broadcast('state') + replayAnswer 'state'), a tray rebuild request, and one
/// terminal line when the problem list changes. Host thread.
/// </summary>
public sealed class StateBroadcast : IDisposable
{
    public const string Event = "state";
    readonly List<IDisposable> _offs = new();
    string _lastProblems;

    public StateBroadcast(IShellStateHub hub, IEvents events, Action<string> log, Action? trayRequest = null)
    {
        _lastProblems = ShellStateHub.ProblemLine(ShellStateHub.Problems(hub.Current));
        _offs.Add(events.Replay(Event, () => hub.Current));
        _offs.Add(hub.OnChange(s => {
            events.Publish(Event, s);
            try { trayRequest?.Invoke(); } catch (Exception e) { log("mantle: the tray rebuild request failed: " + e.Message); }
            string line = ShellStateHub.ProblemLine(ShellStateHub.Problems(s));
            if (line != _lastProblems) { _lastProblems = line; log("mantle: " + line); }
        }));
    }

    public void Dispose() { foreach (var o in _offs) o.Dispose(); _offs.Clear(); }
}
