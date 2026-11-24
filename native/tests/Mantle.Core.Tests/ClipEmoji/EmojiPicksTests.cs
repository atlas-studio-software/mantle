using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Data;
using Mantle.Core.Features.Emoji;
using Xunit;

namespace Mantle.Core.Tests.ClipEmoji;

/// <summary>
/// The picker's saved picks (EMO-036..039): where they live, their format, the write gate and the .unreadable rule.
/// Every test runs in its own TestDir under %TEMP%; nothing touches the real profile.
/// </summary>
public sealed class EmojiPicksTests
{
    const string File_ = @"features\emoji.json";

    static Stores Scratch(TestDir d, ListLog log) => new(d.Path, new SafeFile(WriteGate.Scratch(d.Path)), log);

    static JsonElement Read(TestDir d) => JsonDocument.Parse(File.ReadAllText(d.File(File_))).RootElement;

    // SAFETY (EMO-039 storage): the picks live in features\emoji.json as plain JSON, written tmp + rename, other keys kept
    [Fact]
    public void Picks_are_saved_in_features_emoji_json_as_plain_json_and_no_tmp_is_left()
    {
        using var d = new TestDir("emoji-store");
        d.WriteLikeElectron(File_, "{\n  \"closeAfterInsert\": true,\n  \"later\": 1\n}");
        var log = new ListLog();
        using var stores = Scratch(d, log);
        var picks = new EmojiPicks(stores.Open(File_));
        Assert.True(picks.CloseAfterInsert);

        Assert.True(picks.RememberAsync("\U0001F602", "discord.exe").Result.Ok);
        Assert.True(picks.SetToneAsync(3).Result.Ok);

        var doc = Read(d);
        Assert.Equal(new[] { "\U0001F602" }, doc.GetProperty("recents").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(3, doc.GetProperty("tone").GetInt32());
        Assert.Equal(new[] { "\U0001F602" }, doc.GetProperty("apps").GetProperty("discord.exe").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True(doc.GetProperty("closeAfterInsert").GetBoolean());       // the option and unknown keys survive
        Assert.Equal(1, doc.GetProperty("later").GetInt32());
        Assert.Equal(new[] { "emoji.json" }, Directory.GetFiles(d.File("features")).Select(f => Path.GetFileName(f)).ToArray());   // no tmp behind
        Assert.DoesNotContain(log.Lines, l => l.Contains("\U0001F602", StringComparison.Ordinal));                       // never an emoji in the log

        using var again = Scratch(d, new ListLog());                                                                     // kept across restarts
        var back = new EmojiPicks(again.Open(File_));
        Assert.Equal(new[] { "\U0001F602" }, back.Recents.ToArray());
        Assert.Equal(3, back.Tone);
        Assert.Equal(new[] { "\U0001F602" }, back.AppPicks("discord.exe").ToArray());
    }

    // SAFETY (never write the Electron app's files): with the gate closed Electron's emoji.json stays byte for byte
    [Fact]
    public void A_closed_write_gate_leaves_the_electron_file_byte_for_byte_and_keeps_the_pick_in_memory()
    {
        using var d = new TestDir("emoji-gate");
        d.WriteLikeElectron(File_, "{\n  \"recents\": [\n    \"\U0001F44D\"\n  ],\n  \"tone\": 2\n}");
        byte[] before = File.ReadAllBytes(d.File(File_));
        using var stores = new Stores(d.Path, new SafeFile(WriteGate.Never("a test says no")), new ListLog());
        var picks = new EmojiPicks(stores.Open(File_));
        Assert.Equal(2, picks.Tone);

        var w = picks.RememberAsync("\U0001F602", "notepad.exe").Result;
        Assert.False(w.Ok);
        Assert.Contains("a test says no", w.Reason);
        Assert.False(picks.SetToneAsync(5).Result.Ok);

        Assert.Equal(new[] { "\U0001F602", "\U0001F44D" }, picks.Recents.ToArray());   // this run still has it
        Assert.Equal(5, picks.Tone);
        Assert.True(before.SequenceEqual(File.ReadAllBytes(d.File(File_))));
        Assert.Equal(new[] { "emoji.json" }, Directory.GetFiles(d.File("features")).Select(f => Path.GetFileName(f)).ToArray());
    }

    // SAFETY (.unreadable): a damaged emoji.json is set aside, never overwritten, and the picker starts clean
    [Fact]
    public void A_damaged_emoji_json_is_set_aside_as_unreadable_and_never_overwritten()
    {
        using var d = new TestDir("emoji-aside");
        d.Write(File_, "{\"recents\":");
        using var stores = Scratch(d, new ListLog());
        var store = stores.Open(File_);
        Assert.Equal(ReadState.Corrupt, store.State);
        var picks = new EmojiPicks(store);
        Assert.Empty(picks.Recents);
        Assert.Equal(0, picks.Tone);
        Assert.Equal("{\"recents\":", File.ReadAllText(d.File(File_ + ".unreadable")));

        Assert.True(picks.RememberAsync("❤️", null).Result.Ok);
        Assert.Equal("{\"recents\":", File.ReadAllText(d.File(File_ + ".unreadable")));
        Assert.Equal("❤️", Read(d).GetProperty("recents")[0].GetString());
        Assert.False(Read(d).TryGetProperty("apps", out _));   // no app known: the per-app map is not written
    }

    [Fact]
    public void Recents_hold_32_newest_first_without_repeats_and_apps_hold_12_picks_for_30_apps()
    {
        using var d = new TestDir("emoji-limits");
        using var stores = Scratch(d, new ListLog());
        var picks = new EmojiPicks(stores.Open(File_));
        for (int i = 0; i < 40; i++) Assert.True(picks.RememberAsync(char.ConvertFromUtf32(0x1F600 + i), "app.exe").Result.Ok);
        Assert.Equal(EmojiModel.RecentMax, picks.Recents.Count);
        Assert.Equal(char.ConvertFromUtf32(0x1F600 + 39), picks.Recents[0]);
        Assert.True(picks.RememberAsync(char.ConvertFromUtf32(0x1F600 + 20), "app.exe").Result.Ok);   // a repeat moves to the front
        Assert.Equal(char.ConvertFromUtf32(0x1F600 + 20), picks.Recents[0]);
        Assert.Equal(EmojiModel.RecentMax, picks.Recents.Count);
        Assert.Equal(1, picks.Recents.Count(r => r == char.ConvertFromUtf32(0x1F600 + 20)));
        Assert.Equal(EmojiPicks.PerApp, picks.AppPicks("app.exe").Count);

        for (int a = 0; a < 31; a++) Assert.True(picks.RememberAsync("\U0001F44D", $"a{a}.exe").Result.Ok);
        Assert.Equal(EmojiPicks.AppsMax, picks.AppKeys.Count);
        Assert.DoesNotContain("app.exe", picks.AppKeys);          // the oldest app went first
        Assert.DoesNotContain("a0.exe", picks.AppKeys);
        Assert.Equal("a30.exe", picks.AppKeys[picks.AppKeys.Count - 1]);
        Assert.Equal(EmojiPicks.AppsMax, Read(d).GetProperty("apps").EnumerateObject().Count());
        Assert.Equal("Discord", EmojiPicks.AppName("discord.exe"));
        Assert.Equal("Code", EmojiPicks.AppName("Code.EXE"));
        Assert.Null(EmojiPicks.AppName(null));
    }

    [Fact]
    public void Junk_in_the_stored_file_is_dropped_and_the_tone_is_clamped()
    {
        using var d = new TestDir("emoji-junk");
        d.WriteLikeElectron(File_, "{\"recents\":[\"\U0001F602\",7,null,\"" + new string('x', 70) + "\",\"\"],\"tone\":9,\"apps\":[1]}");
        using var stores = Scratch(d, new ListLog());
        var picks = new EmojiPicks(stores.Open(File_));
        Assert.Equal(new[] { "\U0001F602", "" }, picks.Recents.ToArray());   // lib's arr(): strings of <= 64 units (the empty one is dropped by remember)
        Assert.Equal(0, picks.Tone);
        Assert.Empty(picks.AppKeys);
        Assert.True(picks.RememberAsync("\U0001F44D", null).Result.Ok);
        Assert.Equal(new[] { "\U0001F44D", "\U0001F602" }, picks.Recents.ToArray());

        using var d2 = new TestDir("emoji-tone");
        d2.WriteLikeElectron(File_, "{\"tone\":\"3\"}");
        using var s2 = Scratch(d2, new ListLog());
        Assert.Equal(3, new EmojiPicks(s2.Open(File_)).Tone);   // Number("3") is 3, as in model.js clampTone
    }
}
