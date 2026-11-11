using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.Media;

/// <summary>media-guard.json, normalised (lib/media-guard.js normalize + read).</summary>
public sealed record MediaGuardConfig(IReadOnlyList<string> Apps, bool Guard, string? Label, string? File = null, bool Found = false, string? Reason = null);

/// <summary>Whether the rule is on for this run, and why (lib/media-guard.js state()).</summary>
public sealed record MediaGuardState(bool On, string By);

/// <summary>
/// The protected media apps rule (lib/media-guard.js): apps listed in the data folder's media-guard.json get no play,
/// pause or skip buttons in the bar, so a stray click cannot pause them. <c>--media-guard</c> turns the rule on for a run,
/// <c>--media-controls</c> turns it off and wins. A missing or broken file protects nothing, and says why. Pure.
/// </summary>
public static class MediaGuard
{
    public const string FileName = "media-guard.json";
    public const string FlagOn = "--media-guard";
    public const string FlagOff = "--media-controls";
    public const int MaxApps = 50, MaxText = 80;

    static string? Clean(JsonNode? v)
    {
        if (v is not JsonValue j || !j.TryGetValue<string>(out var s)) return null;
        var t = s.Trim();
        if (t.Length > MaxText) t = t[..MaxText];
        return t.Length > 0 ? t : null;
    }

    /// normalize(): apps (deduplicated, case-insensitive, at most 50 of 80 chars), guard (default: true once apps are
    /// listed), label.
    public static MediaGuardConfig Normalize(JsonNode? raw)
    {
        var o = raw as JsonObject;
        var apps = new List<string>();
        if (o?["apps"] is JsonArray a)
            foreach (var x in a) {
                var t = Clean(x);
                if (t != null && !apps.Any(y => string.Equals(y, t, StringComparison.OrdinalIgnoreCase))) apps.Add(t);
                if (apps.Count >= MaxApps) break;
            }
        bool guard = o?["guard"] is JsonValue g && g.TryGetValue<bool>(out var gb) ? gb : apps.Count > 0;
        return new MediaGuardConfig(apps, guard, Clean(o?["label"]));
    }

    /// read(dir): never throws; a missing, unreadable or broken file protects nothing, with the reason.
    public static MediaGuardConfig Read(string dir)
    {
        string file = Path.Combine(dir ?? "", FileName);
        string text;
        try { text = System.IO.File.ReadAllText(file); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
            return Normalize(null) with { File = file, Found = false, Reason = $"no {FileName}, so no media app is protected" };
        } catch (Exception e) {
            return Normalize(null) with { File = file, Found = false, Reason = $"{FileName} could not be read ({e.Message}), so no media app is protected" };
        }
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(text.TrimStart('﻿')); }
        catch (JsonException e) { return Normalize(null) with { File = file, Found = true, Reason = $"{FileName} is not valid JSON ({e.Message}), so no media app is protected" }; }
        if (parsed is not JsonObject) return Normalize(null) with { File = file, Found = true, Reason = $"{FileName} is not a JSON object, so no media app is protected" };
        return Normalize(parsed) with { File = file, Found = true, Reason = null };
    }

    /// state(cfg, argv): no apps = off ("none"); --media-controls = off; --media-guard = on; else the file's guard.
    public static MediaGuardState State(MediaGuardConfig? cfg, bool flagOn, bool flagOff)
    {
        var c = cfg ?? Normalize(null);
        if (c.Apps.Count == 0) return new(false, "none");
        if (flagOff) return new(false, "controls-flag");
        if (flagOn) return new(true, "flag");
        return new(c.Guard, "config");
    }

    /// match(): the listed app a session belongs to (a substring of its AUMID, app name or title, any case), or null.
    public static string? Match(MediaGuardConfig? cfg, string? aumid, string? appName, string? title)
    {
        if (cfg == null || cfg.Apps.Count == 0) return null;
        var hay = new[] { aumid, appName, title }.Select(v => (v ?? "").ToLowerInvariant()).ToArray();
        foreach (var a in cfg.Apps) {
            var needle = a.ToLowerInvariant();
            if (hay.Any(h => h.Contains(needle, StringComparison.Ordinal))) return a;
        }
        return null;
    }

    public static string ReasonFor(MediaGuardConfig? cfg, string? app) =>
        $"{(string.IsNullOrEmpty(app) ? "This app" : app)}{(cfg?.Label is { } l ? " " + l : "")} · controls off so a stray click can't pause it";

    public static string HowFor(MediaGuardState? st) => st?.By == "flag"
        ? $"Mantle was started with {FlagOn}; start it without that flag to allow them"
        : $"{FileName} in Mantle's data folder turns this on; set \"guard\" to false there, or start Mantle with {FlagOff}, to allow them";

    /// summary(): the payload's <c>guard</c> {on, apps, reason, how}.
    public static JsonObject Summary(MediaGuardConfig? cfg, bool flagOn, bool flagOff)
    {
        var c = cfg ?? Normalize(null);
        var st = State(c, flagOn, flagOff);
        var apps = new JsonArray(c.Apps.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (st.By == "none")
            return new JsonObject { ["on"] = false, ["apps"] = new JsonArray(), ["reason"] = cfg?.Reason ?? $"no media app is protected ({FileName} in Mantle's data folder lists them)", ["how"] = null };
        string names = string.Join(", ", c.Apps);
        if (st.On) return new JsonObject { ["on"] = true, ["apps"] = apps, ["reason"] = $"controls are off for {names} so a stray click can't pause them", ["how"] = HowFor(st) };
        return new JsonObject {
            ["on"] = false, ["apps"] = apps,
            ["reason"] = st.By == "controls-flag" ? $"media controls are on for {names} (Mantle was started with {FlagOff})"
                : $"media controls are on for {names} (start Mantle with {FlagOn} to turn them off)",
            ["how"] = null,
        };
    }
}
