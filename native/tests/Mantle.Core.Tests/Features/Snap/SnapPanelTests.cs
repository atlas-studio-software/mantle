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

// lane r9-snap: the Win+Z picker and Snap Assist as a model (lib/features/snap.js#picker, #pickZone, #openAssist,
// #candidates, #pickWindow, #giveFocus, pick / dismiss; SNAP-062..068, 073..078, 080, 082, 090..093) and the
// never-strand check on every window a pick moves. A fake sidecar answers snap.info / snap.zone / windows / focus /
// snap.monitors / layouts.place with made-up windows and monitors: nothing in Windows is touched, nothing is drawn.
public class SnapPanelTests
{
    sealed class Fake
    {
        public readonly List<(string Verb, JsonObject Args, TimeSpan Timeout)> Calls = new();
        public readonly Dictionary<string, Func<JsonObject, PlatformReply>> Answers = new(StringComparer.Ordinal);
        public readonly List<string> Log = new();
        public readonly List<SnapNotice> Notices = new();
        public readonly List<SnapShow> Shows = new();
        public readonly List<int> Hides = new();

        public SnapPanel Panel(bool test = false) => new(Send, Log.Add, Notices.Add, Shows.Add, Hides.Add, test);

        public List<(string Verb, JsonObject Args, TimeSpan Timeout)> Of(string verb) => Calls.Where(c => c.Verb == verb).ToList();

        ValueTask<PlatformReply> Send(string verb, JsonObject args, TimeSpan timeout)
        {
            Calls.Add((verb, (JsonObject)JsonNode.Parse(args.ToJsonString())!, timeout));
            return ValueTask.FromResult(Answers.TryGetValue(verb, out var f) ? f(args) : PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: " + verb));
        }
    }

    static PlatformReply Ok(string json) => PlatformReply.Success(JsonDocument.Parse(json).RootElement.Clone());

    const string MonA = "{\"id\":\"A\",\"bounds\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1080},\"work\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1040},\"dpi\":96,\"scale\":1,\"primary\":true}";
    const string MonP = "{\"id\":\"P\",\"bounds\":{\"x\":1920,\"y\":0,\"w\":2560,\"h\":1440},\"work\":{\"x\":1920,\"y\":0,\"w\":2560,\"h\":1392},\"dpi\":120,\"scale\":1.25,\"primary\":false}";

    static string F(int x, int y, int w, int h) => "{\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h + "}";

    /// snap.info for Code (hwnd 100) at 200,100 1200x800 on monitor A.
    static string Info(bool resizable = true, bool elevated = false) =>
        "{\"hwnd\":\"100\",\"exe\":\"code.exe\",\"app\":\"Code\",\"title\":\"plan.txt - Code\",\"pid\":77,\"resizable\":" + (resizable ? "true" : "false") +
        ",\"elevatedAboveMantle\":" + (elevated ? "true" : "false") + ",\"frame\":" + F(200, 100, 1200, 800) + ",\"monitor\":" + MonA + "}";

    /// A snap.zone / snap.move answer for window <paramref name="hwnd"/> that landed at <paramref name="frame"/>.
    static string Landed(string frame, string hwnd = "100", string monitor = MonA, string assist = "[]", bool ok = true, string extra = "") =>
        "{\"ok\":" + (ok ? "true" : "false") + ",\"hwnd\":\"" + hwnd + "\",\"exe\":\"code.exe\",\"app\":\"Code\",\"title\":\"plan.txt - Code\",\"pid\":77," +
        "\"from\":\"normal\",\"to\":\"grid:3x1:0,0,1,1\",\"action\":\"snap\",\"monitor\":" + monitor + ",\"frame\":" + frame +
        ",\"moves\":1,\"ms\":2.1,\"fit\":true,\"assist\":" + assist + extra + "}";

    /// Two empty Win+Z thirds after the left one: the middle and the right third of monitor A.
    static readonly string TwoThirdsLeft =
        "[{\"zone\":{\"name\":\"grid:3x1:1,0,1,1\",\"grid\":[3,1],\"cell\":[1,0,1,1]},\"frame\":" + F(640, 0, 640, 1040) + "}," +
        "{\"zone\":{\"name\":\"grid:3x1:2,0,1,1\",\"grid\":[3,1],\"cell\":[2,0,1,1]},\"frame\":" + F(1280, 0, 640, 1040) + "}]";

