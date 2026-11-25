using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mantle.Core.Boot;
using Xunit;

namespace Mantle.Core.Tests.Boot;

/// <summary>
/// lane r12-boot: native/tools/install-autostart.ps1, switch-back.ps1 and switch-to-native.ps1, run ONLY with
/// -DryRun (they print what they would do and change nothing: no schtasks call, no process, no restore). The
/// Task Scheduler is never read here either: the installer's -AssumeTask stands in for it. Windows PowerShell
/// 5.1 (powershell.exe) runs them, the floor they are written for; a machine without it skips (returns).
/// </summary>
public class BootScriptTests
{
    static string? ToolsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) {
            foreach (var c in new[] { Path.Combine(d.FullName, "tools"), Path.Combine(d.FullName, "native", "tools") })
                if (File.Exists(Path.Combine(c, "switch-back.ps1")) && File.Exists(Path.Combine(c, "install-autostart.ps1"))) return c;
        }
        return null;
    }

    static string? PowerShell()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string p = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(p) ? p : null;
    }

    /// Run a script with -DryRun (always added) and the given arguments; its stdout lines and exit code.
    static (int Code, List<string> Lines)? DryRun(string script, params string[] args)
    {
        string? ps = PowerShell();
        string? tools = ToolsDir();
        Assert.True(tools != null, "native/tools was not found above the tests");
        if (ps == null) return null;
        var psi = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(tools!, script), "-DryRun" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60000), script + " -DryRun did not finish in 60 s");
        string e = err.Result;
        Assert.True(e.Trim().Length == 0, script + " wrote to stderr: " + e);
        return (p.ExitCode, text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList());
    }

    static void InOrder(List<string> lines, params string[] wanted)
    {
        int at = -1;
        foreach (var w in wanted) {
            int i = lines.FindIndex(at + 1, l => l.Contains(w, StringComparison.Ordinal));
            Assert.True(i > at, $"expected, after line {at}: {w}\n--- got ---\n" + string.Join("\n", lines));
            at = i;
        }
    }

    [Fact]
    public void BOOT005_BOOT006_the_installers_dry_run_prints_LogonTask_Xml_exactly_and_the_schtasks_command()
    {
        using var d = new TestDir("boot");
        string exe = d.Write(Path.Combine("A&B O'Neil", "Mantle Native", "MantleNative.exe"), "not a real exe");
        const string user = @"PC\A&B O'Neil";
        string log = d.File(Path.Combine("A&B O'Neil", "mantle-native.log"));
        var r = DryRun("install-autostart.ps1", "-Exe", exe, "-User", user, "-Log", log);
        if (r == null) return;
        var (code, lines) = r.Value;
        Assert.True(code == 0, "exit " + code + ":\n" + string.Join("\n", lines));
        int a = lines.IndexOf("----- task xml -----"), b = lines.IndexOf("----- end task xml -----");
        Assert.True(a >= 0 && b > a, string.Join("\n", lines));
        string printed = string.Join("\n", lines.Skip(a + 1).Take(b - a - 1));
        string want = LogonTask.Xml(user, exe, log).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(want, printed);
        Assert.Contains(lines, l => l.StartsWith("would run: schtasks.exe /Create /TN \"Mantle Native\" /XML ", StringComparison.Ordinal) && l.EndsWith(" /F", StringComparison.Ordinal) && l.Contains("mantle-native-logon-task.xml"));
        Assert.Contains("install-autostart: DRY RUN, nothing is changed", lines);
        Assert.Contains($"  runs: \"{exe}\" --live \"--log={log}\"", lines);
    }

    [Fact]
    public void BOOT008_the_installers_OnlyIfExe_says_what_LogonTask_OnlyIfExe_says()
    {
        const string exe = @"C:\Apps\Mantle Native\MantleNative.exe";
        string baseDir = Path.GetTempPath();
        string N = LogonTask.Name;
        (int, List<string>)? Run(string assume) => DryRun("install-autostart.ps1", "-Uninstall", "-OnlyIfExe", exe, "-AssumeTask", assume);

        var none = Run("<none>");
        if (none == null) return;
        Assert.Equal(0, none.Value.Item1);
        Assert.Contains(LogonTask.OnlyIfExe(N, null, exe, baseDir).Line, none.Value.Item2);

        var noExec = Run("<no-exec>")!.Value;
        Assert.Equal(0, noExec.Item1);
        Assert.Contains(LogonTask.OnlyIfExe(N, Array.Empty<string>(), exe, baseDir).Line, noExec.Item2);

        string[] other = { @"C:\Users\x\AppData\Local\Programs\Mantle\Mantle.exe", "MantleNative.exe" };
        var left = Run(string.Join("|", other))!.Value;
        Assert.Equal(0, left.Item1);
        Assert.Contains(LogonTask.OnlyIfExe(N, other, exe, baseDir).Line, left.Item2);
        Assert.DoesNotContain(left.Item2, l => l.Contains("schtasks", StringComparison.Ordinal));

        // case differs (quotes are the C# rule's test: PowerShell 5.1's -File argument parsing mangles embedded quotes)
        string[] mine = { @"C:\Other\tool.exe", exe.ToUpperInvariant() };
        Assert.True(LogonTask.OnlyIfExe(N, mine, exe, baseDir).Remove);
        var gone = Run(string.Join("|", mine))!.Value;
        Assert.Equal(0, gone.Item1);
        Assert.Contains("would run: schtasks.exe /Delete /TN \"Mantle Native\" /F", gone.Item2);
    }

    [Fact]
    public void Switch_back_dry_run_prints_quit_wait_verified_restore_then_the_two_tasks_and_changes_nothing()
    {
        const string exe = @"X:\Apps\Mantle Native\MantleNative.exe";
        var r = DryRun("switch-back.ps1", "-Exe", exe, "-StartElectron");
        if (r == null) return;
        var (code, lines) = r.Value;
        Assert.True(code == 0, "exit " + code + ":\n" + string.Join("\n", lines));
        InOrder(lines,
            "switch-back: DRY RUN, nothing is changed",
            "step 1: ask native Mantle to put Windows back and quit",
            $"would run: \"{exe}\" --quit",
            "step 2: wait up to 30 s until no MantleNative.exe",
            "step 3: put Windows back and require 'verified'",
            @"-File ""X:\Apps\Mantle Native\tools\restore-windows.ps1"" -StateFile ",
            "require exit 0 and a line starting 'verified:'",
            "step 4: disable the native logon task 'Mantle Native'",
            "would run: schtasks.exe /Change /TN \"Mantle Native\" /DISABLE",
            "step 5: enable Electron's logon task 'Mantle'",
            "would run: schtasks.exe /Change /TN Mantle /ENABLE",
            "step 6: start Electron's task now",
            "would run: schtasks.exe /Run /TN Mantle",
            "switch-back: dry run done; nothing was changed");
        Assert.Contains(lines, l => l.StartsWith("step 2:", StringComparison.Ordinal) && l.Contains("(never killed here)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("-NoKill", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("STOPPED", StringComparison.Ordinal));

        var noStart = DryRun("switch-back.ps1", "-Exe", exe)!.Value;
        Assert.Contains(noStart.Item2, l => l.StartsWith("step 6: skipped (no -StartElectron)", StringComparison.Ordinal));
        Assert.DoesNotContain(noStart.Item2, l => l.Contains("would run: schtasks.exe /Run", StringComparison.Ordinal));
    }

    [Fact]
    public void Switch_to_native_dry_run_quits_Electron_with_its_own_command_restores_verified_then_flips_the_tasks()
    {
        const string exe = @"X:\Apps\Mantle Native\MantleNative.exe";
        var r = DryRun("switch-to-native.ps1", "-Exe", exe, "-ElectronClone", @"X:\clone", "-StartNative");
        if (r == null) return;
        var (code, lines) = r.Value;
        Assert.True(code == 0, "exit " + code + ":\n" + string.Join("\n", lines));
        InOrder(lines,
            "switch-to-native: DRY RUN, nothing is changed",
            "step 1: ask Electron Mantle to put Windows back and quit, with its own command",
            @"would run: X:\clone\node_modules\electron\dist\electron.exe . --quit",
            "step 2: wait up to 30 s until Electron is gone",
            "step 3: put Windows back and require 'verified'",
            @"-File X:\clone\tools\restore-windows.ps1 -StateFile ",
            "step 4: disable Electron's logon task 'Mantle'",
            "would run: schtasks.exe /Change /TN Mantle /DISABLE",
            "step 5: register (or re-point) the native logon task 'Mantle Native'",
            $" -Exe \"{exe}\" -TaskName \"Mantle Native\"",   // after -File <tools>\install-autostart.ps1 (asserted below)
            "step 6: start the native task now",
            "would run: schtasks.exe /Run /TN \"Mantle Native\"",
            "switch-to-native: dry run done; nothing was changed");
        Assert.Contains(lines, l => l.StartsWith("step 2:", StringComparison.Ordinal) && l.Contains(@"mantle\lockfile not held", StringComparison.Ordinal) && l.Contains("(never killed here)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("would run: powershell.exe ", StringComparison.Ordinal) && l.Contains("install-autostart.ps1", StringComparison.Ordinal) && l.Contains($" -Exe \"{exe}\"", StringComparison.Ordinal));

        // an installed Electron: its own Mantle.exe --quit and the restore script inside its app folder
        var inst = DryRun("switch-to-native.ps1", "-Exe", exe, "-ElectronExe", @"X:\P\Mantle\Mantle.exe")!.Value;
        Assert.Equal(0, inst.Item1);
        InOrder(inst.Item2, @"would run: X:\P\Mantle\Mantle.exe --quit", @"-File X:\P\Mantle\resources\app\tools\restore-windows.ps1", "step 6: skipped (no -StartNative)");
    }
}
