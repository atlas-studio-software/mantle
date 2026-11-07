# Round 2 contracts (step 0, `native-contracts`)

These are the seams the four round-2 lanes code against in parallel (PLAN.md section 7). They hold
interfaces, attributes and plain records only. Nothing here routes, logs, writes a file or talks to
Windows: the real implementations are the lanes' work. Each `Mantle.Core` contract has a test in
`tests/Mantle.Core.Tests/<Area>/` that proves it compiles and can be met, using a small test double.
The two `Mantle.App` contracts (surfaces, theme) are proved by the build and smoke stages.

**Rule for round 2:** a contract is frozen for the round. The lane that owns its folder may fix
words and add helpers next to it. It must not change or remove a member, and it must not add a
member to an interface without the coordinator's OK, because every other lane's test double of that
interface would stop compiling. New needs go in a new interface in the owner's folder, or in the
lane's report.

**Threading, in one line:** the host thread runs the ported `main.js` + `lib/` logic as a
single-threaded async context (Node's event-loop semantics). The UI threads only render. The
platform keeps its own lane threads. Every contract below says which of these it lives on.

## The contracts

**Platform: `Mantle.Core/Platform/IPlatform.cs`** (`IPlatform`, `IPlatformControl`, `PlatformReply`,
`PlatformEvent`, `PlatformStatus`, `PlatformLane`, `PlatformDefaults`). This is everything Mantle asks of
Windows: a wire verb with JSON arguments and a timeout that becomes the request's `ttl`, pushed events
by name, and a status (`starting` / `ok` / `down`) that always carries a reason. Calls never throw. A
refusal, an expiry, a busy lane, a timeout or a failure is a `PlatformReply` with plain words. The
platform picks the lane from the verb, so callers never name a lane. `OnReady` fires now if the
platform is ready, and again after every restart. **Implements:** native-core (R2-1) with
`ProcessPlatform`, the full `lib/sidecar.js` port against today's `Mantle.Sidecar.exe`; phase 7 adds an
in-process transport behind the same interface. **Consumes:** the feature host and every feature
(through the scoped `IFeatureContext.Platform`), the bar frame (app bar, raise, backdrop), the tray
and the diagnostic report. **Fake:** `tests/Mantle.Core.Tests/Platform/FakePlatform.cs` is public to
the whole test assembly; it answers verbs you script, raises events, and moves the status.
**Threading:** host thread only. Replies complete on the host thread, and every handler runs there,
never on a lane thread or the stdout reader.

**Commands: `Mantle.Core/Commands/ICommandBus.cs`** (`ICommandBus`, `[Command]`, `CommandCall`,
`CommandResult`, `CommandHandler`, `CommandRegistration`). Every action a surface, the tray, a test or a
WebView2 fallback page asks of the host is a named command, and the names are Electron's (`qs.set`,
`bar.pin`, `features.list`, `<feature>.<verb>`). Callers build a `JsonObject`. Handlers get a detached
`JsonElement` and answer with a `CommandResult` (`Ok` with a result, or `Fail` with plain words), never
by throwing. A duplicate name is refused with both owners named. `[Command("name")]` marks a handler
method, and `Mantle.Generators` emits the registration, so there is no central list file. Until the
generator lands, `Register` by hand has the same effect. **Implements:** native-core (the bus, the
call log, and the generator). **Consumes:** surfaces (through `ISurfaceContext.CallAsync`), features
(through `IFeatureContext.Command`), the tray, tests. **Threading:** host thread only. A UI thread
reaches the bus through its surface context, which posts to the host and resumes on the UI thread.

