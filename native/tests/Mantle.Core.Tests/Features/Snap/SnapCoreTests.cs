using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Snap;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Snap;

// lane r9-snap: the Win+arrow move path (lib/features/snap.js#arrow, #probe, #cap, #unname; SNAP-051, 053..057, 100,
// 101) and the never-strand check after every move. A fake sidecar answers snap.move / snap.monitors / layouts.place
// with made-up windows and monitors: nothing in Windows is touched.
public class SnapCoreTests
{
    sealed class Fake
    {
        public readonly List<(string Verb, JsonObject Args, TimeSpan Timeout)> Calls = new();
        public readonly Dictionary<string, Func<JsonObject, PlatformReply>> Answers = new(StringComparer.Ordinal);
        public readonly List<string> Log = new();
        public readonly List<SnapNotice> Notices = new();

        public SnapCore Core(bool test = false) => new(Send, Log.Add, Notices.Add, test);

        ValueTask<PlatformReply> Send(string verb, JsonObject args, TimeSpan timeout)
        {
            Calls.Add((verb, (JsonObject)JsonNode.Parse(args.ToJsonString())!, timeout));
            return ValueTask.FromResult(Answers.TryGetValue(verb, out var f) ? f(args) : PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: " + verb));
        }
    }

    static PlatformReply Ok(string json) => PlatformReply.Success(JsonDocument.Parse(json).RootElement.Clone());

    const string MonA = "{\"id\":\"A\",\"bounds\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1080},\"work\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1040},\"dpi\":96,\"scale\":1,\"primary\":true}";
    const string MonP = "{\"id\":\"P\",\"bounds\":{\"x\":1920,\"y\":0,\"w\":2560,\"h\":1440},\"work\":{\"x\":1920,\"y\":0,\"w\":2560,\"h\":1392},\"dpi\":120,\"scale\":1.25,\"primary\":false}";

    /// A snap.move answer for Notes (hwnd 4242, pid 77) on monitor A, plus <paramref name="extra"/> fields.
    static string Move(string extra, bool ok = true, string monitor = MonA) =>
        "{\"ok\":" + (ok ? "true" : "false") + ",\"hwnd\":\"4242\",\"exe\":\"notes.exe\",\"app\":\"Notes\",\"title\":\"Notes - plan.txt\",\"pid\":77," +
        "\"monitor\":" + monitor + ",\"ms\":3.2,\"moves\":1,\"timing\":{\"check\":0.4,\"fence\":0}" + (extra.Length > 0 ? "," + extra : "") + "}";

    // ------------------------------------------------------------ chords (SNAP-051)

    [Fact]
    public async Task Each_WinArrow_asks_the_sidecar_to_move_the_foreground_window_with_a_2_5_s_timeout()
    {
        var f = new Fake();
        f.Answers["snap.move"] = a => Ok(Move("\"from\":\"normal\",\"to\":\"" + a["dir"]!.GetValue<string>() + "\",\"action\":\"snap\",\"frame\":{\"x\":0,\"y\":0,\"w\":960,\"h\":1040}"));
        var core = f.Core();
        foreach (var (chord, dir) in new[] { ("Win+Left", "left"), ("Win+Right", "right"), ("Win+Up", "up"), ("Win+Down", "down") }) {
            f.Calls.Clear();
            SnapArrowResult r = await core.ArrowAsync(chord);
            Assert.Equal(dir, r.Dir);
            Assert.Equal("snap.move", f.Calls[0].Verb);
            Assert.Equal(dir, f.Calls[0].Args["dir"]!.GetValue<string>());
            Assert.False(f.Calls[0].Args.ContainsKey("hwnd"));
            Assert.Equal(TimeSpan.FromMilliseconds(2500), f.Calls[0].Timeout);
        }
        Assert.StartsWith("Win+Left: a window normal -> left (snap) sidecar 3.2 ms [checks 0.4, move undefined], key to done ", f.Log[0]);
        Assert.EndsWith(" ms, 1 move(s)", f.Log[0]);
    }

