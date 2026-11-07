using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Toasts;

// Port of lib/features/_toasts-owner.js: WHICH APPS' WINDOWS BANNERS MANTLE HAS TURNED OFF, WHAT EACH ONE
// WAS BEFORE, AND PUTTING EVERY ONE OF THEM BACK. The rules, unchanged:
// - WRITE-AHEAD: an app's original and its stamp are SAVED in the record before its registry value changes,
//   so a crash between the two leaves the original on disk. A record that cannot be saved = nothing written.
// - NEVER CLOBBERED TWICE: an owned app keeps the original it was first taken with.
// - EVERY EXIT PUTS IT BACK, and only where the value is still Mantle's 0 (a user's own change is never
//   overwritten). Out-of-process writers (the sidecar's exit, the crash supervisor, restore-banners.ps1) list
//   what they put back in marker files; an owned app reading 0 whose marker names its CURRENT stamp is the
//   user's own 0: let go, never written.
// Pure: the registry (the sidecar's toasts.banners) and the record file come in as interfaces.

public sealed record BannerApp(string Aumid, int? Banner, int? Enabled = null, bool Odd = false);
public sealed record BannerRead(bool Ok, string? Reason, IReadOnlyList<BannerApp> Apps);
public readonly record struct BannerSet(string Aumid, int? Value);
public sealed record BannerWriteResult(string Aumid, bool Ok, int? Now, string? Reason);
public sealed record BannerWrite(bool Ok, string? Reason, IReadOnlyList<BannerWriteResult> Results);
public sealed record BannerFailure(string Aumid, string Reason);

/// <summary>Windows' Notifications\Settings, through the sidecar (toasts.banners op read / write). May throw.</summary>
public interface IBannerRegistry
{
    Task<BannerRead?> ReadAsync();
    Task<BannerWrite?> WriteAsync(IReadOnlyList<BannerSet> set);
}

/// <summary>The record file. Load: null = no file, THROWS when it is there but unreadable. Save THROWS when it cannot write.
/// LoadBack: the put-back marker entries (may throw: no entries, said in the log).</summary>
public interface IBannerRecordStore
{
    JsonNode? Load(out bool exists);
    void Save(BannerRecord record);
    BackFiles LoadBack();
}

public sealed record BannerApply(bool Ok, string? Reason, int Owned, IReadOnlyList<string> Took, IReadOnlyList<string> Retook,
    IReadOnlyList<string> Released, IReadOnlyList<BannerFailure> Failed);
public sealed record BannerRestore(bool Ok, string? Reason, int Restored, int Left, IReadOnlyList<BannerFailure> Failed, IReadOnlyList<string> Released);
public readonly record struct BannerCounts(int Owned, int Released, string? RecordWhy);

public sealed class BannerOwner
{
    sealed record Left(Regex Re, string Why);

    /// Left to Windows, with the reason Settings shows: their pop-ups carry buttons only Windows can run.
    static readonly Left[] LeftList =
    {
        new(new Regex(@"^Windows\.SystemToast\.", RegexOptions.IgnoreCase), "Windows' own system messages (AutoPlay, Bluetooth pairing, ...), whose buttons only work in Windows' pop-up"),
        new(new Regex(@"^Windows\.ActionCenter\.", RegexOptions.IgnoreCase), "Windows' own suggestions, whose buttons only work in Windows' pop-up"),
        new(new Regex(@"^NotifyIconGeneratedAumid_", RegexOptions.IgnoreCase), "notification-area icon balloons: a click only reaches their app through Windows"),
        new(new Regex(@"^(Microsoft\.YourPhone_|Microsoft\.WindowsAlarms_|MSTeams_|MicrosoftTeams|Microsoft\.SkypeApp_|com\.squirrel\.Teams\.)", RegexOptions.IgnoreCase),
            "calls and alarms, which are answered or snoozed with buttons only Windows' pop-up has"),
    };

