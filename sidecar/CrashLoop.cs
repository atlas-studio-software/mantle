// CrashLoop.cs - what the supervisor does when a NATIVE Mantle main dies (lane r12-crash-restart, 2026-11-25).
//
// ONE RULE for native Mantle (MantleNative.exe; supervisor-main.json says "native": true), written down in
// native/docs/lanes/r12-crash-restart.md. It replaces, for native only, the two counters Electron has (main.js
// crashPlan in crash.json for a JS error, and Supervisor.cs's own "3rd crash inside 2 minutes" for any death):
//
//   A death that is a deliberate stop (a stand-down in the record: --quit, switch-back, the kill switch, Restore
//   Windows taskbar; Windows signing out or shutting down; another main replaced it; a process-tree kill) is
//   NOT a crash: crash.json is not touched and nothing is started.
//   Every other death is a crash. It is written to crash.json {history:[ms], last:{at, what, restarted, ...}}
//   (main.js's shape, the same file Electron uses in the same profile), and the plan is main.js crashPlan:
//   the 1st, 2nd and 3rd crash inside 10 minutes are started again; the 4th stays down. Staying down = no
//   restart, Windows put back by tools\restore-windows.ps1 (-NoKill: this supervisor is a Mantle.Sidecar.exe
//   --supervise process itself) that must exit 0 AND print a line starting "verified:" (case-sensitive), a log
//   line, and the record kept for the tray and the next start ("Stopped after repeated errors at hh:mm").
//   A crash that cannot be written down is not started again either (the loop guard could not count it).
//   While ANOTHER shell is live (Electron Mantle's lockfile held, an installed Mantle.exe running, or another
//   native holding Local\Mantle.LiveShell), nothing is started and Windows is NOT put back: that shell owns it.
//
// Pure: every outside effect goes through ICrashHost, so Mantle.Core.Tests runs this with a fake clock, a fake
// launcher and a fake restore (it links this file). C# 5 on purpose: Electron's sidecar is built with the csc.exe
// that ships with Windows (sidecar/build.ps1), and that compiler only knows C# 5.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mantle {

  /// Everything CrashLoop needs from the world. The supervisor implements it (Supervisor.NativeHost); tests fake it.
  interface ICrashHost {
    long Now();
    /// crash.json's history (Unix ms); an unreadable or missing file is an empty list.
    List<long> History();
    /// Write crash.json (atomic). null = written, else the reason it was not.
    string WriteCrash(Dictionary<string, object> record);
    /// Another live shell, in words (null = none): Electron's lockfile held, Mantle.exe running, Local\Mantle.LiveShell held.
    string OtherShell();
    /// Run tools\restore-windows.ps1 -NoKill. null = it ran (exit code and stdout set), else why it would not run.
    string RunRestore(out int exitCode, out string output);
    /// Wait for the old main's helpers and the taskbar, then start main again. The new pid, or 0 and why.
    int StartAgain(out string why);
    /// The crash-loop question ("Start Mantle again now?"). A test instance never asks (false).
    bool AskAgain(string text);
    void Log(string line);
  }

  /// What is known about the death, in the order the supervisor has always checked it.
  sealed class CrashFacts {
    public int Pid;
    public string CodeHex = "unknown";
    public string Words = "it stopped unexpectedly";
    public string What;                 // the crash note main left (its error's first line), or null
    public string StandDownWhy;         // non-null = the record has standDown: a deliberate quit
    public bool ShuttingDown;           // SM_SHUTTINGDOWN
    public Func<int> OtherMain;         // another live main for this folder (pid), 0 = none; asked only when needed
    public Func<bool> TreeKill;         // main and its helper ended together with code 1; asked only when needed
  }

  sealed class CrashOutcome {
    public string Kind;                 // quit | signout | replaced | treekill | othershell | restarted | gaveup | startfailed
    public int NewPid;
    public int Exit;                    // the supervisor's own exit code when it stops watching
    public bool Counted;                // the death went into crash.json's history
    public string Restore;              // the restore's result in words (null when none ran)
    public bool Verified;
    public int Recent;                  // crashes inside the window, this one included
  }

  static class CrashLoop {
    public const long WindowMs = 10 * 60 * 1000;
    public const int MaxRestarts = 3;
    public const int Keep = 10;

    /// main.js crashPlan(history, now): the times inside 10 minutes plus now; more than 3 = stay down.
    public static bool Decide(List<long> history, long now, out List<long> keep, out int recent) {
      var all = new List<long>();
      if (history != null) foreach (long t in history) if (t <= now && now - t < WindowMs) all.Add(t);
      all.Add(now);
      recent = all.Count;
      keep = all.GetRange(Math.Max(0, all.Count - Keep), Math.Min(Keep, all.Count));
      return all.Count <= MaxRestarts;
    }

    public static string Why(bool restart, int recent) {
      if (!restart) return "stopped with an error " + recent + " times in 10 minutes, so it was not started again";
      return recent == 1 ? "stopped with an error, so it is starting again" : "stopped with an error (" + recent + " times in 10 minutes), so it is starting again";
    }

    /// restore-windows.ps1 put Windows back: exit 0 AND a line that starts with "verified:" (its failure line
    /// "NOT VERIFIED - ..." must not count, so the match is case-sensitive and anchored).
    public static bool Verified(int exitCode, string output) {
      return exitCode == 0 && output != null && Regex.IsMatch(output, "^verified:", RegexOptions.Multiline);
    }

    static Dictionary<string, object> Record(List<long> history, Dictionary<string, object> last) {
      var arr = new List<object>();
      foreach (long t in history) arr.Add(t);
      var rec = new Dictionary<string, object>();
      rec["history"] = arr;
      rec["last"] = last;
      return rec;
    }

    /// The death of a native main: classify it, count it, and start it again or stay down.
    public static CrashOutcome Handle(ICrashHost host, CrashFacts f) {
      var o = new CrashOutcome();
      // 1. deliberate stops: never a crash, crash.json untouched, nothing started
      if (f.StandDownWhy != null) {
        host.Log("supervisor: Mantle " + f.Pid + " quit on purpose (" + f.StandDownWhy + "), exit code " + f.CodeHex + "; it is not started again and it is not counted as a crash");
        o.Kind = "quit"; return o;
      }
      if (f.ShuttingDown) {
        host.Log("supervisor: Mantle " + f.Pid + " ended while Windows is signing out or shutting down (exit code " + f.CodeHex + "); it is not started again and it is not counted as a crash");
        o.Kind = "signout"; return o;
      }
      int other = f.OtherMain != null ? f.OtherMain() : 0;
      if (other > 0) {
        host.Log("supervisor: Mantle " + f.Pid + " stopped (exit code " + f.CodeHex + ") because another Mantle (pid " + other + ") already runs for this folder; that is not a crash, nothing is started");
        o.Kind = "replaced"; return o;
      }
      if (f.TreeKill != null && f.TreeKill()) {
        int code; string output;
        string err = host.RunRestore(out code, out output);
        o.Restore = err ?? (Verified(code, output) ? "done and verified" : "NOT verified (exit " + code + ")");
        o.Verified = err == null && Verified(code, output);
        host.Log("supervisor: Mantle " + f.Pid + " and its helper were ended together (exit code " + f.CodeHex + "), which is a deliberate stop, so it is not started again; the Windows taskbar: " + o.Restore);
        o.Kind = "treekill"; return o;
      }

      // 2. a crash: counted first, whatever happens next
      long now = host.Now();
      string what = f.What ?? f.Words;
      List<long> keep; int recent;
      bool restart = Decide(host.History(), now, out keep, out recent);
      o.Recent = recent; o.Counted = true;
      var last = new Dictionary<string, object>();
      last["at"] = now; last["what"] = what; last["restarted"] = false; last["by"] = "supervisor";
      last["pid"] = (long)f.Pid; last["codeHex"] = f.CodeHex; last["words"] = f.Words;

      // 3. another shell is live now: start nothing, and leave Windows to it (no restore)
      string shell = host.OtherShell();
      if (shell != null) {
        last["why"] = "another Mantle is the live shell (" + shell + ")";
        string werr = host.WriteCrash(Record(keep, last));
        host.Log("supervisor: Mantle " + f.Pid + " stopped unexpectedly: " + f.Words + " (exit code " + f.CodeHex + "), but another Mantle is the live shell now (" + shell +
                 "), so it is not started again and Windows is left to that one" + (werr != null ? "; the crash could not be written down (" + werr + ")" : ""));
        o.Kind = "othershell"; return o;
      }

      string why = Why(restart, recent);
      last["restarted"] = restart;
      last["why"] = why;
      string failed = host.WriteCrash(Record(keep, last));
      if (failed != null) {
        restart = false;
        why = "stopped with an error, and the crash could not be written down (" + failed + "), so it was not started again";
        last["restarted"] = false; last["why"] = why;
      }
      host.Log("supervisor: Mantle " + f.Pid + " stopped unexpectedly: " + f.Words + " (exit code " + f.CodeHex + "); crash " + recent + " inside 10 minutes; Mantle " + why);

      if (!restart) {
        GiveUp(host, o, keep, last, failed == null);
        string text = "Mantle stopped unexpectedly " + recent + " time" + (recent == 1 ? "" : "s") + " in 10 minutes, so it was not started again.\n\n" +
                      (o.Verified ? "Windows is back the way it was." : "Windows could not be confirmed back (" + o.Restore + "): run Restore Windows taskbar, or restart Windows Explorer.") +
                      "\n\nThe last time: " + f.Words + ".\n\nStart Mantle again now?";
        if (!host.AskAgain(text)) { o.Kind = "gaveup"; o.Exit = 0; return o; }
        host.Log("supervisor: the user chose to start Mantle again after the crash loop; the crash count starts over");
        keep = new List<long>();
        last["restarted"] = true; last["why"] = "started again by the user after the crash loop";
        host.WriteCrash(Record(keep, last));
      }

      string swhy;
      int pid = host.StartAgain(out swhy);
      if (pid <= 0) {
        last["restarted"] = false; last["failed"] = swhy ?? "it would not start";
        host.Log("supervisor: Mantle could NOT be started again: " + (swhy ?? "it would not start"));
        // a shell that went live during the waits owns Windows now: no restore over it
        string late = host.OtherShell();
        if (late != null) {
          last["why"] = "another Mantle is the live shell (" + late + ")";
          host.WriteCrash(Record(keep, last));
          host.Log("supervisor: another Mantle is the live shell now (" + late + "), so Windows is left to that one");
          o.Kind = "othershell"; o.Exit = 0; return o;
        }
        GiveUp(host, o, keep, last, true);
        o.Kind = "startfailed"; o.Exit = 1; return o;
      }
      last["newPid"] = (long)pid;
      host.WriteCrash(Record(keep, last));
      host.Log("supervisor: started Mantle again as pid " + pid);
      o.Kind = "restarted"; o.NewPid = pid;
      return o;
    }

    /// Stay down: Windows back (verified or said), the record kept with the result for the tray and the next start.
    static void GiveUp(ICrashHost host, CrashOutcome o, List<long> keep, Dictionary<string, object> last, bool write) {
      int code; string output;
      string err = host.RunRestore(out code, out output);
      o.Verified = err == null && Verified(code, output);
      o.Restore = err != null ? "the restore script would not run (" + err + ")" : o.Verified ? "done and verified" : "NOT verified (exit " + code + ")";
      var r = new Dictionary<string, object>();
      r["result"] = o.Restore; r["verified"] = o.Verified;
      last["restore"] = r;
      string werr = write ? host.WriteCrash(Record(keep, last)) : "it could not be written before";
      host.Log("supervisor: Mantle is NOT started again; the Windows taskbar: " + o.Restore +
               (werr == null ? "; crash.json keeps the record for the tray and the next start" : "; the record could not be kept (" + werr + ")"));
    }
  }
}
