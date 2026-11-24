using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Data;

namespace Mantle.Core.Look;

/// <summary>One wallpaper the desktop shows: the look-sync record's <c>id</c>, <c>type</c>, <c>title</c> and <c>file</c>.</summary>
public sealed record DesktopPick(string Id, string Type, string Title, string? File);

/// <summary>What the desktop shows now: a wallpaper, or none and why.</summary>
public sealed record DesktopNow(DesktopPick? Pick, string? Reason)
{
    public static DesktopNow None(string reason) => new(null, reason);
}

/// <summary>A still of the wallpaper: JPEG bytes (quality 90, at most the cover size of the primary screen) and its size, or why not.</summary>
public sealed record DesktopShot(bool Ok, byte[]? Jpeg, int W, int H, string? Reason)
{
    public static DesktopShot Fail(string reason) => new(false, null, 0, 0, reason);
}

/// <summary>A still as the desktop shows it on the primary monitor: cover-fit, physical pixels, BGRA, and the monitor's scale.</summary>
public sealed record DesktopPicture(byte[] Bgra, int W, int H, double Scale);

/// <summary>
/// Where the publisher gets the wallpaper from (Mantle.App: Windows' own wallpaper; tests: a fake). A call that throws
/// is caught and its message becomes the record's reason.
/// </summary>
public interface IDesktopSource
{
    /// The wallpaper now (cheap: it runs on every check).
    DesktopNow Current();
    /// A JPEG still of <paramref name="pick"/>, or why there is none.
    DesktopShot Still(DesktopPick pick);
    /// <paramref name="stillFile"/> decoded for the guard (cover-fit to the primary monitor). Throws with the reason.
    DesktopPicture Picture(string stillFile);
}

/// <summary>A check's outcome, for the log and the tests: published, republished (same still, new guard), adopted, same, failed.</summary>
public sealed record DesktopOutcome(string What, double? Rev, string? Reason, string? Still);

/// <summary>How the publisher is made.</summary>
public sealed record DesktopPublisherOptions
{
    /// Mantle's look-sync instance: <see cref="LookSync.PublishDesktopAsync"/> is the record's writer (re-read, rev + 1,
    /// tmp + rename, retried), and its <see cref="LookSync.Dir"/> (DataPaths.LookSyncDir, so LOOK_SYNC_DIR) holds the stills.
    public required LookSync Sync { get; init; }
    /// The ONLY way the publisher changes a file (the stills and their pruning); its gate decides.
    public required SafeFile Files { get; init; }
    public required IDesktopSource Source { get; init; }
    /// The glass the guard is worked out for, look-sync's <c>{dark, light, render}</c> (the one Mantle shows).
    public Func<JsonNode?>? Glass { get; init; }
    public Action<string>? Log { get; init; }
    /// The still's rename is retried after each delay on EBUSY / EPERM / EACCES (look-sync's 50 / 100 / 200 ms).
    public int[] RetryDelays { get; init; } = { 50, 100, 200 };
    /// The pid in the still's tmp name (tests fix it).
    public int? Pid { get; init; }
}

