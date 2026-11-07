using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Lifecycle;
using Mantle.Core.Logging;
using Mantle.Core.State;
using Xunit;

namespace Mantle.Core.Tests.Lifecycle;

/// <summary>
/// The surface host's logic (Mantle.Core.Lifecycle.SurfaceSet + SurfaceWires; Mantle.App.Shell.Host adapts the
/// generated SurfaceCatalog to it): AtStart surfaces made at start and parked, the others on first use, a rest and a
/// quit destroy them, the threads between the host and a surface, and the surface.* commands.
/// </summary>
public class SurfaceHostTests
{
    sealed class Fake : ISurfaceInstance
    {
        public readonly string Name;
        public readonly List<string> Calls;
        public bool ThrowOnCreate;
        public Fake(string name, List<string> calls) { Name = name; Calls = calls; }
        public bool Shown { get; private set; }
        public void Create() { Calls.Add(Name + ".create"); if (ThrowOnCreate) throw new InvalidOperationException("no window today"); }
        public void Show(string why) { Calls.Add($"{Name}.show({why})"); Shown = true; }
        public void Hide(string why) { Calls.Add($"{Name}.hide({why})"); Shown = false; }
        public void Destroy(string why) { Calls.Add($"{Name}.destroy({why})"); Shown = false; }
    }

    sealed class World
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Log = new();
        public readonly Dictionary<string, int> Made = new();
        public Func<string, bool> Throw = _ => false;
        public SurfaceSet Set = null!;

        public World(bool test = false, bool testSurfaces = false, params (string Name, bool AtStart)[] specs)
        {
            var list = specs.Length > 0 ? specs : new[] { ("launcher", true), ("osd", false) };
            Set = new SurfaceSet(list.Select(s => new SurfaceSpec(s.Item1, s.Item2, () => {
                Made[s.Item1] = Made.GetValueOrDefault(s.Item1) + 1;
                return new Fake(s.Item1, Calls) { ThrowOnCreate = Throw(s.Item1) };
            })), new SurfaceSetOptions { Test = test, TestSurfaces = testSurfaces, Log = Log.Add });
        }

