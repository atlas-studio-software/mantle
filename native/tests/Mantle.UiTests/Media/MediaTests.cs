using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Media;

/// <summary>
/// The now-playing chip and card (Surfaces/Bar/Media, lane native-r7-media) from outside. MANTLE_TEST_MEDIA gives every
/// runner the same two made-up sessions, and every press is a DRY RUN in --test: the log says what would have been sent,
/// and no media key or control ever reaches Windows (or a real Spotify). Ids: docs/lanes/media.md.
/// </summary>
[TestClass]
public class MediaTests
{
    public const string Fixture = "{\"sessions\":[" +
        "{\"aumid\":\"Spotify.exe\",\"app\":{\"name\":\"Spotify\",\"icon\":null},\"title\":\"Night Drive\",\"artist\":\"The Testers\",\"album\":\"Fixtures\"," +
        "\"status\":\"playing\",\"playing\":true,\"current\":true,\"can\":{\"play\":true,\"pause\":true,\"toggle\":true,\"next\":true,\"previous\":false}," +
        "\"timeline\":{\"available\":true,\"positionMs\":65000,\"durationMs\":215000},\"artworkReason\":\"the app did not publish artwork\"}," +
        "{\"aumid\":\"Chrome\",\"app\":{\"name\":\"Google Chrome\",\"icon\":null},\"title\":\"A lecture\",\"status\":\"paused\",\"playing\":false,\"current\":false," +
        "\"can\":{\"play\":true,\"toggle\":true},\"timeline\":{\"available\":false,\"reason\":\"no position: Google Chrome does not publish one\"}}]}";

    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 30))); }
    static void Logged(string dir, string needle, string why, int ms = 3000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Help(AutomationElement e) => e.Properties.HelpText.ValueOrDefault ?? "";

    /// A source row click; a click the card refused because the list had just moved (BAR-930) is made again, as the
    /// card's own words ask ("Click again if you meant it").
    static void PickRow(AutomationElement w, string dir, string id, string label)
    {
        for (int i = 0; i < 3; i++) {
            ClickEl(El(w, id));
            if (FrameProbe.LogHas(dir, $"media: the card picked {label}", 1500)) return;
        }
        Assert.Fail($"the card never picked {label}. Log:
" + Tail(dir));
    }

    static (BarApp App, string Dir) Launch(string profile, string theme = "light", string? fixture = Fixture, string? guard = null, params string[] flags)
    {
        string dir = FrameProbe.NewDir(profile);
        if (guard != null) {
            var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
            Directory.CreateDirectory(ud);
            File.WriteAllText(Path.Combine(ud, "media-guard.json"), guard);
        }
        var env = new Dictionary<string, string>();
        if (fixture != null) env["MANTLE_TEST_MEDIA"] = fixture;
        var app = BarApp.Launch(profile, theme, env: env, extra: new[] { "--bench-out=" + dir }.Concat(flags));
        return (app, dir);
    }

    static AutomationElement Card(BarApp app, string dir)
    {
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Now playing")), 3000), "the media card did not open. Log:\n" + Tail(dir));
        var w = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("media.card")), 3000), "the pop-up has no media.card");
        return w;
    }

    static AutomationElement El(AutomationElement w, string id)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), 3000);
        Assert.IsNotNull(e, $"no {id}");
        return e!;
    }

    [TestMethod]
    public void The_chip_shows_the_session_its_play_pause_is_a_dry_run_and_the_card_draws_it()
    {
        var (app, dir) = Launch("media-chip");
        using var _ = app;
        Logged(dir, "media: a --test run with MANTLE_TEST_MEDIA", "the fixture was not taken", 15000);
        Logged(dir, "media: the chip shows Spotify (playing)", "the chip did not paint");
        var chip = app.Find("bar.media.chip");
        Assert.AreEqual("Night Drive · The Testers", chip.Name);
        Assert.AreEqual("Night Drive · The Testers\nSpotify · Playing\nClick for the media card", Help(chip));
        var tog = app.Find("bar.media.toggle");
        Assert.AreEqual("Pause Spotify", tog.Name);
        Assert.AreEqual("pause", Status(tog));
        Host.Screenshot("media-chip-light");

        // play/pause from the chip: logged, never sent; the made-up session follows, so the glyph flips
        ClickEl(tog);
        Logged(dir, "media: dry run (--test): toggle for Spotify.exe would ask the sidecar media {\"op\":\"control\",\"action\":\"toggle\",\"aumid\":\"Spotify.exe\"}; nothing was sent", "no dry-run line");
        Assert.IsTrue(Host.Until(() => Status(app.Find("bar.media.toggle")) == "play", 3000), "the chip's glyph did not flip to play: " + Status(app.Find("bar.media.toggle")));
        Assert.AreEqual("paused", Status(app.Find("bar.media.chip")));
        ClickEl(app.Find("bar.media.toggle"));   // and back to playing
        Assert.IsTrue(Host.Until(() => Status(app.Find("bar.media.toggle")) == "pause", 3000), "the chip did not go back to playing");

        // the card: art, title, artist · album, app line, progress, transport
        ClickEl(app.Find("bar.media.chip"));
        var w = Card(app, dir);
        Logged(dir, "media: the card opened", "no opened line");
        Assert.AreEqual("Night Drive", El(w, "media.card.title").Name);
        Assert.AreEqual("The Testers · Fixtures", El(w, "media.card.artist").Name);
        Assert.AreEqual("Spotify · Playing", El(w, "media.card.app").Name);
        Assert.AreEqual("none", Help(El(w, "media.card.art")), "no artwork and no app icon: the note glyph");
        Assert.AreEqual("3:35", El(w, "media.card.dur").Name);
        StringAssert.StartsWith(El(w, "media.card.now").Name, "1:");
        Assert.IsTrue(Status(El(w, "media.card.previous")).StartsWith("off"), "previous is not accepted, so it is greyed");
        Assert.AreEqual("Previous track: Spotify does not accept previous track right now", El(w, "media.card.previous").Name);
        Assert.IsTrue(Status(El(w, "media.card.next")).StartsWith("on"));
        Host.Screenshot("media-card-light");

        // a greyed button still says why when clicked (into the card's note), and sends nothing
        ClickEl(El(w, "media.card.previous"));
        Assert.IsTrue(Host.Until(() => El(w, "media.card.note").Name == "Spotify does not accept previous track right now", 2000), "the card did not say why");
        ClickEl(El(w, "media.card.next"));
        Logged(dir, "media: dry run (--test): next for Spotify.exe", "next was not a dry run");
        Assert.IsTrue(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("media.card.note")) == null, 2000), "a success did not clear the note");

        // the source list: two apps, in name order, Automatic last and lit
        Assert.AreEqual("on", Status(El(w, "media.card.src.auto")));
        Assert.AreEqual("Automatic: showing the session Windows calls current", El(w, "media.card.srcwhy").Name);
        Assert.AreEqual("Google Chrome · A lecture", El(w, "media.card.src.chrome").Name);
        Thread.Sleep(600);   // past the 400 ms settle after the card drew
        PickRow(w, dir, "media.card.src.chrome", "Google Chrome");
        Assert.IsTrue(Host.Until(() => El(w, "media.card.srcwhy").Name == "Showing the source you picked", 3000), "the pick did not take: " + El(w, "media.card.srcwhy").Name);
        Assert.AreEqual("on", Status(El(w, "media.card.src.chrome")));
        Assert.AreEqual("Google Chrome · Paused", El(w, "media.card.app").Name);
        Assert.AreEqual("no position: Google Chrome does not publish one", El(w, "media.card.progwhy").Name);

        // its own chip again closes it
        ClickEl(app.Find("bar.media.chip"));
        Logged(dir, "media: the card closed", "no closed line");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("Playback did not respond"), "a press failed:\n" + Tail(dir));
    }

    [TestMethod]
    public void Under_the_guard_automatic_skips_the_protected_app_and_a_pick_of_it_has_no_controls_but_a_lock()
    {
        string fx = "{\"sessions\":[{\"aumid\":\"Lessons.App\",\"app\":{\"name\":\"Lessons\"},\"title\":\"Unit 4\",\"status\":\"playing\",\"playing\":true,\"current\":true,\"can\":{\"toggle\":true}}," +
            "{\"aumid\":\"Spotify.exe\",\"app\":{\"name\":\"Spotify\"},\"title\":\"Song\",\"status\":\"paused\",\"playing\":false,\"current\":false,\"can\":{\"toggle\":true}}]}";
        // the file says guard:false; --media-guard turns the rule on for this run
        var (app, dir) = Launch("media-guard", "dark", fx, "{\"apps\":[\"Lessons\"],\"guard\":false,\"label\":\"(school)\"}", "--media-guard");
        using var _ = app;
        Logged(dir, "mantle: protected media apps: Lessons, rule on (flag)", "the guard file was not read", 15000);
        Logged(dir, "media: the chip shows Spotify (paused)", "automatic did not show the player rather than the protected app");
        Assert.AreEqual("Play Spotify", app.Find("bar.media.toggle").Name);
        ClickEl(app.Find("bar.media.chip"));
        var w = Card(app, dir);
        Assert.AreEqual("Automatic: showing Spotify rather than Lessons (the protected media apps rule)", El(w, "media.card.srcwhy").Name);
        Assert.AreEqual("Lessons · (school)", El(w, "media.card.src.lessons.app").Name);
        Thread.Sleep(600);
        PickRow(w, dir, "media.card.src.lessons.app", "Lessons");
        Assert.IsTrue(Host.Until(() => El(w, "media.card.srcwhy").Name == "Showing the source you picked", 3000), "the pick did not take");
        Assert.AreEqual("Lessons (school) · controls off so a stray click can't pause it", El(w, "media.card.lockhead").Name);
        Assert.AreEqual("Mantle was started with --media-guard; start it without that flag to allow them", El(w, "media.card.lockhow").Name);
        Assert.IsNull(w.FindFirstDescendant(cf => cf.ByAutomationId("media.card.toggle")), "the card has a play/pause for a protected app");
        Logged(dir, "media: the chip shows Lessons (playing, controls off)", "the chip did not follow the pick");
        Assert.IsNull(app.TryFind("bar.media.toggle", 800), "the chip has a play/pause for a protected app (it must be absent, not greyed)");
        Host.Screenshot("media-card-guard-dark");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("media: dry run"), "a control was sent for a protected app");
    }

    [TestMethod]
    public void A_failed_read_raises_Now_playing_not_reading_and_no_session_hides_the_chip()
    {
        var (app, dir) = Launch("media-fail", fixture: "{\"fail\":\"the sessions could not be read: a test\"}");
        using var _ = app;
        Logged(dir, "media: the chip is hidden (no session shown)", "the chip was not hidden", 15000);
        var pip = app.TryFind("bar.more.pip", 8000);
        Assert.IsNotNull(pip, "no pip on more");
        Assert.IsTrue(Host.Until(() => Help(pip!).Contains("Now playing not reading: The sessions could not be read: a test"), 3000), "the pip does not say it: " + Help(pip!));
    }
}
