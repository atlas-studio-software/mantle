// InProcess.cs - the linked sidecar code called directly, inside the test process (no stdio, no
// message loop): what a .NET host such as the WinUI app could call as a plain library.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests {
  [TestClass]
  public class InProcess {
    static readonly Type ProgramType = typeof(Mantle.Json).Assembly.GetType("Mantle.Program", true);

    /// Program.Run(cmd, req, ref result) is the sidecar's dispatcher (private): the same call the
    /// lanes make for every request. Passive is set first, so nothing can touch the live shell.
    internal static string Run(string cmd, Dictionary<string, object> req, out object result) {
      ProgramType.GetField("Passive", BindingFlags.Public | BindingFlags.Static).SetValue(null, true);
      var run = ProgramType.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static, null,
        new[] { typeof(string), typeof(Dictionary<string, object>), typeof(object).MakeByRefType() }, null);
      Assert.IsNotNull(run, "Mantle.Program.Run(string, Dictionary, ref object) was not found");
      var args = new object[] { cmd, req ?? new Dictionary<string, object>(), null };
      string err;
      try { err = (string)run.Invoke(null, args); }
      catch (TargetInvocationException tie) { err = "THREW " + tie.InnerException.GetType().Name + ": " + tie.InnerException.Message; }
      result = args[2];
      return err;
    }

    [TestMethod]
    public void Json_RoundTripsTheWireFormat() {
      string line = Mantle.Json.Write(Mantle.Json.Obj("id", 7, "ok", true, "result",
        Mantle.Json.Obj("s", "a\"b\\c\u0001é", "n", 1.5, "nan", double.NaN, "big", 1e300, "list", new object[] { true, null, 3 })));
      StringAssert.Contains(line, "\"nan\":null");
      var back = (IDictionary)Mantle.Json.Parse(line);
      Assert.AreEqual(true, back["ok"]);
      var res = (IDictionary)back["result"];
      Assert.AreEqual("a\"b\\c\u0001é", res["s"]);
      Assert.AreEqual(3, ((IList)res["list"]).Count);
      // Strict JSON in (README: "+1", "01", ".5" and "1." are errors).
      foreach (string bad in new[] { "{\"a\":+1}", "{\"a\":01}", "{\"a\":.5}", "{\"a\":1.}" }) {
        bool threw = false;
        try { Mantle.Json.Parse(bad); } catch (Exception) { threw = true; }
        Assert.IsTrue(threw, "the parser accepted " + bad);
      }
      // Culture: the wire must not follow the thread culture (a comma decimal would break JSON).
      var was = Thread.CurrentThread.CurrentCulture;
      try {
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        StringAssert.Contains(Mantle.Json.Write(Mantle.Json.Obj("n", 1.5)), "1.5");
      } finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    /// Read-only commands through the real dispatcher. Each answer is written to inproc.txt with its
    /// shape, so the report can say exactly what a library caller gets.
    [DataTestMethod]
    [DataRow("ping")]
    [DataRow("windows")]
    [DataRow("foreground")]
    public void Dispatcher_ReadOnlyCommand_Answers(string cmd) {
      var sw = Stopwatch.StartNew();
      object result;
      string err = Run(cmd, null, out result);
      sw.Stop();
      string json = err == null ? Mantle.Json.Write(result) : null;
      Shape.Note("inproc.txt", cmd + ": " + (err == null ? "ok, " + Shape.Of(json).Count + " shape paths, " + json.Length + " bytes" : "ERROR " + err) +
                               " (" + sw.ElapsedMilliseconds + " ms, thread " + Thread.CurrentThread.GetApartmentState() + ")");
      Assert.IsNull(err, cmd + " failed in-process: " + err);
    }

    /// Feature commands (Features.cs registers them by reflection over THIS assembly) - the
    /// table must be found under .NET exactly as under csc, and a few read-only ones must answer.
    [TestMethod]
    public void Features_AreDiscovered_AndReadOnlyOnesAnswer() {
      object result;
      string err = Run("ping", null, out result);
      Assert.IsNull(err, err);
      var features = (IDictionary)((IDictionary)result)["features"];
      var names = (IList)features["commands"];
      var problems = (IList)features["problems"];
      Shape.Note("inproc.txt", "features: " + names.Count + " commands registered, " + problems.Count + " problems" +
                               (problems.Count > 0 ? ": " + string.Join(" | ", ToStrings(problems)) : ""));
      Assert.IsTrue(names.Count > 50, "only " + names.Count + " feature commands were registered");
      Assert.AreEqual(0, problems.Count, "feature registration problems: " + string.Join(" | ", ToStrings(problems)));
      foreach (string cmd in StdioCommands.Features) {
        var sw = Stopwatch.StartNew();
        string ferr = Run(cmd, null, out var fres);
        sw.Stop();
        Shape.Note("inproc.txt", cmd + ": " + (ferr == null ? "ok, " + Shape.Of(Mantle.Json.Write(fres)).Count + " shape paths" : "ERROR " + ferr) + " (" + sw.ElapsedMilliseconds + " ms)");
      }
    }

    static IEnumerable<string> ToStrings(IList l) { foreach (object o in l) yield return Convert.ToString(o); }
  }
}
