using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Features.Toasts;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Toasts;

// The [Feature("toasts")] half (lib/features/toasts.js) on the feature host's rig: a fake sidecar (the notification list,
// toasts.quiet, open), the registry and record fakes of part 1 for a live rig. THE TAKEOVER GATE is the point of the first
// two tests: a --test run never makes an owner (no registry read or write can happen), a --live run takes and puts back.
public class ToastsFeatureTests
{
    const string D = ToastFixtures.D;

    sealed class World
    {
        public FakeToastClock Clock = new(1_000_000);
        public FakeReg Reg = ToastFixtures.Apps();
        public FakeStore Store = new();
        public OwnBanners Own = new();
        public string Quiet = "{\"dnd\":0,\"quns\":5}";
        public string List = "{\"ok\":true,\"access\":\"Allowed\",\"toasts\":[]}";
        public int Scripts;
    }

    static void T(Func<Rig, World, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test), new World()));

    static FeatureDescriptor Desc(World w, Action<ToastsFeature>? made = null)
    {
        var a = typeof(ToastsFeature).GetCustomAttribute<FeatureAttribute>()!;
        return FeatureDescriptor.Of(a, () => {
            var f = new ToastsFeature(w.Clock, w.Own, w.Reg, w.Store,
                () => { w.Scripts++; return Task.FromResult(new ScriptOutput(0, "1 put back to Windows' own pop-up, 0 left\nverified")); },
                () => { w.Scripts++; return new ScriptOutput(0, "1 put back to Windows' own pop-up, 0 left\nverified"); });
            made?.Invoke(f); return f;
        });
    }

    static void Sidecar(Rig r, World w)
    {
        r.Platform.Handle("notifications", a => {
            string op = a.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
            return PlatformReply.Success(FeatureOptions.Parse(op == "list" ? w.List : op == "open" ? "{\"ok\":true}" : "{\"ok\":true}"));
        });
        r.Platform.Handle("toasts.quiet", _ => PlatformReply.Success(FeatureOptions.Parse(w.Quiet)));
    }

    static async Task Settle() { for (int i = 0; i < 20; i++) await Task.Yield(); }

    static string ListOf(params (long Id, string Aumid)[] ts) =>
        new JsonObject { ["ok"] = true, ["access"] = "Allowed", ["toasts"] = new JsonArray(ts.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["aumid"] = t.Aumid, ["app"] = "Discord", ["title"] = "hi " + t.Id }).ToArray()) }.ToJsonString();

    static ToastsShow[] Shows(Rig r) => r.Events.Published.Where(p => p.Name == "toasts.show").Select(p => (ToastsShow)p.Payload!).ToArray();

    [Fact]
    public void The_module_fields_are_Electrons()
    {
        var a = typeof(ToastsFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("toasts", a.Name);
        Assert.Equal("Show notifications in Mantle's style", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Empty(a.Chords);
    }

    [Fact]
    public void A_test_run_makes_no_owner_and_never_reads_or_writes_Windows_banner_settings() => T(async (r, w) =>
    {
        Sidecar(r, w);
        ToastsFeature? f = null;
        r.Host.Load(new[] { Desc(w, x => f = x) });
        await r.Host.Installed;
        r.Platform.Start();
        w.List = ListOf((1, D));
        r.Platform.Raise("notifications");
        await r.Host.ApplyLiveAsync("the sidecar is ready");
        w.List = ListOf((1, D), (2, D));
        r.Platform.Raise("notifications");
        await Settle();
        w.Clock.Advance(5000);
        await Settle();
        Assert.Null(f!.Owner);
        Assert.Empty(w.Reg.Writes);                                         // nothing of Windows' written
        Assert.Null(w.Store.File);                                          // no record either
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "toasts.banners");
        Assert.Empty(Shows(r));                                             // an app's toast is never Mantle's in --test
        foreach (var c in new[] { "toasts.layout", "toasts.ready", "toasts.click", "toasts.more", "toasts.action", "toasts.copy", "toasts.state", "toasts.testShow", "toasts.testOwn", "toasts.testIds" })
            Assert.True(r.Bus.Has(c), c + " is not registered");
        var st = await r.Bus.CallAsync("toasts.state");
        Assert.False(st.Result.GetProperty("takes").GetBoolean());
        Assert.False(st.Result.GetProperty("live").GetBoolean());
    });

    [Fact]
    public void A_live_run_takes_the_covered_apps_shows_their_new_toasts_and_puts_back_on_stop() => T(async (r, w) =>
    {
        Sidecar(r, w);
        ToastsFeature? f = null;
        r.Host.Load(new[] { Desc(w, x => f = x) });
        await r.Host.Installed;
        Assert.False(r.Bus.Has("toasts.testShow"));                         // no test commands in a live run
        w.List = ListOf((1, D));
        r.Platform.Start();
        await Settle();
        Assert.NotNull(f!.Owner);
        Assert.Contains(w.Reg.Writes, s => s.Aumid == D && s.Value == 0);     // Discord's Windows banner off
        Assert.True(f.Owner!.Owns(D));
        Assert.Empty(Shows(r));                                             // the first list is only remembered
        w.List = ListOf((1, D), (2, D));
        r.Platform.Raise("notifications");
        await Settle();
        var s = Assert.Single(Shows(r));
        Assert.Equal(2, Assert.Single(s.Banners).Id);
        Assert.Equal("hi 2", s.Banners[0].Title);
        // Windows would be quiet: no banner
        w.Quiet = "{\"dnd\":1,\"quns\":5}";
        w.List = ListOf((1, D), (2, D), (3, D));
        r.Platform.Raise("notifications");
        await Settle();
        Assert.Single(Shows(r));
        Assert.Contains(r.Lines, l => l.Contains("no banner while Do not disturb is on"));
        // stopped (not a rest): every banner put back
        await r.Host.StopAllAsync("the test is over");
        await Settle();
        Assert.Contains(w.Reg.Writes, x => x.Aumid == D && x.Value == 1);     // back to its own 1
        Assert.False(f.Owner.Owns(D));
    }, test: false);

    [Fact]
    public void Switched_off_puts_back_and_the_apps_cards_go_but_Mantles_own_stay() => T(async (r, w) =>
    {
        Sidecar(r, w);
        ToastsFeature? f = null;
        r.Host.Load(new[] { Desc(w, x => f = x) });
        await r.Host.Installed;
        w.List = ListOf((1, D));
        r.Platform.Start();
        await Settle();
        long own = w.Own.Post("update", new OwnBannerPost("Mantle v0.2 is out", "Restart to update."));
        await Settle();
        r.Events.Published.Clear();
        r.Host.SetEnabled("toasts", false, "the test");
        await Settle();
        var ids = r.Events.Published.Where(p => p.Name == "toasts.ids").Select(p => (ToastsIds)p.Payload!).Last();
        Assert.Equal(new[] { own }, ids.Ids);
        Assert.Contains(w.Reg.Writes, x => x.Aumid == D && x.Value == 1);
    }, test: false);

    [Fact]
    public void Mantles_own_banner_shows_updates_in_place_drops_and_is_held_while_Windows_is_quiet() => T(async (r, w) =>
    {
        Sidecar(r, w);
        r.Host.Load(new[] { Desc(w) });
        await r.Host.Installed;
        r.Platform.Start();
        await Settle();
        w.Quiet = "{\"dnd\":1,\"quns\":5}";
        long id = w.Own.Post("upd", new OwnBannerPost("Mantle v0.2 is out", "Restart to update.", Actions: new[] { new OwnButtonPost("go", "Update", true) }));
        await Settle();
        Assert.Empty(Shows(r));
        Assert.Contains(r.Lines, l => l.Contains("Mantle's own banner held while Do not disturb is on"));
        w.Quiet = "{\"dnd\":0,\"quns\":5}";
        w.Clock.Advance(ToastsFeature.OwnRetryMs);
        await Settle();
        var card = Assert.Single(Assert.Single(Shows(r)).Banners);
        Assert.True(card.Own); Assert.True(card.Sticky); Assert.Equal(id, card.Id);
        Assert.Equal("go", Assert.Single(card.Actions).Id);
        w.Own.Post("upd", new OwnBannerPost("Downloading", "Mantle v0.2", Progress: new OwnProgressPost("12 of 40 MB", 30)));
        var up = (ToastsUpdate)r.Events.Published.Last(p => p.Name == "toasts.update").Payload!;
        Assert.Equal("Downloading", up.Banner.Title);
        Assert.Equal(30, up.Banner.Progress!.Pct);
        w.Own.Drop("upd");
        Assert.Equal(id, ((ToastsDrop)r.Events.Published.Last(p => p.Name == "toasts.drop").Payload!).Id);
    });

    [Fact]
    public void The_X_on_Mantles_own_banner_goes_to_its_poster_and_copy_never_touches_the_clipboard_in_a_test_run() => T(async (r, w) =>
    {
        Sidecar(r, w);
        r.Host.Load(new[] { Desc(w) });
        await r.Host.Installed;
        r.Platform.Start();
        string? pressed = null;
        long id = w.Own.Post("err", new OwnBannerPost("Update failed", "See the log.", Detail: "C:\\log.txt"), a => { pressed = a; return Task.FromResult<OwnAnswer?>(new OwnAnswer(Keep: false)); });
        await Settle();
        var copy = await r.Bus.CallAsync("toasts.copy", new JsonObject { ["id"] = id });
        Assert.False(copy.Result.GetProperty("ok").GetBoolean());
        Assert.True(copy.Result.GetProperty("test").GetBoolean());
        var x = await r.Bus.CallAsync("toasts.action", new JsonObject { ["id"] = id, ["action"] = "close" });
        Assert.True(x.Result.GetProperty("ok").GetBoolean());
        Assert.False(x.Result.GetProperty("keep").GetBoolean());
        Assert.Equal("close", pressed);
        Assert.Empty(w.Own.List());
    });

    [Fact]
    public void No_notification_access_is_unhealthy_with_the_fix_and_takes_nothing() => T(async (r, w) =>
    {
        Sidecar(r, w);
        r.Host.Load(new[] { Desc(w) });
        await r.Host.Installed;
        w.List = "{\"ok\":true,\"access\":\"Denied\",\"toasts\":[]}";
        r.Platform.Start();
        await Settle();
        Assert.Empty(w.Reg.Writes);
        Assert.False(r.Hub.Piece("toasts").Ok);
        Assert.Contains("Privacy > Notifications", r.Hub.Piece("toasts").Reason);
        var st = await r.Bus.CallAsync("toasts.state");
        Assert.Equal("Banners: Windows' own", st.Result.GetProperty("status").GetProperty("line").GetString());
    }, test: false);

    [Fact]
    public void The_helper_going_down_puts_banners_back_after_the_grace_and_says_so() => T(async (r, w) =>
    {
        Sidecar(r, w);
        r.Host.Load(new[] { Desc(w) });
        await r.Host.Installed;
        w.List = ListOf((1, D));
        r.Platform.Start();
        await Settle();
        r.Platform.SetStatus(PlatformState.Down, "the sidecar exited (code 1)");
        await Settle();
        Assert.False(r.Hub.Piece("toasts").Ok);
        Assert.Contains("Banners: none", r.Hub.Piece("toasts").Reason);
        w.Clock.Advance(BannerSafety.GraceMs);
        await Settle();
        Assert.Equal(1, w.Scripts);                                         // restore-banners.ps1 ran
    }, test: false);

    [Fact]
    public void The_status_line_says_whose_banners_and_why()
    {
        string H(double _) => "12:00";
        Assert.Equal("Banners: Windows' own", ToastsStatus.Of(false, null, null, true, null, true, null, 0, H).Line);
        var none = ToastsStatus.Of(true, null, null, false, new SafetyState(new SafetyDown("x", 0, false), true, false, null, 8000), true, null, 0, H);
        Assert.Equal("none", none.Mode);
        Assert.Equal("Mantle's notification helper stopped at 12:00; Windows' own pop-ups come back within 8 s unless it starts", none.Why);
        var ok = ToastsStatus.Of(true, null, null, true, null, true, null, 0, H);
        Assert.Equal(("mantle", "ok", (string?)null), (ok.Mode, ok.Tone, ok.Why));
        var slow = ToastsStatus.Of(true, null, "Access denied.", true, null, false, "The watch threw", 0, H);
        Assert.Equal("warn", slow.Tone);
        Assert.Contains("because the watch threw", slow.Why);
        Assert.Contains("some apps still get Windows' own pop-up too: access denied", slow.Why);
    }

    [Fact]
    public void A_list_read_is_parsed_with_its_access_and_a_failed_one_says_why()
    {
        var l = NotificationList.Parse(PlatformReply.Success(FeatureOptions.Parse("{\"ok\":true,\"access\":\"Allowed\",\"toasts\":[{\"id\":7,\"aumid\":\"a\",\"timeMs\":5},{\"id\":\"x\"}]}")));
        Assert.True(l.Ok); Assert.Equal("Allowed", l.Access);
        Assert.Equal(7, Assert.Single(l.Toasts).Id);
        var bad = NotificationList.Parse(PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: notifications"));
        Assert.False(bad.Ok); Assert.Equal("unknown command: notifications", bad.Reason);
    }
}
