# Mantle.Sidecar: the Electron sidecar's C#, compiled LINKED under .NET 10

The question: can `sidecar/*.cs` (the Electron build's Win32 sidecar, 131 files, compiled by
`sidecar/build.ps1` with the .NET Framework 4 `csc.exe`, C# 5) also compile under modern .NET from
the **same files**, so the native app can use that code while the Electron build keeps shipping it?

## Verdict: works with shims

- **All 131 `sidecar/*.cs` files compile LINKED, unchanged**, as a `net10.0-windows10.0.26100.0`
  class library, in all three define variants of `build.ps1` (winmd, winrt, none). Nothing in
  `sidecar/` was edited and nothing is copied.
- It takes **3 shim files** in `Shims/` (AclCompat, FileStreamShadow, WinRtCompat; this folder only,
  never seen by csc) and one target-framework choice; a 4th, TextCompat, holds helpers the runtime
  edits E6-E9 call. Before them the stock build had **9 errors in 3 files** (winmd variant; 5 in 1
  file for winrt and none). With edit E1 in sidecar/, FileStreamShadow goes (2 compile shims left).
- **Behaviour matches** for everything the cloud runner can answer: the csc-built exe and the
  .NET-built exe (same sources, `SidecarExe=true`) give the same JSON shape for every command tried
  over stdio, the same 55 protocol commands and the same 344 feature commands; the linked code also
  answers in-process (results below).
- What compiles but behaves differently at RUNTIME under .NET is listed in "Runtime differences";
  the fix for each is a one-line `#if NET` sidecar edit (the edit list), proven on the runner not to
  change the csc build.

## Build it

There is no .NET SDK on the dev laptop; it builds on the Windows cloud runner (any box with the
.NET 10 SDK works the same):

```
dotnet build native/src/Mantle.Sidecar/Mantle.Sidecar.csproj -c Release                     # library, winmd variant
dotnet build native/src/Mantle.Sidecar/Mantle.Sidecar.csproj -c Release -p:SidecarVariant=winrt
dotnet build native/src/Mantle.Sidecar/Mantle.Sidecar.csproj -c Release -p:SidecarExe=true   # Mantle.Sidecar.exe under .NET
pwsh -NoProfile -File native/src/Mantle.Sidecar/check.ps1 [-Compare] [-ProveEdits]           # everything below
```

From the laptop, through cloudtest (public repo, free runner; HEAD must be pushed):

```
cloudtest --committed --os windows --no-install --timeout 30 --artifacts "native/out/**" -- "pwsh -NoProfile -File native/src/Mantle.Sidecar/check.ps1 -Compare -ProveEdits"
```

`check.ps1` ends with one line, `SIDECAR-LINK: ok (...)` or `SIDECAR-LINK: FAILED (...)`, and writes
`native/out/sidecar/<variant>/errors-by-code.txt` (and by file, and the warnings), `tests/compare.txt`
and `tests/inproc.txt`; with `-ProveEdits` the same again under `native/out/sidecar/edited/` plus
`edited/proof.txt`.

### The project

| setting | value | why |
| --- | --- | --- |
| `TargetFramework` | `net10.0-windows10.0.26100.0` | The CsWinRT projection of the Windows SDK replaces `build.ps1`'s raw winmd references. **26100, not 22621**: Recorder.cs uses `GraphicsCaptureSession.IncludeSecondaryWindows` / `MinUpdateInterval` (CS1061 on 22621). A project that references this library needs 26100+ too (Mantle.App is on 22621 today). `TargetPlatformMinVersion` stays 19041. |
| `Compile` | `..\..\..\sidecar\*.cs` with `Link=` | linked, never copied; `SidecarExcludeFiles` (empty) is the exclude list |
| defines | `SidecarVariant`: winmd = `MANTLE_WINMD;MANTLE_UIA` (default, the shipped `-NoUnion` build), winrt = `MANTLE_WINRT;MANTLE_UIA` (a csc build on a box with the Windows SDK), none = `MANTLE_UIA` | the same code paths as each csc variant |
| `UseWindowsForms`, `UseWPF` | true | WinForms (the message loop, Screen, Clipboard, Drawing); WPF only for UIAutomationClient/Types (TrayUia, MenuBar, Emoji) |
| packages | System.Management, System.Data.OleDb, System.ServiceProcess.ServiceController 10.0.0 | GAC assemblies in the Framework (`/r:` in build.ps1), Windows-only packages in .NET |
| `LangVersion` latest, `Nullable` disable, `ImplicitUsings` disable | | C# 5 text means the same thing; no global usings to collide (`Timer`, `Path`, ...) |
| `TreatWarningsAsErrors` | false | 27 warnings, all obsolete-API or style (table below) |
| `InternalsVisibleTo` | Mantle.Sidecar.Tests | every sidecar type is internal |

`Main` is not a problem for a library: in a class library `Program.Main` is an ordinary private
method nobody calls. `SidecarExe=true` makes it the entry point (`StartupObject` Mantle.Program).

## Blocker table (compile time)

Stock build, no shims (cloud run 42351907943). Every error, by category:

| category | count | files | workaround chosen | changes the csc build? |
| --- | --- | --- | --- | --- |
| Removed BCL API: Framework ACL overloads `Directory.CreateDirectory(string, DirectorySecurity)`, `Directory.GetAccessControl(string, sections)` x2, `Directory.SetAccessControl(string, DirectorySecurity)` (CS1503, CS1501) | 4 | Elevate.cs:473, 476, 477, 542 | `Shims/AclCompat.cs`: C# 14 static extension members on `Directory` / `File` forwarding to `FileSystemAclExtensions` | no (shim is net-only) |
| Removed BCL API: `new FileStream(path, mode, FileSystemRights, share, size, options, FileSecurity)` (CS1729) | 1 | Elevate.cs:504 | `Shims/FileStreamShadow.cs`: an internal `Mantle.FileStream : System.IO.FileStream` shadows the name inside namespace Mantle; ctors forward to the base, plus that overload (CreateFileW with the security descriptor). STOPGAP: edit E1 replaces it | no |
| WinRT: `WindowsRuntimeMarshal.GetActivationFactory(Type)` gone with built-in WinRT (CS0117) | 2 | Record.cs:163, Recorder.cs:889 | `Shims/WinRtCompat.cs`: static extension member on `WindowsRuntimeMarshal`: RoGetActivationFactory, wrapped as a classic COM object the existing `[ComImport]` cast QueryInterfaces | no |
| WinRT: API newer than the 22621 projection (CS1061) | 2 | Recorder.cs:839, 840 | target framework `windows10.0.26100.0` | no |
| Main / stdio entry point | 0 | Program.cs | none needed (library: Main is just a method) | |
| WinForms | 0 | | `UseWindowsForms` | |
| WMI (System.Management), OLE DB, ServiceController | 0 | 8 + 2 + 3 files | NuGet packages | |
| COM interop: `[ComImport]`, `Type.GetTypeFromProgID/CLSID` (17 files), `InvokeMember` IDispatch late binding (3 files) | 0 | | compile and run as in the Framework (built-in COM is still there on Windows) | |
| COM interop: `dynamic`, `Marshal.GetActiveObject` | 0 | | not used | |
| P/Invoke | 0 | | `ref` passed to an `in` parameter is warning CS9191 only | |
| WinRT awaiting: `AsTask` (MANTLE_WINRT), `WinRt.Await` on `Completed` (MANTLE_WINMD), `IAsyncOperation`, projections | 0 | Bluetooth, Media, Ocr, Notifications, ... | CsWinRT provides `AsTask` and the same projections | |
| csc-only / LangVersion syntax (C# 5 under C# 14, e.g. `field` keyword) | 0 | | | |
| Removed BCL: BinaryFormatter, Thread.Abort, AppDomain create/unload, Remoting, System.Web | 0 | | not used | |
| Type/namespace collisions linked into one assembly | 0 | | none inside the sidecar; see "Folding into Mantle.Core" | |
| Global usings / nullable / implicit usings | 0 | | disabled in the csproj | |
| Warnings as errors | 0 | | `TreatWarningsAsErrors=false` | |

Warnings left (27, winmd; all compile-only, behaviour noted where it differs):
SYSLIB0054 x12 `Thread.VolatileRead/Write` obsolete (works), CS9191 x6 `ref` for `in` (same call),
SYSLIB0014 x2 `ServicePointManager` (PingTest.cs: its settings are no-ops on .NET),
SYSLIB0023 x2 `RNGCryptoServiceProvider` (works), SYSLIB0032 x1 `HandleProcessCorruptedStateExceptions`
(Desktops.cs:467, **ignored by .NET**, see below), WFDEV005 x1 `Clipboard.GetData(string)`
(FilesShell.cs:414, reads a MemoryStream, unverified), CS0162 / CS0414 / CS0472 x1 each (same in csc).

## Runtime differences (compile clean, behave differently under .NET)

| # | where | Framework (csc) | .NET (linked) | fix |
| --- | --- | --- | --- | --- |
| R1 | Record.cs:166, Recorder.cs:811, 918 | `(ProjectedType)Marshal.GetObjectForIUnknown(p)` gives the projected object | InvalidCastException: GetObjectForIUnknown gives a `__ComObject`; CsWinRT makes projected objects with `T.FromAbi` | E2, E3, E4 |
| R2 | Recorder.cs:1096 | `((IRecDxgiAccess)surf)` is a QueryInterface on the RCW | a CsWinRT object is not an RCW; `As<T>()` is the QueryInterface | E5 |
| R3 | ClipHistory.cs:1243, StartupMeta.cs:229 | `Encoding.Default` = ANSI code page | UTF-8 | E6, E7 (+ `Shims/TextCompat.cs`) |
| R4 | AudioMixer.cs:527, SysSound.cs:1335 | `string.GetHashCode()` is fixed, so a `"sid:<hash>"` key survives a sidecar restart | randomised per process: the key changes on every start | E8, E9 (`NetShimText.FrameworkHash`, proven equal to the Framework's) |
| R5 | Desktops.cs:113-143 | `InterfaceIsIInspectable` + `UnmanagedType.HString` marshalling | not supported by .NET 5+ marshalling: expected to throw on first call | NOT FIXED: a net-only interop declaration (IUnknown + 3 IInspectable slots, HSTRING as IntPtr). Untested: the runner's Windows build is outside Desktops' measured list, so both builds refuse before reaching it |
| R6 | Desktops.cs:467 | `[HandleProcessCorruptedStateExceptions]` catches an AccessViolation from explorer's undocumented interface | an AV can never be caught: the process dies | none in code; keep virtual-desktop calls in a separate process (see hosting) |
| R7 | PingTest.cs | `ServicePointManager` settings apply to `HttpWebRequest` | no-ops (WebRequest runs over HttpClient) | unmeasured |
| R8 | whole process | NLS globalization | ICU (culture-sensitive compares can differ; the sidecar is almost all Ordinal) | `System.Globalization.UseNls=true` in the host's runtimeconfig if needed |

Checked and NOT a difference: every `ProcessStartInfo` sets `UseShellExecute` explicitly (the default
flipped to false in .NET); no `Delegate.BeginInvoke`; `Process.Start(string)` is not used.

## The minimal sidecar edit list

`sidecar-edits.psd1` holds them; each is the same shape around ONE existing line:
`#if NET` / the line with the .NET spelling / `#else` / **the original line, byte for byte** /
`#endif`. csc never defines `NET`, so it compiles exactly the committed text.
`check.ps1 -ProveEdits` applies them on the runner's throwaway checkout only, strips the NET branches
and compares with the committed files (must be identical), builds both csc routes (`-NoUnion` and the
union winmd), builds the three .NET variants WITHOUT `Shims/FileStreamShadow.cs`, reruns the tests,
and restores `sidecar/`.

| id | file:line | before | after (the `#if NET` line) |
| --- | --- | --- | --- |
| E1 | Elevate.cs:504 | `new FileStream(path, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, sec)` | `FileSystemAclExtensions.Create(new FileInfo(path), ...same args...)`; then delete Shims/FileStreamShadow.cs |
| E2 | Record.cs:166 | `(Windows.ApplicationModel.DataTransfer.DataTransferManager)Marshal.GetObjectForIUnknown(p)` | `Windows.ApplicationModel.DataTransfer.DataTransferManager.FromAbi(p)` |
| E3 | Recorder.cs:811 | `(IDirect3DDevice)Marshal.GetObjectForIUnknown(insp)` | `WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(insp)` |
| E4 | Recorder.cs:918 | `(GraphicsCaptureItem)Marshal.GetObjectForIUnknown(p)` | `GraphicsCaptureItem.FromAbi(p)` |
| E5 | Recorder.cs:1096 | `((IRecDxgiAccess)surf)` | `WinRT.CastExtensions.As<IRecDxgiAccess>(surf)` |
| E6 | ClipHistory.cs:1243 | `Encoding.Default.GetString(...)` | `NetShimText.Ansi.GetString(...)` |
| E7 | StartupMeta.cs:229 | `Encoding.Default.GetString(...)` | `NetShimText.Ansi.GetString(...)` |
| E8 | AudioMixer.cs:527 | `((sid ?? "").GetHashCode() & 0x7fffffff)` | `(NetShimText.FrameworkHash(sid ?? "") & 0x7fffffff)` |
| E9 | SysSound.cs:1335 | same as E8 | same as E8 |

Not in the list on purpose: the ACL calls (AclCompat.cs keeps the Framework spelling working with no
edit) and `GetActivationFactory` (WinRtCompat.cs). Still open: R5 (Desktops interop declarations),
which needs a real Windows build in Desktops' list to test.

## Behaviour proof

`native/tests/Mantle.Sidecar.Tests` (MSTest, 8 tests), run by `check.ps1`. Last full run: cloud
run 42353435159 at f4dc932, `check.ps1 -Compare -ProveEdits`:

```
SIDECAR-LINK: ok (stock: winmd built, winrt built, none built; 131/131 sidecar files linked, 4 shims;
  tests 8/8 passed, 0 failed, 0 skipped or inconclusive; 0 behaviour differences in compare.txt ||
  edited: winmd built, winrt built, none built; 131/131 sidecar files linked, 3 shims; tests 8/8 passed,
  0 failed, 0 skipped or inconclusive; 0 behaviour differences in compare.txt;
  edit proof ok (9 edits, csc view identical, both csc routes built))
```

What was compared, exactly:

- **In-process** (the linked code called directly from the test, MTA thread, no message loop, no
  `Main`): the wire format (`Json.Write`/`Parse`, strict-number rejection, NaN as null, culture
  independence); `Program.Run` (the real dispatcher, via reflection, with `Passive` set) for `ping`,
  `windows`, `foreground`: all answer; the feature table finds **344 commands, 0 registration
  problems**; `desktops.list`, `taskview.list`, `layouts.monitors`, `maxfold.state`, `clip.status`,
  `printers.list` answer, `elevate.status` answers its normal no-ticket error. `printers.list`
  answers an EMPTY list in-process (its cache is warmed by the running sidecar) and 31 shape paths
  from either exe.
- **Over stdio, csc exe vs .NET exe**, both `--passive`, both the MANTLE_WINMD variant (the csc one
  is `sidecar/build.ps1 -NoUnion`, the shipped build): the `ready` line; `ping` (same shape, 70
  paths; `commands` 55 = 55 identical; `features.commands` 344 = 344 identical; 0 problems each;
  same version 0.3.0 and dpiMode per-monitor-v2); `windows`, `foreground`, `desktops.list`,
  `taskview.list`, `layouts.monitors`, `maxfold.state`, `clip.status`, `printers.list`: **same
  shape every time**; `elevate.status`: the same error words. Values were not compared (pids, hwnds
  and timings differ between any two runs); arrays were compared by the union of their elements' shapes.
- **Time to `ready`** on the runner: csc 322-463 ms, .NET 1003-1173 ms on a first start; 81-164 ms
  vs 121-242 ms when the files were already cached (the edited pass, minutes later).
- **String hash**: `NetShimText.FrameworkHash` equals .NET Framework's `String.GetHashCode` (asked
  of Windows PowerShell 5.1, 64-bit) on 10/10 samples, including non-BMP text and an embedded NUL;
  .NET's own `GetHashCode` gave a different "abc" value in each test process.
- **Edit proof**: all 9 edits applied; the csc view of all 7 touched files is identical to the
  committed text; `build.ps1 -NoUnion` and the union-winmd build (MANTLE_WINRT) both compile the
  edited sidecar; the three .NET variants build without `Shims/FileStreamShadow.cs`; the 8 tests pass
  again on the edited build.

Found along the way, the same in BOTH builds (a sidecar fact, not a port difference): on this
runner's Windows build the first stdout line is a `desktops` warn log, not `ready` (sidecar/README.md
says `ready` is "first line, always"): `Features.Table` is built before `ready` is emitted and
Desktops' registration logs its version refusal then.

NOT verified (said plainly):

- **The recorder role** (R1, R2, edits E2-E5): the test records each exe's own stand-in window, but
  on the runner the stand-in reports it would be visible (`hidden:false`) and refuses, identically
  for both exes, so no capture ran. E2-E5 are proven to compile and to leave the csc build unchanged,
  not proven to record.
- **R5 Desktops interop**: the runner's Windows build (26100) is outside Desktops' measured list, so
  both builds refuse before the IInspectable/HSTRING calls; whether .NET throws there is inferred
  from the documented removal, not measured.
- Anything the runner cannot answer: audio, Bluetooth, Wi-Fi, brightness, the tray, notifications,
  media sessions, the shell-touching (non-passive) commands. Those compiled in all three variants and
  were not run.
- `Clipboard.GetData("Preferred DropEffect")` under .NET 10 WinForms (WFDEV005), PingTest's
  ServicePointManager settings, ICU vs NLS: unmeasured.

## Hosting inside the WinUI app

Short answer: the **code** can be a library; the **process** (`Program.Main`) cannot be hosted, and
three of its roles must stay separate processes.

**`Program.Main` is process-owning; never call it inside the app.** In order, it: sets process DPI
awareness (`SetProcessDpiAwarenessContext`: the app's manifest already decided it, so this would only
fail and report a `dpiReason`); dispatches the separate roles (below); opens the process's stdout;
installs `AppDomain.UnhandledException` (any escaped exception = shut down, then `Environment.Exit`),
`SetConsoleCtrlHandler` and WinForms' `SetUnhandledExceptionMode`; makes a hidden `MsgForm`; starts the
stdin reader (stdin EOF = shut down), the parent watch (`--parent-pid` death = shut down), the lanes
and the Quick Settings warm-up; and ends in `Application.Run`. Every exit path ends in
`Environment.Exit` (Program.cs:1684), which inside the app would end the app.

**Callable as a library** (proven in-process by the tests, on a plain MTA test thread, no message loop):
the wire format (`Json`), the dispatcher for reads (`Program.Run` via reflection today: `ping`,
`windows`, `foreground`), and feature reads (`desktops.list`, `taskview.list`, `layouts.monitors`,
`maxfold.state`, `clip.status`, `elevate.status`, `printers.list`). Two caveats seen in the results:
reads that serve a cache warmed by `Main` (Quick Settings, `printers.list`) answer empty in-process
until something warms them; and `Program.Emit` (160 calls in 48 files: every event and log) writes
to the stdout writer `Main` opened, so in-process events are **dropped silently** (the write is in a
try/catch). A library host needs an event sink: the smallest sidecar edit is
`static class Program` -> `static partial class Program` (C# 5 accepts it; no behaviour change) plus a
net-only half in this folder that sets the writer to a line sink.

**Needs the sidecar's own thread and message pump** (an STA thread running `Application.Run`, NOT the
WinUI UI thread): everything installed on the loop thread in `Main` and through `Program.Post` / the
ui lane: `SetWinEventHook` (37 calls, 13 files), `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hooks (Hotkey,
DeskClick, Emoji), `RegisterHotKey` (4 files), the WinForms timers and `MsgForm` (WM_ENDSESSION,
the hotkey-tap message). Windows delivers these callbacks only to the installing thread's message
loop, and drops a low-level hook whose thread stalls past `LowLevelHooksTimeout`; on the WinUI
dispatcher thread any layout or render stall would cost the Win-key hook. A WinForms message loop on a
second STA thread inside a WinUI process is supported; ClipHistory.cs and Screenshot.cs already run
their own `Application.Run` threads that way.

**Must stay separate processes** (they are already separate roles of the exe, picked in `Main`
before anything else, and each exists for crash safety):

- `--taskbar-guard=<pid>`: waits for that process to die and clears the taskbar region it may have
  left. Its whole point is surviving the death of the process that hid the taskbar; in-process it
  would die with it. If the taskbar hide moves into the app, the guard must watch the APP's pid.
- `--supervise=<pid>`: restarts Mantle's main process after a crash; by definition outside it.
- `--elevated`: the UAC-elevated administrator step, a different token, so a different process.
- `--record` / `--rec-standin` / `--rec-probe`: one recording per process by design (zero RAM when not
  recording); keep it.

**Better kept out of the app's process even though it could load:** Desktops.cs (R5, R6): it calls
explorer's undocumented virtual-desktop interfaces and relied on catching an AccessViolation, which
.NET cannot do: in-process, an AV there ends the app. Same reasoning for anything that injects into or
reads another process's memory (the tray reader).

Practical path: run the **.NET-built `Mantle.Sidecar.exe`** (`SidecarExe=true`, same sources, shown
equivalent over stdio above) as the app's companion process first, exactly like Electron does today,
and move pure reads in-process one at a time. Startup cost to `ready` on the runner: see the numbers
above (the .NET exe pays JIT and loads WinForms + WPF; ReadyToRun or trimming the WPF reference would
be the first things to try, unmeasured).

## Folding into Mantle.Core

- Every sidecar type is top-level in namespace `Mantle` and internal (`Program`, `Json`, `Win32`,
  `Rect`, `Display`, `Media`, `Battery`, ~130 more). Linked into an assembly whose own code is in
  namespace `Mantle.*`, those names are in scope everywhere (`Mantle` encloses `Mantle.Core`), so a
  Core type called `Display` or `Win32` would silently win inside Core and confuse readers. Keep the
  sidecar sources in THIS assembly (Mantle.Sidecar.dll) and reference it, rather than linking them
  into Mantle.Core directly; expose what the app needs through a small public facade here.
- The shims rely on being in namespace `Mantle` (extension members and the FileStream shadow are
  found by namespace scope); they are internal, so nothing outside this assembly sees them.
- `Features.cs` finds feature commands by reflection over `typeof(Features).Assembly`: linked into
  another assembly it would also pick up any `[MantleFeature]` class there. One more reason to keep
  a separate assembly.
