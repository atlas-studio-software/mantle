using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Lifecycle;

/// <summary>
/// Every quit road goes through ONE shutdown (main.js shutdown(why), CORE-025/026/027/028): it is
/// idempotent, stands the supervisor down first (except after a crash), runs the restore steps in
/// order, each with its own deadline, stops the platform, and exits with the code. If the whole road
/// has not finished in 12 s it leaves anyway, saying so with the recovery command. Host thread.
/// </summary>
public sealed class Shutdown
{
    public static readonly TimeSpan HardStop = TimeSpan.FromSeconds(12);

    /// One step of the road: a name for the log, its own deadline, and the work (never trusted not to throw).
    public sealed record Step(string Name, TimeSpan Deadline, Func<CancellationToken, Task> Run);

    readonly Action<string> _log;
    readonly string _recovery;
    readonly TimeSpan _hardStop;
    Task<int>? _running;

    /// The reason of the first shutdown (later ones are ignored), null until one starts.
    public string? Why { get; private set; }
    public bool Started => _running != null;
    /// The steps that ran, in order, with how they ended ("ok", "timed out", "failed: ...").
    public List<(string Step, string Outcome)> Ran { get; } = new();

    public Shutdown(Action<string> log, string recoveryCommand, TimeSpan? hardStop = null)
    {
        _log = log; _recovery = recoveryCommand; _hardStop = hardStop ?? HardStop;
    }

    /// <summary>Run the road once. A second call returns the first one's task.</summary>
    /// <param name="steps">in order: stand the supervisor down (unless crash), stop the features, restore the
    /// shell, stop the platform; the caller builds the list so the order is visible in one place</param>
    /// <returns>the exit code to use</returns>
    public Task<int> RunAsync(string why, int exitCode, IReadOnlyList<Step> steps)
    {
        if (_running != null) {
            _log($"mantle-native: already shutting down ({Why}); \"{why}\" changes nothing");
            return _running;
        }
        Why = why;
        _log($"mantle-native: shutting down: {why}");
        _running = Road(exitCode, steps);
        return _running;
    }

    async Task<int> Road(int exitCode, IReadOnlyList<Step> steps)
    {
        using var all = new CancellationTokenSource(_hardStop);
        var road = Steps(steps, all.Token);
        var first = await Task.WhenAny(road, Task.Delay(_hardStop)).ConfigureAwait(true);
        if (first != road) {
            _log($"mantle-native: the restore did not finish in {_hardStop.TotalSeconds:0}s, leaving anyway. If Windows looks wrong, run: {_recovery}");
            return exitCode;
        }
        _log($"mantle-native: shut down ({Why}); exit code {exitCode}");
        return exitCode;
    }

    async Task Steps(IReadOnlyList<Step> steps, CancellationToken all)
    {
        foreach (var s in steps) {
            if (all.IsCancellationRequested) { Ran.Add((s.Name, "skipped: out of time")); continue; }
            using var one = CancellationTokenSource.CreateLinkedTokenSource(all);
            one.CancelAfter(s.Deadline);
            string outcome;
            try {
                var work = s.Run(one.Token);
                var done = await Task.WhenAny(work, Task.Delay(s.Deadline, all)).ConfigureAwait(true);
                if (done == work) { await work.ConfigureAwait(true); outcome = "ok"; }
                else outcome = $"timed out after {s.Deadline.TotalSeconds:0.#} s";
            } catch (OperationCanceledException) {
                outcome = $"timed out after {s.Deadline.TotalSeconds:0.#} s";
            } catch (Exception e) {
                outcome = $"failed: {e.GetType().Name}: {e.Message}";
            }
            Ran.Add((s.Name, outcome));
            if (outcome != "ok") _log($"mantle-native: shutdown step \"{s.Name}\" {outcome}; going on with the next");
        }
    }
}
