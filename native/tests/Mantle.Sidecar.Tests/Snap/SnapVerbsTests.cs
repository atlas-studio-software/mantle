// SnapVerbsTests.cs (lane r9-snap) - the sidecar verbs the native snap path calls exist in the linked build, on the
// lanes sidecar/Snap.cs puts them (SNAP-047, SNAP-049). Read-only: `ping` and Lanes.For, never snap.move or
// layouts.place, so no window can be touched.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests.Snap {
  [TestClass]
  public class SnapVerbsTests {
    static List<string> FeatureCommands() {
      object result;
      string err = InProcess.Run("ping", null, out result);
      Assert.IsNull(err, err);
      var features = (IDictionary)((IDictionary)result)["features"];
      return ((IList)features["commands"]).Cast<object>().Select(o => Convert.ToString(o)).ToList();
    }

    static string LaneOf(string cmd) {
      var lanes = typeof(Mantle.Json).Assembly.GetType("Mantle.Lanes", true);
      var @for = lanes.GetMethod("For", BindingFlags.Public | BindingFlags.Static, null,
        new[] { typeof(string), typeof(Dictionary<string, object>) }, null);
      Assert.IsNotNull(@for, "Mantle.Lanes.For(string, Dictionary) was not found");
      return (string)@for.Invoke(null, new object[] { cmd, new Dictionary<string, object>() });
    }

    [TestMethod] // SNAP-049: what the native move path (Mantle.Core/Features/Snap/SnapCore.cs) and the later surfaces call
    public void Every_snap_verb_the_native_path_uses_is_registered() {
      var have = FeatureCommands();
      var missing = new[] { "snap.move", "snap.zone", "snap.info", "snap.monitors", "snap.plan", "layouts.place", "layouts.monitors" }
        .Where(v => !have.Contains(v)).ToList();
      Assert.AreEqual(0, missing.Count, "not registered: " + string.Join(", ", missing));
    }

    [TestMethod] // SNAP-047: a hung app cannot freeze the keyboard-hook thread
    public void Moves_run_on_the_qs_lane_and_the_reads_stay_on_ui() {
      FeatureCommands();   // the feature table (and with it every lane) is filled on the first request
      Assert.AreEqual("qs", LaneOf("snap.move"));
      Assert.AreEqual("qs", LaneOf("snap.zone"));
      Assert.AreEqual("ui", LaneOf("snap.info"));
      Assert.AreEqual("ui", LaneOf("snap.monitors"));
      Assert.AreEqual("ui", LaneOf("snap.plan"));
      Assert.AreEqual("slow", LaneOf("layouts.place"));
    }
  }
}
