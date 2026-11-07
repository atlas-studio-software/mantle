using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// <summary>
/// Where the app is, whether the tests may run here, and every process they start (so each one is
/// killed by PID, never by name).
/// </summary>
internal static class Host
{
    static string? Env(string k) => Environment.GetEnvironmentVariable(k);

    /// <summary>A CI runner, or a machine whose owner said so. The tests show real windows and move the
    /// real mouse, which must never happen on the user's own screen.</summary>
    public static bool Allowed =>
        !string.IsNullOrEmpty(Env("CI")) || !string.IsNullOrEmpty(Env("GITHUB_ACTIONS")) || Env("MANTLE_UITEST_LOCAL") == "1";

    public static void Guard()
    {
        if (!Allowed)
            Assert.Inconclusive("refused: UI tests run only on a CI runner (CI / GITHUB_ACTIONS set) or with MANTLE_UITEST_LOCAL=1 on a machine nobody is using; they show the app and move the mouse");
    }

    public static readonly string RepoRoot = FindRoot();
    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "native", "src"))) return d.FullName;
        return Directory.GetCurrentDirectory();
    }

    public static string ExePath =>
        Env("MANTLE_NATIVE_EXE") is { Length: > 0 } e ? e : Path.Combine(RepoRoot, "native", "out", "bin", "MantleNative", "MantleNative.exe");

    public static string OutDir
    {
        get
        {
            var d = Env("MANTLE_UITEST_OUT") is { Length: > 0 } e ? e : Path.Combine(RepoRoot, "native", "out", "ui");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static readonly UIA3Automation Uia = new();

    static readonly List<Process> Started = new();
    public static Process Track(Process p) { lock (Started) Started.Add(p); return p; }

    public static void Kill(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { /* already gone */ }
    }

    public static void KillAll()
    {
        lock (Started) { foreach (var p in Started) Kill(p); Started.Clear(); }
    }

    /// <summary>Polls until the function returns a non-null value or the time runs out (then null).</summary>
    public static T? Until<T>(Func<T?> f, int ms, int stepMs = 100) where T : class
    {
        var sw = Stopwatch.StartNew();
        for (; ; )
        {
            T? v = null;
            try { v = f(); } catch { /* not yet */ }
            if (v != null || sw.ElapsedMilliseconds > ms) return v;
            Thread.Sleep(stepMs);
        }
    }

    public static bool Until(Func<bool> f, int ms, int stepMs = 100) => Until(() => f() ? "y" : null, ms, stepMs) != null;

    /// <summary>A plain window owned by a process the test controls: a stand-in for "a running app".</summary>
    public sealed class Fixture : IDisposable
    {
        public Process Proc { get; }
        public IntPtr Hwnd { get; }
        public string Title { get; }
        public Fixture(string title, int x, int y)
        {
            Title = title;
            string ps = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
                        $"$f.Text = '{title}'; $f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point({x}, {y}); " +
                        "$f.Size = New-Object System.Drawing.Size(420, 280); [System.Windows.Forms.Application]::Run($f)";
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps)));
            Proc = Track(Process.Start(psi)!);
            var h = Until(() => Win32.WindowsOf(Proc.Id, title).Cast<IntPtr?>().FirstOrDefault() is IntPtr w ? (object)w : null, 20000);
            if (h == null) Assert.Fail($"the fixture window '{title}' did not appear in 20 s");
            Hwnd = (IntPtr)h!;
        }
        public void Dispose() => Kill(Proc);
    }

    /// <summary>Mean luminance (0-255) of a screen rectangle, from the real composited pixels.</summary>
    public static double Luminance(Rectangle r)
    {
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
        double s = 0;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++) { var c = bmp.GetPixel(x, y); s += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; }
        return s / (r.Width * r.Height);
    }

    public static void Screenshot(string name)
    {
        try
        {
            var safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
            FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(OutDir, safe + ".png"));
        }
        catch (Exception e) { Console.WriteLine($"screenshot failed: {e.Message}"); }
    }

    public static IEnumerable<AutomationElement> AppButtons(AutomationElement window) =>
        window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
              .Where(b => (b.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("bar.app.", StringComparison.Ordinal));
}
