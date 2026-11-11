using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Corners;

// HOT CORNERS, the pure part: lib/features/_corners.js, value for value (lane native-r7-desktop).
//
// Push the cursor into a screen corner and its action runs. The cursor has to REST in a CornerZoneDip square for
// CornerDwellMs; then it fires ONCE and re-arms only after the cursor has left the square. A mouse button held in the
// corner, or pressed there during the dwell, disarms it until the cursor leaves. A new watch starts DISARMED.
// Geometry is DIP everywhere (Electron's cursor point and display bounds); the feature converts physical pixels per
// monitor before it calls in here. Nothing in this file reads the screen, the clock or the mouse.

/// <summary>A point in DIP (desktop-independent pixels, virtual-screen origin).</summary>
public readonly record struct DipPoint(double X, double Y)
{
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>A display's bounds in DIP.</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height);

/// <summary>One read of the mouse buttons (GetAsyncKeyState): <paramref name="Held"/> = down now,
/// <paramref name="Pressed"/> = went down since the last read; <paramref name="Ok"/> false = could not read (why).</summary>
public readonly record struct ButtonRead(bool Ok, bool Held = false, bool Pressed = false, string? Reason = null);

/// <summary>What each corner does: an action id per corner (<see cref="CornerRules.Actions"/>).</summary>
public sealed record CornerTable(string Tl, string Tr, string Bl, string Br)
{
    public string Of(string corner) => corner switch { "tl" => Tl, "tr" => Tr, "bl" => Bl, "br" => Br, _ => "off" };
    public CornerTable With(string corner, string action) => corner switch {
        "tl" => this with { Tl = action }, "tr" => this with { Tr = action },
        "bl" => this with { Bl = action }, "br" => this with { Br = action }, _ => this };
    public IEnumerable<(string Corner, string Action)> Each() { yield return ("tl", Tl); yield return ("tr", Tr); yield return ("bl", Bl); yield return ("br", Br); }
}

/// <summary>The watch's state between polls. Phases: idle, dwell, fired, disarmed.</summary>
public sealed record CornerState(string Phase, string? Corner, long? Since, string? Why);

/// <summary>One poll's input (step()'s <c>i</c>).</summary>
public sealed record CornerInput
{
    public long Now { get; init; }
    /// The cursor in DIP, or null when it could not be read (<see cref="CursorWhy"/> says why).
    public DipPoint? Pt { get; init; }
    public string? CursorWhy { get; init; }
    /// The display the cursor is read against when <see cref="Displays"/> is empty (the primary).
    public DipRect Bounds { get; init; }
    /// Every display (DIP); when given, only OUTER corners on any display are hot.
    public IReadOnlyList<DipRect>? Displays { get; init; }
    public required CornerTable Actions { get; init; }
    /// A reason nothing may fire right now (the last action still running), or null.
    public string? Guard { get; init; }
    /// Called ONLY while the cursor is in an active corner square. Null = there is no reader.
    public Func<ButtonRead>? Buttons { get; init; }
}

/// <summary>One poll's answer: <paramref name="Act"/> is "fire" or null; <paramref name="Blocked"/> says why nothing could fire.</summary>
public sealed record CornerStep(CornerState St, string? Act, string? Corner, string? Action, string? Why, string? Blocked);

public static class CornerRules
{
    /// 6 DIP square: holds the bottom edge-clip row (H-3 px) at every scale with 3 DIP to spare.
    public const double CornerZoneDip = 6;
    /// 250 ms at rest in the square, measured from the first poll that saw it there (real wait 250-350 ms).
    public const int CornerDwellMs = 250;
    /// One cursor read every 100 ms; none while every corner is Off, the feature is off, or Mantle rests.
    public const int CornerPollMs = 100;
    /// The display list is re-read at most this often (no display listener).
    public const int DisplaysMs = 2000;

    public static readonly IReadOnlyList<string> Corners = new[] { "tl", "tr", "bl", "br" };
    public static readonly IReadOnlyDictionary<string, string> CornerNames = new Dictionary<string, string> {
        ["tl"] = "top-left", ["tr"] = "top-right", ["bl"] = "bottom-left", ["br"] = "bottom-right" };
    public static readonly IReadOnlyList<string> Actions = new[] { "off", "taskview", "desktop", "launcher", "qs", "lock" };
    public static readonly IReadOnlyDictionary<string, string> ActionNames = new Dictionary<string, string> {
        ["off"] = "Off", ["taskview"] = "Task view", ["desktop"] = "Show desktop", ["launcher"] = "Launcher",
        ["qs"] = "Quick settings", ["lock"] = "Lock screen" };
    /// Top-left = Task view, bottom-right = Show desktop (the hidden taskbar's sliver), the other two Off.
    public static readonly CornerTable Defaults = new("taskview", "off", "off", "desktop");
    /// The lock and sign-in screens: nothing fires over them.
    public static readonly IReadOnlyList<string> LockExes = new[] { "lockapp.exe", "logonui.exe" };

    public static string NameOf(string corner) => CornerNames.TryGetValue(corner, out var n) ? n : corner;
    public static string ActionName(string action) => ActionNames.TryGetValue(action, out var n) ? n : action;

    /// A saved table -> a complete, valid one: unknown corners dropped, an unknown action -> that corner's default.
    public static CornerTable Normalize(IReadOnlyDictionary<string, string?>? saved)
    {
        string Pick(string c) => saved != null && saved.TryGetValue(c, out var a) && a != null && Actions.Contains(a) ? a : Defaults.Of(c);
        return new CornerTable(Pick("tl"), Pick("tr"), Pick("bl"), Pick("br"));
    }

    /// Is any corner set to something?
    public static bool AnyOn(CornerTable? t) => t != null && t.Each().Any(e => e.Action != "off" && e.Action.Length > 0);

    /// Which corner square of display <paramref name="b"/> the point is in, or null. Far sides compared with &lt;=
    /// (175%: 1920 px = 1097.1 DIP, the last pixel can round to x = width and is still this display's corner).
    public static string? CornerAt(DipPoint? pt, DipRect b, double zone = CornerZoneDip)
    {
        if (pt is not { } p || !p.Finite) return null;
        if (p.X < b.X || p.X > b.X + b.Width || p.Y < b.Y || p.Y > b.Y + b.Height) return null;
        bool left = p.X < b.X + zone, right = p.X >= b.X + b.Width - zone;
        bool top = p.Y < b.Y + zone, bottom = p.Y >= b.Y + b.Height - zone;
        if (top && left) return "tl";
        if (top && right) return "tr";
        if (bottom && left) return "bl";
        if (bottom && right) return "br";
        return null;
    }

    static bool OnAny(DipPoint p, IReadOnlyList<DipRect> displays, int skip)
    {
        for (int i = 0; i < displays.Count; i++) {
            if (i == skip) continue;
            var d = displays[i];
            if (p.X >= d.X && p.X < d.X + d.Width && p.Y >= d.Y && p.Y < d.Y + d.Height) return true;
        }
        return false;
    }

    /// The OUTER corner square the point is in, on whichever display holds it, or null (macOS's rule: an inner
    /// corner, one with another display beyond it on either axis, is where the cursor crosses, not where it stops).
    public static string? CornerOnScreens(DipPoint? pt, IReadOnlyList<DipRect>? displays, double zone = CornerZoneDip)
    {
        if (displays == null || displays.Count == 0) return null;
        for (int i = 0; i < displays.Count; i++) {
            var b = displays[i];
            string? c = CornerAt(pt, b, zone);
            if (c == null) continue;
            double L = b.X, R = b.X + b.Width - 1, T = b.Y, Bt = b.Y + b.Height - 1;
            double x = c[1] == 'l' ? L : R, y = c[0] == 't' ? T : Bt;
            var across = new DipPoint(c[1] == 'l' ? L - 1 : R + 1, y);
            var beyond = new DipPoint(x, c[0] == 't' ? T - 1 : Bt + 1);
            return OnAny(across, displays, i) || OnAny(beyond, displays, i) ? null : c;
        }
        return null;
    }

    /// Every display when the caller has them, else the one display.
    public static string? CornerIn(DipPoint? pt, CornerInput i) =>
        i.Displays is { Count: > 0 } ds ? CornerOnScreens(pt, ds) : CornerAt(pt, i.Bounds);

    public static CornerState Initial() => new("idle", null, null, null);

    /// A new watch: disarmed until the cursor is outside every corner.
    public static CornerState StartState() => new("disarmed", null, null,
        "the corners were just switched on (or Mantle started) with the cursor in a corner; it has to leave first");

    /// One poll (step()). idle -> dwell -> fired; disarmed = something was against it while the cursor was in the
    /// corner. Fired and disarmed end ONLY when the cursor leaves the corner (or its corner is switched Off).
    public static CornerStep Step(CornerState? st, CornerInput i)
    {
        var s = st ?? Initial();
        string phase = s.Phase; string? corner = s.Corner; long? since = s.Since; string? why = s.Why;
        void Reset(string ph = "idle", string? w = null, string? c = null) { phase = ph; corner = c; since = null; why = w; }
        CornerStep Out(string? act = null, string? said = null, string? blocked = null)
        {
            var ns = new CornerState(phase, corner, since, why);
            return new CornerStep(ns, act, act != null ? corner : null, act != null && corner != null ? i.Actions.Of(corner) : null, said, blocked);
        }

        if (i.Pt is not { } pt) {
            if (phase == "dwell") Reset();
            return Out(null, null, $"the cursor could not be read ({(string.IsNullOrEmpty(i.CursorWhy) ? "no reason" : i.CursorWhy)})");
        }
        string? c = CornerIn(pt, i);
        bool on = c != null && i.Actions.Of(c) is { Length: > 0 } a && a != "off";
        if (!on) { Reset(); return Out(); }                       // outside every active corner: armed again
        if (corner != null && corner != c) Reset();                // another corner
        if (phase == "fired") return Out();                        // once per arrival
        if (phase == "disarmed") { corner = c; return Out(null, null, why); }
        string where = NameOf(c!);
        if (!string.IsNullOrEmpty(i.Guard)) { Reset("disarmed", i.Guard, c); return Out(null, null, why); }
        ButtonRead b;
        try { b = i.Buttons != null ? i.Buttons() : new ButtonRead(false, Reason: "there is no mouse button reader"); }
        catch (Exception e) { b = new ButtonRead(false, Reason: "the reader threw: " + e.Message); }
        if (!b.Ok) {
            Reset();
            return Out(null, null, $"the mouse buttons cannot be read ({(string.IsNullOrEmpty(b.Reason) ? "no reason" : b.Reason)}), so a corner could fire in the middle of a drag");
        }
        if (b.Held || (phase == "dwell" && b.Pressed)) {
            Reset("disarmed", $"a mouse button went down in the {where} corner (a click, or a window dragged there); the cursor has to leave the corner first", c);
            return Out(null, null, why);
        }
        if (phase != "dwell") { phase = "dwell"; corner = c; since = i.Now; return Out(); }
        long rested = i.Now - (since ?? i.Now);
        if (rested >= CornerDwellMs) {
            phase = "fired"; since = i.Now;
            return Out("fire", $"the cursor rested in the {where} corner for {rested} ms");
        }
        return Out();
    }

    /// The reason a corner must not fire, from the sidecar's gamemode.scan {procs:true} answer read at the moment of
    /// firing, or null. <paramref name="games"/> is the game plan's check (running anywhere in the user's session, or
    /// in front even windowed): it answers the refusal words or null. A scan that did not come back refuses.
    public static string? FireBlocker(CornerScan? scan, Func<CornerScan, string?>? games = null)
    {
        if (scan == null || !scan.Ok) return $"the window in front could not be checked for a game or full screen ({(string.IsNullOrEmpty(scan?.Reason) ? "no answer" : scan!.Reason)}), so nothing fired";
        string exe = scan.FgExe is { Length: > 0 } fe ? fe.Split('\\', '/').Last() : "";
        if (exe.Length > 0 && LockExes.Contains(exe.ToLowerInvariant())) return $"{exe} is in front (the lock or sign-in screen)";
        if (games?.Invoke(scan) is { Length: > 0 } g) return g;
        if (scan.FgFullscreen) return $"{(exe.Length > 0 ? exe : !string.IsNullOrEmpty(scan.FgTitle) ? scan.FgTitle : "a window")} is full screen in front";
        return null;
    }
}

/// <summary>The parts of a gamemode.scan {procs:true} answer the fire-time check reads.</summary>
/// <param name="Procs">(file, session) of every process the scan listed.</param>
public sealed record CornerScan(bool Ok, string? Reason, string? FgExe, string? FgTitle, bool FgFullscreen,
    int? OwnSession = null, IReadOnlyList<(string File, int Session)>? Procs = null);
