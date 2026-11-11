using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.JumpList;
using Mantle.Core.Features.Pinned;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;
using PinOverlay = Mantle.Core.Features.Pinned.PinOverlay;

namespace Mantle.Core.Tests.WindowList;

/// lib/features/jumplist.js and _jumplist-pins.js pin/unpin/undoUnpin (lane native-r7-strip).
public sealed class JumpListTests
{
    static JumpApp App(string id = "Notes.lnk", string kind = "pinned", string? exe = @"C:\Apps\notes.exe", string? aumid = null, params string[] hwnds) =>
        new(id, "Notes", kind, exe, aumid, hwnds);

    [Fact]
    public void Plain_takes_out_hex_codes_and_internal_names()
    {
        Assert.Equal("Mantle's helper could not read the list for that window", JumpRules.Plain("the sidecar could not read the DestList for that hwnd (0x80070005)"));
        Assert.Equal("no list", JumpRules.Plain("no list (error 5)"));
        Assert.Null(JumpRules.Plain(null));
    }

    [Fact]
    public void Window_handles_are_numbers_unique_and_at_most_64()
    {
        Assert.Equal(new[] { "12", "0x1F" }, JumpRules.HwndsOf(new[] { "12", "", null, "12", "0x1F" }));
        Assert.Throws<ArgumentException>(() => JumpRules.HwndsOf(new[] { "12; rm" }));
        var many = Enumerable.Range(1, 65).Select(i => (string?)i.ToString()).ToArray();
        Assert.Contains("more than 64 windows", Assert.Throws<ArgumentException>(() => JumpRules.HwndsOf(many)).Message);
    }

    [Fact]
    public void Target_is_the_window_first_and_refuses_a_url_or_a_path_in_an_app_id()
    {
        var t = JumpRules.TargetOf(App(hwnds: new[] { "100", "200" }));
        Assert.Equal("100", t["hwnd"]!.ToString());
        Assert.Equal(@"C:\Apps\notes.exe", t["exe"]!.ToString());
        Assert.Null(t["app"]);
        Assert.Equal("Some.App!Main", JumpRules.TargetOf(App(exe: null, aumid: "Some.App!Main"))["app"]!.ToString());
        Assert.Contains("cannot start", Assert.Throws<ArgumentException>(() => JumpRules.TargetOf(App(exe: "https://x.example/a.exe"))).Message);
        Assert.Throws<ArgumentException>(() => JumpRules.TargetOf(App(exe: @"C:\a\..\b.exe")));
        Assert.Contains("does not know", Assert.Throws<ArgumentException>(() => JumpRules.TargetOf(App(exe: null, aumid: @"C:\x"))).Message);
        Assert.Equal(JumpRules.ExplorerAppId, JumpRules.TargetOf(App(exe: null, aumid: "Mantle.Files"))["app"]!.ToString());
        Assert.Contains("does not know which program", Assert.Throws<ArgumentException>(() => JumpRules.TargetOf(App(exe: null))).Message);
    }

    [Fact]
    public void Items_are_tokens_from_get_and_app_is_never_removed()
    {
        Assert.True(JumpRules.ItemOk("app"));
        Assert.True(JumpRules.ItemOk("c:1:2"));
        Assert.True(JumpRules.ItemOk("a:15"));
        Assert.False(JumpRules.ItemOk("app", remove: true));
        Assert.False(JumpRules.ItemOk("C:\\x"));
        Assert.False(JumpRules.ItemOk(null));
    }

