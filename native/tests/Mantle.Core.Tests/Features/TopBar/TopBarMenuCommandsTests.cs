using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The dropdowns' commands (lib/features/topbar.js: checkArgs, menus, invoke, uiaInvoke, window, power, menuState's
/// refocus) on the same rig: every handle is checked first; a --test run never sends anything into a real window or
/// runs a power action; a live run goes through the (fake) sidecar and shell.
/// </summary>
public sealed partial class TopBarFeatureTests
{
    static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;
    const string Focus10 = "{\"kind\":\"focus\",\"hwnd\":\"10\",\"exe\":\"C:\\\\x\\\\notepad.exe\",\"desc\":\"Notepad\",\"menu\":{\"source\":\"win32\",\"hwnd\":\"4242\",\"items\":[]}}";

    [Fact]
    public void Page_requests_name_only_the_windows_they_were_given_and_refusals_are_plain_words_TOP_308_309_311_312_313_333()
    {
        var ok = new HashSet<string> { "10", "4242" };
        string? Why(string verb, string json) => TopBarFeature.CheckArgs(verb, El(json), ok).Reason;
        string Args(string verb, string json) => TopBarFeature.CheckArgs(verb, El(json), ok).Args!.ToJsonString();
        Assert.Equal("the app's window could not be identified, so nothing was sent to it", Why("window", "{\"action\":\"minimize\"}"));
        Assert.Equal("the app's window could not be identified, so nothing was sent to it", Why("window", "{\"hwnd\":\"0x10\",\"action\":\"minimize\"}"));
        Assert.Equal("that window is no longer the app in front, so nothing was sent to it; open the menu again", Why("window", "{\"hwnd\":\"99\",\"action\":\"minimize\"}"));
        Assert.Equal("{\"hwnd\":\"10\",\"action\":\"minimize\"}", Args("window", "{\"hwnd\":10,\"action\":\"minimize\",\"extra\":1}"));
        Assert.Equal("{\"hwnd\":\"10\",\"action\":\"ontop\",\"on\":true}", Args("window", "{\"hwnd\":\"10\",\"action\":\"ontop\",\"on\":true}"));
        Assert.Equal("Mantle does not know that window action", Why("window", "{\"hwnd\":\"10\",\"action\":\"explode\"}"));
        Assert.Equal("Mantle does not know that window action", Why("window", "{\"hwnd\":\"10\",\"action\":\"ontop\",\"on\":\"yes\"}"));
        Assert.Equal("{\"hwnd\":\"4242\"}", Args("menus", "{\"hwnd\":\"4242\"}"));
        Assert.Equal("{\"hwnd\":\"4242\",\"init\":[0,2]}", Args("menus", "{\"hwnd\":\"4242\",\"init\":[0,2]}"));
        foreach (var bad in new[] { "[]", "[0,1,2,3,4,5,6,7,8]", "[1000]", "[-1]", "[1.5]", "\"0\"" })
            Assert.Equal("that menu could not be identified; open it again", Why("menus", "{\"hwnd\":\"4242\",\"init\":" + bad + "}"));
        Assert.Equal("{\"hwnd\":\"4242\",\"item\":101}", Args("invoke", "{\"hwnd\":\"4242\",\"item\":101}"));
        foreach (var bad in new[] { "70000", "-1", "1.5", "\"101\"" })
            Assert.Equal("that menu item could not be identified; open the menu again", Why("invoke", "{\"hwnd\":\"4242\",\"item\":" + bad + "}"));
        Assert.Equal("{\"hwnd\":\"10\",\"path\":[1,2],\"names\":[\"View\",null],\"expand\":false}", Args("uiaInvoke", "{\"hwnd\":\"10\",\"path\":[1,2],\"names\":[\"View\",null]}"));
        Assert.Equal("that menu item could not be identified; open the menu again", Why("uiaInvoke", "{\"hwnd\":\"10\",\"path\":[1],\"names\":[\"a\",\"b\"]}"));
        Assert.Equal("that menu item could not be identified; open the menu again", Why("uiaInvoke", "{\"hwnd\":\"10\",\"path\":[1],\"names\":[\"" + new string('x', 261) + "\"]}"));
        Assert.Equal("Mantle does not know that request", Why("frob", "{\"hwnd\":\"10\"}"));
        var words = new[] { Why("window", "{}"), Why("window", "{\"hwnd\":\"99\"}"), Why("menus", "{\"hwnd\":\"10\",\"init\":[]}"), Why("frob", "{\"hwnd\":\"10\"}") };
        Assert.All(words, w => Assert.DoesNotMatch("(?i)hwnd|sidecar|ipc|undefined", w));
    }

