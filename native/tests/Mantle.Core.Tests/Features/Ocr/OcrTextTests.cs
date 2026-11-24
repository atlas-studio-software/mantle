using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.Ocr;
using Xunit;

namespace Mantle.Core.Tests.Features.Ocr;

// lane r10-ocr: the pure text shaping of Copy text (lib/features/_ocr-text.js; OCR-014..017, 019, 020, 022, 023),
// ported case for case from lib/features/_ocr-text.test.js. No sidecar, no Windows OCR.
public class OcrTextTests
{
    static OcrLine L(string text, double y, double h = 14) => new(text, 10, y, 200, h);

    // ------------------------------------------------------------ empty (OCR-020)

    public static IEnumerable<object[]> Empties() => new[] {
        new object[] { "no lines", Array.Empty<OcrLine?>() },
        new object[] { "null", null! },
        new object[] { "only blanks", new OcrLine?[] { L("", 0), L("   ", 20), L("\t ", 40) } },
    };

    [Theory]
    [MemberData(nameof(Empties))]
    public void Empty_NoText_NoLines_NoChars(string what, OcrLine?[]? v)
    {
        var s = OcrText.Shape(v);
        Assert.True(s.Text == "" && s.Lines == 0 && s.Chars == 0, what);
    }

    [Fact]
    public void Empty_TheCardSaysNoTextFound() =>
        Assert.Equal("No text found in that area", OcrText.Headline(OcrText.Shape(Array.Empty<OcrLine?>())));

    // ------------------------------------------------------------ trimming (OCR-014)

    [Fact]
    public void Trim_EndsTrimmed_InnerRunsAreOneSpace()
    {
        var s = OcrText.Shape(new[] { L("   Question 7:   A  train\tleaves  ", 0) });
        Assert.Equal("Question 7: A train leaves", s.Text);
        Assert.Equal(1, s.Lines);
        Assert.Equal("Question 7: A train leaves".Length, s.Chars);
    }

    [Fact]
    public void Trim_NbspAndIdeographicSpaceCountAsSpaces() => Assert.Equal("a b c", OcrText.Tidy("a b　c"));

    [Fact]
    public void Trim_ALineBreakInsideOneOcrLineBecomesASpace() => Assert.Equal("a b", OcrText.Tidy("a\r\nb"));

    [Fact]
    public void Trim_NullIsEmpty() => Assert.Equal("", OcrText.Tidy(null));

    // ------------------------------------------------------------ joining (OCR-015)

    [Fact]
    public void Join_EvenlySpacedLinesJoinWithCrlf_AndCharsCountTheCrlfs()
    {
        var s = OcrText.Shape(new[] { L("one", 0), L("two", 20), L("three", 40) });
        Assert.Equal("one\r\ntwo\r\nthree", s.Text);
        Assert.Equal(3, s.Lines);
        Assert.Equal("one\r\ntwo\r\nthree".Length, s.Chars);
    }

    [Fact]
    public void Join_AnEmptyLineInTheMiddleIsDropped()
    {
        var d = OcrText.Shape(new[] { L("a", 0), L(" ", 10), L("b", 20) });
        Assert.Equal("a\r\nb", d.Text);
        Assert.Equal(2, d.Lines);
    }

    [Fact]
    public void Join_PlainStringsJoinLineByLine() => Assert.Equal("x\r\ny", OcrText.Shape(new[] { "x", "y" }).Text);

    // ------------------------------------------------------------ paragraphs (OCR-016, 017)

    [Fact]
    public void Paragraph_AGapWellPastTheUsualOneBecomesABlankLine_NotCounted()
    {
        var s = OcrText.Shape(new[] { L("Which is a prime number?", 0), L("Pick one.", 20), L("A) 21", 70), L("B) 23", 90), L("C) 25", 110) });
        Assert.Equal("Which is a prime number?\r\nPick one.\r\n\r\nA) 21\r\nB) 23\r\nC) 25", s.Text);
        Assert.Equal(5, s.Lines);
    }

