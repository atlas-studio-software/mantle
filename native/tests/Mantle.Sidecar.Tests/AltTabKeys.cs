// AltTabKeys.cs - Mantle's Alt+Tab key machine, order rules and hotkey refusals, driven exactly as
// Electron's lib/switcher.test.js drives them (lane r9-switcher). The native app runs the SAME
// sidecar code (sidecar/AltTab.cs + Hotkey.cs, linked here), so these are the native rows' tests:
// `hotkey {op:'simulate'}` feeds KeyMachine and SwitchMath a script of key events, `op:'order'` the
// order rules, and the passive refusals prove a test never installs a hook.
//
// Nothing here presses a key, installs a hook or activates a window: Program.Run runs passive.
//
// THE RULE CHECKED IN EVERY SCENARIO: no Alt keyup and no Win keyup is ever swallowed (a swallowed
// modifier keyup is a key Windows believes is still held down).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests {
  [TestClass]
  public class AltTabKeys {
    const string Refusal = "refused: sidecar is passive (test mode)";
    const int TAB = 0x09, ESC = 0x1B, LEFT = 0x25, UP = 0x26, RIGHT = 0x27, DOWN = 0x28, LALT = 0xA4, RALT = 0xA5,
              LWIN = 0x5B, N = 0x4E, A = 0x41, LSHIFT = 0xA0, LCTRL = 0xA2, X = 0x58, BACK = 0x08;
    static readonly HashSet<int> ModifierUps = new HashSet<int> { LALT, RALT, LWIN, 0x5C };
    static int _modUps;

    // ---- key events (the same shapes as switcher.test.js) ---------------------------------------
    static Dictionary<string, object> K(int vk, bool down, string flags = "") {
      var d = new Dictionary<string, object> { ["vk"] = (double)vk, ["down"] = down };
      foreach (string f in flags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
        bool v = !f.StartsWith("!", StringComparison.Ordinal);
        d[v ? f : f.Substring(1)] = v;
      }
      return d;
    }
    static Dictionary<string, object> AltDown => K(LALT, true, "alt");
    static Dictionary<string, object> AltUp => K(LALT, false);
    static Dictionary<string, object> Tab(string f = "") => K(TAB, true, "alt " + f);
    static Dictionary<string, object> TabUp(string f = "") => K(TAB, false, "alt " + f);
    static Dictionary<string, object> WinDown => K(LWIN, true);
    static Dictionary<string, object> WinUp => K(LWIN, false);
    static Dictionary<string, object> Do(string what, int index = -1) {
      var d = new Dictionary<string, object> { ["do"] = what };
      if (index >= 0) d["index"] = (double)index;
      return d;
    }

    static IDictionary Call(Dictionary<string, object> req, out string err) {
      object result;
      err = InProcess.Run("hotkey", req, out result);
      return result == null ? null : (IDictionary)Mantle.Json.Parse(Mantle.Json.Write(result));
    }

    static long Num(object o) => Convert.ToInt64(o, CultureInfo.InvariantCulture);
    static bool True(IDictionary d, string k) => d.Contains(k) && d[k] is bool b && b;

    /// One step as a short string: P pass, S swallow, D pass after the 0xFF partner key, I ignored.
    static string Fmt(IDictionary st) {
      string s = True(st, "ignored") ? "I" : True(st, "swallow") ? "S" : True(st, "dummy") ? "D" : "P";
      string ev = st.Contains("event") ? st["event"] as string : null;
      if (!string.IsNullOrEmpty(ev) && ev != "none") {
        s += " " + ev;
        string key = st.Contains("key") ? st["key"] as string : null;
        if (!string.IsNullOrEmpty(key)) s += ":" + key;
        else if (new[] { "open", "next", "prev", "left", "right", "up", "down", "commit" }.Contains(ev)) s += ":" + Num(st["sel"]);
      }
      return s;
    }

    static IDictionary Sim(string name, object opts, Dictionary<string, object>[] events, string[] want) {
      var req = new Dictionary<string, object> { ["op"] = "simulate", ["events"] = events.Cast<object>().ToList() };
      if (opts != null) foreach (var p in opts.GetType().GetProperties()) {
        object v = p.GetValue(opts);
        req[p.Name] = v is int i ? (object)(double)i : v;
      }
      string err;
      var r = Call(req, out err);
      Assert.IsNull(err, name + ": " + err);
      var steps = ((IList)r["steps"]).Cast<IDictionary>().ToList();
      for (int i = 0; i < events.Length; i++) {
        var e = events[i];
        if (e.ContainsKey("vk") && e["down"] is bool dn && !dn && ModifierUps.Contains((int)(double)e["vk"])) {
          _modUps++;
          Assert.IsFalse(True(steps[i], "swallow"), name + ": step " + i + ", a modifier keyup, is never swallowed");
        }
      }
      var got = steps.Select(Fmt).ToArray();
      CollectionAssert.AreEqual(want, got, name + ": got [" + string.Join(", ", got) + "]");
      return r;
    }

    // SWI-001, SWI-002
    [TestMethod]
    public void QuickTap_SwitchesToTheSecondTile_AndAltUpGetsItsPartnerKey() {
      var r = Sim("a quick tap switches to the second tile", null, new[] { AltDown, Tab(), TabUp(), AltUp },
        new[] { "P", "S open:1", "S", "D commit:1" });
      Assert.AreEqual(1L, Num(r["committed"]), "tap: committed tile 1");
      Assert.IsFalse(True(r, "active"), "tap: the session is over");
      Assert.IsFalse(True(r, "altHeld"), "tap: Alt is not held");
    }

    // SWI-003
    [TestMethod]
    public void HoldAndCycle_MovesOneTilePerTab() =>
      Sim("hold and cycle", null, new[] { AltDown, Tab(), TabUp(), Tab(), TabUp(), Tab(), TabUp(), AltUp },
        new[] { "P", "S open:1", "S", "S next:2", "S", "S next:3", "S", "D commit:3" });

    // SWI-004
    [TestMethod]
    public void Cycling_WrapsRound() =>
      Sim("cycling wraps round", new { n = 3 }, new[] { AltDown, Tab(), TabUp(), Tab(), TabUp(), Tab(), TabUp(), AltUp },
        new[] { "P", "S open:1", "S", "S next:2", "S", "S next:0", "S", "D commit:0" });

    // SWI-005
    [TestMethod]
    public void HeldTab_AutoRepeatKeepsCycling() =>
      Sim("auto-repeat of a held Tab keeps cycling", null, new[] { AltDown, Tab(), Tab(), Tab(), TabUp(), AltUp },
        new[] { "P", "S open:1", "S next:2", "S next:3", "S", "D commit:3" });

    // SWI-006
    [TestMethod]
    public void ShiftTabFirst_StartsAtTheFarEnd() =>
      Sim("Shift+Tab first starts at the far end", null,
        new[] { AltDown, K(LSHIFT, true, "alt"), Tab("shift"), TabUp(), Tab("shift"), TabUp(), K(LSHIFT, false, "alt"), AltUp },
        new[] { "P", "P", "S open:4", "S", "S prev:3", "S", "P", "D commit:3" });

    // SWI-007
    [TestMethod]
    public void TabThenShiftTab_GoesBack() =>
      Sim("Tab then Shift+Tab goes back", null, new[] { AltDown, Tab(), TabUp(), Tab("shift"), TabUp(), AltUp },
        new[] { "P", "S open:1", "S", "S prev:0", "S", "D commit:0" });

    // SWI-008, SWI-009, SWI-010, SWI-011
    [TestMethod]
    public void Arrows_OnlyWhileOpen_UpDownMoveByRows() =>
      Sim("arrows only while the switcher is open; Up/Down move by rows", new { n = 14, cols = 5 }, new[] {
        AltDown, K(RIGHT, true, "alt"), K(RIGHT, false, "alt"), Tab(),
        K(RIGHT, true, "alt"), K(RIGHT, false, "alt"),
        K(DOWN, true, "alt"), K(DOWN, false, "alt"),
        K(DOWN, true, "alt"), K(DOWN, true, "alt"), K(DOWN, false, "alt"),
        K(UP, true, "alt"), K(UP, false, "alt"),
        K(LEFT, true, "alt"), K(LEFT, false, "alt"), TabUp(), AltUp,
      }, new[] { "P", "P", "P", "S open:1", "S right:2", "S", "S down:7", "S", "S down:12", "S down:12", "S", "S up:7", "S", "S left:6", "S", "S", "D commit:6" });

    // SWI-009
    [TestMethod]
    public void DownFromAboveAShortLastRow_LandsOnItsLastTile() =>
      Sim("Down from above a short last row lands on its last tile", new { n = 14, cols = 5 }, new[] {
        AltDown, Tab(), TabUp(),
        K(RIGHT, true, "alt"), K(RIGHT, false, "alt"), K(RIGHT, true, "alt"), K(RIGHT, false, "alt"),
        K(RIGHT, true, "alt"), K(RIGHT, false, "alt"),
        K(DOWN, true, "alt"), K(DOWN, false, "alt"), K(DOWN, true, "alt"), K(DOWN, false, "alt"), AltUp,
      }, new[] { "P", "S open:1", "S", "S right:2", "S", "S right:3", "S", "S right:4", "S", "S down:9", "S", "S down:13", "S", "D commit:13" });

    // SWI-010: before main has sent the column count, Up/Down stay put.
    [TestMethod]
    public void UpDown_WithNoColumnCount_StayPut() =>
      Sim("Up/Down with no grid yet stay put", new { n = 6 }, new[] {
        AltDown, Tab(), TabUp(), K(DOWN, true, "alt"), K(DOWN, false, "alt"), K(UP, true, "alt"), K(UP, false, "alt"), AltUp,
      }, new[] { "P", "S open:1", "S", "S down:1", "S", "S up:1", "S", "D commit:1" });

    // SWI-012
    [TestMethod]
    public void EscWhileHeld_Cancels_AndAltUpStillGetsItsPartnerKey() {
      var r = Sim("Esc while held cancels", null,
        new[] { AltDown, Tab(), TabUp(), K(ESC, true, "alt"), K(ESC, false, "alt"), AltUp },
        new[] { "P", "S open:1", "S", "S cancel", "S", "D" });
      Assert.IsTrue(!r.Contains("committed") || r["committed"] == null, "Esc: nothing committed");
    }

    // SWI-012
    [TestMethod]
    public void AutoRepeat_NeverReopensWhatEscClosed() =>
      Sim("auto-repeat never reopens what Esc closed", null,
        new[] { AltDown, Tab(), K(ESC, true, "alt"), Tab(), TabUp(), K(ESC, false, "alt"), AltUp },
        new[] { "P", "S open:1", "S cancel", "S", "S", "S", "D" });

    // SWI-013
    [TestMethod]
    public void AltEsc_WithNoSwitcherOpen_IsWindowsOwn() =>
      Sim("Alt+Esc with no switcher open is Windows' own", null,
        new[] { AltDown, K(ESC, true, "alt"), K(ESC, false, "alt"), AltUp }, new[] { "P", "P", "P", "P" });

    // SWI-014
    [TestMethod]
    public void TabAfterAltReleased_IsPlain() =>
      Sim("Alt released before Tab: Tab is plain", null, new[] { AltDown, AltUp, K(TAB, true), K(TAB, false) },
        new[] { "P", "P", "P", "P" });

    // SWI-014
    [TestMethod]
    public void TabPressedBeforeAlt_StaysPlainToItsKeyUp_AndTheNextTabOpens() =>
      Sim("Tab pressed before Alt stays plain", null, new[] { K(TAB, true), AltDown, Tab(), TabUp(), Tab(), TabUp(), AltUp },
        new[] { "P", "P", "P", "P", "S open:1", "S", "D commit:1" });

    // SWI-015
    [TestMethod]
    public void WinAltTab_IsWindowsOwn_AndTheWinKeyStillWorks() =>
      Sim("Alt+Tab with Win held is Windows' own", new { winTap = true },
        new[] { WinDown, AltDown, Tab("win"), TabUp("win"), AltUp, WinUp }, new[] { "P", "P", "P", "P", "P", "P" });

    // SWI-015
    [TestMethod]
    public void CtrlAltTab_IsWindowsOwn() =>
      Sim("Ctrl+Alt+Tab (the sticky switcher) is Windows' own", null,
        new[] { K(LCTRL, true), AltDown, Tab("ctrl"), TabUp(), AltUp, K(LCTRL, false) }, new[] { "P", "P", "P", "P", "P", "P" });

    // SWI-016
    [TestMethod]
    public void InjectedKeys_AreIgnored() =>
      Sim("injected keys are ignored before the machine sees them", null,
        new[] { AltDown, Tab("injected"), TabUp("injected"), AltUp }, new[] { "P", "I", "I", "P" });

    // SWI-017
    [TestMethod]
    public void RightAlt_WorksTheSame() =>
      Sim("right Alt works the same", null, new[] { K(RALT, true, "alt"), Tab(), TabUp(), K(RALT, false) },
        new[] { "P", "S open:1", "S", "D commit:1" });

    // SWI-017
    [TestMethod]
    public void BothAlts_LettingGoOfOneDoesNotEndIt() =>
      Sim("both Alts: letting go of one does not end it", null,
        new[] { AltDown, K(RALT, true, "alt"), Tab(), TabUp(), K(RALT, false, "alt otherAlt"), AltUp },
        new[] { "P", "P", "S open:1", "S", "P", "D commit:1" });

    // SWI-019
    [TestMethod]
    public void AKeyDownSayingAltIsUp_EndsAStaleSession_AndTheNextAltTabWorks() =>
      Sim("a keydown that says Alt is up ends a stale session", null,
        new[] { AltDown, Tab(), TabUp(), K(X, true), K(X, false), AltDown, Tab(), TabUp(), AltUp },
        new[] { "P", "S open:1", "S", "P stale", "P", "P", "S open:1", "S", "D commit:1" });

    // SWI-020
    [TestMethod]
    public void Watchdog_FinishesASwitchWhoseAltUpTheHookNeverSaw() =>
      Sim("the watchdog finishes a switch", null, new[] { AltDown, Tab(), TabUp(), Do("watchdog"), AltUp },
        new[] { "P", "S open:1", "S", "P commit:1", "P" });

    // SWI-021
    [TestMethod]
    public void ClickOnATile_SwitchesToIt_AndTheAltUpStillGetsItsPartnerKey() =>
      Sim("a click on a tile switches to it", null, new[] { AltDown, Tab(), TabUp(), Do("pick", 3), AltUp },
        new[] { "P", "S open:1", "S", "P commit:3", "D" });

    // SWI-022
    [TestMethod]
    public void ClickOnATileThatIsNotThere_DoesNothing() =>
      Sim("a click on a tile that is not there does nothing", null, new[] { AltDown, Tab(), TabUp(), Do("pick", 9), AltUp },
        new[] { "P", "S open:1", "S", "P", "D commit:1" });

    // SWI-023
    [TestMethod]
    public void TurnedOff_AltTabIsWindowsOwn() =>
      Sim("turned off, Alt+Tab is Windows' own", new { altTab = false }, new[] { AltDown, Tab(), TabUp(), AltUp },
        new[] { "P", "P", "P", "P" });

    // SWI-026
    [TestMethod]
    public void WinN_SwallowsTheN_NotWin_AndStartDoesNotOpen() =>
      Sim("Win+N", new { winKeys = true, winTap = true },
        new[] { WinDown, K(N, true, "win"), K(N, false, "win"), WinUp }, new[] { "P", "S winkey:n", "S", "D" });

    // SWI-026
    [TestMethod]
    public void WinA_Likewise() =>
      Sim("Win+A likewise", new { winKeys = true },
        new[] { WinDown, K(A, true, "win"), K(A, false, "win"), WinUp }, new[] { "P", "S winkey:a", "S", "D" });

    // SWI-026
    [TestMethod]
    public void WinNHeld_OneOpen_RepeatsSwallowed() =>
      Sim("Win+N held", new { winKeys = true },
        new[] { WinDown, K(N, true, "win"), K(N, true, "win"), K(N, false, "win"), WinUp }, new[] { "P", "S winkey:n", "S", "S", "D" });

    // SWI-027
    [TestMethod]
    public void WinShiftA_And_WinCtrlN_StayWindowsOwn() =>
      Sim("Win+Shift+A and Win+Ctrl+N stay Windows'", new { winKeys = true }, new[] {
        WinDown, K(LSHIFT, true), K(A, true, "win shift"), K(A, false), K(LSHIFT, false),
        K(LCTRL, true), K(N, true, "win ctrl"), K(N, false), K(LCTRL, false), WinUp,
      }, Enumerable.Repeat("P", 10).ToArray());

    // SWI-027
    [TestMethod]
    public void WinKeysOff_WinNIsWindowsOwn() =>
      Sim("with Win+N/A off, they are Windows'", null,
        new[] { WinDown, K(N, true, "win"), K(N, false, "win"), WinUp }, new[] { "P", "P", "P", "P" });

    // SWI-027
    [TestMethod]
    public void AltN_IsNotWinN() =>
      Sim("Alt+N is not Win+N", new { winKeys = true },
        new[] { AltDown, K(N, true, "alt"), K(N, false, "alt"), AltUp }, new[] { "P", "P", "P", "P" });

    // The launcher's Win tap shares the machine (its rows are LAU's); kept here because Electron's
    // suite counts their two Win keyups in the 33 below.
    [TestMethod]
    public void SoloWinTap_GetsItsPartnerKeyBeforeTheWinUp() =>
      Sim("a solo Win tap opens the launcher (partner key before the Win-up)", new { winTap = true },
        new[] { WinDown, WinUp }, new[] { "P", "D wintap" });

    [TestMethod]
    public void WinTapOff_WinIsUntouched() =>
      Sim("with the Win tap off, Win is untouched", null, new[] { WinDown, WinUp }, new[] { "P", "P" });

    // Safety (PLAN §7, lane r9-switcher): the kill switch Ctrl+Alt+Shift+Backspace is never swallowed,
    // with or without a switcher open and with every switch and chord on. Not an Electron scenario.
    static readonly string[] AllChords = {
      "Win+S", "Win+V", "Win+Tab", "Win+Left", "Win+Right", "Win+Up", "Win+Down", "Win+Z",
      "Win+Shift+S", "PrintScreen", "VolumeUp", "VolumeDown", "VolumeMute", "Win+Alt+T", "Win+Ctrl+T", "Win+Alt+N", "Win+I",
      "Ctrl+Shift+Esc", "Win+E", "Win+R", "Win+X", "Win+G", "Win+P", "Win+.", "Win+;",
    };

    [TestMethod]
    public void KillSwitch_PassesThrough_WhileTheSwitcherIsOpen() =>
      Sim("the kill switch passes while the switcher is open", new { winTap = true, winKeys = true, chords = AllChords }, new[] {
        AltDown, Tab(), TabUp(),
        K(LCTRL, true, "alt ctrl"), K(LSHIFT, true, "alt ctrl shift"), K(BACK, true, "alt ctrl shift"),
        K(BACK, false, "alt ctrl shift"), K(LSHIFT, false, "alt ctrl"), K(LCTRL, false, "alt"), AltUp,
      }, new[] { "P", "S open:1", "S", "P", "P", "P", "P", "P", "P", "D commit:1" });

    [TestMethod]
    public void KillSwitch_PassesThrough_WithNoSwitcherOpen() =>
      Sim("the kill switch passes with no switcher open", new { winTap = true, winKeys = true, chords = AllChords }, new[] {
        K(LCTRL, true, "ctrl"), AltDown, K(LSHIFT, true, "alt ctrl shift"), K(BACK, true, "alt ctrl shift"),
        K(BACK, false, "alt ctrl shift"), K(LSHIFT, false, "alt ctrl"), AltUp, K(LCTRL, false),
      }, new[] { "P", "P", "P", "P", "P", "P", "P", "P" });

    // The hand-back (PLAN §7): with Alt+Tab off nothing of an Alt+Tab is swallowed, whatever else is on,
    // and Alt+Shift+Tab and the arrows too. Main sends altTab false when the feature is off, its page is
    // down (unhealthy) or Mantle rests (Mantle.Core SwitcherHook.Want, its own tests).
    [TestMethod]
    public void HandBack_AltTabOff_EveryAltTabKeyIsWindowsOwn() =>
      Sim("Alt+Tab off: every Alt+Tab key is Windows' own", new { altTab = false, winTap = true, winKeys = true, n = 14, cols = 5, chords = AllChords }, new[] {
        AltDown, Tab(), TabUp(), Tab("shift"), TabUp("shift"), K(RIGHT, true, "alt"), K(RIGHT, false, "alt"),
        K(DOWN, true, "alt"), K(DOWN, false, "alt"), K(ESC, true, "alt"), K(ESC, false, "alt"), AltUp,
      }, Enumerable.Repeat("P", 12).ToArray());

    // SWI-025: the keys the switcher does NOT handle (no Delete, digits, Enter, Space, Home/End, Alt+F4,
    // type-to-search) pass straight through while it is open, raise no event and leave the session up.
    // Not an Electron scenario (the row cites none).
    [TestMethod]
    public void KeysTheSwitcherDoesNotHandle_PassWhileItIsOpen() {
      var keys = new[] { 0x2E, 0x31, 0x0D, 0x20, 0x24, 0x23, 0x73, 0x51 };   // Delete, 1, Enter, Space, Home, End, F4, Q
      var events = new List<Dictionary<string, object>> { AltDown, Tab(), TabUp() };
      var want = new List<string> { "P", "S open:1", "S" };
      foreach (int vk in keys) { events.Add(K(vk, true, "alt")); events.Add(K(vk, false, "alt")); want.Add("P"); want.Add("P"); }
      events.Add(AltUp); want.Add("D commit:1");
      var r = Sim("keys the switcher does not handle pass", null, events.ToArray(), want.ToArray());
      Assert.AreEqual(1L, Num(r["committed"]), "the session stayed up to the Alt-up");
    }

    // SWI-018: every scenario above, run again in one go, must have exercised at least the 33
    // modifier keyups Electron's suite counts (fewer means scenarios stopped running).
    [TestMethod]
    public void EveryModifierKeyUp_InEveryScenario_PassedThrough() {
      _modUps = 0;
      foreach (var m in typeof(AltTabKeys).GetMethods()) {
        if (m.Name == nameof(EveryModifierKeyUp_InEveryScenario_PassedThrough)) continue;
        if (m.GetCustomAttributes(typeof(TestMethodAttribute), false).Length == 0) continue;
        if (m.Name.StartsWith("Order_", StringComparison.Ordinal) || m.Name.StartsWith("Refuses_", StringComparison.Ordinal)) continue;
        m.Invoke(this, null);
      }
      Assert.IsTrue(_modUps >= 33, "only " + _modUps + " modifier keyups were exercised (Electron's suite has 33)");
    }

    // ---- order (SWI-030, SWI-031, SWI-032) ------------------------------------------------------
    static void Order(string name, string fg, object[] windows, string[] want, int sel, int selBack) {
      var req = new Dictionary<string, object> { ["op"] = "order", ["fg"] = fg, ["windows"] = windows.Select(w => {
        var d = new Dictionary<string, object>();
        foreach (var p in w.GetType().GetProperties()) { object v = p.GetValue(w); d[p.Name] = v is int i ? (object)(double)i : v; }
        return (object)d;
      }).ToList() };
      string err;
      var r = Call(req, out err);
      Assert.IsNull(err, name + ": " + err);
      var got = ((IList)r["order"]).Cast<object>().Select(o => o.ToString()).ToArray();
      CollectionAssert.AreEqual(want, got, name + ": order [" + string.Join(",", got) + "]");
      Assert.AreEqual(sel, Num(r["sel"]), name + ": selection");
      Assert.AreEqual(selBack, Num(r["selBackwards"]), name + ": selection backwards");
    }
    static readonly object[] W = {
      new { hwnd = "10", z = 0, stamp = 0 },
      new { hwnd = "20", z = 1, stamp = 50 },
      new { hwnd = "30", z = 2, stamp = 90 },
      new { hwnd = "40", z = 3, stamp = 0, topmost = true },
      new { hwnd = "50", z = 4, stamp = 0 },
    };

    [TestMethod]
    public void Order_CurrentFirst_ThenMostRecent_ThenStacking_TopmostLast() {
      Order("the current window first, then most recently used, then stacking order, topmost last", "10", W, new[] { "10", "30", "20", "50", "40" }, 1, 4);
      Order("the current window first even when it is not the most recent", "20", W, new[] { "20", "30", "10", "50", "40" }, 1, 4);
    }

    [TestMethod]
    public void Order_DesktopInFront_MostRecentIsFirstAndSelected() =>
      Order("with the desktop in front", "99", W, new[] { "30", "20", "10", "50", "40" }, 0, 4);

    [TestMethod]
    public void Order_OneWindow_AndNoWindows() {
      Order("one window", "10", new[] { W[0] }, new[] { "10" }, 0, 0);
      Order("no windows", "10", new object[0], new string[0], -1, -1);
    }

    // ---- refusals (SWI-142, SWI-145) -------------------------------------------------------------
    static void Refuses(string name, Dictionary<string, object> req, Func<string, IDictionary, bool> test) {
      string err;
      var r = Call(req, out err);
      Assert.IsTrue(test(err, r), name + ": error " + (err ?? "null"));
    }

    [TestMethod]
    public void Refuses_InPassive_AndMalformedRequestsInEveryMode() {
      Refuses("passive refuses to install Alt+Tab", new Dictionary<string, object> { ["altTab"] = true }, (e, r) => e == Refusal);
      Refuses("passive refuses to install Win+N / Win+A", new Dictionary<string, object> { ["winKeys"] = true }, (e, r) => e == Refusal);
      Refuses("passive refuses a pick, and says why", new Dictionary<string, object> { ["op"] = "pick", ["seq"] = 1.0, ["index"] = 0.0 },
        (e, r) => e != null && e.StartsWith(Refusal + ": pick acts on a live Alt+Tab", StringComparison.Ordinal));
      Refuses("a malformed grid is malformed in every mode", new Dictionary<string, object> { ["op"] = "grid", ["seq"] = 1.0, ["cols"] = 0.0 },
        (e, r) => e != null && e.Contains("grid needs cols"));
      Refuses("a switch must be a boolean", new Dictionary<string, object> { ["altTab"] = "yes" },
        (e, r) => e == "hotkey: altTab must be true or false, not \"yes\"");
      Refuses("hotkey with nothing to do says what it needs", new Dictionary<string, object>(),
        (e, r) => e == "hotkey needs winTap, altTab or winKeys: true or false");
      Refuses("an unknown op is named", new Dictionary<string, object> { ["op"] = "nope" }, (e, r) => e != null && e.Contains("unknown op 'nope'"));
      Refuses("status: no hook in a passive sidecar", new Dictionary<string, object> { ["op"] = "status" },
        (e, r) => e == null && r != null && !True(r, "installed") && !True(r, "altTab") && !True(r, "winKeys"));
    }
  }
}