    const string Windows =
        "[{\"hwnd\":\"100\",\"title\":\"plan.txt - Code\",\"exe\":\"code.exe\"}," +
        "{\"hwnd\":\"200\",\"title\":\"Inbox\",\"exe\":\"C:\\\\Apps\\\\Mail\\\\mail.exe\",\"icon\":\"data:image/png;base64,AA\",\"minimized\":false}," +
        "{\"hwnd\":300,\"title\":\"Docs\",\"exe\":\"word.exe\",\"minimized\":true}]";

    static Fake Rig()
    {
        var f = new Fake();
        f.Answers["snap.info"] = _ => Ok(Info());
        f.Answers["focus"] = _ => Ok("{\"ok\":true}");
        f.Answers["windows"] = _ => Ok(Windows);
        f.Answers["layouts.place"] = _ => Ok("{\"placed\":true}");
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "," + MonP + "]}");
        return f;
    }

    // ------------------------------------------------------------ Win+Z (SNAP-062..067)

    [Fact]
    public async Task Win_Z_asks_snap_info_for_the_foreground_window_and_opens_the_picker_under_its_maximize_button()
    {
        var f = Rig();
        SnapSession? s = await f.Panel().PickerAsync();
        Assert.NotNull(s);
        Assert.Equal("snap.info", f.Calls[0].Verb);
        Assert.False(f.Calls[0].Args.ContainsKey("hwnd"));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), f.Calls[0].Timeout);
        SnapShow show = Assert.Single(f.Shows);
        Assert.Equal("picker", show.Mode);
        Assert.Equal(1, show.Seq);
        Assert.Equal(SnapBox.Xywh(976, 128, 400, 268), show.Rect);          // 200 + 1200 - 400 - 24, 100 + 28
        Assert.Equal("code.exe", show.Payload["app"]!["exe"]!.GetValue<string>());
        Assert.Equal("Code", show.Payload["app"]!["name"]!.GetValue<string>());
        Assert.Null(show.Payload["start"]);
        Assert.Equal("100", s!.Target);
        Assert.Empty(f.Notices);
    }

    [Fact]
    public async Task Win_Z_while_the_picker_is_open_closes_it_and_gives_the_focus_back_to_its_window()
    {
        var f = Rig();
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        Assert.Null(await p.PickerAsync());
        Assert.Null(p.Session);
        Assert.Equal(new[] { 1 }, f.Hides);
        Assert.Equal("Win+Z again", p.LastEnded);
        Assert.Single(f.Of("snap.info"));
        var focus = Assert.Single(f.Of("focus"));
        Assert.Equal("100", focus.Args["hwnd"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromMilliseconds(1500), focus.Timeout);
    }

    [Fact]
    public async Task Win_Z_on_a_fixed_size_window_is_a_notice_not_a_picker_and_the_log_never_names_the_app()
    {
        var f = Rig();
        f.Answers["snap.info"] = _ => Ok(Info(resizable: false));
        Assert.Null(await f.Panel().PickerAsync());
        Assert.Empty(f.Shows);
        SnapNotice n = Assert.Single(f.Notices);
        Assert.Equal("Could not snap Code", n.Title);
        Assert.Equal("Code has a fixed size, so it cannot be resized to fill a zone.", n.Text);
        Assert.Equal(SnapBox.Xywh(0, 0, 1920, 1040), n.Work);
        Assert.Contains("notice: Could not snap the window: the window has a fixed size, so it cannot be resized to fill a zone.", f.Log);
        Assert.DoesNotContain(f.Log, l => l.Contains("Code", StringComparison.Ordinal) || l.Contains("code.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Win_Z_on_an_elevated_window_says_to_run_Mantle_as_administrator_too()
    {
        var f = Rig();
        f.Answers["snap.info"] = _ => Ok(Info(elevated: true));
        await f.Panel().PickerAsync();
        SnapNotice n = Assert.Single(f.Notices);
        Assert.Equal("Could not snap Code", n.Title);
        Assert.Equal("Code is running as administrator, so Windows does not let Mantle move it. To snap it, run Mantle as administrator too.", n.Text);
        Assert.Empty(f.Shows);
    }

    [Fact]
    public async Task Win_Z_when_snap_info_fails_says_snap_layouts_are_not_available_with_the_error()
    {
        var f = Rig();
        f.Answers["snap.info"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, "timed out after 1500 ms");
        await f.Panel().PickerAsync();
        SnapNotice n = Assert.Single(f.Notices);
        Assert.Equal("Snap layouts are not available", n.Title);
        Assert.Equal("Timed out after 1500 ms.", n.Text);
        Assert.Null(n.Work);
        Assert.Empty(f.Shows);
    }

    [Fact]
    public async Task Win_Z_with_no_frame_in_the_answer_is_a_notice_not_a_throw()
    {
        var f = Rig();
        f.Answers["snap.info"] = _ => Ok("{\"hwnd\":\"100\",\"exe\":\"code.exe\",\"resizable\":true,\"monitor\":" + MonA + "}");
        await f.Panel().PickerAsync();
        SnapNotice n = Assert.Single(f.Notices);
        Assert.Equal(SnapPanel.NotAvailable, n.Title);
        Assert.Equal(SnapPanel.NoFrame, n.Text);
        Assert.Empty(f.Shows);
    }

    [Fact]
    public async Task Win_Z_in_a_test_run_with_no_window_asks_nothing_and_says_why()
    {
        var f = Rig();
        await f.Panel(test: true).PickerAsync();
        Assert.Empty(f.Calls);
        SnapNotice n = Assert.Single(f.Notices);
        Assert.Equal(SnapCore.TestTitle, n.Title);
        Assert.Equal(SnapCore.TestText, n.Text);
    }

    // ------------------------------------------------------------ the pick (SNAP-068, 073..075)

    [Fact]
    public async Task A_picked_zone_is_snap_zone_with_the_grid_cell_and_the_other_zones_then_the_window_gets_the_focus()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040)));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "thirds", 0);
        Assert.False(r.Stale);
        Assert.Null(r.Error);
        Assert.Null(r.Notice);
        var z = Assert.Single(f.Of("snap.zone"));
        Assert.Equal("100", z.Args["hwnd"]!.GetValue<string>());
        Assert.Equal("[3,1]", z.Args["grid"]!.ToJsonString());
        Assert.Equal("[0,0,1,1]", z.Args["cell"]!.ToJsonString());
        Assert.Equal("[{\"grid\":[3,1],\"cell\":[1,0,1,1]},{\"grid\":[3,1],\"cell\":[2,0,1,1]}]", z.Args["rest"]!.ToJsonString());
        Assert.Equal(TimeSpan.FromMilliseconds(2500), z.Timeout);
        Assert.Contains("Win+Z thirds/left third: a window -> grid:3x1:0,0,1,1, 1 move(s), sidecar 2.1 ms", f.Log);
        Assert.Contains("#1 picker: ended: a zone was picked", f.Log);
        Assert.Equal("100", f.Of("focus").Last().Args["hwnd"]!.GetValue<string>());
        Assert.Empty(f.Of("windows"));                                       // no empty zone named: no Snap Assist
        Assert.Equal("thirds", p.LastZone!.Value.Layout);
        Assert.Equal(0, p.LastZone.Value.Zone);
    }

    [Fact]
    public async Task The_next_picker_is_given_the_zone_picked_last_as_start()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040)));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        await p.PickerAsync();
        JsonNode start = f.Shows.Last().Payload["start"]!;
        Assert.Equal("thirds", start["layout"]!.GetValue<string>());
        Assert.Equal(0, start["zone"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_unknown_zone_is_refused_before_any_move_and_the_picker_stays_open()
    {
        var f = Rig();
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "thirds", 7);
        Assert.Equal("no zone 7 in layout thirds", r.Error);
        Assert.Equal("no zone 0 in layout nope", (await p.PickAsync(1, "nope", 0)).Error);
        Assert.Empty(f.Of("snap.zone"));
        Assert.NotNull(p.Session);
    }

    [Fact]
    public async Task A_refused_pick_says_could_not_snap_with_the_reason_and_gives_no_focus()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed("null", ok: false, extra: ",\"reason\":\"Code is not responding\""));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "halves", 1);
        Assert.Equal("Could not snap Code", r.Notice!.Title);
        Assert.Equal("Code is not responding.", r.Notice.Text);
        Assert.Empty(f.Of("focus"));
        Assert.Contains("Win+Z halves/right half: a window -> grid:3x1:0,0,1,1 refused: the window is not responding, 1 move(s), sidecar 2.1 ms", f.Log);
        Assert.Null(p.LastZone);
    }

    [Fact]
    public async Task A_pick_with_no_answer_says_could_not_snap_this_window_with_the_error()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, "timed out after 2500 ms");
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "quarters", 3);
        Assert.Equal("Could not snap this window", r.Notice!.Title);
        Assert.Equal("Timed out after 2500 ms.", r.Notice.Text);
        Assert.Contains("Win+Z quarters/bottom-right quarter: timed out after 2500 ms", f.Log);
    }

    [Fact]
    public async Task A_zone_the_app_does_not_fit_says_so_with_the_zone_name_and_gives_the_focus()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft, extra: ",\"fit\":false,\"fitReason\":\"it will not go smaller than 700 px\"")
            .Replace("\"fit\":true,", ""));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "thirds", 0);
        Assert.Equal("Code does not fit the left third", r.Notice!.Title);
        Assert.Equal("It will not go smaller than 700 px.", r.Notice.Text);
        Assert.Empty(f.Of("windows"));                                       // no Snap Assist after a misfit
        Assert.Equal("100", f.Of("focus").Last().Args["hwnd"]!.GetValue<string>());
    }

    // ------------------------------------------------------------ Snap Assist (SNAP-078, 080, 082, 090..093)

    [Fact]
    public async Task A_picked_zone_with_empty_zones_left_opens_Snap_Assist_over_the_next_with_the_other_windows()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "thirds", 0);
        Assert.Null(r.Notice);
        SnapShow show = f.Shows.Last();
        Assert.Equal("assist", show.Mode);
        Assert.Equal(2, show.Seq);
        Assert.Equal(SnapBox.Xywh(640, 0, 640, 1040), show.Rect);
        Assert.Equal("middle third", show.Payload["zone"]!["name"]!.GetValue<string>());
        Assert.Equal(1, show.Payload["left"]!.GetValue<int>());
        Assert.Equal(0, show.Payload["sel"]!.GetValue<int>());
        var windows = Assert.Single(f.Of("windows"));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), windows.Timeout);
        SnapSession s = p.Session!;
        Assert.Equal(new[] { "200", "300" }, s.Cands.Select(c => c.Hwnd));   // not the snapped window; a number hwnd as text
        Assert.Equal("mail.exe", s.Cands[0].Exe);                            // no path
        Assert.Equal("data:image/png;base64,AA", s.Cands[0].Icon);
        Assert.Null(s.Cands[1].Icon);
        Assert.True(s.Cands[1].Minimized);
        Assert.Empty(f.Of("focus"));                                         // Snap Assist has the keyboard, not the window
    }

    [Fact]
    public async Task A_Snap_Assist_pick_moves_that_window_on_the_first_windows_monitor_then_fills_the_next_zone_without_it()
    {
        var f = Rig();
        f.Answers["snap.zone"] = a => Ok(Landed(a["cell"]!.ToJsonString() == "[0,0,1,1]" ? F(0, 0, 640, 1040) : a["cell"]!.ToJsonString() == "[1,0,1,1]" ? F(640, 0, 640, 1040) : F(1280, 0, 640, 1040),
                                                hwnd: a["hwnd"]!.GetValue<string>(), assist: a["hwnd"]!.GetValue<string>() == "100" ? TwoThirdsLeft : "[]"));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        SnapPickResult r = await p.PickAsync(2, hwnd: "200");
        Assert.Null(r.Notice);
        var z = f.Of("snap.zone")[1];
        Assert.Equal("200", z.Args["hwnd"]!.GetValue<string>());
        Assert.Equal("100", z.Args["of"]!.GetValue<string>());
        Assert.Equal("[3,1]", z.Args["grid"]!.ToJsonString());
        Assert.Equal("[1,0,1,1]", z.Args["cell"]!.ToJsonString());
        Assert.False(z.Args.ContainsKey("rest"));
        Assert.Contains("Snap Assist: a window -> grid:3x1:0,0,1,1, 1 move(s), sidecar 2.1 ms", f.Log);
        SnapShow next = f.Shows.Last();
        Assert.Equal(3, next.Seq);
        Assert.Equal("right third", next.Payload["zone"]!["name"]!.GetValue<string>());
        Assert.Equal(0, next.Payload["left"]!.GetValue<int>());
        Assert.Equal(new[] { "300" }, p.Session!.Cands.Select(c => c.Hwnd));
        Assert.Equal(new[] { "200" }, p.Session!.Placed);
        Assert.Empty(f.Of("focus"));

        await p.PickAsync(3, hwnd: "300");                                    // the zones run out: the last window gets the focus
        Assert.Null(p.Session);
        Assert.Equal("300", Assert.Single(f.Of("focus")).Args["hwnd"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pick_of_a_window_Snap_Assist_did_not_offer_is_refused_and_a_stale_seq_is_ignored()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        SnapPickResult forged = await p.PickAsync(2, hwnd: "999");
        Assert.Equal("that window is not one Snap Assist offered", forged.Error);
        Assert.Equal(SnapPanel.NotOffered, (await p.PickAsync(2)).Error);
        Assert.True((await p.PickAsync(1, hwnd: "200")).Stale);
        Assert.Single(f.Of("snap.zone"));
        Assert.Equal(2, p.Session!.Seq);
    }

    [Fact]
    public async Task A_refused_Snap_Assist_pick_says_could_not_snap_the_app_or_that_window()
    {
        var f = Rig();
        int n = 0;
        f.Answers["snap.zone"] = _ => ++n == 1 || n == 3 ? Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft))
            : n == 2 ? Ok(Landed("null", hwnd: "200", ok: false, extra: ",\"reason\":\"it is minimized to the tray\""))
            : PlatformReply.Fail(PlatformOutcome.Failed, "the sidecar is busy");
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        SnapPickResult r = await p.PickAsync(2, hwnd: "200");
        Assert.Equal("Could not snap Code", r.Notice!.Title);
        Assert.Equal("It is minimized to the tray.", r.Notice.Text);
        Assert.Null(p.Session);

        await p.PickerAsync();
        await p.PickAsync(p.Session!.Seq, "thirds", 0);                      // n == 3: Snap Assist again
        SnapPickResult none = await p.PickAsync(p.Session!.Seq, hwnd: "200"); // n == 4: no answer at all
        Assert.Equal("Could not snap that window", none.Notice!.Title);
        Assert.Equal("The sidecar is busy.", none.Notice.Text);
        Assert.Contains("Snap Assist: the sidecar is busy", f.Log);
    }

    [Fact]
    public async Task A_Win_arrow_snap_with_an_empty_half_opens_Snap_Assist_and_with_nothing_to_offer_gives_the_focus_back()
    {
        var f = Rig();
        const string RightHalf = "[{\"zone\":{\"name\":\"right\",\"grid\":[2,1],\"cell\":[1,0,1,1]},\"frame\":{\"x\":960,\"y\":0,\"w\":960,\"h\":1040}}]";
        f.Answers["snap.move"] = _ => Ok(Landed(F(0, 0, 960, 1040), hwnd: "100", assist: RightHalf));
        SnapPanel p = f.Panel();
        await p.ArrowAsync("Win+Left");
        SnapShow show = Assert.Single(f.Shows);
        Assert.Equal("assist", show.Mode);
        Assert.Equal("right half", show.Payload["zone"]!["name"]!.GetValue<string>());
        Assert.Equal(SnapBox.Xywh(960, 0, 960, 1040), show.Rect);

        // While Snap Assist is open, Win+Up moves the window it was for and closes Assist.
        await p.ArrowAsync("Win+Up");
        Assert.Equal("100", f.Of("snap.move")[1].Args["hwnd"]!.GetValue<string>());
        Assert.Contains("#1 assist: ended: another snap key", f.Log);

        // Nothing else open: no Assist, the snapped window keeps the keyboard.
        f.Answers["windows"] = _ => Ok("[{\"hwnd\":\"100\",\"title\":\"plan.txt - Code\",\"exe\":\"code.exe\"}]");
        p.End("test reset");
        int shows = f.Shows.Count;
        await p.ArrowAsync("Win+Left");
        Assert.Equal(shows, f.Shows.Count);
        Assert.Equal("100", f.Of("focus").Last().Args["hwnd"]!.GetValue<string>());
    }

    [Fact]
    public async Task Snap_Assist_with_no_window_list_logs_why_and_opens_nothing()
    {
        var f = Rig();
        f.Answers["windows"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, "timed out after 1500 ms");
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        Assert.Contains("Snap Assist has no window list: timed out after 1500 ms", f.Log);
        Assert.Null(p.Session);
        Assert.Equal("100", f.Of("focus").Last().Args["hwnd"]!.GetValue<string>());
    }

    [Fact]
    public void Zone_words_name_halves_and_quarters_by_name_layout_zones_by_their_cell_else_this_space()
    {
        Assert.Equal("right half", SnapPanel.ZoneWords(new SnapEmptyZone("right", new[] { 2, 1 }, new[] { 1, 0, 1, 1 }, default)));
        Assert.Equal("bottom-left quarter", SnapPanel.ZoneWords(new SnapEmptyZone("bl", new[] { 2, 2 }, new[] { 0, 1, 1, 1 }, default)));
        Assert.Equal("left two thirds", SnapPanel.ZoneWords(new SnapEmptyZone("grid:3x1:0,0,2,1", new[] { 3, 1 }, new[] { 0, 0, 2, 1 }, default)));
        Assert.Equal("third column", SnapPanel.ZoneWords(new SnapEmptyZone(null, new[] { 4, 1 }, new[] { 2, 0, 1, 1 }, default)));
        Assert.Equal("this space", SnapPanel.ZoneWords(new SnapEmptyZone(null, new[] { 5, 1 }, new[] { 0, 0, 1, 1 }, default)));
        Assert.Equal("this space", SnapPanel.ZoneWords(null));
    }

    [Fact]
    public void A_malformed_empty_zone_in_the_answer_is_skipped()
    {
        JsonElement x = JsonDocument.Parse("{\"assist\":[{\"zone\":{\"grid\":[2,1],\"cell\":[1,0,1,1]}},{\"zone\":{\"grid\":[2],\"cell\":[1,0,1,1]},\"frame\":" + F(1, 2, 3, 4) +
                                           "},{\"zone\":{\"name\":\"right\",\"grid\":[2,1],\"cell\":[1,0,1,1]},\"frame\":" + F(960, 0, 960, 1040) + "}]}").RootElement;
        SnapEmptyZone z = Assert.Single(SnapPanel.EmptyZones(x));
        Assert.Equal("right", z.Name);
        Assert.Equal(SnapBox.Xywh(960, 0, 960, 1040), z.Frame);
    }

    // ------------------------------------------------------------ dismiss, switch-off, the keyboard (SNAP-076, 077, 093)

    [Fact]
    public async Task Esc_hands_the_keyboard_back_to_the_window_and_a_click_away_does_not()
    {
        var f = Rig();
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        Assert.True(await p.DismissAsync(1, "esc"));
        Assert.Equal("Esc", p.LastEnded);
        Assert.Equal("100", Assert.Single(f.Of("focus")).Args["hwnd"]!.GetValue<string>());

        await p.PickerAsync();
        Assert.False(await p.DismissAsync(1, "esc"));                         // stale
        Assert.True(await p.DismissAsync(2, "blur"));
        Assert.Equal("click-away", p.LastEnded);
        Assert.Single(f.Of("focus"));
        Assert.Equal(new[] { 1, 2 }, f.Hides);
    }

    [Fact]
    public async Task Switching_snapping_off_ends_Snap_Assist_and_stopping_ends_the_picker()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        Assert.Equal("assist", p.Session!.Mode);
        p.Off();
        Assert.Null(p.Session);
        Assert.Equal("snapping was turned off", p.LastEnded);
        Assert.Contains(2, f.Hides);

        await p.PickerAsync();
        p.Stop();
        Assert.Equal("Mantle is putting Windows back", p.LastEnded);
        Assert.Null(p.Session);
    }

    [Fact]
    public async Task A_notice_replaces_an_open_panel()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(0, 0, 640, 1040), assist: TwoThirdsLeft));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        await p.PickAsync(1, "thirds", 0);
        f.Answers["snap.info"] = _ => Ok(Info(resizable: false));
        await p.PickerAsync();                                                // Win+Z during Assist: asks about Assist's window
        Assert.Equal("100", f.Of("snap.info").Last().Args["hwnd"]!.GetValue<string>());
        Assert.Null(p.Session);
        Assert.Equal("replaced", p.LastEnded);
    }

    [Fact]
    public async Task The_keyboard_check_asks_the_focus_ladder_and_says_when_Windows_kept_the_keyboard()
    {
        var f = Rig();
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        Assert.Null(await p.NoKeysAsync(1, "555", () => true));
        Assert.Empty(f.Of("focus"));
        Assert.Equal(SnapPanel.NoKeys, await p.NoKeysAsync(1, "555", () => false));
        var focus = Assert.Single(f.Of("focus"));
        Assert.Equal("555", focus.Args["hwnd"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromMilliseconds(1500), focus.Timeout);
        Assert.Contains("#1: no keyboard for the panel (focus was asked for and not given)", f.Log);
        Assert.Equal("Windows kept the keyboard on another window, so click your choice here.", SnapPanel.NoKeys);
        Assert.Null(await p.NoKeysAsync(9, "555", () => false));             // stale
        Assert.Null(await new Fake().Panel(test: true).NoKeysAsync(1, "555", () => false));
        Assert.Equal(60, SnapPanel.NoKeysAfterMs);
    }

    [Fact]
    public async Task A_test_run_offers_no_windows_and_never_focuses()
    {
        var f = Rig();
        f.Answers["snap.move"] = _ => Ok(Landed(F(0, 0, 960, 1040), assist: "[{\"zone\":{\"name\":\"right\",\"grid\":[2,1],\"cell\":[1,0,1,1]},\"frame\":" + F(960, 0, 960, 1040) + "}]"));
        SnapPanel p = f.Panel(test: true);
        await p.ArrowAsync("Win+Left");                                       // no target in a test: refused before any call
        Assert.Empty(f.Calls);
        List<SnapCandidate> c = await p.CandidatesAsync("100", Array.Empty<string>());
        Assert.Empty(c);
        Assert.Empty(f.Of("windows"));
    }

    // ------------------------------------------------------------ SAFETY: every window a pick moves (never strand)

    [Fact]
    public async Task Safety_a_Win_Z_zone_landing_off_every_live_monitor_is_put_back_once_and_gets_no_Snap_Assist()
    {
        // Multi-monitor: the answer says monitor P, but P has gone (snap.monitors lists only A).
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(5000, 0, 640, 1040), monitor: MonP, assist: TwoThirdsLeft));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "thirds", 0);
        Assert.True(r.Rescue!.Changed);
        Assert.Null(r.RescueError);
        var place = Assert.Single(f.Of("layouts.place"));
        Assert.Equal("100", place.Args["hwnd"]!.GetValue<string>());
        Assert.Equal("normal", place.Args["state"]!.GetValue<string>());
        Assert.Equal("{\"x\":1280,\"y\":0,\"w\":640,\"h\":1040}", place.Args["frame"]!.ToJsonString());
        Assert.Equal(77, place.Args["pid"]!.GetValue<int>());
        Assert.True(SnapSafety.TitleBarOnLive(r.Rescue.Frame, new[] { new SnapScreen("A", SnapBox.Xywh(0, 0, 1920, 1080), SnapBox.Xywh(0, 0, 1920, 1040)) }));
        Assert.Empty(f.Of("windows"));                                       // the zone is not where Assist's zones were measured
        Assert.Null(p.Session);
        Assert.Equal("100", f.Of("focus").Last().Args["hwnd"]!.GetValue<string>());
    }

    [Fact]
    public async Task Safety_a_Win_Z_put_back_that_fails_is_a_notice_never_silent()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(5000, 0, 640, 1040), assist: TwoThirdsLeft));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        f.Answers["layouts.place"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, "access is denied");
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "thirds", 0);
        Assert.Equal("access is denied", r.RescueError);
        Assert.Equal("Could not put Code back on the screen", r.Notice!.Title);
        Assert.Equal("Access is denied.", r.Notice.Text);
        Assert.Contains(f.Notices, n => n.Title == "Could not put Code back on the screen");
        Assert.Empty(f.Of("windows"));
    }

    [Fact]
    public async Task Safety_multi_monitor_a_Snap_Assist_pick_on_the_first_windows_other_monitor_is_judged_there_and_left_alone()
    {
        var f = Rig();
        string half = "[{\"zone\":{\"name\":\"right\",\"grid\":[2,1],\"cell\":[1,0,1,1]},\"frame\":" + F(3200, 0, 1280, 1392) + "}]";
        f.Answers["snap.move"] = _ => Ok(Landed(F(1920, 0, 1280, 1392), monitor: MonP, assist: half));
        f.Answers["snap.zone"] = _ => Ok(Landed(F(3200, 0, 1280, 1392), hwnd: "200", monitor: MonP));
        SnapPanel p = f.Panel();
        await p.ArrowAsync("Win+Left");
        SnapPickResult r = await p.PickAsync(1, hwnd: "200");
        Assert.Equal("100", f.Of("snap.zone")[0].Args["of"]!.GetValue<string>());
        Assert.Null(r.Rescue);                                               // on its own monitor: no extra call at all
        Assert.Empty(f.Of("snap.monitors"));
        Assert.Empty(f.Of("layouts.place"));
        Assert.Null(r.Notice);
    }

    [Fact]
    public async Task Safety_multi_monitor_a_Snap_Assist_pick_hanging_off_the_other_monitor_is_fitted_back_onto_it()
    {
        var f = Rig();
        f.Answers["snap.move"] = _ => Ok(Landed(F(0, 0, 960, 1040), assist: "[{\"zone\":{\"name\":\"right\",\"grid\":[2,1],\"cell\":[1,0,1,1]},\"frame\":" + F(960, 0, 960, 1040) + "}]"));
        f.Answers["snap.zone"] = _ => Ok(Landed(F(4470, 0, 1280, 1392), hwnd: "200", monitor: MonP));   // 10 px left on P
        SnapPanel p = f.Panel();
        await p.ArrowAsync("Win+Left");
        SnapPickResult r = await p.PickAsync(1, hwnd: "200");
        Assert.True(r.Rescue!.Changed);
        Assert.Equal("P", r.Rescue.Screen!.Id);
        var place = Assert.Single(f.Of("layouts.place"));
        Assert.Equal("200", place.Args["hwnd"]!.GetValue<string>());
        Assert.Equal("{\"x\":3200,\"y\":0,\"w\":1280,\"h\":1392}", place.Args["frame"]!.ToJsonString());
    }

    [Fact]
    public async Task Safety_DPI_too_little_title_bar_at_150_percent_is_put_back_but_the_same_landing_at_100_percent_is_left()
    {
        const string MonH = "{\"id\":\"H\",\"bounds\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1080},\"work\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1040},\"dpi\":144,\"scale\":1.5,\"primary\":true}";
        // 80 px of title bar across: under the 96 px (64 DIP at 144 dpi) a hand needs, over the 64 px at 96 dpi.
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed(F(1840, 0, 960, 1040), monitor: MonH));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonH + "]}");
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "halves", 1);
        Assert.True(r.Rescue!.Changed);
        Assert.Equal("{\"x\":960,\"y\":0,\"w\":960,\"h\":1040}", Assert.Single(f.Of("layouts.place")).Args["frame"]!.ToJsonString());

        var g = Rig();
        g.Answers["snap.zone"] = _ => Ok(Landed(F(1840, 0, 960, 1040), monitor: MonA));
        SnapPanel q = g.Panel();
        await q.PickerAsync();
        SnapPickResult s = await q.PickAsync(1, "halves", 1);
        Assert.Null(s.Rescue);                                               // safe on its own monitor: never moved
        Assert.Empty(g.Of("snap.monitors"));
        Assert.Empty(g.Of("layouts.place"));
    }

    [Fact]
    public async Task Safety_a_Win_arrow_snap_that_had_to_be_put_back_opens_no_Snap_Assist()
    {
        var f = Rig();
        f.Answers["snap.move"] = _ => Ok(Landed(F(5000, 0, 960, 1040), assist: "[{\"zone\":{\"name\":\"right\",\"grid\":[2,1],\"cell\":[1,0,1,1]},\"frame\":" + F(960, 0, 960, 1040) + "}]"));
        f.Answers["snap.monitors"] = _ => Ok("{\"monitors\":[" + MonA + "]}");
        SnapArrowResult r = await f.Panel().ArrowAsync("Win+Left");
        Assert.True(r.Rescued);
        Assert.Single(f.Of("layouts.place"));
        Assert.Empty(f.Of("windows"));
        Assert.Empty(f.Shows);
    }

    [Fact]
    public async Task Safety_a_pick_that_moved_nothing_by_rect_is_not_checked_or_moved()
    {
        var f = Rig();
        f.Answers["snap.zone"] = _ => Ok(Landed("null"));
        SnapPanel p = f.Panel();
        await p.PickerAsync();
        SnapPickResult r = await p.PickAsync(1, "halves", 0);
        Assert.Null(r.Rescue);
        Assert.Empty(f.Of("snap.monitors"));
        Assert.Empty(f.Of("layouts.place"));
    }
}
