using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.QuickLinks;
using Mantle.Core.Features.Run;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.QuickLinks;

/// <summary>
/// [Feature("quicklinks")] (lib/features/quicklinks.js): what each row runs (the plan, pure), Win+X reaching the launcher,
/// and quicklinks.open / quicklinks.power: a --test run's are dry runs that say what they would do; a live run's go
/// through a fake starter here, so nothing is ever opened by a test.
/// </summary>
[Collection("QuickLinksToggle")]   // ToggleLauncher is process-wide
public class QuickLinksFeatureTests
{
    static string? NoWt(string _) => null;
    static string? Wt(string e) => e == "wt.exe" ? @"C:\x\wt.exe" : null;

    [Fact]
    public void The_module_fields_are_Electrons()
    {
        var a = typeof(QuickLinksFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("quicklinks", "Quick links (Win+X)", true, true), (a.Name, a.Label, a.DefaultOn, a.Rest));
        Assert.Equal(new[] { "Win+X" }, a.Chords);
    }

    [Fact]
    public void Each_row_runs_what_the_table_says_never_what_a_page_sends()
    {
        Assert.Equal((null, WinXLinks.Unknown), QuickLinkPlan.For("format-c", NoWt));
        Assert.Equal((null, WinXLinks.Unknown), QuickLinkPlan.For(null, NoWt));
        Assert.Equal((null, WinXLinks.Unknown), QuickLinkPlan.For("search", NoWt));      // Search, Run and Shut down are the page's own
        foreach (var f in new[] { ("events", "eventvwr.msc"), ("devices", "devmgmt.msc"), ("disks", "diskmgmt.msc"), ("computer", "compmgmt.msc"), ("mobility", "mblctr.exe") })
        {
            var s = QuickLinkPlan.For(f.Item1, NoWt).Step!;
            Assert.Equal(("shell", f.Item2, false), (s.Kind, s.File, s.Admin));
        }
        var apps = QuickLinkPlan.For("apps", NoWt).Step!;
        Assert.Equal(("item", "setting", "ms-settings:appsfeatures"), (apps.Kind, apps.Item!.Kind, apps.Item.Path));
        var tm = QuickLinkPlan.For("taskmgr", NoWt).Step!;
        Assert.Equal(("taskmgr", "mantle:taskmgr"), (tm.Item!.MantleApp, tm.Item.Path));
        Assert.EndsWith(@"System32\Taskmgr.exe", tm.Item.WindowsPath);
        Assert.EndsWith("explorer.exe", QuickLinkPlan.For("files", NoWt).Step!.Item!.WindowsPath);
        Assert.Equal("desktop", QuickLinkPlan.For("desktop", NoWt).Step!.Kind);
    }

    [Fact]
    public void Terminal_opens_wt_or_PowerShell_with_words_and_Admin_goes_through_UAC()
    {
        var t = QuickLinkPlan.For("terminal", Wt).Step!;
        Assert.Equal(("wt.exe", false, ""), (t.File, t.Admin, t.Note));
        var n = QuickLinkPlan.For("terminal", NoWt).Step!;
        Assert.Equal(("powershell.exe", "Windows Terminal isn't installed, so Windows PowerShell opened instead."), (n.File, n.Note));
        var a = QuickLinkPlan.For("terminaladmin", Wt).Step!;
        Assert.True(a.Admin);
        Assert.Equal("dry run: would run ShellExecute(\"wt.exe\") as administrator (Windows' UAC prompt)", QuickLinkPlan.Would(a));
    }

    [Fact]
    public void The_submenu_rows_run_shutdown_exe_or_SetSuspendState_and_the_ask_first_ones_refuse_unconfirmed()
    {
        Assert.Equal((null, "Shut down asks first: press its letter twice"), QuickLinkPlan.Power("shutdown", false));
        Assert.Equal((null, "Restart asks first: press its letter twice"), QuickLinkPlan.Power("restart", false));
        Assert.Equal((null, "Sign out asks first: press its letter twice"), QuickLinkPlan.Power("signout", false));
        Assert.Equal((null, "there is no power action called \"format\""), QuickLinkPlan.Power("format", true));
        Assert.Equal(new[] { "/s", "/t", "0" }, QuickLinkPlan.Power("shutdown", true).Step!.Args);
        Assert.Equal(new[] { "/r", "/t", "0" }, QuickLinkPlan.Power("restart", true).Step!.Args);
        Assert.Equal(new[] { "/l" }, QuickLinkPlan.Power("signout", true).Step!.Args);
        Assert.Equal(new[] { "/h" }, QuickLinkPlan.Power("hibernate", false).Step!.Args);   // no ask for Hibernate or Sleep
        Assert.Equal("sleep", QuickLinkPlan.Power("sleep", false).Step!.Kind);
        Assert.EndsWith(@"System32\shutdown.exe", QuickLinkPlan.Power("restart", true).Step!.File);
        Assert.Equal("dry run: would call SetSuspendState(FALSE, FALSE, FALSE) (sleep)", QuickLinkPlan.Would(QuickLinkPlan.Power("sleep", false).Step!));
    }

