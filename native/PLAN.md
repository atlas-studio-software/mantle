# Native Mantle: the plan

Status: plan v1 (2026-11-06), from the parity inventory at origin/master 6ca499f.

- **Contract:** [PARITY.md](PARITY.md), 6,948 rows.
- **Inputs:** [docs/](docs/).
- **Still-open technical unknowns:** two parallel lanes are answering these (see "Open questions"). Where this
  plan depends on them, it says so and names the fallback.

## 1. Goal and promise

Rebuild Mantle fully native in **C# + WinUI 3**, replacing Electron. The aim is that Mantle gets
substantially better and uses far less of the computer.

The promise to the user:

- When this is done it feels like **the same Mantle**: the same features, layout, island theme (light and
  dark), shortcuts and settings, and the user's existing settings and data carried over. It is just faster and
  lighter.
- Every surface is checked side by side against Electron, with screenshots and functional tests, before it
  replaces anything.
- The native app is built **alongside** Electron, which stays live and untouched until native reaches full
  parity. The switch-over happens only then, and Electron stays available as a fallback for a while after it.
- **Electron is the floor, not the ceiling** (the user, 2026-11-07): where native can clearly do better than
  Electron, it does. Each such row is marked `better:` in the parity docs with the reason, and every one is listed
  to the user. Example: running apps keep a stable order in the bar (BAR-289) instead of Electron's z-order,
  where an icon jumps when its window is focused.

**Fully native is the target for every screen.** WebView2 is used only where a screen genuinely cannot be done
well natively, and every such case is flagged to the user, never made silently (section 3.12).

### Evidence so far

The bar spike (branch `spike/winui-bar`) ran on the same GitHub runner as Electron:

| | Processes | Memory | CPU | Paint |
|---|---|---|---|---|
| Native WinUI 3 bar | 1 | 44.5 MB private | 0.05% | under 0.5 s |
| All of Electron Mantle | 15 | about 707 MB | | |

The bar alone in Electron is estimated at 350-375 MB. The saving is all-or-nothing: Electron's main process
(about 237 MB) and the sidecar stay until the last surface moves.

The spike's known defects:

- a 1 px frame line around the window;
- flatter dark glass;
- pins, tray, battery, Wi-Fi and hover were not built.

## 2. Size of the job

| Part | Size | Parity rows |
|---|---|---|
| UI | ~65.7k lines across 28 surfaces (bar 16.6k, settings 10.6k, files 5.7k, taskmgr 5.2k, topbar 5.2k, launcher 3.2k, ...) | 6,060 surface rows |
| `main.js` + core `lib/` | 9.8k + ~19k lines | core 207, ship 189 |
| `lib/features/` | ~40k lines, 60+ features | in the surface files |
| `sidecar/*.cs` | ~74k lines of C#, 136 files | sidecar 119, plus PEND-003 |

There are 1,532 rows with no Electron test today. The native port writes a test for every one of them.

## 3. Target architecture

### 3.1 Decisions at a glance

