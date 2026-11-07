using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;
using Mantle.Core.State;
using Xunit;

namespace Mantle.Core.Tests.Commands;

// lib/call-log.test.js, lib/state.test.js and main.js's mantle:call / replay rules as the spec.
public class CommandBusTests
{
    readonly List<string> _lines = new();

    [Fact]
    public async Task LOG011_unknown_nameless_and_throwing_commands_answer_in_plain_words()
    {
        var bus = new CommandBus(new CallLog(_lines.Add));
        Assert.Equal("Mantle has no command called \"qs.nope\"", (await bus.CallAsync("qs.nope")).Error);
        Assert.Equal("Mantle was asked for a command with no name", (await bus.CallAsync("")).Error);
        bus.Register(new CommandRegistration("shell.boom", "host", _ => throw new InvalidOperationException("the disk is full")));
        var r = await bus.CallAsync("shell.boom");
        Assert.False(r.Ok);
        Assert.Equal("the disk is full", r.Error);
        Assert.Contains(_lines, l => l == "mantle: shell.boom -> threw: InvalidOperationException: the disk is full");
        var dup = Assert.Throws<InvalidOperationException>(() => bus.Register(new CommandRegistration("shell.boom", "bar", _ => default)));
        Assert.Contains("already registered by host, so bar cannot register it too", dup.Message);
    }

    [Fact]
    public async Task A_handler_gets_detached_args_and_a_disposed_registration_is_gone()
    {
        var bus = new CommandBus();
        string? seen = null;
        var reg = bus.Register(new CommandRegistration("bar.pin", "bar", c => { seen = c.Args.GetProperty("path").GetString() + "|" + c.Caller; return ValueTask.FromResult(CommandResult.Success()); }));
        var args = new JsonObject { ["path"] = "a" };
        Assert.True((await bus.CallAsync("bar.pin", args, "tray")).Ok);
        Assert.Equal("a|tray", seen);
        Assert.True(bus.Has("bar.pin"));
        reg.Dispose();
        Assert.False(bus.Has("bar.pin"));
    }

    [Fact]
    public void LOG006_a_failure_is_written_once_counted_then_its_recovery_and_strings_are_hidden()
    {
        var log = new CallLog(_lines.Add);
        var args = new JsonObject { ["ssid"] = "Home Wifi", ["mode"] = "join" };
        for (int i = 0; i < 3; i++) log.Result("net.join", args, CommandResult.Fail("access was denied"));
        Assert.Single(_lines);
        Assert.Equal("mantle: net.join {\"ssid\":\"<9 chars>\",\"mode\":\"join\"} -> access was denied", _lines[0]);
        log.Result("net.join", args, CommandResult.Fail("it took too long and was stopped"));
        Assert.EndsWith("(before this, \"access was denied\" failed 3 time(s) in a row)", _lines[1]);
        log.Result("net.join", args, CommandResult.Success());
        Assert.Equal("mantle: net.join {\"ssid\":\"<9 chars>\",\"mode\":\"join\"} recovered after 1 failure(s) in a row (the last: it took too long and was stopped)", _lines[2]);
        Assert.Equal(0, log.OpenCount);
        var capped = new CallLog(_lines.Add, maxKeys: 2);
        for (int i = 0; i < 5; i++) capped.Result("c" + i, null, CommandResult.Fail("no"));
        Assert.Equal(2, capped.OpenCount);
    }

    [Fact]
    public void LOG007_qs_set_is_always_a_line_and_a_dragged_slider_is_summed_when_it_stops()
    {
        var host = new ManualScheduler();
        var log = new SetLog(_lines.Add, host.Delay);
        log.Record("wifi", "true", false, null, "the adapter is off", 12);
        Assert.Equal("mantle: qs.set wifi=true -> NOT DONE: the adapter is off (12 ms)", _lines[0]);
        log.Record("volume", "10", true, "10", null, 5);
        log.Record("volume", "20", true, "20", null, 7);
        log.Record("volume", "30", true, "30", null, 9);
        Assert.Equal(2, _lines.Count);
        host.Advance(750);
        Assert.Equal("mantle: qs.set volume: 2 more while it was dragged, the last 30 (reads 30), all ok, 7-9 ms each", _lines[2]);
    }

    [Fact]
    public void LOG015_LOG016_events_replay_the_current_value_and_a_type_mismatch_is_said()
    {
        var ev = new EventHub(_lines.Add);
        int current = 5;
        ev.Replay("volume", () => current);
        var got = new List<int>();
        ev.Subscribe<int>("volume", got.Add);
        ev.Publish("volume", 6);
        Assert.Equal(new[] { 5, 6 }, got);
        ev.Subscribe<string>("volume", _ => throw new Exception("never"));
        Assert.Contains(_lines, l => l.Contains("\"volume\" is published as Int32 but a subscriber expects String"));
        Assert.Throws<InvalidOperationException>(() => ev.Replay("volume", () => 1));
        Assert.False(ev.HasReplay("battery"));
    }

    [Fact]
    public void LOG012_the_hub_publishes_only_real_changes_and_compares_features_entry_by_entry()
    {
        var hub = new ShellStateHub(ShellState.Initial(ShellMode.Test, BarEdge.Bottom));
        int changes = 0;
        hub.OnChange(_ => changes++);
        var f = new FeaturePiece("Clipboard", true, true, "running", false, "", null, false);
        Assert.True(hub.Set(s => s with { Features = s.Features.SetItem("clipboard", f) }));
        Assert.False(hub.Set(s => s with { Features = s.Features.Remove("clipboard").SetItem("clipboard", f with { }) }));
        Assert.False(hub.Set(s => s with { Taskbar = s.Taskbar with { } }));
        Assert.True(hub.Set(s => s with { Sidecar = new SidecarPiece(PlatformState.Ok, "sidecar 0.3.0 running as pid 1") }));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void LOG014_problems_list_every_unhealthy_piece_with_its_reason_and_resting_or_stopped_features_are_silent()
    {
        var s = ShellState.Initial(ShellMode.Live, BarEdge.Bottom);
        var p = ShellStateHub.Problems(s).Select(x => x.Piece).ToList();
        Assert.Equal(new[] { "sidecar", "taskbar", "desktop layer", "Win key", "tray", "taskbar watch", "window glass", "shortcuts" }, p);
        var healthy = s with {
            Sidecar = new SidecarPiece(PlatformState.Ok, "ok"), Taskbar = new TaskbarPiece(true, false, "auto-hide"),
            Desktop = new DesktopPiece(true, "progman", "under the icons"), Hotkey = new HotkeyPiece(false, "a game runs", Resting: true),
            Tray = new TrayPiece(true, "ok", true, "watching"), Glass = new GlassPiece("acrylic-blur-behind", "ok"),
            Shortcuts = new ShortcutsPiece(true, true, "ok"),
            Features = s.Features.SetItem("clips", new FeaturePiece("Clips", true, false, "the recorder would not start", false, "", null, false))
                                 .SetItem("files", new FeaturePiece("Files", true, false, "stopped", false, "", null, true))
                                 .SetItem("notes", new FeaturePiece("Notes", true, false, "resting", false, "", null, false, Resting: true)),
            Backdrop = s.Backdrop with { Bar = new BackdropUse(true, false, 0, 0, "capture gave up") },
        };
        var left = ShellStateHub.Problems(healthy);
        Assert.Equal(new[] { "bar backdrop: capture gave up", "Clips: the recorder would not start" }, left.Select(x => x.Piece + ": " + x.Reason));
        Assert.All(ShellStateHub.Problems(s), x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
    }
}
