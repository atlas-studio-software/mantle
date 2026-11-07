using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Safety;

/// <summary>What the guard needs from the run (main.js flags + the pieces other lanes own).</summary>
public sealed class ShellGuardOptions
{
    public required IPlatform Platform { get; init; }
    public required IShellStateHub State { get; init; }
    public required IHostScheduler Host { get; init; }
    public bool Test { get; init; }
    /// --test-tbhide with MANTLE_TBHIDE_TEST_CLASS: the tbhide ladder runs in a test run against a stand-in window.
    public bool TestTbhide { get; init; }
    /// tbhide on (default; --no-tbhide turns it off).
    public bool TbHide { get; init; } = true;
    /// edge clip on (default; --no-edgeclip turns it off).
    public bool EdgeClip { get; init; } = true;
    /// --dock: a real app bar (the bar frame registers it); no edge clip.
    public bool Dock { get; init; }
    /// The island shape (pill); the edge clip is optional for the full-width shapes.
    public bool Pill { get; init; } = true;
    public string RecoveryCommand { get; init; } = "powershell -NoProfile -ExecutionPolicy Bypass -File tools\\restore-windows.ps1";
    /// The standalone restore when the platform cannot be asked (SAFE-012): restore-windows.ps1 -StateFile ... -NoKill, 30 s.
    public Func<(bool Ok, string? Why)>? RunScript { get; init; }
    /// Why the platform cannot be asked right now (ProcessPlatform.BlockedReason), null when it can.
    public Func<string?>? Blocked { get; init; }
    /// Feature stop on a restore (the feature host's StopAllAsync); told the reason.
    public Func<string, Task>? StopFeatures { get; init; }
    /// After a verified restore: the switcher and the features hear released(why).
    public Action<string>? Released { get; init; }
    /// More things a restore lets go of (the desktop layer, the app bar), each with its label: the surfaces' lanes add them.
    public Func<IReadOnlyList<(string Label, Func<Task<PlatformReply>> Run)>>? MoreReleases { get; init; }
    /// The dirty-start note appended to the taskbar reason (SAFE-017), "" when none.
    public Func<string>? Note { get; init; }
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// The live shell changes and their undo (main.js applyLive's shell part, applyEdgeClip, applyTaskbarHide,
/// restoreShell, markRestored / markUnrestored, sidecarUnavailable, and the sidecar's tbhide / edgeclip /
/// taskbar / restore / died events): SAFE-002..016. Host thread only. Every outcome is a state piece with its
/// reason; a restore that cannot be confirmed says so with the one command that fixes it.
/// </summary>
public sealed class ShellGuard
{
    /// SAFE-004: 1 s x10, then 2, 4, 8, 15, 45, 120 s, then every 5 min.
    public static readonly int[] TbHideRetryMs = { 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 2000, 4000, 8000, 15000, 45000, 120000 };
    public const int TbHideRetryAfterMs = 300000;

    readonly ShellGuardOptions _o;
    readonly IPlatform _p;
    readonly IShellStateHub _s;
    Task _chain = Task.CompletedTask;
    IDisposable? _tbRetry;
    int _tbTries;
    bool _quitting;
    bool? _lastRestoreDone;
    double _readyAt;

    public ShellGuard(ShellGuardOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        _p = o.Platform; _s = o.State;
        _p.On("edgeclip", e => OnEdgeClip(e.Body));
        _p.On("tbhide", e => OnTbHide(e.Body));
        _p.On("taskbar", e => OnTaskbar(e.Body));
        _p.On("restore", e => OnRestore(e.Body));
        _p.On("died", e => OnDied(e.Body));
        _p.OnReady(() => { _readyAt = _o.Host.NowMs; _lastRestoreDone = null; });
        _p.OnStatus(st => { if (st.State == PlatformState.Down) Unavailable(st.Reason); });
    }

