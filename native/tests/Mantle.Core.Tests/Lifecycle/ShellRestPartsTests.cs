using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Xunit;
using HostLoop = Mantle.Core.Tests.Features.HostLoop;

namespace Mantle.Core.Tests.Lifecycle;

// The native shell's rest (App: RestCoordinator + ShellRestParts + the surface host's "mantle.rest" listener),
// driven with the real coordinator, the real SurfaceSet and fake surfaces: what main.js restNow / wakeNow do to
// Mantle's windows (FEAT-015, FEAT-017, WIN-002).
public class ShellRestPartsTests
{
    sealed class Win : ISurfaceInstance
    {
        public readonly string Name; public readonly List<string> Did;
        public Win(string name, List<string> did) { Name = name; Did = did; }
        public bool Shown { get; private set; }
        public void Create() => Did.Add("make " + Name);
        public void Show(string why) { Shown = true; Did.Add("show " + Name); }
        public void Hide(string why) { Shown = false; Did.Add("hide " + Name); }
        public void Destroy(string why) => Did.Add("destroy " + Name);
    }

    sealed class Kit
    {
        public readonly Rig Rig;
        public readonly List<string> Did = new();
        public readonly SurfaceSet Set;
        public readonly RestCoordinator Rest;
        public readonly ShellRestParts Parts;
        public bool BarOpen = true;
        public Kit(ManualTime t, bool test)
        {
            Rig = new Rig(t, test);
            Rig.Platform.SetStatus(PlatformState.Ok, "running");
            Rig.Platform.Answer("gamemode.trim", "{\"trimmed\":1}");
            Rig.Platform.Answer("hotkey", "{\"installed\":true}");
            Set = new SurfaceSet(new[] {
                new SurfaceSpec("launcher", true, () => new Win("launcher", Did)),
                new SurfaceSpec("osd", true, () => new Win("osd", Did)),
                new SurfaceSpec("wallpapers", false, () => new Win("wallpapers", Did)),
            }, new SurfaceSetOptions { Test = test, Log = Rig.Lines.Add });
            Set.Start("start");
            // App.StartSurfaces: the surface host destroys every surface on a rest and makes the AtStart ones on the wake
            Rig.Events.Subscribe<RestSignal>("mantle.rest", s => { if (s.On) Set.Rest(s.Why ?? "?"); else Set.Wake(s.Why ?? "?"); });
            Parts = new ShellRestParts(new ShellRestPartsOptions {
                Made = () => Set.List().Where(i => i.Phase == SurfacePhase.Made).Select(i => i.Name).ToArray(),
                BarOpen = () => BarOpen,
                HideLauncher = why => Set.Hide("launcher", why),
                Memory = () => (120, 300),
                Pids = () => new[] { 42 },
            });
            Rest = new RestCoordinator(new RestOptions {
                Platform = Rig.Platform, Events = Rig.Events, State = Rig.Hub, Clock = new HostClock(t), Features = null,
                Parts = Parts, Test = test, Log = Rig.Lines.Add,
            });
        }
    }

    static async Task<RestAnswer> Settle(HostLoop loop, ManualTime t, Task<RestAnswer> a)
    {
        for (var i = 0; i < 40 && !a.IsCompleted; i++) { loop.Drain(); t.AdvanceMs(100); }
        loop.Drain();
        return await a;
    }

