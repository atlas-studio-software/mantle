using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mantle.Core.Features.LauncherIndex;

namespace Mantle.Core.Features.Run;

/// <summary>What a launch did or would do: ok, the plain-words reason, and whether it was a dry run.</summary>
public sealed record LaunchResult(bool Ok, string Reason = "", bool DryRun = false, int? Pid = null);

/// <summary>
/// lib/launcher-index.js#launch for the rows part 1 opens: desktop shortcuts, Store apps (shell:AppsFolder
/// through Explorer, the AUMID checked again), Windows Settings pages, profile commands (Windows Terminal,
/// else a plain PowerShell window), and Mantle's stand-ins, which open Windows' own until Mantle's window
/// exists natively (Electron does the same when Mantle's is off, and says so). A dry run returns what it
/// WOULD run and runs nothing; <c>--test</c> is always a dry run.
/// </summary>
public static class LauncherLaunch
{
    /// One way to start something: ShellExecute on a path or uri, or an exe with arguments.
    public sealed record Plan(string? Open, string? Exe, IReadOnlyList<string> Args, string Via, string Note);

    static string Quote(string s) => "\"" + s + "\"";
    static string WinDir => Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    /// Electron's onPath: is exe reachable on PATH?
    public static string? OnPath(string exe)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(s => s.Length > 0))
        {
            try { var f = Path.Join(d, exe); if (File.Exists(f)) return f; } catch (ArgumentException) { }
        }
        return null;
    }

    /// The plan for a row, or a refusal in plain words (Plan null, reason set). Pure but for File.Exists and PATH.
    public static (Plan? Plan, LaunchResult? Refusal) PlanFor(LauncherItem? item, bool dryRun)
    {
        if (item == null || string.IsNullOrEmpty(item.Kind)) return (null, new LaunchResult(false, "that row has no item attached (a bug in the launcher)"));
        string note = "";
        bool mantleish = item.Kind == "setting" || !string.IsNullOrEmpty(item.MantleApp);
        // Mantle Settings and the Windows pages it covers: Mantle Settings is not in the native build yet, so
        // Windows' own page opens, with Electron's words for "Mantle Settings is not running"
        var cover = mantleish && item.Path.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? SettingsTable.CoverOf(item.Path) : null;
        if (mantleish && (item.Path == "mantle:settings" || cover != null))
        {
            string winUri = cover != null ? item.Path : "ms-settings:";
            note = "opened Windows Settings: Mantle Settings is not running";
            item = item with { Path = winUri, Kind = "setting", MantleApp = null };
        }
        if (item.Kind == "app" && !string.IsNullOrEmpty(item.Aumid) && string.IsNullOrEmpty(item.MantleApp))
        {
            if (!StoreApps.AumidRe.IsMatch(item.Aumid)) return (null, new LaunchResult(false, "that Store app has an id Mantle does not trust, so it was not opened"));
            string arg = @"shell:AppsFolder\" + item.Aumid;
            return (new Plan(null, Path.Combine(WinDir, "explorer.exe"), [arg], "explorer.exe " + arg, note), null);
        }
        if (item.Kind == "app" && string.IsNullOrEmpty(item.MantleApp))
        {
            if (!File.Exists(item.Path))
                return (null, dryRun ? new LaunchResult(true, "dry run: would open " + item.Path + " (but the file is missing)", true)
                                     : new LaunchResult(false, "the shortcut is gone from disk: " + item.Path));
            return (new Plan(item.Path, null, [], "ShellExecute(" + Quote(item.Path) + ")", note), null);
        }
        if (mantleish && item.Path.StartsWith("mantle:", StringComparison.Ordinal))
        {
            string target = item.Path.Substring("mantle:".Length);
            if (dryRun && string.IsNullOrEmpty(item.WindowsPath)) return (null, new LaunchResult(true, "dry run: would open Mantle's " + target + " window", true));
            // a stand-in opens Windows' own while Mantle's window is not in the native build (Electron: Mantle's is off)
            if (!string.IsNullOrEmpty(item.WindowsPath))
            {
                string why = "opened " + (MantleApps.Apps.FirstOrDefault(a => a.Key == item.MantleApp)?.WindowsName ?? "Windows' own") + ": Mantle's " + target + " window is not in the native build yet";
                var own = item with { Path = item.WindowsPath, MantleApp = null, Kind = "app" };
                if (own.Path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
                {
                    string aumid = own.Path.Substring(@"shell:AppsFolder\".Length);
                    if (!StoreApps.AumidRe.IsMatch(aumid)) return (null, new LaunchResult(false, "that Store app has an id Mantle does not trust, so it was not opened"));
                    return (new Plan(null, Path.Combine(WinDir, "explorer.exe"), [own.Path], "explorer.exe " + own.Path, why), null);
                }
                if (!File.Exists(own.Path))
                    return (null, dryRun ? new LaunchResult(true, "dry run: would open " + own.Path + " (but the file is missing)", true)
                                         : new LaunchResult(false, "the shortcut is gone from disk: " + own.Path));
                return (new Plan(own.Path, null, [], "ShellExecute(" + Quote(own.Path) + ")", why), null);
            }
            return (null, new LaunchResult(false, "Mantle's " + target + " window is not in the native build yet"));
        }
        if (item.Kind == "setting")
            return (new Plan(item.Path, null, [], "ShellExecute(" + Quote(item.Path) + ")", note), null);
        if (item.Kind == "command")
        {
            var wt = OnPath("wt.exe");
            if (wt != null)
                return (new Plan(null, wt, ["-w", "0", "nt", "powershell", "-NoExit", "-Command", item.Path], "wt.exe -w 0 nt powershell -NoExit -Command " + Quote(item.Path), note), null);
            return (new Plan(null, "cmd", ["/c", "start", "", "powershell", "-NoExit", "-Command", item.Path],
                "cmd /c start \"\" powershell -NoExit -Command " + Quote(item.Path), "opened in a plain PowerShell window: wt.exe not found"), null);
        }
        if (item.Kind == "url")
            return (null, dryRun ? new LaunchResult(true, "dry run: would open " + item.Path + " in your browser", true)
                                 : new LaunchResult(false, "opening a website from the native launcher is not built yet (Quartz hand-off, launcher part 2)"));
        if (item.Kind == "power") return (null, new LaunchResult(false, item.Name + " runs through launcher.power (which asks first), not apps.launch"));
        return (null, new LaunchResult(false, "the launcher does not know how to open a \"" + item.Kind + "\""));
    }

    public static LaunchResult Launch(LauncherItem? item, bool dryRun)
    {
        var (plan, refusal) = PlanFor(item, dryRun);
        if (refusal != null) return refusal;
        var p = plan!;
        if (dryRun) return new LaunchResult(true, "dry run: would run " + p.Via + (p.Note.Length > 0 ? " — " + p.Note : ""), true);
        try
        {
            ProcessStartInfo psi;
            if (p.Open != null) psi = new ProcessStartInfo(p.Open) { UseShellExecute = true };
            else
            {
                psi = new ProcessStartInfo(p.Exe!) { UseShellExecute = false, CreateNoWindow = p.Exe == "cmd" };
                foreach (var a in p.Args) psi.ArgumentList.Add(a);
            }
            using var proc = Process.Start(psi);
            int? pid = null;
            try { pid = proc?.Id; } catch (InvalidOperationException) { pid = null; }
            return new LaunchResult(true, p.Note, false, pid);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new LaunchResult(false, (p.Open != null ? "Windows refused to open it: " : "could not start it: ") + e.Message);
        }
    }
}
