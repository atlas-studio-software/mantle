using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features.Timers;

namespace Mantle.Core.Lifecycle;

/// <summary>
/// What the app's resume and unlock hook calls on the command bus (main.js powerMonitor 'resume' listeners), lane
/// r11-app-wiring. On resume from sleep: <c>timer.wake</c> (timer.js onWake: a timeout can be late across a sleep) and
/// <c>gcal.resume</c> (BAR-463). An unlock calls nothing here (the look is re-read by the app itself). Each call is
/// started at once and none waits for another; a call that fails is one plain log line, never silent and never thrown.
/// </summary>
public static class WakeCalls
{
    /// The words the session window's hook passes for a resume from sleep (PBT_APMRESUMEAUTOMATIC).
    public const string Resume = "the PC woke from sleep";
    /// The words it passes for a session unlock (WTS_SESSION_UNLOCK).
    public const string Unlock = "the screen was unlocked";
    /// The caller the bus sees.
    public const string Caller = "host";

    static readonly string[] OnResume = { TimerFeature.WakeCommand, "gcal.resume" };

    /// The commands a wake for <paramref name="why"/> calls, in the order they are started.
    public static IReadOnlyList<string> For(string? why) => why == Resume ? OnResume : Array.Empty<string>();

    /// <summary>Call every command <see cref="For"/> names. Returns how many answered ok.</summary>
    public static async Task<int> RunAsync(ICommandBus? bus, string? why, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var names = For(why);
        if (names.Count == 0) return 0;
        if (bus is null)
        {
            log($"mantle: {why}, but the command bus is not up yet, so {string.Join(" and ", names)} could not be called");
            return 0;
        }
        var calls = names.Select(n => (Name: n, Task: Call(bus, n))).ToArray();
        int ok = 0;
        foreach (var (name, task) in calls)
        {
            CommandResult r = await task.ConfigureAwait(true);
            if (r.Ok) ok++;
            else log($"mantle: {why}, but {name} did not run: {r.Error}");
        }
        return ok;
    }

    static async Task<CommandResult> Call(ICommandBus bus, string name)
    {
        try { return await bus.CallAsync(name, null, Caller).ConfigureAwait(true); }
        catch (Exception e) { return CommandResult.Fail(string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message); }
    }
}