    public int TbHideTries => _tbTries;
    public bool TbHideRetrying => _tbRetry != null;
    /// The restore chain (a test awaits it).
    public Task Restoring => _chain;

    string Note() => _o.Note?.Invoke() ?? "";
    string? Blocked() => _o.Blocked != null ? _o.Blocked() : (_p.Status.Ready ? null : (_p.Status.Reason.Length > 0 ? _p.Status.Reason : "the sidecar is not running"));

    static JsonObject Args(params (string K, JsonNode? V)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }
    static bool Bool(JsonElement r, string k) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static bool? MaybeBool(JsonElement r, string k) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.ValueKind == JsonValueKind.True : null;
    static string? Str(JsonElement r, string k) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    void Set(Func<ShellState, ShellState> f) => _s.Set(f);

    // ------------------------------------------------------------------ live start (SAFE-002, 003, 004, 007)

    /// <summary>The shell part of a live start, in main.js's order: taskbar auto-hide, edge clip, taskbar hiding.</summary>
    public async Task ApplyLiveAsync()
    {
        await ApplyAutohideAsync().ConfigureAwait(true);
        if (!_o.Dock) {
            await ApplyEdgeClipAsync().ConfigureAwait(true);
            await ApplyTbHideAsync(fresh: true).ConfigureAwait(true);
        }
    }

    async Task ApplyAutohideAsync()
    {
        var r = await _p.CallAsync("taskbar", Args(("mode", "autohide"))).ConfigureAwait(true);
        if (!r.Ok) {
            string why = r.Error ?? "the sidecar did not say why";
            Set(s => s with { Taskbar = new TaskbarPiece(false, s.Taskbar.WasAutohide, ProcessPlatform.IsOldExeError(why)
                ? $"the sidecar refused \"taskbar auto-hide\": {why}{Note()}"
                : $"could not put the Windows taskbar on auto-hide, so it is untouched: {why}{Note()}") });
            return;
        }
        bool on = Bool(r.Result, "autohide");
        bool? was = MaybeBool(r.Result, "wasAutohide");
        string wasText = was == true ? "it was already auto-hide" : was == false ? "it was always visible before"
            : "the sidecar did not say what it was before, so the restore aims at \"always visible\"";
        string unverified = MaybeBool(r.Result, "verified") == false ? " -- the sidecar could not read the flag back to confirm it" : "";
        Set(s => s with { Taskbar = new TaskbarPiece(on, was, on
            ? $"Windows taskbar set to auto-hide ({wasText}; the original is put back on exit){unverified}{Note()}"
            : $"the sidecar ran the auto-hide command and the Windows taskbar is still always visible{Note()}") });
    }

