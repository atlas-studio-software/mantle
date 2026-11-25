using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Clipboard;

/// <summary>Which pins file the sidecar is given at <c>clip.start</c>, and why (null = pins live in memory for this run).</summary>
public readonly record struct ClipPinsChoice(string? PinsFile, string Why);

/// <summary>
/// The host side of clipboard history (lib/features/clipboard.js) that is not a window: where the pins live, the
/// clip.start request, the health words, item ids, and the list cache. The history itself (capture, dedupe, limits,
/// the DPAPI pins file) is sidecar/ClipHistory.cs, linked unchanged into Mantle.Sidecar (PLAN.md A4).
/// Nothing here ever sees or logs an item's content.
/// </summary>
public static class ClipHost
{
    /// Electron's <c>&lt;userData&gt;/features/clipboard-pins.dat</c> (DPAPI, current user), written by the sidecar.
    public const string PinsRelativePath = @"features\clipboard-pins.dat";
    /// The start-up warm-up: this long after clipboard history starts, arm + list once (CLIP-007).
    public const int WarmMs = 4000;
    /// After a copy while the panel is closed, the list is read again once copies have stopped this long (CLIP-005).
    public const int RelistMs = 400;
    /// The host waits at most this long for the page's "drawn" report before showing anyway (CLIP-006).
    public const int DrawnWaitMs = 300;

    /// <summary>
    /// The sidecar writes the pins file itself, past native's write gate, so the path is handed over ONLY where native
    /// may write it (a test or scratch profile, or the user's own profile in a --live run where native is the only
    /// shell). Anywhere else the sidecar gets no file: pins work for this run and Electron's file is never touched.
    /// </summary>
    public static ClipPinsChoice PinsFor(string dataRoot, WriteGate gate)
    {
        if (string.IsNullOrEmpty(dataRoot)) return new(null, "pins are kept for this run only: there is no profile folder");
        string file = Path.Combine(dataRoot, PinsRelativePath);
        string? no = gate.Refusal(file) ?? gate.Refusal(file + ".tmp") ?? gate.Refusal(file + ".unreadable");
        return no == null
            ? new(file, "pins are saved in " + PinsRelativePath + ", encrypted for this Windows user")
            : new(null, "pins are kept for this run only and not saved, so Electron Mantle's pins file is left alone: " + no);
    }

    /// <summary>The app's pins choice (lane r11-app-wiring): <see cref="PinsFor"/>, and when the gate's check itself throws
    /// (an "is Electron running?" probe that fails), no file, so the pins are kept for this run only, with the reason in
    /// words. Never throws.</summary>
    public static ClipPinsChoice PinsOrRunOnly(string dataRoot, WriteGate gate)
    {
        try { return PinsFor(dataRoot, gate); }
        catch (Exception e) { return new(null, "pins are kept for this run only: the pins file's place could not be checked (" + e.Message + ")"); }
    }

    /// <summary>clip.start's request: <c>{pinsFile?, listen}</c>. A test instance does not listen at all (its items are
    /// seeded), so none of the user's copies can reach a test page.</summary>
    public static JsonObject StartRequest(ClipPinsChoice pins, bool test)
    {
        var o = new JsonObject();
        if (pins.PinsFile != null) o["pinsFile"] = pins.PinsFile;
        o["listen"] = !test;
        return o;
    }

    static readonly Regex TooOld = new("unknown|not a command|no such", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What clip.start's answer means for health (null = healthy).</summary>
    public static string? StartWhy(bool ok, string? error, bool test, string? mode, string? modeError)
    {
        if (!ok) return TooOld.IsMatch(error ?? "")
            ? "the sidecar is too old for clipboard history (it has no clip.start)"
            : "clipboard history could not start: " + error;
        if (!test && mode != "history") return "not recording copies: " + (string.IsNullOrEmpty(modeError) ? "the listener is off" : modeError);
        return null;
    }

    /// <summary>The feature's health: the page's trouble first, then the sidecar's (CLIP-019).</summary>
    public static (bool Ok, string? Why) Health(string? pageWhy, string? sidecarWhy)
    {
        string? why = !string.IsNullOrEmpty(pageWhy) ? pageWhy : sidecarWhy;
        return (string.IsNullOrEmpty(why), string.IsNullOrEmpty(why) ? null : why);
    }

    static readonly Regex ItemId = new(@"^c[0-9]+$", RegexOptions.CultureInvariant);   // JavaScript's \d: ASCII digits only

    /// <summary>A page's item argument checked before it reaches the sidecar (<c>c&lt;n&gt;</c>), else "no item was chosen".</summary>
    public static string ItemArg(string? item)
    {
        if (item == null || !ItemId.IsMatch(item) || item.EndsWith('\n')) throw new ArgumentException("no item was chosen");
        return item;
    }
}

/// <summary>
/// The list cache (CLIP-005, lib/features/clipboard.js listVer/cacheVer): an open draws the cached list when nothing was
/// copied, pinned or deleted since it was read; any change makes it stale; a read that raced a change is not kept.
/// </summary>
public sealed class ClipListCache<T> where T : class
{
    int _ver, _cacheVer = -1;
    T? _cache;

    public int Version => _ver;

    /// A copy, pin, delete, clear, a new sidecar or the feature switched: the cached list is out of date from here.
    public void Invalidate() => _ver++;

    /// Before a clip.list goes out: the version it will answer for.
    public int BeginRead() => _ver;

    /// The answer came back: kept only when nothing changed meanwhile.
    public void EndRead(int version, T list)
    {
        if (version == _ver) { _cache = list; _cacheVer = version; }
    }

    /// The list an open may draw without asking the sidecar (never in a test instance).
    public T? Current(bool test) => !test && _cache != null && _cacheVer == _ver ? _cache : null;
}
