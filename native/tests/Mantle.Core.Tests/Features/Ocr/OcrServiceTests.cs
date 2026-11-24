using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Ocr;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Ocr;

// lane r10-ocr: Copy text's availability and its request (lib/features/screenshot.js#checkOcr, #ocrRecheck, #ocrForPage,
// #commitText; OCR-001, 003..007, 009, 011, 013, 018, 020..022). A fake sidecar answers ocr.status / shot.ocr with made-up
// replies and a fake clipboard records what would be written: no Windows OCR, no real clipboard.
public class OcrServiceTests
{
    const string Secret = "Meet at 3:45 behind the station";

    sealed class Fake : IOcrClipboard
    {
        public readonly List<(string Verb, JsonObject Args, TimeSpan Timeout)> Calls = new();
        public readonly Dictionary<string, Func<JsonObject, PlatformReply>> Answers = new(StringComparer.Ordinal);
        public readonly List<string> Log = new();
        public readonly List<string> Clip = new();
        public Exception? ClipThrows;
        public int Released;
        public double Now = 1000;

        public OcrService Service(bool test = false, string? expect = null) => new(Send, this, Log.Add, () => Now, test, expect);

        ValueTask<PlatformReply> Send(string verb, JsonObject args, TimeSpan timeout)
        {
            Calls.Add((verb, (JsonObject)JsonNode.Parse(args.ToJsonString())!, timeout));
            return ValueTask.FromResult(Answers.TryGetValue(verb, out var f) ? f(args) : PlatformReply.Fail(PlatformOutcome.Failed, "unknown command: " + verb));
        }

        public ValueTask WriteTextAsync(string text)
        {
            if (ClipThrows != null) throw ClipThrows;
            Clip.Add(text);
            return ValueTask.CompletedTask;
        }

        public ValueTask Release() { Released++; return ValueTask.CompletedTask; }
    }

    static PlatformReply Ok(string json) => PlatformReply.Success(JsonDocument.Parse(json).RootElement.Clone());

    const string Ready = "{\"ok\":true,\"available\":true,\"lang\":\"en-US\",\"langs\":[\"en-US\",\"de-DE\"],\"maxDim\":10000,\"via\":\"winrt\",\"why\":null,\"engineMs\":12.5,\"retryMs\":30000}";
    const string NoLang = "{\"ok\":true,\"available\":false,\"lang\":null,\"langs\":[],\"maxDim\":10000,\"via\":\"winrt\",\"why\":\"no OCR language is installed (Windows Settings > Time & language > Language & region: add a language)\"}";
    const string NoWinRt = "{\"ok\":true,\"available\":false,\"lang\":null,\"langs\":[],\"via\":\"none\",\"why\":\"this sidecar was built without WinRT (build.ps1 found no System32\\\\WinMetadata winmds), so Windows' OCR cannot be reached\"}";

    static string Rec(params (string Text, int Y)[] lines) =>
        "{\"lines\":[" + string.Join(",", lines.Select(l => "{\"text\":" + JsonSerializer.Serialize(l.Text) + ",\"x\":4,\"y\":" + l.Y + ",\"w\":300,\"h\":14}")) + "]," +
        "\"lang\":\"en-US\",\"scale\":2,\"angle\":0,\"x\":100,\"y\":200,\"w\":320,\"h\":60,\"ms\":{\"crop\":0.4,\"scale\":3.1,\"ocr\":15.4,\"total\":19}}";

