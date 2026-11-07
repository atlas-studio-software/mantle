using System.IO;
using System.Linq;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests;

public class ReaderTests
{
    // ------------------------------------------------------------------ theme.json
    [Theory]
    [InlineData("{\"mode\":\"dark\"}", ThemeMode.Dark)]
    [InlineData("{\"mode\":\"light\"}", ThemeMode.Light)]
    [InlineData("{\"mode\":\"system\"}", ThemeMode.System)]
    [InlineData("{\"mode\":\"dark\",\"fromANewerMantle\":{\"x\":[1,2]}}", ThemeMode.Dark)]
    public void Theme_reads_the_mode_and_ignores_unknown_keys(string json, ThemeMode want)
    {
        var r = Readers.ParseTheme(json);
        Assert.Equal(ReadState.Ok, r.State);
        Assert.Equal(want, r.Value.Mode);
        Assert.Null(r.Reason);
    }

    [Theory]
    [InlineData("{\"mode\":\"purple\"}", "\"purple\"")]
    [InlineData("{\"mode\":3}", "a number")]
    [InlineData("{}", "no \"mode\"")]
    [InlineData("[\"dark\"]", "not a JSON object")]
    [InlineData("{\"mode\":\"dark\"", "not valid JSON")]
    [InlineData("", "not valid JSON")]
    [InlineData("{\"mode\":\"dark\",}", "not valid JSON")]          // Node's JSON.parse refuses a trailing comma
    [InlineData("{/*c*/\"mode\":\"dark\"}", "not valid JSON")]       // and comments
    public void A_bad_theme_falls_back_to_match_windows_with_a_reason(string json, string because)
    {
        var r = Readers.ParseTheme(json);
        Assert.Equal(ReadState.Corrupt, r.State);
        Assert.Equal(ThemeMode.System, r.Value.Mode);
        Assert.Contains(because, r.Reason);
        Assert.Contains("using the defaults", r.Describe());
    }

    // ------------------------------------------------------------------ look-sync.json
    [Fact]
    public void Local_look_defaults_are_look_syncs_seed()
    {
        var r = Readers.ParseLocalLook("{}");
        Assert.Equal(ReadState.Ok, r.State);
        Assert.True(r.Value.LookSync);
        Assert.Null(r.Value.AccentHue);
        Assert.Equal(GlassSettings.Seed, r.Value.Glass);
        Assert.Equal(5, r.Value.Glass.Dark.BlurPx);
        Assert.Equal(12, r.Value.Glass.Light.BlurPx);
    }

    [Fact]
    public void Local_look_uses_each_good_field_and_skips_each_bad_one()
    {
        var r = Readers.ParseLocalLook("{\"lookSync\":\"yes\",\"accent\":{\"hue\":400},\"glass\":{\"dark\":{\"blurPx\":8,\"dim\":0.5,\"panel\":0},\"light\":{\"blurPx\":99,\"dim\":0.5,\"panel\":0},\"render\":\"picture\"}}");
        Assert.Equal(ReadState.Ok, r.State);
        Assert.True(r.Value.LookSync);                 // default kept
        Assert.Null(r.Value.AccentHue);                // 400 is out of 0..360
        Assert.Equal(GlassSettings.Seed, r.Value.Glass);   // light blur 99 > 64: the whole glass is refused (checkValue)
        Assert.Equal(3, r.Skipped.Count);
        Assert.Contains(r.Skipped, s => s.Contains("glass.light.blurPx"));
    }

    [Fact]
    public void A_hue_of_200_point_0_is_a_whole_number_as_in_javascript()
    {
        Assert.Equal(200, Readers.ParseLocalLook("{\"accent\":{\"hue\":200.0}}").Value.AccentHue);
        Assert.Null(Readers.ParseLocalLook("{\"accent\":{\"hue\":200.5}}").Value.AccentHue);
    }

    [Fact]
    public void Corrupt_local_look_is_defaults_with_a_reason()
    {
        var r = Readers.ParseLocalLook("{lookSync:false}");
        Assert.Equal(ReadState.Corrupt, r.State);
        Assert.Equal(LocalLook.Default, r.Value);
        Assert.NotNull(r.Reason);
    }

