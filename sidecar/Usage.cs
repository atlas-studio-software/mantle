// Usage.cs - `usage.*`: the native half of Mantle's one shared background sampler, for power use and
// screen time (lane mantle-ps-sidecar, 2026-11-18). The user approved the feature on 2026-11-18. The
// spec is the "Wire spec (sidecar -> node)" in the power-pull design notes (not in this repo); Node
// (lib/features/usage.js) decides what the numbers mean, this file only reads Windows.
//
//   usage.sample {procs?:true}  one read of everything the power view needs. Node calls it every 300 s
//                               (and every 2 s while the power view is open):
//     t       UTC ms since 1970 (NOT TaskMgr's .NET ticks/10000)
//     cpus, idle (the Idle process's CPU, 100 ns, summed over cores)
//     procs   [{pid, ppid, file, created (FILETIME as a decimal string), cpu (100 ns), session,
//              path|null, desc|null, windows}] - taskmgr.snapshot's fields minus memory and TITLES;
//             null with procs:false (then gpu is not collected either)
//     selfPid, parentPid
//     energy  {ok, time (ms), pkg, cores, gfx, dram (cumulative pWh), reason}: \Energy Meter(*)\Energy and
//             \Energy Meter(*)\Time, RAW values (Energy is cumulative picowatt-hours, Time its clock in ms).
//             Instances rapl_package0_pkg / _pp0 / _pp1 / _dram -> pkg / cores / gfx / dram; W between two
//             reads = dE x 3.6e-9 / ds. A channel this PC does not list is null.
//     meter   {ok, mw, reason}: \Power Meter(*)\Power, which on the laptop it was probed on is the
//             BATTERY'S discharge rate (0 on AC). Not a total on AC; Node words that.
//     gpu     {ok, byPid:{"pid": 100 ns}, reason}: \GPU Engine(*)\Running Time, raw cumulative, summed per
//             pid over every engine (instances pid_<pid>_luid_..._engtype_<x>).
//     battery Battery.Read() as-is
//     ms      wall time of the call; cpuMs this call's own thread CPU (GetThreadTimes: the scheduler's
//             tick-sampled clock, so one call reads 0 or ~15.6 ms; it is right on average)
//   usage.watch {on:true, idleMs} | {on:false} | {op:'status'}
//     ON starts ONE STA thread (its own message loop) that owns: SetWinEventHook(EVENT_SYSTEM_FOREGROUND,
//     out of context), a hidden top-level window (a message-only window never gets the WM_POWERBROADCAST
//     broadcast) with WTSRegisterSessionNotification, and SetTimer timers for the idle check. A second
//     ON restarts the thread (new idleMs); OFF unhooks, unregisters, destroys the window and ends the
//     thread. All of it dies with the process: a crash needs no restore.
//     -> {ok, watching, reason}; status -> {watching, events, cpuMs (the watch thread's CPU), since,
//        displayHeldReadable, reason, idleMs, idle}
//   usage.fg {}  the current foreground in the event's fg/shell shape, plus idleMs (and displayHeld).
//
// EVENTS {ev:'usage', kind, t (UTC ms of the change)}:
//   fg {hwnd (root owner, decimal string), pid, created, file, path, desc}  only when the root window or
//      the process changes; ApplicationFrameHost's frame -> the Store app inside it (TaskMgr.FramedPid).
//      A burst of foreground events within 50 ms collapses to the last (t = the last one's time).
//      The watch's first act is one fg/shell event for the window in front at that moment.
//   shell {}   the desktop (Progman, WorkerW), a taskbar (Shell_TrayWnd, Shell_SecondaryTrayWnd),
//              LockApp.exe, or no window at all
//   lock, unlock (WTS), suspend, resume (WM_POWERBROADCAST; one resume per sleep)
//   idle {idleMs}   no input for idleMs AND no app holding the display on (a video) for idleMs: counting
//                   stops at t - idleMs, where idleMs runs from the LATER of the last input and the last
//                   check that saw the display held (so a long video with no input is never erased).
//                   SystemExecutionState sees other processes' requests (SetThreadExecutionState and
//                   PowerSetRequest both, probed 2026-11-18). Checked every 30 s while active (sooner when
//                   the threshold is near), every 5 s while idle, and at once after an unlock, a resume or
//                   a foreground change while idle. Active again needs INPUT (a held display alone is not).
//   active {agoMs}  input again: counting restarts at t - agoMs.
// Mantle's own Electron windows are NOT skipped (Node classifies them by hwnd).
//
// PRIVACY (the user's rule for mantle.log): no window title is ever read here (class names and pids
// only), and nothing here logs a process name, a path or a title: errors carry pids and Win32 codes.
//
// PASSIVE (--test): all three are allowed. They only read; nothing in Windows changes.
//
// SEE ALSO (reused, not copied): TaskMgr.ReadProcs / Proc / PathOf / DescOf / AppWindowsByPid / FramedPid,
// Battery.Read, Win32.SetWinEventHook / GetAncestor / GetClassName.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mantle {
  [MantleFeature]
  static class Usage {
    public static void Register(FeatureTable t) {
      t.Add("usage.sample", Lanes.Slow, Sample);   // {procs?} -> energy, meter, gpu, battery, the process table
      t.Add("usage.watch", Lanes.Slow, Watch);     // {on, idleMs} | {on:false} | {op:'status'}
      t.Add("usage.fg", Lanes.Slow, Fg);           // {} -> the foreground now + idleMs
    }

    // ------------------------------------------------------------------ native
    static class UN {
      [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentThread();
      [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
      [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
      [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
      [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
      [DllImport("kernel32.dll")] public static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
      [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);

      [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint Size; public uint Time; }
      [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

      [DllImport("powrprof.dll")] public static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out uint outBuf, uint outLen);

      public delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      public struct WNDCLASSEX {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
      }
      [StructLayout(LayoutKind.Sequential)]
      public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; public uint lPrivate; }
      [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
      [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
      public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                                  IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
      [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
      [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
      [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr hwnd, uint min, uint max);
      [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
      [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
      [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
      [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr wp, IntPtr lp);
      [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
      [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr idEvent, uint ms, IntPtr proc);
      [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, UIntPtr idEvent);

      [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
      [DllImport("wtsapi32.dll")] public static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

      [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhOpenQueryW(string src, IntPtr user, out IntPtr query);
      [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr counter);
      [DllImport("pdh.dll")] public static extern int PdhRemoveCounter(IntPtr counter);
      [DllImport("pdh.dll")] public static extern int PdhCollectQueryData(IntPtr q);
      [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhGetRawCounterArrayW(IntPtr counter, ref uint size, out uint count, IntPtr buf);
      [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhGetCounterInfoW(IntPtr counter, bool explain, ref uint size, IntPtr buf);

      // PDH_RAW_COUNTER_ITEM_W { LPWSTR szName; PDH_RAW_COUNTER { DWORD CStatus; FILETIME TimeStamp;
      // LONGLONG FirstValue; LONGLONG SecondValue; DWORD MultiCount; } } - offsets come from these.
      [StructLayout(LayoutKind.Sequential)] public struct RAW { public uint CStatus; public uint TsLow; public uint TsHigh; public long FirstValue; public long SecondValue; public uint MultiCount; }
      [StructLayout(LayoutKind.Sequential)] public struct RAWITEM { public IntPtr Name; public RAW Value; }
    }

    const uint QUERY_LIMITED = 0x1000, THREAD_QUERY_LIMITED = 0x0800;
    const int SystemExecutionState = 16;             // POWER_INFORMATION_LEVEL
    const uint ES_DISPLAY_REQUIRED = 0x00000002;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_QUIT = 0x0012, WM_POWERBROADCAST = 0x0218, WM_WTSSESSION_CHANGE = 0x02B1;
    const uint WM_APP_STOP = 0x8000 + 0x5A;          // WM_APP + n: end the watch, on its own thread
    const int WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    const int PBT_APMSUSPEND = 0x4, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;
    const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    const int PDH_NO_DATA = unchecked((int)0x800007D5);
    const string ClassName = "Mantle.UsageWatch";

    static long NowMs() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L; }
    static string Hex(int v) { return "0x" + v.ToString("X8", CultureInfo.InvariantCulture); }
    static string Hex(uint v) { return "0x" + v.ToString("X8", CultureInfo.InvariantCulture); }

    static long ThreadCpu(IntPtr h) {
      long c, e, k, u;
      return UN.GetThreadTimes(h, out c, out e, out k, out u) ? k + u : 0;
    }
    static double Ms100ns(long v) { return Math.Round(v / 10000.0, 3); }

    // ------------------------------------------------------------------ paths (shared by sample and watch)
    // pid|created -> exe path ("" = could not be read). Pruned to the live set by every sample.
    static readonly object _pathGate = new object();
    static readonly Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.Ordinal);

    static string CachedPath(uint pid, long created) {
      string k = TaskMgr.Key(pid, created), p;
      lock (_pathGate) if (_paths.TryGetValue(k, out p)) return p;
      p = pid == 4 ? "" : TaskMgr.PathOf(pid);
      lock (_pathGate) { if (_paths.Count > 4000) _paths.Clear(); _paths[k] = p; }
      return p;
    }

    // ------------------------------------------------------------------ usage.sample
    static readonly object _pdhGate = new object();
    static bool _eOpened, _gOpened;
    static IntPtr _eq, _cEnergy, _cTime, _cPower, _gq, _cGpu;
    static string _energyWhy, _meterWhy, _gpuWhy;
    static IntPtr _raw = IntPtr.Zero;
    static int _rawLen;
    static readonly int OffFirst = (int)Marshal.OffsetOf(typeof(UN.RAWITEM), "Value") + (int)Marshal.OffsetOf(typeof(UN.RAW), "FirstValue");
    static readonly int OffStatus = (int)Marshal.OffsetOf(typeof(UN.RAWITEM), "Value") + (int)Marshal.OffsetOf(typeof(UN.RAW), "CStatus");
    static readonly int ItemSize = Marshal.SizeOf(typeof(UN.RAWITEM));

    /// The counter types whose RAW FirstValue is the number itself (a raw count) or a running total
    /// (a count or a 100 ns timer), so one read is enough and two reads give an exact difference.
    static bool RawUsable(uint type, bool totalOnly) {
      bool rawCount = type == 0x00010000 || type == 0x00010100;      // PERF_COUNTER_RAWCOUNT / _LARGE_RAWCOUNT
      if (!totalOnly) return rawCount;
      return rawCount || type == 0x10410400 || type == 0x10410500    // PERF_COUNTER_COUNTER / _BULK_COUNT
          || type == 0x20510500 || type == 0x20570500;               // PERF_100NSEC_TIMER / PERF_PRECISION_100NS_TIMER
    }

    static bool CounterType(IntPtr c, out uint type) {
      type = 0;
      uint size = 0;
      int rc = UN.PdhGetCounterInfoW(c, false, ref size, IntPtr.Zero);
      if (rc != PDH_MORE_DATA && rc != 0) return false;
      if (size < 8) return false;
      IntPtr b = Marshal.AllocHGlobal((int)size);
      try {
        rc = UN.PdhGetCounterInfoW(c, false, ref size, b);
        if (rc != 0) return false;
        type = (uint)Marshal.ReadInt32(b, 4);                         // PDH_COUNTER_INFO_W.dwType
        return true;
      } finally { Marshal.FreeHGlobal(b); }
    }

    static IntPtr AddChecked(IntPtr q, string path, bool totalOnly, string what, ref string why) {
      IntPtr c;
      int rc = UN.PdhAddEnglishCounterW(q, path, IntPtr.Zero, out c);
      if (rc != 0) {
        why = "this PC does not list Windows' " + what + " counters (PDH " + Hex(rc) + ")";
        return IntPtr.Zero;
      }
      uint type;
      if (!CounterType(c, out type)) {
        UN.PdhRemoveCounter(c);
        why = "Windows would not say what kind of counter its " + what + " reading is";
        return IntPtr.Zero;
      }
      if (!RawUsable(type, totalOnly)) {
        UN.PdhRemoveCounter(c);
        why = "Windows' " + what + " reading is not a " + (totalOnly ? "running total" : "raw value") +
              " on this build (counter type " + Hex(type) + "), so Mantle does not use it";
        return IntPtr.Zero;
      }
      return c;
    }

    static void OpenEnergy() {
      if (_eOpened) return;
      _eOpened = true;
      int rc = UN.PdhOpenQueryW(null, IntPtr.Zero, out _eq);
      if (rc != 0) {
        _eq = IntPtr.Zero;
        _energyWhy = _meterWhy = "Windows would not open a performance-counter query (PDH " + Hex(rc) + ")";
        return;
      }
      _cEnergy = AddChecked(_eq, "\\Energy Meter(*)\\Energy", true, "processor energy (Energy Meter)", ref _energyWhy);
      if (_cEnergy != IntPtr.Zero) {
        _cTime = AddChecked(_eq, "\\Energy Meter(*)\\Time", true, "processor energy clock (Energy Meter Time)", ref _energyWhy);
        if (_cTime == IntPtr.Zero) { UN.PdhRemoveCounter(_cEnergy); _cEnergy = IntPtr.Zero; }
      }
      _cPower = AddChecked(_eq, "\\Power Meter(*)\\Power", false, "battery discharge (Power Meter)", ref _meterWhy);
    }

    static void OpenGpu() {
      if (_gOpened) return;
      _gOpened = true;
      int rc = UN.PdhOpenQueryW(null, IntPtr.Zero, out _gq);
      if (rc != 0) { _gq = IntPtr.Zero; _gpuWhy = "Windows would not open a performance-counter query (PDH " + Hex(rc) + ")"; return; }
      _cGpu = AddChecked(_gq, "\\GPU Engine(*)\\Running Time", true, "graphics engine time (GPU Engine Running Time)", ref _gpuWhy);
    }

    /// Every instance's raw FirstValue, or null + why. Valid data only (PDH_CSTATUS_VALID_DATA / NEW_DATA).
    static List<KeyValuePair<string, long>> RawArray(IntPtr c, out string why) {
      why = null;
      for (int tries = 0; tries < 4; tries++) {
        uint size = (uint)_rawLen, count;
        int rc = UN.PdhGetRawCounterArrayW(c, ref size, out count, _raw);
        if (rc == PDH_MORE_DATA || (rc == 0 && _raw == IntPtr.Zero && size > 0)) {
          if (_raw != IntPtr.Zero) Marshal.FreeHGlobal(_raw);
          _rawLen = (int)size + 16384;
          _raw = Marshal.AllocHGlobal(_rawLen);
          continue;
        }
        if (rc == PDH_NO_DATA) return new List<KeyValuePair<string, long>>();   // no instances right now
        if (rc != 0) { why = "Windows did not hand over the reading (PDH " + Hex(rc) + ")"; return null; }
        var l = new List<KeyValuePair<string, long>>((int)count);
        for (int i = 0; i < count; i++) {
          IntPtr it = new IntPtr(_raw.ToInt64() + (long)i * ItemSize);
          int st = Marshal.ReadInt32(it, OffStatus);
          if (st != 0 && st != 1) continue;
          IntPtr np = Marshal.ReadIntPtr(it, 0);
          string name = np == IntPtr.Zero ? "" : Marshal.PtrToStringUni(np);
          l.Add(new KeyValuePair<string, long>(name ?? "", Marshal.ReadInt64(it, OffFirst)));
        }
        return l;
      }
      why = "the reading kept growing while it was read";
      return null;
    }

    static IDictionary ReadEnergy() {
      if (_cEnergy == IntPtr.Zero)
        return Json.Obj("ok", false, "time", null, "pkg", null, "cores", null, "gfx", null, "dram", null, "reason", _energyWhy);
      string why;
      List<KeyValuePair<string, long>> e = RawArray(_cEnergy, out why);
      List<KeyValuePair<string, long>> tm = e == null ? null : RawArray(_cTime, out why);
      if (e == null || tm == null)
        return Json.Obj("ok", false, "time", null, "pkg", null, "cores", null, "gfx", null, "dram", null, "reason", "processor energy: " + why);
      object pkg = null, cores = null, gfx = null, dram = null, time = null;
      foreach (KeyValuePair<string, long> kv in e) {
        string n = kv.Key.ToLowerInvariant();
        if (n.EndsWith("_pkg")) pkg = Add(pkg, kv.Value);
        else if (n.EndsWith("_pp0")) cores = Add(cores, kv.Value);
        else if (n.EndsWith("_pp1")) gfx = Add(gfx, kv.Value);
        else if (n.EndsWith("_dram")) dram = Add(dram, kv.Value);
      }
      long tmax = -1;
      foreach (KeyValuePair<string, long> kv in tm) {
        if (kv.Key.ToLowerInvariant().EndsWith("_pkg")) { time = kv.Value; break; }
        if (kv.Value > tmax) tmax = kv.Value;
      }
      if (time == null && tmax >= 0) time = tmax;
      if (pkg == null && cores == null && gfx == null)
        return Json.Obj("ok", false, "time", time, "pkg", null, "cores", null, "gfx", null, "dram", dram,
                        "reason", e.Count == 0 ? "Windows lists no processor energy readings on this PC"
                                               : "Windows lists energy readings, but none for the processor package, cores or graphics");
      return Json.Obj("ok", true, "time", time, "pkg", pkg, "cores", cores, "gfx", gfx, "dram", dram, "reason", null);
    }

    static object Add(object a, long v) { return a == null ? v : (long)a + v; }

    static IDictionary ReadMeter() {
      if (_cPower == IntPtr.Zero) return Json.Obj("ok", false, "mw", null, "reason", _meterWhy);
      string why;
      List<KeyValuePair<string, long>> p = RawArray(_cPower, out why);
      if (p == null) return Json.Obj("ok", false, "mw", null, "reason", "battery discharge: " + why);
      if (p.Count == 0) return Json.Obj("ok", false, "mw", null, "reason", "Windows lists no Power Meter reading on this PC");
      long mw = 0;
      foreach (KeyValuePair<string, long> kv in p) mw += kv.Value;
      return Json.Obj("ok", true, "mw", mw, "reason", null);
    }

    static IDictionary ReadGpu() {
      OpenGpu();
      if (_cGpu == IntPtr.Zero) return Json.Obj("ok", false, "byPid", null, "reason", _gpuWhy);
      int rc = UN.PdhCollectQueryData(_gq);
      if (rc != 0) return Json.Obj("ok", false, "byPid", null, "reason", "Windows did not collect the graphics engine times (PDH " + Hex(rc) + ")");
      string why;
      List<KeyValuePair<string, long>> rows = RawArray(_cGpu, out why);
      if (rows == null) return Json.Obj("ok", false, "byPid", null, "reason", "graphics engine time: " + why);
      var sums = new Dictionary<long, long>();
      foreach (KeyValuePair<string, long> kv in rows) {
        string n = kv.Key;
        if (!n.StartsWith("pid_", StringComparison.Ordinal)) continue;
        int end = n.IndexOf('_', 4);
        long pid;
        if (end <= 4 || !long.TryParse(n.Substring(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) continue;
        long s;
        sums.TryGetValue(pid, out s);
        sums[pid] = s + kv.Value;
      }
      var by = new Dictionary<string, object>(StringComparer.Ordinal);
      foreach (KeyValuePair<long, long> kv in sums) by[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
      return Json.Obj("ok", true, "byPid", by, "reason", null);
    }

    static string Sample(Dictionary<string, object> req, ref object result) {
      var sw = Stopwatch.StartNew();
      IntPtr me = UN.GetCurrentThread();
      long cpu0 = ThreadCpu(me);
      bool withProcs = Json.GetBool(req, "procs", true);
      long t = NowMs();

      string why;
      List<TaskMgr.Proc> procs = TaskMgr.ReadProcs(out why);
      if (procs == null) return "usage.sample: " + why;

      IDictionary energy, meter, gpu = null;
      lock (_pdhGate) {
        OpenEnergy();
        if (_eq != IntPtr.Zero) {
          int rc = UN.PdhCollectQueryData(_eq);
          if (rc != 0) {
            string w = "Windows did not collect the readings (PDH " + Hex(rc) + ")";
            energy = Json.Obj("ok", false, "time", null, "pkg", null, "cores", null, "gfx", null, "dram", null,
                              "reason", _cEnergy == IntPtr.Zero ? _energyWhy : w);
            meter = Json.Obj("ok", false, "mw", null, "reason", _cPower == IntPtr.Zero ? _meterWhy : w);
          } else { energy = ReadEnergy(); meter = ReadMeter(); }
        } else {
          energy = Json.Obj("ok", false, "time", null, "pkg", null, "cores", null, "gfx", null, "dram", null, "reason", _energyWhy);
          meter = Json.Obj("ok", false, "mw", null, "reason", _meterWhy);
        }
        if (withProcs) gpu = ReadGpu();
      }
      if (gpu == null) gpu = Json.Obj("ok", false, "byPid", null, "reason", "not collected: the sample was asked without the process table (procs:false)");

      long idle = -1;
      List<object> rows = null;
      if (withProcs) {
        Dictionary<uint, List<IntPtr>> wins = TaskMgr.AppWindowsByPid();
        var live = new HashSet<string>(StringComparer.Ordinal);
        rows = new List<object>(procs.Count);
        foreach (TaskMgr.Proc p in procs) {
          if (p.Pid == 0) { idle = p.Cpu; continue; }
          live.Add(TaskMgr.Key(p.Pid, p.Created));
          string path = CachedPath(p.Pid, p.Created);
          List<IntPtr> w;
          int nwin = wins.TryGetValue(p.Pid, out w) ? w.Count : 0;
          rows.Add(Json.Obj("pid", (long)p.Pid, "ppid", (long)p.Ppid, "file", p.File, "created", TaskMgr.Str(p.Created),
                            "cpu", p.Cpu, "session", (long)p.Session, "path", path.Length > 0 ? path : null,
                            "desc", TaskMgr.DescOf(path), "windows", nwin));
        }
        lock (_pathGate) {
          var dead = new List<string>();
          foreach (string k in _paths.Keys) if (!live.Contains(k)) dead.Add(k);
          foreach (string k in dead) _paths.Remove(k);
        }
      } else {
        foreach (TaskMgr.Proc p in procs) if (p.Pid == 0) { idle = p.Cpu; break; }
      }

      IDictionary battery = Battery.Read();
      long cpu1 = ThreadCpu(me);
      result = Json.Obj("t", t, "cpus", Environment.ProcessorCount, "idle", idle >= 0 ? (object)idle : null,
                        "procs", rows, "selfPid", (long)Program.OwnPid, "parentPid", (long)Program.ParentPid,
                        "energy", energy, "meter", meter, "gpu", gpu, "battery", battery,
                        "ms", Math.Round(sw.Elapsed.TotalMilliseconds, 3), "cpuMs", Ms100ns(cpu1 - cpu0));
      return null;
    }

    // ------------------------------------------------------------------ the foreground, classified
    static bool IsShellClass(string c) {
      return c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
    }

    /// pid -> its creation time, exe path and file name, through one limited handle (and the process
    /// table only when no handle opens, for the name).
    static void ProcInfo(uint pid, out long created, out string path, out string file) {
      created = 0; path = ""; file = null;
      IntPtr h = UN.OpenProcess(QUERY_LIMITED, false, pid);
      if (h != IntPtr.Zero) {
        try { long c, e, k, u; if (UN.GetProcessTimes(h, out c, out e, out k, out u)) created = c; }
        finally { UN.CloseHandle(h); }
      }
      if (created != 0) path = CachedPath(pid, created);
      if (path.Length > 0) { file = Path.GetFileName(path); return; }
      string why;
      List<TaskMgr.Proc> ps = TaskMgr.ReadProcs(out why);
      if (ps == null) return;
      foreach (TaskMgr.Proc p in ps) {
        if (p.Pid != pid) continue;
        file = p.File;
        if (created == 0) created = p.Created;
        break;
      }
    }

    /// The fg/shell event fields for `hwnd` (kind first). `sig` changes exactly when the root window or
    /// the process does.
    static IDictionary Classify(IntPtr hwnd, out string sig) {
      sig = "shell";
      if (hwnd == IntPtr.Zero) return Json.Obj("kind", "shell");
      IntPtr root = Win32.GetAncestor(hwnd, Win32.GA_ROOTOWNER);
      if (root == IntPtr.Zero) root = hwnd;
      string cls = Win32.GetClassName(root);
      if (IsShellClass(cls) || (root != hwnd && IsShellClass(Win32.GetClassName(hwnd)))) return Json.Obj("kind", "shell");
      int pid;
      Win32.GetWindowThreadProcessId(root, out pid);
      if (pid <= 0) return Json.Obj("kind", "shell");     // the window is already gone
      if (cls == "ApplicationFrameWindow") pid = TaskMgr.FramedPid(root, pid);
      long created; string path, file;
      ProcInfo((uint)pid, out created, out path, out file);
      if (string.Equals(file, "LockApp.exe", StringComparison.OrdinalIgnoreCase)) return Json.Obj("kind", "shell");
      string hs = root.ToInt64().ToString(CultureInfo.InvariantCulture);
      sig = hs + "|" + pid.ToString(CultureInfo.InvariantCulture) + "|" + created.ToString(CultureInfo.InvariantCulture);
      return Json.Obj("kind", "fg", "hwnd", hs, "pid", (long)pid, "created", created != 0 ? TaskMgr.Str(created) : null,
                      "file", file, "path", path.Length > 0 ? path : null, "desc", TaskMgr.DescOf(path));
    }

    /// Milliseconds since the last input in this session, or -1 when Windows would not say.
    static long InputIdleMs(out uint tick) {
      var li = new UN.LASTINPUTINFO();
      li.Size = (uint)Marshal.SizeOf(typeof(UN.LASTINPUTINFO));
      tick = 0;
      if (!UN.GetLastInputInfo(ref li)) return -1;
      tick = li.Time;
      return unchecked((uint)Environment.TickCount - li.Time);
    }

    /// Whether some app holds the display on (a video): false + why when Windows would not say.
    static bool DisplayHeld(out bool held, out string why) {
      uint st;
      uint nt = UN.CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out st, 4);
      if (nt != 0) {
        held = false;
        why = "Windows would not say whether an app keeps the display on (CallNtPowerInformation " + Hex(nt) + "), so idle is judged by input alone";
        return false;
      }
      held = (st & ES_DISPLAY_REQUIRED) != 0;
      why = null;
      return true;
    }

    static string Fg(Dictionary<string, object> req, ref object result) {
      string sig;
      IDictionary r = Classify(Win32.GetForegroundWindow(), out sig);
      uint tick;
      long idle = InputIdleMs(out tick);
      bool held; string why;
      bool readable = DisplayHeld(out held, out why);
      r["t"] = NowMs();
      r["idleMs"] = idle >= 0 ? (object)idle : null;
      r["displayHeld"] = readable ? (object)held : null;
      if (idle < 0) r["reason"] = "Windows would not say when the last input was (GetLastInputInfo failed)";
      else if (!readable) r["reason"] = why;
      result = r;
      return null;
    }

    // ------------------------------------------------------------------ usage.watch
    sealed class Watcher {
      public Thread Thread;
      public uint Tid;
      public IntPtr ThreadHandle, Hwnd, Hook;
      public Win32.WinEventProc Proc;              // kept alive for the hook's life
      public bool Wts;
      public int IdleMs;
      public long Since;
      public volatile int Events;
      public volatile bool Running, Idle;
      public string StartError, Reason;
      public readonly ManualResetEvent Ready = new ManualResetEvent(false);
      // only on the watch thread:
      public string LastSig;
      public IntPtr PendingFg;
      public long PendingAt;
      public bool FgArmed, Asleep;
      public long LastResumeAt;
      public uint IdleInputTick;
      public long LastHeldAt;                       // UTC ms of the last check that saw the display held
      public bool DisplayReadable = true;
      public string DisplayWhy;
    }

    static readonly object _watchGate = new object();
    static Watcher _cur;                            // the running watch, or the last one
    static UN.WndProcFn _wndProc;                   // kept alive for the window class's life
    static bool _classOk;
    static readonly UIntPtr TimerFg = new UIntPtr(1), TimerIdle = new UIntPtr(2);
    const int DefaultIdleMs = 300000, FgCollapseMs = 50, ActiveCheckMs = 30000, IdleCheckMs = 5000;

    static string Watch(Dictionary<string, object> req, ref object result) {
      string op = Json.GetString(req, "op");
      if (op == "status") { result = Status(); return null; }
      if (op != null) return "usage.watch: unknown op (only 'status' is one)";
      object onv;
      if (req == null || !req.TryGetValue("on", out onv)) return "usage.watch needs on:true, on:false or op:'status'";
      bool on = Json.GetBool(req, "on", false);
      int idleMs = Json.GetInt(req, "idleMs", DefaultIdleMs);
      if (on && (idleMs < 1000 || idleMs > 86400000))
        return "usage.watch: idleMs must be 1000 to 86400000 (it was " + idleMs.ToString(CultureInfo.InvariantCulture) + ")";
      lock (_watchGate) {
        string stopWhy = StopLocked();
        if (!on || stopWhy != null) {
          result = Json.Obj("ok", stopWhy == null && !on, "watching", _cur != null && _cur.Running, "reason", stopWhy);
          return null;
        }
        Watcher old = _cur;
        if (old != null && old.ThreadHandle != IntPtr.Zero) { UN.CloseHandle(old.ThreadHandle); old.ThreadHandle = IntPtr.Zero; }
        var w = new Watcher();
        w.IdleMs = idleMs;
        var th = new Thread(delegate () { Run(w); });
        th.IsBackground = true;
        th.Name = "usage-watch";
        th.SetApartmentState(ApartmentState.STA);
        w.Thread = th;
        _cur = w;
        th.Start();
        if (!w.Ready.WaitOne(3000)) {
          result = Json.Obj("ok", false, "watching", false, "reason", "the watch thread did not start within 3 s");
          return null;
        }
        if (w.StartError != null) {
          th.Join(1000);
          result = Json.Obj("ok", false, "watching", false, "reason", w.StartError);
          return null;
        }
        result = Json.Obj("ok", true, "watching", true, "reason", w.Reason);
        return null;
      }
    }

    /// Under _watchGate. Null when nothing runs any more.
    static string StopLocked() {
      Watcher w = _cur;
      if (w == null || w.Thread == null || !w.Thread.IsAlive) return null;
      bool posted = w.Hwnd != IntPtr.Zero && UN.PostMessageW(w.Hwnd, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
      if (!posted && w.Tid != 0) UN.PostThreadMessageW(w.Tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
      if (!w.Thread.Join(3000)) return "the watch thread did not end within 3 s";
      return null;
    }

    static IDictionary Status() {
      Watcher w = _cur;
      bool held; string why;
      bool readable = DisplayHeld(out held, out why);
      if (w == null)
        return Json.Obj("watching", false, "events", 0, "cpuMs", 0.0, "since", null, "displayHeldReadable", readable,
                        "reason", readable ? null : why, "idleMs", null, "idle", false);
      string reason = w.Reason;
      if (!readable) reason = reason == null ? why : reason + "; " + why;
      return Json.Obj("watching", w.Running, "events", (long)w.Events,
                      "cpuMs", w.ThreadHandle != IntPtr.Zero ? Ms100ns(ThreadCpu(w.ThreadHandle)) : 0.0,
                      "since", w.Since > 0 ? (object)w.Since : null, "displayHeldReadable", readable,
                      "reason", reason, "idleMs", (long)w.IdleMs, "idle", w.Idle);
    }

    static bool EnsureClass(out string why) {
      why = null;
      if (_classOk) return true;
      _wndProc = WndProc;
      var wc = new UN.WNDCLASSEX();
      wc.cbSize = (uint)Marshal.SizeOf(typeof(UN.WNDCLASSEX));
      wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
      wc.hInstance = UN.GetModuleHandleW(null);
      wc.lpszClassName = ClassName;
      if (UN.RegisterClassExW(ref wc) == 0) {
        int err = Marshal.GetLastWin32Error();
        if (err != 1410) { why = "Windows would not register the watch window's class (Win32 error " + err + ")"; return false; }  // 1410: already registered
      }
      _classOk = true;
      return true;
    }

    static void Run(Watcher w) {
      try {
        w.Tid = UN.GetCurrentThreadId();
        w.ThreadHandle = UN.OpenThread(THREAD_QUERY_LIMITED, false, w.Tid);
        string why;
        if (!EnsureClass(out why)) { w.StartError = why; return; }
        w.Hwnd = UN.CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "Mantle usage watch", WS_POPUP,
                                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, UN.GetModuleHandleW(null), IntPtr.Zero);
        if (w.Hwnd == IntPtr.Zero) {
          w.StartError = "Windows would not create the watch window (Win32 error " + Marshal.GetLastWin32Error() + ")";
          return;
        }
        w.Proc = OnWinEvent;
        w.Hook = Win32.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, w.Proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (w.Hook == IntPtr.Zero) {
          w.StartError = "Windows would not install the foreground-change hook (SetWinEventHook failed)";
          UN.DestroyWindow(w.Hwnd); w.Hwnd = IntPtr.Zero;
          return;
        }
        var notes = new List<string>();
        w.Wts = UN.WTSRegisterSessionNotification(w.Hwnd, 0);   // NOTIFY_FOR_THIS_SESSION
        if (!w.Wts) {
          int err = Marshal.GetLastWin32Error();
          notes.Add("lock and unlock are not heard (WTSRegisterSessionNotification, Win32 error " + err + ")");
          Program.EmitLog("warn", "usage: WTSRegisterSessionNotification failed (Win32 error " + err + ")");
        }
        uint tick;
        if (InputIdleMs(out tick) < 0) notes.Add("Windows would not say when the last input was, so idle is never reported");
        bool held; string dwhy;
        w.DisplayReadable = DisplayHeld(out held, out dwhy);
        w.DisplayWhy = dwhy;
        if (!w.DisplayReadable) notes.Add(dwhy);
        w.Reason = notes.Count == 0 ? null : string.Join("; ", notes.ToArray());
        w.Since = NowMs();
        w.Running = true;
        EmitFg(w, Win32.GetForegroundWindow(), w.Since);
        CheckIdle(w);
        w.Ready.Set();

        UN.MSG m;
        int got;
        while ((got = UN.GetMessageW(out m, IntPtr.Zero, 0, 0)) > 0) {
          UN.TranslateMessage(ref m);
          UN.DispatchMessageW(ref m);
        }
        if (got < 0) Program.EmitLog("warn", "usage: the watch thread's message loop failed (Win32 error " + Marshal.GetLastWin32Error() + ")");
      } catch (Exception ex) {
        if (w.StartError == null && !w.Ready.WaitOne(0)) w.StartError = "the watch thread failed to start (" + ex.GetType().Name + ")";
        Program.EmitLog("warn", "usage: the watch thread stopped on " + ex.GetType().Name);
      } finally {
        try { Teardown(w); } catch (Exception) { }
        w.Running = false;
        w.Ready.Set();
      }
    }

    /// On the watch thread: everything it holds, released. Idempotent.
    static void Teardown(Watcher w) {
      w.Running = false;
      if (w.Hook != IntPtr.Zero) { Win32.UnhookWinEvent(w.Hook); w.Hook = IntPtr.Zero; }
      if (w.Hwnd != IntPtr.Zero) {
        UN.KillTimer(w.Hwnd, TimerFg);
        UN.KillTimer(w.Hwnd, TimerIdle);
        if (w.Wts) { UN.WTSUnRegisterSessionNotification(w.Hwnd); w.Wts = false; }
        IntPtr h = w.Hwnd;
        UN.DestroyWindow(h);                        // WM_DESTROY -> PostQuitMessage
        w.Hwnd = IntPtr.Zero;
      }
    }

    static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) {
      try {
        Watcher w = _cur;
        if (w == null || !w.Running || hook != w.Hook || w.Hwnd == IntPtr.Zero) return;
        w.PendingFg = hwnd;
        w.PendingAt = NowMs();
        if (!w.FgArmed) { w.FgArmed = true; UN.SetTimer(w.Hwnd, TimerFg, FgCollapseMs, IntPtr.Zero); }
      } catch (Exception ex) { Program.EmitLog("warn", "usage: a foreground change could not be handled (" + ex.GetType().Name + ")"); }
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp) {
      try {
        Watcher w = _cur;
        if (w != null && hwnd == w.Hwnd) {
          if (msg == WM_TIMER) {
            if (wp.ToInt64() == 1) {
              UN.KillTimer(hwnd, TimerFg);
              w.FgArmed = false;
              bool changed = EmitFg(w, w.PendingFg, w.PendingAt);
              if (changed && w.Idle) CheckIdle(w);
            } else if (wp.ToInt64() == 2) {
              CheckIdle(w);
              if (!w.FgArmed) EmitFg(w, Win32.GetForegroundWindow(), NowMs());   // a missed or late change (a Store app's frame filling in)
            }
            return IntPtr.Zero;
          }
          if (msg == WM_WTSSESSION_CHANGE) {
            int what = wp.ToInt32();
            if (what == WTS_SESSION_LOCK) Emit(w, "lock", null, NowMs());
            else if (what == WTS_SESSION_UNLOCK) { Emit(w, "unlock", null, NowMs()); CheckIdle(w); }
            return IntPtr.Zero;
          }
          if (msg == WM_POWERBROADCAST) {
            int what = wp.ToInt32();
            long now = NowMs();
            if (what == PBT_APMSUSPEND) { w.Asleep = true; Emit(w, "suspend", null, now); }
            else if (what == PBT_APMRESUMEAUTOMATIC || what == PBT_APMRESUMESUSPEND) {
              // Both arrive after a wake the user started; one resume per sleep.
              if (w.Asleep || now - w.LastResumeAt > 30000) { Emit(w, "resume", null, now); w.LastResumeAt = now; }
              w.Asleep = false;
              CheckIdle(w);
            }
            return new IntPtr(1);
          }
          if (msg == WM_APP_STOP) { Teardown(w); return IntPtr.Zero; }
          if (msg == WM_DESTROY) { UN.PostQuitMessage(0); return IntPtr.Zero; }
        }
      } catch (Exception ex) {
        Program.EmitLog("warn", "usage: window message " + Hex(msg) + " could not be handled (" + ex.GetType().Name + ")");
      }
      return UN.DefWindowProcW(hwnd, msg, wp, lp);
    }

    static void Emit(Watcher w, string kind, IDictionary fields, long t) {
      IDictionary o = Json.Obj("kind", kind, "t", t);
      if (fields != null) foreach (DictionaryEntry kv in fields) if ((string)kv.Key != "kind") o[kv.Key] = kv.Value;
      Features.Event("usage", o);
      w.Events++;
    }

    /// One fg/shell event when the root window or the process differs from the last one sent.
    static bool EmitFg(Watcher w, IntPtr hwnd, long t) {
      string sig;
      IDictionary r = Classify(hwnd, out sig);
      if (sig == w.LastSig) return false;
      w.LastSig = sig;
      Emit(w, (string)r["kind"], r, t);
      return true;
    }

    /// The idle check, and when the next one runs.
    static void CheckIdle(Watcher w) {
      if (w.Hwnd == IntPtr.Zero) return;
      uint tick;
      long idle = InputIdleMs(out tick);
      bool held = false; string why;
      w.DisplayReadable = DisplayHeld(out held, out why);
      if (!w.DisplayReadable) held = false;
      uint next;
      long now = NowMs();
      if (held) w.LastHeldAt = now;
      if (idle < 0) next = ActiveCheckMs;                   // unreadable: never idle (the status says why)
      else if (!w.Idle) {
        // Inactive since the LATER of the last input and the last check that saw the display held: after
        // an hour of video with no input, idle starts when the video stopped, not an hour ago.
        long since = w.LastHeldAt > 0 ? Math.Min(idle, now - w.LastHeldAt) : idle;
        if (since >= w.IdleMs) {
          w.Idle = true;
          w.IdleInputTick = tick;
          Emit(w, "idle", Json.Obj("idleMs", since), now);
          next = IdleCheckMs;
        } else next = (uint)Math.Max(1000L, Math.Min((long)ActiveCheckMs, w.IdleMs - since + 250));
      } else {
        if (tick != w.IdleInputTick) {
          w.Idle = false;
          Emit(w, "active", Json.Obj("agoMs", idle), NowMs());
          next = (uint)Math.Max(1000L, Math.Min((long)ActiveCheckMs, w.IdleMs - idle + 250));
        } else next = IdleCheckMs;
      }
      UN.SetTimer(w.Hwnd, TimerIdle, next, IntPtr.Zero);
    }
  }
}
