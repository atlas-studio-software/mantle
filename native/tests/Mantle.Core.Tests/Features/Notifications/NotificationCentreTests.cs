using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Platform;
using Mantle.Core.State;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Notifications;

// lib/notifications.test.js and the bar's notifications rules, ported (lane native-r7-notif): BAR-825..861, BAR-863.
public class NotificationCentreTests
{
    const string List3 = "{\"ok\":true,\"available\":true,\"via\":\"listener+database\",\"rev\":1,\"toasts\":[" +
        "{\"id\":104,\"aumid\":\"com.squirrel.Discord.Discord\",\"app\":\"Discord\",\"title\":\"Alex\",\"opens\":\"app\"}," +
        "{\"id\":103,\"aumid\":\"COM.SQUIRREL.DISCORD.DISCORD\",\"app\":\"Discord\",\"title\":\"Jordan\",\"opens\":\"app\"}," +
        "{\"id\":101,\"aumid\":\"com.readdle.SparkDesktop\",\"app\":\"Spark\",\"title\":\"Calendar\",\"opens\":\"protocol\"}]}";

    sealed class Kit : IDisposable
    {
        public readonly FakePlatform P = new();
        public readonly EventHub Events;
        public readonly List<string> Logs = new();
        public readonly TestDir Dir = new("notifs");
        public readonly List<JsonElement> Pushed = new();
        public readonly ShellStateHub Hub = new(ShellState.Initial(ShellMode.Test, BarEdge.Bottom));
        public NotificationCentre C = null!;
        public string Seen => Dir.File("notifications-seen.json");
        public Kit() { Events = new EventHub(Logs.Add); Events.Subscribe<NotificationsPayload>(NotificationCentre.Event, p => Pushed.Add(p.Body)); }
        public NotificationCentre Make(bool test = false, string? fixture = null, Func<string?>? access = null, SafeFile? files = null)
        {
            C = new NotificationCentre(new NotificationCentreOptions {
                Platform = P, Events = Events, Clock = HostClock.Inline(), Test = test, Log = Logs.Add, Fixture = fixture, AccessProblem = access,
                Files = files ?? new SafeFile(WriteGate.Scratch(Dir.Path)), SeenFile = Seen, State = Hub,
            });
            return C;
        }
        public void Dispose() { C?.Dispose(); Dir.Dispose(); }
    }

