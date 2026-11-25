using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mantle.Core.Boot;

/// <summary>
/// Start at sign-in for the NATIVE exe (ship.md BOOT-001..008, DATA-037): the same Task Scheduler task as
/// Electron's tools/mantle-logon-task.xml (and for the same reasons, written there: 15 s after Explorer's
/// taskbar exists, the exe run directly, no 3-day time limit, owned by the service and not a terminal),
/// under its OWN name so the two shells' tasks never overwrite each other.
///
/// The task is named <see cref="Name"/> ("Mantle Native", URI <c>\Mantle Native</c>), not Electron's
/// <c>Mantle</c>: Electron's install-autostart.ps1, its uninstaller (-OnlyIfExe) and installer/ci-install-test.ps1
/// all look up "Mantle" by name, and schtasks /Create /F would silently re-point Electron's task at the native
/// exe. Two tasks, one enabled at a time, is what native/tools/switch-to-native.ps1 and switch-back.ps1 flip.
/// The space follows Electron's own "Mantle Elevated Helper"; every script quotes it.
///
/// native/tools/install-autostart.ps1 writes the SAME XML (a test compares its -DryRun output with
/// <see cref="Xml"/>), so a change here must be made there too.
/// </summary>
public static class LogonTask
{
    public const string Name = "Mantle Native";
    public const string Uri = @"\Mantle Native";
    /// Electron's task (tools/mantle-logon-task.xml): never written by native code, only disabled / enabled by the switch scripts.
    public const string ElectronName = "Mantle";
    /// Native's own log in the user's profile: Program.cs turns --log=<profile>\mantle.log (Electron's) into this anyway.
    public const string LogFileName = "mantle-native.log";

    /// The task's arguments: <c>--live "--log=&lt;log&gt;"</c> (the log path quoted: it is under the profile and may hold spaces).
    public static string Arguments(string log) => "--live \"--log=" + log + "\"";

    /// The default log: %APPDATA%\mantle\mantle-native.log.
    public static string DefaultLog(string appData) => Path.Combine(appData, "mantle", LogFileName);

    /// XML text escaping, as PowerShell's [SecurityElement]::Escape does it (&amp; &lt; &gt; &quot; &apos;).
    public static string Escape(string s)
    {
        var b = new StringBuilder(s.Length + 8);
        foreach (char c in s) {
            switch (c) {
                case '&': b.Append("&amp;"); break;
                case '<': b.Append("&lt;"); break;
                case '>': b.Append("&gt;"); break;
                case '"': b.Append("&quot;"); break;
                case '\'': b.Append("&apos;"); break;
                default: b.Append(c); break;
            }
        }
        return b.ToString();
    }