    [Fact]
    public void Paragraph_TwoLinesFarApartGetABlankLine() => Assert.Equal("a\r\n\r\nb", OcrText.Shape(new[] { L("a", 0), L("b", 60) }).Text);

    [Fact]
    public void Paragraph_TwoOrdinaryLinesDoNot() => Assert.Equal("a\r\nb", OcrText.Shape(new[] { L("a", 0), L("b", 20) }).Text);

    [Fact]
    public void Paragraph_TheNextColumnIsJustANewLine() =>
        Assert.Equal("a\r\nb\r\nc\r\nd", OcrText.Shape(new[] { L("a", 0), L("b", 20), L("c", 40), L("d", 0) }).Text);

    [Fact]
    public void Paragraph_NoBoxesNoGuessing()
    {
        Assert.Empty(OcrText.ParagraphBreaks(new[] { new OcrLine("a", Y: double.NaN, H: 1), new OcrLine("b", Y: 100, H: 1) }));
        Assert.Empty(OcrText.ParagraphBreaks(new[] { new OcrLine("a"), new OcrLine("b", Y: 100, H: 1) }));
    }

    [Fact]
    public void Paragraph_EvenlyLooseLinesStayOneParagraph() =>
        Assert.Equal("a\r\nb\r\nc\r\nd", OcrText.Shape(new[] { L("a", 0), L("b", 30), L("c", 60), L("d", 90) }).Text);

    // ------------------------------------------------------------ the card and the log (OCR-019, 022)

    static OcrShaped Train() => OcrText.Shape(new[] {
        L("Question 7: A train leaves the station at 3:45 PM", 0), L("traveling 62 miles per hour.", 20), L("How far?", 40) });

    [Fact]
    public void Card_CopiedThreeLines_AndOneLineIsSingular()
    {
        Assert.Equal("Copied 3 lines", OcrText.Headline(Train()));
        Assert.Equal("Copied 1 line", OcrText.Headline(OcrText.Shape(new[] { "x" })));
    }

    [Fact]
    public void Preview_CutAtAWordBoundaryWithAnEllipsis()
    {
        string p = OcrText.Preview(Train().Text);
        Assert.True(p.Length <= 41 && p.EndsWith("…", StringComparison.Ordinal), p);
        Assert.Equal("Question 7: A train leaves the station…", p);
    }

    [Fact]
    public void Preview_LineBreaksShowAsSpaces_ShortTextWhole()
    {
        Assert.Equal("ab cd", OcrText.Preview("ab\r\ncd"));
        Assert.Equal("Hello", OcrText.Preview("Hello"));
    }

    [Fact]
    public void Log_CountsAndTimings_NeverTheText()
    {
        var s = Train();
        string log = OcrText.LogLine(s, 142.4, "ocr 15 ms at 2x, en-US");
        Assert.Equal("ocr: 3 lines, " + s.Chars + " chars, 142 ms (ocr 15 ms at 2x, en-US)", log);
        foreach (var word in new[] { "Question", "train", "miles", "How far" }) Assert.DoesNotContain(word, log, StringComparison.Ordinal);
        Assert.Equal("ocr: 0 lines, 0 chars, 30 ms", OcrText.LogLine(OcrText.Shape(Array.Empty<OcrLine?>()), 30));
    }

    // ------------------------------------------------------------ similarity (OCR-023)

    [Fact]
    public void Similarity_IdenticalBothEmptyOneWrongNothingAlike()
    {
        Assert.Equal(1, OcrText.Similarity("abc", "abc"));
        Assert.Equal(1, OcrText.Similarity("", ""));
        Assert.True(Math.Abs(OcrText.Similarity("abcdefghij", "abcdefghiX") - 0.9) < 1e-9);
        Assert.Equal(0, OcrText.Similarity("aaa", "bbb"));
    }
}
