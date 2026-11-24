using System.Linq;
using System.Text.RegularExpressions;
using Mantle.Core.Features.Switcher;
using Xunit;

namespace Mantle.Core.Tests.Features.Switcher;

// lib/switcher.test.js parking(), noticeCases() and boxCases() (cleanBoxes and the log lines), ported
// (SWI-043, SWI-088, SWI-114..SWI-120 (the words and the rule), SWI-055 (privacy of the switch line)).
public class SwitcherWordsTests
{
    static readonly PhysRect A = new(0, 0, 1920, 1080);
    static readonly PhysRect B = new(0, -1080, 1920, 1080);
    static readonly PhysRect C = new(0, 1080, 1920, 1080);
    static readonly PhysRect D = new(-1920, 0, 1920, 1080);
    static readonly PhysRect E = new(1920, 0, 1920, 1080);

    static bool Hits(ParkPlace p, int w, int h, PhysRect d) => p.X < d.X + d.W && p.X + w > d.X && p.Y < d.Y + d.H && p.Y + h > d.Y;

    [Fact]
    public void Parking_goes_above_else_below_left_right_else_far_away()
    {
        var one = SwitcherWords.ParkSpot(A, 800, 243, new[] { A });
        Assert.Equal(new ParkPlace(0, -243 - 64, "above"), one);
        var two = SwitcherWords.ParkSpot(A, 800, 243, new[] { A, B });
        Assert.Equal("below", two.Side);
        Assert.Equal(1080 + 64, two.Y);
        var three = SwitcherWords.ParkSpot(A, 800, 243, new[] { A, B, C, D });
        Assert.Equal("right of", three.Side);
        Assert.Equal(1920 + 64, three.X);
        var left = SwitcherWords.ParkSpot(A, 800, 243, new[] { A, B, C });
        Assert.Equal(new ParkPlace(-800 - 64, 0, "left of"), left);
        var boxed = SwitcherWords.ParkSpot(A, 800, 243, new[] { A, B, C, D, E });
        Assert.Equal(new ParkPlace(-32000, -32000, "far from"), boxed);
        var cases = new[] { (one, new[] { A }), (two, new[] { A, B }), (three, new[] { A, B, C, D }), (boxed, new[] { A, B, C, D, E }) };
        Assert.True(cases.All(c => !c.Item2.Any(d => Hits(c.Item1, 800, 243, d))), "no parking spot overlaps a monitor of its own layout");
    }

    // Machinery words that must never reach the notice on screen (no jargon, no error codes).
    static readonly Regex Jargon = new(@"SetForegroundWindow|ladder|sidecar|hwnd|Win32Exception|threw|0x[0-9a-f]+|undefined|null|[0-9]{4,}", RegexOptions.IgnoreCase);
    static bool Clean(NoticeText n) => !Jargon.IsMatch(n.Title) && !Jargon.IsMatch(n.Text ?? "");
    const string Detail = "SetForegroundWindow was refused, then the focus ladder: could not raise window 1312: ...";

    [Fact]
    public void Hung_says_the_plain_sentence_and_what_to_do_never_the_API()
    {
        var n = SwitcherWords.NoticeWords("hung", "Task Manager", Detail);
        Assert.Equal("Task Manager isn't responding, so Windows wouldn't bring it to the front.", n.Title);
        Assert.Contains("Task Manager (Ctrl+Shift+Esc)", n.Text);
        Assert.True(Clean(n));
    }

    [Fact]
    public void Closed_is_one_sentence_with_no_second_line()
    {
        var n = SwitcherWords.NoticeWords("closed", "Notepad", "the window closed before Mantle could switch to it");
        Assert.Equal(new NoticeText("Notepad closed before Mantle could switch to it.", null), n);
    }

    [Fact]
    public void Refused_names_the_window_and_says_click_it_on_the_taskbar()
    {
        var n = SwitcherWords.NoticeWords("refused", "Visual Studio Code", Detail);
        Assert.Equal("Windows wouldn't bring Visual Studio Code to the front.", n.Title);
        Assert.Contains("taskbar", n.Text);
        Assert.True(Clean(n));
        Assert.Equal("Windows wouldn't bring that window to the front.", SwitcherWords.NoticeWords("refused", "", Detail).Title);
        Assert.StartsWith("That window isn't responding", SwitcherWords.NoticeWords("hung", null, Detail).Title);
    }

    [Fact]
    public void Any_other_cause_says_Mantle_could_not_and_never_the_exception()
    {
        var n = SwitcherWords.NoticeWords("error", "Paint", "switching threw Win32Exception: boom");
        Assert.Equal("Mantle couldn't switch to Paint.", n.Title);
        Assert.True(Clean(n));
        Assert.False(string.IsNullOrEmpty(n.Text));
        var none = SwitcherWords.NoticeWords(null, "Paint", null);
        Assert.Equal("Mantle couldn't switch to Paint.", none.Title);
        Assert.Equal("Try Alt+Tab again, or click it on the taskbar.", none.Text);
        Assert.True(Clean(none));
    }

