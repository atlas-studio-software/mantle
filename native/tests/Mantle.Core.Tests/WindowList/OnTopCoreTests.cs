using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.OnTop;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

/// lib/features/_ontop-core.js against a made-up Windows (lib/features/ontop.test.js's rules; lane native-r7-strip).
public sealed class OnTopCoreTests
{
    /// A made-up Windows: windows by handle (pid, on top, alive), every ontop.* call recorded.
    sealed class FakeWin
    {
        public readonly Dictionary<string, (int Pid, bool Top, bool Alive)> W = new();
        public readonly List<string> Calls = new();
        public bool Passive;
        public string? SetFails;

        static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

        public ValueTask<PlatformReply> Send(string verb, JsonObject a)
        {
            Calls.Add(verb + " " + a.ToJsonString());
            string h = a["hwnd"]?.ToString() ?? "";
            switch (verb) {
                case "ontop.set": {
                    if (Passive) return new(PlatformReply.Fail(PlatformOutcome.Failed, OnTopCore.Passive + ": would have kept window " + h + " on top"));
                    if (SetFails != null) return new(PlatformReply.Fail(PlatformOutcome.Failed, SetFails));
                    if (!W.TryGetValue(h, out var w) || !w.Alive) return new(PlatformReply.Fail(PlatformOutcome.Failed, "that window has closed"));
                    bool on = a["on"]!.GetValue<bool>();
                    W[h] = (w.Pid, on, true);
                    return new(PlatformReply.Success(El(new JsonObject { ["hwnd"] = h, ["was"] = w.Top, ["changed"] = w.Top != on, ["tries"] = 1 })));
                }
                case "ontop.info":
                    return W.TryGetValue(h, out var i) && i.Alive
                        ? new(PlatformReply.Success(El(new JsonObject { ["hwnd"] = h, ["pid"] = i.Pid, ["name"] = "App" + h, ["topmost"] = i.Top })))
                        : new(PlatformReply.Fail(PlatformOutcome.Failed, "no such window"));
                case "ontop.check": {
                    var ws = new JsonArray();
                    foreach (var x in a["hwnds"]!.AsArray()) {
                        string k = x!["hwnd"]!.ToString();
                        bool alive = W.TryGetValue(k, out var c) && c.Alive;
                        ws.Add((JsonNode)new JsonObject { ["hwnd"] = k, ["alive"] = alive, ["topmost"] = alive && c.Top });
                    }
                    return new(PlatformReply.Success(El(new JsonObject { ["windows"] = ws })));
                }
            }
            return new(PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: " + verb));
        }
    }

    static (OnTopCore Core, FakeWin Win, List<string> Log, List<IReadOnlyList<OnTopPin>> Saved) Make(bool test = false)
    {
        var win = new FakeWin();
        var log = new List<string>();
        var saved = new List<IReadOnlyList<OnTopPin>>();
        return (new OnTopCore(win.Send, log.Add, saved.Add, test), win, log, saved);
    }

    static OnTopFacts F(string h, int pid, bool top = false, string name = "Notes", string? refuse = null) => new(h, pid, 0, name, "notes.exe", top, refuse);

    [Fact]
    public async Task Pin_then_unpin_is_recorded_saved_and_logged_without_the_name()
    {
        var (core, win, log, saved) = Make();
        win.W["10"] = (500, false, true);
        var r = await core.SetAsync(F("10", 500), true, "menu");
        Assert.Equal("pinned", r.Did); Assert.Equal("Kept on top: Notes", r.Text);
        Assert.True(win.W["10"].Top);
        Assert.Single(core.List);
        Assert.Equal("500", saved.Last().Single().Pid.ToString());
        Assert.Equal("Notes is already kept on top", (await core.SetAsync(F("10", 500), true, "menu")).Text);
        var off = await core.SetAsync(F("10", 500), false, "menu");
        Assert.Equal("No longer on top: Notes", off.Text);
        Assert.False(win.W["10"].Top);
        Assert.Empty(core.List);
        Assert.Contains(log, l => l == "kept on top: an app window (pid 500, window 10) via menu");
        Assert.DoesNotContain(log, l => l.Contains("Notes"));
    }

