using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mantle.Core.Safety;

/// <summary>What shell-state.json (the sidecar's recovery note, data-files F-001) says, read-only.</summary>
public sealed record ShellStateNote(bool Exists, bool Readable, bool? Restored, int? Pid, string? Error)
{
    public static ShellStateNote Read(string file)
    {
        if (!File.Exists(file)) return new(false, false, null, null, null);
        try {
            string text = File.ReadAllText(file).TrimStart('﻿');
            using var d = JsonDocument.Parse(text);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new(true, false, null, null, "it is not a JSON object");
            bool? restored = null;
            if (r.TryGetProperty("restored", out var x) && (x.ValueKind == JsonValueKind.True || x.ValueKind == JsonValueKind.False)) restored = x.ValueKind == JsonValueKind.True;
            int? pid = r.TryGetProperty("pid", out var p) && p.TryGetInt32(out int pv) ? pv : null;
            return new(true, true, restored, pid, null);
        } catch (Exception e) {
            return new(true, false, null, null, e.GetType().Name + ": " + e.Message);
        }
    }
}

/// <summary>
/// The dirty-start guard (SAFE-017): a live start whose shell-state.json says restored:false comes after
/// a run that died without restoring, so <c>restore-windows.ps1 -StateFile ... -NoKill</c> runs (30 s)
/// BEFORE the sidecar records a new "original". The outcome is a note for the taskbar reason. An
/// unreadable file is reported, never guessed at.
/// </summary>
public static class DirtyStart
{
    public const int ScriptTimeoutMs = 30000;

    public sealed record Verdict(bool RunRestore, string? Note);

    public static Verdict Judge(bool live, ShellStateNote state)
    {
        if (!live) return new(false, null);
        if (!state.Exists) return new(false, null);
        if (!state.Readable) return new(false, $"(shell-state.json could not be read: {state.Error}; the previous run's state is unknown, so nothing was restored first)");
        if (state.Restored != false) return new(false, null);
        return new(true, null);
    }

    /// The note after the script ran.
    public static string Outcome(bool ok, string recoveryCommand, string? why)
        => ok ? "(the previous run died without restoring; Windows was put back before this one started)"
              : $"(the previous run died without restoring and putting Windows back first did not finish{(why != null ? ": " + why : "")}; run this by hand: {recoveryCommand})";

    /// Run the restore script synchronously (live only), with the 30 s limit.
    public static (bool Ok, string? Why) RunScript(string script, string stateFile)
    {
        try {
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-StateFile", stateFile, "-NoKill" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (false, "PowerShell did not start");
            if (!p.WaitForExit(ScriptTimeoutMs)) { try { p.Kill(); } catch (Exception) { } return (false, $"it did not finish in {ScriptTimeoutMs / 1000} s"); }
            return p.ExitCode == 0 ? (true, null) : (false, $"it exited with code {p.ExitCode}");
        } catch (Exception e) { return (false, e.Message); }
    }
}
