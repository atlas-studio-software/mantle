using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Ocr;

// lane r10-ocr: Copy text's availability and its one request per selection (lib/features/screenshot.js#checkOcr,
// #ocrForPage, #ocrRecheck, #commitText; OCR-001, 003..007, 009, 011, 013, 018, 020..022). Electron has no feature of
// its own for this (it lives inside screenshot.js), so this is a service the screenshot overlay (SHOT rows) calls:
// CheckAsync at sidecar ready, ShouldRecheck at each capture, ForPage for the overlay's Copy text button, CopyTextAsync
// at a committed text drag. Talks only to the local sidecar (Windows.Media.Ocr in sidecar/Ocr.cs: local, offline).
//
// PRIVACY: the recognised text goes to the injected clipboard and the card, nowhere else. Every log line here is
// counts, timings, sizes and reasons with paths removed: never a pixel, never a word of the text.

/// <summary>Can Windows' OCR be reached. Unavailable always carries <see cref="Why"/> (raw; the card shows <see cref="OcrWords.PlainError"/>).</summary>
public sealed record OcrState(bool Available, string? Why, string? Lang)
{
    public static readonly OcrState NotChecked = new(false, OcrWords.NotChecked, null);
}

/// <summary>What the overlay's Copy text button gets: disabled with the plain reason, or the engine's language.</summary>
public sealed record OcrForPage(bool Available, string? Why, string? Lang);

/// <summary>A selection in PHYSICAL virtual-screen px, as shot.ocr takes it (the overlay maps its drag to these).</summary>
public sealed record OcrRect(int X, int Y, int W, int H);

/// <summary>The card for a Copy text: Note is the headline or "the text could not be read"; errors are plain words.</summary>
public sealed record OcrCard(bool Copied, string? CopyError, string? TextError, string Note, int Lines, int Chars, string? Headline, string? Preview);

/// <summary>--test only: what WOULD have gone on the clipboard, as counts and a similarity score, never the text.</summary>
public sealed record OcrDryRun(int Lines, int Chars, double? Similarity, bool? Exact);

/// <summary>The clipboard hand-off: plain text only. Throws (plain words) when the clipboard cannot be written.</summary>
public interface IOcrClipboard
{
    ValueTask WriteTextAsync(string text);
}

/// <summary>The clipboard the app hands over while native Mantle has no screenshot overlay (lane r11-app-wiring): it
/// writes nothing and says why, so a Copy text that somehow comes is a "NOT copied (...)" line and a card error, never a
/// silent no-op and never the user's clipboard.</summary>
public sealed class NoOverlayClipboard : IOcrClipboard
{
    public const string Words = "native Mantle has no screenshot overlay yet, so Copy text puts nothing on the clipboard";
    public static readonly NoOverlayClipboard Instance = new();
    NoOverlayClipboard() { }
    public ValueTask WriteTextAsync(string text) => throw new InvalidOperationException(Words);
}

public sealed class OcrService
{
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromMilliseconds(8000);   // ocr.status at sidecar ready
    public static readonly TimeSpan OcrTimeout = TimeSpan.FromMilliseconds(20000);     // shot.ocr: 10-70 ms measured; a backstop
    public const double RecheckMs = 30000;                                              // unavailable: asked again at a capture, at most this often

    public delegate ValueTask<PlatformReply> Sender(string verb, JsonObject args, TimeSpan timeout);

    readonly Sender _send;
    readonly IOcrClipboard _clipboard;
    readonly Action<string> _log;
    readonly Func<double> _nowMs;
    readonly bool _test;
    readonly string? _expect;
    Task<OcrState>? _asking;

    /// <param name="expect">--test only: MANTLE_SHOT_TEST_EXPECT, the text the test drew (scored, never logged).</param>
    public OcrService(Sender send, IOcrClipboard clipboard, Action<string> log, Func<double> nowMs, bool test = false, string? expect = null)
    {
        _send = send; _clipboard = clipboard; _log = log; _nowMs = nowMs; _test = test; _expect = test ? expect : null;
    }

    /// <summary>The app's transport (lane r11-app-wiring): the platform's own call, which never throws (a throw anyway is
    /// caught in <see cref="Ask"/> and <see cref="CopyTextAsync"/>).</summary>
    public static Sender Via(IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return (verb, args, timeout) => platform.CallAsync(verb, args, timeout);
    }