| # | Decision | One-line reason |
|---|---|---|
| A1 | **C# on .NET 10 + WinUI 3** (Windows App SDK), **unpackaged** and self-contained | Windows draws the glass itself and the sidecar is already C#. Unpackaged keeps `%APPDATA%\mantle` and the app id exactly as they are, whereas MSIX package identity would redirect AppData |
| A2 | Build mode (ReadyToRun vs Native AOT, framework-dependent vs self-contained) **decided from the native-infra bench numbers** | AOT saves startup and RAM, but trimming breaks reflection. Pick by measurement, not by taste |
| A3 | **Final process model: one resident app process, plus two tiny watchdog processes** (taskbar guard, crash supervisor). The elevated helpers and the recorder run only when needed | Each process Electron spends today is RAM. The guard and supervisor must outlive the app, so they stay separate |
| A4 | **The platform layer is reused as source.** `sidecar/*.cs` is linked unchanged into `Mantle.Sidecar` (native-infra is proving the .NET 10 build), behind one `IPlatform` interface with two transports: out-of-process (today's `Mantle.Sidecar.exe` over the same JSON-lines protocol) and in-process | Zero rewrite of 74k proven lines. Electron and native share every sidecar fix during the parallel period. Out-of-process first de-risks the start; in-process comes once the hazards are tested (phase 7) |
| A5 | **Host logic is ported module by module from JS to C#**, keeping the same module boundaries and names, and **the JS tests are ported as the spec** | Keeps every edge case the JS already handles. Reviewable one module at a time, and maps 1:1 onto parity rows |
| A6 | **The host runs on one dedicated thread with a single-threaded async context.** UI threads only render. The platform keeps its own lane threads | The ported JS code assumes a single-threaded event loop, so this means no locks and no races. The UI never waits on Windows calls |
| A7 | **Commands keep Electron's names** (`qs.set`, `bar.pin`, ...) as `[Command("...")]` handlers on a typed command bus | Parity rows, logs, tests and the ledger all speak these names, and a WebView2 fallback page can call the very same commands |
| A8 | **Registration by attribute and source generator** (`[Surface]`, `[Feature]`, `[Command]`), with no central list file | Parallel lanes never edit the same file. This mirrors today's "a feature is found at start-up, no main.js edit" |
| A9 | **One theme as one ResourceDictionary**, generated from `docs/theme-tokens.md` with token names kept 1:1 (`--lg-fill` becomes `LgFill`). Light and dark are theme dictionaries, and a lint test rejects colour literals outside `Theme/` | The one-theme rule (colour only through `--lg-*`) carries over mechanically and stays enforced |
| A10 | **Same data, same paths, same schemas, unknown keys preserved** (`%APPDATA%\mantle`, `features\<name>.json`, `look.json`, ...) | The user's settings carry over with no migration step, and rollback to Electron keeps working |
| A11 | **Same log file, format, rotation and scrubbing** as today | The existing tooling, habits and diagnostic report keep working |
| A12 | **Installer stays NSIS, still named `Mantle-Setup.exe`, same per-user location. The updater is a port of `lib/updater.js`. Signing goes through SignPath** like the Electron installer | The Electron updater can deliver the native app at switch-over. The CI install test and the put-windows-back logic are reused |
| A13 | **Code lives on the long-lived branch `native/main` of the public repo**, merged *from* master every round, all under `native/` | See section 4 for the argument |

### 3.2 Process model

**Phase 1 (from round 2):**

```
Mantle.exe (WinUI 3)  ── JSON lines (stdin/stdout) ──  Mantle.Sidecar.exe        (today's exe, unchanged)
   │                                                     └─ --taskbar-guard=<sidecar pid>   (started by the sidecar)
   └─ Mantle.Sidecar.exe --supervise=<Mantle pid> --supervise-detach   (started by Mantle.exe, out of tree)
```

- `Mantle.exe` is the only new code. It speaks the existing protocol exactly as `lib/sidecar.js` does: ids,
  `ttl`, 8 s call timeout, ready wait, the 3/6/12 s restart ladder, 60 s stable reset, a 15 s health ping,
  the stderr rate limit, and refusal of an outdated exe.
- The sidecar's ~100 `sidecar/test-*.js` scripts stay valid unchanged.
- The supervisor's record (`supervisor-main.json`) names `Mantle.exe`, so a crash restarts native, never
  Electron.

**Final (phase 7, behind the same `IPlatform`):**

```
Mantle.exe (WinUI 3 + host + platform in-process on its own lane/hook threads)
   ├─ Mantle.Guard.exe --taskbar-guard=<Mantle pid>     (tiny; clears the taskbar region if Mantle dies)
   └─ Mantle.Guard.exe --supervise=<Mantle pid>          (tiny; restarts Mantle, gives up after 3 crashes in 2 min and runs the restore)
on demand: Mantle.Sidecar.exe --record (one recording) · --elevated (one UAC action) · mantle.elevated.exe (Task Manager admin helper)
```

- **Why the guard and supervisor stay separate.** The restore model assumes something survives the app's
  death (sidecar-map hazard 6), and Electron Mantle's safety record rests on that.
- **Why `Mantle.Guard.exe` is tiny.** It is built from `Supervisor.cs` plus the guard part of
  `TaskbarHide.cs` only, preferably Native AOT. Today both are full copies of the 74k-line sidecar.
- **The in-process move is a gate, not an assumption.** Ship it only if the bench shows a real saving *and*
  hook health stays green under UI load: low-level hooks must never miss `LowLevelHooksTimeout` during a
  GC or a busy XAML frame. Reliability outranks efficiency in the user's order of priorities. If the gate
  fails, the platform stays out of process, rebuilt with .NET 10 and trimmed, and that is still far below
  Electron.
- **Never two shells.** `Mantle.exe --live` refuses to start, with a plain-words reason, if Electron Mantle is
  running live, detected by its single-instance lock, its exe path, or its `shell-state.json` showing a live
  session in progress. Only one may own the taskbar, the hooks and the app bar. This is a new safety row
  (SAFE, to be added by the core lane).

### 3.3 Threading

| Thread | What runs there | Rule |
|---|---|---|
| **Shell UI thread** | bar, top bar, pop-ups, launcher, switcher, OSD, toasts, desktop layer | Renders only. Every action is a command posted to the host. It never blocks on the platform |
| **App UI threads** | Settings, Files, Task Manager, Wallpapers, Player, Clips: one each, *if* Windows App SDK supports windows on separate threads for the build we ship (open question Q-I3) | A 10k-item Files list or a 300-row Task Manager tick must never jank the bar. Fallback: one UI thread, with heavy work moved off it, list virtualisation, and coalesced updates |
| **Host thread** | ported `main.js` + `lib/` logic, ShellState, feature host, stores, look-sync | Single-threaded async context, the same semantics as Node's event loop. State is immutable snapshots, published to UI threads through `DispatcherQueue` |
| **Platform lanes** | `ui` (the WinForms message loop: WinEvent hooks, timers, hidden windows), `capture`, `slow`, `audio`, `qs`, `focus`, `scan`, `notif` | Unchanged from `sidecar/Lanes.cs`, with their STA/MTA contracts. Out of process in phase 1 |
| **Hook threads** | low-level keyboard (Hotkey), desktop clicks (DeskClick), Emoji, PopWatch | Highest priority, never shared with anything else. Hook health is watched (HookHealth) and re-installs silently-dropped hooks |

### 3.4 Code layout (aligned with native-infra's skeleton; if its final layout differs, the skeleton wins and these ownership rules map onto it)

```
native/
  src/
    Mantle.App/                 WinUI 3 exe
      App.xaml(.cs), Program.cs        startup only (owned by the core lane)
      Theme/                           tokens light/dark, materials, motion (data-look lane)
      Shell/Windows/  Shell/Popup/  Shell/Material/  Shell/Tray/     window kinds + pop-up framework
      Surfaces/<Surface>/              one folder per surface: Bar/Frame, Bar/Tasks, Bar/QuickSettings, TopBar, Launcher, ...
    Mantle.Core/                net10.0-windows, no XAML, headless-testable
      Args/ Lifecycle/ Safety/ Logging/ State/ Platform/ Commands/ Data/ Look/ Features/ Keys/ Update/
      Features/<Feature>/              ported lib/features/<name>.js logic, one folder each
    Mantle.Generators/          source generator for [Surface]/[Feature]/[Command]
    Mantle.Sidecar/             (native-infra) sidecar/*.cs linked into a .NET 10 library
    Mantle.Guard/               (phase 7) tiny guard + supervisor exe
  tests/
    Mantle.Core.Tests/<Area>/   ported lib/**/*.test.js (unit and logic)
    Mantle.UiTests/<Surface>/   (native-infra harness) UI Automation click-through per parity row
    Mantle.Sidecar.Tests/       (native-infra) platform build + behaviour
  tools/parity/                 (native-infra) Electron vs native screenshot pairs
  bench/                        (native-infra) memory/CPU/start bench
  docs/  PARITY.md  PLAN.md
```

**Files nobody edits in parallel:** `App.xaml(.cs)`, `Directory.Build.props`, `Directory.Packages.props`,
the csproj files, and the CI workflows. The coordinator owns them; a lane that needs a change asks in its
report. SDK-style globbing (including XAML `Page` items) means new files never need csproj edits.

### 3.5 How Electron code maps to native

| Electron | Native |
|---|---|
| `ui/<surface>/index.html + .js + .css` | `Surfaces/<Surface>/*.xaml` + view models (CommunityToolkit.Mvvm, source-generated) |
| `window.mantle.call(name, args)` / `on(event)` / replay | a `ICommandBus.Call("name", args)` handler on the host thread / `IEvents` subscription with current-value replay |
| `lib/features/<name>.js` (`install/stop/rest/chords/observe/options`) | `IFeature` in `Mantle.Core/Features/<Name>/` with the same lifecycle: DOWN with a reason, restart without restarting Mantle, a 10 s install deadline, release everything on a failed install, rest while a game runs |
| `lib/state.js` ShellState (every piece carries a `reason`) | `ShellState` record with the same pieces and plain-words reasons, pushed to every surface |
| `BrowserWindow` levels, `setIgnoreMouseEvents`, `focusable:false`, parked windows, `capturePage` | window kinds in `Shell/Windows` (from the native-windowing lane): BarWindow (app bar, non-activating), PopupWindow (activating or not, outside-click dismiss, raise on show), OverlayWindow (click-through, excluded from capture), DesktopLayerWindow (under the icons in WorkerW), AppWindow (own title bar), ToastWindow |
| `lib/test-quiet.js` | the same contract: every `--test` window alpha 0, click-through, never activated, out of app lists. Applied in `Shell/Windows` for every kind |
| `globalShortcut`, sidecar hotkey hook | Alt+Space and the kill switch via `RegisterHotKey`. Everything else stays in the platform hook and arrives as `hotkey` events, the same as today |
| Tray `Menu`, balloon | `Shell/Tray` (Shell_NotifyIcon), the same items, wording and balloons |
| `nativeTheme`, `powerMonitor`, `screen` | `UISettings.ColorValuesChanged`, `WM_POWERBROADCAST` / session notifications, `WM_DISPLAYCHANGE` + per-monitor DPI |

### 3.6 Crash safety (unchanged guarantees, new process)

Every exit path in `docs/electron-architecture.md` section 8 keeps its restore, and the SAFE and SC rows are
the contract:

- **Quit, `--quit` or the kill chord:** stand the supervisor down, stop the features, restore the shell, stop
  the platform, with a 12 s hard stop.
- **Session end:** the same path, through `WM_QUERYENDSESSION` / `WM_ENDSESSION`.
- **Unhandled exception:** write `crash.json` (at most 3 restarts in 10 minutes) and let the supervisor bring
  Mantle back.
- **Native crash or kill:**
  - the guard clears the taskbar region;
  - the platform's own restore runs: in-process it is the app's unhandled-exception path, and out-of-process
    it is the sidecar's stdin EOF;
  - the supervisor restarts Mantle, or after 3 crashes runs `tools/restore-windows.ps1` and tells the user.
- **Explorer restart:** re-attach the desktop layer (back-off 1.5 to 20 s, at most 5 remakes in 10 minutes)
  and retake the app bar. Known hazard: a window parented into Explorer's desktop dies with `0xc000041d`
  when Explorer dies (Q-W3).
- **Dirty start:** if `shell-state.json` says `restored:false`, run the restore first, synchronously,
  before touching anything.
- `tools/restore-windows.ps1` and its siblings ship unchanged. They are the escape hatch with no app running.

### 3.7 Data and settings compatibility

The source is `docs/data-files.md`:

- **(a) Read as-is.** The native app uses `%APPDATA%\mantle` exactly. It reads and writes the same files
  with the same JSON shapes, keeps the same atomic tmp-and-rename writes and the `.unreadable` aside rule,
  and **preserves unknown keys**. Settings key index: settings-mantle.md.
  - **Rule for the fallback period:** native adds no keys that would change Electron's behaviour, and never
    rewrites a file into a shape Electron cannot read.
  - Round-trip tests run on fixtures shaped from every file.
- **(b) One-time migration**, done by the switch-over tool and the installer:
  - re-point the `Mantle` logon task to the native exe;
  - rewrite the player file associations, keeping the same ProgIDs;
  - keep the AppUserModelID `com.owensilva.mantle`;
  - rewrite the supervisor record;
  - archive the Electron updater records so they are never interpreted.
- **(c) Electron-internal data:**
  - **localStorage:** 19 keys, which include the Task Manager layout and the desktop widgets switch.
    Exported by a small Electron command (`profile.export` writes `<userData>\export\localstorage.json`),
    shipped in the switch-over prep round with the user's OK, and imported once by native.
  - **Google Calendar token and client:** these are Electron `safeStorage` ciphertext. Native decrypts them
    once with the DPAPI-protected key in `Local State` (Chromium's AES-GCM `v10` format, same Windows
    account only), then re-protects them with plain DPAPI. If anything fails, it says so and asks the user to
    sign in again.
  - The public privacy page's claims (calendar.readonly only, token encrypted on the PC, sign-out deletes
    and revokes, ...) bind the code. If its wording names Electron's mechanism, the page is updated first
    (PEND-008).
