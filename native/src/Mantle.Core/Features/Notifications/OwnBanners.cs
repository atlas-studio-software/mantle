using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Notifications;

// Port of lib/own-banners.js: MANTLE'S OWN BANNERS (the updater's "Mantle vX is out", results, failures). One
// banner per key, a stable negative id per key for the process, always sticky, at most 3 buttons; a button or
// the X goes to the poster's handler. The toasts surface shows them before apps' banners (never under "+N more").

public sealed record OwnProgress(string Text, double? Pct);
public sealed record OwnButton(string Id, string Label, bool Primary = false);

/// <summary>What a poster gives: anything left null takes the default.</summary>
public sealed record OwnBannerPost(string? Title = null, string? Body = null, string? Detail = null, string? App = null,
    string? Tone = null, OwnProgressPost? Progress = null, IReadOnlyList<OwnButtonPost>? Actions = null);
public sealed record OwnProgressPost(string? Text, double? Pct);
public sealed record OwnButtonPost(string? Id, string? Label, bool Primary = false);

/// <summary>A cleaned banner as the page draws it.</summary>
public sealed record OwnBanner(long Id, string App, string Title, string Body, string? Detail, OwnProgress? Progress, string Tone,
    IReadOnlyList<OwnButton> Actions)
{
    public bool Own => true;
    public bool Sticky => true;
    public string Opens => "none";
}

public enum OwnChange { Post, Drop }
public sealed record OwnBannerEvent(OwnChange Type, OwnBanner Banner, long Id, bool Update);

/// <summary>A handler's answer: keep (null = a button keeps the card, the X drops it), ok false with a reason.</summary>
public sealed record OwnAnswer(bool? Ok = null, bool? Keep = null, string? Reason = null);
public sealed record OwnActResult(bool Ok, bool Keep, string? Reason);

public sealed class OwnBanners
{
    static readonly HashSet<string> Tones = new(StringComparer.Ordinal) { "info", "ok", "error" };
    public const int MaxApp = 40, MaxTitle = 200, MaxBody = 600, MaxDetail = 4000, MaxProgress = 160, MaxLabel = 40, MaxActionId = 40, MaxActions = 3;

    sealed class Entry
    {
        public long Id;
        public OwnBanner Banner = null!;
        public Func<string, Task<OwnAnswer?>>? OnAction;
        public HashSet<string> Running = new(StringComparer.Ordinal);
    }