        public SurfaceInfo Info(string name) => Set.List().Single(i => i.Name == name);
    }

    [Fact]
    public void AtStart_surfaces_are_made_at_start_hidden_and_the_others_wait_for_first_use()
    {
        var w = new World();
        w.Set.Start("start");
        Assert.Equal(new[] { "launcher.create" }, w.Calls);
        Assert.Equal(SurfacePhase.Made, w.Info("launcher").Phase);
        Assert.False(w.Info("launcher").Shown);   // parked: made, never shown
        Assert.Equal(SurfacePhase.NotMade, w.Info("osd").Phase);
        Assert.Contains("surfaces: 2 in this build; made at start: launcher; on first use: osd", w.Log);
        Assert.Contains("surface launcher: made at start, hidden and parked", w.Log);

        var a = w.Set.Show("osd", "volume up");
        Assert.True(a.Ok); Assert.True(a.Shown);
        Assert.Equal(new[] { "launcher.create", "osd.create", "osd.show(volume up)" }, w.Calls);
        Assert.Contains("surface osd: made on first use (volume up)", w.Log);
        w.Set.Hide("osd", "timeout");
        w.Set.Show("osd", "again");
        Assert.Equal(1, w.Made["osd"]);   // made once, then shown and hidden
    }

    [Fact]
    public void A_name_nobody_built_is_answered_in_words()
    {
        var w = new World(false, false, ("test-parked", true));
        w.Set.Start("start");
        var a = w.Set.Toggle("launcher", "Alt+Space");
        Assert.False(a.Ok);
        Assert.Equal("there is no launcher surface in this build, so nothing was shown (it has: test-parked)", a.Said);
        Assert.Contains(w.Log, l => l.StartsWith("surface launcher: not shown (Alt+Space): there is no launcher surface", StringComparison.Ordinal));
        var empty = new SurfaceSet(Array.Empty<SurfaceSpec>(), new SurfaceSetOptions());
        Assert.EndsWith("(this build has no surfaces yet)", empty.Show("launcher", "x").Said);
        Assert.False(w.Set.Run("blink", "test-parked", "x").Ok);
    }

    [Fact]
    public void Test_stand_ins_are_made_only_in_a_test_run_that_asks_for_them()
    {
        foreach (var (test, asked) in new[] { (false, false), (false, true), (true, false) }) {
            var w = new World(test, asked, ("test-parked", true), ("test-lazy", false));
            w.Set.Start("start");
            Assert.Empty(w.Calls);
            Assert.False(w.Set.Has("test-parked"));
            var a = w.Set.Show("test-lazy", "x");
            Assert.False(a.Ok);
            Assert.Contains("test stand-in", a.Said);
            Assert.Contains(w.Log, l => l.Contains("test stand-ins not made in this run: test-parked, test-lazy", StringComparison.Ordinal));
        }
        var on = new World(true, true, ("test-parked", true), ("test-lazy", false));
        on.Set.Start("start");
        Assert.Equal(new[] { "test-parked.create" }, on.Calls);
        Assert.True(on.Set.Show("test-lazy", "x").Ok);
        Assert.True(SurfaceSet.TestSurfacesWanted(true, n => n == "MANTLE_TEST_SURFACES" ? "1" : null));
        Assert.False(SurfaceSet.TestSurfacesWanted(false, n => "1"));
    }

    [Fact]
    public void A_surface_that_throws_while_made_says_why_and_is_tried_again_on_the_next_show()
    {
        var w = new World();
        w.Throw = n => n == "osd" && !w.Made.ContainsKey("osd-ok");
        var a = w.Set.Show("osd", "volume up");
        Assert.False(a.Ok);
        Assert.Equal("could not be made (on first use (volume up)): no window today", a.Said);
        Assert.Equal(SurfacePhase.Failed, w.Info("osd").Phase);
        Assert.Contains("osd.destroy(it failed to be made)", w.Calls);   // whatever it made is let go
        w.Made["osd-ok"] = 1;
        Assert.True(w.Set.Show("osd", "volume up").Ok);
        Assert.Equal(2, w.Made["osd"]);
    }

    [Fact]
    public void Toggle_shows_then_hides_and_hiding_a_surface_never_made_is_fine()
    {
        var w = new World();
        var h = w.Set.Hide("osd", "x");
        Assert.True(h.Ok);
        Assert.Equal("osd is not made, so it is already hidden", h.Said);
        Assert.Empty(w.Calls);
        Assert.True(w.Set.Toggle("osd", "t1").Shown);
        Assert.False(w.Set.Toggle("osd", "t2").Shown);
        Assert.Equal(new[] { "osd.create", "osd.show(t1)", "osd.hide(t2)" }, w.Calls);
    }

    [Fact]
    public void A_rest_destroys_every_surface_and_the_wake_makes_the_AtStart_ones_again()
    {
        var w = new World();
        w.Set.Start("start");
        w.Set.Show("osd", "x");
        var gone = w.Set.Rest("game.exe is fullscreen");
        Assert.Equal(new[] { "launcher", "osd" }, gone);
        Assert.Contains("launcher.destroy(a rest (game.exe is fullscreen))", w.Calls);
        Assert.All(w.Set.List(), i => Assert.Equal(SurfacePhase.NotMade, i.Phase));
        var refused = w.Set.Show("launcher", "Win tap");
        Assert.False(refused.Ok);
        Assert.Equal("Mantle is resting while a game runs (game.exe is fullscreen), so launcher was not shown", refused.Said);

        var made = w.Set.Wake("the game ended");
        Assert.Equal(new[] { "launcher" }, made);   // the AtStart one is parked again; osd waits for its next use
        Assert.Equal(2, w.Made["launcher"]);
        Assert.Equal(1, w.Made["osd"]);
        Assert.Empty(w.Set.Wake("twice"));
    }

    [Fact]
    public void Quit_destroys_every_surface_and_none_is_made_again()
    {
        var w = new World();
        w.Set.Start("start");
        Assert.Equal(new[] { "launcher" }, w.Set.Close("Mantle was asked to quit"));
        Assert.Contains("surface launcher: destroyed (Mantle was asked to quit)", w.Log);
        var a = w.Set.Show("osd", "x");
        Assert.False(a.Ok);
        Assert.Equal("Mantle is quitting, so osd was not shown", a.Said);
        Assert.True(w.Set.Hide("osd", "x").Ok);
        Assert.Empty(w.Set.Wake("x"));
        Assert.Equal(1, w.Made["launcher"]);
    }

    // ------------------------------------------------------------------ the threads

    /// Two queues standing in for the UI thread and the host thread; nothing runs until it is pumped.
    sealed class Threads
    {
        public readonly Queue<Action> Ui = new(), HostQ = new();
        public bool UiGone;
        public bool PostUi(Action a) { if (UiGone) return false; Ui.Enqueue(a); return true; }
        public void PostHost(Action a) => HostQ.Enqueue(a);
        public string? Running;
        public void Pump()
        {
            for (int i = 0; i < 100 && (Ui.Count > 0 || HostQ.Count > 0); i++) {
                while (HostQ.Count > 0) { Running = "host"; HostQ.Dequeue()(); }
                while (Ui.Count > 0) { Running = "ui"; Ui.Dequeue()(); }
            }
            Running = null;
        }
    }

    sealed class Lines : ILogSink { public readonly List<string> All = new(); public void Line(string t) => All.Add(t); }

    static ShellState S0 => ShellState.Initial(ShellMode.Test, BarEdge.Bottom);

    [Fact]
    public void State_reaches_the_UI_thread_coalesced_to_the_last_of_a_burst()
    {
        var t = new Threads();
        var feed = new StateFeed(S0, t.PostUi, _ => { });
        var seen = new List<ShellState>();
        feed.Subscribe(seen.Add);
        var s1 = S0 with { Edge = BarEdge.Top };
        var s2 = s1 with { Edge = BarEdge.Bottom, Mode = ShellMode.Live };
        feed.Push(s1); feed.Push(s2);
        Assert.NotEqual(s2, feed.Current);
        Assert.Single(t.Ui);   // one UI job for the burst
        Assert.Empty(seen);   // nothing until the UI thread runs
        t.Pump();
        Assert.Single(seen);
        Assert.Equal(s2, seen[0]);
        Assert.Equal(s2, feed.Current);
        Assert.Equal(1, feed.Deliveries);
        feed.Push(s1); t.Pump();
        Assert.Equal(2, feed.Deliveries);
    }

    SurfaceLink Link(Threads t, CommandBus bus, EventHub ev, Lines log, string name = "launcher")
        => new(name, new SurfaceWires { PostUi = t.PostUi, PostHost = t.PostHost, Bus = bus, Events = ev, Feed = new StateFeed(S0, t.PostUi, log.Line), Log = log });

    [Fact]
    public async Task A_command_from_a_surface_runs_on_the_host_thread_with_its_name_and_answers_on_its_UI_thread()
    {
        var t = new Threads();
        var bus = new CommandBus();
        string? ranOn = null, caller = null, got = null;
        bus.Register(new CommandRegistration("state.get", "host", c => {
            ranOn = t.Running; caller = c.Caller; got = c.Args.GetProperty("q").GetString();
            return new ValueTask<CommandResult>(CommandResult.Success());
        }));
        var link = Link(t, bus, new EventHub(_ => { }), new Lines());
        var args = new JsonObject { ["q"] = "first" };
        var pending = link.CallAsync("state.get", args);
        args["q"] = "changed after the call";   // the surface may reuse its object at once
        Assert.False(pending.IsCompleted);   // never waits: nothing ran yet
        Assert.Single(t.HostQ);
        Assert.Empty(t.Ui);
        t.Running = "host"; t.HostQ.Dequeue()(); t.Running = null;   // the host thread runs the command ...
        Assert.Equal("first", got);
        Assert.False(pending.IsCompleted);   // ... and the answer goes back through the UI thread's queue
        Assert.Single(t.Ui);
        t.Pump();
        var r = await pending;
        Assert.True(r.Ok);
        Assert.Equal("host", ranOn);
        Assert.Equal("launcher", caller);
    }

    [Fact]
    public void A_host_event_is_re_posted_to_the_UI_thread_with_the_current_value_first_and_stops_when_disposed()
    {
        var t = new Threads();
        var ev = new EventHub(_ => { });
        string current = "light";
        ev.Replay("theme", () => current);
        var link = Link(t, new CommandBus(), ev, new Lines());
        var seen = new List<(string, string?)>();
        var sub = link.On<string>("theme", v => seen.Add((v, t.Running)));
        t.Pump();
        Assert.Equal(new[] { ("light", (string?)"ui") }, seen);
        t.PostHost(() => ev.Publish("theme", "dark"));
        t.Pump();
        Assert.Equal(("dark", (string?)"ui"), seen[^1]);
        t.PostHost(() => ev.Publish("theme", "light"));
        sub.Dispose();   // disposed before the queued payload reaches the UI thread
        t.Pump();
        Assert.Equal(2, seen.Count);
        link.Dispose();
    }

    [Fact]
    public void A_destroyed_surface_hears_nothing_more_and_its_log_lines_carry_its_name()
    {
        var t = new Threads();
        var ev = new EventHub(_ => { });
        var log = new Lines();
        var link = Link(t, new CommandBus(), ev, log, "osd");
        int events = 0;
        link.On<int>("volume", _ => events++);
        t.Pump();
        link.Log.Line("shown\nsecond line");
        Assert.Equal(new[] { "osd: shown", "osd: second line" }, log.All);
        t.PostHost(() => ev.Publish("volume", 5));
        link.Dispose();
        t.Pump();
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task The_control_posts_to_the_UI_thread_and_says_so_when_there_is_no_set_or_no_UI_thread()
    {
        var t = new Threads();
        SurfaceSet? set = null;
        var c = new SurfaceControl(() => set, t.PostUi);
        var early = c.RunAsync("show", "launcher", "x");
        t.Pump();
        Assert.Equal("launcher was not shown: the surfaces are not set up yet (Mantle is still starting)", (await early).Said);

        var w = new World(); set = w.Set; w.Set.Start("start");
        var ok = c.RunAsync("toggle", "launcher", "Alt+Space");
        Assert.False(ok.IsCompleted);
        t.Pump();
        Assert.True((await ok).Shown);
        var list = c.ListAsync(); t.Pump();
        Assert.Equal(new[] { "launcher", "osd" }, (await list).Select(i => i.Name));

        t.UiGone = true;
        Assert.Equal("osd was not hidden: the UI thread is gone (Mantle is closing)", (await c.RunAsync("hide", "osd", "x")).Said);
    }

    [Fact]
    public async Task Alt_Space_with_no_launcher_surface_is_one_plain_log_line_never_a_silent_no_op()
    {
        var t = new Threads();
        var w = new World(true, true, ("test-parked", true));
        w.Set.Start("start");
        var c = new SurfaceControl(() => w.Set, t.PostUi);
        var log = new List<string>();
        var run = SurfaceControl.ToggleFromKey(c, "launcher", Mantle.Core.Keys.ShellShortcuts.Launcher, log.Add);
        t.Pump();
        await run;
        Assert.Equal(new[] { "mantle: Alt+Space: there is no launcher surface in this build, so nothing was shown (it has: test-parked)" }, log);

        var w2 = new World();
        w2.Set.Start("start");
        var c2 = new SurfaceControl(() => w2.Set, t.PostUi);
        var run2 = SurfaceControl.ToggleFromKey(c2, "launcher", "Alt+Space", log.Add);
        t.Pump(); await run2;
        Assert.Single(log);   // an ok toggle says nothing more
        Assert.Contains("launcher.show(Alt+Space)", w2.Calls);
    }

    // ------------------------------------------------------------------ the commands

    [Fact]
    public async Task The_surface_commands_are_registered_from_the_generated_catalog_and_take_a_name()
    {
        var t = new Threads();
        var w = new World(); w.Set.Start("start");
        var bus = new CommandBus();
        using var reg = Catalogs.Register(bus, new SurfaceCommands(new SurfaceControl(() => w.Set, t.PostUi)));
        foreach (var n in new[] { "surface.show", "surface.hide", "surface.toggle", "surface.list" }) {
            Assert.True(bus.Has(n), n);
            Assert.Contains(Catalogs.Commands, x => x.Name == n && x.Owner == "SurfaceCommands" && !x.Static);
        }
        async Task<CommandResult> Call(string n, JsonObject? a) { var p = bus.CallAsync(n, a, "settings").AsTask(); t.Pump(); return await p; }

        var shown = await Call("surface.show", new JsonObject { ["name"] = "osd" });
        Assert.True(shown.Ok, shown.Error);
        Assert.True(shown.Result.GetProperty("shown").GetBoolean());
        Assert.Contains("osd.show(settings)", w.Calls);   // no "why": the caller's name
        await Call("surface.hide", new JsonObject { ["name"] = "osd", ["why"] = "Esc" });
        Assert.Contains("osd.hide(Esc)", w.Calls);

        var none = await Call("surface.toggle", null);
        Assert.False(none.Ok);
        Assert.Equal("surface.toggle needs the surface's name, like { \"name\": \"launcher\" }", none.Error);
        var bad = await Call("surface.show", new JsonObject { ["name"] = "..\\x" });
        Assert.False(bad.Ok);
        Assert.Contains("lower-case letters, digits and dashes", bad.Error);
        var missing = await Call("surface.show", new JsonObject { ["name"] = "wallpapers" });
        Assert.False(missing.Ok);
        Assert.StartsWith("there is no wallpapers surface in this build", missing.Error);

        var list = await Call("surface.list", null);
        var arr = list.Result.GetProperty("surfaces");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("made", arr[0].GetProperty("phase").GetString());
        Assert.Equal("osd", SurfaceCommands.NameOf(JsonDocument.Parse("\"osd\"").RootElement));
    }

    // ------------------------------------------------------------------ --open and the second start

    [Fact]
    public void A_test_start_may_open_a_test_stand_in_and_every_other_start_keeps_Electrons_allow_list()
    {
        var stand = new[] { "test-lazy", "test-parked" };
        var t = OpenRequest.From(new[] { "x.exe", "--test", "--profile-name=a", "--open=test-lazy" }, stand)!;
        Assert.True(t.Ok);
        Assert.Equal("test-lazy", t.Target);
        Assert.Equal("mantle: opened test-lazy for a second start (--open=test-lazy, a test stand-in)", t.Line);
        // without --test, or a name the build does not have: Electron's refusal, exactly
        var live = OpenRequest.From(new[] { "x.exe", "--open=test-lazy" }, stand)!;
        Assert.False(live.Ok);
        Assert.Equal(OpenRequest.From(new[] { "x.exe", "--open=test-lazy" })!.Line, live.Line);
        Assert.False(OpenRequest.From(new[] { "--test", "--open=test-other" }, stand)!.Ok);
        Assert.False(OpenRequest.From(new[] { "--test", "--open=launcher" }, stand)!.Ok);
        Assert.True(OpenRequest.From(new[] { "--test", "--open=wallpapers" }, stand)!.Ok);

        Assert.Equal((SecondAsk.Surface, "toggle:test-lazy"), SecondLaunch.Route(new[] { "--test", "--test-surface=toggle:test-lazy" }, true));
        Assert.Equal(SecondAsk.Plain, SecondLaunch.Route(new[] { "--test-surface=show:x" }, false).Ask);   // never outside a test run
        var args = LaunchArgs.Parse(new[] { "x.exe", "--test-surface=show:x", "--user-data=C:\\scratch" });
        Assert.Contains("--test-surface", args.IgnoredTestSwitches());
        Assert.Empty(args.Unknown);
    }
}