/// <summary>
/// lib/look-desktop.js's publisher, the part native needs (lane r10-look; CONTRACT.md section 2 "Shared store", section 4,
/// the user's 2026-10-10 answers, "desktop.guard", "Light look" and "2026-11-11"): only Mantle writes look.json <c>desktop</c>, and it writes
/// it even while look sync is OFF. On each wallpaper change it writes the still <c>stills\wall-&lt;rev&gt;.jpg</c> FIRST (tmp +
/// rename), then the record through the shared module's writer, then keeps the last 3 stills. The record is the contract's
/// shape: <c>{id, type, title, file, ok, reason, still, stillSize, guard{dark, light} | guardReason, guardLightReason?, rev, by}</c>,
/// each guard entry <see cref="Entry"/> over the contract's 100 DIP top band; no other key. A change of the glass's dim or blur
/// republishes the same still with a new guard and a new rev. The same wallpaper again publishes nothing. At the first check
/// a record from Mantle's last run that still describes the wallpaper is adopted, not republished.
/// One check runs at a time (at most one waits). Nothing here throws: every failure is a reason in the log, said once.
/// </summary>
public sealed class DesktopPublisher
{
    public const int Keep = 3;
    /// The contract's band (CONTRACT "desktop.guard": the top 100 DIP of the primary monitor). Native's own windows use
    /// NativeGuard.BandMinDip (140) for themselves; the published entry stays at the contract's 100.
    public const double BandDip = 100;
    public const string StillsFolder = "stills";
    static readonly Regex StillName = new(@"^wall-(\d+)\.jpg$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly DesktopPublisherOptions _o;
    readonly int _pid;
    readonly object _lock = new();
    readonly HashSet<string> _said = new(StringComparer.Ordinal);
    Task _queue = Task.CompletedTask;
    Task<DesktopOutcome>? _waiting;
    int _waitingGen, _gen;
    bool _started;
    string? _lastKey;
    string? _lastGuardKey;
    JsonObject? _lastRec;

    public DesktopPublisher(DesktopPublisherOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        if (o.Sync == null || o.Files == null || o.Source == null) throw new ArgumentException("desktop publisher: Sync, Files and Source are required");
        _pid = o.Pid ?? Environment.ProcessId;
        StillsDir = Path.Combine(o.Sync.Dir, StillsFolder);
    }

    /// <c>&lt;look-sync folder&gt;\stills</c>.
    public string StillsDir { get; }

    /// Look at the wallpaper and the glass now; publish when either moved. Queued behind a running check (at most one waits,
    /// and a later ask joins it). Never throws.
    public Task<DesktopOutcome> CheckAsync(string why)
    {
        lock (_lock) {
            if (_waiting != null && _waitingGen == _gen) return _waiting;
            int g = _gen;
            var t = _queue.ContinueWith(_ => { lock (_lock) _gen = g + 1; return RunSafe(why); },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _waiting = t;
            _waitingGen = g;
            _queue = t.ContinueWith(_ => { }, TaskScheduler.Default);
            return t;
        }
    }

    async Task<DesktopOutcome> RunSafe(string why)
    {
        try { return await Run(why).ConfigureAwait(false); }
        catch (Exception e) {
            string r = "the desktop record check failed (" + e.GetType().Name + ": " + e.Message + ")";
            Say(r);
            return new DesktopOutcome("failed", null, r, null);
        }
    }

    async Task<DesktopOutcome> Run(string why)
    {
        var inp = GlassInputs();
        string gk = GuardKey(inp.DarkDim, inp.DarkBlur, inp.LightDim, inp.LightBlur);
        DesktopNow now;
        try { now = _o.Source.Current() ?? DesktopNow.None("the wallpaper could not be read (no answer came back)"); }
        catch (Exception e) { now = DesktopNow.None("the wallpaper could not be read (" + e.Message + ")"); }
        string key = KeyOf(now);
        // a folder this run may not write (the user's own look-sync folder outside --live as the only shell): no still is made
        string? refused;
        try { refused = _o.Files.Gate.Refusal(_o.Sync.File); } catch (Exception e) { refused = "the folder could not be checked (" + e.Message + ")"; }
        if (refused != null) {
            _lastKey = null;
            string r = "the desktop record is not written: " + refused;
            Say(r);
            return new DesktopOutcome("failed", null, r, null);
        }
        if (!_started) {
            _started = true;
            if (Adopt(now, key) is { } kept) {
                if (_lastGuardKey == gk) {
                    Line("kept the desktop record from Mantle's last run (rev " + Num(kept) + "): it still describes the wallpaper");
                    return new DesktopOutcome("adopted", kept, null, LookSyncCore.Str(_lastRec?["still"]));
                }
                Line("the desktop record from Mantle's last run (rev " + Num(kept) + ") still describes the wallpaper; its top band is worked out again for this glass");
            }
        }
        if (key == _lastKey) {
            if (gk == _lastGuardKey) return new DesktopOutcome("same", null, null, LookSyncCore.Str(_lastRec?["still"]));
            return await RepublishGuard(now, key, gk, inp, why).ConfigureAwait(false);
        }
        return await Publish(now, key, gk, inp, why).ConfigureAwait(false);
    }

    // ---- one publish ----------------------------------------------------------------------------

    async Task<DesktopOutcome> Publish(DesktopNow now, string key, string gk, GuardIn inp, string why)
    {
        if (now.Pick is not DesktopPick it) {
            string reason = now.Reason is { Length: > 0 } r0 ? r0 : "there is no wallpaper picture";
            const string gr = "there is no wallpaper picture to work it out from";
            var none = new JsonObject {
                ["ok"] = false, ["reason"] = reason, ["id"] = null, ["type"] = null, ["title"] = null, ["file"] = null,
                ["still"] = null, ["stillSize"] = null, ["guardReason"] = gr, ["guardLightReason"] = gr,
            };
            var w0 = await Write(none).ConfigureAwait(false);
            if (!w0.Ok) { _lastKey = null; _lastRec = null; Say("the desktop record could not be written: " + w0.Reason); return new DesktopOutcome("failed", null, w0.Reason, null); }
            _lastKey = key; _lastGuardKey = gk; _lastRec = Stamped(none, w0.Rev);
            Line("shared that there is no desktop picture (" + reason + "; rev " + Num(w0.Rev) + "; " + why + ")");
            Recovered();
            return new DesktopOutcome("published", w0.Rev, reason, null);
        }

        DesktopShot shot;
        try { shot = _o.Source.Still(it) ?? DesktopShot.Fail("no still came back"); }
        catch (Exception e) { shot = DesktopShot.Fail("the still could not be made (" + e.Message + ")"); }
        if (shot.Ok && shot.Jpeg is not { Length: > 0 }) shot = DesktopShot.Fail("the still came back empty");
        if (shot.Ok && !(shot.W > 0 && shot.H > 0)) shot = DesktopShot.Fail("the still has no size");
        string? stillFile = null;
        if (shot.Ok) {
            // the still FIRST, so the record never names a file that is not there
            var (file, err) = WriteStill(shot.Jpeg!);
            if (err != null) shot = DesktopShot.Fail(err);
            else stillFile = file;
        }
        var rec = new JsonObject { ["id"] = it.Id, ["type"] = it.Type, ["title"] = it.Title, ["file"] = it.File };
        if (stillFile != null) {
            rec["ok"] = true; rec["reason"] = null; rec["still"] = stillFile;
            rec["stillSize"] = new JsonArray(LookSyncCore.N(shot.W), LookSyncCore.N(shot.H));
            Guard(rec, stillFile, inp);
        } else {
            rec["ok"] = false; rec["reason"] = shot.Reason ?? "there is no still"; rec["still"] = null; rec["stillSize"] = null;
            string gr = "there is no still of \"" + it.Title + "\" to work it out from";
            rec["guardReason"] = gr; rec["guardLightReason"] = gr;
            Say("no still of \"" + it.Title + "\": " + (shot.Reason ?? "no reason came back"));
        }
        var w = await Write(rec).ConfigureAwait(false);
        if (!w.Ok) {
            _lastKey = null; _lastRec = null;   // tried again at the next change
            Say("the desktop record could not be written: " + w.Reason);
            return new DesktopOutcome("failed", null, w.Reason, stillFile);
        }
        _lastKey = key; _lastGuardKey = gk; _lastRec = Stamped(rec, w.Rev);
        if (stillFile != null) {
            Line("shared the desktop wallpaper: " + it.Type + " " + it.Id + ", still " + shot.W + "x" + shot.H + " (rev " + Num(w.Rev) + "; " + why + ")");
            Prune(stillFile);
            Recovered();
        }
        return new DesktopOutcome("published", w.Rev, stillFile == null ? LookSyncCore.Str(rec["reason"]) : null, stillFile);
    }

    /// Same wallpaper, new dim or blur: the same still, a new guard, a new rev (a still that is gone is made again).
    async Task<DesktopOutcome> RepublishGuard(DesktopNow now, string key, string gk, GuardIn inp, string why)
    {
        var prev = _lastRec;
        string? still = prev != null ? LookSyncCore.Str(prev["still"]) : null;
        if (prev == null || still == null || !File.Exists(still)) {
            _lastKey = null;
            return await Publish(now, key, gk, inp, why).ConfigureAwait(false);
        }
        var rec = Bare(prev);
        Guard(rec, still, inp);
        var w = await Write(rec).ConfigureAwait(false);
        _lastGuardKey = gk;   // a failed write waits for the next change; it does not retry in a loop
        if (!w.Ok) {
            Say("the desktop record with the new top band could not be written: " + w.Reason);
            return new DesktopOutcome("failed", null, w.Reason, still);
        }
        _lastRec = Stamped(rec, w.Rev);
        Line("republished the desktop record for the new glass (rev " + Num(w.Rev) + "; " + why + ")");
        Recovered();
        return new DesktopOutcome("republished", w.Rev, null, still);
    }

    /// The shared module's own queued check first (a theme or glass another app wrote a moment ago is applied, a damaged
    /// file gets its own recovery), then the record through its writer: re-read, rev + 1, tmp + rename, retried.
    async Task<LookWrite> Write(JsonObject rec)
    {
        try { _o.Sync.Refresh(); } catch (Exception e) { Say("the shared look could not be read before the desktop write (" + e.Message + ")"); }
        try { return await _o.Sync.PublishDesktopAsync(rec).ConfigureAwait(false); }
        catch (Exception e) { return new LookWrite(false, null, e.Message); }
    }

    /// <c>stills\wall-&lt;next rev&gt;.jpg</c> through the gate (tmp + rename, retried). The rev is the doc's + 1, as the
    /// record's will be; a missing or damaged file is re-made first, one rev more, hence 2.
    (string? File, string? Error) WriteStill(byte[] jpeg)
    {
        long next = 2;
        try {
            var r = LookSyncCore.Read(_o.Sync.Dir);
            if (r.State == "ok" && r.Doc != null && LookSyncCore.IsInt(r.Doc["rev"], out var dr) && dr >= 0) next = (long)dr + 1;
        } catch (Exception) { /* 2 */ }
        string file = Path.Combine(StillsDir, "wall-" + next.ToString(CultureInfo.InvariantCulture) + ".jpg");
        string tmp = file + "." + _pid.ToString(CultureInfo.InvariantCulture) + ".tmp";
        string? err;
        try { err = _o.Files.WriteAtomic(file, jpeg, tmp, _o.RetryDelays); }
        catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
        return err == null ? (file, null) : (null, "the still could not be saved in " + StillsDir + " (" + err + ")");
    }

    /// Only the last <see cref="Keep"/> stills stay (never <paramref name="keep"/>, the one the record names); a leftover .tmp goes.
    void Prune(string keep)
    {
        string[] names;
        try { names = Directory.GetFiles(StillsDir).Select(p => Path.GetFileName(p)).ToArray(); }
        catch (Exception) { return; }
        var stills = names.Select(n => (Name: n, Rev: StillRev(n))).Where(x => x.Rev >= 0).OrderByDescending(x => x.Rev).ToList();
        foreach (var x in stills.Skip(Keep)) {
            string f = Path.Combine(StillsDir, x.Name);
            if (SamePath(f, keep)) continue;
            string? err = _o.Files.Remove(f);
            if (err != null) Say("an old still could not be removed (" + err + ")");
        }
        foreach (var n in names)
            if (n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) _o.Files.Remove(Path.Combine(StillsDir, n));   // in use: left
    }

    /// look-desktop.js coverSize: the size that still covers a <paramref name="sw"/> x <paramref name="sh"/> screen, never bigger
    /// than the picture (cover fit, at most the screen); no screen size keeps the picture's own.
    public static (int W, int H) CoverSize(int iw, int ih, int sw, int sh)
    {
        if (!(iw > 0 && ih > 0)) return (0, 0);
        if (!(sw > 0 && sh > 0)) return (iw, ih);
        double s = Math.Min(1, Math.Max(sw / (double)iw, sh / (double)ih));
        return (Math.Max(1, (int)Math.Round(iw * s, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(ih * s, MidpointRounding.AwayFromZero)));
    }

    /// The rev in a still's name (<c>wall-12.jpg</c> -> 12), else -1.
    public static long StillRev(string name)
    {
        var m = StillName.Match(name ?? "");
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : -1;
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    // ---- the guard (desktop.guard.<theme>) ------------------------------------------------------

    readonly record struct GuardIn(double? DarkDim, double? DarkBlur, double? LightDim, double? LightBlur);

    GuardIn GlassInputs()
    {
        JsonNode? g = null;
        try { g = _o.Glass?.Invoke(); } catch (Exception e) { Say("Mantle's glass could not be read for the top band (" + e.Message + ")"); }
        static double? Num2(JsonNode? theme, string k) => theme is JsonObject o && LookSyncCore.TryNum(o[k], out var d) && double.IsFinite(d) ? d : null;
        var dark = g?["dark"]; var light = g?["light"];
        return new GuardIn(Num2(dark, "dim"), Num2(dark, "blurPx"), Num2(light, "dim"), Num2(light, "blurPx"));
    }

    static string K(double? x) => x is double d ? Math.Round(d, 2).ToString(CultureInfo.InvariantCulture) : "-";
    static string GuardKey(double? dd, double? db, double? ld, double? lb) => K(dd) + "/" + K(db) + "|" + K(ld) + "/" + K(lb) + "|" + K(BandDip);

    /// The key a stored record's guard was worked out for (its dims, blurs and band), to compare with the glass now.
    static string GuardKeyOf(JsonObject rec)
    {
        static double? F(JsonNode? n, string k) => n is JsonObject o && LookSyncCore.TryNum(o[k], out var d) ? d : null;
        var g = rec["guard"] as JsonObject;
        var dk = g?["dark"]; var lt = g?["light"];
        double? band = F(dk?["region"], "heightDip");
        string key = K(F(dk, "dim")) + "/" + K(F(dk, "blurPx")) + "|" + K(F(lt, "dim")) + "/" + K(F(lt, "blurPx")) + "|" + K(band);
        return dk == null ? "none" : key;
    }

    /// Works the guard out from the still as the desktop shows it, for the glass now; else <c>guardReason</c>.
    void Guard(JsonObject rec, string stillFile, GuardIn inp)
    {
        rec.Remove("guard"); rec.Remove("guardReason"); rec.Remove("guardLightReason");
        string? fail = null;
        DesktopPicture? pic = null;
        try { pic = _o.Source.Picture(stillFile); }
        catch (Exception e) { fail = "the still could not be read for it (" + e.Message + ")"; }
        if (fail == null && pic == null) fail = "the still could not be read for it (no picture came back)";
        if (fail == null && (inp.DarkDim is not double || inp.DarkBlur is not double)) fail = "Mantle's dark glass could not be read";
        JsonObject? dark = null;
        if (fail == null) {
            try {
                var p = pic!;
                dark = Entry(NativeGuard.Rows(p.Bgra, p.W, p.H, p.Scale, dark: true), p.Bgra, inp.DarkDim!.Value, inp.DarkBlur!.Value);
            } catch (Exception e) { fail = "it could not be worked out (" + e.Message + ")"; }
        }
        if (fail != null) {
            rec["guardReason"] = fail; rec["guardLightReason"] = fail;
            Say("the top band could not be worked out: " + fail);
            return;
        }
        var guard = new JsonObject { ["dark"] = dark };
        string? lightWhy = null;
        if (inp.LightDim is double ld && inp.LightBlur is double lb) {
            try {
                var p = pic!;
                guard["light"] = Entry(NativeGuard.Rows(p.Bgra, p.W, p.H, p.Scale, dark: false), p.Bgra, ld, lb);
            } catch (Exception e) { lightWhy = "it could not be worked out (" + e.Message + ")"; }
        } else lightWhy = "Mantle's light glass could not be read";
        rec["guard"] = guard;
        if (lightWhy != null) { rec["guardLightReason"] = lightWhy; Say("the light top band could not be worked out: " + lightWhy); }
    }

    /// <summary>One <c>desktop.guard.&lt;theme&gt;</c> entry in the contract's shape, <c>{alpha, contrast, dim, blurPx, region
    /// {topDip 0, heightDip}, monitor [w, h DIP]}</c>: <see cref="NativeGuard.Entry"/>'s rule (floored at Dim, .02 steps,
    /// ceiling max(.88, Dim), for the STORED glass) over the top <paramref name="bandDip"/> DIP, by default the contract's 100.</summary>
    public static JsonObject Entry(GuardRows rows, ReadOnlySpan<byte> bgra, double dim, double blurPx, double bandDip = BandDip)
    {
        var (need, _) = rows.NeedOver(0, bandDip);
        var (a, _) = NativeGuard.Step02(dim, need);
        var m = NativeGuard.Measure(bgra, rows.W, rows.H, rows.Scale, rows.Dark, a, 0, bandDip);
        double c = Math.Round(Math.Min(m.Box, m.Row), 2);
        return new JsonObject {
            ["alpha"] = a, ["contrast"] = c, ["dim"] = dim, ["blurPx"] = blurPx,
            ["region"] = new JsonObject { ["topDip"] = 0, ["heightDip"] = bandDip },
            ["monitor"] = new JsonArray((JsonNode)Math.Round(rows.W / rows.Scale), (JsonNode)Math.Round(rows.H / rows.Scale)),
        };
    }

    // ---- start: adopt the last run's record -----------------------------------------------------

    /// A record Mantle wrote last run that names this wallpaper and a still that is there: kept (its rev), not republished.
    double? Adopt(DesktopNow now, string key)
    {
        if (now.Pick is not DesktopPick it) return null;
        LookRead r;
        try { r = LookSyncCore.Read(_o.Sync.Dir); } catch (Exception) { return null; }
        if (r.State != "ok" || r.Doc?["desktop"] is not JsonObject d) return null;
        if (LookSyncCore.Str(d["by"]) != "mantle" || !LookSyncCore.IsBool(d["ok"], out var ok) || !ok) return null;
        if (LookSyncCore.Str(d["id"]) != it.Id || LookSyncCore.Str(d["type"]) != it.Type || LookSyncCore.Str(d["file"]) != it.File) return null;
        string? still = LookSyncCore.Str(d["still"]);
        if (still == null || !File.Exists(still)) return null;
        LookSyncCore.TryNum(d["rev"], out var rev);
        _lastKey = key;
        _lastRec = (JsonObject)LookSyncCore.Clone(d)!;
        _lastGuardKey = GuardKeyOf(d);
        return rev;
    }

    // ---- small helpers --------------------------------------------------------------------------

    static string KeyOf(DesktopNow now) => now.Pick is DesktopPick p ? p.Id + "|" + p.Type + "|" + (p.File ?? "") : "none|" + (now.Reason ?? "");

    static JsonObject Stamped(JsonObject rec, double? rev)
    {
        var c = (JsonObject)LookSyncCore.Clone(rec)!;
        c["rev"] = LookSyncCore.N(rev ?? 0);
        c["by"] = "mantle";
        return c;
    }

    static JsonObject Bare(JsonObject rec)
    {
        var c = (JsonObject)LookSyncCore.Clone(rec)!;
        foreach (var k in new[] { "rev", "by", "guard", "guardReason", "guardLightReason" }) c.Remove(k);
        return c;
    }

    static string Num(double? d) => d is double x ? x.ToString(CultureInfo.InvariantCulture) : "?";

    void Line(string line) { try { _o.Log?.Invoke("desktop: " + line); } catch (Exception) { /* never worth a failure */ } }

    /// A failure is said once per reason (until a publish succeeds again).
    void Say(string line)
    {
        lock (_said) { if (!_said.Add(line)) return; }
        Line(line);
    }

    void Recovered() { lock (_said) _said.Clear(); }
}
