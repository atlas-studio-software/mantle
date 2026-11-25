// SnapPlanTests.cs (lane r9-snap) - the Win+arrow state machine and the zone geometry of sidecar/Snap.cs,
// called in-process through the sidecar's own dispatcher (`snap.plan`, pure: made-up monitors, no window is
// touched). The native app reuses Snap.cs as source (PLAN A4), so this IS the native snap geometry; the cases
// and every expected rect are sidecar/test-snap.js's pure half ("the geometry, by hand" and "odd work areas,
// apps Windows scales"), worked out here by the same hand-written arithmetic, never by asking the sidecar twice.
// Parity rows: SNAP-005..035 (the plan half of 007/008/011/025/026/034), 046 (zone refusals), 079 (assist complements).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests.Snap {
  [TestClass]
  public class SnapPlanTests {
    // ------------------------------------------------------------ the harness
    internal sealed class R {
      public bool Ok; public string Error; public JsonElement Result;
      public string S(string k) => Result.ValueKind == JsonValueKind.Object && Result.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
      public JsonElement P(string k) => Result.ValueKind == JsonValueKind.Object && Result.TryGetProperty(k, out var v) ? v : default;
      public override string ToString() => Ok ? Result.GetRawText().Substring(0, Math.Min(300, Result.GetRawText().Length)) : "error: " + Error;
    }

    internal static R Plan(JsonObject args) {
      var req = (Dictionary<string, object>)Mantle.Json.Parse(args.ToJsonString());
      object result;
      string err = InProcess.Run("snap.plan", req, out result);
      if (err != null) return new R { Ok = false, Error = err };
      using var doc = JsonDocument.Parse(Mantle.Json.Write(result));
      return new R { Ok = true, Result = doc.RootElement.Clone() };
    }

    static JsonArray A(params int[] v) { var a = new JsonArray(); foreach (int x in v) a.Add(x); return a; }
    static JsonObject Mon(string id, int[] bounds, int[] work, int dpi, bool primary = false) =>
      new JsonObject { ["id"] = id, ["bounds"] = A(bounds), ["work"] = A(work), ["dpi"] = dpi, ["primary"] = primary };
    static JsonArray Mons(params JsonObject[] m) { var a = new JsonArray(); foreach (var x in m) a.Add(x); return a; }

    // half(o, s, n, d) = o + floor((2 s n + d) / (2 d)): the one half-up rounding every edge uses
    static int Half(int o, int s, int n, int d) => o + (2 * s * n + d) / (2 * d);
    internal static int[] Cell(int[] work, int cols, int rows, int x, int y, int w, int h) {
      int l = work[0], t = work[1], W = work[2] - work[0], H = work[3] - work[1];
      return new[] { Half(l, W, x, cols), Half(t, H, y, rows), Half(l, W, x + w, cols), Half(t, H, y + h, rows) };
    }
    static readonly Dictionary<string, int[]> Zones = new Dictionary<string, int[]> {
      ["left"] = new[] { 2, 1, 0, 0, 1, 1 }, ["right"] = new[] { 2, 1, 1, 0, 1, 1 },
      ["tl"] = new[] { 2, 2, 0, 0, 1, 1 }, ["tr"] = new[] { 2, 2, 1, 0, 1, 1 },
      ["bl"] = new[] { 2, 2, 0, 1, 1, 1 }, ["br"] = new[] { 2, 2, 1, 1, 1, 1 },
    };
    static int[] Zone(int[] work, string name) { var z = Zones[name]; return Cell(work, z[0], z[1], z[2], z[3], z[4], z[5]); }

    /// {x,y,w,h} of the answer vs an [l,t,r,b] expectation
    static bool Same(JsonElement got, int[] want, int tol = 0) {
      if (got.ValueKind != JsonValueKind.Object || want == null) return false;
      int[] w = { want[0], want[1], want[2] - want[0], want[3] - want[1] };
      string[] k = { "x", "y", "w", "h" };
      for (int i = 0; i < 4; i++) {
        if (!got.TryGetProperty(k[i], out var v) || v.ValueKind != JsonValueKind.Number) return false;
        if (Math.Abs(v.GetInt32() - w[i]) > tol) return false;
      }
      return true;
    }
    static int[] Ltrb(JsonElement r) {
      int x = r.GetProperty("x").GetInt32(), y = r.GetProperty("y").GetInt32();
      return new[] { x, y, x + r.GetProperty("w").GetInt32(), y + r.GetProperty("h").GetInt32() };
    }
    static string Fmt(JsonElement r) => r.ValueKind == JsonValueKind.Object ? r.GetRawText() : "(none)";

    // ------------------------------------------------------------ one 1920x1080 monitor at 100 %
    static readonly int[] OneWork = { 0, 0, 1920, 1040 };
    static JsonArray One() => Mons(Mon("A", new[] { 0, 0, 1920, 1080 }, OneWork, 96, true));
    static readonly int[] Frame = { 100, 100, 900, 700 }, Win = { 93, 100, 907, 707 };   // 7/0/7/7: a 100 % standard frame

    static R P1(string dir, Action<JsonObject> more = null) {
      var a = new JsonObject { ["monitors"] = One(), ["frame"] = A(Frame), ["window"] = A(Win), ["dir"] = dir };
      more?.Invoke(a);
      return Plan(a);
    }
    static R At(int[] f, string dir) =>
      Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(f), ["window"] = A(f[0] - 7, f[1], f[2] + 7, f[3] + 7), ["dir"] = dir });

    [TestMethod] // SNAP-005, SNAP-032
    public void WinLeft_on_a_normal_window_is_the_left_half_of_the_work_area_with_the_borders_added_back() {
      var r = P1("left");
      Assert.IsTrue(r.Ok && r.S("action") == "snap" && Same(r.P("frame"), new[] { 0, 0, 960, 1040 }), r.ToString());
      Assert.IsTrue(Same(r.P("window"), new[] { -7, 0, 967, 1047 }) && r.P("marginsOk").ValueKind == JsonValueKind.True, "window " + Fmt(r.P("window")));
    }

    [TestMethod] // SNAP-079 (the complement of a half is the other half)
    public void Snap_Assist_is_offered_the_other_half() {
      var r = P1("left");
      var assist = r.P("assist");
      Assert.IsTrue(r.Ok && assist.GetArrayLength() == 1 && Same(assist[0].GetProperty("frame"), new[] { 960, 0, 1920, 1040 }), r.ToString());
    }

    [TestMethod] // SNAP-006
    public void WinRight_is_the_right_half() {
      var r = P1("right");
      Assert.IsTrue(r.Ok && Same(r.P("frame"), new[] { 960, 0, 1920, 1040 }), r.ToString());
    }

    [TestMethod] // SNAP-007, SNAP-008 (the plan half; the move itself is sidecar/test-snap.js's live half)
    public void WinUp_maximizes_and_WinDown_minimizes_a_normal_window() {
      var up = P1("up");
      Assert.IsTrue(up.Ok && up.S("action") == "maximize" && up.S("to") == "max", up.ToString());
      var down = P1("down");
      Assert.IsTrue(down.Ok && down.S("action") == "minimize", down.ToString());
    }

    [TestMethod] // SNAP-009, SNAP-010, SNAP-011
    public void A_maximized_window_restores_on_Down_snaps_on_Left_and_does_nothing_on_Up_saying_why() {
      var down = P1("down", a => a["maximized"] = true);
      Assert.IsTrue(down.Ok && down.S("action") == "restore" && down.S("to") == "normal", down.ToString());
      var up = P1("up", a => a["maximized"] = true);
      Assert.IsTrue(up.Ok && up.S("action") == "none" && (up.S("why") ?? "").Contains("already maximized"), up.ToString());
      var left = P1("left", a => a["maximized"] = true);
      Assert.IsTrue(left.Ok && left.S("action") == "snap" && left.S("to") == "left", left.ToString());
    }

    [TestMethod] // SNAP-012
    public void A_minimized_window_is_left_alone() {
      var r = P1("left", a => a["minimized"] = true);
      Assert.IsTrue(r.Ok && r.S("action") == "none" && (r.S("why") ?? "").Contains("minimized"), r.ToString());
    }

    [TestMethod] // SNAP-046
    public void A_bad_direction_is_none_with_the_reason() {
      var r = P1("sideways");
      Assert.IsTrue(r.Ok && r.S("action") == "none" && (r.S("why") ?? "").Contains("left, right, up or down"), r.ToString());
    }

    [TestMethod] // SNAP-015..020: the quarter ladder, from frames that ARE the zones (recognised by position)
    public void The_quarter_ladder_walks_like_Windows() {
      int[] L = Zone(OneWork, "left"), TL = Zone(OneWork, "tl"), BL = Zone(OneWork, "bl"), TR = Zone(OneWork, "tr"), BR = Zone(OneWork, "br"), RT = Zone(OneWork, "right");
      var ladder = new (int[] f, string dir, string to, int[] want)[] {
        (L, "up", "tl", TL), (L, "down", "bl", BL), (TL, "down", "left", L), (BL, "up", "left", L),
        (TL, "right", "tr", TR), (TR, "left", "tl", TL), (BL, "right", "br", BR), (BR, "left", "bl", BL),
        (TR, "down", "right", RT), (BR, "up", "right", RT),
      };
      var bad = new List<string>();
      foreach (var (f, dir, to, want) in ladder) {
        var r = At(f, dir);
        if (!(r.Ok && r.S("to") == to && Same(r.P("frame"), want))) bad.Add($"{r.S("from")} + Win+{dir} -> {to}: got {r}");
      }
      var tlUp = At(TL, "up");
      if (!(tlUp.Ok && tlUp.S("action") == "maximize")) bad.Add("top-left quarter + Win+Up should maximize: " + tlUp);
      var blDown = At(BL, "down");
      if (!(blDown.Ok && blDown.S("action") == "minimize")) bad.Add("bottom-left quarter + Win+Down should minimize: " + blDown);
      var back = At(TL, "down");
      if (!(back.Ok && back.P("assist").GetArrayLength() == 1 && Same(back.P("assist")[0].GetProperty("frame"), RT))) bad.Add("tl -> left: assist should be the right half: " + back);
      var lUp = Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(L), ["dir"] = "up" });
      if (!(lUp.Ok && Same(lUp.P("assist")[0].GetProperty("frame"), BL))) bad.Add("left -> tl: assist should be the bottom-left quarter: " + lUp);
      Assert.AreEqual(0, bad.Count, string.Join("\n", bad));
    }

    [TestMethod] // SNAP-029
    public void A_frame_2px_off_a_half_still_counts_as_that_half_and_3px_off_is_a_normal_window() {
      int[] L = Zone(OneWork, "left");
      var two = At(new[] { L[0] + 2, L[1], L[2] - 2, L[3] + 2 }, "up");
      Assert.IsTrue(two.Ok && two.S("from") == "left", two.ToString());
      var three = At(new[] { L[0] + 3, L[1], L[2], L[3] }, "up");
      Assert.IsTrue(three.Ok && three.S("from") == "normal" && three.S("action") == "maximize", three.ToString());
    }

    [TestMethod] // SNAP-014, SNAP-025: un-snap puts the remembered pre-snap frame back exactly
    public void Left_half_plus_WinRight_puts_the_remembered_pre_snap_frame_back_exactly() {
      int[] L = Zone(OneWork, "left");
      var r = Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(L), ["dir"] = "right",
        ["remembered"] = new JsonObject { ["zone"] = "left", ["frame"] = A(L), ["pre"] = A(Frame), ["preMonitor"] = "A" } });
      Assert.IsTrue(r.Ok && r.S("action") == "unsnap" && r.S("restoredFrom") == "remembered" && Same(r.P("frame"), Frame), r.ToString());
    }

    [TestMethod] // SNAP-026 (Windows' restore rect is the fallback), SNAP-027 (else a centred two thirds)
    public void Unsnap_falls_back_to_Windows_restore_rect_then_to_a_centred_two_thirds() {
      var win = Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(Zone(OneWork, "right")), ["dir"] = "left", ["placement"] = A(300, 200, 1100, 800) });
      Assert.IsTrue(win.Ok && win.S("restoredFrom") == "windows" && Same(win.P("frame"), new[] { 300, 200, 1100, 800 }), win.ToString());
      var mid = Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(Zone(OneWork, "right")), ["dir"] = "left" });
      Assert.IsTrue(mid.Ok && mid.S("restoredFrom") == "centered" && Same(mid.P("frame"), new[] { 320, 173, 1600, 866 }), mid.ToString());
    }

    [TestMethod] // SNAP-021
    public void WinDown_on_a_WinZ_third_goes_back_to_the_pre_snap_frame() {
      int[] g3 = Cell(OneWork, 3, 1, 1, 0, 1, 1);
      var r = Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(g3), ["dir"] = "down",
        ["remembered"] = new JsonObject { ["zone"] = "grid:3x1:1,0,1,1", ["frame"] = A(g3), ["pre"] = A(Frame), ["preMonitor"] = "A" } });
      Assert.IsTrue(r.Ok && r.S("from") == "grid:3x1:1,0,1,1" && r.S("action") == "unsnap" && Same(r.P("frame"), Frame), r.ToString());
    }

    [TestMethod] // SNAP-021 (lane r9-snap): Left/Right from a Win+Z third snap it to that half, Up maximizes
    public void A_WinZ_third_snaps_to_a_half_on_Left_or_Right_and_maximizes_on_Up() {
      int[] g3 = Cell(OneWork, 3, 1, 1, 0, 1, 1);
      R From(string dir) => Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(g3), ["dir"] = dir,
        ["remembered"] = new JsonObject { ["zone"] = "grid:3x1:1,0,1,1", ["frame"] = A(g3), ["pre"] = A(Frame), ["preMonitor"] = "A" } });
      var l = From("left");
      Assert.IsTrue(l.Ok && l.S("action") == "snap" && l.S("to") == "left" && Same(l.P("frame"), Zone(OneWork, "left")), l.ToString());
      var rt = From("right");
      Assert.IsTrue(rt.Ok && rt.S("to") == "right" && Same(rt.P("frame"), Zone(OneWork, "right")), rt.ToString());
      var up = From("up");
      Assert.IsTrue(up.Ok && up.S("action") == "maximize" && up.S("to") == "max", up.ToString());
    }

    // ------------------------------------------------------------ exact tiling on awkward sizes (SNAP-030)
    static readonly int[] OddWork = { 0, 0, 1919, 1031 };
    static JsonArray Odd() => Mons(Mon("O", new[] { 0, 0, 1919, 1079 }, OddWork, 96, true));
    static R OddPlan(int[] grid, int[] cell) =>
      Plan(new JsonObject { ["monitors"] = Odd(), ["frame"] = A(10, 10, 500, 500), ["grid"] = A(grid), ["cell"] = A(cell) });
    static int[] OddFrame(int[] grid, int[] cell) { var r = OddPlan(grid, cell); Assert.IsTrue(r.Ok, r.ToString()); return Ltrb(r.P("frame")); }

    [TestMethod] // SNAP-030
    public void Zones_on_an_odd_work_area_tile_with_one_shared_edge_never_a_gap_or_an_overlap() {
      int[] a = OddFrame(new[] { 2, 1 }, new[] { 0, 0, 1, 1 }), b = OddFrame(new[] { 2, 1 }, new[] { 1, 0, 1, 1 });
      Assert.IsTrue(a[2] == b[0] && a[2] - a[0] == 960 && b[2] == 1919, "halves of 1919");
      int[] t1 = OddFrame(new[] { 3, 1 }, new[] { 0, 0, 1, 1 }), t2 = OddFrame(new[] { 3, 1 }, new[] { 1, 0, 1, 1 }), t3 = OddFrame(new[] { 3, 1 }, new[] { 2, 0, 1, 1 });
      Assert.IsTrue(t1[2] == t2[0] && t2[2] == t3[0] && t3[2] == 1919 && t1[2] - t1[0] == 640 && t2[2] - t2[0] == 639, "thirds of 1919 (640 | 639 | 640)");
      int[] two = OddFrame(new[] { 3, 1 }, new[] { 0, 0, 2, 1 });
      Assert.AreEqual(t3[0], two[2], "2/3 + 1/3 meet on the same edge as the thirds");
      var q = new[] { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 } }.Select(p => OddFrame(new[] { 2, 2 }, new[] { p[0], p[1], 1, 1 })).ToArray();
      Assert.IsTrue(q[0][2] == q[1][0] && q[0][3] == q[2][1] && q[3][2] == 1919 && q[3][3] == 1031, "quarters of an odd work area tile with no gap");
    }

    [TestMethod] // SNAP-022, SNAP-046
    public void A_grid_cell_that_is_a_half_is_named_left_and_a_cell_outside_its_grid_is_refused_in_words() {
      var r = OddPlan(new[] { 4, 2 }, new[] { 0, 0, 2, 2 });
      Assert.IsTrue(r.Ok && r.S("to") == "left", r.ToString());
      var bad = OddPlan(new[] { 3, 1 }, new[] { 2, 0, 2, 1 });
      Assert.IsTrue(!bad.Ok && (bad.Error ?? "").Contains("do not fit"), bad.ToString());
    }

    // ------------------------------------------------------------ 125 % with a top app bar (SNAP-031..033)
    static readonly int[] HiWork = { 0, 40, 2560, 1552 };
    static JsonArray Hi() => Mons(Mon("H", new[] { 0, 0, 2560, 1600 }, HiWork, 120, true));

    [TestMethod] // SNAP-031, SNAP-032
    public void Zones_start_below_a_top_app_bar_and_carry_the_125_percent_borders() {
      var r = Plan(new JsonObject { ["monitors"] = Hi(), ["frame"] = A(200, 200, 1200, 900), ["window"] = A(191, 200, 1209, 909), ["dir"] = "left" });
      Assert.IsTrue(r.Ok && Same(r.P("frame"), new[] { 0, 40, 1280, 1552 }), r.ToString());
      Assert.IsTrue(Same(r.P("window"), new[] { -9, 40, 1289, 1561 }), "window " + Fmt(r.P("window")));
      var tl = Plan(new JsonObject { ["monitors"] = Hi(), ["frame"] = A(Zone(HiWork, "left")), ["dir"] = "up" });
      Assert.IsTrue(tl.Ok && Same(tl.P("frame"), new[] { 0, 40, 1280, 796 }), "top-left quarter under a top bar: " + tl);
    }

    [TestMethod] // SNAP-033
    public void Impossible_borders_are_flagged_and_treated_as_none() {
      var r = Plan(new JsonObject { ["monitors"] = Hi(), ["frame"] = A(200, 200, 1200, 900), ["window"] = A(150, 100, 1300, 1000), ["dir"] = "left" });
      Assert.IsTrue(r.Ok && r.P("marginsOk").ValueKind == JsonValueKind.False, r.ToString());
      Assert.AreEqual(Fmt(r.P("frame")), Fmt(r.P("window")), "with impossible borders the window rect IS the frame");
    }

    // ------------------------------------------------------------ three monitors: 100 % left, 125 % primary, 150 % right (SNAP-013/016/017/023/028/034)
    static readonly int[] AW = { -1920, 0, 0, 1040 }, BW = { 0, 0, 2560, 1552 }, CW = { 2560, -200, 5440, 1372 };
    static JsonArray Three() => Mons(
      Mon("A", new[] { -1920, 0, 0, 1080 }, AW, 96),
      Mon("B", new[] { 0, 0, 2560, 1600 }, BW, 120, true),
      Mon("C", new[] { 2560, -200, 5440, 1420 }, CW, 144));

    [TestMethod] // SNAP-013, SNAP-034 (borders scale 9 -> 7 across DPI)
    public void Left_half_plus_WinLeft_goes_to_the_right_half_of_the_monitor_to_the_left_scaling_the_borders() {
      int[] onB = Zone(BW, "left");
      var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(onB), ["window"] = A(onB[0] - 9, onB[1], onB[2] + 9, onB[3] + 9), ["dir"] = "left" });
      Assert.IsTrue(r.Ok && r.S("monitor") == "A" && r.S("to") == "right" && Same(r.P("frame"), Zone(AW, "right")), r.ToString());
      var m = r.P("margins");
      Assert.IsTrue(m.GetProperty("l").GetInt32() == 7 && m.GetProperty("b").GetInt32() == 7, "margins " + Fmt(m));
    }

    [TestMethod] // SNAP-013, SNAP-016, SNAP-023: wrap round like Windows
    public void WinLeft_and_WinRight_wrap_from_the_outermost_monitors() {
      var l = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(Zone(AW, "left")), ["dir"] = "left" });
      Assert.IsTrue(l.Ok && l.S("monitor") == "C" && l.S("to") == "right" && Same(l.P("frame"), Zone(CW, "right")), "leftmost Win+Left: " + l);
      var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(Zone(CW, "right")), ["dir"] = "right" });
      Assert.IsTrue(r.Ok && r.S("monitor") == "A" && r.S("to") == "left", "rightmost Win+Right: " + r);
    }

    [TestMethod] // SNAP-017
    public void A_top_left_quarter_walks_to_the_top_right_quarter_of_the_monitor_to_the_left() {
      var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(Zone(BW, "tl")), ["dir"] = "left" });
      Assert.IsTrue(r.Ok && r.S("monitor") == "A" && r.S("to") == "tr", r.ToString());
    }

    [TestMethod] // SNAP-018, SNAP-019, SNAP-020 (lane r9-snap): the other quarters' cross-monitor steps, which test-snap.js does not walk
    public void The_other_quarters_walk_to_the_mirrored_quarter_of_the_neighbour_monitor_and_wrap() {
      var bad = new List<string>();
      void Step(int[] from, string dir, string mon, string to, int[] want) {
        var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(from), ["dir"] = dir });
        if (!(r.Ok && r.S("monitor") == mon && r.S("to") == to && Same(r.P("frame"), want))) bad.Add($"Win+{dir} -> {mon}/{to}: got {r}");
      }
      Step(Zone(BW, "tr"), "right", "C", "tl", Zone(CW, "tl"));   // SNAP-018: tr + Win+Right -> tl of the monitor to the right
      Step(Zone(BW, "bl"), "left", "A", "br", Zone(AW, "br"));    // SNAP-019: bl + Win+Left -> br of the monitor to the left
      Step(Zone(BW, "br"), "right", "C", "bl", Zone(CW, "bl"));   // SNAP-020: br + Win+Right -> bl of the monitor to the right
      Step(Zone(CW, "tr"), "right", "A", "tl", Zone(AW, "tl"));   // from the rightmost it wraps to the leftmost
      Step(Zone(AW, "bl"), "left", "C", "br", Zone(CW, "br"));    // from the leftmost it wraps to the rightmost
      Assert.AreEqual(0, bad.Count, string.Join("\n", bad));
    }

    [TestMethod] // SNAP-028: the pre-snap frame is carried to the monitor the window is on now (same offset, DPI-scaled size, kept there)
    public void The_pre_snap_frame_is_carried_onto_the_current_monitor_scaled_for_DPI() {
      int[] rA = Zone(AW, "right");
      var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(rA), ["dir"] = "left",
        ["remembered"] = new JsonObject { ["zone"] = "right", ["frame"] = A(rA), ["pre"] = A(500, 300, 1500, 1100), ["preMonitor"] = "B" } });
      Assert.IsTrue(r.Ok && r.S("action") == "unsnap" && r.S("monitor") == "A" &&
        Same(r.P("frame"), new[] { -1920 + 400, 240, -1920 + 400 + 800, 240 + 640 }), r.ToString());
    }

    // ------------------------------------------------------------ un-snap never strands a window (lane r11-snap-strand)
    // SnapGeom.Translate now fits a frame whose monitor is gone, and one on the same monitor whose title bar is off its
    // work area, by the rule of SnapSafety.TitleBarOn (SnapGeom.TitleBarOn is its twin): the top 32 DIP inside the work
    // area (2 px slack) and min(width, 64 DIP) of it across, DIP to px = (dip * dpi + 48) / 96. sidecar/test-snap.js
    // has the same cases.
    static R UnsnapLeft(JsonArray mons, int[] work, int[] pre, string preMonitor) {
      int[] l = Zone(work, "left");
      return Plan(new JsonObject { ["monitors"] = mons, ["frame"] = A(l), ["dir"] = "right",
        ["remembered"] = new JsonObject { ["zone"] = "left", ["frame"] = A(l), ["pre"] = A(pre), ["preMonitor"] = preMonitor } });
    }

    [TestMethod] // SNAP-028 (never strands): the remembered monitor is gone
    public void Unsnap_whose_remembered_monitor_is_gone_fits_the_frame_onto_the_current_work_area() {
      // [2200,150,3000,750] (800x600) was on a monitor that is unplugged: preMon is null. Fit onto A's 0..1920 x 0..1040:
      // left = max(0, min(2200, 1920-800=1120)) = 1120, top = max(0, min(150, 1040-600=440)) = 150.
      var r = UnsnapLeft(One(), OneWork, new[] { 2200, 150, 3000, 750 }, "GONE");
      Assert.IsTrue(r.Ok && r.S("action") == "unsnap" && r.S("restoredFrom") == "remembered" && r.S("monitor") == "A" &&
        Same(r.P("frame"), new[] { 1120, 150, 1920, 750 }), r.ToString());
      // 2560x1600 is bigger than the work area: shrunk to 1920x1040 at 0,0
      var big = UnsnapLeft(One(), OneWork, new[] { 1920, -100, 4480, 1500 }, "GONE");
      Assert.IsTrue(big.Ok && Same(big.P("frame"), new[] { 0, 0, 1920, 1040 }), big.ToString());
    }

    [TestMethod] // SNAP-028 (never strands): the same monitor, its work area shrank so the title bar is off it
    public void Unsnap_on_the_same_monitor_after_its_work_area_shrank_fits_the_title_bar_back_on() {
      // the resolution shrank: [2000,200,2600,800] has -80 px of title bar across 0..1920. Fit: left = 1920-600 = 1320.
      var r = UnsnapLeft(One(), OneWork, new[] { 2000, 200, 2600, 800 }, "A");
      Assert.IsTrue(r.Ok && r.S("restoredFrom") == "remembered" && Same(r.P("frame"), new[] { 1320, 200, 1920, 800 }), r.ToString());
      // the taskbar moved to the top (work from y 48): top 10 < 48-2. Fit: top = max(48, min(10, 1080-600)) = 48.
      int[] topWork = { 0, 48, 1920, 1080 };
      var top = UnsnapLeft(Mons(Mon("A", new[] { 0, 0, 1920, 1080 }, topWork, 96, true)), topWork, new[] { 100, 10, 900, 610 }, "A");
      Assert.IsTrue(top.Ok && Same(top.P("frame"), new[] { 100, 48, 900, 648 }), top.ToString());
    }

    [TestMethod] // SNAP-014, SNAP-025, SNAP-028: partly off the screen on purpose, title bar grabbable -> exactly where it was
    public void Unsnap_puts_a_frame_left_partly_off_the_screen_back_exactly_while_its_title_bar_is_grabbable() {
      // [1500,600,2300,1300]: bar 600..632 inside 0..1042, 420 px of it across. Kept, though its right and bottom are off.
      var r = UnsnapLeft(One(), OneWork, new[] { 1500, 600, 2300, 1300 }, "A");
      Assert.IsTrue(r.Ok && r.S("restoredFrom") == "remembered" && Same(r.P("frame"), new[] { 1500, 600, 2300, 1300 }), r.ToString());
      // 64 px across at 100 % is enough; 63 is not (fit: left = max(0, min(-737, 1120)) = 0)
      var on = UnsnapLeft(One(), OneWork, new[] { -736, 300, 64, 900 }, "A");
      Assert.IsTrue(on.Ok && Same(on.P("frame"), new[] { -736, 300, 64, 900 }), "64 px: " + on);
      var off = UnsnapLeft(One(), OneWork, new[] { -737, 300, 63, 900 }, "A");
      Assert.IsTrue(off.Ok && Same(off.P("frame"), new[] { 0, 300, 800, 900 }), "63 px: " + off);
      // at 125 % the 64 DIP are (64*120+48)/96 = 80 px: 80 across stays, 79 is fitted (top stays 300, inside 40..1552)
      var on125 = UnsnapLeft(Hi(), HiWork, new[] { -720, 300, 80, 900 }, "H");
      Assert.IsTrue(on125.Ok && Same(on125.P("frame"), new[] { -720, 300, 80, 900 }), "80 px at 125 %: " + on125);
      var off125 = UnsnapLeft(Hi(), HiWork, new[] { -721, 300, 79, 900 }, "H");
      Assert.IsTrue(off125.Ok && Same(off125.P("frame"), new[] { 0, 300, 800, 900 }), "79 px at 125 %: " + off125);
    }

    [TestMethod] // SNAP-026 (never strands): Windows' own restore rect, stranded on the same monitor
    public void Windows_restore_rect_with_its_title_bar_off_the_same_monitor_is_fitted_onto_it() {
      // placement [1880,300,2680,900]: most of it is off A but A is still its monitor (the only overlap); 40 px of
      // title bar across < 64. Fit: left = 1920-800 = 1120.
      var r = Plan(new JsonObject { ["monitors"] = One(), ["frame"] = A(Zone(OneWork, "right")), ["dir"] = "left", ["placement"] = A(1880, 300, 2680, 900) });
      Assert.IsTrue(r.Ok && r.S("restoredFrom") == "windows" && Same(r.P("frame"), new[] { 1120, 300, 1920, 900 }), r.ToString());
    }

    [TestMethod] // SNAP-079: the assist after a cross-monitor snap is on the NEW monitor
    public void Assist_after_a_cross_monitor_snap_is_on_the_new_monitor() {
      int[] onB = Zone(BW, "left");
      var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(onB), ["dir"] = "left", ["monitor"] = "B", ["window"] = A(onB) });
      Assert.IsTrue(r.Ok && r.P("assist").GetArrayLength() == 1 && Same(r.P("assist")[0].GetProperty("frame"), Zone(AW, "left")), r.ToString());
    }

    [TestMethod] // SNAP-024: a window is on the monitor holding most of its area
    public void A_window_straddling_two_monitors_is_judged_on_the_one_holding_most_of_it() {
      // 1000 px wide, 700 of it on B (x 0..700) and 300 on A: Win+Up is planned on B (the maximize keeps it there)
      var r = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(-300, 100, 700, 700), ["dir"] = "left" });
      Assert.IsTrue(r.Ok && r.S("monitor") == "B" && Same(r.P("frame"), Zone(BW, "left")), r.ToString());
      var r2 = Plan(new JsonObject { ["monitors"] = Three(), ["frame"] = A(-700, 100, 300, 700), ["dir"] = "left" });
      Assert.IsTrue(r2.Ok && r2.S("monitor") == "A" && Same(r2.P("frame"), Zone(AW, "left")), r2.ToString());
    }

    // ------------------------------------------------------------ apps Windows scales (SNAP-035)
    static int MulDiv(int a, int b, int c) {
      long n = (long)a * b, h = c / 2;
      return n >= 0 ? (int)((n + h) / c) : -(int)((-n + h) / c);
    }
    static int Virt(int v, int mon, int app) => (app == 0 || app == mon) ? v : MulDiv(MulDiv(v, app, mon), mon, app);
    /// Independent brute force: can the app put one edge exactly on `line` with its other edge inside the zone?
    static bool CanReach(int s0, int e0, int ms, int me, int org, int mon, int app, bool end) {
      int k = (int)Math.Ceiling(mon / (double)app) + 1;
      for (int p = s0 - ms - k; p <= s0 - ms + k; p++)
        for (int l = e0 + me - (s0 - ms) - 2 * k; l <= e0 + me - (s0 - ms) + 2 * k; l++) {
          int a = org + Virt(p - org, mon, app) + ms, b = a - ms + Virt(l, mon, app) - me;
          if (end ? (b == e0 && a >= s0 && a <= s0 + k) : (a == s0 && b <= e0 && b >= e0 - k)) return true;
        }
      return false;
    }

    static JsonArray TopBar125() => Mons(Mon("T", new[] { 0, 0, 1920, 1080 }, new[] { 0, 35, 1920, 1080 }, 120, true));
    static R Op(string zone, int appDpi) => Plan(new JsonObject { ["monitors"] = TopBar125(), ["frame"] = A(100, 100, 900, 700), ["window"] = A(91, 100, 909, 709), ["zone"] = zone, ["appDpi"] = appDpi });

    [TestMethod] // SNAP-035: the user's live layout, by hand
    public void A_DPI_unaware_quarter_under_a_125_percent_top_bar_is_rounded_inward_and_never_overlaps_its_neighbour() {
      R tl = Op("tl", 0), bl = Op("bl", 0);
      Assert.IsTrue(tl.P("fit").ValueKind == JsonValueKind.True && Same(tl.P("reach"), new[] { 0, 35, 960, 558 }) && bl.P("fit").ValueKind == JsonValueKind.True &&
        Ltrb(tl.P("reach"))[3] == Ltrb(bl.P("reach"))[1], "per-monitor-aware tl/bl: " + tl + " / " + bl);
      tl = Op("tl", 96); bl = Op("bl", 96);
      Assert.IsTrue(tl.S("fit") == "rounded" && Same(tl.P("reach"), new[] { 0, 36, 960, 558 }) && Same(tl.P("window"), new[] { -9, 36, 969, 567 }) &&
        (tl.S("fitReason") ?? "").Contains("top edge 1 px inside"), "DPI-unaware tl: " + tl);
      Assert.IsTrue(bl.P("fit").ValueKind == JsonValueKind.True && Same(bl.P("reach"), new[] { 0, 558, 960, 1080 }) &&
        Ltrb(tl.P("reach"))[3] == Ltrb(bl.P("reach"))[1], "DPI-unaware bl: " + bl);
      var lh = Op("left", 96);
      Assert.IsTrue(lh.P("fit").ValueKind == JsonValueKind.True && Same(lh.P("reach"), new[] { 0, 35, 960, 1080 }), "DPI-unaware left half: " + lh);
    }

    [TestMethod] // SNAP-030, SNAP-031, SNAP-035: the sweep (top bar at 1.0/1.25/1.5x, odd sizes, a monitor not at 0,0)
    public void Sweep_every_zone_stays_inside_the_work_area_never_overlaps_and_neighbours_meet_where_reachable() {
      var cases = new (string label, int dpi, int[] bounds, int[] work)[] {
        ("1.0x, top bar 28 px", 96, new[] { 0, 0, 1920, 1080 }, new[] { 0, 28, 1920, 1080 }),
        ("1.25x, top bar 35 px (1045 tall)", 120, new[] { 0, 0, 1920, 1080 }, new[] { 0, 35, 1920, 1080 }),
        ("1.5x, top bar 42 px", 144, new[] { 0, 0, 1920, 1080 }, new[] { 0, 42, 1920, 1080 }),
        ("1.0x, 1079 tall", 96, new[] { 0, 0, 1920, 1080 }, new[] { 0, 0, 1920, 1079 }),
        ("1.25x, 1079 tall", 120, new[] { 0, 0, 1920, 1080 }, new[] { 0, 0, 1920, 1079 }),
        ("1.5x, 1919 x 1079", 144, new[] { 0, 0, 1919, 1079 }, new[] { 0, 0, 1919, 1079 }),
        ("1.25x, top bar + taskbar, 1919 wide", 120, new[] { 0, 0, 1919, 1080 }, new[] { 0, 35, 1919, 1032 }),
        ("1.5x, right of the primary, top bar", 144, new[] { 1920, 0, 3840, 1080 }, new[] { 1920, 42, 3840, 1080 }),
      };
      var Z = new Dictionary<string, int[]> {
        ["left"] = new[] { 2, 1, 0, 0, 1, 1 }, ["right"] = new[] { 2, 1, 1, 0, 1, 1 }, ["tl"] = new[] { 2, 2, 0, 0, 1, 1 }, ["tr"] = new[] { 2, 2, 1, 0, 1, 1 },
        ["bl"] = new[] { 2, 2, 0, 1, 1, 1 }, ["br"] = new[] { 2, 2, 1, 1, 1, 1 },
        ["t1"] = new[] { 3, 1, 0, 0, 1, 1 }, ["t2"] = new[] { 3, 1, 1, 0, 1, 1 }, ["t3"] = new[] { 3, 1, 2, 0, 1, 1 }, ["two"] = new[] { 3, 1, 0, 0, 2, 1 },
      };
      var pairs = new (string a, string b, char axis)[] { ("left", "right", 'x'), ("tl", "tr", 'x'), ("bl", "br", 'x'), ("t1", "t2", 'x'), ("t2", "t3", 'x'), ("two", "t3", 'x'), ("tl", "bl", 'y'), ("tr", "br", 'y') };
      var all = new List<string>();
      foreach (var (label, dpi, bounds, work) in cases) {
        int b = (int)Math.Round(7.0 * dpi / 96, MidpointRounding.AwayFromZero);   // 7 px standard borders, scaled (JS Math.round)
        int[] frame = { bounds[0] + 100, work[1] + 100, bounds[0] + 900, work[1] + 700 };
        int[] window = { frame[0] - b, frame[1], frame[2] + b, frame[3] + b };
        foreach (int app in new[] { 0, 96 }) {
          var got = new Dictionary<string, (int[] zone, int[] reach, JsonElement fit)>();
          var bad = new List<string>();
          foreach (var kv in Z) {
            var r = Plan(new JsonObject { ["monitors"] = Mons(Mon("S", bounds, work, dpi, bounds[0] == 0)), ["monitor"] = "S", ["frame"] = A(frame), ["window"] = A(window),
              ["grid"] = A(kv.Value[0], kv.Value[1]), ["cell"] = A(kv.Value[2], kv.Value[3], kv.Value[4], kv.Value[5]), ["appDpi"] = app });
            if (!r.Ok) { bad.Add(kv.Key + ": " + r.Error); continue; }
            got[kv.Key] = (Ltrb(r.P("frame")), Ltrb(r.P("reach")), r.P("fit"));
          }
          bool scaled = app > 0 && app != dpi;
          int kIn = scaled ? 1 : 0, kOut = scaled ? (int)Math.Ceiling(dpi / (double)app) : 0, rounded = 0;
          foreach (var kv in got) {
            var (zone, reach, fit) = kv.Value;
            if (reach[0] < work[0] || reach[1] < work[1] || reach[2] > work[2] || reach[3] > work[3]) bad.Add($"{kv.Key} leaves the work area [{string.Join(",", reach)}]");
            bool[] rim = { zone[0] == work[0], zone[1] == work[1], zone[2] == work[2], zone[3] == work[3] };
            for (int i = 0; i < 4; i++)
              if (Math.Abs(reach[i] - zone[i]) > (rim[i] ? kOut : kIn)) { bad.Add($"{kv.Key} is too far off its zone [{string.Join(",", reach)}] vs [{string.Join(",", zone)}]"); break; }
            bool exact = Enumerable.Range(0, 4).All(i => reach[i] == zone[i]);
            bool fitTrue = fit.ValueKind == JsonValueKind.True, fitRounded = fit.ValueKind == JsonValueKind.String && fit.GetString() == "rounded";
            if (exact != fitTrue || (!exact && !fitRounded)) bad.Add($"{kv.Key} fit {fit.GetRawText()} but reach {(exact ? "==" : "!=")} zone");
            if (!exact) rounded++;
          }
          foreach (var (pa, pb, axis) in pairs) {
            if (!got.TryGetValue(pa, out var GA) || !got.TryGetValue(pb, out var GB)) continue;
            int i = axis == 'x' ? 0 : 1, line = GA.zone[i + 2];
            if (GA.reach[i + 2] > GB.reach[i]) bad.Add($"{pa} overlaps {pb} by {GA.reach[i + 2] - GB.reach[i]} px");
            bool aOuter = GA.zone[i] == work[i], bOuter = GB.zone[i + 2] == work[i + 2];
            if (!aOuter || !bOuter) continue;
            int ms = axis == 'x' ? b : 0, org = bounds[i];
            bool aCan = CanReach(GA.zone[i], line, ms, b, org, dpi, app == 0 ? dpi : app, true), bCan = CanReach(line, GB.zone[i + 2], ms, b, org, dpi, app == 0 ? dpi : app, false);
            if (aCan && bCan && (GA.reach[i + 2] != line || GB.reach[i] != line)) bad.Add($"{pa}|{pb} do not meet on {line} although both can ({GA.reach[i + 2]} / {GB.reach[i]})");
          }
          if (!scaled && rounded != 0) bad.Add($"{rounded} zone(s) rounded although Windows does not scale this app");
          foreach (var x in bad.Take(4)) all.Add($"{label}, {(app != 0 ? "DPI-unaware" : "per-monitor-aware")}: {x}");
        }
      }
      Assert.AreEqual(0, all.Count, string.Join("\n", all));
    }
  }
}
