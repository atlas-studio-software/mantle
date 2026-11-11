using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Mantle.Core.Features.Desktop;

/// <summary>
/// MANTLE_TEST_DESKTOP (a --test run only): what the desktop layer shows in place of live readings, so every runner
/// draws the same thing (UI tests, screenshot pairs). Every key is optional:
/// <c>{"now":"2026-11-06T16:15:32", "stats":{"cpu":7,"memUsed":20830591385,"memTotal":34359738368,"battery":{"percent":81,"charging":true},"batteryReason":"ok"},
/// "mc":"connect ECONNREFUSED 127.0.0.1:7373", "strip":[["Sidecar","..."]], "widgets":"off", "menuAt":[300,200], "short":true}</c>.
/// <c>now</c> freezes the clock (it never ticks); <c>mc</c> is the probe's answer (absent = "ok": the dashboard answers);
/// <c>strip</c> replaces the state's rows; <c>menuAt</c> is where a test opens the menu, in physical px from the layer's corner.
/// </summary>
public sealed record DeskFixture(DateTime? Now, DeskStats.View? Stats, string? McReason, bool McOk,
    IReadOnlyList<DeskRow>? Strip, bool WidgetsOff, (int X, int Y)? MenuAt)
{
    public static DeskFixture? Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { error = "MANTLE_TEST_DESKTOP is not a JSON object"; return null; }
            DateTime? now = r.TryGetProperty("now", out var n) && n.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(n.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
            DeskStats.View? stats = null;
            if (r.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object) {
                double N(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                double? pct = null; bool chg = false;
                if (s.TryGetProperty("battery", out var b) && b.ValueKind == JsonValueKind.Object) {
                    pct = b.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
                    chg = b.TryGetProperty("charging", out var c) && c.ValueKind == JsonValueKind.True;
                }
                string? why = s.TryGetProperty("batteryReason", out var br) && br.ValueKind == JsonValueKind.String ? br.GetString() : null;
                stats = s.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? DeskStats.Failed(e.GetString()!)
                    : DeskStats.Of(N("cpu"), N("memUsed"), N("memTotal"), pct, chg, why);
            }
            string? mc = r.TryGetProperty("mc", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            List<DeskRow>? strip = null;
            if (r.TryGetProperty("strip", out var st) && st.ValueKind == JsonValueKind.Array) {
                strip = new();
                foreach (var row in st.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 2) strip.Add(new(row[0].GetString() ?? "", row[1].GetString() ?? ""));
            }
            bool off = r.TryGetProperty("widgets", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() == "off";
            (int, int)? at = r.TryGetProperty("menuAt", out var ma) && ma.ValueKind == JsonValueKind.Array && ma.GetArrayLength() == 2
                ? (ma[0].GetInt32(), ma[1].GetInt32()) : null;
            return new DeskFixture(now, stats, mc is null or "ok" ? null : mc, mc is null or "ok", strip, off, at);
        } catch (Exception e) { error = "MANTLE_TEST_DESKTOP could not be read: " + e.Message; return null; }
    }
}
