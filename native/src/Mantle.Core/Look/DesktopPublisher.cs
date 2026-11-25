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
}

/// <summary>A check's outcome, for the log and the tests: published, adopted, same, kept (game rest), failed.</summary>
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
    /// Why Mantle rests now (a game runs), else null. While it rests, a check that would publish <c>ok:false</c> keeps the last
    /// record instead (CONTRACT section 4: "During game rest ... Mantle keeps the last record rather than publishing ok:false").
    public Func<string?>? RestingWhy { get; init; }
    public Action<string>? Log { get; init; }
    /// The still's rename is retried after each delay on EBUSY / EPERM / EACCES (look-sync's 50 / 100 / 200 ms).
    public int[] RetryDelays { get; init; } = { 50, 100, 200 };
    /// The pid in the still's tmp name (tests fix it).
    public int? Pid { get; init; }
    /// Tests only: runs between the still and the record (the still's path, or null when the record names none), so a test can
    /// see what is on disk at that moment or write as another app in between.
    internal Action<string?>? BeforeRecord { get; init; }
}

/// <summary>
/// lib/look-desktop.js's publisher, the part native needs (lane r10-look; CONTRACT.md section 2 "Shared store", section 4,
/// the user's 2026-10-10 answers and the Lane 0 freeze; v2 "Retired on v2", CONTRACT.md:385 at look-sync dca42dc): only Mantle writes
/// look.json <c>desktop</c>, and it writes
/// it even while look sync is OFF. On each wallpaper change it writes the still <c>stills\wall-&lt;rev&gt;.jpg</c> FIRST (tmp +
/// rename), then the record through the shared module's writer, then keeps the last 3 stills. The record is the contract's
/// shape: <c>{id, type, title, file, ok, reason, still, stillSize, rev, by}</c> and no other key: NO <c>guard</c> (the v1 top band;
/// native neither publishes it nor adds a <c>region</c>) and no <c>answers</c> (the Lane 0 freeze dropped wallpaperRequest). It is
/// published once per wallpaper change, never on a timer and never for a glass change. The same wallpaper again publishes
/// nothing. At the first check a record from Mantle's last run that still describes the wallpaper and carries no v1 key is
/// adopted, not republished. While Mantle rests for a game, a check that would publish ok:false keeps the last record.
/// One check runs at a time (at most one waits). Nothing here throws: every failure is a reason in the log, said once.
/// </summary>
public sealed class DesktopPublisher
{
    public const int Keep = 3;
    /// Keys a v2 native record never carries (CONTRACT.md:385 and the Lane 0 freeze); a stored record with one is republished.
    public static readonly string[] NotPublished = { "guard", "guardReason", "guardLightReason", "region", "answers" };
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

    /// Look at the wallpaper now; publish when it moved. Queued behind a running check (at most one waits,
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
                Line("kept the desktop record from Mantle's last run (rev " + Num(kept) + "): it still describes the wallpaper");
                return new DesktopOutcome("adopted", kept, null, LookSyncCore.Str(_lastRec?["still"]));
            }
        }
        if (key == _lastKey) return new DesktopOutcome("same", null, null, LookSyncCore.Str(_lastRec?["still"]));
        return await Publish(now, key, why).ConfigureAwait(false);
    }

    // ---- one publish ----------------------------------------------------------------------------

    async Task<DesktopOutcome> Publish(DesktopNow now, string key, string why)
    {
        if (now.Pick is not DesktopPick it) {
            string reason = now.Reason is { Length: > 0 } r0 ? r0 : "there is no wallpaper picture";
            if (Resting() is string rw0) return KeepWhileResting(rw0, reason);
            var none = new JsonObject {
                ["ok"] = false, ["reason"] = reason, ["id"] = null, ["type"] = null, ["title"] = null, ["file"] = null,
                ["still"] = null, ["stillSize"] = null,
            };
            var w0 = await Write(none).ConfigureAwait(false);
            if (!w0.Ok) { _lastKey = null; _lastRec = null; Say("the desktop record could not be written: " + w0.Reason); return new DesktopOutcome("failed", null, w0.Reason, null); }
            _lastKey = key; _lastRec = Stamped(none, w0.Rev);
            Line("shared that there is no desktop picture (" + reason + "; rev " + Num(w0.Rev) + "; " + why + ")");
            Recovered();
            return new DesktopOutcome("published", w0.Rev, reason, null);
        }

        DesktopShot shot;
        try { shot = _o.Source.Still(it) ?? DesktopShot.Fail("no still came back"); }
        catch (Exception e) { shot = DesktopShot.Fail("the still could not be made (" + e.Message + ")"); }
        if (shot.Ok && shot.Jpeg is not { Length: > 0 }) shot = DesktopShot.Fail("the still came back empty");
        if (shot.Ok && !(shot.W > 0 && shot.H > 0)) shot = DesktopShot.Fail("the still has no size");
        if (!shot.Ok && Resting() is string rw1) return KeepWhileResting(rw1, shot.Reason ?? "there is no still");
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
        } else {
            rec["ok"] = false; rec["reason"] = shot.Reason ?? "there is no still"; rec["still"] = null; rec["stillSize"] = null;
            Say("no still of \"" + it.Title + "\": " + (shot.Reason ?? "no reason came back"));
        }
        var w = await Write(rec).ConfigureAwait(false);
        if (!w.Ok) {
            _lastKey = null; _lastRec = null;   // tried again at the next change
            Say("the desktop record could not be written: " + w.Reason);
            return new DesktopOutcome("failed", null, w.Reason, stillFile);
        }
        _lastKey = key; _lastRec = Stamped(rec, w.Rev);
        if (stillFile != null) {
            Line("shared the desktop wallpaper: " + it.Type + " " + it.Id + ", still " + shot.W + "x" + shot.H + " (rev " + Num(w.Rev) + "; " + why + ")");
            Prune(stillFile);
            Recovered();
        }
        return new DesktopOutcome("published", w.Rev, stillFile == null ? LookSyncCore.Str(rec["reason"]) : null, stillFile);
    }

    /// Why Mantle rests now, else null (a reader that throws counts as not resting: the record then says what is true).
    string? Resting()
    {
        try { return _o.RestingWhy?.Invoke() is { Length: > 0 } w ? w : null; } catch (Exception) { return null; }
    }

    /// Game rest: nothing is written, so the last record stays; the next check after the rest looks again.
    DesktopOutcome KeepWhileResting(string resting, string reason)
    {
        _lastKey = null;
        Say("kept the last desktop record while Mantle rests (" + resting + ") instead of sharing that " + reason);
        return new DesktopOutcome("kept", null, reason, null);
    }

    /// The shared module's own queued check first (a theme or glass another app wrote a moment ago is applied, a damaged
    /// file gets its own recovery), then the record through its writer: re-read, rev + 1, tmp + rename, retried.
    async Task<LookWrite> Write(JsonObject rec)
    {
        _o.BeforeRecord?.Invoke(LookSyncCore.Str(rec["still"]));
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

    // ---- start: adopt the last run's record -----------------------------------------------------

    /// A record Mantle wrote last run that names this wallpaper and a still that is there, with no v1 key (<see cref="NotPublished"/>):
    /// kept (its rev), not republished. A record with a guard (native before v2, or Electron) is republished once without it.
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
        if (NotPublished.Any(k => d.ContainsKey(k))) return null;
        LookSyncCore.TryNum(d["rev"], out var rev);
        _lastKey = key;
        _lastRec = (JsonObject)LookSyncCore.Clone(d)!;
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