    readonly Action<string> _log;
    readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);
    readonly List<string> _order = new();                    // post order (a re-post of a dropped key goes last)
    readonly Dictionary<string, Entry> _up = new(StringComparer.Ordinal);
    readonly List<Action<OwnBannerEvent>> _subs = new();
    long _next = -1;

    public OwnBanners(Action<string>? log = null) { _log = log ?? (_ => { }); }

    static string Str(string? v, int max) => v == null ? "" : v.Length > max ? v[..(max - 1)] + "…" : v;

    /// clean(): only known fields, with their limits; what was left out is named.
    public static (OwnBanner Banner, List<string> Dropped) Clean(OwnBannerPost b, long id)
    {
        var dropped = new List<string>();
        OwnProgress? progress = null;
        if (b.Progress != null)
        {
            double? p = b.Progress.Pct is double d && double.IsFinite(d) ? Math.Max(0, Math.Min(100, d)) : null;
            progress = new OwnProgress(Str(b.Progress.Text, MaxProgress), p);
        }
        var actions = new List<OwnButton>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in b.Actions ?? Array.Empty<OwnButtonPost>())
        {
            var aid = a?.Id != null ? Str(a.Id, MaxActionId) : "";
            var label = a != null ? Str(a.Label, MaxLabel) : "";
            if (aid.Length == 0 || label.Length == 0) { dropped.Add($"a button with no {(aid.Length > 0 ? "label" : "id")}"); continue; }
            if (aid == "close") { dropped.Add("a button with the id \"close\" (that is the X)"); continue; }
            if (seen.Contains(aid)) { dropped.Add($"a second \"{aid}\" button"); continue; }
            if (actions.Count >= MaxActions) { dropped.Add($"\"{aid}\" (at most {MaxActions} buttons)"); continue; }
            seen.Add(aid);
            actions.Add(new OwnButton(aid, label, a!.Primary));
        }
        var app = Str(b.App, MaxApp);
        var detail = Str(b.Detail, MaxDetail);
        return (new OwnBanner(id, app.Length > 0 ? app : "Mantle", Str(b.Title, MaxTitle), Str(b.Body, MaxBody), detail.Length > 0 ? detail : null,
            progress, b.Tone != null && Tones.Contains(b.Tone) ? b.Tone : "info", actions), dropped);
    }

    void Emit(OwnBannerEvent ev)
    {
        foreach (var fn in _subs.ToArray())
        {
            try { fn(ev); } catch (Exception ex) { _log($"a listener for Mantle's own banners threw ({ex.Message})"); }
        }
    }

    string? KeyOf(long id) { foreach (var k in _order) if (_up[k].Id == id) return k; return null; }

    /// Show or update the banner for <paramref name="key"/>; returns its id (stable for the process).
    public long Post(string key, OwnBannerPost banner, Func<string, Task<OwnAnswer?>>? onAction = null)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("post(key, banner): key must be a non-empty string", nameof(key));
        if (banner == null) throw new ArgumentNullException(nameof(banner), $"post(\"{key}\", banner): a banner must be an object");
        if (!_ids.TryGetValue(key, out var id)) { id = _next--; _ids[key] = id; }
        var (clean, dropped) = Clean(banner, id);
        if (dropped.Count > 0) _log($"banner \"{key}\": left out {string.Join(", ", dropped)}");
        bool was = _up.TryGetValue(key, out var old);
        if (!was) _order.Add(key);
        _up[key] = new Entry { Id = id, Banner = clean, OnAction = onAction, Running = was ? old!.Running : new HashSet<string>(StringComparer.Ordinal) };
        Emit(new OwnBannerEvent(OwnChange.Post, clean, id, was));
        return id;
    }

    public bool Drop(string key)
    {
        if (!_up.TryGetValue(key, out var e)) return false;
        _up.Remove(key); _order.Remove(key);
        Emit(new OwnBannerEvent(OwnChange.Drop, e.Banner, e.Id, false));
        return true;
    }

    public IReadOnlyList<OwnBanner> List() => _order.Select(k => _up[k].Banner).ToArray();

    public IDisposable OnChange(Action<OwnBannerEvent> fn)
    {
        if (fn == null) throw new ArgumentNullException(nameof(fn));
        _subs.Add(fn);
        return new Off(() => _subs.Remove(fn));
    }

    sealed class Off : IDisposable { Action? _a; public Off(Action a) { _a = a; } public void Dispose() { _a?.Invoke(); _a = null; } }

    /// A button (or "close", the X) pressed on banner <paramref name="id"/>.
    public async Task<OwnActResult> ActAsync(long id, string? action)
    {
        var what = action ?? "";
        var key = KeyOf(id);
        if (key == null) return new OwnActResult(false, false, $"Mantle has no banner {id} up any more");
        var e = _up[key];
        bool close = what == "close";
        if (!close && !e.Banner.Actions.Any(a => a.Id == what)) return new OwnActResult(false, true, $"this banner has no \"{(what.Length > 0 ? what : "(nothing)")}\" button");
        if (e.Running.Contains(what)) return new OwnActResult(false, true, "that is already running");
        if (e.OnAction == null)
        {
            if (close) { Drop(key); return new OwnActResult(true, false, null); }
            _log($"banner \"{key}\": the \"{what}\" button has nothing behind it");
            return new OwnActResult(false, true, "nothing is set up to answer that button");
        }
        e.Running.Add(what);
        OwnAnswer? r;
        try { r = await e.OnAction(what); }
        catch (Exception ex)
        {
            _log($"banner \"{key}\": \"{what}\" failed: {ex.Message}");
            if (close) Drop(key);
            return new OwnActResult(false, !close, ex.Message);
        }
        finally { e.Running.Remove(what); }   // the same set an update in place carried over (Post)
        bool keep = r?.Keep ?? !close;
        if (!keep) Drop(key);   // also when the poster updated it meanwhile: keep:false is its last word
        bool ok = !(r != null && r.Ok == false);
        var why = ok ? null : Str(r!.Reason, MaxBody);
        return new OwnActResult(ok, keep, ok ? null : (why!.Length > 0 ? why : "it did not work, and no reason came back"));
    }
}
