using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Microsoft.Win32;
using Xunit;

namespace Mantle.Core.Tests.Look;

// The look as App.xaml.cs wires it (main.js getTheme + getLook + COMMANDS theme.* / look.*): the theme core and the
// look host behind one lock, the generated commands, the "theme" / "look" rebroadcast on the host thread, Match
// Windows through a scratch key, and the --theme pin. Real host thread, real folder watcher, scratch folders only.
public class LookShellTests
{
    sealed class Rig : IDisposable
    {
        public readonly TestDir Dir = new("shell");
        public readonly ListLog Log = new();
        public readonly Stores Stores;
        public readonly Mantle.Core.Lifecycle.HostLoop Loop = new();
        public readonly CommandBus Bus = new();
        public readonly EventHub Events;
        public readonly LookShell Shell;
        public volatile string Windows = "light";
        public readonly IDisposable Commands;
        public string LookDir => Dir.File("look-sync");
        public Rig(string? pinned = null, Func<WindowsMode>? windows = null)
        {
            Events = new EventHub(Log.Line);
            Stores = new Stores(Dir.Path, new SafeFile(WriteGate.Scratch(Dir.Path)), Log);
            Shell = new LookShell(new LookShellOptions {
                Stores = Stores, Files = new SafeFile(WriteGate.Scratch(Dir.Path)), LookDir = LookDir,
                WindowsMode = windows ?? (() => new WindowsMode(Windows, null)), Post = Loop.Post, Events = Events, Log = Log, PinnedTheme = pinned,
                Tune = o => o with { DebounceMs = 30, RetryDelays = new[] { 5, 10, 20 }, CorruptRetryMs = 60 },
            });
            Commands = Catalogs.Register(Bus, Shell);
        }
        /// Run on the host thread and wait for the answer.
        public Task<T> On<T>(Func<Task<T>> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loop.Post(async () => { try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task;
        }
        public Task<CommandResult> Call(string name, JsonObject? args = null) => On(() => Bus.CallAsync(name, args, "test").AsTask());
        public Task<ThemePayload> NextTheme(Func<ThemePayload, bool> want)
        {
            var tcs = new TaskCompletionSource<ThemePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            IDisposable? sub = null;
            Loop.Post(() => sub = Events.Subscribe<ThemePayload>("theme", p => { if (want(p)) tcs.TrySetResult(p); }));
            _ = tcs.Task.ContinueWith(_ => Loop.Post(() => sub?.Dispose()), TaskScheduler.Default);
            return tcs.Task;
        }
        public JsonObject? Shared() { try { return JsJson.Parse(File.ReadAllText(Path.Combine(LookDir, "look.json"))) as JsonObject; } catch (IOException) { return null; } }
        public async Task Until(Func<bool> ok, string what, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (!ok()) { if (sw.ElapsedMilliseconds > ms) throw new Xunit.Sdk.XunitException("timed out waiting for " + what + "\n" + Log.All); await Task.Delay(10); }
        }
        public void Dispose() { Loop.Post(() => { Commands.Dispose(); Shell.Dispose(); }); Thread.Sleep(50); Loop.Dispose(); Stores.Dispose(); Dir.Dispose(); }
    }

    static async Task<T> Within<T>(Task<T> t, int ms, string what)
    {
        if (await Task.WhenAny(t, Task.Delay(ms)) != t) throw new Xunit.Sdk.XunitException($"{what} did not come within {ms} ms");
        return await t;
    }

    static string? ThemeOf(JsonObject? doc) => doc?["look"]?["theme"]?["v"]?.GetValue<string>();
    static string? ThemeBy(JsonObject? doc) => doc?["look"]?["theme"]?["by"]?.GetValue<string>();

    [Fact]
    public async Task Every_theme_and_look_command_is_registered_by_the_generated_catalog()
    {
        using var r = new Rig();
        foreach (var n in new[] { "theme.get", "theme.set", "look.get", "look.sync", "look.glass", "look.accent" }) {
            Assert.True(r.Bus.Has(n), n + " is not registered");
            Assert.Contains((n, "LookShell", false), Catalogs.Commands);
        }
        var get = await r.Call("theme.get");
        // LOOK-012: lib/theme.js view(), key for key and in its order
        Assert.Equal(new[] { "ok", "mode", "theme", "windows", "windowsWhy", "pinned", "note", "reason" }, get.Result.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.True(get.Result.GetProperty("ok").GetBoolean());
        Assert.Equal("light", get.Result.GetProperty("windows").GetString());
        Assert.Equal("system", get.Result.GetProperty("mode").GetString());
        Assert.Equal("light", get.Result.GetProperty("theme").GetString());
        var bad = await r.Call("theme.set", new JsonObject { ["mode"] = "blue" });
        Assert.False(bad.Result.GetProperty("ok").GetBoolean());
        var noHue = await r.Call("look.accent", new JsonObject());
        Assert.Equal("the accent change came without a hue (null for Mantle's own)", noHue.Result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Theme_set_rebroadcasts_the_theme_within_1_s_saves_it_and_shares_it_once()
    {
        using var r = new Rig();
        r.Shell.Start();
        await r.Until(() => r.Shared() != null, "the seed of look.json");
        var dark = r.NextTheme(p => p.Theme == "dark");
        var sw = Stopwatch.StartNew();
        var set = await r.Call("theme.set", new JsonObject { ["mode"] = "dark" });
        // LOOK-011: the full view plus value
        Assert.Equal(new[] { "ok", "mode", "theme", "windows", "windowsWhy", "pinned", "note", "reason", "value" }, set.Result.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.Equal("dark", set.Result.GetProperty("theme").GetString());
        Assert.Equal("dark", set.Result.GetProperty("value").GetString());
        var p = await Within(dark, 1000, "the theme event after theme.set");
        Assert.True(sw.ElapsedMilliseconds < 1000, "theme event after " + sw.ElapsedMilliseconds + " ms");
        Assert.Equal("dark", p.View.Mode);
        Assert.Equal("dark", r.Shell.Now);
        await r.Until(() => ThemeOf(r.Shared()) == "dark", "look.json to share the theme");
        Assert.Equal("mantle", ThemeBy(r.Shared()));
        Assert.Contains("\"mode\": \"dark\"", File.ReadAllText(r.Dir.File("theme.json")));
    }

    [Fact]
    public async Task Another_apps_theme_reaches_the_theme_event_within_1_s_and_is_not_shared_back()
    {
        using var r = new Rig();
        r.Shell.Start();
        await r.Until(() => r.Shared() != null, "the seed of look.json");
        await Task.Delay(100);
        using var helm = new LookSync(new LookSyncOptions { App = "helm", Dir = r.LookDir, Files = new SafeFile(WriteGate.Scratch(r.Dir.Path)) });
        var dark = r.NextTheme(p => p.Theme == "dark");
        var sw = Stopwatch.StartNew();
        Assert.True((await helm.PublishAsync("theme", JsonValue.Create("dark"))).Ok);
        await Within(dark, 1000, "Helm's theme as the theme event");
        Assert.True(sw.ElapsedMilliseconds < 1000, "followed after " + sw.ElapsedMilliseconds + " ms");
        Assert.Equal("dark", (await r.Call("theme.get")).Result.GetProperty("mode").GetString());
        await Task.Delay(300);
        Assert.Equal("helm", ThemeBy(r.Shared()));   // applied from sync, never re-shared as Mantle's own pick
        Assert.Contains("look: following Helm", r.Log.All);
    }

    [Fact]
    public async Task A_pinned_test_run_never_takes_a_shared_theme()
    {
        using var r = new Rig(pinned: "light");
        r.Shell.Start();
        await r.Until(() => r.Shared() != null, "the seed of look.json");
        await Task.Delay(100);
        using var helm = new LookSync(new LookSyncOptions { App = "helm", Dir = r.LookDir, Files = new SafeFile(WriteGate.Scratch(r.Dir.Path)) });
        Assert.True((await helm.PublishAsync("theme", JsonValue.Create("dark"))).Ok);
        await r.Until(() => r.Log.All.Contains("this test run is pinned by --theme=light"), "the pin's refusal");
        Assert.Equal("light", r.Shell.Now);
        Assert.False(File.Exists(r.Dir.File("theme.json")));   // a pinned run never saves
    }

    [Fact]
    public async Task Match_Windows_follows_a_scratch_key_through_the_shell_within_1_s_and_is_not_shared()
    {
        if (!OperatingSystem.IsWindows()) return;
        string sub = @"Software\Mantle-native-tests\shell-" + Guid.NewGuid().ToString("N")[..8];
        using (var key = Registry.CurrentUser.CreateSubKey(sub)) key.SetValue(ThemeCore.AppsUseLightTheme, 1, RegistryValueKind.DWord);
        try {
            Assert.Equal(sub, ThemeCore.KeyFor(true, n => n == "MANTLE_TEST_THEME_KEY" ? sub : null));
            Assert.Equal(ThemeCore.PersonalizeKey, ThemeCore.KeyFor(false, n => n == "MANTLE_TEST_THEME_KEY" ? sub : null));   // never in a live run
            using var r = new Rig(windows: () => ThemeCore.ReadWindowsMode(sub));
            r.Shell.Start();
            await r.Until(() => r.Shared() != null, "the seed of look.json");
            r.Shell.FollowWindows(sub);
            Assert.Equal("light", r.Shell.Now);
            await Task.Delay(100);
            var dark = r.NextTheme(p => p.Theme == "dark");
            var sw = Stopwatch.StartNew();
            using (var key = Registry.CurrentUser.OpenSubKey(sub, true)!) key.SetValue(ThemeCore.AppsUseLightTheme, 0, RegistryValueKind.DWord);
            var p = await Within(dark, 1000, "the theme event after Windows went dark");
            Assert.True(sw.ElapsedMilliseconds < 1000, "followed after " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal("system", p.View.Mode);
            Assert.Equal("dark", p.View.Windows);
            Assert.Contains("theme: Windows apps are dark now, so Mantle is dark", r.Log.All);
            await Task.Delay(200);
            Assert.Equal("system", ThemeOf(r.Shared()));   // Windows moving is not a pick: nothing new is shared
        } finally {
            Registry.CurrentUser.DeleteSubKeyTree(sub, false);
        }
    }
}