    public async Task ApplyEdgeClipAsync()
    {
        if (!_o.EdgeClip) {
            Set(s => s with { EdgeClip = new EdgeClipPiece(false, false, "--no-edgeclip: the cursor can reach the bottom row, so the Windows taskbar may slide up through the island's gaps") });
            return;
        }
        var r = await _p.CallAsync("edgeclip", Args(("on", true), ("inset", 2)), TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
        if (r.Ok) {
            bool applied = Bool(r.Result, "applied");
            Set(s => s with { EdgeClip = new EdgeClipPiece(true, applied, applied
                ? "the cursor stops 2 px short of the bottom row, so the auto-hide Windows taskbar stays down and the air around the island stays clean"
                : $"Mantle asked for the 2 px edge clip and it is not in force, so the Windows taskbar can slide up through the island's gaps: {Str(r.Result, "reason") ?? "the sidecar did not say why"}") });
            return;
        }
        string why = ProcessPlatform.IsOldExeError(r.Error) ? "the sidecar exe has no \"edgeclip\" command yet: " + PlatformWords.Rebuild : r.Error ?? "failed";
        if (!_o.Pill) {
            Set(s => s with { EdgeClip = new EdgeClipPiece(false, false, $"not used: the full-width bar covers the bottom edge itself, and the clip was unavailable anyway ({why})") });
            _o.Log($"mantle: the 2 px edge clip was not applied, which only matters for the island shape: {why}");
            return;
        }
        Set(s => s with { EdgeClip = new EdgeClipPiece(true, false, $"the Windows taskbar can slide up through the island's gaps: {why}") });
    }

    public async Task ApplyTbHideAsync(bool fresh = false)
    {
        if (!_o.TbHide) {
            Set(s => s with { TbHide = new TbHidePiece(false, false, "--no-tbhide: the Windows taskbar can show when Explorer reveals it (a foreground change, a flashing app, the Win key)") });
            return;
        }
        if (fresh) { _tbRetry?.Dispose(); _tbRetry = null; _tbTries = 0; }
        var r = await _p.CallAsync("tbhide", Args(("on", true)), TimeSpan.FromMilliseconds(9000)).ConfigureAwait(true);
        if (r.Ok) {
            bool applied = Bool(r.Result, "applied");
            string said = Str(r.Result, "reason") ?? "the sidecar did not say why";
            string guard = Str(r.Result, "guard") is { } g ? "; guard " + g : "";
            string since = applied && _readyAt > 0 ? $" ({((_o.Host.NowMs - _readyAt) / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s after the sidecar was ready)" : "";
            string again = applied ? "" : RetryTbHide();
            if (applied) _tbTries = 0;
            Set(s => s with { TbHide = new TbHidePiece(true, applied, said + guard + again) });
            _o.Log($"mantle: taskbar hiding {(applied ? "in force" : "NOT in force")}: {said}{guard}{since}{again}");
            return;
        }
        bool old = ProcessPlatform.IsOldExeError(r.Error);
        string why = old ? "the sidecar exe has no \"tbhide\" command yet: " + PlatformWords.Rebuild : r.Error ?? "failed";
        string again2 = old ? "" : RetryTbHide();
        Set(s => s with { TbHide = new TbHidePiece(true, false, $"the Windows taskbar can show when Explorer reveals it: {why}{again2}") });
        _o.Log($"mantle: taskbar hiding NOT in force: {why}{again2}");
    }

    string RetryTbHide()
    {
        if (!_o.TbHide || (_o.Test && !_o.TestTbhide) || _quitting) return "";
        if (_tbRetry != null) return "; trying again soon";
        int ms = _tbTries < TbHideRetryMs.Length ? TbHideRetryMs[_tbTries] : TbHideRetryAfterMs;
        _tbTries++;
        _tbRetry = _o.Host.Delay(TimeSpan.FromMilliseconds(ms), () => { _tbRetry = null; if (!_quitting) _ = ApplyTbHideAsync(); });
        return $"; trying again in {Math.Round(ms / 1000.0).ToString(CultureInfo.InvariantCulture)} s (attempt {_tbTries + 1})";
    }

    /// <summary>A fresh sidecar restarts the ladder (SAFE-004); the host calls this on every ready after the first.</summary>
    public Task ReapplyAfterRestartAsync() => _quitting ? Task.CompletedTask : ApplyLiveAsync();

    // ------------------------------------------------------------------ the sidecar's own words (SAFE-006, 014, 015)

    void OnEdgeClip(JsonElement m)
    {
        bool applied = Bool(m, "applied");
        string said = Str(m, "reason") ?? (applied ? "the sidecar re-applied it" : "the sidecar did not say why");
        Set(s => s with { EdgeClip = s.EdgeClip with { Applied = applied, Reason = applied
            ? $"the cursor stops 2 px short of the bottom row ({said})"
            : $"the 2 px edge clip is not in force, so the Windows taskbar can slide up through the island's gaps: {said}" } });
        _o.Log($"mantle: edge clip {(applied ? "back on" : "yielded")}: {said}");
    }

    void OnTbHide(JsonElement m)
    {
        bool applied = Bool(m, "applied");
        string said = Str(m, "reason") ?? (applied ? "the sidecar re-applied it" : "the sidecar did not say why");
        string guard = Str(m, "guard") is { } g ? "; guard " + g : "";
        Set(s => s with { TbHide = s.TbHide with { Applied = applied, Reason = said + guard } });
        _o.Log($"mantle: taskbar hiding {(applied ? "in force" : "NOT in force")}: {said}{guard}");
    }

    void OnTaskbar(JsonElement m)
    {
        var ah = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("autohide", out var a) ? a : default;
        if (ah.ValueKind != JsonValueKind.True && ah.ValueKind != JsonValueKind.False) {
            string raw = m.GetRawText();
            Set(s => s with { Taskbar = s.Taskbar with { Autohide = false, Reason = $"the sidecar sent a taskbar event in the old shape ({raw}), so Mantle does not know the auto-hide flag: {PlatformWords.Rebuild}" } });
            return;
        }
        bool on = ah.ValueKind == JsonValueKind.True;
        string why = Str(m, "reason") ?? (on ? "the Windows taskbar is on auto-hide" : "the Windows taskbar is always visible");
        Set(s => s with { Taskbar = s.Taskbar with { Autohide = on, Reason = why } });
    }

    void OnRestore(JsonElement m)
    {
        bool done = Bool(m, "done");
        _lastRestoreDone = done;
        string detail = Str(m, "detail") ?? "the sidecar did not say what it put back";
        if (_o.Test) {
            Set(s => s with {
                Taskbar = s.Taskbar with { Autohide = false, Reason = $"test mode: the Windows taskbar was left exactly as it is, and the sidecar confirmed it on the way out ({detail})" },
                Workarea = new WorkareaPiece(false, $"test mode: no app bar was ever registered, and the sidecar confirmed it on the way out ({detail})"),
            });
            return;
        }
        if (done) {
            MarkRestored($"the sidecar put Windows back on its way out: {detail}");
            _o.Log($"mantle: the sidecar restored Windows on its own: {detail}");
        } else {
            MarkUnrestored($"the sidecar tried to put Windows back on its way out and could not: {detail}");
            _o.Log($"mantle: the sidecar could NOT restore Windows: {detail}\n    run: {_o.RecoveryCommand}");
        }
    }

    void OnDied(JsonElement m)
    {
        if (_o.Test) return;
        string how = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("code", out var c) && c.TryGetInt32(out int code)
            ? "code " + code.ToString(CultureInfo.InvariantCulture)
            : Str(m, "signal") is { } sig ? "killed by " + sig : "code unknown";
        if (_lastRestoreDone == true) { MarkRestored($"the sidecar restored Windows and then exited ({how})"); return; }
        MarkUnrestored($"the sidecar exited ({how}) without confirming it put Windows back");
        _o.Log($"mantle: the sidecar exited without confirming the restore. If the taskbar looks wrong:\n    {_o.RecoveryCommand}");
    }

    /// <summary>SAFE-016: while the platform is unavailable every shell piece says what Windows is doing instead.</summary>
    public void Unavailable(string reason)
    {
        Set(s => {
            var n = s with { Tray = new TrayPiece(false, $"cannot read the notification area: {reason}", false, $"the Windows taskbar is not being watched, so it can come up over Mantle's bar: {reason}") };
            if (_o.Test) return n;
            n = n with {
                Taskbar = n.Taskbar with { Autohide = false, Reason = $"the Windows taskbar is untouched and still where you put it: {reason}" },
                Workarea = new WorkareaPiece(false, $"Mantle's bar is not registered as an app bar, so maximised windows will sit under it: {reason}"),
                Hotkey = n.Hotkey with { WinTap = false, Reason = $"the Win key still opens the Windows Start menu: {reason}" },
            };
            if (n.Desktop.Attached || n.Desktop.Reason != ShellState.NotStartedYet)
                n = n with { Desktop = new DesktopPiece(false, null, $"the desktop layer is floating instead of sitting under the wallpaper: {reason}") };
            if (n.EdgeClip.On) n = n with { EdgeClip = n.EdgeClip with { Applied = false, Reason = $"the cursor is not confined, so the Windows taskbar can slide up through the island's gaps: {reason}" } };
            if (n.TbHide.On) n = n with { TbHide = n.TbHide with { Applied = false, Reason = $"the Windows taskbar is visible again (the sidecar's guard clears its region when the sidecar stops): {reason}" } };
            return n;
        });
    }

    // ------------------------------------------------------------------ the restore (SAFE-005, 010..013)

    public void MarkRestored(string why)
    {
        Set(s => s with {
            Taskbar = s.Taskbar with { Autohide = false, Reason = $"the Windows taskbar is back the way you had it: {why}" },
            Workarea = new WorkareaPiece(false, $"the app bar is gone and the whole screen is available again: {why}"),
            Desktop = new DesktopPiece(false, null, $"the desktop layer is detached: {why}"),
            Hotkey = s.Hotkey with { WinTap = false, Reason = $"the Win key opens the Windows Start menu again: {why}" },
        });
        try { _o.Released?.Invoke(why); } catch (Exception e) { _o.Log("mantle: telling the features they were released failed: " + e.Message); }
    }

    public void MarkUnrestored(string why)
    {
        string tail = $"{why}. Run this and Windows comes back: {_o.RecoveryCommand}";
        Set(s => s with { Taskbar = s.Taskbar with { Autohide = false, Reason = tail }, Workarea = new WorkareaPiece(false, tail) });
    }

    async Task ReleaseEdgeClipAsync(string why)
    {
        if (!_o.EdgeClip || _o.Test || _o.Dock || Blocked() != null) return;
        var r = await _p.CallAsync("edgeclip", Args(("on", false)), TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
        if (r.Ok) Set(s => s with { EdgeClip = new EdgeClipPiece(false, false, $"the cursor can reach the bottom row again: {why}") });
        else _o.Log($"mantle: the edge clip was not released by hand ({r.Error}); the sidecar drops it as it exits");
    }

    async Task ReleaseTbHideAsync(string why)
    {
        _quitting = true;
        _tbRetry?.Dispose(); _tbRetry = null;
        if (!_o.TbHide || (_o.Test && !_o.TestTbhide) || Blocked() != null) return;
        var r = await _p.CallAsync("tbhide", Args(("on", false)), TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
        if (r.Ok) {
            string said = Str(r.Result, "reason") ?? "released";
            Set(s => s with { TbHide = new TbHidePiece(false, false, $"the Windows taskbar draws again: {why} ({said})") });
            _o.Log($"mantle: taskbar hiding off: {said} ({why})");
        } else {
            _o.Log($"mantle: the taskbar region was not cleared by hand ({r.Error}); the sidecar and its guard clear it as it exits");
        }
    }

    /// <summary>Put Windows back (restoreShell): idempotent, serialised, swallows everything.</summary>
    public Task RestoreAsync(string why)
    {
        var prev = _chain;
        _chain = Chained(prev, why);
        return _chain;
    }

    async Task Chained(Task prev, string why)
    {
        try { await prev.ConfigureAwait(true); } catch (Exception) { /* the earlier one said its own failure */ }
        try { await Restore(why).ConfigureAwait(true); }
        catch (Exception e) { _o.Log($"mantle: restore path threw, which it never should: {e}
    run: {_o.RecoveryCommand}"); }
    }

    async Task Restore(string why)
    {
        if (_o.Test) {
            _quitting = true;
            _tbRetry?.Dispose(); _tbRetry = null;
            Set(s => s with {
                Taskbar = s.Taskbar with { Autohide = false, Reason = "test mode: nothing was changed, so there is nothing to restore" },
                Workarea = new WorkareaPiece(false, "test mode: no app bar was registered, so there is nothing to give back"),
            });
            if (_o.StopFeatures != null) await _o.StopFeatures(why).ConfigureAwait(true);
            return;
        }
        if (Blocked() is { } blocked) { WithScript(why, "the sidecar cannot be asked: " + blocked); return; }

        var done = new List<string>();
        var failures = new List<string>();
        await ReleaseEdgeClipAsync(why).ConfigureAwait(true);
        await ReleaseTbHideAsync(why).ConfigureAwait(true);

        var more = _o.MoreReleases?.Invoke() ?? Array.Empty<(string, Func<Task<PlatformReply>>)>();
        bool appbar = false;
        foreach (var (label, run) in more) {
            if (label != "the app bar") continue;
            appbar = true;
            var a = await run().ConfigureAwait(true);
            if (a.Ok) done.Add("app bar removed"); else failures.Add($"the app bar is still registered ({a.Error})");
        }
        if (!appbar) done.Add("no app bar was registered");

        var r = await _p.CallAsync("taskbar", Args(("restore", true)), TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
        if (!r.Ok) failures.Add($"the taskbar's auto-hide flag could not be put back ({r.Error})");
        else {
            var res = r.Result;
            string original = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("state", out var st) && st.TryGetInt32(out int sv)
                ? ((sv & 1) != 0 ? "auto-hide" : "always visible")
                : (_s.Current.Taskbar.WasAutohide == true ? "auto-hide" : "always visible");
            bool neverChanged = MaybeBool(res, "changedByUs") == false;
            if (!neverChanged && MaybeBool(res, "restored") == false) failures.Add($"the taskbar's auto-hide flag is NOT back to {original}");
            else if (MaybeBool(res, "verified") == false) failures.Add($"the taskbar's auto-hide flag was set back to {original} but the sidecar could not read it back to confirm");
            else {
                done.Add(neverChanged ? "taskbar auto-hide left as it was (Mantle never changed it)" : $"taskbar auto-hide back to {original}");
                done.Add("verified");
            }
        }

        if (_o.StopFeatures != null) {
            try { await _o.StopFeatures(why).ConfigureAwait(true); } catch (Exception e) { _o.Log("mantle: stopping the features failed: " + e.Message); }
        }

        var hk = await _p.CallAsync("hotkey", Args(("winTap", false), ("altTab", false), ("winKeys", false), ("chords", new JsonArray()), ("observe", new JsonArray())), TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
        if (!hk.Ok) failures.Add($"the keyboard hook (Win key, Alt+Tab, Win+N, Win+A, feature shortcuts) ({hk.Error})");
        foreach (var (label, run) in more) {
            if (label == "the app bar") continue;
            var x = await run().ConfigureAwait(true);
            if (!x.Ok) failures.Add($"{label} ({x.Error})");
        }

        if (failures.Count == 0) {
            _o.Log("mantle: restored: " + string.Join("; ", done));
            MarkRestored(why);
            return;
        }
        _o.Log($"mantle: RESTORE INCOMPLETE ({why}): {string.Join("; ", failures)}\n    run this and Windows comes back:\n    {_o.RecoveryCommand}");
        MarkUnrestored($"{why}: {string.Join("; ", failures)}");
    }

    void WithScript(string why, string because)
    {
        _o.Log($"mantle: restoring Windows with tools\\restore-windows.ps1 instead ({because})");
        var (ok, err) = _o.RunScript != null ? _o.RunScript() : (false, "there is no restore script to run");
        if (ok) { MarkRestored($"{why}; {because}, so tools/restore-windows.ps1 put Windows back and verified it"); return; }
        MarkUnrestored($"{why}: the sidecar was gone and tools/restore-windows.ps1 did not finish cleanly ({err})");
        _o.Log($"mantle: RESTORE NOT VERIFIED. Run this, and if it still looks wrong add -RestartExplorer:\n    {_o.RecoveryCommand}");
    }
}
