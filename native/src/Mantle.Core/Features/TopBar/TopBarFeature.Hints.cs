using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mantle.Core.Features.TopBar;

// lib/features/topbar.js's TOP-ROW HINTS (TOP-362..371, 374): TopRowHints listens on \\.\pipe\look-sync-toprow (a --test
// run on look-sync-toprow-test-<pid>, never the live one; LOOK_SYNC_TOPROW_PIPE overrides), checks each sender's window
// with the sidecar's `winproc`, keeps hints in dark only, and hands them to the sidecar's colour layer (`underhint`).
// The layer itself, the wash base and the capture verdicts are the glass / under-colour work: until a layer runs, the
// sidecar's refusal is said once per reason. A new sidecar gets every kept hint again (TOP-374).
public sealed partial class TopBarFeature
{
    public const string HintsPipeEnv = "LOOK_SYNC_TOPROW_PIPE";
    /// App: is the shell dark now (the look's resolved theme)? Unset (unit tests): the hints never listen.
    public sealed record HintsConfig(Func<bool> Dark);
    static HintsConfig? _hintsConfig;
    public static void ConfigureHints(HintsConfig c) => _hintsConfig = c;

    TopRowHints? _hints;
    readonly HashSet<string> _hintRefused = new();
    public JsonObject? HintsView => _hints?.View();

    /// The pipe this run listens on: the env override, else a --test run's own, else the live one. A --test run never
    /// takes the live pipe, even when the override names it.
    public static (string Pipe, string? Note) HintsPipe(bool test, string? over, int pid)
    {
        string? note = null;
        if (test && !string.IsNullOrEmpty(over) && string.Equals(TopRowHints.PipePath(over), TopRowHints.DefaultPipe, StringComparison.OrdinalIgnoreCase))
        { note = $"{HintsPipeEnv} names the live pipe, which a --test run never takes"; over = null; }
        return (TopRowHints.PipePath(!string.IsNullOrEmpty(over) ? over : test ? $"look-sync-toprow-test-{pid}" : null), note);
    }

    void InstallHints()
    {
        if (_hintsConfig is not { } cfg) { _ctx.Log("hints: not listening: this run gives the top bar no theme to judge hints by (unconfigured)"); return; }
        if (!_live && !_test) { _ctx.Log("hints: not listening: only a --live or a --test run listens for top-row hints"); return; }
        var (pipe, note) = HintsPipe(_test, Environment.GetEnvironmentVariable(HintsPipeEnv), Environment.ProcessId);
        if (note != null) _ctx.Log("hints: " + note);
        _hints = new TopRowHints(new TopRowHintsOptions
        {
            PipeName = pipe,
            ProcOf = WinProc,
            Dark = cfg.Dark,
            Forward = ForwardHint,
            Log = line => _ctx.Log("hints: " + line),
            // --test: every wake in words, so a test can show the listener costs nothing while nobody connects
            OnWake = what => { if (_test) _ctx.Log("hints: listener woke: " + what); },
        });
        _hints.StartAsync().ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) _ctx.Log("hints: not listening: " + (t.Exception?.InnerException?.Message ?? "the start was cancelled"));
            else if (!t.Result.Ok && t.Result.Reason != "stopped") _ctx.Log("hints: not listening: " + t.Result.Reason);
        }, TaskScheduler.Default);
    }

    async Task<HintProc> WinProc(string hwnd)
    {
        var r = await _ctx.Platform.CallAsync("winproc", new JsonObject { ["hwnd"] = hwnd }, TimeSpan.FromMilliseconds(2000)).ConfigureAwait(false);
        if (!r.Ok) return new HintProc(false, Reason: r.Error ?? "the Mantle helper gave no reason");
        var x = r.Result;
        if (x.ValueKind != JsonValueKind.Object) return new HintProc(false, Reason: "the Mantle helper's answer had no window");
        bool ok = x.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        int pid = x.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pi) ? pi : 0;
        string? path = x.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.String ? pa.GetString() : null;
        string? reason = x.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null;
        return new HintProc(ok, pid, path, ok ? null : reason ?? "the Mantle helper gave no reason");
    }

    /// To the sidecar's colour layer: {hwnd, app, top} or {hwnd, top: null}. A refusal is said once per reason (a
    /// sidecar with no layer running refuses every hint).
    void ForwardHint(string hwnd, string app, JsonObject? top, HintMeta meta)
    {
        if (_stopped) return;
        var args = top != null ? new JsonObject { ["hwnd"] = hwnd, ["app"] = app, ["top"] = top.DeepClone() } : new JsonObject { ["hwnd"] = hwnd, ["top"] = null };
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _ctx.Platform.CallAsync("underhint", args, TimeSpan.FromMilliseconds(2000)).ConfigureAwait(false);
                string? why = !r.Ok ? r.Error ?? "no reason given"
                    : r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False
                        ? (r.Result.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null) ?? "no reason given"
                        : null;
                if (why != null) lock (_hintRefused) if (_hintRefused.Add(why)) _ctx.Log($"hints: the sidecar did not take {app} hint: {why}");
            }
            catch (Exception e) { _ctx.Log("hints: forwarding a hint failed: " + e.Message); }
        });
    }

    void HintsSidecarReady() => _hints?.ResendAll();
    void StopHints() { _hints?.Stop(); _hints = null; }
}