**Events: `Mantle.Core/Commands/IEvents.cs`** (`IEvents`). These are named host-to-surface events
with current-value replay (Electron's `broadcast` / `replay`, and `window.mantle.on` in a page).
Payloads are typed, immutable C# values, not JSON, so high-rate events cost no serialisation. A type
mismatch between publisher and subscriber is logged with both type names, never dropped silently.
**Implements:** native-core. **Consumes:** features (`IFeatureContext.Broadcast` / `Replay`) and
surfaces (`ISurfaceContext.On`). **Threading:** host thread. Surface subscriptions are re-posted to
the surface's UI thread, which is why payloads must be immutable.

**Features: `Mantle.Core/Features/IFeature.cs`** (`IFeature`, `[Feature]`, `IFeatureContext`,
`FeatureDescriptor`, `FeatureOption`, `FeatureStop`, `FeatureFlags`). This is the native form of
`lib/features/<name>.js`, with `lib/features/README.md` as the spec. The module fields (name, label,
defaultOn, chords, observe, rest) become the attribute's. The generator turns each `[Feature]` class
into a `FeatureDescriptor`, whose `Create` makes a fresh instance for each install. `InstallAsync` has
10 s to settle. `StopAsync` has 2.5 s and receives its reason and whether it is a rest. The context
mirrors `ctx`: log, scoped platform (4 s default, never throws, released at stop), `Command`,
`OnChord` and `OnKey` (only for listed chords and keys), `Health`, `Enabled` / `OnEnabled`, the
`features\<name>.json` store, `OnOption`, `Broadcast` / `Replay`, and a `Stopping` token. The host
takes the descriptor list as an argument, so it never depends on the generated catalog existing.
**Implements:** native-feature-host (R2-4): the host, the context, the lifecycle, the chord registry.
Features themselves are ported from round 3 on. **Consumes:** every feature. **Threading:** host
thread for install, stop, every context member and every handler a feature registers.

**Data: `Mantle.Core/Data/IStore.cs`** (`IStore`, `IStores`, `StoreWrite`). One writable JSON object
file in the profile, with the same path and shape as Electron's, atomic tmp-and-rename writes, the
`.unreadable` aside rule, and every unknown key written back unchanged. The in-memory document is the
truth: `TryGet` sees a `SetAsync` at once, and the returned task completes when the file write is
done. `UpdateAsync` makes several changes in ONE write. `OnChanged` reports a change on disk made by
someone else (Electron, Helm, an editor). `IStores.Open(relativePath)` returns one instance per file
for the life of the process. `State` and `Reason` reuse the readers' `ReadState`. **Implements:**
native-data-look (R2-2), next to the existing read-only readers. **Consumes:** the feature host
(`features.json`, `keep-ready.json`, each feature's settings file), look-sync, theme, and later every
settings writer. **Threading:** host thread. File I/O may finish off the host thread, but two writes
to one file are never reordered.

**State: `Mantle.Core/State/ShellState.cs`** (`ShellState`, its pieces, `IShellStateHub`). The record
shape of `lib/state.js` (`initialState()`, value for value), plus the pieces other modules add
(`features`, `switcher`, `notifications`, `wallpaper`). Every piece carries a plain-words reason at all
times. `TaskbarPiece.Hidden` mirrors `Autohide` and is never set on its own. Snapshots are immutable,
and lanes change one with `with`. `IShellStateHub.Set` returns false, and publishes nothing, when the
result equals the current snapshot. `Features` is an immutable dictionary that compares by reference,
so the real hub compares it entry by entry. **Implements:** native-core (the hub, and the
`problems()` strip). **Consumes:** every surface (through `ISurfaceContext.State` / `OnState`). Writers
are the pieces' owners: native-core (sidecar, taskbar, workarea, desktop, edgeclip, tbhide),
native-windows (bar, glass, bar backdrop), and native-feature-host (features, hotkey, shortcuts, tray).
**Threading:** snapshots are safe on any thread. `Set` and `OnChange` are host-thread only.

**Surfaces: `Mantle.App/Shell/ISurface.cs`** (`ISurface`, `[Surface]`, `SurfaceDescriptor`,
`ISurfaceContext`). One piece of Mantle's own UI (the bar, the top bar, the launcher, the OSD, ...),
named as in Electron. A surface is created once (hidden, TestQuiet in `--test`), then shown and hidden,
and destroyed on a rest or at quit. `AtStart` surfaces are created when Mantle starts. A surface only
renders. Its context gives it the UI-thread dispatcher, the log, the latest `ShellState` and its
changes, `CallAsync` for host commands, and `On<T>` for host events, all delivered on its UI thread.
**Implements:** each surface's lane (the bar frame is native-windows' in round 2). Native-core
implements the context and creates surfaces from the generated `SurfaceDescriptor` list.
**Consumes:** the shell host in `App.xaml.cs`. **Threading:** the surface's own UI thread for every
member and every handler. It never blocks that thread on the host or the platform. This contract
lives in `Mantle.App` (it needs `DispatcherQueue`), so it is proved by the build stage, not by a unit
test.

**Theme: `Mantle.App/Theme/Theme.xaml`**. This is the ONE theme entry point. `App.xaml` merges it once
and never changes for theme work again. It has no resources of its own. It merges `Theme/Tokens.xaml`
(moved here from the app root, unchanged), and every later theme file is merged here by the theme's
owner. `BarWindow.ThemeToken` now searches nested merged dictionaries, so a token may live in any file
merged under `Theme.xaml`. **Implements:** native-data-look (R2-2) owns `Theme/**` from here on.
**Consumes:** every surface, through `{ThemeResource ...}`. **Threading:** UI threads (XAML resources).
The smoke stage proves it: the bar starts, and its tint comes from `ThemeToken`.

## Ownership check against PLAN.md section 7 (round 2)

| Contract file | Owner from here on (PLAN.md table) | Other lanes |
|---|---|---|
| `Mantle.Core/Platform/IPlatform.cs` | R2-1 native-core (`Platform/**`) | R2-3, R2-4 read only |
| `Mantle.Core/Commands/ICommandBus.cs`, `IEvents.cs` | R2-1 (`Commands/**`) | R2-2, R2-3, R2-4 read only |
| `Mantle.Core/State/ShellState.cs` | R2-1 (`State/**`) | R2-3, R2-4 write pieces through `IShellStateHub.Set`, never edit the file |
| `Mantle.Core/Data/IStore.cs` | R2-2 native-data-look (`Data/**`) | R2-4 read only |
| `Mantle.Core/Features/IFeature.cs` | R2-4 native-feature-host (`Features/**`) | none in round 2 |
| `Mantle.App/Theme/Theme.xaml`, `Theme/Tokens.xaml` | R2-2 (`Theme/**`) | none edit; all use the tokens |
| `Mantle.App/Shell/ISurface.cs` | **no lane in the table**: see 1 below | R2-1 implements the context; R2-3 implements the bar surface |
| `tests/Mantle.Core.Tests/{Platform,Commands,State}/*` | R2-1 | R2-4 uses `FakePlatform` read only |
| `tests/Mantle.Core.Tests/Data/*` | R2-2 | none |
| `tests/Mantle.Core.Tests/Features/*` | R2-4 | none |

No R2 lane needs to edit another lane's contract file to do its own work. The table does leave
these shared or unowned files, each with a proposed fix:

1. **`Mantle.App/Shell/ISurface.cs` has no owner** (R2-3 owns only `Shell/{Windows,Popup,Material}`,
   R2-4 only `Shell/Tray`). Fix: freeze it for round 2 (coordinator). Changes go through a lane
   report.
2. **The bar's start-up path.** `App.xaml.cs` (R2-1) creates the native-infra `BarWindow`, and the
   files at the `Mantle.App` root (`BarWindow.xaml(.cs)`, `Uia.cs`, `Native/*.cs` except
   `TransparentBackdrop.cs`, `AUTOMATION.md`) are in no lane's folders, while R2-3 builds the real bar
   in `Surfaces/Bar/Frame`. Fix: R2-3 owns those root files in round 2 and moves what it keeps into
   its folders, keeping the UI Automation ids the smoke stage checks. R2-1 makes `App.xaml.cs` create
   surfaces from the `SurfaceDescriptor` list. At merge, the coordinator makes the one-line switch from
   `new BarWindow(...)` to the `bar` surface.
3. **`Mantle.Generators` needs shared build files.** A new project means an `.sln` entry and an
   analyzer `ProjectReference` in `Mantle.Core.csproj` and `Mantle.App.csproj`, and PLAN.md 3.4 says
   nobody edits those in parallel. Fix: once native-ci-aot has merged its csproj changes, the
   coordinator lets R2-1 make exactly those three edits. Until then, lanes register by hand
   (`ICommandBus.Register`, `FeatureDescriptor.Of`, `SurfaceDescriptor`), which has the same runtime
   effect.
4. **Root-level `Mantle.Core` files and tests are unowned:** `DataPaths.cs`, `LaunchArgs.cs`,
   `SingleInstance.cs`, `TestQuiet.cs`, and in the tests `ArgsTests`, `LogTests`, `SingleInstanceTests`,
   `ReaderTests`, `ReadOnlyTests`, `WatcherTests`, `TestDir.cs`, `Fixtures/`. Fix: R2-1 takes
   `LaunchArgs`, `SingleInstance`, `TestQuiet` and their tests (Args / Lifecycle). R2-2 takes
   `DataPaths` and the reader tests with `Fixtures/` (Data). `TestDir.cs` stays shared and frozen.
   `DataPaths` is also where R2-1's `--test` isolation lands, so R2-1 asks R2-2 for any change there
   rather than editing it. `ReadOnlyTests` ("native never writes Electron's data") now exempts only
   the `IStore` interface from its no-writing-method check. Its source scan will flag the first real
   atomic write in `Data/`, so R2-2 has to decide how that guard changes, and say so in its report.
   That decision is about the user's live files, not just a test.
5. **`tests/Mantle.UiTests` root files** (`Host.cs`, `Win32.cs`, `BarTests.cs`, the csproj,
   `run-ui.ps1`) are unowned; the lanes own only subfolders. Fix: `BarTests.cs` goes to R2-3.
   `Host.cs` and `Win32.cs` are frozen, and a lane that needs a helper adds it in its own subfolder.
6. **New fields in a ShellState piece** written by R2-3 or R2-4 (for example `BarPiece`) would mean
   editing R2-1's `State/ShellState.cs`. The pieces already carry every field `lib/state.js` and its
   writers use, so none should be needed. If one is, the lane asks in its report, and R2-1 adds it
   as an `init` property with a default, which breaks no caller.

## What the generator will emit (for R2-1; the other lanes must not depend on it)

- In `Mantle.Core`: a catalog of `FeatureDescriptor`s (one per `[Feature]` class, with `Options` from
  an optional `public static IReadOnlyList<FeatureOption> Options` on the class) and the start-up
  registration of every static `[Command]` method.
- For each type with instance `[Command]` methods, a `RegisterCommands(ICommandBus, owner)` that its
  owner calls when it comes up.
- In `Mantle.App`: a catalog of `SurfaceDescriptor`s, one per `[Surface]` class.
- No reflection at run time (Native AOT is the likely build mode). A malformed attribute (a bad name,
  a duplicate, a wrong handler signature) is a build error with plain words.

## Coordinator decisions for round 2 (2026-11-07)

The six shared-file questions above, settled as proposed:

1. `Mantle.App/Shell/ISurface.cs` is frozen for round 2. A lane that needs a change asks the coordinator.
2. native-windows owns the `Mantle.App` root files (`BarWindow*`, `Uia.cs`, `Native/*` except `TransparentBackdrop.cs`,
   `AUTOMATION.md`). native-core builds surfaces from the descriptor list; the coordinator makes the one-line switch
   to the `bar` surface at merge.
3. The source generator: after native-ci-aot merges, native-core alone makes the `.sln` entry and the analyzer
   references in both csproj files. Until then lanes register by hand.
4. `Mantle.Core` root files: native-core takes `LaunchArgs`, `SingleInstance`, `TestQuiet` and their tests;
   native-data-look takes `DataPaths`, the reader tests and `Fixtures/`; `TestDir.cs` stays frozen.
5. `tests/Mantle.UiTests`: `BarTests.cs` goes to native-windows; `Host.cs` and `Win32.cs` are frozen.
6. A new `ShellState` field is asked of native-core, which adds it with a default.

The read-only guard (`ReadOnlyTests`): until switch-over, native must never write the user's real Electron data.
native-data-look implements writes behind one gate: writes go only to a test or scratch profile directory unless the
app runs `--live` as the only shell. The guard test changes from "no writes anywhere" to "no write path that skips
the gate", and keeps failing on any direct file write outside the store.

## native-core-3 additions (2026-11-07, append only)

- `[Command("name", When = nameof(X))]`: the generated registration registers the command only when the bool
  property or field `X` of the same type reads true at that moment (main.js's `if (flags.test) COMMANDS[...] = ...`).
  A `When` that is missing, not a bool, static against an instance handler (or the reverse), or private is MANTLE003.
  `features.fire` uses it (`When = nameof(TestRun)`), so `Catalogs.Register(bus, new FeatureCommands(host))` is the
  only registration and a live run never has the command. `FeatureCommands.Register` (by hand) is gone.
- `Mantle.Core.Look.LookShell` is the look as the app wires it: theme core + look host behind one lock, the
  generated `theme.get/theme.set/look.get/look.sync/look.glass/look.accent` (`Catalogs.Register(bus, look)`), and the
  rebroadcast. Every change of the theme or of its `--lg-*` overrides is published on the host's events as
  `"theme"` with a `ThemePayload(ThemeView View, JsonObject Look)`, every look change as `"look"` (the look host's
  view), on the host thread. Surfaces built from descriptors subscribe `ctx.On<ThemePayload>("theme", ...)`.
- Until the bar is a descriptor surface, `MantleApp.ThemeShown` (static event, raised on the UI thread) and
  `MantleApp.ThemeNow` carry the same payload. **Asked of native-windows:** BarWindow subscribes `ThemeShown` and
  applies `p.Theme == "dark"` through its own `SetTheme`, instead of re-resolving Match Windows from Windows' real key
  (its `Resolve` reads `HKCU\...\Personalize` directly, so a test's scratch key and a live Match Windows change never
  reach the bar today). The app logs which it is at start: `theme: ... reaches N surface(s) directly`.
- `MANTLE_TEST_THEME_KEY=<key under HKCU>`: a `--test` run reads and watches Windows' app mode there instead of
  `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` (ThemeCore.KeyFor); a live run never takes it.
- `--test-popwatch` and `--test-popwatch-away` are known test-only switches (LaunchArgs; ignored with a log line
  without `--test`), for the windows lane to read as `Host.Args.Test && Host.Args.Has("test-popwatch")`.
