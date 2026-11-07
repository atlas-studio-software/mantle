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
/// NEVER TWO SHELLS (PLAN.md 3.2, SAFE-038): <c>MantleNative.exe --live</c> refuses to start while
/// Electron Mantle runs live, because only one may own the taskbar, the hooks and the app bar. Electron
/// is detected three ways, any one is enough: its single-instance lock file in its profile (Chromium's
/// <c>lockfile</c>, which exists only while a holder runs), an installed <c>Mantle.exe</c> running from
/// the install folder, and a <c>shell-state.json</c> whose live session (restored:false) belongs to a
/// process that is still running. Only reads; never touches Electron's files or processes.
/// </summary>
public static class TwoShells
{
    public const string ElectronLockFile = "lockfile";

    public sealed record Facts(bool LockFile, IReadOnlyList<int> InstalledPids, ShellStateNote State, bool StatePidAlive, string? StatePidName);

    /// The refusal in plain words, or null when native may take the shell.
    public static string? Verdict(Facts f)
    {
        var why = new List<string>();
        if (f.LockFile) why.Add("its single-instance lock is held in its profile");
        if (f.InstalledPids.Count > 0) why.Add($"Mantle.exe is running (pid {string.Join(", ", f.InstalledPids)})");
        if (f.State.Restored == false && f.State.Pid is { } pid && f.StatePidAlive)
            why.Add($"its shell-state.json shows a live session that has not put Windows back, owned by pid {pid}{(f.StatePidName != null ? " (" + f.StatePidName + ")" : "")}, which is still running");
        if (why.Count == 0) return null;
        return "Mantle (the Electron app) is running live, so this native Mantle did not start: only one of them may own the taskbar, the keyboard hooks and the app bar. "
             + "Seen: " + string.Join("; ", why) + ". Quit the other Mantle first (its tray menu, or Mantle.exe --quit), then start this one again.";
    }

    /// Read the facts from this machine (read-only).
    public static Facts Probe(string electronProfile, string? installedExe)
    {
        bool lockFile = File.Exists(Path.Combine(electronProfile, ElectronLockFile));
        var installed = new List<int>();
        if (installedExe != null) {
            foreach (var p in SafeProcesses("Mantle")) {
                try { if (string.Equals(p.MainModule?.FileName, installedExe, StringComparison.OrdinalIgnoreCase)) installed.Add(p.Id); }
                catch (Exception) { /* another user's or an elevated process: not ours to read */ }
                finally { p.Dispose(); }
            }
        }
        var state = ShellStateNote.Read(Path.Combine(electronProfile, "shell-state.json"));
        bool alive = false; string? name = null;
        if (state.Pid is { } pid && pid != Environment.ProcessId) {
            try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; name = p.ProcessName; } catch (Exception) { alive = false; }
        }
        return new Facts(lockFile, installed, state, alive, name);
    }

    static IEnumerable<Process> SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); } catch (Exception) { return Array.Empty<Process>(); }
    }

    /// Electron's installed exe: %LOCALAPPDATA%\Programs\Mantle\Mantle.exe (the NSIS per-user install).
    public static string InstalledExe(Func<string, string?> env)
        => Path.Combine(env("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mantle", "Mantle.exe");
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

/// <summary>
/// The read-only gate for core's own writes (crash.json): until switch-over native never writes the
/// user's real Electron profile, except as the ONLY live shell (contracts.md "The read-only guard").
/// native-data-look's store gate is the general one; this is the same rule for the crash record.
/// </summary>
public static class WriteGate
{
    public static bool May(DataPaths paths, RunKind kind) => !paths.IsUserProfile || kind == RunKind.Live;
}