    [Fact]
    public async Task A_chord_that_is_not_an_arrow_sends_nothing()
    {
        var f = new Fake();
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Z");
        Assert.Null(r.Dir);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task While_a_panel_is_open_the_arrows_act_on_the_window_it_is_for()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"left\",\"to\":\"tl\",\"action\":\"snap\""));
        await f.Core().ArrowAsync("Win+Up", "999");
        Assert.Equal("999", f.Calls[0].Args["hwnd"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ notices (SNAP-053..057)

    [Fact]
    public async Task Nothing_to_do_shows_no_notice()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"max\",\"to\":\"max\",\"action\":\"none\",\"why\":\"it is already maximized\""));
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Up");
        Assert.Null(r.Notice);
        Assert.Empty(f.Notices);
        Assert.Single(f.Calls);
    }

    [Theory]
    [InlineData("maximize", "Could not maximize Notes")]
    [InlineData("minimize", "Could not minimize Notes")]
    [InlineData("snap", "Could not snap Notes")]
    [InlineData("unsnap", "Could not snap Notes")]
    public async Task A_refused_move_names_the_app_and_gives_the_sidecars_reason_as_a_sentence(string action, string title)
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"normal\",\"to\":\"normal\",\"action\":\"" + action + "\",\"reason\":\"notes has a fixed size\"", ok: false));
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Left");
        Assert.Equal(title, r.Notice!.Title);
        Assert.Equal("Notes has a fixed size.", r.Notice.Text);
        Assert.Equal(new SnapBox(0, 0, 1920, 1040), r.Notice.Work);
    }

    [Fact]
    public async Task A_refusal_with_no_reason_says_Windows_gave_none_and_no_app_name_says_this_window()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok("{\"ok\":false,\"action\":\"snap\"}");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Left");
        Assert.Equal("Could not snap this window", r.Notice!.Title);
        Assert.Equal("Windows gave no reason.", r.Notice.Text);
    }

    [Fact]
    public async Task A_sidecar_error_shows_could_not_snap_this_window_with_the_error()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => PlatformReply.Fail(PlatformOutcome.TimedOut, "snap.move timed out after 2500 ms");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.Equal("Could not snap this window", r.Notice!.Title);
        Assert.Equal("Snap.move timed out after 2500 ms.", r.Notice.Text);
        Assert.Null(r.Notice.Work);
        Assert.StartsWith("Win+Right: snap.move timed out after 2500 ms (", f.Log[0]);
    }

    [Theory]
    [InlineData("tl", "Notes does not fit the top-left quarter")]
    [InlineData("right", "Notes does not fit the right half")]
    [InlineData("grid:3x1:1,0,1,1", "Notes does not fit the zone")]
    public async Task An_app_that_does_not_fit_its_zone_says_which_zone(string to, string title)
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"normal\",\"to\":\"" + to + "\",\"action\":\"snap\",\"fit\":false,\"fitReason\":\"it will not go narrower than 700 px\""));
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Left");
        Assert.Equal(title, r.Notice!.Title);
        Assert.Equal("It will not go narrower than 700 px.", r.Notice.Text);
    }

    [Fact]
    public async Task A_rounded_fit_is_not_a_failure_and_shows_no_notice()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"normal\",\"to\":\"tl\",\"action\":\"snap\",\"fit\":\"rounded\",\"frame\":{\"x\":0,\"y\":1,\"w\":960,\"h\":519}"));
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Left");
        Assert.Null(r.Notice);
    }

    [Fact]
    public async Task The_notice_names_the_app_but_no_log_line_ever_does()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"normal\",\"to\":\"normal\",\"action\":\"snap\",\"reason\":\"NOTES.EXE is not responding (Notes - plan.txt)\"", ok: false));
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Left");
        Assert.Equal("Could not snap Notes", r.Notice!.Title);
        Assert.Contains("NOTES.EXE", r.Notice.Text);
        Assert.NotEmpty(f.Log);
        Assert.DoesNotContain(f.Log, l => l.Contains("notes", StringComparison.OrdinalIgnoreCase) || l.Contains("plan.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(f.Log, l => l == "notice: Could not snap the window: the window is not responding (the window).");
    }

    [Fact]
    public void Unname_replaces_the_longest_name_first_in_any_case_and_skips_one_letter_names()
    {
        Assert.Equal("the window is busy, the window", SnapWords.Unname("Notes - plan.txt is busy, NOTES", new[] { "Notes", "Notes - plan.txt", "x", null }));
        Assert.Equal("a b", SnapWords.Unname("a b", new[] { "a", "b" }));
        Assert.Equal("", SnapWords.Unname(null, null));
    }

    [Fact]
    public void Cap_makes_a_sentence()
    {
        Assert.Equal("Done.", SnapWords.Cap("  done "));
        Assert.Equal("Really?", SnapWords.Cap("really?"));
        Assert.Equal("", SnapWords.Cap(null));
    }

    // ------------------------------------------------------------ test mode (SNAP-109, the moves-nothing half)

    [Fact]
    public async Task A_test_run_with_no_test_window_moves_nothing_and_says_so()
    {
        var f = new Fake();
        SnapArrowResult r = await f.Core(test: true).ArrowAsync("Win+Left");
        Assert.Empty(f.Calls);
        Assert.Equal(SnapCore.TestTitle, r.Notice!.Title);
        Assert.Equal("A --test instance only moves its own test windows, never yours.", r.Notice.Text);
    }

    // ------------------------------------------------------------ health probe (SNAP-100, SNAP-101)

    [Fact]
    public void The_probe_is_healthy_on_an_answer_down_for_an_older_sidecar_and_retried_while_busy()
    {
        Assert.True(SnapCore.Probe(Ok("{\"monitors\":[]}"), 0).Ok);
        SnapProbe older = SnapCore.Probe(PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: snap.monitors"), 0);
        Assert.False(older.Ok);
        Assert.Equal(SnapCore.Older, older.Reason);
        Assert.Null(older.RetryAfter);
        int[] want = { 2000, 5000, 15000, 30000 };
        for (int i = 0; i < want.Length; i++) {
            SnapProbe busy = SnapCore.Probe(PlatformReply.Fail(PlatformOutcome.TimedOut, "snap.monitors timed out"), i);
            Assert.Equal(SnapCore.Busy, busy.Reason);
            Assert.Equal(TimeSpan.FromMilliseconds(want[i]), busy.RetryAfter);
        }
        SnapProbe silent = SnapCore.Probe(PlatformReply.Fail(PlatformOutcome.TimedOut, "snap.monitors timed out"), 4);
        Assert.Equal(SnapCore.Silent, silent.Reason);
        Assert.Null(silent.RetryAfter);
    }

    // ------------------------------------------------------------ SAFETY: the never-strand check after a move

    [Fact]
    public async Task An_unsnap_whose_remembered_monitor_is_gone_is_put_back_on_a_live_monitor_with_one_layouts_place()
    {
        // the fake sidecar's un-snap answers the pre-snap frame where it was on P, which is unplugged: 2200..3000 is on no
        // monitor (the real Snap.cs did this before lane r11-snap-strand; it now fits it itself, so this is the second
        // guard's own test). Clamp onto A: left = max(0, min(2200, 1920-800)) = 1120.
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"restoredFrom\":\"remembered\",\"frame\":{\"x\":2200,\"y\":200,\"w\":800,\"h\":600}"));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        f.Answers["layouts.place"] = _ => Ok("{\"hwnd\":\"4242\",\"placed\":true}");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.Equal(new[] { "snap.move", "snap.monitors", "layouts.place" }, f.Calls.Select(c => c.Verb).ToArray());
        JsonObject place = f.Calls[2].Args;
        Assert.Equal("4242", place["hwnd"]!.GetValue<string>());
        Assert.Equal("normal", place["state"]!.GetValue<string>());
        Assert.Equal(77, place["pid"]!.GetValue<int>());
        Assert.Equal("{\"x\":1120,\"y\":200,\"w\":800,\"h\":600}", place["frame"]!.ToJsonString());
        Assert.Equal(TimeSpan.FromMilliseconds(6000), f.Calls[2].Timeout);
        Assert.True(r.Rescued);
        Assert.Null(r.Notice);
        Assert.Contains(f.Log, l => l.Contains("put back at 1120,200 800x600"));
    }

    [Fact]
    public async Task A_snap_that_lands_on_its_own_monitor_costs_no_extra_call()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"normal\",\"to\":\"left\",\"action\":\"snap\",\"frame\":{\"x\":0,\"y\":0,\"w\":960,\"h\":1040}"));
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Left");
        Assert.Single(f.Calls);
        Assert.Null(r.Rescue);
        Assert.False(r.Rescued);
    }

    [Fact]
    public async Task A_frame_on_another_live_monitor_is_left_where_it_is()
    {
        // the move was for A, the window ended on P (live): read the monitors, find it safe, send no place
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"frame\":{\"x\":2200,\"y\":200,\"w\":800,\"h\":600}"));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "," + MonP + "]}");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.Equal(new[] { "snap.move", "snap.monitors" }, f.Calls.Select(c => c.Verb).ToArray());
        Assert.False(r.Rescue!.Changed);
        Assert.Null(r.Notice);
    }

    [Fact]
    public async Task When_the_monitors_cannot_be_read_the_window_is_still_brought_onto_the_monitor_it_was_moved_to()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"frame\":{\"x\":-5000,\"y\":-5000,\"w\":800,\"h\":600}"));
        f.Answers["snap.monitors"] = _ => PlatformReply.Fail(PlatformOutcome.TimedOut, "snap.monitors timed out");
        f.Answers["layouts.place"] = _ => Ok("{\"hwnd\":\"4242\",\"placed\":true}");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.Equal("{\"x\":0,\"y\":0,\"w\":800,\"h\":600}", f.Calls[2].Args["frame"]!.ToJsonString());
        Assert.True(r.Rescued);
        Assert.Contains(f.Log, l => l.Contains("could not read the monitors"));
    }

    [Fact]
    public async Task A_put_back_that_fails_is_said_in_a_notice_never_silent()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"frame\":{\"x\":2200,\"y\":200,\"w\":800,\"h\":600}"));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        f.Answers["layouts.place"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, "Notes is running as administrator");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.False(r.Rescued);
        Assert.Equal("Notes is running as administrator", r.RescueError);
        Assert.Equal("Could not put Notes back on the screen", r.Notice!.Title);
        Assert.Equal("Notes is running as administrator.", r.Notice.Text);
        Assert.DoesNotContain(f.Log, l => l.Contains("notes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_put_back_answered_placed_false_counts_as_failed()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"frame\":{\"x\":2200,\"y\":200,\"w\":800,\"h\":600}"));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        f.Answers["layouts.place"] = _ => Ok("{\"hwnd\":\"4242\",\"placed\":false,\"reason\":\"that window has closed\"}");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.False(r.Rescued);
        Assert.Equal("That window has closed.", r.Notice!.Text);
    }

    [Fact]
    public async Task A_stranded_answer_with_no_window_handle_sends_no_place_and_says_so()
    {
        var f = new Fake();
        f.Answers["snap.move"] = _ => Ok("{\"ok\":true,\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"monitor\":" + MonA + ",\"frame\":{\"x\":2200,\"y\":200,\"w\":800,\"h\":600}}");
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        SnapArrowResult r = await f.Core().ArrowAsync("Win+Right");
        Assert.DoesNotContain(f.Calls, c => c.Verb == "layouts.place");
        Assert.NotNull(r.Notice);
        Assert.False(r.Rescued);
    }

    [Fact]
    public async Task Maximize_minimize_and_restore_answers_carry_no_frame_and_cost_no_check()
    {
        var f = new Fake();
        foreach (string action in new[] { "maximize", "minimize", "restore" }) {
            f.Calls.Clear();
            f.Answers["snap.move"] = _ => Ok(Move("\"from\":\"normal\",\"to\":\"max\",\"action\":\"" + action + "\",\"frame\":null"));
            SnapArrowResult r = await f.Core().ArrowAsync("Win+Up");
            Assert.Single(f.Calls);
            Assert.Null(r.Rescue);
        }
    }
}
