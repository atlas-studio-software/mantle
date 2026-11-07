using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Features.Icons;
using Mantle.Core.Features.Pinned;
using Mantle.Core.Features.WindowList;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

// Ported from lib/main-guard.test.js (groupRunning / buildPinned), lib/icon-fresh.test.js, lib/winlist-icons.test.js
// neighbours and ui/bar/bar.page.test.js (dock clicks): BAR-192, BAR-197..201, BAR-259, BAR-286..290, BAR-297, BAR-300..303.
public class TaskStripTests
{
    static JsonElement J(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    static WinEntry W(string hwnd, string? exe, string? aumid = null, bool fg = false, bool min = false, string title = "t", string? icon = "data:image/png;base64,AA", bool pending = false)
        => new(hwnd, title, exe, 7, aumid, null, min, fg, icon, icon != null ? "exe" : null, pending);

    [Fact]
    public void Mantle_own_shell_windows_are_never_running_apps_but_its_app_windows_are()
    {
        Assert.True(RunningWindows.IsMantleWindow(W("1", @"C:\x\electron.exe", title: "Mantle bar")));
        Assert.True(RunningWindows.IsMantleWindow(W("1", @"C:\x\MantleNative.exe", title: "Mantle bar")));
        Assert.True(RunningWindows.IsMantleWindow(W("1", @"C:\x\MantleNative.exe", title: "Mantle pop-up: More")));
        Assert.True(RunningWindows.IsMantleWindow(W("1", @"C:\x\a.exe", aumid: "COM.OWENSILVA.MANTLE")));
        Assert.False(RunningWindows.IsMantleWindow(W("1", @"C:\x\electron.exe", title: "Some page")));
        Assert.False(RunningWindows.IsMantleWindow(W("1", @"C:\x\electron.exe", title: "Mantle Settings")));
        var l = RunningWindows.WithoutMantle(J("[{\"hwnd\":\"1\",\"exe\":\"C:\\\\e\\\\electron.exe\",\"title\":\"Mantle desktop\"},{\"hwnd\":2,\"exe\":\"C:\\\\n.exe\",\"title\":\"N\",\"pid\":5}]"));
        Assert.Single(l!);
        Assert.Equal("2", l![0].Hwnd);
        Assert.Equal(5, l[0].Pid);
        Assert.Null(RunningWindows.WithoutMantle(J("{\"not\":\"a list\"}")));
    }

    [Fact]
    public void Windows_group_aumid_first_exe_second_and_learn_the_other_identifier()
    {
        var g = RunningWindows.Group(new[] {
            // main.js keys a window by its aumid, else its exe: the first window carries only the aumid, the second
            // both, so the group learns the exe (a window with no aumid would start a group of its own, as in Electron)
            W("1", null, aumid: "com.squirrel.Discord", icon: null),
            W("2", @"C:\D\Discord.exe", aumid: "com.squirrel.Discord", fg: true),
            W("3", @"C:\Windows\ApplicationFrameHost.exe", aumid: "Calc_8wekyb3d8bbwe!App"),
            W("4", @"C:\Windows\ApplicationFrameHost.exe", aumid: "Mail_8wekyb3d8bbwe!App"),
        });
        Assert.Equal(3, g.Count);
        Assert.Equal("com.squirrel.discord", g[0].Key);
        Assert.Equal(@"c:\d\discord.exe", g[0].Exe);   // learned from the second window
        Assert.Equal(@"C:\D\Discord.exe", g[0].ExeRaw);
        Assert.Equal("data:image/png;base64,AA", g[0].Icon);
        Assert.Equal(2, g[0].Windows.Count);
        Assert.Equal(new[] { "calc_8wekyb3d8bbwe!app", "mail_8wekyb3d8bbwe!app" }, g.Skip(1).Select(x => x.Key));
        Assert.Equal("Notepad", RunningWindows.NameForExe(@"C:\Windows\notepad.exe", "x"));
    }

    static Pin MkPin(string id, string? exe, int order, string? aumid = null) => new() { Id = id, Name = id, Exe = exe, Aumid = aumid, Order = order, IconPng = "data:image/png;base64,PP" };

    [Fact]
    public async Task Pins_claim_their_group_by_aumid_or_exe_and_running_apps_follow_in_windows_order()
    {
        var list = await TaskStripBuilder.BuildAsync(new TaskStripBuilder.Input {
            Running = new[] {
                W("10", @"C:\x\other.exe"),
                W("11", @"C:\D\Discord.exe", aumid: "com.squirrel.Discord", fg: true),
                W("12", @"C:\x\note.exe", min: true, fg: true),
            },
            Pins = new PinnedResult(true, null, new[] { MkPin("note.lnk", @"c:\x\note.exe", 1), MkPin("discord.lnk", @"c:\d\update.exe", 0, "com.squirrel.Discord") }),
        });
        Assert.True(list.Ok);
        Assert.Equal(new[] { "discord.lnk", "note.lnk", "running:c:\\x\\other.exe" }, list.Items.Select(i => i.Id));
        Assert.Equal(new[] { "pinned", "pinned", "running" }, list.Items.Select(i => i.Kind));
        Assert.True(list.Items[0].Active);
        Assert.Single(list.Items[0].Windows);
        Assert.False(list.Items[1].Active);          // minimised is never active
        Assert.True(list.Items[1].Running);
        Assert.Equal("Other", list.Items[2].Name);
    }

    [Fact]
    public async Task Icons_pending_show_none_and_a_failed_pins_read_keeps_the_running_apps()
    {
        var list = await TaskStripBuilder.BuildAsync(new TaskStripBuilder.Input {
            Running = new[] { W("1", @"C:\x\a.exe", icon: null, pending: true) },
            Pins = new PinnedResult(false, "Windows' pinned-apps folder is missing", Array.Empty<Pin>()),
        });
        Assert.False(list.Ok);
        Assert.Equal("the pinned list is incomplete: Windows' pinned-apps folder is missing", list.Reason);
        Assert.Null(list.Items[0].Icon);
        Assert.Equal("still loading", list.Items[0].IconReason);
    }

    sealed class Acts : ITaskActions
    {
        public readonly List<string> Did = new();
        public string? FocusErr;
        public ValueTask<string?> FocusAsync(TaskWindow w) { Did.Add("focus " + w.Hwnd); return ValueTask.FromResult(FocusErr); }
        public ValueTask<string?> MinimizeAsync(TaskWindow w) { Did.Add("minimize " + w.Hwnd); return ValueTask.FromResult<string?>(null); }
        public ValueTask<string?> CloseAsync(TaskWindow w) { Did.Add("close " + w.Hwnd); return ValueTask.FromResult<string?>(null); }
        public ValueTask<TaskOpenResult> LaunchAsync(TaskItem item, bool fresh) { Did.Add("launch " + item.Id + (fresh ? " fresh" : "")); return ValueTask.FromResult(TaskOpen.DryRun(item, fresh)); }
    }

    static TaskItem Item(string id, params TaskWindow[] w) => new(id, "App " + id, "pinned", null, null, null, "none", w) { Pin = new Pin { Id = id, LnkPath = @"C:\p\" + id + ".lnk" } };

    [Fact]
    public async Task A_click_launches_focuses_minimises_or_lists_like_the_windows_taskbar()
    {
        var list = new TaskList(true, null, new[] {
            Item("none"),
            Item("one", new TaskWindow("5", 1, "a", false, false)),
            Item("front", new TaskWindow("6", 1, "b", true, false)),
            Item("two", new TaskWindow("7", 1, "c", false, false), new TaskWindow("8", 1, "d", false, true)),
        });
        var a = new Acts();
        var r = await TaskOpen.OpenAsync(list, "none", false, a);
        Assert.Equal("dry-run", r.Action);
        Assert.Equal(@"dry run: would open C:\p\none.lnk", r.Reason);
        Assert.Equal("focus", (await TaskOpen.OpenAsync(list, "one", false, a)).Action);
        Assert.Equal("minimize", (await TaskOpen.OpenAsync(list, "front", false, a)).Action);
        var two = await TaskOpen.OpenAsync(list, "two", false, a);
        Assert.Equal("list", two.Action);
        Assert.Equal(2, two.Windows!.Count);
        Assert.Equal(@"dry run: would another copy of open C:\p\two.lnk".Replace("would another copy of open", "would another copy of open"), (await TaskOpen.OpenAsync(list, "two", true, a)).Reason);
        Assert.Equal(new[] { "launch none", "focus 5", "minimize 6", "launch two fresh" }, a.Did);
        a.FocusErr = "access denied";
        Assert.Equal("App one is running but Mantle could not focus it: access denied", (await TaskOpen.OpenAsync(list, "one", false, a)).Reason);
        Assert.Contains("not in the list Mantle last built", (await TaskOpen.OpenAsync(list, "zzz", false, a)).Reason);
        Assert.Contains("positive whole number", (await TaskOpen.WindowAsync(list, "-3", "focus", a)).Reason);
        Assert.Contains("does not know the action", (await TaskOpen.WindowAsync(list, "5", "explode", a)).Reason);
    }

    [Fact]
    public void Icon_freshness_restats_every_2_s_and_a_changed_file_is_sticky_stale()
    {
        long size = 10;
        (bool, bool, double, long) Stat(string _) => (true, true, 1000, size);
        var e = IconFresh.Stamp("ico", null, @"C:\a.exe", 0, Stat);
        Assert.Equal("1000:10", e.Stamp);
        Assert.True(IconFresh.IsFresh(e, @"C:\a.exe", 1000, stat: Stat));
        size = 11;
        Assert.True(IconFresh.IsFresh(e, @"C:\a.exe", 1999, stat: Stat));     // not re-stat'ed within 2 s
        Assert.False(IconFresh.IsFresh(e, @"C:\a.exe", 2500, stat: Stat));
        size = 10;
        Assert.False(IconFresh.IsFresh(e, @"C:\a.exe", 5000, stat: Stat));    // sticky
        var s = IconFresh.Stamp("ico", null, @"shell:appsfolder\X", 0);
        Assert.Null(s.Stamp);
        Assert.False(IconFresh.IsFresh(s, @"shell:appsfolder\X", 60000, recheckMs: 60000));
        Assert.Equal("", IconResolver.StoreAumidOf(@"shell:AppsFolder\not an id"));
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", IconResolver.StoreAumidOf(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
    }

    [Fact]
    public async Task An_unreachable_sidecar_never_takes_an_icon_off_the_bar()
    {
        bool down = false; double now = 0; int asked = 0;
        var r = new IconResolver((verb, args, _) => { asked++; return ValueTask.FromResult(J("{\"icon\":\"data:image/png;base64,II\"}")); },
            () => now, () => down ? "it is not running" : null, _ => (true, true, 1, 1));
        Assert.Equal("data:image/png;base64,II", (await r.ForAsync(new[] { null, @"C:\a.exe" })).Icon);
        now = 61000; down = true;
        Assert.Equal("data:image/png;base64,II", (await r.ForAsync(new[] { @"C:\a.exe" })).Icon);
        Assert.Equal(1, asked);
        Assert.Contains("the sidecar cannot be asked", (await r.ForAsync(new[] { @"C:\b.exe" })).Reason);
    }

    [Fact]
    public void Running_only_apps_keep_their_first_seen_place_when_focus_or_minimise_reorders_the_windows()
    {
        var o = new FirstSeenOrder();
        static TaskItem R(string id) => new("running:" + id, id, "running", null, null, null, "x", new[] { new TaskWindow("1", 1, id, false, false) });
        static TaskItem P(string id) => new(id, id, "pinned", null, null, null, "x", Array.Empty<TaskWindow>());
        static string[] Ids(List<TaskItem> l) => l.Select(x => x.Id).ToArray();
        Assert.Equal(new[] { "p", "running:a", "running:b", "running:c" }, Ids(o.Apply(new() { P("p"), R("a"), R("b"), R("c") }, true)));
        // the sidecar's z-order after c is focused and a minimised: the strip does not move
        Assert.Equal(new[] { "p", "running:a", "running:b", "running:c" }, Ids(o.Apply(new() { P("p"), R("c"), R("b"), R("a") }, true)));
        // b closes and d opens: d goes last; b back again goes after d
        Assert.Equal(new[] { "p", "running:a", "running:c", "running:d" }, Ids(o.Apply(new() { P("p"), R("d"), R("c"), R("a") }, true)));
        Assert.Equal(new[] { "p", "running:a", "running:c", "running:d", "running:b" }, Ids(o.Apply(new() { P("p"), R("b"), R("d"), R("c"), R("a") }, true)));
        // a running list that was not read cleanly forgets nobody
        o.Apply(new() { P("p") }, false);
        Assert.Equal(new[] { "running:a", "running:c" }, Ids(o.Apply(new() { R("c"), R("a") }, true)));
    }

    [Fact]
    public void A_windows_push_moves_the_active_mark_at_once_between_the_rate_limited_rebuilds()
    {
        var host = new ManualScheduler();
        var p = new FakePlatform();
        p.SetStatus(PlatformState.Ok, "sidecar running", new PlatformInfo(9, true, "0"));
        p.Answer("windows", "[]");
        var got = new List<TaskList>();
        var marks = new List<TaskList>();
        using var d = new TestDir("marks");
        using var svc = new TaskStripService(new TaskStripOptions {
            Platform = p, Host = host, PinsDir = null, PinsNote = "no pins", UserData = d.Path,
            Publish = got.Add, Marks = marks.Add, WatchPins = false,
        });
        svc.Start();
        host.Advance(0);
        static string Two(bool a, bool b, bool aMin = false) =>
            "{\"list\":[{\"hwnd\":\"5\",\"exe\":\"C:\\\\x\\\\a.exe\",\"title\":\"A\",\"foreground\":" + (a ? "true" : "false") + ",\"minimized\":" + (aMin ? "true" : "false") + ",\"icon\":\"data:image/png;base64,AA\"}," +
            "{\"hwnd\":\"6\",\"exe\":\"C:\\\\x\\\\b.exe\",\"title\":\"B\",\"foreground\":" + (b ? "true" : "false") + ",\"icon\":\"data:image/png;base64,BB\"}]}";
        p.Raise("windows", Two(true, false));
        host.Advance(1100);
        Assert.Equal(2, got.Count);
        Assert.True(got[1].Items[0].Active);
        Assert.False(got[1].Items[1].Active);
        Assert.Empty(marks);                                  // nothing to re-mark before the first list with windows

        p.Raise("windows", Two(false, true));                 // B comes to the front 100 ms after the rebuild
        Assert.Equal(2, got.Count);                           // the rebuild still waits for its second
        Assert.Single(marks);                                 // the marks do not (bar.js renderWindows)
        Assert.False(marks[0].Items[0].Active);
        Assert.True(marks[0].Items[1].Active);
        Assert.Same(marks[0], svc.Last);                      // a click decides from what the user sees

        p.Raise("windows", Two(false, true));                 // the same marks again: nothing sent
        Assert.Single(marks);
        p.Raise("windows", Two(true, false, aMin: true));     // a minimised window is never the active one
        Assert.Equal(2, marks.Count);
        Assert.False(marks[1].Items[0].Active);
        Assert.True(marks[1].Items[0].Windows[0].Minimized);
        Assert.False(marks[1].Items[1].Active);
        host.Advance(1000);
        Assert.Equal(3, got.Count);
        Assert.False(got[2].Items[0].Active);
    }

    [Fact]
    public void The_service_reads_the_windows_push_rate_limits_its_pushes_and_says_a_broken_list()
    {
        var host = new ManualScheduler();
        var p = new FakePlatform();
        p.SetStatus(PlatformState.Ok, "sidecar running", new PlatformInfo(9, true, "0"));
        p.Answer("windows", "[]");
        p.Answer("icon", "{\"icon\":null}");
        var got = new List<TaskList>();
        var problems = new List<string?>();
        using var d = new TestDir("strip");
        using var svc = new TaskStripService(new TaskStripOptions {
            Platform = p, Host = host, PinsDir = null, PinsNote = "a test run shows no pins", UserData = d.Path,
            Publish = got.Add, WindowsProblem = problems.Add, WatchPins = false,
        });
        svc.Start();
        host.Advance(0);
        Assert.Single(got);                                   // the first push goes at once, from one `windows` call (BAR-286)
        Assert.Contains(p.Calls, c => c.Verb == "windows");
        Assert.Contains("a test run shows no pins", got[0].Reason);
        p.Raise("windows", "{\"ev\":\"windows\",\"list\":[{\"hwnd\":\"5\",\"exe\":\"C:\\\\x\\\\a.exe\",\"title\":\"A\",\"foreground\":true,\"icon\":\"data:image/png;base64,AA\"}]}");
        p.Raise("windows", "{\"ev\":\"windows\",\"list\":[{\"hwnd\":\"5\",\"exe\":\"C:\\\\x\\\\a.exe\",\"title\":\"A2\",\"icon\":\"data:image/png;base64,AA\"}]}");
        host.Advance(500);
        Assert.Single(got);                                   // at most once a second (BAR-297)
        host.Advance(600);
        Assert.Equal(2, got.Count);
        Assert.True(got[1].Items.Count == 1, $"the second push has {got[1].Items.Count} items (ok {got[1].Ok}, reason: {got[1].Reason ?? "none"}; windows pushes {svc.WindowPushes}, builds {svc.Builds})");
        Assert.Equal("A2", got[1].Items.Single().Windows.Single().Title);
        p.Raise("windows", "{\"ev\":\"windows\",\"list\":null}");
        Assert.Equal("the list of open windows could not be read, so the running marks may be out of date", problems.Last());
    }
}
