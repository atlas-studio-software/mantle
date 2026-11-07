using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>lib/theme.test.js ported (the spec): modes, resolve, persistence, the unreadable rule, notes, refusals, logs, follow Windows.</summary>
public class ThemeCoreTests
{
    sealed class Rig : IDisposable
    {
        public readonly TestDir Dir = new("theme");
        public readonly ListLog Log = new();
        public WindowsMode Win = new("light", null);
        public Stores Stores;
        public Rig(WriteGate? gate = null) => Stores = new Stores(Dir.Path, new SafeFile(gate ?? WriteGate.Scratch(Dir.Path)), Log);
        public ThemeCore Make(string? pin = null) => new((JsonStore)Stores.Open("theme.json"), () => Win, pin, Log);
        public string File => Dir.File("theme.json");
        public void Dispose() { Stores.Dispose(); Dir.Dispose(); }
    }

    [Fact]
    public void The_modes_are_system_light_dark_and_a_fresh_profile_is_Match_Windows()
    {
        Assert.Equal(new[] { "system", "light", "dark" }, ThemeCore.Modes);
        using var r = new Rig();
        var t = r.Make();
        Assert.Equal("system", t.Mode);
        Assert.Null(t.Get().Note);
    }

    [Theory]
    [InlineData("light", "dark", "light")] [InlineData("dark", "light", "dark")] [InlineData("system", "dark", "dark")]
    [InlineData("system", "light", "light")] [InlineData("system", null, "light")]
    public void Resolve_fixed_modes_win_and_Match_Windows_follows_the_app_mode(string mode, string? windows, string theme)
        => Assert.Equal(theme, ThemeCore.Resolve(mode, windows));

    [Fact]
    public void An_unknown_or_throwing_Windows_mode_shows_light_and_says_why()
    {
        using var r = new Rig();
        r.Win = new WindowsMode(null, "no idea");
        var v = r.Make().Get();
        Assert.Equal("light", v.Theme);
        Assert.Equal("no idea, so Match Windows shows light", v.WindowsWhy);
        var thrower = new ThemeCore(null, () => throw new InvalidOperationException("boom"), null, r.Log);
        Assert.Equal("light", thrower.Theme);
        Assert.Equal("Windows' mode could not be read (boom), so Match Windows shows light", thrower.Get().WindowsWhy);
    }

    [Fact]
    public void A_pick_is_saved_as_mode_and_read_back_with_no_tmp_left()
    {
        using var r = new Rig();
        var res = r.Make().Set(JsonValue.Create("dark"));
        Assert.True(res.Ok);
        Assert.Equal("dark", res.Value);
        Assert.Equal("{\n  \"mode\": \"dark\"\n}", File.ReadAllText(r.File));
        Assert.False(File.Exists(r.File + ".tmp"));
        using var r2 = new Stores(r.Dir.Path, new SafeFile(WriteGate.Scratch(r.Dir.Path)), r.Log);
        Assert.Equal("dark", new ThemeCore((JsonStore)r2.Open("theme.json"), () => r.Win, null, r.Log).Mode);
        Assert.True(r.Make().Set(new JsonObject { ["mode"] = "system" }).Ok);   // {mode} form, Match Windows saved too
        Assert.Contains("\"system\"", File.ReadAllText(r.File));
    }

    [Fact]
    public void No_mode_key_means_the_default_with_no_note()
    {
        using var r = new Rig();
        r.Dir.Write("theme.json", "{\"other\":1}");
        var t = r.Make();
        Assert.Equal("system", t.Mode);
        Assert.Null(t.Get().Note);
    }