    [Fact]
    public void The_list_words_say_none_error_partial_and_off()
    {
        var none = JumpRules.Parse(JsonDocument.Parse("{\"sections\":[],\"none\":\"this app doesn't publish one\"}").RootElement, 3);
        Assert.Equal(new[] { "No jump list: this app doesn't publish one" }, none.Quiet().Lines);
        var part = JumpRules.Parse(JsonDocument.Parse("{\"sections\":[{\"kind\":\"recent\",\"name\":\"Recent\",\"items\":[{\"title\":\"a.txt\",\"item\":\"a:0\",\"canRemove\":true},{\"sep\":true}]}],\"error\":\"the sidecar failed (0x80004005)\"}").RootElement, 3);
        Assert.Equal(2, part.Sections[0].Items.Count);
        Assert.True(part.Sections[0].Items[0].CanRemove);
        var q = part.Quiet();
        Assert.True(q.Warn);
        Assert.Equal("Part of the jump list could not be read: Mantle's helper failed", q.Lines.Single());
        var bad = new JumpListReply(false, false, "no answer", Array.Empty<JumpSection>(), null, null, null, 0);
        Assert.Equal("The jump list could not be read: no answer", bad.Quiet().Lines.Single());
        Assert.Equal("Pin to this list: tasks belong to the app and cannot be pinned or removed", JumpRules.SubWhy("tasks", false));
        Assert.Equal("Unpin from this list: Windows has no public way for another program to unpin a jump list item", JumpRules.SubWhy("recent", true));
    }

    static FakePlatform Ready() { var p = new FakePlatform(); p.Start(); return p; }

    [Fact]
    public async Task A_test_run_dry_runs_open_remove_and_close_and_logs_no_names()
    {
        var p = Ready().Answer("jumplist.open", "{\"ok\":true,\"dryRun\":true,\"route\":\"link default command\"}");
        var log = new List<string>();
        var s = new JumpListService(new JumpListOptions { Platform = p, Test = true, Log = log.Add });
        var r = await s.OpenAsync(App(hwnds: "77"), "a:3", "secret.txt");
        Assert.True(r.Ok); Assert.True(r.DryRun);
        Assert.Equal("dry run: would open it via link default command", r.Reason);
        Assert.Contains("\"dry\":true", p.Calls.Single().ArgsJson);
        Assert.False((await s.RemoveAsync(App(hwnds: "77"), "app", "Notes")).Ok);
        Assert.Equal("Mantle lost track of that item: open the menu again", (await s.OpenAsync(App(), "../x", null)).Reason);
        var c = await s.CloseAsync(new[] { "77", "78" });
        Assert.True(c.DryRun);
        Assert.Contains(log, l => l.Contains("would send WM_CLOSE to 2 window(s): 77, 78"));
        Assert.DoesNotContain(log, l => l.Contains("secret") || l.Contains("Notes"));
        Assert.Single(p.Calls);   // the close never reached the sidecar
    }

    [Fact]
    public async Task An_old_helper_is_said_in_words()
    {
        var p = Ready().Handle("jumplist.get", _ => PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: jumplist.get"));
        var s = new JumpListService(new JumpListOptions { Platform = p });
        var r = await s.GetAsync(App(hwnds: "5"));
        Assert.False(r.Ok);
        Assert.Equal("Mantle's helper is out of date: restart Mantle to finish its update", r.Reason);
    }

    [Fact]
    public async Task The_fixture_answers_get_by_exe_name_and_never_calls_the_sidecar()
    {
        using var d = new TestDir("jl-fixture");
        string fx = d.File("jl.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fx)!);
        File.WriteAllText(fx, "{\"apps\":{\"notes.exe\":{\"sections\":[{\"kind\":\"tasks\",\"name\":\"Tasks\",\"items\":[{\"title\":\"New note\",\"item\":\"c:0:0\"}]}]},\"*\":{\"fail\":\"the sidecar timed out\"}}}");
        var p = Ready();
        var log = new List<string>();
        var s = new JumpListService(new JumpListOptions { Platform = p, Test = true, Fixture = fx, Log = log.Add });
        var r = await s.GetAsync(App());
        Assert.Equal("New note", r.Sections.Single().Items.Single().Title);
        var other = await s.GetAsync(App(exe: @"C:\Apps\other.exe"));
        Assert.False(other.Ok);
        Assert.Equal("Mantle's helper timed out", other.Reason);
        Assert.Contains(log, l => l.Contains("jump list: get: tasks:1 in"));
        Assert.Empty(p.Calls);
    }

    static Stores ScratchStores(TestDir d) => new(d.Path, new SafeFile(WriteGate.Scratch(d.Path)), new ListLog());

    static PinOverlay Saved(TestDir d)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(d.File(@"features\jumplist.json")));
        return PinOverlay.Norm(doc.RootElement.GetProperty("pins"));
    }

