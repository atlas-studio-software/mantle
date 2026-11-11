using System;
using System.IO;
using System.Text.Json.Nodes;
using Mantle.Core.Features.Media;
using Xunit;

namespace Mantle.Core.Tests.Media;

/// <summary>lib/media-pick.test.js and lib/media-guard.test.js, the same cases (BAR-954..962, BAR-1022, BAR-1023).</summary>
public class MediaPickGuardTests
{
    static MediaPickSession S(string aumid, string name, bool playing = false, bool prot = false) => new(aumid, name, playing, prot);
    static readonly MediaPickSession Spotify = S("Spotify.exe", "Spotify", true), Chrome = S("Chrome", "Google Chrome");

    [Fact]
    public void A_pick_is_shown_while_its_session_exists_and_AUMIDs_match_in_any_case()
    {
        var pick = MediaPick.Make("spotify.EXE", Spotify, 0);
        var c = MediaPick.Choose(pick, new[] { Spotify, Chrome }, Chrome, false, 1000);
        Assert.Equal("you", c.By);
        Assert.Equal("spotify.EXE", c.Want);
        Assert.Equal("Spotify", c.Pick!.Name);
        Assert.True(MediaPick.SameAumid("A!b", "a!B"));
        Assert.False(MediaPick.SameAumid(null, null));
    }

    [Fact]
    public void A_pick_that_blinks_out_is_held_for_60_s_and_applies_again_when_it_is_back()
    {
        var pick = MediaPick.Make("Spotify.exe", Spotify, 0);
        var away = MediaPick.Choose(pick, new[] { Chrome }, Chrome, false, 1000);
        Assert.Equal("waiting", away.By);
        Assert.Equal("current", away.Auto);
        Assert.Equal(1000, away.Pick!.GoneSince);
        Assert.Equal(60000, away.Waiting!.LeftMs);
        Assert.Contains("your pick is kept for up to 60 s", away.Waiting.Reason);
        // a short blink never drops it: it is back, and the grace starts over
        var back = MediaPick.Choose(away.Pick, new[] { Spotify, Chrome }, Chrome, false, 30000);
        Assert.Equal("you", back.By);
        Assert.Null(back.Pick!.GoneSince);
        var again = MediaPick.Choose(back.Pick, new[] { Chrome }, Chrome, false, 40000);
        Assert.Equal(60000, again.Waiting!.LeftMs);
    }

    [Fact]
    public void After_60_s_absent_the_pick_drops_to_automatic_with_a_reason_and_its_return_does_not_repick_it()
    {
        var pick = MediaPick.Make("Spotify.exe", Spotify, 0)! with { GoneSince = 0 };
        var c = MediaPick.Choose(pick, new[] { Chrome }, Chrome, false, 60000);
        Assert.Null(c.Pick);
        Assert.Equal("current", c.By);
        Assert.Equal("Spotify's media session was gone for 60 s, so the card went back to automatic", c.Dropped!.Reason);
        var later = MediaPick.Choose(c.Pick, new[] { Spotify, Chrome }, Chrome, false, 61000);
        Assert.Equal("current", later.By);
    }

    [Fact]
    public void Automatic_is_Windows_current_except_under_the_guard_and_the_first_when_there_is_none()
    {
        var prot = S("Lessons", "Lessons", true, prot: true);
        var player = S("Spotify.exe", "Spotify", playing: false), playing = S("Chrome", "Chrome", playing: true);
        Assert.Equal("current", MediaPick.Choose(null, new[] { prot, player }, prot, false, 0).By);
        var g = MediaPick.Choose(null, new[] { prot, player, playing }, prot, true, 0);
        Assert.Equal(("protected-rule", "Chrome"), (g.By, g.Want));   // a playing one first
        var first = MediaPick.Choose(null, new[] { player, playing }, null, false, 0);
        Assert.Equal(("first", "Spotify.exe"), (first.By, first.Want));
        Assert.Null(MediaPick.Choose(null, Array.Empty<MediaPickSession>(), null, false, 0).By);
    }

