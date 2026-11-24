using System;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Ocr;

// lane r10-ocr: the plain words of Copy text (lib/features/screenshot.js PLAIN, #plainError, #noPaths, #missingCommand,
// #checkOcr; OCR-003..006, 013, 021). The table is screenshot.js's whole PLAIN list, most specific first, the first
// match wins, so an OCR reason reads exactly as Electron's card does. The raw reason goes to the log (paths removed).

public static class OcrWords
{
    /// Before the first ocr.status answer (OCR-003). Note the typographic apostrophe, as in Electron.
    public const string NotChecked = "Windows’ OCR has not been checked yet (the sidecar is still starting)";
    /// The sidecar has no ocr.status verb (OCR-006): its raw reason, which reads as "out of date" on the card.
    public const string OlderSidecar = "the sidecar has no ocr.status command (an older build): rebuild it with npm run build:sidecar";
    public const string NoReasonGiven = "Windows’ OCR said it is unavailable and gave no reason";
    public const string CouldNotRead = "the text could not be read";
    public const string NoReason = "no reason was given";

    public const string OutOfDate = "part of Mantle is out of date: restart Mantle to update it";
    public const string StillStarting = "part of Mantle is still starting: try again in a few seconds";
    public const string NoLanguage = "Windows has no text recognition installed: add a language in Windows Settings > Time & language > Language & region";
    public const string NoWinRt = "Windows' text recognition cannot be reached by this copy of Mantle";
    public const string TryAgain = "Windows could not read the text in that area: try again";

    /// <summary>"Windows' OCR could not be asked (...)": an ocr.status that failed for another reason.</summary>
    public static string CouldNotAsk(string? error) => "Windows’ OCR could not be asked (" + error + ")";

    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const RegexOptions C = RegexOptions.CultureInvariant;

    static readonly (Regex Re, string Words)[] Plain = {
        (new Regex(@"older build|build:sidecar|no shot\.\w+ command|no ocr\.status command|unknown command", I), OutOfDate),
        (new Regex(@"not been checked yet|still starting|sidecar (is )?(down|not running)|sidecar process", I), StillStarting),
        (new Regex(@"no OCR language", I), NoLanguage),
        (new Regex(@"built without WinRT", I), NoWinRt),
        (new Regex(@"OCR", I), TryAgain),
        (new Regex(@"is gone|was released|replaced it", I), "the capture had already closed: take it again"),
        (new Regex(@"UnauthorizedAccess|access (to [^,;]*)?(is )?denied|0x80070005|read-only", I), "Windows did not let Mantle save there: pick another folder in Mantle Settings > Screenshots"),
        (new Regex(@"disk is full|not enough space|0x80070070|0x80070027", I), "the disk is full: free some space and take it again"),
        (new Regex(@"DirectoryNotFound|could not find a part of the path|could not be made|not a usable path|PathTooLong|network name|network path", I), "the screenshots folder could not be reached: pick another one in Mantle Settings > Screenshots"),
        (new Regex(@"999 (files named|screenshots with this name)", I), "that folder already has 999 screenshots taken in the same second"),
        (new Regex(@"holding the clipboard|clipboard could not be reached|clipboard did not answer|clipboard thread", I), "another app was using the clipboard: take it again"),
        (new Regex(@"timed out|timeout|did not answer within", I), "Mantle took too long to answer: take it again"),
        (new Regex(@"no memory|could not be allocated|OutOfMemory", I), "there was not enough memory for a picture this big"),
        (new Regex(@"outside the capture", I), "the selection was off the screen"),
        (new Regex(@"too small", I), "the selection was too small"),
        (new Regex(@"BitBlt|screen DC|virtual screen|listed no monitors|frame folder|memory DC|could not be captured", I), "Windows did not let Mantle read the screen: try again"),
    };

    /// <summary>A failure's reason for the card, in plain words (the raw one goes to the log). Never empty.</summary>
    public static string PlainError(string? err)
    {
        string e = (err ?? "").Trim();
        if (e.Length == 0) return NoReason;
        foreach (var (re, words) in Plain) if (re.IsMatch(e)) return words;
        string t = NoPaths(e);
        t = Regex.Replace(t, @"^shot\.\w+:\s*", "", C);
        t = Regex.Replace(t, @"\b[A-Z]\w*Exception:\s*", "", C);
        t = Regex.Replace(t, @"\s*\((HRESULT |Win32 error |error )?0x[0-9A-F]{8}\)", "", I);
        t = Regex.Replace(t, @"\s*\(?(HRESULT |Win32 error )0x?[0-9A-F]+\)?:?", "", I);
        t = Regex.Replace(t, @"\s+", " ", C).Trim();
        return t.Length > 0 ? t : NoReason;
    }

    /// <summary>A reason with every file path replaced by "a path" (screenshot.js#noPaths): the log never names a folder.</summary>
    public static string NoPaths(string? s)
    {
        string t = s ?? "";
        t = Regex.Replace(t, @"'[^'\r\n]*[\\/][^'\r\n]*'", "a path", C);
        t = Regex.Replace(t, "\"[^\"\\r\\n]*[\\\\/][^\"\\r\\n]*\"", "a path", C);
        t = Regex.Replace(t, @"(^|[\s(])[A-Za-z]:[\\/](?:[^\\/\r\n,;)]+[\\/])*[^\\/\s,;)]*", "${1}a path", C);
        t = Regex.Replace(t, @"(^|[\s(])\\\\(?:[^\\/\r\n,;)]+[\\/])*[^\\/\s,;)]*", "${1}a path", C);
        t = Regex.Replace(t, @"[^\s'""(]*\\[^\s'"",;)]*", "a path", C);
        t = Regex.Replace(t, "a path( a path)+", "a path", C);
        return t;
    }

    /// <summary>The sidecar does not know the verb (screenshot.js#missingCommand).</summary>
    public static bool MissingCommand(string? err) =>
        Regex.IsMatch(err ?? "", "unknown command|not a command|no such command", I);
}