    [Fact]
    public void Test_runs_answer_window_invoke_and_power_with_dry_runs_and_never_reach_a_window_TOP_313_315_046_047_048_049() => T(async (r, s) =>
    {
        var sh = new FakeShell();
        await Up(r, s, shell: sh);
        r.Platform.Answer("topbar.menus", "{\"menu\":{\"source\":\"win32\",\"hwnd\":\"4242\",\"items\":[]}}");
        r.Platform.Raise("topbar", Focus10);
        await Tick(r, 100);
        var w = await r.Bus.CallAsync("topbar.window", new JsonObject { ["hwnd"] = "10", ["action"] = "minimize" });
        Assert.Equal("dry run (--test): would have run the window action minimize on window 10", Reason(w.Result));
        var inv = await r.Bus.CallAsync("topbar.invoke", new JsonObject { ["hwnd"] = "4242", ["item"] = 101 });
        Assert.Equal("dry run (--test): would have sent menu item 101 to window 4242", Reason(inv.Result));
        var uia = await r.Bus.CallAsync("topbar.uiaInvoke", new JsonObject { ["hwnd"] = "10", ["path"] = new JsonArray(1) });
        Assert.StartsWith("a --test instance never runs a UI Automation action", Reason(uia.Result));
        var no = await r.Bus.CallAsync("topbar.window", new JsonObject { ["hwnd"] = "99", ["action"] = "close" });
        Assert.False(Flag(no.Result, "ok"));
        Assert.True(r.Logged("refused a page request (window): that window is no longer the app in front, so nothing was sent to it; open the menu again"));
        // the menu read: a --test run sends no WM_INITMENUPOPUP (init dropped), the read itself goes on
        var m = await r.Bus.CallAsync("topbar.menus", new JsonObject { ["hwnd"] = "4242", ["init"] = new JsonArray(0) });
        Assert.True(m.Ok);
        Assert.Equal("{\"hwnd\":\"4242\"}", r.Platform.Calls.Last(c => c.Verb == "topbar.menus").ArgsJson);
        Assert.Equal(0, Calls(r, "topbar.window") + Calls(r, "topbar.invoke") + Calls(r, "topbar.uiaInvoke"));
        // power: Restart and Shut down ask first; each is a dry run that names what it would have run
        Assert.Equal("Restart asks first: press Enter on it twice", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "restart" })).Result));
        Assert.Equal("Shut down asks first: press Enter on it twice", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "shutdown", ["confirmed"] = false })).Result));
        Assert.Matches(@"^dry run \(--test\): would have run .*\\shutdown\.exe /r /t 0$", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "restart", ["confirmed"] = true })).Result));
        Assert.Matches(@"^dry run \(--test\): would have run .*\\shutdown\.exe /s /t 0$", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "shutdown", ["confirmed"] = true })).Result));
        Assert.Matches(@"^dry run \(--test\): would have run .*\\rundll32\.exe user32\.dll,LockWorkStation$", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "lock" })).Result));
        Assert.Equal("dry run (--test): would have put the PC to sleep (the Mantle helper's session sleep)", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "sleep" })).Result));
        Assert.Equal("there is no power action called \"frob\"", Reason((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "frob" })).Result));
        Assert.Empty(sh.Spawned);
        Assert.Equal(0, Calls(r, "session"));
        // closing a menu in --test never moves the keyboard
        var ms = await r.Bus.CallAsync("topbar.menuState", new JsonObject { ["open"] = false, ["refocus"] = true });
        Assert.False(Flag(ms.Result, "refocused"));
        Assert.Equal(0, Calls(r, "focus"));
    });

    [Fact]
    public void Live_runs_send_window_invoke_power_and_refocus_through_the_sidecar_and_shell_TOP_077_311_313() => T(async (r, s) =>
    {
        var sh = new FakeShell();
        await Up(r, s, shell: sh);
        r.Platform.Answer("topbar.window", "{\"ok\":true}").Answer("topbar.invoke", "{\"ok\":true}").Answer("focus", "{\"ok\":true}").Answer("session", "{\"ok\":true}");
        r.Platform.Raise("topbar", Focus10);
        await Tick(r, 100);
        Assert.True(Flag((await r.Bus.CallAsync("topbar.window", new JsonObject { ["hwnd"] = "10", ["action"] = "zoom" })).Result, "ok"));
        Assert.Equal("{\"hwnd\":\"10\",\"action\":\"zoom\"}", r.Platform.Calls.Last(c => c.Verb == "topbar.window").ArgsJson);
        await r.Bus.CallAsync("topbar.invoke", new JsonObject { ["hwnd"] = "4242", ["item"] = 7 });
        Assert.Equal("{\"hwnd\":\"4242\",\"item\":7,\"focus\":true}", r.Platform.Calls.Last(c => c.Verb == "topbar.invoke").ArgsJson);
        Assert.True(Flag((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "lock" })).Result, "ok"));
        Assert.Matches(@"\\rundll32\.exe user32\.dll,LockWorkStation$", sh.Spawned.Single());
        Assert.True(Flag((await r.Bus.CallAsync("topbar.power", new JsonObject { ["action"] = "sleep" })).Result, "ok"));
        Assert.Contains("\"action\":\"sleep\"", r.Platform.Calls.Last(c => c.Verb == "session").ArgsJson);
        var ms = await r.Bus.CallAsync("topbar.menuState", new JsonObject { ["open"] = false, ["refocus"] = true });
        Assert.True(Flag(ms.Result, "refocused"));
        Assert.Equal("{\"hwnd\":\"10\"}", r.Platform.Calls.Last(c => c.Verb == "focus").ArgsJson);
        await r.Bus.CallAsync("topbar.menuState", new JsonObject { ["open"] = false, ["refocus"] = false });   // click-away: the keyboard stays
        Assert.Equal(1, Calls(r, "focus"));
    }, test: false);
}
