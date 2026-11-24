using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Snap;

/// <summary>A notice for the snap surface: the title and text exact, and the work area to centre it on (null = the bar's display).</summary>
public sealed record SnapNotice(string Title, string Text, SnapBox? Work);

/// <summary>What one Win+arrow did. <see cref="Rescue"/> is set when the landed frame was checked against every live monitor.</summary>
public sealed record SnapArrowResult(string Chord, string? Dir, PlatformReply? Reply, SnapNotice? Notice, SnapLanding? Rescue, string? RescueError)
{
    /// True when a stranded window was put back on a live monitor by one layouts.place.
    public bool Rescued => Rescue is { Changed: true } && RescueError == null;
}

/// <summary>The start-up probe's verdict: healthy or not, the reason, and when to ask again (null = do not).</summary>
public sealed record SnapProbe(bool Ok, string? Reason, TimeSpan? RetryAfter);

/// <summary>
/// THE WIN+ARROW MOVE PATH (lib/features/snap.js#arrow, #probe), lane r9-snap. A chord asks the sidecar to move the
/// foreground window (snap.move {dir}, 2.5 s); a refusal or an error becomes a notice in snap.js's words; nothing to do
/// shows nothing. NEW (the never-strand rule, SnapSafety): every frame the sidecar reports it landed is checked; a title
/// bar off every live monitor's work area is put back with ONE layouts.place onto the monitor holding most of it, and a
/// put-back that fails is said in a notice, never silent. The geometry is the sidecar's (PLAN A4); no Win32 here.
/// </summary>
public sealed class SnapCore
{
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromMilliseconds(2500);      // snap.js arrow
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(3000);     // snap.js probe
    public static readonly TimeSpan MonitorsTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan PlaceTimeout = TimeSpan.FromMilliseconds(6000);     // layouts.place, as lib/features/layouts.js
    public static readonly int[] ProbeRetryMs = { 2000, 5000, 15000, 30000 };           // snap.js PROBE_RETRY_MS

    public const string Older = "Mantle's window helper is an older version that cannot move windows (built before Snap.cs): restart Mantle to update it";
    public const string Busy = "waiting for Mantle's window helper to answer (it is busy)";
    public const string Silent = "Mantle's window helper did not answer, so snapping may not work: restart Mantle if Win+arrows do nothing";
    public const string TestTitle = "Nothing to snap in a test run";
    public const string TestText = "A --test instance only moves its own test windows, never yours.";
    public const string ErrorTitle = "Could not snap this window";

