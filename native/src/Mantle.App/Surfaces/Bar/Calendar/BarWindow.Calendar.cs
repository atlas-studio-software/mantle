using System;
using System.Globalization;
using System.Threading.Tasks;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.Calendar;
using Mantle.Core.Lifecycle;

namespace Mantle.App;

/// The clock's calendar pop-up (lane native-r7-calendar): a left or right click on the clock toggles it (bar.js
/// el.clock click / contextmenu, BAR-418), one key "cal" in the pop-up framework (Shell/Popup): instant (made once, parked
/// between opens, repainted on today's month at each open), no focus on open; after the first frame it asks for the
/// keyboard (PopupHost.TakeKeyboard, Q-W6) so the arrow keys reach the grid, and gives it back on the close.
/// A --test run never takes the keyboard unless MANTLE_TEST_CAL_KEYS=1 (with MANTLE_TEST_VISIBLE=1, a CI runner only);
/// MANTLE_TEST_CAL_NOW (test only, epoch ms) freezes the flyout's clock for the screenshot pair.
public sealed partial class BarWindow
{
    CalPanel? _cal;
    public const string CalKeysEnv = "MANTLE_TEST_CAL_KEYS", CalNowEnv = "MANTLE_TEST_CAL_NOW";

    /// Called once from the bar's start, after the pop-up framework exists (BarWindow.xaml.cs, after StartQuickSettings).
    void StartCalendar()
    {
        if (Popups == null) { Host.Log.Line("cal: the pop-up framework is not up, so the calendar does not open"); return; }
        if (Host.Args.Test && Host.Env("MANTLE_TEST_POPUPS") == "1") { Host.Log.Line("cal: MANTLE_TEST_POPUPS=1: the clock keeps the framework's stand-in \"cal\""); return; }
        var popups = Popups;
        bool keys = !Host.Args.Test || (Host.Env(CalKeysEnv) == "1" && !Host.Quiet);
        Func<DateTime> now = () => DateTime.Now;
        if (Host.Args.Test && long.TryParse(Host.Env(CalNowEnv), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)) {
            var frozen = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            now = () => frozen;
            Host.Log.Line($"cal: {CalNowEnv}: the flyout's clock is frozen at {frozen:yyyy-MM-dd HH:mm:ss}");
        }
        Func<string, Task<string?>> pressKeys = _ => Task.FromResult<string?>("the host is not attached yet");
        _cal = new CalPanel(Host.Log.Line, Host.Args.Test, now, new CalPanel.Room(
            Resize: h => popups.Resize("cal", h),
            TakeKeys: () => keys ? popups.TakeKeyboard("cal")
                : (false, "a test instance never takes the keyboard from whoever is at the machine, so the arrow keys stay where they are"),
            GiveKeys: why => popups.GiveBackKeyboard(why),
            Close: why => { if (popups.OpenKey == "cal") popups.Close(why); },
            Keys: combo => _calKeys?.Invoke(combo) ?? pressKeys(combo)));
        if (Host.Args.Test && keys) Host.Log.Line($"cal: {CalKeysEnv}=1: this test calendar may take the keyboard for the arrow keys");
        // BAR-415: the clock's tooltip is the locale long date, then "Click: the calendar" (bar.js tick), read when it opens
        var tip = new Microsoft.UI.Xaml.Controls.ToolTip { Content = now().ToString("D", CultureInfo.CurrentCulture) + "\nClick: the calendar" };
        tip.Opened +=(_, _) => tip.Content = now().ToString("D", CultureInfo.CurrentCulture) + "\nClick: the calendar";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ClockBtn, tip);
        ClockBtn.Click += (_, _) => OpenCalendar();
        ClockBtn.RightTapped += (_, e) => { e.Handled = true; OpenCalendar(); };
        popups.Anchor(ClockBtn);
        popups.Closed += (key, why) => { if (key == "cal" && _cal is { Open: true }) _cal.Closed(why); };
        popups.KeyboardLost += key => { if (key == "cal") _cal?.KeyboardLost(); };
    }

    Func<string, Task<string?>>? _calKeys;

    /// Open (or, from the clock again, close) the calendar above the clock.
    internal void OpenCalendar()
    {
        if (_cal == null || Popups == null) return;
        _popAnchor = ClockBtn;
        bool closing = Popups.OpenKey == "cal";
        double h = closing ? 0 : _cal.Prepare(Dark);
        var cal = _cal;
        Popups.Toggle(new PopupKind("cal", "Calendar", CalPanel.WidthDip, closing ? 400 : h, Focusable: false, Instant: true, Away: true, () => cal.Root), ClockBtn);
        if (Popups.OpenKey == "cal" && !cal.Open) cal.Opened();
    }

    /// From the host thread's wiring, posted here: the panel's road to the gcal.* commands and pushes, and the sidecar's
    /// key press for Windows' notification centre.
    internal void AttachCalendar(SurfaceLink link, Func<string, Task<string?>> keys)
    {
        _calKeys = keys;
        if (_cal == null) { Host.Log.Line("cal: the host is up but the bar made no calendar, so nothing is attached"); return; }
        _cal.Attach(link);
    }
}
