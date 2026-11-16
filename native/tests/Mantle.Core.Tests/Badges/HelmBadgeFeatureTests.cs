using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Badges;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.Badges;

/// <summary>
/// [Feature("helmbadge")] (lib/helm-badge.js feature + createWatcher) over a temp badge file (MANTLE_HELM_BADGE_FILE through
/// the Host's env), never Helm's real file: the watch on the folder (tmp + rename), a missing folder waited for through
/// its parent, the writer's exit heard (no 30 s poll), off, and an unusable sources setting.
/// </summary>
public class HelmBadgeFeatureTests
{
    sealed class B
    {
        public Rig R = null!; public string Dir = ""; public string File = "";
        public JsonElement Badge0 => ((JsonElement)R.Events.Published.Last(p => p.Name == HelmBadgeFeature.StateEvent).Payload!).GetProperty("badges")[0];
        public JsonElement Badges => ((JsonElement)R.Events.Published.Last(p => p.Name == HelmBadgeFeature.StateEvent).Payload!).GetProperty("badges");
        public void Write(string json) { Directory.CreateDirectory(Path.GetDirectoryName(File)!); System.IO.File.WriteAllText(File + ".tmp", json); System.IO.File.Move(File + ".tmp", File, true); }
        /// File events arrive on a pool thread: wait for them, run the 120 ms settle, let the host queue run.
        public async Task Settle() { for (int i = 0; i < 3; i++) { await Task.Delay(150); R.Time.AdvanceMs(HelmBadgeFeature.SettleMs); await Task.Delay(30); } }
        public async Task<bool> Until(Func<bool> ok) { for (int i = 0; i < 40; i++) { if (ok()) return true; await Settle(); } return ok(); }
    }

    static void T(Func<B, Task> body, bool test = true, string? sub = null, bool env = true, Action<B>? before = null) => HostLoop.Run(async (_, time) =>
    {
        var b = new B { R = new Rig(time, test), Dir = Path.Combine(Path.GetTempPath(), "mantle-hb-test-" + Guid.NewGuid().ToString("N")[..8]) };
        Directory.CreateDirectory(b.Dir);
        b.File = sub == null ? Path.Combine(b.Dir, "badge.json") : Path.Combine(b.Dir, sub, "badge.json");
        try
        {
            b.R.Platform.Start();
            before?.Invoke(b);
            var h = new HelmBadgeFeature.Host(new HostClock(time), k => k == HelmBadgeFeature.FileEnv ? (env ? b.File : null) : k == "APPDATA" ? Path.Combine(b.Dir, "appdata") : null);
            var a = typeof(HelmBadgeFeature).GetCustomAttribute<FeatureAttribute>()!;
            b.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => new HelmBadgeFeature(h)) });
            await b.R.Host.Installed;
            await body(b);
            b.R.Host.SetEnabled("helmbadge", false, "test end");
        }
        finally { try { Directory.Delete(b.Dir, true); } catch (IOException) { } }
    });

    [Fact]
    public void The_module_fields_are_Electrons()
    {
        var a = typeof(HelmBadgeFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("helmbadge", "App counts on the bar (Helm: chats that need you)", true, false), (a.Name, a.Label, a.DefaultOn, a.Rest));
    }

    [Fact]
    public void A_test_run_without_the_fixture_reads_no_file() => T(async b =>
    {
        await Task.Yield();
        Assert.Equal(0, b.Badges.GetArrayLength());
        Assert.Contains(b.R.Lines, l => l.Contains("MANTLE_HELM_BADGE_FILE is not set, so no app's count is read"));
    }, env: false);

    [Fact]
    public void The_count_follows_the_file_through_tmp_and_rename_and_the_log_names_only_the_place() => T(async b =>
    {
        Assert.Equal(3, b.Badge0.GetProperty("count").GetInt32());
        Assert.Equal("3 need you: 2 waiting, 1 stopped on an error", b.Badge0.GetProperty("tip").GetString());
        Assert.Equal("helm.exe", b.Badge0.GetProperty("exe").GetString());
        b.Write("{\"v\":1,\"waiting\":1}");
        Assert.True(await b.Until(() => b.Badge0.GetProperty("count").GetInt32() == 1), "the watch did not see the rename");
        Assert.Equal("1 needs you: 1 waiting", b.Badge0.GetProperty("tip").GetString());
        System.IO.File.WriteAllText(b.File, "{not json");
        Assert.True(await b.Until(() => b.Badge0.GetProperty("reason").GetString() == HelmBadge.Unreadable), "an unreadable file was not said");
        Assert.Contains(b.R.Lines, l => l.Contains("source 1: count 1"));
        Assert.DoesNotContain(b.R.Lines, l => l.Contains("Helm", StringComparison.Ordinal) && l.Contains("count"));
    }, before: b => b.Write("{\"v\":1,\"waiting\":2,\"errors\":1}"));

    [Fact]
    public void A_missing_folder_is_waited_for_through_its_parent_not_a_timer() => T(async b =>
    {
        Assert.Equal(HelmBadge.NotRunning, b.Badge0.GetProperty("reason").GetString());
        b.Write("{\"v\":1,\"count\":2}");
        Assert.True(await b.Until(() => b.Badge0.GetProperty("count").GetInt32() == 2), "the folder's creation was not heard");
    }, sub: "Helm");

    [Fact]
    public void A_count_hides_when_its_writer_exits_heard_not_polled() => T(async b =>
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true })!;
        b.Write($"{{\"v\":1,\"count\":4,\"pid\":{p.Id}}}");
        Assert.True(await b.Until(() => b.Badge0.GetProperty("count").GetInt32() == 4), "a live writer's count did not show");
        p.WaitForExit(15000);
        Assert.True(await b.Until(() => b.Badge0.GetProperty("count").GetInt32() == 0), "the writer's exit was not heard");
        Assert.Equal(HelmBadge.NotRunning, b.Badge0.GetProperty("reason").GetString());
    }, before: b => b.Write("{\"v\":1,\"count\":0}"));

    [Fact]
    public void Switched_off_the_count_is_zero_and_says_turned_off() => T(async b =>
    {
        await Task.Yield();
        b.R.Host.SetEnabled("helmbadge", false, "test");
        Assert.Equal(0, b.Badge0.GetProperty("count").GetInt32());
        Assert.Equal("turned off", b.Badge0.GetProperty("reason").GetString());
    }, before: b => b.Write("{\"v\":1,\"count\":5}"));

    [Fact]
    public void An_unusable_sources_setting_is_a_health_failure_in_words() => T(async b =>
    {
        await Task.Yield();
        Assert.Equal(0, b.Badges.GetArrayLength());
        var piece = b.R.Hub.Piece("helmbadge");
        Assert.False(piece.Ok);
        Assert.Contains("its sources setting has no usable entry (exe + absolute file)", piece.Reason);
    }, before: b => b.R.Stores.Get("features\\helmbadge.json").SetAsync("sources", JsonNode.Parse("[{\"exe\":\"x.exe\",\"file\":\"relative.json\"}]")).AsTask().Wait());
}
