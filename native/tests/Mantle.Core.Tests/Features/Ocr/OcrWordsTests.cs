using System.Text.RegularExpressions;
using Mantle.Core.Features.Ocr;
using Xunit;

namespace Mantle.Core.Tests.Features.Ocr;

// lane r10-ocr: Copy text's plain words (lib/features/screenshot.js PLAIN, #plainError, #noPaths; OCR-003..006, 013,
// 021), the OCR reasons out of lib/features/screenshot.test.js's RAW list plus the sidecar's own shot.ocr refusals.
public class OcrWordsTests
{
    static readonly Regex Jargon = new(@"sidecar|\bshot\.\w|Exception|HRESULT|0x[0-9A-F]{4}|Win32|BitBlt|npm|build:|[A-Za-z]:\\|\\\\|undefined|null|NaN|\[object");

    [Theory]
    [InlineData("Windows' OCR could not read the selection (HRESULT 0x80004005: Unspecified error)", OcrWords.TryAgain)]
    [InlineData("no OCR language is installed (Windows Settings > Time & language > Language & region: add a language with its optical character recognition feature)", OcrWords.NoLanguage)]
    [InlineData("Windows\u2019 OCR has not been checked yet (the sidecar is still starting)", OcrWords.StillStarting)]
    [InlineData("the sidecar has no ocr.status command (an older build): rebuild it with npm run build:sidecar", OcrWords.OutOfDate)]
    [InlineData("this sidecar was built without WinRT (build.ps1 found no System32\\WinMetadata winmds), so Windows' OCR cannot be reached", OcrWords.NoWinRt)]
    [InlineData("the selection is too small to hold text (5x5 px)", "the selection was too small")]
    [InlineData("the rect 99999,0 10x10 is outside the capture", "the selection was off the screen")]
    [InlineData("capture 12 is gone (it was released, or a newer one replaced it)", "the capture had already closed: take it again")]
    [InlineData("the sidecar is not running", OcrWords.StillStarting)]
    public void PlainError_EveryOcrReasonReadsInPlainWords(string raw, string want)
    {
        string p = OcrWords.PlainError(raw);
        Assert.Equal(want, p);
        Assert.False(Jargon.IsMatch(p), p);
        Assert.True(p.Length <= 120, p);
    }

    [Fact]
    public void PlainError_NeverEmpty()
    {
        Assert.Equal("no reason was given", OcrWords.PlainError(""));
        Assert.Equal("no reason was given", OcrWords.PlainError(null));
    }

    [Fact]
    public void PlainError_AnUnknownReasonKeepsItsWords_MinusTheTypeAndThePath() =>
        Assert.Equal("the screenshot could not be saved (A generic error occurred in GDI+.)",
            OcrWords.PlainError("shot.save: the screenshot could not be saved (ExternalException: A generic error occurred in GDI+.)"));

    [Fact]
    public void NoPaths_TheWordsAroundAPathStay_UncToo()
    {
        Assert.Equal("the folder a path could not be made (denied)", OcrWords.NoPaths("the folder C:\\A B\\c could not be made (denied)"));
        Assert.Equal("a path failed", OcrWords.NoPaths("\\\\server\\share\\x.png failed"));
    }

    [Theory]
    [InlineData("unknown command: ocr.status", true)]
    [InlineData("not a command", true)]
    [InlineData("timed out", false)]
    [InlineData(null, false)]
    public void MissingCommand_OnlyAnUnknownVerb(string? err, bool want) => Assert.Equal(want, OcrWords.MissingCommand(err));
}