- **Test isolation:** `--test` uses `%TEMP%\mantle-test-<profile>` and a temp `LOOK_SYNC_DIR`, so a test
  never touches the real store. There is a regression test for exactly that, because Electron once leaked a
  test write into the real look store.

### 3.8 Theme and look.json

- `Theme/Tokens.xaml` is generated from `docs/theme-tokens.md`. It has ThemeDictionaries Light and Dark, the
  same token names, motion tokens (durations and cubic-bezier curves, in and out), radii, type, and focus
  rings.
  - PEND-005 adds the contrast rules and the guard-band maths.
  - The tokens that exist only in the dark block get explicit light values, as a `deviation` that fixes a
    known Electron gap.
- **Material:** blurred backdrop picture, then a tint layer with hairlines and lip, then content, with the
  `.62` fallback fill when there is no picture.
  - The see-through decision stays: a **drawn** wallpaper picture aligned to the window's screen position
    (look-sync `glass.render = picture`), costing about 1% CPU, not live capture.
  - Natively this is a Composition surface with a Gaussian blur and a colour matrix for `saturate(150%)`.
  - Native-windowing decides acrylic vs drawn picture for light (Q-W5).
- **look.json:** `Mantle.Core/Look` ports `lib/look-sync.js` byte-compatibly: schema 1, rev logic, tmp
  naming, and the vendored contract tests. Helm and Quartz vendor the same contract.
  - Mantle stays the only writer of the `desktop` record and its stills.
  - It also keeps setting Windows' own wallpaper and lock screen to match.
