using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features.Notifications;
using Xunit;

namespace Mantle.Core.Tests.Features.Notifications;

// lib/own-banners.test.js, ported (TST-104..TST-107): Mantle's own banners, one per key, buttons to the poster.
public class OwnBannersTests
{
    [Fact]
    public void Each_key_gets_a_stable_negative_id_and_a_repost_of_a_dropped_key_keeps_it_at_the_end()
    {
        var ob = new OwnBanners();
        var events = new List<OwnBannerEvent>();
        ob.OnChange(events.Add);
        long a = ob.Post("update", new OwnBannerPost("Mantle v2 is out"));
        long b = ob.Post("fail", new OwnBannerPost("Mantle was not updated", Tone: "error"));
        Assert.Equal(-1, a); Assert.Equal(-2, b);
        Assert.Equal(-1, ob.Post("update", new OwnBannerPost("Mantle v2 is out", "again")));
        Assert.True(events.Last().Update);                                            // a re-post updates in place
        Assert.True(ob.Drop("update")); Assert.False(ob.Drop("update"));
        Assert.Equal(OwnChange.Drop, events.Last().Type);
        Assert.Equal(-1, ob.Post("update", new OwnBannerPost("back")));
        Assert.Equal(new long[] { -2, -1 }, ob.List().Select(x => x.Id));             // at the end of the list
        Assert.Throws<ArgumentException>(() => ob.Post("", new OwnBannerPost()));
    }

    [Fact]
    public void Clean_keeps_known_fields_with_their_limits_and_names_what_it_left_out()
    {
        var logs = new List<string>();
        var ob = new OwnBanners(logs.Add);
        ob.Post("k", new OwnBannerPost(new string('t', 300), Body: "b", App: null, Tone: "loud", Detail: "",
            Progress: new OwnProgressPost("Installing", 140),
            Actions: new[] { new OwnButtonPost("update", "Update", true), new OwnButtonPost("close", "Close"), new OwnButtonPost("x", null),
                new OwnButtonPost("update", "Again"), new OwnButtonPost("later", "Later"), new OwnButtonPost("skip", "Skip"), new OwnButtonPost("more", "More") }));
        var b = ob.List().Single();
        Assert.Equal(200, b.Title.Length); Assert.EndsWith("…", b.Title);
        Assert.Equal("Mantle", b.App); Assert.Equal("info", b.Tone); Assert.Null(b.Detail);
        Assert.Equal(100, b.Progress!.Pct);
        Assert.Null(OwnBanners.Clean(new OwnBannerPost(Progress: new OwnProgressPost("x", null)), -1).Banner.Progress!.Pct);   // indeterminate
        Assert.Equal(new[] { "update", "later", "skip" }, b.Actions.Select(a => a.Id));
        Assert.True(b.Sticky && b.Own); Assert.Equal("none", b.Opens);
        var said = logs.Single();
        Assert.Contains("the id \"close\"", said); Assert.Contains("a button with no label", said);
        Assert.Contains("a second \"update\" button", said); Assert.Contains("\"more\" (at most 3 buttons)", said);
    }

    [Fact]
    public async Task Buttons_go_to_the_handler_and_the_X_drops_by_default()
    {
        var ob = new OwnBanners();
        var got = new List<string>();
        long id = ob.Post("u", new OwnBannerPost("t", Actions: new[] { new OwnButtonPost("go", "Go") }), w => { got.Add(w); return Task.FromResult<OwnAnswer?>(null); });
        var r = await ob.ActAsync(id, "go");
        Assert.True(r.Ok && r.Keep); Assert.Single(ob.List());                       // a button keeps the card by default
        var x = await ob.ActAsync(id, "close");
        Assert.True(x.Ok); Assert.False(x.Keep); Assert.Empty(ob.List());              // the X drops it
        Assert.Equal(new[] { "go", "close" }, got);
        Assert.Equal("Mantle has no banner -1 up any more", (await ob.ActAsync(id, "go")).Reason);
    }

    [Fact]
    public async Task Answers_and_failures_are_passed_on_in_words()
    {
        var ob = new OwnBanners();
        long id = ob.Post("u", new OwnBannerPost("t", Actions: new[] { new OwnButtonPost("go", "Go"), new OwnButtonPost("boom", "Boom"), new OwnButtonPost("no", "No") }),
            w => w switch
            {
                "go" => Task.FromResult<OwnAnswer?>(new OwnAnswer(Keep: false)),
                "no" => Task.FromResult<OwnAnswer?>(new OwnAnswer(Ok: false)),
                "boom" => throw new InvalidOperationException("the disk is full"),
                _ => throw new InvalidOperationException("the X failed"),
            });
        Assert.Equal("this banner has no \"nope\" button", (await ob.ActAsync(id, "nope")).Reason);
        var no = await ob.ActAsync(id, "no");
        Assert.False(no.Ok); Assert.Equal("it did not work, and no reason came back", no.Reason);
        var boom = await ob.ActAsync(id, "boom");
        Assert.False(boom.Ok); Assert.True(boom.Keep); Assert.Equal("the disk is full", boom.Reason);
        var close = await ob.ActAsync(id, "close");                                   // the X still removes the card if the handler throws
        Assert.False(close.Ok); Assert.Empty(ob.List());
        long id2 = ob.Post("v", new OwnBannerPost("t", Actions: new[] { new OwnButtonPost("go", "Go") }), _ => Task.FromResult<OwnAnswer?>(new OwnAnswer(Keep: false)));
        var go = await ob.ActAsync(id2, "go");
        Assert.True(go.Ok); Assert.False(go.Keep); Assert.Empty(ob.List());          // keep:false drops it
        long id3 = ob.Post("w", new OwnBannerPost("t", Actions: new[] { new OwnButtonPost("go", "Go") }));
        Assert.Equal("nothing is set up to answer that button", (await ob.ActAsync(id3, "go")).Reason);
        Assert.True((await ob.ActAsync(id3, "close")).Ok);                             // no handler: the X still closes
    }

    [Fact]
    public async Task The_same_action_is_refused_while_it_runs_even_across_an_update_in_place()
    {
        var ob = new OwnBanners();
        var gate = new TaskCompletionSource<OwnAnswer?>();
        var post = new OwnBannerPost("Mantle v2 is out", Actions: new[] { new OwnButtonPost("update", "Update", true) });
        long id = ob.Post("u", post, _ => gate.Task);
        var first = ob.ActAsync(id, "update");
        Assert.Equal("that is already running", (await ob.ActAsync(id, "update")).Reason);
        ob.Post("u", post with { Title = "Updating Mantle to v2" }, _ => gate.Task);   // the card becomes the progress card
        Assert.Equal("that is already running", (await ob.ActAsync(id, "update")).Reason);
        gate.SetResult(null);
        Assert.True((await first).Ok);
    }

    [Fact]
    public void A_throwing_listener_is_logged_and_does_not_stop_the_next()
    {
        var logs = new List<string>();
        var ob = new OwnBanners(logs.Add);
        int seen = 0;
        ob.OnChange(_ => throw new InvalidOperationException("bad listener"));
        using var off = ob.OnChange(_ => seen++);
        ob.Post("k", new OwnBannerPost("t"));
        Assert.Equal(1, seen); Assert.Contains(logs, l => l.Contains("bad listener"));
    }
}
