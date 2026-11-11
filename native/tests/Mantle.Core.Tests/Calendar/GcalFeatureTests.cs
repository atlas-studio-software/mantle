using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Features.Calendar;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.Calendar;

/// The [Feature("gcal")] half (lib/features/gcal.js) on the feature host's test rig: its commands, the --test
/// refusals and the MANTLE_TEST_GCAL fixture. One class, so the static host wiring and the fixture variable are
/// never shared between two tests at once. The data folder is a scratch one; nothing reaches Google.
public class GcalFeatureTests
{
    static readonly string[] Commands = { "gcal.status", "gcal.signIn", "gcal.signOut", "gcal.pickClient", "gcal.setCalendar", "gcal.opened", "gcal.want", "gcal.open" };

    const string Fixture = """
        {"status":{"enabled":true,"client":true,"connected":true,"signedIn":true,"account":"me@example.com","calendars":[{"id":"me@example.com","name":"me@example.com","color":"#4285f4","primary":true,"on":true}],"syncedAt":1,"syncing":false,"error":null},
         "events":{"ver":1,"spans":[],"calendars":[{"id":"me@example.com","color":"#4285f4","name":"me@example.com"}],
                   "events":[{"k":"14:me@example.come1","cal":"me@example.com","title":"Dentist","allDay":false,"s":1794416400000,"e":1794420000000,"link":"https://www.google.com/calendar/event?eid=e1"},
                             {"k":"14:me@example.come2","cal":"me@example.com","title":"Trip","allDay":true,"s":"2026-11-12","e":"2026-11-14"}]}}
        """;

    sealed class Opened { public readonly List<string> Urls = new(); }

    static void T(Func<Rig, TestDir, Opened, Task> body, bool test = true, string? fixture = null) => HostLoop.Run(async (_, time) => {
        using var d = new TestDir("gcal-feature");
        var opened = new Opened();
        GcalFeature.Configure(new GcalFeature.Host(d.Path, new SafeFile(WriteGate.Scratch(d.Path)), OpenExternal: u => { opened.Urls.Add(u); return null; }));
        Environment.SetEnvironmentVariable(GcalFeature.FixtureEnv, fixture);
        try { await body(new Rig(time, test), d, opened); }
        finally { Environment.SetEnvironmentVariable(GcalFeature.FixtureEnv, null); }
    });

    static FeatureDescriptor D() => FeatureDescriptor.Of(typeof(GcalFeature).GetCustomAttribute<FeatureAttribute>()!, () => new GcalFeature());

    static async Task<JsonElement> Call(Rig r, string name, string args = "{}")
    {
        var res = await r.Bus.CallAsync(name, JsonNode.Parse(args) as JsonObject);
        Assert.True(res.Ok, name + ": " + res.Error);
        return res.Result;
    }

