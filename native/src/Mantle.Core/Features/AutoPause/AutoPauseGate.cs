using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// The auto-pause host's request gate (docs/autopause-protocol.md "Security gate", lib/features/_autopause-compat.js
/// originOk / fetchOk / hostOk / srcKey and the /pause ttl clamp). PURE: headers in, a verdict with words out.
///
/// - Origin: a request that carries one is served only from a browser EXTENSION (chrome-extension://, moz-extension://,
///   extension://); a web page gets 403.
/// - No Origin: a local app only while it also carries no Sec-Fetch-Site, or "none" (a typed URL). A page's
///   &lt;img src=".../pause"&gt; or no-cors GET carries Sec-Fetch-Site and is refused before any route runs.
/// - Host must be 127.0.0.1 or localhost (optionally :port): no DNS rebinding.
/// </summary>
public static class AutoPauseGate
{
    /// The 403 body's reason, word for word the Electron host's (U+2019 apostrophe).
    public const string Refused = "only this PC’s apps and browser extensions may use this";
    public const int MaxBody = 4096;
    public const int TtlMinMs = 5000, TtlMaxMs = 600000;

    static readonly Regex SrcRe = new("^[A-Za-z0-9:._-]{1,80}$", RegexOptions.CultureInvariant);
    static readonly Regex ExtRe = new("^(chrome-extension|moz-extension|extension)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The gate's answer: <see cref="Allow"/>, or the refusal's code and the log's plain words.</summary>
    /// <param name="Kind">A short key for the log throttle (<c>origin</c>, <c>fetch-site</c>, <c>host</c>, <c>duplicate</c>).</param>
    public sealed record Verdict(bool Allow, string Kind, string Words)
    {
        public static readonly Verdict Pass = new(true, "", "");
    }

    /// No Origin, or an extension's. PURE (originOk).
    public static bool OriginOk(string? origin) => string.IsNullOrEmpty(origin) || ExtRe.IsMatch(origin);

    /// <summary>fetchOk: with an Origin, the Origin decides; without one, Sec-Fetch-Site must be ABSENT (null) or "none".
    /// An empty but present Sec-Fetch-Site is refused, as Node's <c>site === undefined</c> test refuses it.</summary>
    public static bool FetchOk(string? origin, string? secFetchSite)
    {
        if (!string.IsNullOrEmpty(origin)) return OriginOk(origin);
        return secFetchSite == null || string.Equals(secFetchSite.Trim(), "none", StringComparison.OrdinalIgnoreCase);
    }

    /// hostOk: 127.0.0.1 or localhost, with this port or none (case-insensitive).
    public static bool HostOk(string? host, int? port)
    {
        var h = host ?? "";
        var p = port is int n ? n.ToString(CultureInfo.InvariantCulture) : "null";
        return Regex.IsMatch(h, "^(127\\.0\\.0\\.1|localhost)(:" + p + ")?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The whole gate, in Electron's order (Host, then Origin / Sec-Fetch-Site). <paramref name="header"/> looks a
    /// header up by lower-case name (null = absent); <paramref name="duplicated"/> says whether a header came twice.</summary>
    public static Verdict Check(Func<string, string?> header, int? port, Func<string, bool>? duplicated = null)
    {
        // NATIVE (stricter than Node, which keeps the first Host and joins two Origins with ", "): a request that names
        // its Host, Origin or Sec-Fetch-Site twice is refused; no browser sends one.
        foreach (var name in new[] { "host", "origin", "sec-fetch-site" })
            if (duplicated != null && duplicated(name)) return new(false, "duplicate", $"a request with two {name} headers was refused");
        if (!HostOk(header("host"), port)) return new(false, "host", "a request for another host name was refused (only 127.0.0.1 and localhost are served, so a web site cannot rebind its name to this PC)");
        var origin = header("origin");
        var site = header("sec-fetch-site");
        if (!FetchOk(origin, site))
            return !string.IsNullOrEmpty(origin)
                ? new(false, "origin", "a request from a web page was refused (only browser extensions may send an Origin here)")
                : new(false, "fetch-site", $"a request a browser sent for a web page was refused (no Origin, Sec-Fetch-Site {Clip(site)})");
        return Verdict.Pass;
    }

    /// srcKey: trimmed, cut to 80 characters, and then it must match ^[A-Za-z0-9:._-]{1,80}$, else "browser".
    public static string SrcKey(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length > 80) s = s.Substring(0, 80);
        return SrcRe.IsMatch(s) ? s : "browser";
    }

    /// <summary>The /pause ttl: Math.round(Number(ttl)), clamped to 5 s .. 10 min when it is above 0; 0 (= the engine's
    /// 180 s default) when it is missing, junk, 0 or negative.</summary>
    public static long Ttl(string? raw)
    {
        if (raw == null) return 0;
        var t = raw.Trim();
        if (t.Length == 0) return 0;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return 0;
        var r = Math.Floor(d + 0.5);
        if (!(r > 0)) return 0;
        return (long)Math.Min(TtlMaxMs, Math.Max(TtlMinMs, r));
    }

    static string Clip(string? site)
    {
        var s = (site ?? "").Trim().ToLowerInvariant();
        return s is "cross-site" or "same-site" or "same-origin" or "none" ? s : s.Length == 0 ? "empty" : "of an unknown kind";
    }
}

/// <summary>A query string read the way URLSearchParams reads it: '+' is a space, %XX is decoded, the FIRST value wins.</summary>
public sealed class AutoPauseQuery
{
    readonly List<KeyValuePair<string, string>> _all = new();

    public AutoPauseQuery(string? query)
    {
        var q = query ?? "";
        if (q.StartsWith('?')) q = q.Substring(1);
        foreach (var part in q.Split('&'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            var k = eq < 0 ? part : part.Substring(0, eq);
            var v = eq < 0 ? "" : part.Substring(eq + 1);
            _all.Add(new(Decode(k), Decode(v)));
        }
    }

    public bool Has(string key) { foreach (var kv in _all) if (kv.Key == key) return true; return false; }

    /// The first value, or null when the key is missing (URLSearchParams.get).
    public string? Get(string key) { foreach (var kv in _all) if (kv.Key == key) return kv.Value; return null; }

    static string Decode(string s)
    {
        var t = s.Replace('+', ' ');
        try { return Uri.UnescapeDataString(t); } catch (UriFormatException) { return t; }
    }
}
