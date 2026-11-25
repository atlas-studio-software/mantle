using System;
using System.Linq;
using System.Xml.Linq;
using Mantle.Core.Boot;
using Xunit;

namespace Mantle.Core.Tests.Boot;

/// lane r12-boot: the native logon task (BOOT-001..008 adapted, DATA-037). Pure: nothing is registered.
public class LogonTaskTests
{
    static readonly XNamespace T = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    const string User = @"PC\A&B O'Neil";
    const string Exe = @"C:\Users\A&B O'Neil\Apps\Mantle Native\MantleNative.exe";
    const string Log = @"C:\Users\A&B O'Neil\AppData\Roaming\mantle\mantle-native.log";

    static XElement One(XDocument d, params string[] path)
    {
        XElement e = d.Root!;
        foreach (var p in path) e = e.Element(T + p) ?? throw new Exception("missing " + string.Join("/", path));
        return e;
    }

    [Fact]
    public void BOOT001_the_name_does_not_collide_with_Electrons_Mantle_task()
    {
        Assert.Equal("Mantle Native", LogonTask.Name);
        Assert.Equal(@"\Mantle Native", LogonTask.Uri);
        Assert.Equal("Mantle", LogonTask.ElectronName);
        Assert.NotEqual(LogonTask.ElectronName, LogonTask.Name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BOOT002_BOOT003_BOOT005_every_setting_of_Electrons_template_and_the_native_action_escaped()
    {
        string xml = LogonTask.Xml(User, Exe, Log);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        // the raw text is escaped (an unescaped & would not even parse)
        Assert.Contains("A&amp;B O&apos;Neil", xml);
        Assert.DoesNotContain("A&B", xml);
        var d = XDocument.Parse(xml);
        Assert.Equal("Task", d.Root!.Name.LocalName);
        Assert.Equal("1.4", (string?)d.Root.Attribute("version"));

        Assert.Equal(@"\Mantle Native", One(d, "RegistrationInfo", "URI").Value);
        Assert.Contains("--quit", One(d, "RegistrationInfo", "Description").Value);

        var trig = One(d, "Triggers", "LogonTrigger");
        Assert.Equal("true", trig.Element(T + "Enabled")!.Value);
        Assert.Equal(User, trig.Element(T + "UserId")!.Value);
        Assert.Equal("PT15S", trig.Element(T + "Delay")!.Value);
        Assert.Single(One(d, "Triggers").Elements());

        var pr = One(d, "Principals", "Principal");
        Assert.Equal("Author", (string?)pr.Attribute("id"));
        Assert.Equal(User, pr.Element(T + "UserId")!.Value);
        Assert.Equal("InteractiveToken", pr.Element(T + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", pr.Element(T + "RunLevel")!.Value);

        var s = One(d, "Settings");
        string S(string n) => s.Element(T + n)?.Value ?? throw new Exception("no " + n);
        Assert.Equal("IgnoreNew", S("MultipleInstancesPolicy"));
        Assert.Equal("false", S("DisallowStartIfOnBatteries"));
        Assert.Equal("false", S("StopIfGoingOnBatteries"));
        Assert.Equal("true", S("AllowHardTerminate"));
        Assert.Equal("true", S("StartWhenAvailable"));
        Assert.Equal("false", S("RunOnlyIfNetworkAvailable"));
        Assert.Equal("true", S("AllowStartOnDemand"));
        Assert.Equal("true", S("Enabled"));
        Assert.Equal("false", S("Hidden"));
        Assert.Equal("false", S("RunOnlyIfIdle"));
        Assert.Equal("false", S("WakeToRun"));
        Assert.Equal("PT0S", S("ExecutionTimeLimit"));
        Assert.Equal("4", S("Priority"));
        Assert.Equal("false", s.Element(T + "IdleSettings")!.Element(T + "StopOnIdleEnd")!.Value);
        Assert.Equal("PT1M", s.Element(T + "RestartOnFailure")!.Element(T + "Interval")!.Value);
        Assert.Equal("3", s.Element(T + "RestartOnFailure")!.Element(T + "Count")!.Value);

        var acts = One(d, "Actions");
        Assert.Equal("Author", (string?)acts.Attribute("Context"));
        var exec = Assert.Single(acts.Elements());
        Assert.Equal("Exec", exec.Name.LocalName);
        Assert.Equal(Exe, exec.Element(T + "Command")!.Value);
        Assert.Equal("--live \"--log=" + Log + "\"", exec.Element(T + "Arguments")!.Value);
        Assert.Equal(@"C:\Users\A&B O'Neil\Apps\Mantle Native", exec.Element(T + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void The_xml_refuses_a_relative_exe_and_the_default_log_is_natives_own()
    {
        Assert.Throws<ArgumentException>(() => LogonTask.Xml(User, "MantleNative.exe", Log));
        Assert.Throws<ArgumentException>(() => LogonTask.Xml("", Exe, Log));
        Assert.Equal(@"X:\R\mantle\mantle-native.log", LogonTask.DefaultLog(@"X:\R"));
        Assert.Equal("a&amp;b&lt;c&gt;&quot;d&apos;", LogonTask.Escape("a&b<c>\"d'"));
    }

    const string Base = @"D:\Here";

    [Fact]
    public void BOOT008_OnlyIfExe_removes_only_when_an_Exec_action_is_exactly_that_exe()
    {
        string name = LogonTask.Name;
        // same path
        Assert.True(LogonTask.OnlyIfExe(name, new[] { Exe }, Exe, Base).Remove);
        // different case
        Assert.True(LogonTask.OnlyIfExe(name, new[] { Exe.ToUpperInvariant() }, Exe.ToLowerInvariant(), Base).Remove);
        // quoted, either side, with spaces around
        Assert.True(LogonTask.OnlyIfExe(name, new[] { "  \"" + Exe + "\" " }, Exe, Base).Remove);
        Assert.True(LogonTask.OnlyIfExe(name, new[] { Exe }, "\"" + Exe + "\"", Base).Remove);
        // not normalised the same way: .. and forward slashes
        Assert.True(LogonTask.OnlyIfExe(name, new[] { @"C:/Users/A&B O'Neil/Apps/x/../Mantle Native/MantleNative.exe" }, Exe, Base).Remove);
        // one of several actions
        var two = LogonTask.OnlyIfExe(name, new[] { @"C:\Other\tool.exe", Exe }, Exe, Base);
        Assert.True(two.Remove);
        Assert.Equal($"removed the 'Mantle Native' logon task (it started {Exe})", two.Line);

        // a different exe: left alone, in words, naming what it starts
        var other = LogonTask.OnlyIfExe(name, new[] { @"C:\Users\x\AppData\Local\Programs\Mantle\Mantle.exe" }, Exe, Base);
        Assert.False(other.Remove);
        Assert.Equal($@"left the 'Mantle Native' logon task alone: it starts C:\Users\x\AppData\Local\Programs\Mantle\Mantle.exe, not {Exe}", other.Line);

        // a relative command in the task is never "exactly that exe"
        Assert.False(LogonTask.OnlyIfExe(name, new[] { "MantleNative.exe" }, Base + @"\MantleNative.exe", Base).Remove);
        // a relative -OnlyIfExe is taken against the caller's folder
        Assert.True(LogonTask.OnlyIfExe(name, new[] { Base + @"\bin\MantleNative.exe" }, @"bin\MantleNative.exe", Base).Remove);
        Assert.False(LogonTask.OnlyIfExe(name, new[] { @"E:\bin\MantleNative.exe" }, @"bin\MantleNative.exe", Base).Remove);

        // a task with no Exec action
        var none = LogonTask.OnlyIfExe(name, Array.Empty<string>(), Exe, Base);
        Assert.False(none.Remove);
        Assert.Equal($"left the 'Mantle Native' logon task alone: it starts no program, not {Exe}", none.Line);
        // no task at all
        var gone = LogonTask.OnlyIfExe(name, null, Exe, Base);
        Assert.False(gone.Remove);
        Assert.Equal("no 'Mantle Native' logon task; nothing to remove", gone.Line);
        // empty commands never match
        Assert.Null(LogonTask.NormaliseCommand(""));
        Assert.Null(LogonTask.NormaliseCommand("\"\""));
        Assert.Equal(Exe.ToLowerInvariant(), LogonTask.NormaliseCommand("\"" + Exe + "\""));
        Assert.True(new[] { Exe }.All(c => LogonTask.NormaliseCommand(c) == LogonTask.NormaliseWanted(Exe, Base)));
    }
}