    static bool B(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static JsonElement[] Pushed(Rig r, string name) => r.Events.Published.Where(p => p.Name == name).Select(p => (JsonElement)p.Payload!).ToArray();

    [Fact]
    public void The_module_fields_are_Electrons_and_the_eight_commands_are_there() => T(async (r, _, _) =>
    {
        var a = typeof(GcalFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("gcal", a.Name);
        Assert.Equal("Google Calendar in the clock's calendar", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        Assert.True(r.Hub.Piece("gcal").Ok);
        foreach (var c in Commands) Assert.True(r.Bus.Has(c), c + " is not registered");
        Assert.True(r.Events.HasReplay("gcal.status"));
        Assert.True(r.Events.HasReplay("gcal.events"));
    });

    [Fact]
    public void A_test_run_answers_sign_in_sign_out_the_file_dialog_and_open_with_dry_runs_and_writes_and_sends_nothing() => T(async (r, d, opened) =>
    {
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        foreach (var c in new[] { "gcal.signIn", "gcal.signOut", "gcal.pickClient" }) {
            var o = await Call(r, c);
            Assert.True(B(o, "ok"), c);
            Assert.True(B(o, "dryRun"), c);
            Assert.StartsWith("test run", S(o, "reason"));
        }
        Assert.True(r.Logged("dry run (--test): Sign in would open Google's consent page"));
        Assert.True(r.Logged("dry run (--test): Sign out would revoke at Google and delete token.bin and cache.json"));
        var want = await Call(r, "gcal.want", "{\"year\":2026,\"month\":10}");
        Assert.False(B(want, "covered"));
        Assert.Equal(GcalFeature.TestWhy, S(want, "reason"));
        Assert.False(B(await Call(r, "gcal.opened"), "synced"));
        var st = await Call(r, "gcal.status");
        Assert.True(B(st, "test"));
        Assert.False(B(st, "signedIn"));
        Assert.Empty(opened.Urls);   // no browser
        Assert.False(Directory.Exists(Path.Combine(d.Path, "gcal")) && Directory.GetFiles(Path.Combine(d.Path, "gcal")).Length > 0);
    });

    [Fact]
    public void The_fixture_is_drawn_never_Googles_and_a_rows_link_stays_in_the_feature() => T(async (r, _d, opened) =>
    {
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        Assert.True(r.Logged("MANTLE_TEST_GCAL: the calendar draws its made-up status and 2 events"));
        var st = await Call(r, "gcal.status");
        Assert.Equal("me@example.com", S(st, "account"));
        Assert.True(B(st, "test"));
        var ev = (JsonElement)r.Events.ReplayValue("gcal.events")!;
        var rows = ev.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.False(row.TryGetProperty("link", out _)));   // privacy claim 2: the bar never holds a link
        // only the calendar's own two events ever go out
        Assert.All(r.Events.Published.Where(p => p.Name.StartsWith("gcal.", StringComparison.Ordinal)), p => Assert.Contains(p.Name, new[] { GcalFeature.StatusEvent, GcalFeature.EventsEvent }));
        Assert.NotEmpty(Pushed(r, GcalFeature.StatusEvent));

        var open = await Call(r, "gcal.open", "{\"event\":\"14:me@example.come1\"}");
        Assert.True(B(open, "dryRun"));
        Assert.True(r.Logged("dry run (--test): an event's Google page would open"));
        Assert.False(B(await Call(r, "gcal.open", "{\"event\":\"nope\"}"), "ok"));
        Assert.Contains("gave no web page", S(await Call(r, "gcal.open", "{\"event\":\"14:me@example.come2\"}"), "reason"));
        Assert.True(B(await Call(r, "gcal.setCalendar", "{\"calendar\":\"me@example.com\",\"on\":false}"), "dryRun"));
        Assert.Empty(opened.Urls);
    }, fixture: Fixture);

    [Fact]
    public void Turning_the_feature_off_pauses_it_and_the_status_says_so() => T(async (r, _, _) =>
    {
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        r.Host.SetEnabled("gcal", false, "test");
        await Task.Yield();
        var last = Pushed(r, GcalFeature.StatusEvent).Last();
        Assert.False(B(last, "enabled"));
        var want = await Call(r, "gcal.want", "{\"year\":2026,\"month\":10}");
        Assert.False(B(want, "covered"));
        r.Host.SetEnabled("gcal", true, "test");
        await Task.Yield();
        Assert.True(B(Pushed(r, GcalFeature.StatusEvent).Last(), "enabled"));
    });

    [Fact]
    public void A_live_run_with_no_client_file_says_so_and_the_native_file_dialog_is_not_there_yet() => T(async (r, d, opened) =>
    {
        r.Host.Load(new[] { D() });
        await r.Host.Installed;
        var s = await Call(r, "gcal.signIn");
        Assert.False(B(s, "ok"));
        Assert.Contains("client file", S(s, "reason"));
        var p = await Call(r, "gcal.pickClient");
        Assert.False(B(p, "ok"));
        Assert.Contains("not in the native app yet", S(p, "reason"));
        var o = await Call(r, "gcal.signOut");   // nothing to revoke, nothing to delete: still fine
        Assert.True(B(o, "ok"));
        Assert.Empty(opened.Urls);
        Assert.False(File.Exists(Path.Combine(d.Path, "gcal", "token.bin")));
    }, test: false);
}
