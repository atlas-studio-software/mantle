// Stdio.cs - the csc-built Mantle.Sidecar.exe (.NET Framework 4) and the .NET-built one (the same
// linked sources), each started in --passive, asked the same read-only commands over stdio, and the
// answers compared by JSON shape (and by value where a value is deterministic: the command lists).
// Both exe paths come from check.ps1 -Compare; without them these tests are Inconclusive.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.Sidecar.Tests {
  internal static class StdioCommands {
    public static readonly string[] Core = { "ping", "windows", "foreground" };
    /// Read-only feature commands (each is a read with no arguments).
    public static readonly string[] Features = {
      "desktops.list", "taskview.list", "layouts.monitors", "elevate.status", "maxfold.state", "clip.status", "printers.list"
    };
    /// Paths whose KEYS are data (per-lane / per-cache maps), not schema.
    public static readonly string[] DataKeyed = { "$.load.", "$.caches.", "$.watches." };
  }

  internal sealed class SidecarProc : IDisposable {
    readonly Process _p;
    readonly BlockingCollection<string> _lines = new BlockingCollection<string>();
    public readonly string Name;
    public string Ready;
    public long ReadyMs;
    public readonly List<string> Logs = new List<string>();

    public SidecarProc(string name, string exe) {
      Name = name;
      var psi = new ProcessStartInfo(exe, "--passive --parent-pid=" + Environment.ProcessId) {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
      };
      var sw = Stopwatch.StartNew();
      _p = Process.Start(psi);
      _p.StandardInput.AutoFlush = true;
      var t = new System.Threading.Thread(() => {
        try { string l; while ((l = _p.StandardOutput.ReadLine()) != null) _lines.Add(l); } catch (Exception) { }
        _lines.CompleteAdding();
      }) { IsBackground = true, Name = name + " stdout" };
      t.Start();
      _p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (Logs) Logs.Add("stderr: " + e.Data); };
      _p.BeginErrorReadLine();
      Ready = Next(20000);
      ReadyMs = sw.ElapsedMilliseconds;
    }

    string Next(int ms) {
      string l;
      return _lines.TryTake(out l, ms) ? l : null;
    }

    /// The reply line for `id`, skipping events (kept in Logs when they are log events).
    public string Ask(int id, string cmd, int timeoutMs = 20000) {
      var bytes = new UTF8Encoding(false).GetBytes("{\"id\":" + id + ",\"cmd\":\"" + cmd + "\"}\n");
      _p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
      _p.StandardInput.BaseStream.Flush();
      var until = Stopwatch.StartNew();
      while (until.ElapsedMilliseconds < timeoutMs) {
        string l = Next((int)Math.Max(1, timeoutMs - until.ElapsedMilliseconds));
        if (l == null) break;
        using var doc = JsonDocument.Parse(l);
        var root = doc.RootElement;
        if (root.TryGetProperty("ev", out var ev)) {
          if (ev.GetString() == "log") lock (Logs) Logs.Add(l);
          continue;
        }
        if (root.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id) return l;
      }
      return null;
    }

    public void Dispose() {
      try { if (!_p.HasExited) Ask(99999, "quit", 8000); } catch (Exception) { }
      try { if (!_p.WaitForExit(8000)) _p.Kill(); } catch (Exception) { }
      _p.Dispose();
    }
  }

  [TestClass]
  public class Stdio {
    static string Exe(string env) {
      string p = Environment.GetEnvironmentVariable(env);
      if (string.IsNullOrEmpty(p) || !File.Exists(p)) Assert.Inconclusive(env + " is not set to an existing exe (run check.ps1 -Compare)");
      return p;
    }

    static void Report(string line) { Shape.Note("compare.txt", line); }

    static JsonElement Result(string reply, out bool ok, out string error) {
      var doc = JsonDocument.Parse(reply);
      var root = doc.RootElement;
      ok = root.GetProperty("ok").GetBoolean();
      error = ok ? null : (root.TryGetProperty("error", out var e) ? e.GetString() : "(no error text)");
      return ok ? root.GetProperty("result").Clone() : default;
    }

    static string[] Strings(JsonElement arr) {
      return arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray().Select(x => x.GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray() : new string[0];
    }

    [TestMethod]
    public void BothExes_AnswerTheSameShape() {
      string cscExe = Exe("MANTLE_SIDECAR_CSC_EXE"), netExe = Exe("MANTLE_SIDECAR_NET_EXE");
      using var csc = new SidecarProc("csc", cscExe);
      using var net = new SidecarProc("net", netExe);
      Report("ready: csc " + (csc.Ready ?? "NONE") + " in " + csc.ReadyMs + " ms; net " + (net.Ready ?? "NONE") + " in " + net.ReadyMs + " ms");
      Assert.IsNotNull(csc.Ready, "the csc exe never said ready");
      Assert.IsNotNull(net.Ready, "the .NET exe never said ready");
      Assert.AreEqual(Shape.Of(csc.Ready).Count, Shape.Of(net.Ready).Count, "the ready lines differ in shape");

      var failures = new List<string>();
      int id = 1;
      foreach (string cmd in StdioCommands.Core.Concat(StdioCommands.Features)) {
        bool core = StdioCommands.Core.Contains(cmd);
        int myId = id++;
        var sw = Stopwatch.StartNew();
        string a = csc.Ask(myId, cmd);
        long aMs = sw.ElapsedMilliseconds; sw.Restart();
        string b = net.Ask(myId, cmd);
        long bMs = sw.ElapsedMilliseconds;
        if (a == null || b == null) {
          string why = cmd + ": NO ANSWER from " + (a == null ? "csc " : "") + (b == null ? "net" : "");
          Report(why); failures.Add(why); continue;
        }
        var ra = Result(a, out bool okA, out string errA);
        var rb = Result(b, out bool okB, out string errB);
        string timing = " (csc " + aMs + " ms, net " + bMs + " ms)";
        if (okA != okB) {
          string why = cmd + ": csc " + (okA ? "ok" : "ERROR " + errA) + " but net " + (okB ? "ok" : "ERROR " + errB) + timing;
          Report(why); if (core) failures.Add(why); continue;
        }
        if (!okA) {
          Report(cmd + ": both answered an error, " + (errA == errB ? "the same words: " + errA : "different words: csc \"" + errA + "\" net \"" + errB + "\"") + timing);
          continue;
        }
        var sa = Shape.Of(ra); var sb = Shape.Of(rb);
        var diff = Shape.Diff(sa, sb, "csc", "net", StdioCommands.DataKeyed);
        string sizes = ra.ValueKind == JsonValueKind.Array ? "; csc " + ra.GetArrayLength() + " items, net " + rb.GetArrayLength() + " items" : "";
        if (diff.Count == 0) Report(cmd + ": same shape (" + sa.Count + " paths)" + sizes + timing);
        else {
          Report(cmd + ": SHAPE DIFFERS (" + diff.Count + "): " + string.Join("; ", diff.Take(12)) + sizes + timing);
          if (core) failures.Add(cmd + " shape differs");
        }
        if (cmd == "ping") {
          // Deterministic values: the protocol's command index and the feature table.
          var ca = Strings(ra.GetProperty("commands")); var cb = Strings(rb.GetProperty("commands"));
          var fa = Strings(ra.GetProperty("features").GetProperty("commands")); var fb = Strings(rb.GetProperty("features").GetProperty("commands"));
          var pa = Strings(ra.GetProperty("features").GetProperty("problems")); var pb = Strings(rb.GetProperty("features").GetProperty("problems"));
          Report("ping.commands: csc " + ca.Length + ", net " + cb.Length + (ca.SequenceEqual(cb) ? " (identical)" : " (DIFFER)"));
          Report("ping.features.commands: csc " + fa.Length + ", net " + fb.Length + (fa.SequenceEqual(fb) ? " (identical)" :
                 " (DIFFER: only csc " + string.Join(",", fa.Except(fb)) + "; only net " + string.Join(",", fb.Except(fa)) + ")"));
          Report("ping.features.problems: csc " + pa.Length + ", net " + pb.Length + (pb.Length > 0 ? ": " + string.Join(" | ", pb) : ""));
          Report("ping.version: csc " + ra.GetProperty("version") + ", net " + rb.GetProperty("version") + "; dpiMode csc " + ra.GetProperty("dpiMode") + ", net " + rb.GetProperty("dpiMode"));
          if (!ca.SequenceEqual(cb)) failures.Add("ping.commands differ");
          if (!fa.SequenceEqual(fb)) failures.Add("ping.features.commands differ");
        }
      }
      lock (csc.Logs) foreach (string l in csc.Logs.Take(10)) Report("csc log: " + l);
      lock (net.Logs) foreach (string l in net.Logs.Take(10)) Report("net log: " + l);
      Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
    }
  }
}