    // ------------------------------------------------------------------ look.json
    [Fact]
    public void Shared_look_reads_each_field_with_its_rev()
    {
        string text = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "profile", "look-sync", "look.json"));
        var r = Readers.ParseSharedLook(text);
        Assert.Equal(ReadState.Ok, r.State);
        Assert.Empty(r.Skipped);
        Assert.Equal(7, r.Value.Rev);
        Assert.Equal("helm", r.Value.WriterApp);
        Assert.Equal(ThemeMode.Light, r.Value.Theme);
        Assert.True(r.Value.HasAccent);
        Assert.Null(r.Value.AccentHue);
        Assert.Equal(20, r.Value.Glass!.Light.BlurPx);
        Assert.NotNull(r.Value.Desktop);
        Assert.Equal("preset-1", r.Value.Desktop!.Id);
    }

    [Theory]
    [InlineData("{\"rev\":1}", ReadState.Corrupt, "schema is missing")]
    [InlineData("{\"schema\":1}", ReadState.Corrupt, "rev is missing")]
    [InlineData("{\"schema\":2,\"rev\":1}", ReadState.Newer, "newer version")]
    [InlineData("7", ReadState.Corrupt, "not an object")]
    [InlineData("{\"schema\":", ReadState.Corrupt, "not valid JSON")]
    public void Shared_look_refusals_match_look_sync(string json, ReadState state, string because)
    {
        var r = Readers.ParseSharedLook(json);
        Assert.Equal(state, r.State);
        Assert.Contains(because, r.Reason);
        Assert.Null(r.Value.Theme);
        Assert.Null(r.Value.Glass);
    }

    [Fact]
    public void A_bad_shared_field_is_skipped_and_the_rest_used()
    {
        var r = Readers.ParseSharedLook("{\"schema\":1,\"rev\":3,\"look\":{\"theme\":{\"v\":\"neon\",\"rev\":1},\"accent\":{\"v\":{\"hue\":12},\"rev\":-1},\"glass\":\"x\"},\"later\":true}");
        Assert.Equal(ReadState.Ok, r.State);
        Assert.Null(r.Value.Theme);
        Assert.False(r.Value.HasAccent);
        Assert.Null(r.Value.Glass);
        Assert.Equal(3, r.Skipped.Count);
    }

    // ------------------------------------------------------------------ features.json
    [Fact]
    public void Feature_switches_keep_booleans_only()
    {
        var r = Readers.ParseFeatures("{\"topbar\":true,\"snap\":false,\"odd\":\"yes\"}");
        Assert.Equal(ReadState.Ok, r.State);
        Assert.True(r.Value.Get("topbar"));
        Assert.False(r.Value.Get("snap"));
        Assert.Null(r.Value.Get("odd"));
        Assert.Null(r.Value.Get("never-set"));
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void Features_that_are_not_an_object_say_so_in_electrons_words()
    {
        var r = Readers.ParseFeatures("[true]");
        Assert.Equal(ReadState.Corrupt, r.State);
        Assert.Contains("does not hold switches", r.Reason);
    }

    // ------------------------------------------------------------------ features\jumplist.json
    [Fact]
    public void Pins_overlay_reads_add_hide_and_order()
    {
        string text = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "profile", "features", "jumplist.json"));
        var r = Readers.ParsePins(text);
        Assert.Equal(ReadState.Ok, r.State);
        Assert.Equal(2, r.Value.Add.Count);
        Assert.Single(r.Value.Hide);
        Assert.Equal("Example Notes", r.Value.Add[0].Name);
        Assert.Equal("Example.Notes_abc123!App", r.Value.Add[0].Aumid);
        Assert.Null(r.Value.Add[0].Exe);
        Assert.Equal(new[] { @"exe:c:\apps\example\editor.exe", "aumid:Example.Notes_abc123!App" }, r.Value.Order.ToArray());
    }

    [Fact]
    public void Pins_without_an_id_are_skipped_and_order_is_deduplicated()
    {
        var r = Readers.ParsePins("{\"pins\":{\"add\":[{\"name\":\"no id\"},{\"id\":\"a\"}],\"hide\":\"x\",\"order\":[\"a\",\"a\",3,\"\",\"b\"]}}");
        Assert.Single(r.Value.Add);
        Assert.Empty(r.Value.Hide);
        Assert.Equal(new[] { "a", "b" }, r.Value.Order.ToArray());
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void A_settings_file_without_pins_is_an_empty_overlay()
    {
        var r = Readers.ParsePins("{\"somethingElse\":1}");
        Assert.Equal(ReadState.Ok, r.State);
        Assert.Equal(0, r.Value.Add.Count + r.Value.Hide.Count + r.Value.Order.Count);
    }
}

