using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Microsoft.Win32;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>lib/look-host.test.js's checks that are not in the golden table (drift guard, listeners, the grace,
/// the start line), the Windows mode reader, and the Match Windows follower's speed.</summary>
public class LookHostTests
{
    sealed class Rig : IDisposable
    {
        public readonly TestDir Dir = new("host");
        public readonly ListLog Log = new();
        public readonly Stores Stores;
        public string Mode = "dark";
        public readonly LookHost Host;
        public Rig(int grace = 0)
        {
            Stores = new Stores(Dir.Path, new SafeFile(WriteGate.Scratch(Dir.Path)), Log);
            var opts = new LookSyncOptions { App = "mantle", Dir = Dir.File("look-sync"), Files = new SafeFile(WriteGate.Scratch(Dir.Path)) };
            Host = new LookHost(Stores.Open("look-sync.json"), opts, new LookTheme(() => Mode, () => Mode == "dark" ? "dark" : "light", m => { Mode = m; return null; }), Log, grace);
        }
        public void Dispose() { Host.Dispose(); Stores.Dispose(); Dir.Dispose(); }
    }

    static string RepoFile(string rel, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "..", rel));

    [Fact]
    public void The_dark_and_light_constants_are_the_stylesheets_own_values()
    {
        string css = File.ReadAllText(RepoFile(Path.Combine("ui", "shared", "glass-light.css")));
        string light = css[css.IndexOf(":root {", StringComparison.Ordinal)..css.IndexOf(":root[data-theme=\"dark\"]", StringComparison.Ordinal)];
        int di = css.IndexOf(":root[data-theme=\"dark\"]", StringComparison.Ordinal);
        string dark = css[di..css.IndexOf("\n}", di, StringComparison.Ordinal)];
        string? Tok(string block, string n) => Regex.Match(block, @"\s" + Regex.Escape(n) + @":\s*([^;]+);") is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        Assert.Equal($"blur({LookHost.Light.BlurPx}px) saturate({LookHost.Light.Saturate}%)", Tok(light, "--lg-blur"));
        Assert.Equal("rgba(255,255,255,.45)", Tok(light, "--lg-fill"));
        Assert.Equal("rgba(255,255,255,.72)", Tok(light, "--lg-fill-strong"));
        Assert.Equal("rgba(255,255,255,.88)", Tok(light, "--lg-fill-pop"));
        Assert.Equal(new[] { 0.72, 0.88 }, LookHost.LightPanels.Select(p => p.A));
        // dark: each fill the panel shifts, compared as numbers (the sheet writes .83 or 0.83)
        foreach (var (name, r, g, b, a) in LookHost.DarkFills) {
            string? v = Tok(dark, name);
            if (v != null && v.StartsWith("var(", StringComparison.Ordinal)) v = Tok(dark, v[4..^1].Trim());   // --lg-fill-pop: var(--lg-fill)
            Assert.True(v != null, name + " is missing from the dark block");
            var c = GlassAdapt.ParseColor(v);
            Assert.True(c == new Rgba(r, g, b, a), name + ": the sheet says " + v);
        }
        Assert.Equal($"blur({LookHost.Dark.BlurPx}px)", Tok(dark, "--lg-blur"));
        var bar = GlassAdapt.ParseColor(Tok(dark, "--lg-fill-bar"));
        Assert.True(bar == new Rgba(LookHost.DarkBar[0], LookHost.DarkBar[1], LookHost.DarkBar[2], LookHost.Dark.Dim), "--lg-fill-bar: " + Tok(dark, "--lg-fill-bar"));
        var seed = LookSyncCore.Seed();
        Assert.Equal("{\"blurPx\":12,\"dim\":0.45,\"panel\":0}", JsJson.Stringify(seed["glass"]!["light"], 0));
    }

    [Fact]
    public void At_the_seed_values_there_are_no_overrides_and_a_listener_that_throws_is_logged()
    {
        using var rig = new Rig();
        Assert.Empty(rig.Host.Vars("dark"));
        Assert.Empty(rig.Host.Vars("light"));
        int second = 0;
        using var a = rig.Host.OnChange(_ => throw new InvalidOperationException("boom"));
        using var b = rig.Host.OnChange(_ => second++);
        rig.Host.SetDesktop((JsonObject)JsJson.Parse("{\"desktop\":{\"ok\":true},\"windows\":{\"ok\":true}}")!);
        Assert.Equal(1, second);
        Assert.Contains("look: a listener threw: boom", rig.Log.Lines);
        b.Dispose();
        rig.Host.SetDesktop((JsonObject)JsJson.Parse("{\"desktop\":{\"ok\":true},\"windows\":{\"ok\":true}}")!);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task Start_logs_the_folder_name_only_and_a_soft_guard_failure_waits_out_the_grace()
    {
        using var rig = new Rig(grace: 300);
        rig.Host.Start();
        await rig.Host.Sync.WhenIdle();
        Assert.Contains("look: sync with Helm and Quartz is on; shared folder look-sync", rig.Log.Lines);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains(rig.Dir.Path, StringComparison.OrdinalIgnoreCase));
        rig.Mode = "dark";
        var first = rig.Host.TopGuard("dark");
        Assert.False(first.Failure);
        Assert.Equal("the shared darkening is being worked out", first.Why);
        var said = new TaskCompletionSource<bool>();
        using var _ = rig.Host.OnChange(c => { if (c.Why == "guard") said.TrySetResult(true); });
        Assert.True(await Task.WhenAny(said.Task, Task.Delay(3000)) == said.Task, "the grace timer re-asks");
        var later = rig.Host.TopGuard("dark");
        Assert.True(later.Failure && later.Soft);
        Assert.Matches("has not shared the desktop wallpaper yet", later.Why);
        Assert.Equal("light: the top bar keeps its light frost", rig.Host.TopGuard("light").Why);
    }

    [Fact]
    public void ReadWindowsMode_reads_AppsUseLightTheme_and_a_missing_value_is_light()
    {
        Assert.Equal(new WindowsMode("dark", null), ThemeCore.ReadWindowsMode(() => 0));
        Assert.Equal(new WindowsMode("light", null), ThemeCore.ReadWindowsMode(() => 1));
        Assert.Equal(new WindowsMode("light", null), ThemeCore.ReadWindowsMode(() => null));
        Assert.Equal(new WindowsMode("light", null), ThemeCore.ReadWindowsMode(() => "0"));   // not a DWORD: Windows' default
        Assert.Throws<UnauthorizedAccessException>(() => ThemeCore.ReadWindowsMode(() => throw new UnauthorizedAccessException("denied")));
        // a read that throws is reported by the theme core, never thrown
        var log = new ListLog();
        var core = new ThemeCore(null, () => ThemeCore.ReadWindowsMode(() => throw new UnauthorizedAccessException("denied")), null, log);
        Assert.Null(core.Get().Windows);
        Assert.Contains("could not be read (denied)", core.Get().WindowsWhy);
        Assert.Equal("light", core.Theme);
    }

    [Fact]
    public void Match_Windows_follows_a_change_of_the_watched_key_within_1_s()
    {
        if (!OperatingSystem.IsWindows()) return;
        // a scratch key of the test's own under HKCU: the user's real theme key is only ever read
        string sub = @"Software\Mantle-native-tests\theme-" + Guid.NewGuid().ToString("N")[..8];
        using var key = Registry.CurrentUser.CreateSubKey(sub);
        key.SetValue(ThemeCore.AppsUseLightTheme, 1, RegistryValueKind.DWord);
        try {
            var log = new ListLog();
            var core = new ThemeCore(null, () => ThemeCore.ReadWindowsMode(() => key.GetValue(ThemeCore.AppsUseLightTheme)), null, log);
            Assert.Equal("light", core.Theme);
            var told = new ManualResetEventSlim();
            string? seen = null;
            using var off = core.On(v => { seen = v.Theme; told.Set(); });
            var host = new object();
            using var follow = ThemeFollow.Start(core, a => { lock (host) a(); }, log, sub);
            Thread.Sleep(100);   // the watch is armed on its own thread
            var sw = Stopwatch.StartNew();
            key.SetValue(ThemeCore.AppsUseLightTheme, 0, RegistryValueKind.DWord);
            Assert.True(told.Wait(1000), "Mantle did not follow Windows within 1 s");
            Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal("dark", seen);
            Assert.Contains("theme: Windows apps are dark now, so Mantle is dark", log.Lines);
        } finally {
            Registry.CurrentUser.DeleteSubKeyTree(sub, false);
        }
    }
}
