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
      // The README says `ready` is the first line; record anything that came before it.
      while (sw.ElapsedMilliseconds < 20000) {
        string l = Next((int)Math.Max(1, 20000 - sw.ElapsedMilliseconds));
        if (l == null) break;
        if (l.Contains("\"ev\":\"ready\"")) { Ready = l; break; }
        BeforeReady.Add(l);
      }
      ReadyMs = sw.ElapsedMilliseconds;
    }

    public readonly List<string> BeforeReady = new List<string>();


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

  /// Another role of the same exe (--rec-standin, --record): its own process, its stdout read by a
  /// thread into a queue (one reader per stream, so a timed-out wait never leaves a read pending).
  internal sealed class RoleProc {
    public readonly Process P;
    readonly BlockingCollection<string> _lines = new BlockingCollection<string>();
    public RoleProc(string exe, string args) {
      var psi = new ProcessStartInfo(exe, args) {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = false,
        CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
      };
      P = Process.Start(psi);
      P.StandardInput.AutoFlush = true;
      new System.Threading.Thread(() => {
        try { string l; while ((l = P.StandardOutput.ReadLine()) != null) _lines.Add(l); } catch (Exception) { }
        _lines.CompleteAdding();
      }) { IsBackground = true }.Start();
    }
    public string Until(Func<string, bool> want, int ms) {
      var sw = Stopwatch.StartNew();
      while (sw.ElapsedMilliseconds < ms) {
        if (!_lines.TryTake(out var l, (int)Math.Max(1, ms - sw.ElapsedMilliseconds))) return null;
        if (want(l)) return l;
      }
      return null;
    }
    public void Say(string line) { try { P.StandardInput.WriteLine(line); } catch (Exception) { } }
    public void End(int ms) {
      try { if (!P.HasExited) { P.StandardInput.Close(); if (!P.WaitForExit(ms)) P.Kill(); } } catch (Exception) { }
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
      Report("lines before ready (README: ready is the first line): csc " + csc.BeforeReady.Count + ", net " + net.BeforeReady.Count +
             (csc.BeforeReady.Count + net.BeforeReady.Count > 0 ? "; first: " + (csc.BeforeReady.Concat(net.BeforeReady).First().Length > 160 ? csc.BeforeReady.Concat(net.BeforeReady).First().Substring(0, 160) + "..." : csc.BeforeReady.Concat(net.BeforeReady).First()) : ""));
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
      lock (csc.Logs) foreach (string l in csc.Logs.Take(6)) Report("csc log: " + l);
      lock (net.Logs) foreach (string l in net.Logs.Take(6)) Report("net log: " + l);
      Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
    }

    /// The recorder role (Recorder.cs: WinRT GraphicsCapture + D3D + Media Foundation), the part of
    /// the sidecar with the most WinRT interop. Each exe records ITS OWN stand-in window (test mode:
    /// never the screen, never another window) for ~1.5 s. REPORT-ONLY: whether the runner can
    /// capture at all is what the csc baseline says; a difference is written to compare.txt.
    [TestMethod]
    public void Recorder_SameOutcomeAsCsc() {
      string cscExe = Exe("MANTLE_SIDECAR_CSC_EXE"), netExe = Exe("MANTLE_SIDECAR_NET_EXE");
      string a = RecordOnce("csc", cscExe), b = RecordOnce("net", netExe);
      Report("recorder: csc " + a + " | net " + b);
      bool aDone = a.StartsWith("done", StringComparison.Ordinal), bDone = b.StartsWith("done", StringComparison.Ordinal);
      if (aDone != bDone) Report("recorder: OUTCOME DIFFERS (csc " + (aDone ? "recorded" : "did not record") + ", net " + (bDone ? "recorded" : "did not record") + ")");
    }

    static string RecordOnce(string name, string exe) {
      string dir = Path.Combine(Shape.OutDir(), "rec-" + name);
      Directory.CreateDirectory(dir);
      RoleProc standin = null, rec = null;
      try {
        standin = new RoleProc(exe, "--rec-standin --at=under,320,240 --secs=60");
        string si = standin.Until(l => l.Contains("\"ev\":\"standin\""), 15000);
        if (si == null) return "no stand-in window (" + (standin.P.HasExited ? "exited " + standin.P.ExitCode : "silent") + ")";
        using var sdoc = JsonDocument.Parse(si);
        var s = sdoc.RootElement;
        if (s.TryGetProperty("hidden", out var hidden) && hidden.ValueKind != JsonValueKind.True) return "the stand-in would have been visible: " + si;
        string hwnd = s.GetProperty("hwnd").ToString();
        int pid = s.GetProperty("pid").GetInt32();
        rec = new RoleProc(exe, "--record");
        rec.Say("{\"mode\":\"window\",\"hwnd\":\"" + hwnd + "\",\"fps\":30,\"audio\":false,\"mic\":false,\"cursor\":false,\"test\":true,\"allowPid\":" + pid +
                ",\"out\":" + JsonSerializer.Serialize(Path.Combine(dir, "rec.mp4")) + ",\"maxSecs\":10}");
        var events = new List<string>();
        var sw = Stopwatch.StartNew();
        long startedAt = -1;
        bool stopped = false;
        string end = null;
        while (sw.ElapsedMilliseconds < 30000 && end == null) {
          string l = rec.Until(_ => true, 500);
          if (l == null && rec.P.HasExited) break;
          if (l != null) {
            if (!l.Contains("\"ev\":\"tick\"")) events.Add(l.Length > 200 ? l.Substring(0, 200) + "..." : l);
            if (l.Contains("\"ev\":\"done\"") || l.Contains("\"ev\":\"error\"")) end = l;
            if (l.Contains("\"ev\":\"started\"")) startedAt = sw.ElapsedMilliseconds;
          }
          if (!stopped && startedAt >= 0 && sw.ElapsedMilliseconds - startedAt > 1500) { rec.Say("stop"); stopped = true; }
        }
        Shape.Note("compare.txt", "recorder " + name + " events: " + string.Join(" ", events.Take(6)));
        if (end == null) return "no done/error event in 30 s (" + events.Count + " events" + (rec.P.HasExited ? ", exited " + rec.P.ExitCode : "") + ")";
        using var edoc = JsonDocument.Parse(end);
        var e2 = edoc.RootElement;
        return e2.GetProperty("ev").GetString() == "done"
          ? "done: " + e2.GetProperty("frames") + " frames, " + e2.GetProperty("secs") + " s"
          : "error: " + e2.GetProperty("error").GetString();
      } catch (Exception ex) {
        return "test error " + ex.GetType().Name + ": " + ex.Message;
      } finally {
        if (rec != null) rec.End(8000);
        if (standin != null) { standin.Say("close"); standin.End(5000); }
      }
    }

    /// AudioMixer/SysSound send "sid:<string hash>" keys; .NET randomises string.GetHashCode per
    /// process, so edit E8/E9 uses NetShimText.FrameworkHash. Proven here against the real .NET
    /// Framework: Windows PowerShell 5.1 runs on it (64-bit), so its String.GetHashCode is the csc
    /// sidecar's.
    [TestMethod]
    public void FrameworkHash_MatchesDotNetFramework() {
      string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
      if (!File.Exists(ps)) Assert.Inconclusive("no Windows PowerShell 5.1 to ask");
      string[] samples = { "", "a", "ab", "abc", "abcd", "abcde", "{0.0.0.00000000}.{5a7d3e1b-1c4f-4f8a-9b1e-123456789abc}|#%b{9f2e}", "é中😀", "Mantle.Sidecar", "ab" + (char)0 + "cd" };
      var psi = new ProcessStartInfo(ps, "-NoProfile -NonInteractive -Command -") {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
      };
      using var p = Process.Start(psi);
      // Each sample travels as base64 UTF-16 so no quoting or console code page can touch it.
      foreach (string s in samples)
        p.StandardInput.WriteLine("[Environment]::Is64BitProcess.ToString() + ' ' + [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.Unicode.GetBytes(s)) + "')).GetHashCode()");
      p.StandardInput.Close();
      string[] got = p.StandardOutput.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
      p.WaitForExit(20000);
      Assert.AreEqual(samples.Length, got.Length, "powershell answered: " + string.Join(" / ", got));
      var shim = typeof(Mantle.Json).Assembly.GetType("Mantle.NetShimText", true).GetMethod("FrameworkHash");
      int same = 0;
      for (int i = 0; i < samples.Length; i++) {
        var parts = got[i].Split(' ');
        Assert.AreEqual("True", parts[0], "Windows PowerShell was not 64-bit");
        int fw = int.Parse(parts[1]);
        int mine = (int)shim.Invoke(null, new object[] { samples[i] });
        if (fw == mine) same++;
        else Report("FrameworkHash MISMATCH for sample " + i + ": framework " + fw + ", shim " + mine);
      }
      Report("FrameworkHash: " + same + "/" + samples.Length + " samples equal to .NET Framework's String.GetHashCode; .NET's own is randomised (\"abc\" -> " + "abc".GetHashCode() + " in this process)");
      Assert.AreEqual(samples.Length, same);
    }
  }
}