    [Fact]
    public void With_only_protected_apps_there_is_no_chip_and_a_pick_of_one_drops_at_once()
    {
        var prot = S("Lessons", "Lessons", prot: true);
        var pick = MediaPick.Make("Lessons", prot, 0);
        var c = MediaPick.Choose(pick, new[] { prot }, prot, true, 10);
        Assert.True(c.ProtectedOnly);
        Assert.Equal("protected-only", c.By);
        Assert.Null(c.Pick);
        Assert.Contains("does not outlive the other player", c.Dropped!.Reason);
    }

    [Fact]
    public void The_guard_file_is_normalised_and_a_missing_or_broken_one_protects_nothing()
    {
        var c = MediaGuard.Normalize(JsonNode.Parse("{\"apps\":[\" Lessons \",\"lessons\",\"\",5,\"" + new string('x', 90) + "\"],\"label\":\"school\"}"));
        Assert.Equal(new[] { "Lessons", new string('x', 80) }, c.Apps);
        Assert.True(c.Guard);   // default true once apps are listed
        Assert.Equal("school", c.Label);
        Assert.False(MediaGuard.Normalize(JsonNode.Parse("{\"apps\":[\"a\"],\"guard\":false}")).Guard);

        string dir = Path.Combine(Path.GetTempPath(), "mantle-mg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            Assert.Equal("no media-guard.json, so no media app is protected", MediaGuard.Read(dir).Reason);
            File.WriteAllText(Path.Combine(dir, MediaGuard.FileName), "{nope");
            var bad = MediaGuard.Read(dir);
            Assert.True(bad.Found);
            Assert.StartsWith("media-guard.json is not valid JSON (", bad.Reason);
            Assert.Empty(bad.Apps);
            File.WriteAllText(Path.Combine(dir, MediaGuard.FileName), "﻿[1]");
            Assert.Equal("media-guard.json is not a JSON object, so no media app is protected", MediaGuard.Read(dir).Reason);
            File.WriteAllText(Path.Combine(dir, MediaGuard.FileName), "{\"apps\":[\"Lessons\"]}");
            Assert.Equal(new[] { "Lessons" }, MediaGuard.Read(dir).Apps);
        } finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_flags_turn_the_rule_on_and_off_and_off_wins_and_the_words_say_how()
    {
        var c = MediaGuard.Normalize(JsonNode.Parse("{\"apps\":[\"Lessons\"],\"guard\":false,\"label\":\"(school)\"}"));
        Assert.Equal(new MediaGuardState(false, "config"), MediaGuard.State(c, false, false));
        Assert.Equal(new MediaGuardState(true, "flag"), MediaGuard.State(c, true, false));
        Assert.Equal(new MediaGuardState(false, "controls-flag"), MediaGuard.State(c, true, true));
        Assert.Equal(new MediaGuardState(false, "none"), MediaGuard.State(MediaGuard.Normalize(null), true, false));
        Assert.Equal("Lessons", MediaGuard.Match(c, "x", "LESSONS Learning", null));
        Assert.Equal("Lessons", MediaGuard.Match(c, null, null, "lessons - lesson 4"));
        Assert.Null(MediaGuard.Match(c, "Spotify.exe", "Spotify", "song"));
        Assert.Equal("Lessons (school) · controls off so a stray click can't pause it", MediaGuard.ReasonFor(c, "Lessons"));
        Assert.Equal("Mantle was started with --media-guard; start it without that flag to allow them", MediaGuard.HowFor(new(true, "flag")));
        var on = MediaGuard.Summary(c, true, false);
        Assert.Equal("controls are off for Lessons so a stray click can't pause them", (string?)on["reason"]);
        Assert.Equal("media controls are on for Lessons (Mantle was started with --media-controls)", (string?)MediaGuard.Summary(c, false, true)["reason"]);
        Assert.Equal("media controls are on for Lessons (start Mantle with --media-guard to turn them off)", (string?)MediaGuard.Summary(c, false, false)["reason"]);
    }
}