    /// The app ids a test registers; a --test Mantle manages ONLY these (and a live one never does).
    public const string TestPrefix = "Mantle.ToastTest";
    static bool IsTestId(string a) => a.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase);

    public static string? LeftToWindows(string? aumid, bool test)
    {
        var a = aumid ?? "";
        if (a.Length == 0) return "Windows did not say which app it is";
        if (test) return IsTestId(a) ? null : "a test copy of Mantle never changes a real app's Windows setting";
        if (IsTestId(a)) return "a test app id";
        foreach (var l in LeftList) if (l.Re.IsMatch(a)) return l.Why;
        return null;
    }

    readonly IBannerRegistry _reg;
    readonly IBannerRecordStore _store;
    readonly Action<string> _log;
    readonly bool _test;
    readonly Func<double> _clock;
    BannerRecord? _rec;
    string? _recWhy;
    string? _backWhy;
    Task _chain = Task.CompletedTask;

    public BannerOwner(IBannerRegistry reg, IBannerRecordStore store, Action<string>? log = null, bool test = false, Func<double>? now = null)
    {
        _reg = reg ?? throw new ArgumentNullException(nameof(reg));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? (_ => { });
        _test = test;
        _clock = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    BannerRecord? Load()
    {
        try
        {
            var raw = _store.Load(out bool exists);
            _recWhy = null;
            return exists ? BannerRecord.FromJson(raw) : BannerRecord.Empty();
        }
        catch (Exception ex)
        {
            _recWhy = $"Mantle could not read its record of Windows' original pop-up settings ({Words.First(ex.Message)})";
            return null;
        }
    }

    void Save(BannerRecord r) { _store.Save(r); _rec = r; }
    static void Drop(BannerRecord r, string k) { r.Owned.Remove(k); r.Taken.Remove(k); }
    static bool Has(List<string> list, string aumid) => list.Any(x => string.Equals(x, aumid, StringComparison.OrdinalIgnoreCase));

    List<BackEntry> BackEntries()
    {
        BackFiles b;
        try { b = _store.LoadBack(); }
        catch (Exception ex)
        {
            NoteBack($"the put-back marker files could not be read ({Words.First(ex.Message)})");
            return new List<BackEntry>();
        }
        var bad = b.Unreadable.Count > 0 ? $"a put-back marker file could not be read ({Words.First(string.Join("; ", b.Unreadable))})" : null;
        NoteBack(bad);
        return b.Entries.Where(e => !string.IsNullOrEmpty(e.Aumid)).ToList();
    }

    void NoteBack(string? why)
    {
        if (why == _backWhy) return;
        _backWhy = why;
        if (why != null) _log($"{why}; what it lists is ignored, so an app reading 0 stays Mantle's own and is put back at the quit");
    }

    /// The user's own 0: a writer put this app back after its current take (its stamp matches).
    static bool PutBackSince(List<BackEntry> back, string k, BannerRecord r)
    {
        var mine = r.Taken.TryGetValue(k, out var t) ? Stamp.Of(t) : Stamp.None;
        return back.Any(e => string.Equals(e.Aumid, k, StringComparison.OrdinalIgnoreCase) && e.Took.Same(mine));
    }

    /// A fresh integer stamp: the clock's, but above this app's previous stamp and every stamp a marker names for it.
    double StampFor(BannerRecord r, string k, List<BackEntry> back)
    {
        double t = _clock();
        double s = double.IsFinite(t) ? Math.Floor(t) : 0;
        if (r.Taken.TryGetValue(k, out var prev) && double.IsFinite(prev)) s = Math.Max(s, Math.Floor(prev) + 1);
        foreach (var e in back)
            if (string.Equals(e.Aumid, k, StringComparison.OrdinalIgnoreCase) && e.Took.Kind == StampKind.Number && double.IsFinite(e.Took.Value))
                s = Math.Max(s, Math.Floor(e.Took.Value) + 1);
        return s;
    }

    /// One operation at a time: an apply never interleaves with a restore. Continuations stay on the caller's context.
    async Task<T> Serial<T>(Func<Task<T>> fn)
    {
        var prev = _chain;
        var done = new TaskCompletionSource();
        _chain = done.Task;
        try { await prev; return await fn(); }
        finally { done.SetResult(); }
    }

    async Task<BannerRead> ReadReg()
    {
        BannerRead? r;
        try { r = await _reg.ReadAsync(); }
        catch (Exception ex) { return new BannerRead(false, $"Windows' notification settings could not be read ({Words.First(ex.Message)})", Array.Empty<BannerApp>()); }
        if (r == null || !r.Ok || r.Apps == null) return new BannerRead(false, r?.Reason ?? "Windows' notification settings could not be read", Array.Empty<BannerApp>());
        return r;
    }

    async Task<BannerWrite> WriteReg(IReadOnlyList<BannerSet> set)
    {
        try
        {
            var w = await _reg.WriteAsync(set);
            return w != null && w.Results != null ? w : new BannerWrite(false, w?.Reason ?? "no answer", Array.Empty<BannerWriteResult>());
        }
        catch (Exception ex) { return new BannerWrite(false, Words.First(ex.Message), Array.Empty<BannerWriteResult>()); }
    }

    static Dictionary<string, BannerWriteResult> ByAumid(BannerWrite w)
    {
        var d = new Dictionary<string, BannerWriteResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in w.Results) d[x.Aumid ?? ""] = x;
        return d;
    }

    /// Take every app Mantle should cover and make sure each owned one is still off.
    /// <paramref name="fresh"/> (the switch turned on by the user): forget `released`, every app is offered again.
    public Task<BannerApply> ApplyAsync(string why, bool fresh = false) => Serial(async () =>
    {
        var none = Array.Empty<string>();
        var r = Load();
        if (r == null) return new BannerApply(false, _recWhy, 0, none, none, none, Array.Empty<BannerFailure>());
        bool dirty = false;
        if (fresh && r.Released.Count > 0) { r.Released.Clear(); dirty = true; }
        var now = await ReadReg();
        if (!now.Ok) { _rec = r; return new BannerApply(false, now.Reason, r.Owned.Count, none, none, none, Array.Empty<BannerFailure>()); }
        var byId = new Dictionary<string, BannerApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in now.Apps) byId[a.Aumid ?? ""] = a;
        var back = BackEntries();
        var set = new List<BannerSet>();
        var took = new List<string>(); var retook = new List<string>(); var released = new List<string>();
        int changedByUser = 0;
        var offAfterPutBack = new List<string>();
        // 1. what Mantle already owns: still Mantle's 0, put back by someone (take it again), or the user's now
        foreach (var k in r.Owned.Keys.ToArray())
        {
            if (!byId.TryGetValue(k, out var a)) continue;            // gone from Windows' list: kept, restore skips it
            if (a.Banner == 0 && !a.Odd)
            {
                if (!PutBackSince(back, k, r)) continue;              // still Mantle's own 0
                Drop(r, k);
                if (!Has(r.Released, k)) r.Released.Add(k);
                released.Add(k); offAfterPutBack.Add(k); dirty = true;
                continue;
            }
            if (!a.Odd && a.Banner == r.Owned[k])
            {
                r.Taken[k] = StampFor(r, k, back);                    // a NEW stamp, saved before the 0 (write-ahead)
                set.Add(new BannerSet(a.Aumid, 0));
                retook.Add(a.Aumid); dirty = true;
                continue;
            }
            Drop(r, k);
            if (!Has(r.Released, k)) r.Released.Add(k);
            released.Add(k); dirty = true; changedByUser++;
            _log($"You changed an app's Windows pop-up setting yourself, so Mantle let that app go ({changedByUser} so far this pass)");
        }
        if (offAfterPutBack.Count > 0)
            _log($"banners ({why}): Mantle let {offAfterPutBack.Count} app(s) go: their Windows pop-ups were put back while Mantle was not running and you have turned them off yourself since, so Mantle draws no banner for them and never turns Windows' back on");
        // 2. apps not owned yet
        foreach (var a in now.Apps)
        {
            var id = a.Aumid ?? "";
            if (id.Length == 0 || r.Owned.KeyOf(id) != null || Has(r.Released, id)) continue;
            if (LeftToWindows(id, _test) != null) continue;
            if (a.Odd || a.Enabled == 0 || a.Banner == 0) continue;
            if (!(a.Banner == null || a.Banner == 1)) continue;
            r.Owned[id] = a.Banner == 1 ? 1 : null;
            r.Taken[id] = StampFor(r, id, back);                      // saved with the original, before the 0
            set.Add(new BannerSet(id, 0));
            took.Add(id); dirty = true;
        }
        if (dirty)
        {
            try { Save(r); }
            catch (Exception ex)
            {
                // nothing was written yet, so nothing needs undoing; the originals were never at risk
                return new BannerApply(false, $"Mantle could not save its record of Windows' original pop-up settings, so it changed nothing ({Words.First(ex.Message)})",
                    0, none, none, released, Array.Empty<BannerFailure>());
            }
        }
        else _rec = r;
        var failed = new List<BannerFailure>();
        if (set.Count > 0)
        {
            var w = await WriteReg(set);
            var res = ByAumid(w);
            bool undo = false;
            foreach (var s in set)
            {
                res.TryGetValue(s.Aumid, out var x);
                if (x != null && x.Ok) continue;
                failed.Add(new BannerFailure(s.Aumid, x?.Reason ?? w.Reason ?? "Windows did not answer"));
                // a NEW take whose value did not change is not Mantle's: drop it so the record mirrors the registry
                if (took.Contains(s.Aumid) && !(x != null && x.Now == 0)) { var k = r.Owned.KeyOf(s.Aumid); if (k != null) { Drop(r, k); undo = true; } }
            }
            if (undo)
            {
                try { Save(r); }
                catch (Exception ex) { _log($"the record could not be saved after a failed write ({Words.First(ex.Message)}); restore() skips apps that are not 0"); }
            }
        }
        int owned = r.Owned.Count;
        if (took.Count > 0 || retook.Count > 0 || failed.Count > 0)
            _log($"banners ({why}): {owned} app(s) covered by Mantle; took {took.Count}, took back {retook.Count}{(failed.Count > 0 ? $", {failed.Count} FAILED" : "")}");
        return new BannerApply(failed.Count == 0, failed.Count > 0 ? $"Windows did not take the change for {failed.Count} app(s)" : null,
            owned, took, retook, released, failed);
    });

    /// Every owned app back to its original, where the value is still Mantle's 0. An entry is kept only when its
    /// write failed (a later restore, the sidecar or the script finishes it). <paramref name="keepReleased"/>: a quit.
    public Task<BannerRestore> RestoreAsync(string why, bool keepReleased = false) => Serial(async () =>
    {
        var none = Array.Empty<string>();
        var r = Load();
        if (r == null) return new BannerRestore(false, $"{_recWhy}; Windows' pop-ups were left as they are", 0, 0, Array.Empty<BannerFailure>(), none);
        var ids = r.Owned.Keys.ToArray();
        if (ids.Length == 0)
        {
            if (!keepReleased && r.Released.Count > 0) { r.Released.Clear(); try { Save(r); } catch { /* nothing owned: harmless */ } }
            _rec = r;
            return new BannerRestore(true, null, 0, 0, Array.Empty<BannerFailure>(), none);
        }
        var now = await ReadReg();
        if (!now.Ok) { _rec = r; return new BannerRestore(false, $"{now.Reason}; Mantle will put them back next time it can", 0, 0, Array.Empty<BannerFailure>(), none); }
        var byId = new Dictionary<string, BannerApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in now.Apps) byId[a.Aumid ?? ""] = a;
        var back = BackEntries();
        var set = new List<(string Aumid, int? Value, string Key)>();
        var offAfterPutBack = new List<string>();
        int left = 0;
        foreach (var k in ids)
        {
            if (byId.TryGetValue(k, out var a) && a.Banner == 0 && !a.Odd)
            {
                if (PutBackSince(back, k, r))
                {
                    Drop(r, k);
                    if (!Has(r.Released, k)) r.Released.Add(k);
                    offAfterPutBack.Add(k); left++;
                    continue;
                }
                set.Add((a.Aumid, r.Owned[k], k));
            }
            else { Drop(r, k); left++; }   // gone, or the user's own value: nothing of Mantle's to undo
        }
        if (offAfterPutBack.Count > 0)
            _log($"banners put back ({why}): Mantle let {offAfterPutBack.Count} app(s) go without writing them: their Windows pop-ups were put back while Mantle was not running and you have turned them off yourself since, so they stay off as he set them");
        var failed = new List<BannerFailure>();
        if (set.Count > 0)
        {
            var w = await WriteReg(set.Select(s => new BannerSet(s.Aumid, s.Value)).ToArray());
            var res = ByAumid(w);
            foreach (var s in set)
            {
                res.TryGetValue(s.Aumid, out var x);
                if (x != null && x.Ok) Drop(r, s.Key);
                else failed.Add(new BannerFailure(s.Aumid, x?.Reason ?? w.Reason ?? "Windows did not answer"));
            }
        }
        if (!keepReleased) r.Released.Clear();
        string? saveWhy = null;
        try { Save(r); } catch (Exception ex) { saveWhy = Words.First(ex.Message); _rec = r; }
        int restored = set.Count - failed.Count;
        _log($"banners put back ({why}): {restored} app(s) back to Windows' own pop-up{(left > 0 ? $", {left} left as you set them" : "")}{(failed.Count > 0 ? $", {failed.Count} FAILED" : "")}");
        var reason = failed.Count > 0 ? $"Windows did not take the change back for {failed.Count} app(s); tools\\restore-banners.ps1 puts them back"
            : saveWhy != null ? $"they are back, but the record could not be updated ({saveWhy})" : null;
        return new BannerRestore(failed.Count == 0, reason, restored, left, failed, offAfterPutBack);
    });

    /// Does Mantle cover this app right now (its Windows banner is off because of Mantle)?
    public bool Owns(string? aumid) => _rec != null && !string.IsNullOrEmpty(aumid) && _rec.Owned.KeyOf(aumid) != null;
    public bool Released(string? aumid) => _rec != null && !string.IsNullOrEmpty(aumid) && Has(_rec.Released, aumid);
    public BannerCounts Counts() => new(_rec?.Owned.Count ?? 0, _rec?.Released.Count ?? 0, _recWhy);

    /// Load the record without touching the registry (the feature's first look).
    public BannerCounts Peek() { var r = Load(); if (r != null) _rec = r; return Counts(); }
}
