using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Ocr;

// lane r10-ocr: the screenshot overlay's "Copy text", the pure half (lib/features/_ocr-text.js; OCR-014..017, 019, 020,
// 022, 023). What Windows' OCR hands back (sidecar/Ocr.cs via shot.ocr: one entry per line, in reading order, with the
// line's box in physical px) becomes the plain text for the clipboard and the card's one-line confirmation.
//
// PRIVACY: nothing here logs. LogLine() is the ONLY thing about a recognition that may reach the log, and it is counts
// and timings by construction (it never sees the text).

/// <summary>One recognised line as the sidecar sends it: the text and its box (crop px). A line without a box has nulls.</summary>
public sealed record OcrLine(string? Text, double? X = null, double? Y = null, double? W = null, double? H = null);

/// <summary>The clipboard text and its counts. <see cref="Text"/> "" = nothing recognised. Blank paragraph lines are not counted.</summary>
public sealed record OcrShaped(string Text, int Lines, int Chars)
{
    public static readonly OcrShaped Empty = new("", 0, 0);
}

public static class OcrText
{
    /// Windows' own line break: what Notepad, Word and a text box expect.
    public const string Eol = "\r\n";
    public const int PreviewChars = 40;
    public const string NoText = "No text found in that area";

    // JS \s plus NBSP, the U+2000..U+200B spaces, the ideographic space (as _ocr-text.js#tidy); ﻿ is in JS's \s.
    static readonly Regex Spaces = new(@"[\s  -​　﻿]+", RegexOptions.CultureInvariant);
    static readonly Regex TrailPunct = new(@"[\s.,;:]+$", RegexOptions.CultureInvariant);

    public static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToArray();
        int m = s.Length >> 1;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }

    /// <summary>One line's text, tidied: every run of whitespace (tabs, NBSP, stray breaks) is one space; no ends.</summary>
    public static string Tidy(string? s) => Spaces.Replace(s ?? "", " ").Trim(' ');

    /// <summary>
    /// Where a paragraph ends: the indices i such that a blank line goes BEFORE lines[i]. With fewer than two positive gaps
    /// a gap must exceed 1.5 line heights; otherwise it must exceed max(median line height, 1.7x the median positive gap).
    /// A line starting above the previous one (the next column) is never a break; lines without boxes get no guessing.
    /// </summary>
    public static ISet<int> ParagraphBreaks(IReadOnlyList<OcrLine> lines)
    {
        var o = new HashSet<int>();
        bool boxed = lines.All(l => l is { Y: double y, H: double h } && double.IsFinite(y) && double.IsFinite(h) && h > 0);
        if (!boxed || lines.Count < 2) return o;
        double hMed = Median(lines.Select(l => l.H!.Value).ToArray());
        var gaps = new double[lines.Count - 1];
        for (int i = 1; i < lines.Count; i++) gaps[i - 1] = lines[i].Y!.Value - (lines[i - 1].Y!.Value + lines[i - 1].H!.Value);
        var positive = gaps.Where(g => g > 0).ToArray();
        double limit = positive.Length >= 2 ? Math.Max(hMed, 1.7 * Median(positive)) : 1.5 * hMed;
        for (int i = 1; i < lines.Count; i++) if (gaps[i - 1] > limit) o.Add(i);
        return o;
    }

    /// <summary>Plain strings are accepted too (no boxes, so no paragraph guessing).</summary>
    public static OcrShaped Shape(IEnumerable<string?>? lines) => Shape(lines?.Select(t => new OcrLine(t)));

    /// <summary>
    /// OCR lines -> the clipboard text: blank lines dropped, each line tidied, joined with CRLF, a blank line at a
    /// paragraph gap (<see cref="ParagraphBreaks"/>). Chars counts the CRLFs.
    /// </summary>
    public static OcrShaped Shape(IEnumerable<OcrLine?>? lines)
    {
        var kept = new List<OcrLine>();
        foreach (var l in lines ?? Array.Empty<OcrLine?>()) {
            if (l == null) continue;
            string t = Tidy(l.Text);
            if (t.Length > 0) kept.Add(l with { Text = t });
        }
        if (kept.Count == 0) return OcrShaped.Empty;
        var breaks = ParagraphBreaks(kept);
        var parts = new List<string>();
        for (int i = 0; i < kept.Count; i++) { if (breaks.Contains(i)) parts.Add(""); parts.Add(kept[i].Text!); }
        string text = string.Join(Eol, parts);
        return new OcrShaped(text, kept.Count, text.Length);
    }

    /// <summary>The first ~40 characters on one line (breaks as a space), cut at a word past 60%, with an ellipsis when cut.</summary>
    public static string Preview(string? text, int n = 0)
    {
        int max = n > 0 ? n : PreviewChars;
        string flat = Tidy(text);
        if (flat.Length <= max) return flat;
        string cut = flat.Substring(0, max);
        int sp = cut.LastIndexOf(' ');
        return TrailPunct.Replace(sp >= max * 0.6 ? cut.Substring(0, sp) : cut, "") + "…";
    }

    /// <summary>What the card says: "Copied 3 lines" ("1 line"), or <see cref="NoText"/>.</summary>
    public static string Headline(OcrShaped? shaped)
    {
        if (shaped == null || shaped.Lines == 0) return NoText;
        return "Copied " + shaped.Lines.ToString(CultureInfo.InvariantCulture) + " line" + (shaped.Lines == 1 ? "" : "s");
    }

    /// <summary>The ONLY log line about a recognition: counts and timings, never the text.</summary>
    public static string LogLine(OcrShaped? shaped, double ms, string? extra = null)
    {
        int n = shaped?.Lines ?? 0, c = shaped?.Chars ?? 0;
        return "ocr: " + n.ToString(CultureInfo.InvariantCulture) + " line" + (n == 1 ? "" : "s") + ", "
            + c.ToString(CultureInfo.InvariantCulture) + " char" + (c == 1 ? "" : "s") + ", "
            + JsRound(ms).ToString(CultureInfo.InvariantCulture) + " ms" + (string.IsNullOrEmpty(extra) ? "" : " (" + extra + ")");
    }

    /// <summary>1 - Levenshtein distance / the longer length: 1 identical, 0 nothing alike (tests only; MANTLE_SHOT_TEST_EXPECT).</summary>
    public static double Similarity(string? a, string? b)
    {
        a ??= ""; b ??= "";
        if (a.Length == 0 && b.Length == 0) return 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++) {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// JavaScript's Math.round: half rounds up (towards +infinity).
    internal static long JsRound(double x) => double.IsFinite(x) ? (long)Math.Floor(x + 0.5) : 0;
}
