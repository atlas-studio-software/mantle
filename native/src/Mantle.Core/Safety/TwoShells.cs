using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Mantle.Core.Data;

namespace Mantle.Core.Safety;

/// <summary>What Electron's single-instance lock file says: nothing there, a file left behind, held by a running Electron, or unknown.</summary>
public enum LockState { Absent, Stale, Held, Unknown }

/// <summary>
/// NEVER TWO SHELLS (PLAN.md 3.2, SAFE-038): <c>MantleNative.exe --live</c> refuses to start while
/// Electron Mantle runs live, because only one may own the taskbar, the hooks and the app bar. Any one
/// positive signal is enough:
///
///   - its single-instance lock (Chromium's <c>lockfile</c> in <c>%APPDATA%\mantle</c>) is HELD: the file
///     stays behind as a 0-byte file after Electron exits, so its mere existence says nothing. A running
///     Electron keeps it open for writing, so an exclusive open fails with a sharing violation; a file
///     that opens exclusively is a leftover. Any other failure is "unknown", which counts as held (fail
///     closed, said in words);
///   - an installed <c>Mantle.exe</c> runs from the install folder;
///   - its <c>shell-state.json</c> shows a live session (restored:false) owned by a process that still runs.
///
/// Only reads. The exclusive open asks for read access and lasts microseconds; it never changes the file.
/// The same facts feed the ONE write gate (<see cref="Data.WriteGate"/>): <see cref="OtherShell"/> is its
/// "is another shell running" answer, so stores refuse the user's files while Electron runs.
/// </summary>
public static class TwoShells
{
    public const string ElectronLockFile = "lockfile";

    public sealed record Facts(LockState Lock, IReadOnlyList<int> InstalledPids, ShellStateNote State, bool StatePidAlive, string? StatePidName, string? LockNote = null);

    /// <summary>Is the lock file at <paramref name="file"/> held by a running process? (Read-only.)</summary>
    public static (LockState State, string? Note) ProbeLock(string file)
    {
        if (!File.Exists(file)) return (LockState.Absent, null);
        try {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None)) { }
            return (LockState.Stale, null);
        } catch (IOException e) when ((e.HResult & 0xFFFF) == 32 || (e.HResult & 0xFFFF) == 33) {
            return (LockState.Held, null);   // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION: a holder has it open
        } catch (FileNotFoundException) {
            return (LockState.Absent, null);  // gone between the two looks: Electron just exited
        } catch (DirectoryNotFoundException) {
            return (LockState.Absent, null);
        } catch (Exception e) {
            return (LockState.Unknown, $"its lock file could not be checked ({e.GetType().Name}: {e.Message}), so it is taken as held");
        }
    }

    /// The refusal in plain words, or null when native may take the shell.
    public static string? Verdict(Facts f)
    {
        var why = Seen(f);
        if (why.Count == 0) return null;
        return "Mantle (the Electron app) is running live, so this native Mantle did not start: only one of them may own the taskbar, the keyboard hooks and the app bar. "
             + "Seen: " + string.Join("; ", why) + ". Quit the other Mantle first (its tray menu, or Mantle.exe --quit), then start this one again.";
    }

    static List<string> Seen(Facts f)
    {
        var why = new List<string>();
        if (f.Lock == LockState.Held) why.Add("its single-instance lock is held in its profile");
        else if (f.Lock == LockState.Unknown) why.Add(f.LockNote ?? "its lock file could not be checked, so it is taken as held");
        if (f.InstalledPids.Count > 0) why.Add($"Mantle.exe is running (pid {string.Join(", ", f.InstalledPids)})");
        if (f.State.Restored == false && f.State.Pid is { } pid && f.StatePidAlive)
            why.Add($"its shell-state.json shows a live session that has not put Windows back, owned by pid {pid}{(f.StatePidName != null ? " (" + f.StatePidName + ")" : "")}, which is still running");
        return why;
    }

    /// The write gate's words while Electron runs ("Electron Mantle is running (its single-instance lock ...)"), else null.
    public static string? GateWords(Facts f)
    {
        var why = Seen(f);
        return why.Count == 0 ? null : "Electron Mantle is running (" + string.Join("; ", why) + ")";
    }

    /// Read the facts from this machine (read-only).
    public static Facts Probe(string electronProfile, string? installedExe)
    {
        var (lockState, lockNote) = ProbeLock(Path.Combine(electronProfile, ElectronLockFile));
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
        return new Facts(lockState, installed, state, alive, name, lockNote);
    }

    static IEnumerable<Process> SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); } catch (Exception) { return Array.Empty<Process>(); }
    }

    /// Electron's installed exe: %LOCALAPPDATA%\Programs\Mantle\Mantle.exe (the NSIS per-user install).
    public static string InstalledExe(Func<string, string?> env)
        => Path.Combine(env("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mantle", "Mantle.exe");

    /// Electron's profile: %APPDATA%\mantle.
    public static string ElectronProfile(Func<string, string?> env)
        => Path.Combine(env("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mantle");

    /// <summary>
    /// The "other shell" answer for <see cref="WriteGate.For"/>: null while native is the only shell, else
    /// the words. It probes again at most every <paramref name="every"/> (default 3 s), because a store
    /// may write many times a second and Electron can start at any moment.
    /// </summary>
    public static Func<string?> OtherShell(Func<Facts> probe, Func<double> clockMs, TimeSpan? every = null)
    {
        double ttl = (every ?? TimeSpan.FromSeconds(3)).TotalMilliseconds;
        double at = double.NegativeInfinity;
        string? last = null;
        object gate = new();
        return () => {
            lock (gate) {
                double now = clockMs();
                if (now - at >= ttl) {
                    try { last = GateWords(probe()); }
                    catch (Exception e) { last = "whether Electron Mantle is running could not be checked (" + e.Message + ")"; }
                    at = now;
                }
                return last;
            }
        };
    }

    /// <summary>The ONE write gate for this run: data-look's <see cref="WriteGate"/>, told about Electron by this class.</summary>
    public static WriteGate Gate(DataPaths paths, LaunchArgs args, Func<string, string?> env)
    {
        string profile = ElectronProfile(env), exe = InstalledExe(env);
        var watch = Stopwatch.StartNew();
        return WriteGate.For(paths, args, OtherShell(() => Probe(profile, exe), () => watch.Elapsed.TotalMilliseconds));
    }
}
