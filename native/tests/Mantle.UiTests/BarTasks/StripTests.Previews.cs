using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// The window flyout with live previews (docs/parity/bar.md BAR-221..233, BAR-308..314). Fixture windows are plain
/// WinForms forms in ONE powershell.exe (so they group under the scratch "Alpha Shell" pin), each filled with a
/// solid colour: a card's live picture is then checked in the real screen pixels (DWM draws it, so the colour at the
/// centre of the drawn rectangle is the form's own). Pointer moves are real (FlaUI Mouse on the runner's desktop).
/// </summary>
public partial class StripTests
{
    /// N forms in one powershell.exe, closed by its PID. Colours by name (System.Drawing.Color.FromName).
    sealed class Forms : IDisposable
    {
        public Process Proc { get; }
        public IntPtr[] Hwnds { get; }
        public string[] Titles { get; }
        public Forms(string prefix, int n, string[] colours, int w = 420, int h = 280)
        {
            Titles = Enumerable.Range(0, n).Select(i => $"{prefix} {i}").ToArray();
            var sb = new System.Text.StringBuilder("Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; $fs = @(); ");
            for (int i = 0; i < n; i++)
                sb.Append($"$f = New-Object System.Windows.Forms.Form; $f.Text = '{Titles[i]}'; $f.StartPosition = 'Manual'; " +
                          $"$f.Location = New-Object System.Drawing.Point({40 + i * 60}, {40 + i * 30}); $f.Size = New-Object System.Drawing.Size({w}, {h}); " +
                          $"$f.BackColor = [System.Drawing.Color]::FromName('{colours[i % colours.Length]}'); $fs += $f; ");
            sb.Append("foreach ($x in $fs) { $x.Show() }; [System.Windows.Forms.Application]::Run($fs[0])");
            var psi = new ProcessStartInfo(PowerShellExe) { UseShellExecute = false };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(sb.ToString())));
            Proc = Host.Track(Process.Start(psi)!);
            var hw = new IntPtr[n];
            bool all = Host.Until(() => {
                for (int i = 0; i < n; i++) if (hw[i] == IntPtr.Zero) hw[i] = Win32.WindowsOf(Proc.Id, Titles[i]).FirstOrDefault();
                return hw.All(x => x != IntPtr.Zero);
            }, 30000, 200);
            if (!all) Assert.Fail($"the fixture forms did not all appear in 30 s ({hw.Count(x => x != IntPtr.Zero)} of {n})");
            Hwnds = hw;
        }
        public void Dispose() => Host.Kill(Proc);
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);

    static IntPtr Flyout(BarApp app, string name) => FrameProbe.Find(app.Proc.Id, "Mantle pop-up: " + name + " windows");
    static AutomationElement? ById(IntPtr pop, string id) => pop == IntPtr.Zero ? null : Host.Uia.FromHandle(pop).FindFirstDescendant(cf => cf.ByAutomationId(id));
    static string StatusOf(AutomationElement? e) { try { return e?.Properties.ItemStatus.ValueOrDefault ?? ""; } catch (Exception) { return ""; } }

    /// "live; source WxH; screen L,T,R,B" -> the screen rectangle DWM was given.
    static Rectangle LiveRect(string status)
    {
        var part = status.Split("; ").First(p => p.StartsWith("screen ", StringComparison.Ordinal))["screen ".Length..].Split(',').Select(int.Parse).ToArray();
        return Rectangle.FromLTRB(part[0], part[1], part[2], part[3]);
    }

    /// Mean B, G, R of the middle third of a screen rectangle (the form's client area; its title bar is at the top).
    static (double B, double G, double R) MeanColour(Rectangle r)
    {
        int x = r.X + r.Width / 3, y = r.Y + r.Height / 2, w = Math.Max(2, r.Width / 3), h = Math.Max(2, r.Height / 4);
        var px = Win32.Grab(x, y, w, h);
        double b = 0, g = 0, rr = 0; int n = px.Length / 4;
        for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; rr += px[i + 2]; }
        return (b / n, g / n, rr / n);
    }

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);

    /// A REAL pointer move (SendInput, absolute), in 10 steps 15 ms apart: WinUI hears pointer enter and exit only
    /// from real input; a SetCursorPos jump (FlaUI's Mouse.MoveTo) reaches the window as no pointer event at all.
    static void MoveTo(Point p)
    {
        GetCursorPos(out var from);
        int sw = FrameProbe.ScreenW, sh = FrameProbe.ScreenH;
        for (int i = 1; i <= 10; i++) {
            int x = from.X + (p.X - from.X) * i / 10, y = from.Y + (p.Y - from.Y) * i / 10;
            var inp = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { dx = x * 65535 / (sw - 1), dy = y * 65535 / (sh - 1), dwFlags = 0x0001 | 0x8000 } } };
            SendInput(1, inp, Marshal.SizeOf<INPUT>());
            Thread.Sleep(15);
        }
    }
    static Point Centre(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [TestMethod]
    public void Two_windows_click_opens_live_previews_one_card_each_and_a_card_focuses_its_window()
    {
        const string profile = "uitest-strip-preview";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("preview-" + tag, ("Alpha Shell", PowerShellExe));
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
        using var other = new Host.Fixture($"Preview other {tag}", 960, 60, "pwsh.exe");
        using var forms = new Forms($"Preview {tag}", 2, new[] { "Red", "Blue" });
        var cs = new List<Cell>();
        Cell? Alpha() { cs = Cells(app); return cs.FirstOrDefault(c => c.Pinned && c.Status.Contains("2 windows", StringComparison.Ordinal)); }
        Assert.IsTrue(Host.Until(() => Alpha() != null, 20000, 250), $"in 20 s Alpha Shell's pin did not hold both windows: {Describe(cs)}");
        IntPtr Pop() => Flyout(app, "Alpha Shell");

        // a click touches no window (bar.open action "list") and opens the previews over the icon (BAR-221, BAR-199)
        IntPtr fg = Win32.GetForegroundWindow();
        Click(Alpha()!, app: app);
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()), 4000), $"the previews did not open; the app's click lines: {ClickLines(profile)}");
        Assert.AreEqual(fg, Win32.GetForegroundWindow(), "the click on an app with two windows changed the window in front");
        AutomationElement? Card(int n) => ById(Pop(), "bar.preview." + n);
        AutomationElement? Shot(int n) => ById(Pop(), "bar.preview." + n + ".shot");
        Assert.IsTrue(Host.Until(() => Card(0) != null && Card(1) != null, 3000), "the flyout does not have a card per window (BAR-224)");
        Assert.IsNull(Card(2), "the flyout has more cards than the app has windows");
        var names = new[] { Card(0)!.Name, Card(1)!.Name };
        CollectionAssert.AreEquivalent(forms.Titles, names, "the cards are not the app's two windows");
        Assert.AreEqual("Close this window", ById(Pop(), "bar.preview.0.close")?.Name, "the card's X has no name (BAR-224)");
        Assert.IsNull(ById(Pop(), "bar.winlist.0"), "two windows fell back to the list");

        // the live pictures: DWM draws each window into its box (BAR-308), letterboxed (BAR-309); the screen shows it
        string[] st = new string[2];
        bool live = Host.Until(() => { st[0] = StatusOf(Shot(0)); st[1] = StatusOf(Shot(1)); return st.All(s => s.StartsWith("live", StringComparison.Ordinal)); }, 5000, 150);
        Assert.IsTrue(live, $"the boxes have no live picture after 5 s: [{st[0]}] [{st[1]}]; words: [{Shot(0)?.Name}] [{Shot(1)?.Name}]");
        Thread.Sleep(300);   // DWM's next frame
        Host.Screenshot("preview-open");
        for (int i = 0; i < 2; i++) {
            var r = LiveRect(st[i]);
            var src = st[i].Split("; ")[1]["source ".Length..].Split('x').Select(int.Parse).ToArray();
            Assert.IsTrue(Math.Abs(r.Width / (double)r.Height - src[0] / (double)src[1]) < 0.03, $"card {i}: the picture is squashed: drawn {r.Width}x{r.Height} for a {src[0]}x{src[1]} window (BAR-309)");
            var (b, g, rr) = MeanColour(r);
            bool red = names[i] == forms.Titles[0];
            Assert.IsTrue(red ? rr > 150 && g < 100 && b < 100 : b > 150 && rr < 100 && g < 100,
                $"card {i} ({names[i]}): the screen inside its drawn picture {r} is B{b:F0} G{g:F0} R{rr:F0}, not the {(red ? "red" : "blue")} form: no live picture on screen (BAR-308)");
        }
        // the active window's card is marked (BAR-225)
        int act = Array.FindIndex(forms.Hwnds, h => h == Win32.GetForegroundWindow());
        if (act >= 0) {
            int card = Array.IndexOf(names, forms.Titles[act]);
            Assert.IsTrue(StatusOf(Card(card)).Contains("active", StringComparison.Ordinal), $"the window in front ({forms.Titles[act]}) has no active mark: [{StatusOf(Card(card))}]");
        }

        // a card: the flyout closes and that window comes to the front (BAR-227)
        int pick = act == 0 ? 1 : 0;
        int pickCard = Array.IndexOf(names, forms.Titles[pick]);
        Click(new Cell(-1, names[pickCard], "", Card(pickCard)!.BoundingRectangle, Card(pickCard)!));
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(Pop()), 3000), "the flyout stayed open after a card was clicked");
        Assert.IsTrue(Host.Until(() => Win32.GetForegroundWindow() == forms.Hwnds[pick], 4000), $"the card did not bring its window to the front (foreground: {Win32.Title(Win32.GetForegroundWindow())}); log: {ClickLines(profile)}");

        // the icon again opens it, and again closes it (its own toggle)
        Click(Alpha()!, app: app);
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()), 4000), "the previews did not open the second time");
        Click(Alpha()!, app: app);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(Pop()), 3000), "a second click on the icon did not close its previews");

        // the X: a test run never closes a window; the card says why in words and the flyout stays up (BAR-228)
        Click(Alpha()!, app: app);
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()) && Card(1) != null, 4000), "the previews did not open the third time");
        var x = ById(Pop(), "bar.preview.0.close")!;
        long at = LogLength(profile);
        Click(new Cell(-1, "close", "", x.BoundingRectangle, x));
        string log = "";
        Assert.IsTrue(Host.Until(() => { log = LogSince(profile, at); return log.Contains("bar tasks: window close (a card's X): failed: could not close that window: a test run never closes", StringComparison.Ordinal); }, 4000), $"no refused close line: {Tail(log)}");
        Assert.IsTrue(Host.Until(() => StatusOf(Shot(0)) == "held" && (Shot(0)?.Name ?? "").StartsWith("Not closed: ", StringComparison.Ordinal), 3000),
            $"the refused card does not say why: [{StatusOf(Shot(0))}] [{Shot(0)?.Name}]");
        Assert.IsTrue(FrameProbe.Showing(Pop()), "the X closed the flyout (it stays up, BAR-228)");
        Assert.IsTrue(Win32.IsWindow(forms.Hwnds[0]) && Win32.IsWindow(forms.Hwnds[1]), "a test run closed a window");

        // a window that closes while shown: its card says so at once, and the flyout is built again without it (BAR-231, 232, 312)
        at = LogLength(profile);
        Win32.PostMessageW(forms.Hwnds[1], 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        Assert.IsTrue(Host.Until(() => { log = LogSince(profile, at); return log.Contains("has closed: its card says so", StringComparison.Ordinal); }, 6000), $"no 'has closed' line for the closed window: {Tail(log)}");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()) && Card(0)?.Name == forms.Titles[0] && Card(1) == null, 5000),
            $"the flyout was not rebuilt with the one window left: card 0 [{Card(0)?.Name}], card 1 [{Card(1)?.Name}]; log: {Tail(LogSince(profile, at))}");
        // the last window gone: the flyout closes
        forms.Dispose();
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(Pop()), 6000), "the flyout stayed open after its app's windows all closed (BAR-232)");
        Host.LogContains(() => LogSince(profile, 0), "taken down (", "the log never says the pictures were taken down (BAR-311)");
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }

    [TestMethod]
    public void Resting_on_a_running_app_opens_its_previews_moving_across_swaps_them_and_leaving_closes_them()
    {
        const string profile = "uitest-strip-hover";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("hover-" + tag, ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
        using var forms = new Forms($"Hover {tag}", 1, new[] { "Red" });
        using var other = new Host.Fixture($"Hover other {tag}", 960, 60, "pwsh.exe");
        var cs = new List<Cell>();
        Cell? Alpha() { cs = Cells(app); return cs.FirstOrDefault(c => c.Pinned && c.Open && c.Name == forms.Titles[0]); }
        Cell? Beta() { cs = Cells(app); return cs.FirstOrDefault(c => c.Pinned && !c.Open); }
        Cell? Other() { cs = Cells(app); return cs.FirstOrDefault(c => !c.Pinned && c.Name == other.Title); }
        Assert.IsTrue(Host.Until(() => Alpha() != null && Beta() != null && Other() != null, 20000, 250), $"in 20 s the strip did not show Alpha (open), Beta (pin) and the running app: {Describe(cs)}");
        IntPtr Pop() => Flyout(app, "Alpha Shell");
        // a UI Automation read of the strip can miss a cell while it re-renders: read again for up to 3 s
        Cell Must(Func<Cell?> f, string what) { Cell? c = null; Host.Until(() => (c = f()) != null, 3000, 100); return c ?? throw new AssertFailedException($"{what}'s cell is not in the strip: {Describe(cs)}"); }
        var away = new Point(5, 5);
        MoveTo(away);
        Thread.Sleep(600);

        // a pin with no windows: no flyout however long the pointer rests (BAR-221)
        MoveTo(Centre(Must(Beta, "Beta").Rect));
        Thread.Sleep(1200);
        Assert.AreEqual(IntPtr.Zero, FrameProbe.All(app.Proc.Id).FirstOrDefault(w => w.Title.StartsWith("Mantle pop-up:", StringComparison.Ordinal) && FrameProbe.Showing(w.Hwnd)).Hwnd,
            "resting on a pin with no windows opened a pop-up");

        // resting on a running app: nothing at once, its previews after the 400 ms dwell (BAR-221)
        var t0 = Stopwatch.StartNew();
        MoveTo(Centre(Must(Alpha, "Alpha").Rect));
        Thread.Sleep(150);
        Assert.IsFalse(FrameProbe.Showing(Pop()), "the previews opened before the dwell");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()), 3000, 50), $"resting on a running app did not open its previews in 3 s; log: {Tail(LogSince(profile, 0))}");
        Assert.IsTrue(t0.ElapsedMilliseconds >= 380, $"the previews opened {t0.ElapsedMilliseconds} ms after the pointer arrived, before the 400 ms dwell");
        Host.LogContains(() => LogSince(profile, 0), "bar previews: Alpha Shell: opened by hover", "the log does not say the hover opened it");
        Assert.IsTrue(Host.Until(() => StatusOf(ById(Pop(), "bar.preview.0.shot")).StartsWith("live", StringComparison.Ordinal), 4000), $"the hover card has no live picture: [{StatusOf(ById(Pop(), "bar.preview.0.shot"))}]");

        // into the flyout: it stays (re-entering cancels the leave, BAR-223)
        var popRect = ById(Pop(), "bar.preview.0")!.BoundingRectangle;
        MoveTo(new Point(popRect.X + popRect.Width / 2, popRect.Y + 12));
        Thread.Sleep(1000);
        Assert.IsTrue(FrameProbe.Showing(Pop()), "moving from the icon into its flyout closed it");

        // away from both: closed 350 ms later (BAR-223)
        MoveTo(away);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(Pop()), 2000, 50), "leaving the icon and the flyout did not close it in 2 s");

        // open again, then across to another running app: its flyout at once, no dwell (BAR-222)
        MoveTo(Centre(Must(Alpha, "Alpha").Rect));
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()), 3000, 50), "the previews did not open the second time");
        IntPtr OtherPop() => FrameProbe.All(app.Proc.Id).FirstOrDefault(w => w.Title.StartsWith("Mantle pop-up:", StringComparison.Ordinal) && w.Title != "Mantle pop-up: Alpha Shell windows" && FrameProbe.Showing(w.Hwnd)).Hwnd;
        var t1 = Stopwatch.StartNew();
        MoveTo(Centre(Must(Other, "Other").Rect));
        Assert.IsTrue(Host.Until(() => OtherPop() != IntPtr.Zero, 2000, 30), "moving to another running app did not swap the flyout to its windows");
        Assert.IsTrue(t1.ElapsedMilliseconds < 1500, $"the swap took {t1.ElapsedMilliseconds} ms");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(Pop()), 2000, 50), "Alpha's flyout stayed up beside the other app's");

        // across a pin with no windows: the close starts (BAR-222)
        MoveTo(Centre(Must(Beta, "Beta").Rect));
        Assert.IsTrue(Host.Until(() => OtherPop() == IntPtr.Zero, 2000, 50), "crossing a pin with no windows did not close the flyout");
        MoveTo(away);
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }

    [TestMethod]
    public void Too_many_windows_for_cards_open_the_window_list_with_the_reason()
    {
        const string profile = "uitest-strip-toomany";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("toomany-" + tag, ("Alpha Shell", PowerShellExe));
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
        // the fewest windows whose 128 DIP cards cannot fit across this bar window (bar.js flyBuild)
        GetWindowRect(app.Hwnd, out var br);
        double scale = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        double avail = (br.Right - br.Left) / scale - 34;
        int n = 1;
        while (n * 140 + (n - 1) * 6 <= avail) n++;
        using var forms = new Forms($"Many {tag}", n, new[] { "Green" }, 300, 200);
        var cs = new List<Cell>();
        Cell? Alpha() { cs = Cells(app); return cs.FirstOrDefault(c => c.Pinned && c.Status.Contains(n + " windows", StringComparison.Ordinal)); }
        Assert.IsTrue(Host.Until(() => Alpha() != null, 30000, 250), $"in 30 s Alpha Shell's pin did not hold all {n} windows: {Describe(cs)}");
        IntPtr Pop() => Flyout(app, "Alpha Shell");
        Click(Alpha()!, app: app);
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Pop()), 4000), $"the flyout did not open; log: {ClickLines(profile)}");
        Assert.IsTrue(Host.Until(() => ById(Pop(), "bar.winlist." + (n - 1)) != null, 3000), $"{n} windows did not fall back to the list with a row each (BAR-226)");
        Assert.IsNull(ById(Pop(), "bar.preview.0"), "the list also has preview cards");
        string reason = n + " windows are too many to preview side by side in this bar, so this is the list";
        var said = Host.Uia.FromHandle(Pop()).FindAllDescendants().Select(e => { try { return e.Name ?? ""; } catch (Exception) { return ""; } }).ToList();
        Assert.IsTrue(said.Contains(reason), $"the list does not say why it is a list (\"{reason}\"); it has: [{string.Join(" | ", said.Take(8))}]");
        Host.Screenshot("preview-too-many");
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }
}