    /// <summary>screenshot.js's <c>ctx.sidecar.onReady(... checkOcr())</c>: ocr.status at every sidecar ready (and now, when
    /// it is ready already). The check logs its own answer once (ready, or unavailable with the reason). Dispose to stop.</summary>
    public IDisposable AskOnReady(IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return platform.OnReady(() => _ = CheckAsync());
    }

    public OcrState State { get; private set; } = OcrState.NotChecked;
    public double AskedAtMs { get; private set; }
    /// --test: the last Copy text's dry run (null after a real run or before any).
    public OcrDryRun? LastDryRun { get; private set; }

    /// Raised when availability flips; the open overlays push the new <see cref="ForPage"/> (screenshot.ocr).
    public event Action<OcrForPage>? Changed;

    public OcrForPage ForPage() =>
        new(State.Available, State.Available ? null : OcrWords.PlainError(State.Why), State.Lang);

    /// <summary>Ask again? Only while unavailable for a reason that can pass (never "built without WinRT"), at most every 30 s.</summary>
    public static bool ShouldRecheck(OcrState? s, double lastAtMs, double nowMs) =>
        s != null && !s.Available && !(s.Why ?? "").Contains("built without WinRT", StringComparison.OrdinalIgnoreCase)
        && nowMs - lastAtMs >= RecheckMs;

    /// <summary>At a capture: asks again when <see cref="ShouldRecheck"/> says so, not awaited. True when it asked.</summary>
    public bool RecheckAtCapture()
    {
        if (!ShouldRecheck(State, AskedAtMs, _nowMs())) return false;
        _ = CheckAsync();
        return true;
    }

    /// <summary>The state an ocr.status reply means (pure).</summary>
    public static OcrState FromStatus(PlatformReply r)
    {
        if (!r.Ok) return new OcrState(false, OcrWords.MissingCommand(r.Error) ? OcrWords.OlderSidecar : OcrWords.CouldNotAsk(r.Error), null);
        var o = r.Result;
        if (!(Bool(o, "available") ?? false)) {
            string? why = Str(o, "why");
            return new OcrState(false, string.IsNullOrEmpty(why) ? OcrWords.NoReasonGiven : why, null);
        }
        return new OcrState(true, null, Str(o, "lang"));
    }

    /// <summary>ocr.status (8 s), one at a time: a second call while one is in flight shares it.</summary>
    public Task<OcrState> CheckAsync()
    {
        if (_asking != null) return _asking;
        var t = Ask();
        if (!t.IsCompleted) _asking = t;   // a reply that came back at once must not be cached as "in flight"
        return t;
    }

    async Task<OcrState> Ask()
    {
        try {
            AskedAtMs = _nowMs();
            PlatformReply r;
            try { r = await _send("ocr.status", new JsonObject(), StatusTimeout); }
            catch (Exception ex) { r = PlatformReply.Fail(PlatformOutcome.Failed, string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message); }
            bool was = State.Available;
            State = FromStatus(r);
            if (State.Available != was || !was)
                _log(State.Available
                    ? "Copy text: Windows' OCR is ready (" + State.Lang + "; installed: " + string.Join(", ", Langs(r)) + ")"
                    : "Copy text is unavailable: " + OcrWords.NoPaths(State.Why));
            if (State.Available != was) Changed?.Invoke(ForPage());
            return State;
        } finally { _asking = null; }
    }

    /// <summary>The shot.ocr reply's lines, in reading order, boxes in crop px.</summary>
    public static IReadOnlyList<OcrLine> Lines(JsonElement rec)
    {
        var o = new List<OcrLine>();
        if (rec.ValueKind != JsonValueKind.Object || !rec.TryGetProperty("lines", out var ls) || ls.ValueKind != JsonValueKind.Array) return o;
        foreach (var l in ls.EnumerateArray()) {
            if (l.ValueKind == JsonValueKind.String) { o.Add(new OcrLine(l.GetString())); continue; }
            if (l.ValueKind != JsonValueKind.Object) continue;
            o.Add(new OcrLine(Str(l, "text"), Num(l, "x"), Num(l, "y"), Num(l, "w"), Num(l, "h")));
        }
        return o;
    }