    public static readonly IReadOnlyDictionary<string, string> Dirs = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Win+Left"] = "left", ["Win+Right"] = "right", ["Win+Up"] = "up", ["Win+Down"] = "down",
    };

    public delegate ValueTask<PlatformReply> Sender(string verb, JsonObject args, TimeSpan timeout);

    readonly Sender _send;
    readonly Action<string> _log;
    readonly Action<SnapNotice> _notice;
    readonly bool _test;

    public SnapCore(Sender send, Action<string> log, Action<SnapNotice> notice, bool test)
    {
        _send = send; _log = log; _notice = notice; _test = test;
    }

    /// <summary>The probe's answer: ok; "unknown command" = an older sidecar (down, no retry); else busy and retried at 2, 5, 15, 30 s, then down.</summary>
    public static SnapProbe Probe(PlatformReply r, int attempt)
    {
        if (r.Ok) return new SnapProbe(true, null, null);
        if ((r.Error ?? "").Contains("unknown command", StringComparison.Ordinal)) return new SnapProbe(false, Older, null);
        if (attempt >= 0 && attempt < ProbeRetryMs.Length) return new SnapProbe(false, Busy, TimeSpan.FromMilliseconds(ProbeRetryMs[attempt]));
        return new SnapProbe(false, Silent, null);
    }

    static string R1(double ms) => Math.Round(ms, 1).ToString(CultureInfo.InvariantCulture);

    /// A JSON value as JavaScript's template literal prints it (a missing one is "undefined").
    static string Js(JsonElement x, string key)
    {
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty(key, out var v)) return "undefined";
        return v.ValueKind switch {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => v.GetRawText(),
        };
    }

    static bool Truthy(JsonElement x, string key)
    {
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty(key, out var v)) return false;
        return v.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0 && !double.IsNaN(d),
            JsonValueKind.String => (v.GetString() ?? "").Length > 0,
            JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };
    }

    static int Int(JsonElement x, string key, int fallback = 0) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    /// <summary>An {x,y,w,h} answer rect (SnapRect.ToJson) as a box; null when missing.</summary>
    public static SnapBox? Box(JsonElement x, string key)
    {
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty(key, out var r) || r.ValueKind != JsonValueKind.Object) return null;
        foreach (string k in new[] { "x", "y", "w", "h" })
            if (!r.TryGetProperty(k, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out _)) return null;
        return SnapBox.Xywh(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());
    }

    /// <summary>A monitor as SnapMonitor.ToJson writes it ({id, bounds, work, dpi, primary}); null when it has no bounds.</summary>
    public static SnapScreen? Screen(JsonElement m)
    {
        SnapBox? bounds = Box(m, "bounds");
        if (bounds == null) return null;
        SnapBox work = Box(m, "work") ?? bounds.Value;
        bool primary = m.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
        return new SnapScreen(SnapWords.Str(m, "id") ?? "", bounds.Value, work, Int(m, "dpi", 96), primary);
    }

    /// <summary>snap.monitors' answer {monitors:[..]} as screens (empty when unreadable).</summary>
    public static List<SnapScreen> Screens(JsonElement result)
    {
        var list = new List<SnapScreen>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("monitors", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray()) if (Screen(m) is { } s) list.Add(s);
        return list;
    }

    SnapNotice Notice(string title, string text, SnapBox? work, IEnumerable<string?>? names)
    {
        _log($"notice: {SnapWords.Unname(title, names)}: {SnapWords.Unname(text, names)}");
        var n = new SnapNotice(title, text, work);
        _notice(n);
        return n;
    }

    /// <summary>
    /// One Win+arrow. <paramref name="target"/> is the window a picker or Snap Assist is for (its hwnd); null = the
    /// foreground window. A --test run with no target moves nothing and says so.
    /// </summary>
    public async ValueTask<SnapArrowResult> ArrowAsync(string chord, string? target = null)
    {
        if (!Dirs.TryGetValue(chord, out string? dir)) return new SnapArrowResult(chord, null, null, null, null, null);
        var sw = Stopwatch.StartNew();
        if (_test && target == null)
            return new SnapArrowResult(chord, dir, null, Notice(TestTitle, TestText, null, null), null, null);

        var args = new JsonObject { ["dir"] = dir };
        if (target != null) args["hwnd"] = target;
        PlatformReply r = await _send("snap.move", args, MoveTimeout).ConfigureAwait(true);
        if (!r.Ok) {
            _log($"{chord}: {r.Error} ({R1(sw.Elapsed.TotalMilliseconds)} ms)");
            return new SnapArrowResult(chord, dir, r, Notice(ErrorTitle, SnapWords.Cap(r.Error), null, null), null, null);
        }

        JsonElement x = r.Result;
        bool ok = Truthy(x, "ok");
        string timing = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("timing", out var t) && t.ValueKind == JsonValueKind.Object
            ? $" [checks {Js(t, "check")}, move {Js(t, "move")}]" : "";
        _log($"{chord}: a window {Js(x, "from")} -> {Js(x, "to")} ({Js(x, "action")}{(ok ? "" : ", refused")}) sidecar {Js(x, "ms")} ms" +
             $"{timing}, key to done {R1(sw.Elapsed.TotalMilliseconds)} ms, {Int(x, "moves")} move(s){(Truthy(x, "corrected") ? " (corrected once)" : "")}");

        string[] names = SnapWords.NamesOf(x);
        SnapScreen? mon = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("monitor", out var mj) ? Screen(mj) : null;
        SnapBox? work = mon?.Work;
        if (!ok)
            return new SnapArrowResult(chord, dir, r, Notice(SnapWords.RefusedTitle(SnapWords.Str(x, "action"), SnapWords.AppOf(x)), SnapWords.Why(x), work, names), null, null);
        if (x.TryGetProperty("fit", out var fit) && fit.ValueKind == JsonValueKind.False)
            return new SnapArrowResult(chord, dir, r, Notice(SnapWords.NoFitTitle(SnapWords.AppOf(x), SnapWords.Str(x, "to")), SnapWords.Cap(SnapWords.Str(x, "fitReason")), work, names), null, null);

        var (landing, err) = await GuardAsync(chord, x, mon, names).ConfigureAwait(true);
        SnapNotice? fail = null;
        if (err != null)
            fail = Notice("Could not put " + SnapWords.AppOf(x) + " back on the screen", SnapWords.Cap(err), landing?.Screen?.Work ?? work, names);
        return new SnapArrowResult(chord, dir, r, fail, landing, err);
    }

    /// <summary>
    /// The never-strand check on a landed frame. Fast path: on the monitor the move was for, no extra call. Else every
    /// live monitor is read (snap.monitors; when that fails, the move's own monitor is all there is) and
    /// <see cref="SnapSafety.Clamp"/> decides; a stranded window gets ONE layouts.place with the safe frame.
    /// </summary>
    async ValueTask<(SnapLanding? Landing, string? Error)> GuardAsync(string chord, JsonElement x, SnapScreen? mon, string[] names)
    {
        SnapBox? landed = Box(x, "frame");
        if (landed == null) return (null, null);            // nothing moved by rect: maximize, minimize, restore, none
        if (mon != null && SnapSafety.TitleBarOn(landed.Value, mon)) return (null, null);

        var live = new List<SnapScreen>();
        PlatformReply m = await _send("snap.monitors", new JsonObject(), MonitorsTimeout).ConfigureAwait(true);
        if (m.Ok) live = Screens(m.Result);
        if (live.Count == 0) {
            _log($"{chord}: could not read the monitors to check where the window landed ({SnapWords.Unname(m.Error ?? "no monitor listed", names)}); judged on the monitor it was moved to");
            if (mon != null) live.Add(mon);
        }
        SnapLanding landing = SnapSafety.Clamp(landed.Value, live);
        if (!landing.Changed) return (landing, null);

        string? hwnd = SnapWords.Str(x, "hwnd");
        if (string.IsNullOrEmpty(hwnd)) {
            _log($"{chord}: the window landed at {landed.Value} with its title bar off every live monitor, and the answer has no window handle to put it back");
            return (landing, "the window's handle was not in the answer");
        }
        var place = new JsonObject {
            ["hwnd"] = hwnd, ["state"] = "normal",
            ["frame"] = new JsonObject { ["x"] = landing.Frame.Left, ["y"] = landing.Frame.Top, ["w"] = landing.Frame.Width, ["h"] = landing.Frame.Height },
        };
        int pid = Int(x, "pid");
        if (pid != 0) place["pid"] = pid;
        PlatformReply p = await _send("layouts.place", place, PlaceTimeout).ConfigureAwait(true);
        bool placed = p.Ok && !(p.Result.ValueKind == JsonValueKind.Object && p.Result.TryGetProperty("placed", out var pl) && pl.ValueKind == JsonValueKind.False);
        if (placed) {
            _log($"{chord}: the window landed at {landed.Value} ({landing.Why}); put back at {landing.Frame}");
            return (landing, null);
        }
        string why = p.Ok ? SnapWords.Str(p.Result, "reason") ?? "Windows did not move it" : p.Error ?? "no answer";
        _log($"{chord}: the window landed at {landed.Value} with its title bar off every live monitor and could not be put back: {SnapWords.Unname(why, names)}");
        return (landing, why);
    }
}