- **Switching** light/dark (Match Windows or Settings) re-themes every open surface in under 1 s, which is
  today's target. Electron misses it slightly (1.1 to 1.3 s).

### 3.9 Logging

- `--log=<path>` (the logon task passes it): the same file, local-time stamps, 5 MB rotation keeping `.1`
  and `.2`, and scrubbing of the home folder, user and PC name.
- Platform log lines keep the `[sidecar <level>]` prefix.
- `call-log` folding (repeated failures become one line with a count), plain-words errors on screen with the
  codes in the log, and the tray diagnostic report (last 15 lines, health, versions) are all unchanged.

### 3.10 Updater, installer, code signing

- **Updater:** a port of `lib/updater.js` and `update-watch.js`:
  - GitHub `releases/latest`, unauthenticated;
  - the asset `Mantle-Setup.exe`, with the tag matching the version;
  - the "Mantle vX is out" banner and the pending record.
- **Release channels:** until the switch-over, native builds are published only as GitHub **pre-releases**
  tagged `native-v*`. `releases/latest` ignores pre-releases, so no Electron install ever picks one up early.
- **Installer:** a hand-written NSIS script reusing `installer/installer.nsh` logic:
  - per-user to `%LOCALAPPDATA%\Programs\Mantle`;
  - `Mantle.exe --quit` before replacing files, and put-windows-back on uninstall;
  - the same retry/cancel boxes and control names that `ci-install-test.ps1` drives;
  - the restore scripts at a fixed relative path.
- **Upgrade from Electron:**
  - The installer quits Electron with its own `--quit` and waits for the restore.
  - It **moves the Electron app folder to a fallback folder instead of deleting it**, runs the migration
    (3.7b/c), and re-points the logon task.
  - It adds a Start menu entry "Mantle (classic)" for rollback (section 8).
- **Signing:** `Mantle.exe`, `Mantle.Sidecar.exe`, `Mantle.Guard.exe`, `mantle.elevated.exe` and the installer
  are all signed through the SignPath project that the installer work is setting up. It needs a second
  artifact configuration (Q-O4), and builds happen on GitHub-hosted runners from the public repo, which
  SignPath's open-source programme requires.
- **Task Manager admin helper:** it checks the client image, so it must accept the native `Mantle.exe`. That
  means a helper version bump and one UAC click by the user to reinstall it.

### 3.11 Test strategy