    [Fact]
    public void The_name_is_the_pinned_apps_else_the_title_cut_at_48()
    {
        Assert.Equal("Word", SwitcherWords.NameFor("Word", "Secret plans.docx - Word"));
        Assert.Equal("Secret plans.docx - Word", SwitcherWords.NameFor(null, "  Secret plans.docx - Word "));
        string t48 = new string('t', 48), t49 = new string('t', 49);
        Assert.Equal(t48, SwitcherWords.NameFor("", t48));
        Assert.Equal(new string('t', 47) + "…", SwitcherWords.NameFor(null, t49));
        Assert.Equal("", SwitcherWords.NameFor(null, null));
    }

    [Fact]
    public void A_notice_is_due_only_for_a_real_failure_with_something_to_switch_to()
    {
        Assert.True(SwitcherWords.NoticeDue(false, "refused", 3));
        Assert.True(SwitcherWords.NoticeDue(false, null, null));
        Assert.False(SwitcherWords.NoticeDue(true, null, 3));
        Assert.False(SwitcherWords.NoticeDue(false, "none", 3));
        Assert.False(SwitcherWords.NoticeDue(false, "refused", 0));
    }

    static readonly string[] List = { "1001", "1002" };
    static readonly PreviewBox Good = new("1001", 38, 38, 228, 143);

    [Fact]
    public void CleanBoxes_keeps_a_box_of_a_window_in_this_Alt_Tab()
    {
        var out_ = SwitcherWords.CleanBoxes(new[] { Good }, List);
        Assert.Single(out_);
        Assert.Equal(Good, out_[0]);
    }

    [Fact]
    public void CleanBoxes_refuses_other_windows_odd_handles_and_bad_numbers()
    {
        Assert.Empty(SwitcherWords.CleanBoxes(new[] { Good with { Hwnd = "999999" } }, List));
        Assert.Empty(SwitcherWords.CleanBoxes(new[] { Good with { Hwnd = "1001; x" } }, new[] { "1001; x" }));
        Assert.Empty(SwitcherWords.CleanBoxes(new[] { Good with { Hwnd = new string('1', 21) } }, new[] { new string('1', 21) }));
        Assert.Empty(SwitcherWords.CleanBoxes(new[]
        {
            Good with { W = double.NaN }, Good with { X = double.PositiveInfinity }, Good with { H = -5 }, Good with { Y = -1e4 - 1 },
            Good with { X = 1e5 + 1 }, Good with { Hwnd = null },
        }, List));
    }

    [Fact]
    public void CleanBoxes_one_box_per_window_nothing_from_nothing_and_a_flood_is_capped()
    {
        var out_ = SwitcherWords.CleanBoxes(new[] { Good, Good, new PreviewBox("1002", 1, 2, 3, 4) }, List);
        Assert.Equal(2, out_.Count);
        Assert.Equal("1002", out_[1].Hwnd);
        Assert.Empty(SwitcherWords.CleanBoxes(null, List));
        Assert.Empty(SwitcherWords.CleanBoxes(new PreviewBox?[] { null, null }, List));
        Assert.Empty(SwitcherWords.CleanBoxes(new[] { Good }, null));
        Assert.Single(SwitcherWords.CleanBoxes(Enumerable.Repeat(Good, 1000).ToArray(), List));
        // Only the first 400 are looked at: a good box at 401 is not drawn.
        var late = Enumerable.Repeat(Good with { Hwnd = "999999" }, 400).Append(Good).ToArray();
        Assert.Empty(SwitcherWords.CleanBoxes(late, List));
    }

    [Fact]
    public void The_switch_log_line_never_carries_the_title_or_the_handle()
    {
        string ok = SwitcherWords.ActivatedLine(new ActivatedEvent(7, true, "alt-up", "direct", 3));
        Assert.Equal("#7: switched (alt-up, direct, 3 ms)", ok);
        string bad = SwitcherWords.ActivatedLine(new ActivatedEvent(8, false, "click", Cause: "refused",
            Reason: "SetForegroundWindow was refused, then the focus ladder: could not raise window 1312"));
        Assert.Contains("could NOT switch (click, refused)", bad);
        Assert.Contains("focus ladder", bad);
        Assert.DoesNotContain("1312", bad);
        Assert.EndsWith("could not raise the window", bad);
        Assert.Equal("#9: could NOT switch (watchdog, no cause given): no reason given",
            SwitcherWords.ActivatedLine(new ActivatedEvent(9, false, "watchdog")));
    }
}