    sealed class W { public Rig R = null!; public List<QuickLinkPlan.Step> Started = new(); }

    static void T(bool test, Func<W, Task> body) => HostLoop.Run(async (_, time) =>
    {
        var w = new W { R = new Rig(time, test) };
        w.R.Platform.Start();
        var a = typeof(QuickLinksFeature).GetCustomAttribute<FeatureAttribute>()!;
        w.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => new QuickLinksFeature { Starter = s => { w.Started.Add(s); return new LaunchResult(true, s.Note); } }) });
        await w.R.Host.Installed;
        await body(w);
    });

    static async Task<JsonElement> Call(W w, string verb, JsonObject a)
    {
        var r = await w.R.Bus.CallAsync("quicklinks." + verb, a);
        Assert.True(r.Ok, r.Error);
        return r.Result;
    }

    [Fact]
    public void A_test_runs_rows_are_dry_runs_that_say_what_they_would_do() => T(true, async w =>
    {
        var r = await Call(w, "open", new JsonObject { ["link"] = "events" });
        Assert.Equal(("events", true, "dry run: would run ShellExecute(\"eventvwr.msc\")", true),
            (r.GetProperty("link").GetString(), r.GetProperty("ok").GetBoolean(), r.GetProperty("reason").GetString(), r.GetProperty("dryRun").GetBoolean()));
        r = await Call(w, "open", new JsonObject { ["link"] = "apps" });
        Assert.True(r.GetProperty("dryRun").GetBoolean());
        Assert.StartsWith("dry run: would run ShellExecute(\"ms-settings:appsfeatures\")", r.GetProperty("reason").GetString());
        r = await Call(w, "open", new JsonObject { ["link"] = "nope" });
        Assert.Equal(("nope", false, "That quick link isn't one Mantle knows.", false),
            (r.GetProperty("link").GetString(), r.GetProperty("ok").GetBoolean(), r.GetProperty("reason").GetString(), r.TryGetProperty("dryRun", out _)));
        r = await Call(w, "power", new JsonObject { ["action"] = "restart", ["confirmed"] = true });
        Assert.EndsWith("shutdown.exe\" /r /t 0", r.GetProperty("reason").GetString());
        Assert.True(r.GetProperty("dryRun").GetBoolean());
        r = await Call(w, "power", new JsonObject { ["action"] = "restart" });
        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Empty(w.Started);
        Assert.Contains(w.R.Lines, l => l.Contains("quicklinks: events: dry run"));
        Assert.Contains(w.R.Lines, l => l.Contains("quicklinks: power restart: refused (Restart asks first"));
    });

    [Fact]
    public void Desktop_asks_the_helper_and_says_when_it_is_down() => T(true, async w =>
    {
        w.R.Platform.Answer("hotcorners.desktop", "{\"ok\":true,\"wouldCall\":\"x\"}");
        var r = await Call(w, "open", new JsonObject { ["link"] = "desktop" });
        Assert.Equal("dry run: would show the desktop", r.GetProperty("reason").GetString());
        var call = w.R.Platform.Calls.Single(c => c.Verb == "hotcorners.desktop");
        Assert.Contains("\"dryRun\":true", call.ArgsJson);
        await w.R.Platform.StopAsync("gone");
        r = await Call(w, "open", new JsonObject { ["link"] = "desktop" });
        Assert.Equal("Desktop needs the Mantle helper, and it is not connected right now.", r.GetProperty("reason").GetString());
        Assert.False(r.GetProperty("ok").GetBoolean());
    });

    [Fact]
    public void A_live_run_starts_the_planned_step_and_says_the_fallback() => T(false, async w =>
    {
        var r = await Call(w, "open", new JsonObject { ["link"] = "devices" });
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.False(r.TryGetProperty("dryRun", out _));
        Assert.Equal("devmgmt.msc", w.Started.Single().File);
        await Call(w, "power", new JsonObject { ["action"] = "sleep" });
        Assert.Equal("sleep", w.Started[1].Kind);
    });

    [Fact]
    public void Win_X_toggles_the_launchers_quick_links_and_a_missing_launcher_is_said() => T(true, w =>
    {
        var asked = new List<string>();
        try
        {
            QuickLinksFeature.ToggleLauncher = why => { asked.Add(why); return true; };
            w.R.Host.FireChord("Win+X");
            Assert.Equal(new[] { "Win+X" }, asked);
            QuickLinksFeature.ToggleLauncher = null;
            w.R.Host.FireChord("Win+X");
            Assert.Contains(w.R.Lines, l => l.Contains("quicklinks: Win+X: there is no launcher to open"));
        }
        finally { QuickLinksFeature.ToggleLauncher = null; }
        return Task.CompletedTask;
    });
}