    /// <summary>
    /// The task XML for <paramref name="user"/> (DOMAIN\name) running <paramref name="exe"/> (a full path)
    /// with <paramref name="log"/>; the working directory is the exe's folder. Every value is XML-escaped
    /// (a profile name may hold &amp; or '). Register it as UTF-16 LE with a BOM (the declaration says UTF-16).
    /// </summary>
    public static string Xml(string user, string exe, string log)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("the task needs a user", nameof(user));
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathFullyQualified(exe)) throw new ArgumentException("the task needs the exe's full path", nameof(exe));
        string root = Path.GetDirectoryName(exe) ?? throw new ArgumentException("the exe has no folder", nameof(exe));
        string u = Escape(user), e = Escape(exe), r = Escape(root), a = Escape(Arguments(log));
        var x = new StringBuilder();
        void L(string line) => x.Append(line).Append("\r\n");
        L("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        L("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        L("  <RegistrationInfo>");
        L("    <Description>Mantle (native), the user&apos;s shell layer over Windows: starts 15 s after logon. Stop it cleanly with &quot;" + e + "&quot; --quit; put Windows back without Mantle with tools\\restore-windows.ps1 next to it. Registered by native/tools/install-autostart.ps1.</Description>");
        L("    <URI>" + Escape(Uri) + "</URI>");
        L("  </RegistrationInfo>");
        L("  <Triggers>");
        L("    <LogonTrigger>");
        L("      <Enabled>true</Enabled>");
        L("      <UserId>" + u + "</UserId>");
        L("      <Delay>PT15S</Delay>");
        L("    </LogonTrigger>");
        L("  </Triggers>");
        L("  <Principals>");
        L("    <Principal id=\"Author\">");
        L("      <UserId>" + u + "</UserId>");
        L("      <LogonType>InteractiveToken</LogonType>");
        L("      <RunLevel>LeastPrivilege</RunLevel>");
        L("    </Principal>");
        L("  </Principals>");
        L("  <Settings>");
        L("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        L("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        L("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        L("    <AllowHardTerminate>true</AllowHardTerminate>");
        L("    <StartWhenAvailable>true</StartWhenAvailable>");
        L("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
        L("    <IdleSettings>");
        L("      <StopOnIdleEnd>false</StopOnIdleEnd>");
        L("      <RestartOnIdle>false</RestartOnIdle>");
        L("    </IdleSettings>");
        L("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        L("    <Enabled>true</Enabled>");
        L("    <Hidden>false</Hidden>");
        L("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
        L("    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>");
        L("    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>");
        L("    <WakeToRun>false</WakeToRun>");
        L("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        L("    <Priority>4</Priority>");
        L("    <RestartOnFailure>");
        L("      <Interval>PT1M</Interval>");
        L("      <Count>3</Count>");
        L("    </RestartOnFailure>");
        L("  </Settings>");
        L("  <Actions Context=\"Author\">");
        L("    <Exec>");
        L("      <Command>" + e + "</Command>");
        L("      <Arguments>" + a + "</Arguments>");
        L("      <WorkingDirectory>" + r + "</WorkingDirectory>");
        L("    </Exec>");
        L("  </Actions>");
        L("</Task>");
        return x.ToString();
    }

    /// <summary>
    /// One Exec command as a comparable path: quotes and surrounding spaces stripped, made full, lower-cased.
    /// Null when it is empty or not a full path: the Task Scheduler resolves a bare or relative command
    /// against PATH and the working folder, so it is never "exactly that exe".
    /// </summary>
    public static string? NormaliseCommand(string? command)
    {
        string c = (command ?? "").Trim().Trim('"').Trim();
        if (c.Length == 0 || !Path.IsPathFullyQualified(c)) return null;
        try { return Path.GetFullPath(c).ToLowerInvariant(); } catch (Exception) { return null; }
    }

    /// The -OnlyIfExe value as a comparable path: a relative one is taken against <paramref name="baseDir"/> (the shell's folder).
    public static string NormaliseWanted(string exe, string baseDir)
        => Path.GetFullPath(exe.Trim().Trim('"').Trim(), baseDir).ToLowerInvariant();

    public sealed record RemoveVerdict(bool Remove, string Line);

    /// <summary>
    /// BOOT-008 for the native task: <c>-Uninstall -OnlyIfExe &lt;exe&gt;</c> removes the task only when one of its Exec
    /// actions is exactly that exe (full path, case-insensitive, quotes stripped). <paramref name="commands"/> is null
    /// when there is no such task. The words are install-autostart.ps1's, which applies the same rule.
    /// </summary>
    public static RemoveVerdict OnlyIfExe(string taskName, IReadOnlyList<string>? commands, string exe, string baseDir)
    {
        if (commands == null) return new(false, $"no '{taskName}' logon task; nothing to remove");
        string want = NormaliseWanted(exe, baseDir);
        foreach (var c in commands)
            if (NormaliseCommand(c) == want) return new(true, $"removed the '{taskName}' logon task (it started {exe})");
        string starts = commands.Count == 0 ? "no program" : string.Join(", ", commands);
        return new(false, $"left the '{taskName}' logon task alone: it starts {starts}, not {exe}");
    }
}