| Layer | What | Where it runs |
|---|---|---|
| Unit | ported `lib/**/*.test.js`, same cases, xUnit/MSTest in `Mantle.Core.Tests` | locally if the .NET SDK is installed (quick), else cloud |
| Platform | `sidecar/test-*.js` unchanged against the out-of-process exe. For phase 7, a stdio-compat host of the in-process build, so the same scripts run against it | cloud |
| UI behaviour | UI Automation click-through (native-infra's `Mantle.UiTests`), one or more tests per parity row. Every interactive element's `AutomationId` = its Electron DOM id or test name, so rows map 1:1 | cloud (visible windows are fine there) |
| Screenshot parity | `native/tools/parity`: the same fixture (fixed wallpaper, window-list fixture, frozen clock, fixed data) in Electron and native, light + dark, 100% + 150% DPI, per surface state. Diff with thresholds, plus the PNG pairs reviewed by the coordinator | cloud |
| No-overlap | hover and focus boxes never touch their neighbours (the user's 10-15 rule), measured from UIA bounding rects on every surface (PEND-010) | cloud |
| Bench | private MB, private working set, CPU idle, cold start, pop-up open latency, per surface and in total, vs Electron on the same runner (native/bench) | cloud |
| Live-only | the `live-only` rows, with exact steps; run by the user, or by a lane only when the user has been idle 3 min and has said go | the user's desktop |
| Safety drills | kill -9 the app, hang the platform, restart Explorer, sign out; Windows must be restored every time | cloud, plus one live drill with the user's go before the switch |

Rules:

- **Local runs are invisible and isolated:** `--test`, passive platform, temp profile, never on the user's
  screen, never while a game runs.
- Heavy suites (UI, screenshot, bench) go through the free `--committed` cloud recipes on the public repo, so
  a lane pushes its branch before running them.
- Judge every run by its summary line.
- **Budgets** (set firmly after round 2's bench):
  - total private memory ≤ 25% of Electron's on the same runner (about ≤ 175 MB with the default surfaces);
  - no single surface heavier than its Electron counterpart;
  - CPU idle ≤ Electron's;
  - pop-up open latency ≤ Electron's;
  - the PERF.md budgets (PKG-040..051) hold.

### 3.12 WebView2 policy

Native first. WebView2 is allowed only where the content *is* web, or where a measured native attempt fails
parity. Each case gets a `webview2:<ledger id>` status and is **flagged to the user**. When it is used:

- one shared WebView2 environment;
- created on demand, destroyed when hidden or idle (the RAM rule);
- it hosts the **existing** `ui/<page>` with a `window.mantle` bridge onto the same command bus. The fallback
  is the real Electron page, not a third implementation.

| Row(s) | Verdict | Why |
|---|---|---|
| Web (HTML/Lively) wallpapers, WALL-111..120 and related | **WebView2 by necessity** | the content is HTML with the Lively shim |
| Top bar "Mission Control" panel | **WebView2 by necessity** | it is a live web page |
| Media player, PLAY-*; clips gallery playback | **candidate** | Chromium plays .webm/.mkv and its subtitle and track rules. MediaPlayerElement needs Store codec extensions. Try native first, then decide with the user |
| Look-away, LKA-* | **native candidate** (ONNX Runtime or Windows ML with the same BlazeFace model) | match the existing y4m fixtures. WebView2 only if that fails, since a camera page in WebView2 is heavy |
| Files PDF preview | **native** (`Windows.Data.Pdf`) | no WebView2 needed |
| Spotify "Up next" | **native** C# CDP client injecting the same JS string verbatim | no browser needed |
| Unfold animation, live window thumbnails | **native** (Composition; `DwmRegisterThumbnail` onto the window's HWND, as today) | no WebView2 needed. Q-W7 covers XAML overlap with thumbnails |

## 4. Where native code lives (and why `native/main` is right)

Native code lives on the **long-lived branch `native/main` of the public repo**, entirely under `native/`.
`native/main` merges origin/master in every round; it is never merged into master before the switch-over.

- **Why not master directly?**
  - master is the Electron ship line. Every push there feeds the go-live and "is master ahead of live?"
    checks, and native commits would make that permanently noisy.
  - Electron's builder config packages `**/*`, so `native/` on master would ship inside the Electron app
    until `!native/**` is added.
  - Native lanes would race the Electron sessions' pushes.
- **Why not a separate repo?** The native build *links `sidecar/*.cs` from this tree*. A separate repo would
  fork the 74k-line platform, and every Electron sidecar fix would need porting by hand. The ledger also needs
  master's history next to native's.
- **Why the public repo?** Free GitHub runners for the Windows builds and UI suites (`--committed` cloud
  recipes), and SignPath's open-source programme needs public builds. The spike is already public.
- **Merges are conflict-free by construction:** native lanes never edit outside `native/`, so merging master
  into `native/main` only ever adds Electron changes.
- **Lane branches:** `native/<lane>` is cut from `native/main` and pushed (needed for the cloud recipes and
  passes the public-repo guard). The coordinator reviews `git diff` and merges it into `native/main`.
- **The only Electron-side changes** are the switch-over prep in round 9: the `profile.export` command,
  `!native/**` in `installer/electron-builder.yml`, and anything the ledger shows is needed. They go through
  master with the normal Electron ship flow and the user's OK.
- **At switch-over:** `native/main` merges into master. The Electron files stay in the tree for the fallback
  period, and removing them later is the user's call.

## 5. Keeping up with Electron during the port

The parity ledger (`docs/ledger.md`) is keyed by master commit. The coordinator follows it at the start of
every round:

1. `git log <last processed>..origin/master --name-only`.
2. Classify each commit through the path map: `none`, `rows+`, `rows~`, `rows-`, `sidecar` or `data`.
3. Add or edit rows. A changed row that was already past `todo` becomes `stale@<sha>`.
4. Merge master into `native/main`.
5. Record the commits and move "last processed".

Because native shares the sidecar sources, platform fixes arrive with the merge and only need the platform
tests re-run. A switch-over requires zero unprocessed commits and zero `stale` rows. A small
`native/tools/ledger.js` (path classification, a draft of the rows) is a later chore: the round 9 ship lane,
or sooner if the ledger pass starts taking more than a few minutes.

## 6. Phases and port order

Shell surfaces come before apps, because they are what the user touches all day and they prove the hardest
windowing. Within each phase, lanes are split by file.

| Phase | Rounds (est.) | Content |
|---|---|---|
| 0 | 1 (now) | native-infra (skeleton, linked sidecar build, CI, UI and screenshot harness, bench), native-windowing (window primitives), this plan and the inventory |
| 1. Foundations | 2 | contracts, host core + safety, data + look + theme, window kinds + pop-up framework + bar frame, feature host + keys + tray |
| 2. Bar | 3-4 | tasks/pins, quick settings, tray + notification centre + clock/calendar, media + game/power mode + downloads + badges |
| 3. Shell surfaces | 3-5 | launcher + search, OSD + toasts, top bar, desktop layer + widgets + strip + hot corners, switcher + task view, snap/layouts + unfold, clipboard + emoji + welcome, wallpapers |
| 4. Background features | 6 | auto-pause, app power, RAM reclaim, maxfold, Spotify helpers, game watch (host logic, little UI) |
| 5. Apps | 6-8 | Settings (framework + Mantle pages + Windows pages), Files, Task Manager, screenshot + OCR, screen recording + clips, notes + timer + always-on-top + project, player + look-away |
| 6. Ship | 9 | updater, installer, signing, autostart, migration + switch tooling, Electron-side export (with the user's OK) |
| 7. Platform in-process | 9 | the `IPlatform` in-process transport, the 14 threading hazards as tests, PEND-003, `Mantle.Guard.exe`, and the bench gate |
| 8. Verify and switch | 10-12 | full side-by-side sweeps + fix lanes, ledger catch-up, safety drills, the user's dogfood switch, fallback period |

## 7. Lanes: rules and the next two rounds

Rules for every round:

- **At most 4 lanes at once, one level deep.** No nested fan-out: the inventory round's nested agents pushed
  the laptop to 100% CPU and spent about 9 points of the weekly budget in 40 minutes.
- **Builds and heavy tests run in the cloud.** The laptop has no .NET SDK; installing one is the user's call
  (Q-O1).
- **Each lane owns:**
  - its source folders and its test folders;
  - the status column of its parity rows (and nothing else in the parity files);
  - the expansion of any PEND rows in its area, as its first task;
  - confirming that the Electron test it cites really covers each row (PEND-012).
- **A lane is done when:**
  - every row it owns is `tested`;
  - its screenshot pairs and bench numbers are produced through the cloud recipes;
  - it reports: rows `tested`, test names, numbers, deviations it proposes, and what is left.
  - Then the coordinator reviews the diff, looks at the pairs and marks rows `verified`.
- **Models:**
  - opus-xhigh for cross-cutting or safety-critical lanes;
  - opus-high for bounded surfaces;
  - sonnet-high for read-only review lanes (for example an independent screenshot-pair review).

### Round 2: foundations

**Preconditions:**

- native-infra's skeleton (`Mantle.App`, `Mantle.Sidecar` linked build, CI, `Mantle.UiTests`,
  `tools/parity`, `bench`) is merged into `native/main`;
- native-windowing has reported its primitives;
- `native/main` exists, with origin/master merged in.

**Step 0, `native-contracts`** (coordinator or opus-high, about 1 hour, alone). One small commit on
`native/main` holding interfaces and attributes only, so the four lanes can code against them in parallel:

- `Mantle.Core/Platform/IPlatform.cs`: verbs with lane and ttl, events, status;
- `Commands/ICommandBus.cs` + `[Command]`;
- `Features/IFeature.cs` + `[Feature]` + `IFeatureContext`;
- `Data/IStore.cs`;
- `State/ShellState.cs` (the record shape from `lib/state.js`);
- `Mantle.App/Shell/ISurface.cs` + `[Surface]`;
- an empty `Theme/Theme.xaml` merged once in `App.xaml`;
- a fake `IPlatform` for tests.

Then four parallel lanes:

| Lane | Model | Owns (files) | Parity rows | Delivers |
|---|---|---|---|---|
| **R2-1 `native-core`** | opus-xhigh (safety-critical, cross-cutting) | `Mantle.App/{App.xaml(.cs),Program.cs}`, `Mantle.Core/{Args,Lifecycle,Safety,Logging,State,Platform,Commands}/**`, `Mantle.Generators/**`, `tests/Mantle.Core.Tests/{same}/**` | core.md CORE, SAFE, LOG, and the WIN test-quiet rows; sidecar.md protocol/client rows; ship.md PKG flag rows | flags + exit codes (0/1/2/3/4); single instance + `--quit` / `--open`; the never-two-shells refusal; `--test` isolation + invisible windows; log tee; ShellState; `ProcessPlatform` (the full lib/sidecar.js port against today's `Mantle.Sidecar.exe`); supervisor start with the native record; dirty-start guard; shutdown and session-end; crash.json; command bus + source generator |
| **R2-2 `native-data-look`** | opus-high | `Mantle.Core/{Data,Look}/**`, `Mantle.App/Theme/**`, `tests/Mantle.Core.Tests/{Data,Look}/**`, `tests/Mantle.UiTests/Theme/**` | ship.md DATA; shell.md LOOK (theme, accent, look.json sync, host apply; the desktop-record publisher waits for wallpapers); settings-mantle key index; PEND-005, PEND-009 | stores for every data-files (a) file with exact paths, atomic writes, the `.unreadable` rule, unknown-key preservation and round-trip fixtures; the look-sync C# port with the vendored contract tests; `Tokens.xaml` light/dark from theme-tokens.md; Match Windows switching under 1 s; accent; the no-colour-literal lint |
| **R2-3 `native-windows`** | opus-xhigh (Win32/DWM edge cases, the most visible) | `Mantle.App/Shell/{Windows,Popup,Material}/**`, `Mantle.App/Surfaces/Bar/Frame/**`, `tests/Mantle.UiTests/{Windows,Popup,BarFrame}/**` | bar.md sections "Bar shell: shape, geometry, reveal, hover, glass, state" (116), "Windows shell integration" (64), "Pop-up framework" (42), "Clock" (12; Google Calendar is round 4); core.md WIN (raise, foreground grant, parked windows, popwatch) | window kinds from the windowing primitives (no frame line); the bar island (pill/edge, hover reveal/dock, app bar reservation through the platform, multi-monitor, DPI); the glass material; the pop-up framework (dismiss, raise, focus or no focus, keyboard); clock + Start button. Bar screenshot pairs light/dark at 100%/150% |
| **R2-4 `native-feature-host`** | opus-high | `Mantle.Core/{Features,Keys}/**` (the host, not features), `Mantle.App/Shell/Tray/**`, `tests/Mantle.Core.Tests/{Features,Keys}/**`, `tests/Mantle.UiTests/Tray/**` | core.md FEAT, KEY, TRAY | the feature lifecycle (DOWN + reason, restart, 10 s deadline, release on failure); game rest (`_game-watch` hub, rest/wake); idle unload (10 min); keep-ready policy (`keep-ready.json`); the chord registry on platform `hotkey` events, with hand-back to Windows when a feature is off, unhealthy or resting; Alt+Space + kill switch; the tray icon, menu, balloons and diagnostic report; `--test-chord` / `--test-key` equivalents |

### Round 3: the bar's tasks, quick settings, launcher, OSD and toasts

Precondition: round 2 merged, and its bench numbers are in.

| Lane | Model | Owns (files) | Parity rows | Notes |
|---|---|---|---|---|
| **R3-1 `native-bar-tasks`** | opus-xhigh (largest bar area, thumbnails, drag) | `Surfaces/Bar/Tasks/**`, `Mantle.Core/Features/{Pinned,WindowList,JumpList,Icons}/**`, matching tests | bar.md "Pins, running apps, clicks, previews, app menu, reorder, badges, overflow" (134), "Jump list" (18), "Keep on top and top bar hide" (19); PEND-011 | reads the Windows pin folder + `jumplist.json` unchanged; DWM thumbnails onto the HWND; real OS drag to reorder |
| **R3-2 `native-bar-qs`** | opus-high | `Surfaces/Bar/QuickSettings/**`, `Mantle.Core/Features/{QuickSettings,PowerMode}/**`, matching tests | bar.md "Quick settings, network, Bluetooth, sound, battery" (202), "Power mode" (19) | the Wi-Fi password field in a non-activating pop-up is the known hard piece (Q-W6) |
| **R3-3 `native-launcher`** | opus-xhigh (search ranking, focus rules, latency) | `Surfaces/Launcher/**`, `Mantle.Core/Features/{Search,Run,LauncherIndex}/**`, matching tests | launch.md LAU (277) | Start Menu `.lnk` + PowerShell profile functions + ms-settings index; Win tap / Alt+Space; latency at or below Electron's |
| **R3-4 `native-osd-toasts`** | opus-high | `Surfaces/{Osd,Toasts}/**`, `Mantle.Core/Features/{Osd,Toasts,Notifications}/**`, matching tests | launch.md OSD (115), TST (134) | taking over Windows' OSD and toasts, with every restore-on-crash writer ported; the notification centre UI comes later in the bar, on this lane's store |

### Rounds 4 and later (sketch, refined after each round)

| Round | Lanes |
|---|---|
| 4 | bar tray + notification centre + clock/Google Calendar (all 104 calendar rows, privacy claims first); bar media + game mode + downloads + Helm badge + quick links (PEND-006); top bar (382 rows, opus-xhigh); desktop layer + widgets + status strip + desktop menu + hot corners |
| 5 | switcher + task view; snap/layouts + unfold; clipboard + emoji + welcome; wallpapers engine + window + the look desktop publisher (web wallpapers = WebView2, flagged) |
| 6 | Settings framework + Mantle pages (PEND-004); Settings > System; the other Windows pages; background features (auto-pause wire-exact with Quartz, app power, RAM reclaim, maxfold) |
| 7 | Files (PEND-007); Task Manager A (Processes, Performance, Settings, Get ready to game); Task Manager B (Details, Services, Startup (PEND-013), Users, App history, admin helper); Spotify helpers + OCR + GIF + tools hub |
| 8 | screenshot; screen recording + clips; notes + timer + always-on-top + project; player + look-away (the codec and camera decisions go to the user) |
| 9 | ship (updater, installer, signing, autostart); migration + switch tooling + Electron export (with the user's OK); platform in-process (phase 7 gate); perf pass |
| 10-11 | side-by-side sweeps (every surface × light/dark × DPI) by sonnet-high review lanes + opus fix lanes; ledger catch-up |
| 12 | safety drills, the user's dogfood switch, fallback period |

## 8. Switch-over and rollback

**Gate, all required:**

1. PARITY done: every row `verified`, or approved as `deviation`, `webview2`, `dropped` or `live-only`
   (with the steps run).
2. Ledger caught up, zero `stale`.
3. Budgets met (3.11).
4. Full native suite green on the cloud, judged by summary lines.
5. Safety drills passed on the cloud, plus one live drill with the user's go.
6. A carry-over dry run on a copy of the user's real profile, done locally (it holds tokens, so it never
   leaves the PC): every file round-trips with no lost keys.
7. The user says go.

**Steps on the user's machine** (the live clone; the go-live recipe gets a native variant):

1. Build native into a side folder.
2. Export localStorage from the running Electron app (`profile.export`).
3. Do a migration dry run and show the user what will carry over.
4. Quit Electron with `--quit` and confirm the restore.
5. Run the migration for real: gcal re-protect, logon task, associations, supervisor record.
6. Start native through the scheduled task, then confirm the state line and every surface on screen.

**Rollback (one command or one tray item, "Switch back to classic Mantle"):**

1. Native `--quit`, which restores Windows.
2. Re-point the logon task to Electron.
3. Start Electron.

Settings survive because native kept the files and shapes Electron reads (3.7). A matching "switch to
native" exists for retries.

**Fallback period:**

- Electron stays installed and buildable on master until the user says to remove it (decided 2026-11-07:
  no fixed length). After the switch-over, ask the user once a week whether it can go.
- Public users get native only after the user's own dogfood. It arrives as a normal release, which the
  Electron updater delivers, and its installer keeps the Electron folder for rollback.

## 9. Risks

| Risk | Mitigation |
|---|---|
| **Scope and budget**: 6,948 rows, ~12 rounds of 4 lanes | Pace to the weekly cap; sonnet for reviews; no nested fan-out; shell first so value lands early |
| **Moving target**: Electron keeps shipping | the ledger every round; the shared sidecar sources; zero-stale gate |
| **Glass fidelity** (light acrylic vs drawn picture; flatter dark glass in the spike) | the windowing lane (Q-W5); screenshot pairs with numeric thresholds; the picture approach already has Electron's tuned formula |
| **Focus and activation**: non-activating pop-ups with text input, foreground grants, parked windows | windowing primitives first (round 2); Q-W6; UIA tests for keyboard paths |
| **Desktop layer in WorkerW** dies with Explorer (`0xc000041d`) | re-attach back-off ported; Q-W3; Explorer-restart drill in the suite |
| **Hook starvation in-process** | out-of-process first; in-process only behind the gate with hook-health tests under load |
| **WebView2 creep** (each instance costs a browser process) | policy 3.12; on demand, destroyed when idle; every case flagged |
| **Codecs** for the player and clips | try native first; the user decides on Store extensions vs WebView2 |
| **Electron-internal data** (localStorage, safeStorage) | export command + one-time decrypt; sign-in-again fallback in plain words |
| **Admin helper and signing** for new exe names | helper version bump + one UAC; SignPath artifact config (Q-O4) |
| **WinUI 3 platform limits**: memory per window, threads per window, AOT | native-infra bench; on-demand windows; fallbacks named in 3.3 and A2 |
| **Two shells at once** | never-two-shells refusal; native never runs `--live` on the user's PC before the drills |
| **Inventory gaps** (PEND-001..014, test column by grep) | each owning lane expands its PEND rows and confirms its tests first |

## 10. Estimate (an estimate, not a commitment)

About **12 rounds after this one**, realistically **10 to 16**. The range depends on how many fix rounds the
side-by-side sweeps need and how much Electron changes meanwhile. A round is 2 to 4 lanes plus review.

At the current weekly budget this is weeks, not days. The inventory alone used about 9 points of the weekly
budget, and implementation rounds on Opus will cost more per round. Pace one or two rounds a day under the
cap.

## 11. Open questions

**For native-infra:**

- **Q-I1** Does the linked `Mantle.Sidecar` (.NET 10) behave identically to the csc build? That covers the
  three compile-blocker shims, the 26100 projection, the WinRT files via CsWinRT, the UIA assemblies, and the
  `Features.Table` reflection scan under trimming. This decides whether phase 7 is possible.
- **Q-I2** Build mode: ReadyToRun vs Native AOT, self-contained vs framework-dependent. What are the measured
  private MB, cold start, package size and trim warnings for a WinUI 3 app?
- **Q-I3** Can Windows App SDK host windows on separate UI threads in the version we ship? This decides 3.3.
- **Q-I4** Screenshot parity for windows that must stay invisible locally: is it cloud-only, or does a
  render-to-bitmap path work while the window is invisible?
- **Q-I5** Does UI Automation work on `--test` windows (alpha 0, click-through, no activation), so behaviour
  tests can run locally too?
- **Q-I6** The CI matrix and minutes for a full native suite on public runners. Which `--committed` cloud
  recipes should exist (`mantle-native-core`, `-ui`, `-parity`, `-bench`)?

**For native-windowing:**

- **Q-W1** The fix for the 1 px frame line.
- **Q-W2** The bar as a reserved screen area (app bar) from a WinUI window, across DPI and multi-monitor.
- **Q-W3** The desktop layer under the icons (WorkerW child of Progman on 25H2), and surviving an Explorer
  restart.
- **Q-W4** The cost of a parked hidden window versus creating one on demand (this feeds the keep-ready
  policy).
- **Q-W5** Glass: light acrylic vs the drawn picture, dark depth, and the CPU cost of each.
- **Q-W6** Non-activating pop-ups that still take text input (the Wi-Fi password, launcher typing).
- **Q-W7** DWM thumbnails in a WinUI window (z-order against XAML, clipping, scroll); click-through and
  capture exclusion; always-on-top bands equivalent to Electron's `screen-saver` / `pop-up-menu` levels.

**For the user:**

- **Q-O1** DECIDED 2026-11-07: cloud-only. No .NET SDK on the laptop; every build and test runs on GitHub
  runners.
- **Q-O2** DECIDED 2026-11-07 (the user left it to the plan): yes, WebView2 for web (Lively) wallpapers and the
  Mission Control panel only. Made on demand when one is showing, destroyed when it is not (a web view costs
  a browser process), and named in the log and in Settings as a web view.
- **Q-O3** Player and clips codecs: accept Windows' codec extensions with a native player, or WebView2 for the
  player if native can't match? Ask once the native attempt has numbers.
- **Q-O4** DECIDED 2026-11-07 (left to the plan): the native exes and installer are signed under the same
  SignPath project as the Electron installer, with a second artifact configuration. The SignPath application
  itself has not been submitted yet (the user submits it); until it is approved, native pre-releases are unsigned.
- **Q-O5** The switch-over release version number and its name (version numbers are the user's call).
- **Q-O6** Optional: before full parity, would the user like to try standalone native app windows (Files,
  Task Manager, Settings) launched by hand while Electron stays live? The default is no, which keeps the
  "Electron untouched" promise.
- **Q-O7** DECIDED 2026-11-07: no fixed length. Electron stays until the user says to remove it; after the
  switch-over, ask once a week.
