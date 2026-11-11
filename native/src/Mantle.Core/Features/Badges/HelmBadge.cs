using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Mantle.Core.Features.Badges;

/// <summary>A badge source: the bar items it counts on (exe: a bare name or a full path), its file, its label.</summary>
public sealed record BadgeSource(string Exe, string File, string Label);

/// <summary>A source's reading: count, waiting, errors and why there is no count ("" when it is fine).</summary>
public sealed record BadgeReading(int Count, int Waiting, int Errors, string Reason);

/// <summary>
/// App counts on the bar (lib/helm-badge.js, the pure half): the badge file's parse, which bar item a source counts on,
/// and the words. The real Helm's file is only ever READ (never written, never started or stopped); a test run points
/// the default source elsewhere with MANTLE_HELM_BADGE_FILE.
/// </summary>
public static class HelmBadge
{
    public const string NotRunning = "not running";
    public const string Unreadable = "its badge file could not be read";

    /// <summary>Helm's own source: %APPDATA%\Helm\badge.json, or MANTLE_HELM_BADGE_FILE.</summary>
    public static IReadOnlyList<BadgeSource> DefaultSources(Func<string, string?> env)
    {
        var appData = env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(env("USERPROFILE") ?? "", "AppData", "Roaming");
        var file = env("MANTLE_HELM_BADGE_FILE") is { Length: > 0 } f ? f : Path.Combine(appData, "Helm", "badge.json");
        return new[] { new BadgeSource("helm.exe", file, "Helm") };
    }

    static int N0(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return 0;
        double n = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            JsonValueKind.True => 1,
            _ => 0,
        };
        return double.IsFinite(n) && n > 0 ? (int)Math.Min(Math.Floor(n), int.MaxValue) : 0;
    }

    /// <summary>A file's text (null = missing) as a reading. A count whose writer (pid) is gone is "not running".</summary>
    public static BadgeReading Parse(string? text, Func<int, bool> alive)
    {
        if (text == null) return new(0, 0, 0, NotRunning);
        JsonElement v;
        try { using var doc = JsonDocument.Parse(text); v = doc.RootElement.Clone(); }
        catch (JsonException) { return new(0, 0, 0, Unreadable); }
        if (v.ValueKind != JsonValueKind.Object) return new(0, 0, 0, Unreadable);
        int waiting = N0(v, "waiting"), errors = N0(v, "errors");
        int count = N0(v, "count"); if (count == 0) count = waiting + errors;
        if (count == 0) return new(0, waiting, errors, "");
        if (v.TryGetProperty("pid", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            bool ok = p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pid) && pid > 0 && alive(pid);
            if (!ok) return new(0, 0, 0, NotRunning);
        }
        return new(count, waiting, errors, "");
    }

    /// <summary>Does a source count on a bar item? A full path must be equal (any case); a bare name matches the file name.</summary>
    public static bool Matches(BadgeSource? source, string? itemExe)
    {
        if (source == null || string.IsNullOrEmpty(itemExe)) return false;
        var want = source.Exe.ToLowerInvariant();
        if (want.Length == 0) return false;
        var have = itemExe.ToLowerInvariant().Replace('/', '\\');
        if (want.Contains('\\') || want.Contains('/')) return have == want.Replace('/', '\\');
        return have == want || have.EndsWith("\\" + want, StringComparison.Ordinal);
    }

    /// <summary>The tooltip line: "2 need you: 1 waiting, 1 stopped on an error".</summary>
    public static string Describe(BadgeReading? b)
    {
        if (b == null || b.Count == 0) return "";
        var parts = new List<string>();
        if (b.Waiting > 0) parts.Add($"{b.Waiting} waiting");
        if (b.Errors > 0) parts.Add($"{b.Errors} stopped on an error");
        return $"{b.Count} {(b.Count == 1 ? "needs" : "need")} you{(parts.Count > 0 ? ": " + string.Join(", ", parts) : "")}";
    }

    /// <summary>The disc's text: the count, "9+" above 9, "" for none.</summary>
    public static string DiscText(int n) => n <= 0 ? "" : n > 9 ? "9+" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The disc's ink: warn when at least one stopped on an error, else accent.</summary>
    public static bool DiscWarn(BadgeReading b) => b.Errors > 0;

    /// <summary>A custom <c>sources</c> setting kept only where usable (an exe, an absolute file); label = the exe's base name.</summary>
    public static List<BadgeSource> CleanSources(JsonElement list)
    {
        var outp = new List<BadgeSource>();
        if (list.ValueKind != JsonValueKind.Array) return outp;
        foreach (var s in list.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var exe = s.TryGetProperty("exe", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : "";
            var file = s.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
            if (exe.Length == 0 || !Path.IsPathFullyQualified(file)) continue;
            var label = s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String && l.GetString()!.Trim().Length > 0
                ? l.GetString()!.Trim() : BaseNoExe(exe);
            outp.Add(new(exe, file, label));
        }
        return outp;
    }

    static string BaseNoExe(string exe)
    {
        var b = exe.Replace('/', '\\').Split('\\')[^1];
        return b.EndsWith(".exe", StringComparison.Ordinal) ? b[..^4] : b;
    }
}