    [Theory]
    [InlineData("{\"mode\":", "is not valid JSON")]
    [InlineData("[1]", "does not hold a setting (not a JSON object)")]
    [InlineData("{\"mode\":\"purple\"}", "says \"purple\", which is not a mode")]
    [InlineData("{\"mode\":7}", "says a number, which is not a mode")]
    public void An_unusable_theme_json_is_kept_aside_with_a_note_and_Mantle_starts_on_Match_Windows(string text, string why)
    {
        using var r = new Rig();
        r.Dir.Write("theme.json", text);
        var t = r.Make();
        Assert.Equal("system", t.Mode);
        Assert.Equal($"The saved appearance was not used: theme.json {why}; kept as theme.json.unreadable, so Mantle is back to Match Windows", t.Get().Note);
        Assert.Equal(text, File.ReadAllText(r.File + ".unreadable"));
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains(r.Dir.Path, StringComparison.OrdinalIgnoreCase));
        Assert.True(t.Set(JsonValue.Create("light")).Ok);
        Assert.Null(t.Get().Note);   // a good save clears it
    }

    [Fact]
    public void A_save_that_fails_still_applies_the_pick_and_says_so()
    {
        using var r = new Rig(WriteGate.Never("EPERM"));
        var t = r.Make();
        var res = t.Set(JsonValue.Create("dark"));
        Assert.True(res.Ok);
        Assert.Equal("dark", t.Theme);
        Assert.Equal("Dark is on now, but it could not be saved (EPERM), so Mantle goes back to the last saved appearance at its next start", res.View!.Note);
        Assert.Contains(r.Log.Lines, l => l.StartsWith("theme: Dark is on now", StringComparison.Ordinal));
        Assert.False(File.Exists(r.File));
    }

    [Fact]
    public void Anything_that_is_not_a_mode_is_refused_in_words_and_nothing_changes()
    {
        using var r = new Rig();
        var t = r.Make();
        int told = 0;
        using var off = t.On(_ => told++);
        Assert.Equal("Mantle's appearance can be \"light\", \"dark\" or \"system\" (Match Windows); it was sent \"" + new string('x', 40) + "\"", t.Set(JsonValue.Create(new string('x', 99))).Reason);
        Assert.EndsWith("it was sent a object", t.Set(new JsonObject { ["mode"] = new JsonObject() }).Reason);
        Assert.EndsWith("it was sent a list", t.Set(new JsonArray()).Reason);
        Assert.EndsWith("it was sent nothing", t.Set(null).Reason);
        Assert.Equal(0, told);
        Assert.False(File.Exists(r.File));
    }

    [Fact]
    public void The_same_mode_again_still_tells_listeners_but_logs_nothing_and_a_move_logs_once()
    {
        using var r = new Rig();
        var t = r.Make();
        int told = 0;
        using var off = t.On(_ => told++);
        t.Set(JsonValue.Create("dark"));
        Assert.Contains("theme: Dark -> Mantle is dark", r.Log.Lines);
        int lines = r.Log.Lines.Length;
        t.Set(JsonValue.Create("dark"));
        Assert.Equal(2, told);
        Assert.Equal(lines, r.Log.Lines.Length);
    }

    [Fact]
    public void Match_Windows_follows_Windows_live_and_a_fixed_mode_stays()
    {
        using var r = new Rig();
        var t = r.Make();
        var seen = new List<string>();
        using var off = t.On(v => seen.Add(v.Theme));
        Assert.False(t.WindowsChanged());
        r.Win = new WindowsMode("dark", null);
        Assert.True(t.WindowsChanged());
        Assert.Equal("dark", t.Theme);
        Assert.Contains("theme: Windows apps are dark now, so Mantle is dark", r.Log.Lines);
        t.Set(JsonValue.Create("light"));
        r.Win = new WindowsMode("light", null);
        Assert.True(t.WindowsChanged());
        r.Win = new WindowsMode("dark", null);
        Assert.True(t.WindowsChanged());
        Assert.Equal("light", t.Theme);
        Assert.Equal("dark", t.Get().Windows);
        Assert.Contains("theme: Windows apps are dark now (Mantle stays light: it is set to Light)", r.Log.Lines);
    }

    [Fact]
    public void A_throwing_listener_is_logged_and_the_next_still_runs_and_off_unsubscribes()
    {
        using var r = new Rig();
        var t = r.Make();
        int second = 0;
        using var a = t.On(_ => throw new InvalidOperationException("bad"));
        var b = t.On(_ => second++);
        t.Set(JsonValue.Create("dark"));
        Assert.Equal(1, second);
        Assert.Contains("theme: a listener threw: bad", r.Log.Lines);
        b.Dispose();
        t.Set(JsonValue.Create("light"));
        Assert.Equal(1, second);
    }

    [Fact]
    public void A_pinned_test_run_never_reads_or_writes_theme_json()
    {
        using var r = new Rig();
        r.Dir.Write("theme.json", "{\"mode\":\"light\"}");
        var t = new ThemeCore(null, () => r.Win, "dark", r.Log);
        Assert.Equal("dark", t.Mode);
        Assert.Equal("This test run was started with --theme=dark, so a change here lasts until it quits and is never saved", t.Get().Pinned);
        Assert.True(t.Set(JsonValue.Create("system")).Ok);
        Assert.Equal("system", t.Mode);
        Assert.Null(t.Get().Note);
        Assert.Equal("{\"mode\":\"light\"}", File.ReadAllText(r.File));
    }
}
