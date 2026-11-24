using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Snap;
using Xunit;

namespace Mantle.Core.Tests.Features.Snap;

// lane r9-snap: the [Feature("snap")] half (lib/features/snap.js; SNAP-001, SNAP-051, SNAP-054, SNAP-100, SNAP-101) on the
// feature host's test rig: a fake sidecar with made-up windows and monitors, never the real one.
public class SnapFeatureTests
{
    static void T(Func<Rig, Task> body, bool test) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D() => FeatureDescriptor.Of(typeof(SnapFeature).GetCustomAttribute<FeatureAttribute>()!, () => new SnapFeature());

    static async Task Settle(Rig r, Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    const string MonA = "{\"id\":\"A\",\"bounds\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1080},\"work\":{\"x\":0,\"y\":0,\"w\":1920,\"h\":1040},\"dpi\":96,\"scale\":1,\"primary\":true}";

    static string Move(string extra, bool ok = true) =>
        "{\"ok\":" + (ok ? "true" : "false") + ",\"hwnd\":\"4242\",\"exe\":\"notes.exe\",\"app\":\"Notes\",\"title\":\"Notes - plan.txt\",\"pid\":77," +
        "\"monitor\":" + MonA + ",\"ms\":3.2,\"moves\":1," + extra + "}";

    static bool Piece(Rig r, Func<Mantle.Core.State.FeaturePiece, bool> test) =>
        r.Hub.Current.Features.TryGetValue("snap", out var p) && test(p);

    [Fact]
    public void The_feature_is_snap_on_by_default_rests_for_games_and_claims_the_four_arrows_but_not_yet_Win_Z()
    {
        var a = typeof(SnapFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("snap", a.Name);
        Assert.Equal("Window snapping (Win+arrows, Win+Z)", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Equal(new[] { "Win+Left", "Win+Right", "Win+Up", "Win+Down" }, a.Chords);
        Assert.DoesNotContain("Win+Z", a.Chords);
    }

    [Fact]
    public void A_chord_moves_the_window_in_front_and_one_that_lands_off_every_monitor_is_put_back() => T(async r =>
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Platform.Answer("snap.monitors", "{\"monitors\":[" + MonA + "]}");
        r.Platform.Answer("snap.move", Move("\"from\":\"left\",\"to\":\"normal\",\"action\":\"unsnap\",\"frame\":{\"x\":2200,\"y\":200,\"w\":800,\"h\":600}"));
        r.Platform.Answer("layouts.place", "{\"hwnd\":\"4242\",\"placed\":true}");
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        await Settle(r, () => Piece(r, p => p.Ok));

        r.Platform.Raise("chord", "{\"chord\":\"Win+Right\"}");
        await Settle(r, () => r.Platform.Calls.Any(c => c.Verb == "layouts.place"));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "snap.move" && c.ArgsJson.Contains("\"dir\":\"right\"", StringComparison.Ordinal));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "layouts.place" && c.ArgsJson.Contains("\"x\":1120", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Lines, l => l.Contains("Notes", StringComparison.OrdinalIgnoreCase));
    }, test: false);

    [Fact]
    public void A_refused_move_is_broadcast_as_a_notice_for_the_snap_surface() => T(async r =>
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Platform.Answer("snap.monitors", "{\"monitors\":[" + MonA + "]}");
        r.Platform.Answer("snap.move", Move("\"from\":\"normal\",\"to\":\"normal\",\"action\":\"snap\",\"reason\":\"notes has a fixed size\"", ok: false));
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        await Settle(r, () => Piece(r, p => p.Ok));

        r.Platform.Raise("chord", "{\"chord\":\"Win+Left\"}");
        await Settle(r, () => r.Events.Published.Any(p => p.Name == SnapFeature.NoticeEvent));
        var n = (JsonElement)r.Events.Published.Last(p => p.Name == SnapFeature.NoticeEvent).Payload!;
        Assert.Equal("Could not snap Notes", n.GetProperty("title").GetString());
        Assert.Equal("Notes has a fixed size.", n.GetProperty("text").GetString());
        Assert.Equal(1920, n.GetProperty("work").GetProperty("w").GetInt32());
    }, test: false);

    [Fact]
    public void An_older_sidecar_without_Snap_cs_marks_the_feature_down_in_words() => T(async r =>
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Host.Load(new[] { D() });   // no snap.monitors: the fake answers "unknown command"
        await r.Host.Installed;
        await Settle(r, () => Piece(r, p => !p.Ok && p.Reason.Contains("older version", StringComparison.Ordinal)));
        Assert.True(Piece(r, p => !p.Ok && p.Reason.Contains("older version", StringComparison.Ordinal)));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "snap.monitors" && c.Timeout == TimeSpan.FromMilliseconds(3000));
    }, test: false);
}
