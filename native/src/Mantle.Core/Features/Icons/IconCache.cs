using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Features.WindowList;

namespace Mantle.Core.Features.Icons;

/// <summary>
/// lib/icon-fresh.js (BAR-300, BAR-301): an icon answer is stamped with its file's mtime and size; it is re-stat'ed
/// at most every 2 s and asked again when the file changed (sticky stale) or after the recheck age, so a rebuilt exe
/// shows its new icon without a restart. A non-file path (shell:AppsFolder\&lt;id&gt;) has no stamp: age only.
/// </summary>
public static class IconFresh
{
    public const double StatEveryMs = 2000;

    public sealed class Entry
    {
        public string? Icon;
        public string? Reason;
        public string? Stamp;
        public double AskedAt;
        public double CheckedAt;
        public bool Stale;
    }

    static readonly Regex AbsPath = new(@"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.Compiled);

    /// "mtimeMs:size", "not-a-file", "missing", or null for a path that is not a local absolute one.
    public static string? FileStamp(string? p, Func<string, (bool Exists, bool IsFile, double MtimeMs, long Size)>? stat = null)
    {
        if (p == null || !AbsPath.IsMatch(p)) return null;
        try {
            var s = stat != null ? stat(p) : Stat(p);
            if (!s.Exists) return "missing";
            return s.IsFile ? Math.Round(s.MtimeMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + s.Size : "not-a-file";
        } catch (Exception) { return "missing"; }
    }

    static (bool, bool, double, long) Stat(string p)
    {
        if (File.Exists(p)) {
            var fi = new FileInfo(p);
            return (true, true, (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds, fi.Length);
        }
        return Directory.Exists(p) ? (true, false, 0, 0) : (false, false, 0, 0);
    }

    public static Entry Stamp(string? icon, string? reason, string? p, double now, Func<string, (bool, bool, double, long)>? stat = null)
        => new() { Icon = icon, Reason = reason, Stamp = FileStamp(p, stat), AskedAt = now, CheckedAt = now };

    public static bool IsFresh(Entry? e, string? p, double now, double? recheckMs = null, double statEveryMs = StatEveryMs,
        Func<string, (bool, bool, double, long)>? stat = null)
    {
        if (e == null || e.Stale) return false;
        if (recheckMs != null && now - e.AskedAt >= recheckMs) return false;
        if (now - e.CheckedAt < statEveryMs) return true;
        e.CheckedAt = now;
        if (FileStamp(p, stat) == e.Stamp) return true;
        e.Stale = true;
        return false;
    }
}

/// <summary>
/// main.js#iconFor (BAR-300..302): the first candidate path with an icon wins; answers are cached per path (and
/// icon index) and re-asked by <see cref="IconFresh"/>. A Store app id (shell:AppsFolder\&lt;id&gt;) asks the
/// sidecar's <c>app</c>, anything else <c>icon</c>. An answer due again keeps its picture while the sidecar
/// cannot be asked: an unreachable sidecar never takes an icon off the bar. Host thread only.
/// </summary>
public sealed class IconResolver
{
    public const string AppsFolder = @"shell:appsfolder\";
    public const string PngUrl = "data:image/png;base64,";
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(60);   // main.js ICON_RECHECK_MS
    static readonly Regex StoreAumid = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,200}_[A-Za-z0-9]{8,20}![A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    readonly Dictionary<string, IconFresh.Entry> _cache = new(StringComparer.Ordinal);
    readonly Func<double> _now;
    readonly Func<string?> _blocked;
    readonly SidecarCall _call;
    readonly Func<string, (bool, bool, double, long)>? _stat;

    /// <param name="blocked">The platform's "cannot be asked" reason (sidecar.blockedReason()), null when it can.</param>
    public IconResolver(SidecarCall call, Func<double> nowMs, Func<string?> blocked, Func<string, (bool, bool, double, long)>? stat = null)
    { _call = call; _now = nowMs; _blocked = blocked; _stat = stat; }

    public int Count => _cache.Count;

    /// Forget a path's answer (a Store update, 'appchanged', drops that app's icon: BAR-279).
    public void Drop(string path) { foreach (var k in new List<string>(_cache.Keys)) if (k == path.ToLowerInvariant() || k.StartsWith(path.ToLowerInvariant() + "#", StringComparison.Ordinal)) _cache.Remove(k); }

    /// shell:AppsFolder\&lt;id&gt; -> the id; null when it is not such a path; "" when it is but the id is not a package app id.
    public static string? StoreAumidOf(string? p)
    {
        string s = p ?? "";
        if (s.Length < AppsFolder.Length || !s[..AppsFolder.Length].Equals(AppsFolder, StringComparison.OrdinalIgnoreCase)) return null;
        string id = s[AppsFolder.Length..];
        return StoreAumid.IsMatch(id) ? id : "";
    }

    public async ValueTask<(string? Icon, string? Reason)> ForAsync(IEnumerable<string?> candidates, int index = 0)
    {
        var tried = new List<string>();
        foreach (var raw in candidates) {
            if (string.IsNullOrEmpty(raw)) continue;
            string p = raw;
            string key = index != 0 ? p.ToLowerInvariant() + "#" + index : p.ToLowerInvariant();
            _cache.TryGetValue(key, out var hit);
            double now = _now();
            if (hit != null && IconFresh.IsFresh(hit, p, now, RecheckAfter.TotalMilliseconds, IconFresh.StatEveryMs, _stat)) {
                if (hit.Icon != null) return (hit.Icon, null);
                tried.Add(Path.GetFileName(p) + ": " + hit.Reason);
                continue;
            }
            if (_blocked() is string blocked) {
                if (hit?.Icon != null) return (hit.Icon, null);
                return (null, "no icon: the sidecar cannot be asked (" + blocked + ")");
            }
            string? icon = null, reason;
            string? store = StoreAumidOf(p);
            try {
                if (store == "") reason = "not an app id Mantle asks Windows about";
                else if (store != null) {
                    var r = await _call("app", new JsonObject { ["aumid"] = store }, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
                    string? png = !TaskJson.IsFalse(r, "ok") && TaskJson.Str(r, "iconPng") is string s && s.StartsWith(PngUrl, StringComparison.Ordinal) ? s : null;
                    icon = png;
                    reason = png != null ? null : TaskJson.IsFalse(r, "ok") ? "Windows does not know that app" : "Windows had no icon for it";
                } else {
                    var args = new JsonObject { ["path"] = p };
                    if (index != 0) args["index"] = index;
                    var r = await _call("icon", args, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
                    icon = TaskJson.Str(r, "icon") is string s && s.Length > 0 ? s : null;
                    reason = icon != null ? null : "Windows had no icon for it";
                }
            } catch (Exception e) {
                // the ask failed (not "Windows has no icon"): keep the picture it had; asked again next time round
                icon = hit?.Icon; reason = icon != null ? null : e.Message;
            }
            _cache[key] = IconFresh.Stamp(icon, reason, p, _now(), _stat);
            if (icon != null) return (icon, null);
            tried.Add(Path.GetFileName(p) + ": " + reason);
        }
        return (null, tried.Count > 0 ? "no icon (" + string.Join("; ", tried) + ")" : "no icon: nothing to read one from (no shortcut, target or exe path)");
    }
}
