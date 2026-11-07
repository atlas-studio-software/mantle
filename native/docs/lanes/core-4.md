# Lane native-r3-core-4: notes (round 3)

Notes for the coordinator and for the surface lanes. `docs/contracts.md` is frozen this round, so what would have
been appended there is here.

## The surface host (for the launcher, OSD/toasts and tasks lanes)

- `Mantle.App/Shell/Host/SurfaceHost.cs` (namespace `Mantle.App.Shell.Hosting`: a namespace called `Host` would
  shadow the `Mantle.App.Host` class for every file under `Mantle.App.Shell`). It makes every entry of
  `Mantle.Generated.SurfaceCatalog.All` through one `Mantle.Core.Lifecycle.SurfaceSet` on the shell UI thread:
  - `AtStart = true`: made at start, hidden and parked (`ISurface.Create`, never `Show`), once the host's bus, events,
    state and look exist (so the first `ctx.State` and the `theme` replay are real).
  - otherwise: made on first use (the first show, toggle or `--open`).
  - destroyed on a rest (`"mantle.rest"` with `On = true`; AtStart ones are made again on the wake, the others on their
    next use) and at quit (the ONE shutdown's step "destroy the surfaces", before Windows is put back).
  - a surface that throws in `Create` is logged with the reason, its `Destroy` is called, and the next show tries again.
- `ISurfaceContext` (per surface): `Log` prefixes every line with the surface name (`launcher: ...`); `State` is the UI
  thread's own copy, `OnState` is coalesced (a burst arrives as its last snapshot); `CallAsync` copies the args, runs the
  command on the host thread with the surface's name as the caller and completes on the UI thread; `On<T>` subscribes on
  the host thread and re-posts every payload (replay first) to the UI thread; everything a surface subscribed is dropped
  when it is destroyed. `"theme"` now has a replay source (`ThemePayload`), so `ctx.On<ThemePayload>("theme", ...)` gets
  the current theme at once.
- Commands (generated, `SurfaceCommands`): `surface.show`, `surface.hide`, `surface.toggle` with `{ "name": "<surface>",
  "why"?: "<for the log>" }` (or a bare string), and `surface.list`. A name nobody built answers
  `there is no <name> surface in this build, so nothing was shown (it has: ...)`.
- Alt+Space (live only; a test run registers no global shortcuts) calls `surface.toggle launcher` through
  `SurfaceControl.ToggleFromKey`; with no launcher surface it logs
  `mantle: Alt+Space: there is no launcher surface in this build, ...` (never silent). The launcher lane needs only
  `[Surface("launcher", AtStart = true)]`.
- `--open=<target>` from a second start shows the surface of that name (Electron's allow-list: `wallpapers`, `player`);
  a failure is the line `mantle: a second start asked for <Label> (--open=<target>), but it did not open: <why>`.
- Test stand-ins: `test-parked` (AtStart) and `test-lazy` (`Shell/Host/TestSurfaces.cs`). Any surface named `test-...`
  is made ONLY in a `--test` run with `MANTLE_TEST_SURFACES=1`; elsewhere it is listed as skipped and never made. A
  `--test` second start may also `--open=test-...`.
- Test-only second-start switches (ignored with a log line without `--test`): `--test-surface=<show|hide|toggle>:<name>`
  (through the `surface.*` commands) and `--test-theme=<mode>` (`theme.set`, the answer logged).

## The bar this round

- Still on its own creation path (`App.OnLaunched`); the coordinator's one-line switch to a `bar` surface is untouched.
- `UseShell(new PlatformBarShell(_platform, _hostLoop))` is called on the UI thread once the platform exists. `--dock`
  then asks for the app bar; a test or scratch run's sidecar is passive and refuses, so nothing is reserved.
- Theme: `BarWindow.FollowHostTheme()` (called from `App.OnLaunched`) subscribes `MantleApp.ThemeShown`; the
  theme.json path stays. Match Windows in that path now reads the run's key (`MantleApp.ThemeKey`, a test's scratch
  key), the same read as the host's, so both paths agree. The host's theme also follows theme.json on disk now
  (`ThemeCore.FileChanged` via `LookShell.ThemeFileChanged`, from the data watcher), so surfaces hear it through `theme`.

## Deviations and open points

- `--open` on a FIRST launch keeps Electron's rule (CORE-010: nothing running -> exit 3, never becomes a shell); only a
  second start opens a surface.
- The rest coordinator is still not constructed in `App` (no `IRestParts` yet); the surface host already listens to
  `"mantle.rest"`, so wiring it is the only missing step. The app bar and the desktop layer are not released on a rest
  yet; at quit the bar's `StopFrame` removes its app bar when the window closes (after the sidecar has stopped, so
  Explorer drops it).
- A live `--open` failure is not shown as a tray notice yet (Electron's `trayNotice`).

## The first click right after start (lane native-r3-core-4b)

- Symptom: UI test `Popup_exit_plays_before_the_park_and_a_replaced_one_goes_at_once` failed with "More did not open"
  (about half the runs on this branch, also in popup-only runs, so not a test-order effect). It is the only test that
  clicks the bar the moment its window appears (the others make a fixture window first).
- Root cause (CI logs with a test-only subclass on the bar's windows, runs 42380318551 and 42381646443): for some tens
  of ms after a WinUI window's first frame, its content island does not take the pointer yet. The press reaches the
  input site's HWND (`Microsoft.UI.Content.DesktopChildSiteBridge`) as WM_POINTERDOWN, WinUI lets it fall through to
  DefWindowProc, which makes WM_LBUTTONDOWN/UP, and no XAML element hears it (lost when made 16..39 ms after the first
  `Rendering`; a click 62 ms after went straight to XAML and the HWND got no mouse message at all). Win32 and UI
  Automation already show the window and its buttons in place at that point. This branch's start work only shifted the
  timing (bisect: 0 failures in 6 runs at a72e56e/f6fe3a7, failures from 66f5923 on, whose start-up change is one
  queued UI job and a log line).
- Fix: `Shell/Host/EarlyClicks.cs`. The bar (`BarWindow.Popups.cs`, one call) watches its input site: a fallen-through
  left click (down and up on the same enabled `Button`) is passed to that button (`new ButtonAutomationPeer(b).Invoke()`;
  the element's own peer comes back typed `FrameworkElementAutomationPeer` in the AOT build), with the line
  `bar: input: a click on <id> came before the window took the pointer itself (its first moments), so Mantle pressed
  <id> for it`. WinUI never makes those legacy messages for a press it took, so nothing is clicked twice; the watch
  comes off once XAML hears a press itself. Any other surface window can use the same `EarlyClicks.Watch(hwnd, root,
  name, log)`.
- The popup test's failure message now says where the click went (`WindowFromPoint`, the window in front) and keeps the
  whole app log in `native/out/ui/popmotion-<time>-native-log.txt`; it prints which path the first click took.

## Launcher created at start (native/main c34ceda merged)

- `surface launcher: could not be made (at start, hidden and parked): Specified cast is not valid.` in the AOT build
  (CI run 42382391852, the one full app log kept from the merged build; not checked in other runs). The surface host logs it, calls Destroy and will try again on the next
  show, so start and the bar are unaffected. The launcher lane needs to find the cast in `LauncherSurface.Create`
  (`XamlReader.Load` result or a `FindName` cast; the bar's own `(TextBlock)XamlReader.Load(...)` works in AOT).
