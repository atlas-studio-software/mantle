using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Sidecar;

/// <summary>
/// Lane native-r7-sidecar: the sidecar can never outlive the app. The app is killed by its own PID
/// (Process.Kill, not its tree), as a crash or Task Manager would end it, and its sidecar must be gone
/// within seconds.
/// <para>What ends it has to be the APP's job, so the test rules out everything else:</para>
/// <list type="bullet">
/// <item>The sidecar is a stand-in (tests/Mantle.SidecarStub, through MANTLE_SIDECAR_EXE, honoured only with
/// --test) that never reads stdin and never watches --parent-pid: the real sidecar's own exits (stdin EOF,
/// parent watch) cannot be what ends it.</item>
/// <item>TestRunJob's job ends its members only when the test host exits, and the host is running while the
/// assertion waits.</item>
/// <item>The app's log says the sidecar is "in Mantle's own job" (IsProcessInJob on the app's job handle).</item>
/// </list>
/// The stand-in's first start exits by itself (code 3), so the one killed here is the RESTARTED sidecar:
/// the restart ladder's new process joins the same job.
/// </summary>
[TestClass]
public class SidecarJobUiTests
{
    static readonly Regex Started = new(@"started the sidecar, pid (\d+)(.*)$", RegexOptions.Multiline);

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "";
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    static string? StubExe()
    {
        var bin = Path.Combine(Host.RepoRoot, "native", "tests", "Mantle.SidecarStub", "bin");
        if (!Directory.Exists(bin)) return null;
        return Directory.GetFiles(bin, "Mantle.SidecarStub.exe", SearchOption.AllDirectories)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(30));

    [TestMethod]
    public void Killing_the_app_ends_its_restarted_sidecar_through_the_apps_own_job()
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        string stub = StubExe() ?? throw new AssertFailedException("no Mantle.SidecarStub.exe under native/tests/Mantle.SidecarStub/bin (the UI test project references it, so dotnet test builds it)");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-sidecar-job-" + Guid.NewGuid().ToString("N")[..6]);
        var stubDir = Path.Combine(ud, "stub");
        Directory.CreateDirectory(ud);
        // no redirection, like the other UI tests: the app inherits the test run's output pipe, the setup of the 80-minute hang
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false };
        foreach (var a in new[] { "--test", "--profile-name=uitest-sidecar-job", $"--user-data={ud}", "--theme=light" }) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        psi.Environment["MANTLE_SIDECAR_EXE"] = stub;
        psi.Environment["MANTLE_SIDECAR_STUB_DIR"] = stubDir;

        Process? side = null;
        using var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud };
        try {
            Host.LogContains(() => app.Log, "test run using the sidecar at " + stub, "the stand-in sidecar was not taken", 20000);
            // the first start, its "crash", and the restart
            Assert.IsTrue(Host.Until(() => Started.Matches(app.Log).Count >= 2, 30000),
                "the app did not start the sidecar twice (start, then the restart after its exit with code 3):\n" + Tail(app.Log));
            Host.LogContains(() => app.Log, "sidecar exited with code 3; restarting", "the first stand-in's exit was not seen as a crash");
            var starts = Started.Matches(app.Log);
            foreach (Match m in starts)
                StringAssert.Contains(m.Groups[2].Value, "in Mantle's own job", "a sidecar start is not in the app's job: " + m.Value);
            int pid = int.Parse(starts[1].Groups[1].Value);
            // the app logs the start as it creates the process; the stand-in writes its note once its runtime is up
            Assert.IsTrue(Host.Until(() => File.Exists(Path.Combine(stubDir, $"start-2-{pid}.txt")), 10000), $"pid {pid} from the log is not the stand-in's second start: " + string.Join(", ", Directory.GetFiles(stubDir).Select(f => Path.GetFileName(f))));

            side = Process.GetProcessById(pid);
            Assert.IsTrue(side.StartTime >= app.Proc.StartTime, $"pid {pid} started before the app: not its sidecar");
            Thread.Sleep(1000);
            Assert.IsFalse(side.HasExited, "the restarted stand-in ended by itself, so its end below would prove nothing");

            // the crash: the app's own PID, not its tree
            app.Proc.Kill(entireProcessTree: false);
            Assert.IsTrue(app.Proc.WaitForExit(10000), "the app did not end within 10 s of Process.Kill");
            var sw = Stopwatch.StartNew();
            bool gone = side.WaitForExit(5000);
            Assert.IsTrue(gone, $"the sidecar (pid {pid}) outlived the killed app by 5 s: the app's job did not end it (the test host and its own job are still running, so nothing else would have)");
            Console.WriteLine($"sidecar job: pid {pid} ended {sw.ElapsedMilliseconds} ms after the app was gone");
        } finally {
            try { if (side is { HasExited: false }) { side.Kill(entireProcessTree: false); side.WaitForExit(5000); } } catch (Exception) { }
            side?.Dispose();
        }
    }
}