    [Fact]
    public void A_rest_destroys_every_surface_keeps_the_bar_and_a_wake_makes_the_AtStart_ones_again() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t, test: false);
        k.Set.Show("wallpapers", "a test opened it");   // an on-first-use surface that is open now
        Assert.Equal(new[] { "bar", "launcher", "osd", "wallpapers" }, k.Parts.Pages());
        Assert.True(k.Parts.LauncherAlive);

        var a = await Settle(loop, t, k.Rest.RestAsync(true, "Elden Ring runs"));
        Assert.True(a.Ok);
        Assert.Equal(new[] { "launcher", "osd", "wallpapers" }, a.Closed);
        Assert.Equal(new[] { "bar" }, k.Parts.Pages());
        // the launcher is hidden at once, then destroyed with the others (the same UI queue)
        Assert.True(k.Did.IndexOf("hide launcher") < k.Did.IndexOf("destroy launcher"));
        Assert.Contains("destroy osd", k.Did);
        Assert.Contains("destroy wallpapers", k.Did);
        Assert.False(k.Parts.LauncherAlive);
        Assert.Contains(k.Rig.Platform.Calls, c => c.Verb == "hotkey" && c.ArgsJson.Contains("\"winTap\":false"));
        Assert.True(k.Rig.Hub.Current.Hotkey.Resting);
        Assert.Contains(k.Rig.Platform.Calls, c => c.Verb == "gamemode.trim" && c.ArgsJson.Contains("[42]"));
        Assert.StartsWith("closed 3 windows (launcher, osd, wallpapers); Mantle's memory 120 MB -> 120 MB (working set 300 MB -> 300 MB); idle pages of 1 Mantle process handed back to Windows; kept: bar, the link to Windows and the host", a.Text);
        // nothing is shown while resting, and it says why
        var no = k.Set.Show("osd", "a key");
        Assert.False(no.Ok);
        Assert.Equal("Mantle is resting while a game runs (Elden Ring runs), so osd was not shown", no.Said);

        k.Did.Clear();
        var w = await Settle(loop, t, k.Rest.RestAsync(false, "Elden Ring exited"));
        Assert.True(w.Ok);
        Assert.Equal(new[] { "make launcher", "make osd" }, k.Did);   // hidden and parked; the on-first-use one waits
        Assert.Contains("launcher", w.Back!);
        Assert.Equal(new[] { "bar", "launcher", "osd" }, k.Parts.Pages());
        Assert.True(k.Parts.LauncherAlive);
        Assert.Contains(k.Rig.Platform.Calls, c => c.Verb == "hotkey" && c.ArgsJson.Contains("\"winTap\":true"));
        Assert.Equal("a tap of the Win key opens the Mantle launcher", k.Rig.Hub.Current.Hotkey.Reason);
        Assert.StartsWith("back after 0 min (Elden Ring exited): launcher", w.Text);
    });

    [Fact]
    public void In_a_test_run_the_rest_never_asks_for_the_Win_key_hook_and_a_closed_bar_is_not_kept() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t, test: true);
        k.BarOpen = false;
        k.Set.Hide("launcher", "x");
        Assert.Equal(new[] { "launcher", "osd" }, k.Parts.Pages());
        var a = await Settle(loop, t, k.Rest.RestAsync(true, "a game runs"));
        Assert.Equal(new[] { "launcher", "osd" }, a.Closed);
        Assert.Contains("kept: no window", a.Text);
        Assert.DoesNotContain(k.Rig.Platform.Calls, c => c.Verb == "hotkey");   // a test run never asks for the hook
        var w = await Settle(loop, t, k.Rest.RestAsync(false, "it exited"));
        Assert.Equal("test mode: the Win key hook was not installed", k.Rig.Hub.Current.Hotkey.Reason);
        Assert.Equal(new[] { "launcher", "osd" }, k.Parts.Pages());
        Assert.True(w.Ok);
    });

    [Fact]
    public void The_parts_never_throw_into_the_coordinator_and_name_this_process_only()
    {
        var p = new ShellRestParts(new ShellRestPartsOptions { Made = () => throw new InvalidOperationException("gone"), BarOpen = () => throw new InvalidOperationException("gone") });
        Assert.Empty(p.Pages());
        Assert.False(p.LauncherAlive);
        Assert.Equal(new[] { Environment.ProcessId }, p.OwnPids());
        Assert.False(p.SwitcherRest("x"));
        Assert.False(p.WallpaperRest("x"));
        var (priv, ws) = p.Memory();
        Assert.True(priv > 0 && ws > 0);
        p.AfterRest();
        Assert.Equal((SecondAsk.Rest, "on:a game"), SecondLaunch.Route(new[] { "--test", "--test-rest=on:a game" }, true));
        Assert.Equal(SecondAsk.Plain, SecondLaunch.Route(new[] { "--test-rest=on" }, false).Ask);   // never outside a test run
        Assert.Contains("--test-rest", LaunchArgs.Parse(new[] { "x.exe", "--test-rest=on", "--user-data=C:\\scratch" }).IgnoredTestSwitches());
    }
}