    static JsonArray Ids(params long[] ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
    static int Unread(JsonObject p) => (int)p["unread"]!;

    [Fact]
    public async Task Unread_is_what_arrived_since_seen_and_seen_persists_and_prunes()
    {
        using var k = new Kit();
        k.P.Answer("notifications", List3);
        k.P.SetStatus(PlatformState.Ok, "up");
        var c = k.Make();
        c.Start();
        var p = (await c.Push("t"))!;
        Assert.Equal(3, Unread(p));
        Assert.Equal(3, (int)p["count"]!);
        var r = c.Seen(Ids(104, 103, 99999, 104));                      // unknown ids are ignored, not stored
        Assert.True((bool)r["ok"]!);
        Assert.Equal(1, Unread(c.Last!));
        var file = JsonNode.Parse(File.ReadAllText(k.Seen))!["ids"]!.AsArray().Select(x => (long)x!).OrderBy(x => x).ToArray();
        Assert.Equal(new long[] { 103, 104 }, file);
        Assert.False(File.Exists(k.Seen + ".tmp"));
        Assert.Equal("3 notifications, 1 unread (listener+database)", k.Hub.Current.Notifications!.Reason);
        // a restart does not re-badge them; an id no longer listed is pruned from the file
        var c2 = new NotificationCentre(new NotificationCentreOptions { Platform = k.P, Events = new EventHub(_ => { }), Clock = HostClock.Inline(), Files = new SafeFile(WriteGate.Scratch(k.Dir.Path)), SeenFile = k.Seen });
        k.P.Answer("notifications", List3.Replace("{\"id\":104,", "{\"id\":105,"));
        var p2 = (await c2.Push("restart"))!;
        Assert.Equal(2, Unread(p2));                                      // 105 (new) and 101; 103 stays seen
        Assert.DoesNotContain("104", File.ReadAllText(k.Seen));
        c2.Dispose();
    }

    [Fact]
    public async Task A_bad_seen_file_and_an_unsavable_one_are_said_in_the_payload()
    {
        using var k = new Kit();
        File.WriteAllText(k.Seen, "{not json");
        k.P.Answer("notifications", List3);
        k.P.SetStatus(PlatformState.Ok, "up");
        var c = k.Make(files: new SafeFile(WriteGate.Never("a read-only run")));
        c.Start();
        var p = (await c.Push("t"))!;
        Assert.Equal(NotificationCentre.Say.SeenRead, (string)p["seenNote"]!);
        c.Seen(Ids(104));
        Assert.Equal(NotificationCentre.Say.SeenSave, (string)c.Last!["seenNote"]!);
        Assert.Contains(k.Logs, l => l.Contains("could not be saved"));
    }

    [Fact]
    public async Task Only_ids_last_shown_are_acted_on_capped_deduplicated_integers_only()
    {
        using var k = new Kit();
        k.P.Answer("notifications", List3);
        k.P.SetStatus(PlatformState.Ok, "up");
        var c = k.Make();
        c.Start();
        await c.Push("t");
        var asked = new JsonArray(JsonValue.Create(104), JsonValue.Create(104), JsonValue.Create(1.5), JsonValue.Create("101"), JsonValue.Create(77));
        Assert.Equal(new long[] { 104, 101 }, c.KnownIds(asked));
        var many = new JsonArray(Enumerable.Range(0, 600).Select(i => (JsonNode)JsonValue.Create(i)).Append(JsonValue.Create(104)).ToArray());
        Assert.Empty(c.KnownIds(many));                                   // capped at 500 before 104 is reached
        int before = k.P.Calls.Count;
        var r = await c.RemoveAsync(Ids(424242));
        Assert.False((bool)r["ok"]!);
        Assert.Equal(NotificationCentre.Say.NotShown, (string)r["reason"]!);
        Assert.Equal(before, k.P.Calls.Count);                            // refused without calling the sidecar
    }

    [Fact]
    public async Task Clear_all_is_exactly_the_ids_shown_and_an_apps_clear_matches_its_aumid_any_case()
    {
        using var k = new Kit();
        var removes = new List<string>();
        k.P.Answer("notifications", List3);
        k.P.Handle("notifications", a => {
            if (a.GetProperty("op").GetString() == "remove") { removes.Add(a.GetProperty("ids").GetRawText()); return PlatformReply.Success(JsonDocument.Parse("{\"ok\":true,\"removed\":[],\"missing\":[],\"failed\":[]}").RootElement.Clone()); }
            return PlatformReply.Success(JsonDocument.Parse(List3).RootElement.Clone());
        });
        k.P.SetStatus(PlatformState.Ok, "up");
        var c = k.Make();
        c.Start();
        await c.Push("t");
        await c.ClearAsync("com.squirrel.discord.discord");
        await c.ClearAsync(null);
        Assert.Equal(new[] { "[104,103]", "[104,103,101]" }, removes);
        Assert.DoesNotContain(k.P.Calls, x => x.ArgsJson.Contains("\"clear\""));   // never Windows' ClearNotifications()
    }

    [Fact]
    public async Task Failures_are_plain_words_and_the_technical_cause_goes_to_the_log_once()
    {
        using var k = new Kit();
        k.P.Handle("notifications", _ => PlatformReply.Fail(PlatformOutcome.Failed, "HRESULT 0x80070005 in Notifications.cs"));
        k.P.SetStatus(PlatformState.Ok, "up");
        var c = k.Make();
        c.Start();
        var p = (await c.Push("a"))!;
        await c.Push("b");
        Assert.False((bool)p["ok"]!);
        Assert.Equal(NotificationCentre.Say.CannotRead, (string)p["reason"]!);
        Assert.Single(k.Logs, l => l.Contains("the list could not be read"));   // each failure logged once, not every poll
        Assert.Equal("ok", NotificationCentre.PlainOr("  Windows kept it  ", "x") == "Windows kept it" ? "ok" : "no");
        Assert.Equal("x", NotificationCentre.PlainOr("the sidecar said null", "x"));
        Assert.Equal("x", NotificationCentre.PlainOr(new string('a', 301), "x"));
        // an old exe without the reader
        k.P.Handle("notifications", _ => PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: notifications"));
        Assert.Equal(NotificationCentre.Say.OldExe, (string)(await c.Push("c"))!["reason"]!);
        Assert.False(k.Hub.Current.Notifications!.Ok);
    }

    [Fact]
    public async Task The_watch_is_armed_on_ready_a_missing_watch_polls_and_the_same_rev_is_ignored()
    {
        using var k = new Kit();
        int lists = 0;
        k.P.Handle("notifications", a => a.GetProperty("op").GetString() == "watch"
            ? PlatformReply.Success(JsonDocument.Parse("{\"watching\":false,\"reason\":\"no database\"}").RootElement.Clone())
            : (++lists > 0 ? PlatformReply.Success(JsonDocument.Parse(List3).RootElement.Clone()) : default!));
        var c = k.Make();
        c.Start();
        k.P.SetStatus(PlatformState.Ok, "up");                          // the sidecar's ready arms the watch
        Assert.True(SpinWait.SpinUntil(() => lists >= 1, 3000));
        Assert.False(c.Watching);
        Assert.Contains(k.Logs, l => l.Contains("not watching: no database; polling every 10 s"));
        Assert.Contains(NotificationCentre.Say.Slow, (string)c.Last!["detail"]!);
        int now = lists;
        k.P.Raise("notifications", "{\"rev\":1}");                         // the rev already shown: no read
        Assert.Equal(now, lists);
        k.P.Raise("notifications", "{\"rev\":2}");
        Assert.True(SpinWait.SpinUntil(() => lists > now, 3000));
    }

    [Fact]
    public async Task A_test_run_reads_the_fixture_and_every_action_is_a_dry_run()
    {
        using var k = new Kit();
        string fixture = "{\"toasts\":[{\"id\":104,\"aumid\":\"a\",\"app\":\"Discord\",\"unread\":true,\"opens\":\"app\"},{\"id\":90,\"aumid\":\"b\",\"app\":\"Tray\",\"unread\":false,\"opens\":\"none\",\"opensWhy\":\"it came from a notification-area icon\"},{\"id\":101,\"aumid\":\"c\",\"app\":\"Spark\",\"opens\":\"protocol\",\"unread\":false}]}";
        var c = k.Make(test: true, fixture: fixture);
        c.Start();
        var p = (await c.Push("t"))!;
        Assert.Equal(1, Unread(p));                                       // the fixture's own unread flags
        var o = await c.OpenAsync(JsonValue.Create(90));
        Assert.False((bool)o["ok"]!);
        Assert.Equal("it came from a notification-area icon", (string)o["reason"]!);
        var r = await c.RemoveAsync(Ids(104));
        Assert.True((bool)r["ok"]! && (bool)r["dryRun"]!);
        Assert.Contains(k.Logs, l => l.Contains("notifs: dry run (--test): dismiss: would dismiss 1 notification (104) in Windows; nothing was sent"));
        Assert.Equal(2, (int)c.Last!["count"]!);
        o = await c.OpenAsync(JsonValue.Create(101));
        Assert.True((bool)o["ok"]!);
        Assert.Equal("protocol", (string)o["via"]!);
        Assert.Empty(k.P.Calls);                                          // the sidecar was never asked anything
    }

    [Fact]
    public async Task A_test_runs_failures_come_from_the_fixture_or_the_access_check()
    {
        using var k = new Kit();
        var c = k.Make(test: true, fixture: "{\"toasts\":[{\"id\":1,\"app\":\"A\"}],\"fail\":{\"remove\":\"Windows kept it.\",\"seen\":\"the disk is full\"}}");
        c.Start();
        await c.Push("t");
        var r = await c.RemoveAsync(Ids(1));
        Assert.False((bool)r["ok"]!);
        Assert.Single(r["failed"]!.AsArray());
        Assert.False((bool)c.Seen(Ids(1))["ok"]!);
        using var k2 = new Kit();
        var c2 = k2.Make(test: true, access: () => "Windows is not letting apps read your notifications");
        c2.Start();
        var p = (await c2.Push("t"))!;
        Assert.False((bool)p["ok"]!);
        Assert.Equal("Windows is not letting apps read your notifications", (string)p["reason"]!);
        Assert.Empty(k2.P.Calls);
    }

    [Fact]
    public void The_commands_are_registered_with_electrons_names()
    {
        using var k = new Kit();
        var bus = new CommandBus();
        var area = new NotificationArea(new NotificationAreaOptions { Platform = k.P, Events = k.Events, Clock = HostClock.Inline(), Test = true });
        using var _ = NotificationCommands.Register(bus, new NotificationCommands(k.Make(test: true), area));
        foreach (var n in new[] { "notifs.get", "notifs.seen", "notifs.remove", "notifs.clear", "notifs.open", "tray.list", "tray.click", "sys.keys" })
            Assert.True(bus.Has(n), n + " is not registered");
    }
}
