using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features.Corners;
using Xunit;

namespace Mantle.Core.Tests.Features.Corners;

// corners.test.js "the actions (fake effects)" and lib/corner.js's registry (the bottom-right occupants).
public class CornerActionsTests
{
    sealed class Fx : ICornerEffects
    {
        public readonly List<string> Calls = new();
        public Func<string, bool, CornerFx>? ChordFn;
        public Func<bool, Task<CornerFx>>? DesktopFn, QsFn;
        public Func<bool, CornerFx>? LauncherFn, LockFn;
        public CornerFx Chord(string chord, bool dry) { Calls.Add($"chord {chord} {dry}"); return ChordFn?.Invoke(chord, dry) ?? (dry ? new CornerFx(true, DryRun: true, Feature: "taskview") : new CornerFx(true, Feature: "taskview")); }
        public Task<CornerFx> DesktopAsync(bool dry) { Calls.Add($"desktop {dry}"); return DesktopFn?.Invoke(dry) ?? Task.FromResult(dry ? new CornerFx(true, DryRun: true, WouldCall: "Shell.Application ToggleDesktop (IShellDispatch4)") : new CornerFx(true, Called: "Shell.Application ToggleDesktop (IShellDispatch4)")); }
        public CornerFx Launcher(bool dry) { Calls.Add($"launcher {dry}"); return LauncherFn?.Invoke(dry) ?? new CornerFx(true, DryRun: dry); }
        public Task<CornerFx> QsAsync(bool dry) { Calls.Add($"qs {dry}"); return QsFn?.Invoke(dry) ?? Task.FromResult(new CornerFx(true, DryRun: dry)); }
        public CornerFx Lock(bool dry) { Calls.Add($"lock {dry}"); return LockFn?.Invoke(dry) ?? (dry ? new CornerFx(true, DryRun: true, WouldCall: "user32 LockWorkStation") : new CornerFx(true, Called: "LockWorkStation")); }
    }

    static readonly string[] Five = { "taskview", "desktop", "launcher", "qs", "lock" };

    [Fact]
    public async Task Live_each_runs_its_effect_once_and_says_what_it_did()
    {
        var fx = new Fx();
        var live = new Dictionary<string, CornerOutcome>();
        foreach (var a in Five) live[a] = await CornerActions.RunAsync(a, fx, test: false);
        Assert.All(live.Values, o => Assert.True(o.Ok && !o.DryRun));
        Assert.Contains("opened Task view (taskview's Win+Tab)", live["taskview"].Said);
        Assert.Contains("toggled Show desktop", live["desktop"].Said);
        Assert.Equal("opened the launcher", live["launcher"].Said);
        Assert.Equal("opened quick settings", live["qs"].Said);
        Assert.Equal("locked the screen", live["lock"].Said);
        Assert.Equal(5, fx.Calls.Count);
        Assert.All(fx.Calls, c => Assert.EndsWith("False", c));
        Assert.Equal("chord Win+Tab False", fx.Calls[0]);   // Task view goes through Win+Tab's own feature, no key sent
    }

    [Fact]
    public async Task Test_every_action_is_a_dry_run_and_says_so()
    {
        var fx = new Fx();
        var dry = new Dictionary<string, CornerOutcome>();
        foreach (var a in Five) dry[a] = await CornerActions.RunAsync(a, fx, test: true);
        Assert.All(dry.Values, o => { Assert.True(o.Ok); Assert.True(o.DryRun); Assert.StartsWith("DRY RUN (--test)", o.Said); });
        Assert.All(fx.Calls, c => Assert.EndsWith("True", c));
        Assert.Contains("ToggleDesktop", dry["desktop"].Said);
        Assert.Contains("LockWorkStation", dry["lock"].Said);
    }

    [Fact]
    public async Task Failures_are_words_never_throws()
    {
        Assert.Equal("closed quick settings (it was open)", (await CornerActions.RunAsync("qs", new Fx { QsFn = _ => Task.FromResult(new CornerFx(true, Closed: true)) }, false)).Said);
        Assert.Equal("Task view did not open: Task View is switched off in the tray",
            (await CornerActions.RunAsync("taskview", new Fx { ChordFn = (_, _) => new CornerFx(false, "Task View is switched off in the tray") }, false)).Said);
        var tvDry = await CornerActions.RunAsync("taskview", new Fx { ChordFn = (_, d) => new CornerFx(false, "no feature takes Win+Tab", d) }, true);
        Assert.False(tvDry.Ok); Assert.True(tvDry.DryRun); Assert.Equal("Task view could not open: no feature takes Win+Tab", tvDry.Said);
        Assert.Equal("Show desktop did not run: refused: sidecar is passive (test mode)",
            (await CornerActions.RunAsync("desktop", new Fx { DesktopFn = _ => Task.FromResult(new CornerFx(false, "refused: sidecar is passive (test mode)")) }, false)).Said);
        Assert.Equal("Launcher failed: there is no launcher window to open",
            (await CornerActions.RunAsync("launcher", new Fx { LauncherFn = _ => throw new InvalidOperationException("there is no launcher window to open") }, false)).Said);
        Assert.Equal("Quick settings did not open: the bar did not answer within 1500 ms",
            (await CornerActions.RunAsync("qs", new Fx { QsFn = _ => Task.FromResult(new CornerFx(false, "the bar did not answer within 1500 ms")) }, false)).Said);
        Assert.Equal("the screen did not lock: koffi could not bind LockWorkStation (x)",
            (await CornerActions.RunAsync("lock", new Fx { LockFn = _ => new CornerFx(false, "koffi could not bind LockWorkStation (x)") }, false)).Said);
        Assert.Equal("Show desktop failed: sidecar gone",
            (await CornerActions.RunAsync("desktop", new Fx { DesktopFn = _ => throw new InvalidOperationException("sidecar gone") }, false)).Said);
        var unknown = await CornerActions.RunAsync("explode", new Fx(), false);
        Assert.False(unknown.Ok); Assert.Equal("\"explode\" is not a corner action", unknown.Said);
    }

    [Fact]
    public void Occupants_registry_changes_only_on_real_changes()
    {
        var reg = new CornerOccupants();
        var heard = new List<string>();
        var off = reg.OnChange(heard.Add);
        reg.OnChange(_ => throw new InvalidOperationException("a bad listener"));
        Assert.True(reg.Set("shot", (1500.4, 800.6, 300, 120)));
        Assert.Equal(new CornerOccupant("shot", 1500, 801, 300, 120, 0, 0), reg.List().Single());
        Assert.False(reg.Set("shot", (1500, 801, 300, 120)));          // the same rect again: no change
        Assert.True(reg.Set("shot", (1500, 801, 300, 120), lift: 60)); // a new lift alone is a change
        Assert.True(reg.Set("shot", (1500, 801, 300, 120), lift: 60, rest: 90));
        Assert.Equal(60, reg.List().Single().Rest);                    // rest never more than lift
        Assert.True(reg.Set("shot", (1500, 801, 300, 120), lift: null, rest: 40));
        Assert.Equal(0, reg.List().Single().Rest);                     // no lift means no rest
        Assert.True(reg.Set("shot", (0, 0, 0, 10)));                   // a zero rect clears it
        Assert.Empty(reg.List());
        Assert.False(reg.Set("shot", null));
        reg.Changed("the bar's pop-up opened");
        Assert.Equal(new[] { "shot is up", "shot is up", "shot is up", "shot is up", "shot went", "the bar's pop-up opened" }, heard);
        off.Dispose();
        reg.Changed(null);
        Assert.Equal(6, heard.Count);
    }
}