    /// <summary>
    /// A committed Copy text drag: shot.ocr over the SAME frozen frame (grab), 20 s backstop; <paramref name="release"/>
    /// frees the frame after the answer whatever it was; the text goes on the clipboard only when something was read.
    /// <paramref name="via"/> is how the drag was committed (log only).
    /// </summary>
    public async Task<OcrCard> CopyTextAsync(int seq, int grab, OcrRect rect, string via, Func<ValueTask> release)
    {
        double t0 = _nowMs();
        LastDryRun = null;
        OcrShaped? shaped = null;
        string? textError = null;
        JsonElement rec = default;
        bool haveRec = false;
        if (!State.Available) {
            textError = State.Why ?? OcrWords.NoReason;
        } else {
            PlatformReply r;
            var args = new JsonObject { ["grab"] = grab, ["x"] = rect.X, ["y"] = rect.Y, ["w"] = rect.W, ["h"] = rect.H };
            try { r = await _send("shot.ocr", args, OcrTimeout); }
            catch (Exception ex) { r = PlatformReply.Fail(PlatformOutcome.Failed, string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message); }
            if (r.Ok) { rec = r.Result; haveRec = true; shaped = OcrText.Shape(Lines(rec)); }
            else {
                string e = string.IsNullOrWhiteSpace(r.Error) ? OcrWords.NoReason : r.Error!;
                textError = e.StartsWith("shot.ocr: ", StringComparison.Ordinal) ? e.Substring("shot.ocr: ".Length) : e;
            }
        }
        try { await release(); }
        catch (Exception ex) { _log("#" + seq.ToString(CultureInfo.InvariantCulture) + ": the frame could not be released (" + OcrWords.NoPaths(ex.Message) + ")"); }

        bool copied = false;
        string? copyError = null;
        if (shaped is { Lines: > 0 }) {
            try {
                if (_test) {
                    string got = shaped.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                    LastDryRun = new OcrDryRun(shaped.Lines, shaped.Chars,
                        _expect == null ? null : OcrText.JsRound(OcrText.Similarity(got, _expect) * 1000) / 1000.0,
                        _expect == null ? null : string.Equals(got, _expect, StringComparison.Ordinal));
                } else {
                    await _clipboard.WriteTextAsync(shaped.Text);
                }
                copied = true;
            } catch (Exception ex) { copyError = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message; }
        }
        double ms = _nowMs() - t0;
        string size = rect.W.ToString(CultureInfo.InvariantCulture) + "x" + rect.H.ToString(CultureInfo.InvariantCulture) + ", " + via;
        string how = haveRec
            ? "ocr " + OcrText.JsRound(Num(Obj(rec, "ms"), "ocr") ?? 0).ToString(CultureInfo.InvariantCulture) + " ms at "
              + (Num(rec, "scale") ?? 0).ToString(CultureInfo.InvariantCulture) + "x, " + Str(rec, "lang") + "; " + size
            : size;
        _log("#" + seq.ToString(CultureInfo.InvariantCulture) + ": "
            + (textError != null ? "text NOT read (" + OcrWords.NoPaths(textError) + ")" : OcrText.LogLine(shaped, ms, how)) + "; "
            + (copied ? "copied" : copyError != null ? "NOT copied (" + OcrWords.NoPaths(copyError) + ")" : "nothing copied"));
        bool any = shaped is { Lines: > 0 };
        return new OcrCard(copied, copyError == null ? null : OcrWords.PlainError(copyError), textError == null ? null : OcrWords.PlainError(textError),
            textError != null ? OcrWords.CouldNotRead : OcrText.Headline(shaped),
            any ? shaped!.Lines : 0, any ? shaped!.Chars : 0, any ? OcrText.Headline(shaped) : null, any ? OcrText.Preview(shaped!.Text) : null);
    }

    static IEnumerable<string> Langs(PlatformReply r)
    {
        if (!r.Ok || r.Result.ValueKind != JsonValueKind.Object || !r.Result.TryGetProperty("langs", out var a) || a.ValueKind != JsonValueKind.Array) yield break;
        foreach (var l in a.EnumerateArray()) if (l.ValueKind == JsonValueKind.String) yield return l.GetString()!;
    }

    static JsonElement Obj(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) ? v : default;

    static string? Str(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? Bool(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    static double? Num(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