    static async Task<(Fake F, OcrService S)> ReadyService(bool test = false, string? expect = null)
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => Ok(Ready);
        var s = f.Service(test, expect);
        await s.CheckAsync();
        return (f, s);
    }

    static readonly OcrRect R = new(100, 200, 320, 60);

    // ------------------------------------------------------------ availability (OCR-001, 003..007)

    [Fact]
    public void BeforeTheFirstAnswer_CopyTextIsDisabledWithItsReason()
    {
        var s = new Fake().Service();
        Assert.False(s.State.Available);
        Assert.Equal(OcrWords.NotChecked, s.State.Why);
        var p = s.ForPage();
        Assert.False(p.Available);
        Assert.Equal(OcrWords.StillStarting, p.Why);
    }

    [Fact]
    public async Task Status_AskedWithAn8sTimeout_ReadyLogsTheLanguageAndInstalled()
    {
        var (f, s) = await ReadyService();
        var call = Assert.Single(f.Calls);
        Assert.Equal("ocr.status", call.Verb);
        Assert.Equal(TimeSpan.FromMilliseconds(8000), call.Timeout);
        Assert.True(s.State.Available);
        Assert.Equal("en-US", s.State.Lang);
        Assert.Equal(new OcrForPage(true, null, "en-US"), s.ForPage());
        Assert.Contains("Copy text: Windows' OCR is ready (en-US; installed: en-US, de-DE)", f.Log);
    }

    [Fact]
    public async Task Status_NoLanguage_DisabledWithTheSettingsPath()
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => Ok(NoLang);
        var s = f.Service();
        await s.CheckAsync();
        Assert.False(s.State.Available);
        Assert.Equal(OcrWords.NoLanguage, s.ForPage().Why);
        Assert.Contains(f.Log, l => l.StartsWith("Copy text is unavailable: no OCR language is installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_UnavailableWithoutAReason_StillCarriesOne()
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => Ok("{\"ok\":true,\"available\":false}");
        var s = f.Service();
        await s.CheckAsync();
        Assert.Equal(OcrWords.NoReasonGiven, s.State.Why);
        Assert.False(string.IsNullOrWhiteSpace(s.ForPage().Why));
    }

    [Fact]
    public async Task Status_OlderSidecarWithoutTheVerb_ReadsOutOfDate()
    {
        var f = new Fake();   // no ocr.status answer: "unknown command: ocr.status"
        var s = f.Service();
        await s.CheckAsync();
        Assert.Equal(OcrWords.OlderSidecar, s.State.Why);
        Assert.Equal(OcrWords.OutOfDate, s.ForPage().Why);
    }

    [Fact]
    public async Task Status_ATimeout_CouldNotBeAsked_AndRecheckedLater()
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, "sidecar \"ocr.status\" timed out after 8000 ms");
        var s = f.Service();
        await s.CheckAsync();
        Assert.StartsWith("Windows’ OCR could not be asked (", s.State.Why, StringComparison.Ordinal);
        Assert.Equal(OcrWords.TryAgain, s.ForPage().Why);   // the /OCR/ entry comes before the timeout one in PLAIN, as in Electron
        Assert.True(OcrService.ShouldRecheck(s.State, s.AskedAtMs, f.Now + OcrService.RecheckMs));
    }

    [Fact]
    public void Recheck_After30s_NotSooner_NeverWhileAvailable_NeverWithoutWinRt()
    {
        var timedOut = new OcrState(false, "timed out", null);
        Assert.True(OcrService.ShouldRecheck(timedOut, 1000, 1000 + OcrService.RecheckMs));
        Assert.False(OcrService.ShouldRecheck(timedOut, 1000, 1000 + OcrService.RecheckMs - 1));
        Assert.False(OcrService.ShouldRecheck(new OcrState(true, null, "en-US"), 0, 1e12));
        Assert.True(OcrService.ShouldRecheck(new OcrState(false, "no OCR language is installed", null), 0, OcrService.RecheckMs));
        Assert.False(OcrService.ShouldRecheck(new OcrState(false, "this sidecar was built without WinRT", null), 0, 1e12));
    }

    [Fact]
    public async Task RecheckAtCapture_AsksAgain_AndTheOverlaysHearTheChange()
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => Ok(NoLang);
        var s = f.Service();
        var heard = new List<OcrForPage>();
        s.Changed += heard.Add;
        await s.CheckAsync();
        Assert.Empty(heard);                       // unavailable -> unavailable is no change
        f.Now += 1000;
        Assert.False(s.RecheckAtCapture());        // too soon
        f.Now += OcrService.RecheckMs;
        f.Answers["ocr.status"] = _ => Ok(Ready);  // a language was added meanwhile
        Assert.True(s.RecheckAtCapture());
        Assert.Equal(2, f.Calls.Count(c => c.Verb == "ocr.status"));
        Assert.True(s.State.Available);
        Assert.Equal(new OcrForPage(true, null, "en-US"), Assert.Single(heard));
    }

    [Fact]
    public async Task NoWinRt_NeverAskedAgain()
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => Ok(NoWinRt);
        var s = f.Service();
        await s.CheckAsync();
        Assert.Equal(OcrWords.NoWinRt, s.ForPage().Why);
        f.Now += 10 * OcrService.RecheckMs;
        Assert.False(s.RecheckAtCapture());
        Assert.Single(f.Calls);
    }

    // ------------------------------------------------------------ Copy text (OCR-009, 011, 013, 018, 020..022)

    [Fact]
    public async Task CopyText_SameFrameRect_20sBackstop_ReleasedAfter_PlainTextOnTheClipboard()
    {
        var (f, s) = await ReadyService();
        f.Answers["shot.ocr"] = _ => Ok(Rec(("Which is a prime number?", 0), ("Pick one.", 20)));
        var card = await s.CopyTextAsync(7, 42, R, "drag", f.Release);
        var call = f.Calls.Last();
        Assert.Equal("shot.ocr", call.Verb);
        Assert.Equal(TimeSpan.FromMilliseconds(20000), call.Timeout);
        Assert.Equal("{\"grab\":42,\"x\":100,\"y\":200,\"w\":320,\"h\":60}", call.Args.ToJsonString());
        Assert.Equal(1, f.Released);
        Assert.Equal("Which is a prime number?\r\nPick one.", Assert.Single(f.Clip));
        Assert.True(card.Copied);
        Assert.Null(card.TextError);
        Assert.Equal("Copied 2 lines", card.Note);
        Assert.Equal("Copied 2 lines", card.Headline);
        Assert.Equal(2, card.Lines);
    }

    [Fact]
    public void Lines_ReadingOrderWithBoxes_PlainStringsToo()
    {
        var rec = JsonDocument.Parse("{\"lines\":[{\"text\":\"a\",\"x\":1,\"y\":2,\"w\":3,\"h\":4},\"b\",7]}").RootElement;
        var ls = OcrService.Lines(rec);
        Assert.Equal(2, ls.Count);
        Assert.Equal(new OcrLine("a", 1, 2, 3, 4), ls[0]);
        Assert.Equal(new OcrLine("b"), ls[1]);
        Assert.Empty(OcrService.Lines(default));
    }

    [Fact]
    public async Task CopyText_NothingRecognised_ClipboardLeftAlone_NoError()
    {
        var (f, s) = await ReadyService();
        f.Answers["shot.ocr"] = _ => Ok(Rec());
        var card = await s.CopyTextAsync(1, 3, R, "drag", f.Release);
        Assert.Empty(f.Clip);
        Assert.False(card.Copied);
        Assert.Null(card.TextError);
        Assert.Null(card.CopyError);
        Assert.Equal(OcrText.NoText, card.Note);
        Assert.Contains(f.Log, l => l.EndsWith("; nothing copied", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("shot.ocr: the selection is too small to hold text (5x5 px)", "the selection was too small")]
    [InlineData("shot.ocr: the rect 99999,0 10x10 is outside the capture", "the selection was off the screen")]
    [InlineData("shot.ocr: capture 3 is gone (it was released, or a newer one replaced it)", "the capture had already closed: take it again")]
    [InlineData("shot.ocr: Windows' OCR could not read the selection (HRESULT 0x80004005: Unspecified error)", OcrWords.TryAgain)]
    public async Task CopyText_ARefusal_CouldNotBeRead_InPlainWords_NothingCopied_FrameStillReleased(string error, string plain)
    {
        var (f, s) = await ReadyService();
        f.Answers["shot.ocr"] = _ => PlatformReply.Fail(PlatformOutcome.Failed, error);
        var card = await s.CopyTextAsync(2, 3, new OcrRect(0, 0, 5, 5), "drag", f.Release);
        Assert.Empty(f.Clip);
        Assert.Equal(1, f.Released);
        Assert.False(card.Copied);
        Assert.Equal(OcrWords.CouldNotRead, card.Note);
        Assert.Equal(plain, card.TextError);
        Assert.Contains(f.Log, l => l.StartsWith("#2: text NOT read (", StringComparison.Ordinal) && !l.Contains("shot.ocr: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyText_WhileUnavailable_NeverAsksTheSidecar_CardCarriesTheReason()
    {
        var f = new Fake();
        f.Answers["ocr.status"] = _ => Ok(NoLang);
        var s = f.Service();
        await s.CheckAsync();
        var card = await s.CopyTextAsync(4, 9, R, "drag", f.Release);
        Assert.DoesNotContain(f.Calls, c => c.Verb == "shot.ocr");
        Assert.Equal(1, f.Released);
        Assert.Equal(OcrWords.CouldNotRead, card.Note);
        Assert.Equal(OcrWords.NoLanguage, card.TextError);
    }

    [Fact]
    public async Task CopyText_AClipboardThatFails_SaysSoInPlainWords()
    {
        var (f, s) = await ReadyService();
        f.Answers["shot.ocr"] = _ => Ok(Rec(("x", 0)));
        f.ClipThrows = new InvalidOperationException("the clipboard could not be reached: another app is holding the clipboard");
        var card = await s.CopyTextAsync(5, 3, R, "drag", f.Release);
        Assert.False(card.Copied);
        Assert.Equal("another app was using the clipboard: take it again", card.CopyError);
        Assert.Contains(f.Log, l => l.Contains("; NOT copied (", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ privacy (OCR-022) and --test (OCR-023)

    [Fact]
    public async Task Privacy_TheRecognisedTextIsNeverInTheLog_OnlyCountsAndTimings()
    {
        var (f, s) = await ReadyService();
        f.Answers["shot.ocr"] = _ => Ok(Rec((Secret, 0), ("second line here", 20)));
        await s.CopyTextAsync(6, 3, R, "drag", f.Release);
        var line = Assert.Single(f.Log, l => l.StartsWith("#6: ", StringComparison.Ordinal));
        Assert.StartsWith("#6: ocr: 2 lines, ", line, StringComparison.Ordinal);
        Assert.Contains("(ocr 15 ms at 2x, en-US; 320x60, drag); copied", line, StringComparison.Ordinal);
        foreach (var l in f.Log) {
            Assert.DoesNotContain("Meet", l, StringComparison.Ordinal);
            Assert.DoesNotContain("station", l, StringComparison.Ordinal);
            Assert.DoesNotContain("second line", l, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TestMode_ADryRun_NeverTheRealClipboard_OnlyTheScore()
    {
        var (f, s) = await ReadyService(test: true, expect: Secret);
        f.Answers["shot.ocr"] = _ => Ok(Rec((Secret, 0)));
        var card = await s.CopyTextAsync(8, 3, R, "drag", f.Release);
        Assert.Empty(f.Clip);
        Assert.True(card.Copied);
        Assert.Equal(new OcrDryRun(1, Secret.Length, 1, true), s.LastDryRun);
        Assert.DoesNotContain(f.Log, l => l.Contains("Meet", StringComparison.Ordinal));
    }
}