public class MantleDataTests
{
    static DataPaths PathsFor(TestDir d) => DataPaths.Resolve(LaunchArgs.Parse(new[] { "--user-data=" + d.Path }), _ => null, Path.GetTempPath());

    [Fact]
    public void The_fixture_profile_reads_cleanly()
    {
        using var d = new TestDir("data");
        d.CopyFixtureProfile();
        var log = new ListLog();
        using var data = new MantleData(PathsFor(d), log);
        data.Load();
        Assert.Equal(ThemeMode.Dark, data.Theme.Value.Mode);
        Assert.False(data.LocalLook.Value.LookSync);
        Assert.Equal(210, data.LocalLook.Value.AccentHue);
        Assert.Equal(ReadState.Ok, data.SharedLook.State);
        Assert.True(data.Features.Value.Get("topbar"));
        Assert.Equal(2, data.Pins.Value.Add.Count);
        // sync is off in the fixture, so the bar shows Mantle's own glass, not the shared one
        Assert.Equal(16, data.EffectiveGlass.Light.BlurPx);
        Assert.Equal(5, log.Lines.Count(l => l.StartsWith("data: ", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void With_sync_on_the_shared_glass_wins()
    {
        using var d = new TestDir("data");
        d.CopyFixtureProfile();
        d.Write("look-sync.json", "{\"lookSync\":true}");
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        Assert.Equal(20, data.EffectiveGlass.Light.BlurPx);
    }

    [Fact]
    public void A_fresh_profile_is_all_defaults_and_every_file_is_said_missing()
    {
        using var d = new TestDir("data");
        var log = new ListLog();
        using var data = new MantleData(PathsFor(d), log);
        data.Load();
        Assert.Equal(ReadState.Missing, data.Theme.State);
        Assert.Equal(ThemeMode.System, data.Theme.Value.Mode);
        Assert.Equal(GlassSettings.Seed, data.EffectiveGlass);
        Assert.Equal(5, log.Lines.Count(l => l.Contains(": missing", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void Corrupt_files_are_logged_with_their_reason_never_silently()
    {
        using var d = new TestDir("data");
        d.Write("theme.json", "{\"mode\":");
        d.Write("features.json", "\"on\"");
        var log = new ListLog();
        using var data = new MantleData(PathsFor(d), log);
        data.Load();
        Assert.Equal(ReadState.Corrupt, data.Theme.State);
        Assert.Contains(log.Lines, l => l.StartsWith("data: theme.json: corrupt (it is not valid JSON", System.StringComparison.Ordinal) && l.Contains("using the defaults"));
        Assert.Contains(log.Lines, l => l.StartsWith("data: features.json: corrupt (it does not hold switches", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_theme_file_over_64_KB_is_too_large()
    {
        using var d = new TestDir("data");
        d.Write("theme.json", "{\"mode\":\"dark\",\"pad\":\"" + new string('x', 70 * 1024) + "\"}");
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        Assert.Equal(ReadState.TooLarge, data.Theme.State);
        Assert.Equal(ThemeMode.System, data.Theme.Value.Mode);
    }

    [Fact]
    public void A_file_locked_by_a_writer_reads_and_one_locked_exclusively_is_unreadable()
    {
        using var d = new TestDir("data");
        string p = d.Write("theme.json", "{\"mode\":\"light\"}");
        using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) {
            using var data = new MantleData(PathsFor(d), new ListLog());
            data.Load();
            Assert.Equal(ThemeMode.Light, data.Theme.Value.Mode);
        }
        using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            using var data = new MantleData(PathsFor(d), new ListLog());
            data.Load();
            Assert.Equal(ReadState.Unreadable, data.Theme.State);
            Assert.NotNull(data.Theme.Reason);
        }
    }
}