    [Fact]
    public async Task Pin_unpin_and_undo_write_Mantles_own_list_and_put_the_app_back_in_its_place()
    {
        using var d = new TestDir("jl-pin");
        using var stores = ScratchStores(d);
        var store = stores.Open(@"features\jumplist.json");
        await store.SetAsync("pins", PinOverlayEdit.ToJson(new PinOverlay { Order = new[] { "A.lnk", "Notes.lnk", "B.lnk" } }));
        var log = new List<string>();
        long now = 1000;
        var s = new JumpListService(new JumpListOptions { Platform = Ready(), Overlay = store, Test = true, Log = log.Add, Now = () => now });
        var un = await s.PinAsync(App(), pin: false);
        Assert.True(un.Ok); Assert.Equal("hid", un.Did); Assert.True(un.Undo);
        Assert.Equal(new[] { "A.lnk", "B.lnk" }, Saved(d).Order);
        Assert.Equal("Notes.lnk", Saved(d).Hide.Single().Id);
        now += 60_000;
        var back = await s.PinAsync(App(), pin: true, undo: true);
        Assert.Equal("restored", back.Did);
        Assert.Equal(new[] { "A.lnk", "Notes.lnk", "B.lnk" }, Saved(d).Order);
        Assert.Empty(Saved(d).Hide);
        // a running-only app: pinned is an add; unpinned again it is removed; an undo 11 min later is an ordinary pin
        var run = App(id: "run:notes", kind: "running");
        Assert.Equal("added", (await s.PinAsync(run, pin: true)).Did);
        Assert.Equal("already", (await s.PinAsync(run, pin: true)).Did);
        Assert.Equal("removed", (await s.PinAsync(run, pin: false)).Did);
        now += 11 * 60_000;
        Assert.Equal("added", (await s.PinAsync(run, pin: true, undo: true)).Did);
        Assert.Contains(log, l => l.Contains("undo unpin (nothing kept to undo from), pin: added (in this test profile)"));
        Assert.DoesNotContain(log, l => l.Contains("Notes"));
    }

    [Fact]
    public void Two_different_app_ids_on_one_exe_are_two_apps()
    {
        var o = PinOverlayEdit.Pin(PinOverlay.Empty, new PinOverlayEdit.App("x", "Files", @"c:\e\electron.exe", "Mantle.Files")).Overlay;
        var r = PinOverlayEdit.Pin(o, new PinOverlayEdit.App("y", "Task Manager", @"c:\e\electron.exe", "Mantle.TaskManager"));
        Assert.Equal("added", r.Did);
        Assert.Equal(2, r.Overlay.Add.Count);
        Assert.Throws<InvalidOperationException>(() => PinOverlayEdit.Pin(PinOverlay.Empty, new PinOverlayEdit.App("z", "Z", null, null)));
    }

    [Fact]
    public void The_arrow_keys_wrap_and_Home_End_go_to_the_ends()
    {
        Assert.Equal(0, JumpRules.Step(-1, 5, JumpRules.Move.Down));
        Assert.Equal(4, JumpRules.Step(-1, 5, JumpRules.Move.Up));
        Assert.Equal(3, JumpRules.Step(2, 5, JumpRules.Move.Down));
        Assert.Equal(0, JumpRules.Step(4, 5, JumpRules.Move.Down));   // wraps
        Assert.Equal(4, JumpRules.Step(0, 5, JumpRules.Move.Up));     // wraps
        Assert.Equal(0, JumpRules.Step(3, 5, JumpRules.Move.Home));
        Assert.Equal(4, JumpRules.Step(1, 5, JumpRules.Move.End));
        Assert.Equal(-1, JumpRules.Step(0, 0, JumpRules.Move.Down));
    }
}
