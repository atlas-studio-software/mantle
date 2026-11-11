using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The dropdowns and their keys (ui/topbar/topbar.js activate / move / stepTitle / openSub / refreshLevel / keydown,
/// ui/topbar/model.js MANTLE_MENU / appMenu / layoutItems), as Mantle.Core's TopBarMenu: pure, with "now" passed in.
/// </summary>
public sealed class TopBarMenuTests
{
    static readonly JsonObject Npp = TopBarFixtures.Focus("npp")!;
    static readonly JsonObject Chrome = TopBarFixtures.Focus("chrome")!;
    static TopBarMenu Open(IReadOnlyList<TopBarTitle> t, int ti) => TopBarMenu.ForTitle(ti, t[ti], 8);
    static TopBarMenu Mantle(bool overGames = false, bool peeking = false, JsonObject? layouts = null)
        => Open(TopBarModel.Titles(Npp, Npp["menu"] as JsonObject, overGames, peeking, layouts), 0);
    static int Row(TopBarMenu m, string label, int level = 0) => m.Stack[level].Items.ToList().FindIndex(i => i.Label == label);
    static MenuCall Pick(TopBarMenu m, string label, long now = 0, int level = 0)
    {
        var s = m.Activate(level, Row(m, label, level), now, Npp, Npp["menu"] as JsonObject);
        Assert.Equal(MenuDo.Call, s.Do);
        Assert.True(s.Refocus);
        return s.Call!;
    }
    static string A(MenuCall c) => c.Verb + " " + (c.Args?.ToJsonString() ?? "");

    [Fact]
    public void Mantle_menu_order_and_what_each_row_calls_TOP_031_032_033_034_035_036_046_047_050()
    {
        var m = Mantle();
        Assert.Equal(new[] { "About Mantle", "Mantle Settings…", "Windows Settings…", "Keep the top bar over full-screen games", "Hide top bar", null,
            "Layouts", "Save layout…", null, "Lock", "Sleep", "Restart…", "Shut down…" }, m.Top.Items.Select(i => i.T == "sep" ? null : i.Label));
        Assert.Equal("topbar.about ", A(Pick(m, "About Mantle")));
        Assert.Equal("topbar.settings ", A(Pick(m, "Mantle Settings…")));
        Assert.Equal("Win+I", m.Top.Items[1].Key);
        Assert.Equal("topbar.winSettings ", A(Pick(m, "Windows Settings…")));
        Assert.Equal("topbar.overGames {\"on\":true}", A(Pick(m, "Keep the top bar over full-screen games")));
        Assert.Equal("topbar.overGames {\"on\":false}", A(Pick(Mantle(overGames: true), "Keep the top bar over full-screen games")));
        Assert.Equal("✓", TopBarMenu.Row(Mantle(overGames: true).Top.Items[3], false).Check);
        Assert.Equal("topbar.hidden {\"on\":true,\"via\":\"the Mantle menu\"}", A(Pick(m, "Hide top bar")));
        Assert.Equal("Win+Alt+T", m.Top.Items[4].Key);
        Assert.Equal("topbar.hidden {\"on\":false,\"via\":\"the Mantle menu, while peeking\"}", A(Pick(Mantle(peeking: true), "Show top bar")));
        Assert.Equal("topbar.power {\"action\":\"lock\",\"confirmed\":false}", A(Pick(m, "Lock")));
        Assert.Equal("topbar.power {\"action\":\"sleep\",\"confirmed\":false}", A(Pick(m, "Sleep")));
    }

    [Fact]
    public void Restart_and_shut_down_arm_first_disarm_after_4_s_and_a_double_click_is_not_two_decisions_TOP_048_049_051()
    {
        foreach (var (label, confirm, action) in new[] { ("Restart…", "Click again to restart", "restart"), ("Shut down…", "Click again to shut down", "shutdown") })
        {
            var m = Mantle();
            int i = Row(m, label);
            var s = m.Activate(0, i, 1000, Npp, null);
            Assert.Equal(MenuDo.Armed, s.Do);
            Assert.Equal((confirm, true), (TopBarMenu.Row(m.Top.Items[i], true).Label, m.Armed != null));
            Assert.Equal(MenuDo.Nothing, m.Activate(0, i, 1000 + TopBarMenu.ArmMinMs - 1, Npp, null).Do);   // a double-click
            Assert.Equal($"topbar.power {{\"action\":\"{action}\",\"confirmed\":true}}", A(m.Activate(0, i, 1000 + TopBarMenu.ArmMinMs, Npp, null).Call!));
            // the 4 s run out: disarmed, and the next click arms again
            var m2 = Mantle();
            m2.Activate(0, i, 0, Npp, null);
            Assert.True(m2.Disarm(m2.Top.Items[i]));
            Assert.Null(m2.Armed);
            Assert.Equal(MenuDo.Armed, m2.Activate(0, i, TopBarMenu.ArmMs + 10, Npp, null).Do);
        }
        Assert.Equal((4000, 300), (TopBarMenu.ArmMs, TopBarMenu.ArmMinMs));
    }

    [Fact]
    public void App_menus_new_window_and_window_actions_and_the_desktop_TOP_052_053_054_055_056_057_058_059_060()
    {
        var chrome = TopBarModel.Titles(Chrome, null);
        var m = Open(chrome, 1);
        Assert.Equal(new[] { "New window", null, "Minimize", "Zoom", "Always on top", null, "Close window", "Quit Google Chrome" }, m.Top.Items.Select(i => i.T == "sep" ? null : i.Label));
        var nw = m.Activate(0, 0, 0, Chrome, null).Call!;
        Assert.Equal("topbar.newWindow", nw.Verb);
        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", (string?)nw.Args!["exe"]);
        Assert.Equal("[\"--new-window\"]", nw.Args["args"]!.ToJsonString());
        foreach (var (label, act) in new[] { ("Minimize", "minimize"), ("Zoom", "zoom"), ("Always on top", "ontop"), ("Close window", "close"), ("Quit Google Chrome", "quit") })
            Assert.Equal($"topbar.window {{\"hwnd\":\"5151\",\"action\":\"{act}\"}}", A(m.Activate(0, Row(m, label), 0, Chrome, null).Call!));
        // zoomed reads "Restore size"; a topmost window ticks "Always on top"; no "New window" for an app Mantle cannot open one for
        var z = (JsonObject)Chrome.DeepClone(); z["zoomed"] = true; z["topmost"] = true; z["exe"] = @"C:\x\paint.exe";
        var zm = TopBarModel.AppMenu(z);
        Assert.Equal("Restore size", zm[1].Label);
        Assert.True(zm[2].Checked);
        Assert.Equal("Minimize", zm[0].Label);
        // the desktop: only "New File Explorer window", explorer.exe and no args; no Window title
        var desk = TopBarModel.Titles(TopBarFixtures.Focus("desktop"), null);
        var dm = Open(desk, 1);
        Assert.Equal("New File Explorer window", Assert.Single(dm.Top.Items).Label);
        Assert.Equal("topbar.newWindow {\"exe\":\"C:\\\\Windows\\\\explorer.exe\",\"args\":[]}", A(dm.Activate(0, 0, 0, null, null).Call!));
        Assert.DoesNotContain(desk, t => t.Key == "window");
        // a menu-less app's Window title: Minimize, Zoom, Always on top
        Assert.Equal(new[] { "Minimize", "Zoom", "Always on top" }, chrome.Single(t => t.Key == "window").Items!.Select(i => i.Label));
    }

    [Fact]
    public void Own_menus_rows_separators_win32_and_uia_picks_and_nothing_to_run_TOP_062_063_064_065_069()
    {
        var rows = TopBarModel.FromJson(JsonNode.Parse("""
            [{"t":"sep"},{"t":"item","label":"A","id":7,"pos":1,"checked":true},{"t":"sep"},{"t":"sep"},{"t":"item","label":"","pos":4,"radio":true,"checked":true},
             {"t":"item","label":"","ownerDraw":true,"pos":5},{"t":"item","label":"Sub","pos":6,"items":[{"t":"sep"},{"t":"item","label":"x","id":9,"pos":1},{"t":"sep"}]},{"t":"sep"}]
            """)!.AsArray());
        Assert.Equal(new[] { "item", "sep", "item", "item", "item" }, rows.Select(r => r.T));
        Assert.Equal(("A", "✓", false), (TopBarMenu.Row(rows[0], false).Label, TopBarMenu.Row(rows[0], false).Check, TopBarMenu.Row(rows[0], false).Off));
        Assert.Equal(("(untitled)", "●"), (TopBarMenu.Row(rows[2], false).Label, TopBarMenu.Row(rows[2], false).Check));
        Assert.Equal(("(the app draws this item itself)", true), (TopBarMenu.Row(rows[3], false).Label, TopBarMenu.Row(rows[3], false).Off));
        Assert.Equal("▸", TopBarMenu.Row(rows[4], false).Arrow);
        Assert.Equal(new[] { "x" }, rows[4].Items!.Select(i => i.Label));   // cleaned recursively
        // the Notepad++ fixture's File menu: a Win32 leaf calls invoke with its id; a disabled row does nothing
        var t = TopBarModel.Titles(Npp, Npp["menu"] as JsonObject);
        var file = Open(t, 2);
        Assert.Equal("Ctrl+N", file.Top.Items[0].Key);
        Assert.Equal("topbar.invoke {\"hwnd\":\"4242\",\"item\":101}", A(Pick(file, "New")));
        Assert.Equal(MenuDo.Nothing, file.Activate(0, Row(file, "Save a Copy As…"), 0, Npp, Npp["menu"] as JsonObject).Do);
        Assert.True(TopBarMenu.Row(file.Top.Items[Row(file, "Save a Copy As…")], false).Off);
        // a UI Automation leaf: uiaInvoke with the positions and the labels along the path
        var uia = new JsonObject { ["source"] = "uia", ["hwnd"] = "77" };
        var lv = new MenuLevel { Path = new[] { 2 }, Names = new string?[] { "View" } };
        var c = TopBarMenu.CallFor(new TopBarItem("item", "Zoom", Pos: 3), lv, false, Npp, uia)!;
        Assert.Equal("topbar.uiaInvoke {\"hwnd\":\"77\",\"path\":[2,3],\"names\":[\"View\",\"Zoom\"]}", A(c));
        // nothing to run it with
        var none = new TopBarMenu(); none.Stack.Add(new MenuLevel { Items = new[] { new TopBarItem("item", "Odd") } });
        var s = none.Activate(0, 0, 0, Npp, null);
        Assert.Equal((MenuDo.Close, true, "Odd: this item has nothing to run it with"), (s.Do, s.Refocus, s.Note));
    }

    [Fact]
    public void A_title_refreshes_and_lazy_submenus_ask_and_say_why_they_are_empty_TOP_061_067_068()
    {
        var t = TopBarModel.Titles(Npp, Npp["menu"] as JsonObject);
        var file = Open(t, 2);
        Assert.Equal(new[] { 0 }, file.Top.Path);
        // the fresh read (topbar.menus) replaces the level with the app's current states
        var fresh = (JsonObject)Npp["menu"]!.DeepClone();
        ((JsonObject)fresh["items"]![0]!["items"]![0]!)["enabled"] = false;
        TopBarMenu.Refreshed(file.Top, new JsonObject { ["menu"] = fresh });
        Assert.False(file.Top.Items[0].Enabled);
        // Language is lazy: "Asking the app…", then what the app said, or why it stayed empty
        var lang = t.Single(x => x.Label == "Language");
        Assert.True(lang.Lazy);
        var m = new TopBarMenu(); m.Stack.Add(new MenuLevel { Items = new[] { new TopBarItem("item", "Language", Items: Array.Empty<TopBarItem>(), Pos: 5, Lazy: true) } });
        var (pushed, title, refresh) = m.PushSub(0, 0, 10, 240, 40, "win32");
        Assert.Equal((true, (int?)null, true), (pushed, title, refresh));
        Assert.Equal(TopBarMenu.Asking, m.Top.Info);
        Assert.Equal("Asking the app for this menu…", TopBarMenu.Asking);
        TopBarMenu.Refreshed(m.Top, JsonNode.Parse("""{"menu":{"items":[{"t":"item","label":"Language","pos":5,"items":[]}]}}""")!.AsObject());
        Assert.Equal("The app fills this menu only when its own menu opens, and it stayed empty.", m.Top.Info);
        TopBarMenu.Refreshed(m.Top, new JsonObject { ["ok"] = false, ["reason"] = "the app is not answering" });
        Assert.Equal(("the app is not answering", true), (m.Top.Info, m.Top.Bad));
        Assert.Equal("This menu is empty.", TopBarMenu.Empty);
    }

    [Fact]
    public void More_lists_the_hidden_titles_and_opens_each_as_a_submenu_TOP_070()
    {
        var t = TopBarModel.Titles(Npp, Npp["menu"] as JsonObject);
        var m = TopBarMenu.ForMore(t, 12, 900);
        Assert.Equal(new[] { "Plugins", "Window", "?" }, m.Top.Items.Select(i => i.Label));
        Assert.True(m.IsMore);
        var (pushed, _, refresh) = m.PushSub(0, 2, 900, 1130, 60, "win32");
        Assert.True(pushed);
        Assert.True(refresh);   // a Win32 title from » gets fresh states, as on the bar
        Assert.Equal("About Notepad++", m.Top.Items[0].Label);
        Assert.Equal(new[] { 12 }, m.Top.Path);
        // a lazy UI Automation title hidden in »: the same road as clicking it on the bar
        var u = TopBarMenu.ForMore(new[] { new TopBarTitle("m0", "View", Pos: 0, Source: "uia", Lazy: true, Items: Array.Empty<TopBarItem>()) }, 0, 0);
        Assert.Equal((false, (int?)0, false), u.PushSub(0, 0, 0, 230, 40, "uia"));
    }

    [Fact]
    public void Dropdowns_flip_pull_in_cap_and_size_the_window_TOP_072_073_074_079()
    {
        var m = new TopBarMenu(); m.Stack.Add(new MenuLevel { Items = new[] { new TopBarItem("item", "Sub", Items: new[] { new TopBarItem("item", "x") }) } });
        m.PushSub(0, 0, 100, 330, 40, null);
        Assert.Equal((332d, (double?)-132d, (double?)34d), (m.Top.X, m.Top.FlipX, m.Top.Y));   // 2 px right of the row, 6 px above it
        Assert.Equal(100, TopBarMenu.Left(1, 1200, 230, 1280, 100));   // a submenu flips to its parent's left
        Assert.Equal(1046, TopBarMenu.Left(0, 1200, 230, 1280, null));  // a top-level one is pulled in to 4 px
        Assert.Equal(4, TopBarMenu.Left(1, 1200, 230, 1280, -132));    // never left of 4
        Assert.Equal(300, TopBarMenu.Left(0, 300, 230, 1280, null));
        Assert.Equal((30d, (double?)null), TopBarMenu.FitDown(0, 30, 300, 1032));
        Assert.Equal((30d, (double?)794d), TopBarMenu.FitDown(0, 30, 2000, 832));       // capped at the work area - 8, scrolls
        Assert.Equal((30d, (double?)72d), TopBarMenu.FitDown(0, 30, 2000, 60));         // never under 72
        Assert.Equal((424d, (double?)null), TopBarMenu.FitDown(1, 600, 400, 832));      // a submenu moves up instead
        Assert.Equal((30d, (double?)794d), TopBarMenu.FitDown(1, 600, 1000, 832));      // not above 30
        Assert.Equal(28, TopBarMenu.WindowHeight(Array.Empty<double>()));
        Assert.Equal(366, TopBarMenu.WindowHeight(new[] { 200.0, 357.4 }));
        Assert.Equal(15d, TopBarMenu.KeepVisible(300, 24, 0, 314, 600));   // past the fold: scrolled so the row shows + 5
        Assert.Equal(95d, TopBarMenu.KeepVisible(100, 24, 120, 200, 600));
        Assert.Null(TopBarMenu.KeepVisible(100, 24, 50, 200, 600));
        Assert.Null(TopBarMenu.KeepVisible(300, 24, 0, 600, 400));        // no scroll at all
    }

    [Fact]
    public void Result_cut_off_and_cannot_show_notes_TOP_074_076_081()
    {
        Assert.Equal("Minimize: the window is gone", TopBarMenu.ResultNote("Minimize", false, "the window is gone", false));
        Assert.Equal("Minimize: it did not happen, and no reason came back", TopBarMenu.ResultNote("Minimize", false, null, false));
        Assert.Equal("dry run (--test): would have x", TopBarMenu.ResultNote("Lock", true, "dry run (--test): would have x", true));
        Assert.Null(TopBarMenu.ResultNote("Lock", true, "x", false));
        Assert.Equal("The menu is cut off: it needs 412 px and the bar's window stayed 28 px tall", TopBarMenu.CutOff(412, 28));
        Assert.Equal("The menu cannot be shown in full: the bar's window did not grow", TopBarMenu.CannotShow(null));
        Assert.Equal(1500, TopBarMenu.WaitMs);
    }

    [Fact]
    public void Layout_menus_restore_rename_delete_save_and_the_name_box_TOP_037_038_039_040_041_042_043_044_082()
    {
        Assert.Equal("Window layouts are not running", Mantle().Top.Items[6].Items!.Single().Label);
        var off = TopBarModel.LayoutItems(new JsonObject { ["enabled"] = false });
        Assert.Equal("Window layouts are switched off in the tray", off.Restore.Single().Label);
        Assert.False(off.Save.Single().Enabled);
        var none = TopBarModel.LayoutItems(new JsonObject { ["layouts"] = new JsonArray() });
        Assert.Equal("No layouts yet: Save layout… names what is on screen", none.Restore.Single().Label);
        var st = JsonNode.Parse("""{"layouts":[{"name":"Work","count":3},{"name":"Solo","count":1}]}""")!.AsObject();
        var x = TopBarModel.LayoutItems(st);
        Assert.Equal(new[] { "Work", "Solo", null, "Rename", "Delete" }, x.Restore.Select(i => i.T == "sep" ? null : i.Label));
        Assert.Equal(("3 windows", "1 window"), (x.Restore[0].Key, x.Restore[1].Key));
        var busy = TopBarModel.LayoutItems(JsonNode.Parse("""{"layouts":[{"name":"Work","count":3}],"busy":"Work"}""")!.AsObject());
        Assert.Equal("Putting Work back…", busy.Restore[0].Label);
        Assert.All(busy.Restore.Where(i => i.Act == "layoutRestore"), i => Assert.False(i.Enabled));
        Assert.Equal(new[] { "New layout…", null, "Replace Work", "Replace Solo" }, x.Save.Select(i => i.T == "sep" ? null : i.Label));
        Assert.Equal(("y", "v"), (Mantle().Top.Items[6].Mn, Mantle().Top.Items[7].Mn));

        var m = Mantle(layouts: st);
        m.PushSub(0, 6, 8, 230, 100, null);
        Assert.Equal("layouts.restore {\"name\":\"Work\",\"via\":\"the Mantle menu\"}", A(m.Activate(1, 0, 0, Npp, null).Call!));
        m.PushSub(1, 4, 8, 230, 200, null);   // Delete > Work, confirmed
        Assert.Equal(MenuDo.Armed, m.Activate(2, 0, 0, Npp, null).Do);
        Assert.Equal("Click again to delete Work", TopBarMenu.Row(m.Top.Items[0], true).Label);
        Assert.Equal("layouts.delete {\"name\":\"Work\"}", A(m.Activate(2, 0, 500, Npp, null).Call!));
        // Rename > Work…: the name box pre-filled; typed keys stay in it; Escape leaves the box with the menu open; Enter renames
        var r = Mantle(layouts: st);
        r.PushSub(0, 6, 8, 230, 100, null); r.PushSub(1, 3, 8, 230, 200, null);
        Assert.Equal(MenuDo.Render, r.Activate(2, 0, 0, Npp, null).Do);
        Assert.Equal("Work", r.Edit!.Value.Value);
        Assert.Equal(MenuDo.Nothing, r.Key("a", 0, Npp, null).Do);
        Assert.Equal(MenuDo.Nothing, r.Key("ArrowDown", 0, Npp, null).Do);
        Assert.Equal(MenuDo.Render, r.Key("Escape", 0, Npp, null).Do);
        Assert.Null(r.Edit);
        Assert.Equal(3, r.Stack.Count);
        r.Activate(2, 0, 0, Npp, null);
        r.Edit = (r.Edit!.Value.Item, "Office");
        Assert.Equal(MenuDo.Commit, r.Key("Enter", 0, Npp, null).Do);
        Assert.Equal("layouts.rename {\"from\":\"Work\",\"to\":\"Office\"}", A(r.Commit()!));
        // Save layout… > New layout…: an empty box; Enter saves; Replace <name> is confirmed
        var s = Mantle(layouts: st);
        s.PushSub(0, 7, 8, 230, 100, null);
        s.Activate(1, 0, 0, Npp, null);
        Assert.Equal("", s.Edit!.Value.Value);
        s.Edit = (s.Edit!.Value.Item, "Home");
        var save = s.Commit()!;
        Assert.Equal(("layouts.save {\"name\":\"Home\"}", "Save layout"), (A(save), save.Label));
        Assert.Equal(MenuDo.Armed, s.Activate(1, 2, 0, Npp, null).Do);
        Assert.Equal("layouts.save {\"name\":\"Work\",\"replace\":true}", A(s.Activate(1, 2, 400, Npp, null).Call!));
    }

    [Fact]
    public void Arrow_keys_skip_separators_and_disabled_rows_and_wrap_TOP_083()
    {
        var t = TopBarModel.Titles(Npp, Npp["menu"] as JsonObject);
        var edit = Open(t, 3);   // Undo, Redo (disabled), sep, Cut, Copy, Paste, sep, Select All
        Assert.Equal(MenuDo.Render, edit.Key("ArrowDown", 0, Npp, null).Do);
        Assert.Equal("Undo", edit.Top.Items[edit.Top.Sel].Label);
        edit.Key("ArrowDown", 0, Npp, null);
        Assert.Equal("Cut", edit.Top.Items[edit.Top.Sel].Label);   // Redo is disabled, then a separator
        edit.Key("ArrowUp", 0, Npp, null);
        Assert.Equal("Undo", edit.Top.Items[edit.Top.Sel].Label);
        edit.Key("ArrowUp", 0, Npp, null);
        Assert.Equal("Select All", edit.Top.Items[edit.Top.Sel].Label);   // wraps
        edit.Key("ArrowDown", 0, Npp, null);
        Assert.Equal("Undo", edit.Top.Items[edit.Top.Sel].Label);
        var up = Open(t, 3);
        up.Key("ArrowUp", 0, Npp, null);
        Assert.Equal("Select All", up.Top.Items[up.Top.Sel].Label);   // ArrowUp with nothing selected starts at the end
    }

    [Fact]
    public void Right_left_escape_enter_and_space_TOP_084_085_086_087()
    {
        var t = TopBarModel.Titles(Npp, Npp["menu"] as JsonObject);
        var file = Open(t, 2);
        file.Top.Sel = Row(file, "Recent Files");
        var s = file.Key("ArrowRight", 0, Npp, null);
        Assert.Equal((MenuDo.OpenSub, 0, 2, true), (s.Do, s.Level, s.Index, s.SelectFirst));   // a row with a submenu opens it, first row selected
        file.PushSub(0, 2, 8, 230, 80, "win32"); file.Move(1, 1);
        Assert.Equal("notes.txt", file.Top.Items[file.Top.Sel].Label);
        s = file.Key("ArrowRight", 0, Npp, null);
        Assert.Equal((MenuDo.OpenTitle, 1, true), (s.Do, s.Index, s.SelectFirst));   // a leaf: the next title
        Assert.Equal(MenuDo.Render, file.Key("ArrowLeft", 0, Npp, null).Do);           // in a submenu: closes it
        Assert.Single(file.Stack);
        s = file.Key("ArrowLeft", 0, Npp, null);
        Assert.Equal((MenuDo.OpenTitle, -1), (s.Do, s.Index));                         // at the top: the previous title
        Assert.Equal(0, TopBarMenu.StepTitle(14, 1, 15));   // wraps through » and back to the glyph
        Assert.Equal(14, TopBarMenu.StepTitle(0, -1, 15));
        file.PushSub(0, 2, 8, 230, 80, "win32");
        Assert.Equal(MenuDo.Render, file.Key("Escape", 0, Npp, null).Do);   // the deepest submenu first
        s = file.Key("Escape", 0, Npp, null);
        Assert.Equal((MenuDo.Close, true), (s.Do, s.Refocus));               // then the menu, the app's keyboard back
        file.Top.Sel = 0;
        Assert.Equal("topbar.invoke {\"hwnd\":\"4242\",\"item\":101}", A(file.Key("Enter", 0, Npp, Npp["menu"] as JsonObject).Call!));
        Assert.Equal(MenuDo.Call, file.Key(" ", 0, Npp, Npp["menu"] as JsonObject).Do);
        var fresh = Open(t, 2);
        Assert.Equal(MenuDo.Nothing, fresh.Key("Enter", 0, Npp, null).Do);   // nothing selected yet
    }

    [Fact]
    public void A_letter_runs_its_mnemonic_or_first_label_row_and_tool_menus_keep_left_right_TOP_088_089()
    {
        var m = Mantle();
        var s = m.Key("y", 0, Npp, null);
        Assert.Equal((MenuDo.OpenSub, 6), (s.Do, s.Index));           // Layouts by its mnemonic y
        Assert.Equal(MenuDo.Call, Mantle().Key("w", 0, Npp, null).Do);      // "Windows Settings…" by its first letter
        Assert.Equal("topbar.winSettings ", A(Mantle().Key("W", 0, Npp, null).Call!));
        Assert.Equal(MenuDo.Armed, Mantle().Key("r", 0, Npp, null).Do);     // Restart… still asks first
        Assert.Equal(MenuDo.Nothing, Mantle().Key("q", 0, Npp, null).Do);   // no row
        Assert.Equal(-1, TopBarMenu.Mnemonic(new[] { new TopBarItem("item", "Off", Enabled: false) }, 'o'));
        var tool = Mantle(); tool.Tool = "timer";
        Assert.Equal(MenuDo.Nothing, tool.Key("ArrowRight", 0, Npp, null).Do);
        Assert.Equal(MenuDo.Nothing, tool.Key("ArrowLeft", 0, Npp, null).Do);
    }
}