    [Fact]
    public async Task A_window_on_top_by_itself_is_never_Mantles()
    {
        var (core, win, _, _) = Make();
        win.W["11"] = (5, true, true);
        var r = await core.SetAsync(F("11", 5, top: true), true, "menu");
        Assert.Equal("refused", r.Did);
        Assert.Equal("Notes is already on top", r.Text);
        Assert.Equal("it keeps itself on top (its own setting), so Mantle leaves it exactly as it is", r.Reason);
        Assert.DoesNotContain(win.Calls, c => c.StartsWith("ontop.set", StringComparison.Ordinal));
        var off = await core.SetAsync(F("11", 5, top: true), false, "menu");
        Assert.Equal("Notes keeps itself on top", off.Text);
        Assert.True(win.W["11"].Top);
    }

    [Fact]
    public async Task A_reused_handle_is_forgotten_and_refusals_are_said_in_plain_words()
    {
        var (core, win, log, _) = Make();
        win.W["12"] = (1, false, true);
        await core.ToggleAsync(F("12", 1), "Win+Ctrl+T");
        win.W["12"] = (2, false, true);   // the handle now belongs to another program
        var r = await core.ToggleAsync(F("12", 2), "Win+Ctrl+T");
        Assert.Contains(log, l => l.Contains("its handle now belongs to another program"));
        Assert.Equal("pinned", r.Did);
        var adm = await core.SetAsync(F("13", 3, refuse: "Notes runs as administrator (access denied, UIPI)"), true, "menu");
        Assert.Equal("Can’t keep Notes on top", adm.Text);
        Assert.Equal("Notes runs as administrator: it runs as administrator (or is protected)", adm.Reason);
        Assert.Contains(log, l => l.StartsWith("refused (menu): Can’t keep the window on top: the window runs as administrator", StringComparison.Ordinal));
        Assert.Equal("test run: Mantle only changes its own test windows", OnTopCore.Plain(OnTopCore.Passive + ": would have"));
        Assert.Equal("Nothing to keep on top", (await core.ToggleAsync(null, "Win+Ctrl+T")).Text);
    }

    [Fact]
    public async Task A_test_run_is_a_dry_run()
    {
        var (core, win, _, _) = Make(test: true);
        win.Passive = true;
        win.W["14"] = (4, false, true);
        var r = await core.SetAsync(F("14", 4), true, "menu");
        Assert.Equal("dry", r.Did); Assert.Equal("Dry run: would keep Notes on top", r.Text); Assert.Equal("quiet", r.Tone);
        Assert.Empty(core.List);
    }

    [Fact]
    public async Task Sweep_forgets_closed_and_taken_off_top_and_release_puts_back_every_pin()
    {
        var (core, win, log, _) = Make();
        foreach (var h in new[] { "1", "2", "3" }) { win.W[h] = (int.Parse(h), false, true); await core.SetAsync(F(h, int.Parse(h)), true, "menu"); }
        win.W["1"] = (1, true, false);    // closed
        win.W["2"] = (2, false, true);    // the app took itself off top
        var s = await core.SweepAsync();
        Assert.Equal(2, s.Dropped);
        Assert.Equal("3", core.List.Single().Hwnd);
        Assert.Contains(log, l => l.Contains("its window closed"));
        var put = await core.ReleaseAllAsync("Mantle is quitting");
        Assert.Single(put);
        Assert.False(win.W["3"].Top);
        Assert.Contains(log, l => l == "put back 1 window(s) kept on top (Mantle is quitting)");
    }

    [Fact]
    public async Task Resume_after_a_game_and_adopt_after_a_crash()
    {
        var (core, win, _, _) = Make();
        win.W["5"] = (50, false, true); win.W["6"] = (60, false, true);
        await core.SetAsync(F("5", 50), true, "menu"); await core.SetAsync(F("6", 60), true, "menu");
        var saved = await core.ReleaseAllAsync("a game runs: test");
        win.W["6"] = (61, false, true);   // closed and its handle reused
        var r = await core.ResumeAsync(saved, "the game");
        Assert.Equal(new[] { "Notes" }, r.Back);
        Assert.Equal("Notes: it closed", r.Skipped.Single());
        Assert.True(win.W["5"].Top);
        var (fresh, win2, log2, _) = Make();
        win2.W["7"] = (70, true, true); win2.W["8"] = (80, false, true);
        int kept = await fresh.AdoptAsync(new[] { new OnTopPin("7", 70, "Video"), new OnTopPin("8", 80, "Gone") });
        Assert.Equal(1, kept);
        Assert.Equal("Video", fresh.List.Single().Name);
        Assert.Contains(log2, l => l == "a previous Mantle left 2 window(s) kept on top: 1 still on top, kept");
    }
}
