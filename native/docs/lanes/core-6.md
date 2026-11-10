# Lane native-r4-core: notes (round 4)

## The rest (game mode's host.rest) is wired

- `App.HostStart` makes the one `RestCoordinator` (main.js restMantle) with `Mantle.Core.Lifecycle.ShellRestParts`
  and sets it as the feature host's `RestHost`, so a feature's `ctx.host.rest(on, why)` really rests. `TrimSoon("60 s
  after start")` runs at start (FEAT-022).
- A rest: rest:true features stop; the launcher is hidden at once and the Win key goes back to Windows' Start (a live
  run asks the sidecar's hook); `"mantle.rest" {on:true}` reaches the surface host, which destroys EVERY catalog
  surface's windows (`SurfaceSet.Rest`; nothing is shown until the wake, with the reason in words); after 1.5 s the
  windows left and Mantle's memory are read and the idle pages are trimmed. The bar is kept, as Electron keeps it.
- A wake: `"mantle.rest" {on:false}`: the AtStart surfaces are made again, hidden and parked (`SurfaceSet.Wake`), the
  others wait for their next use; the features install again; the Win key hook is asked again; a trim 60 s later.
- `ShellRestParts.Pages()` = `bar` + the surfaces made now, from `SurfaceHost.Made` (a snapshot taken after every UI
  job the host posts, so the host thread reads it without a hop or a wait).
- Test-only trigger: a `--test` second start's `--test-rest=on|off[:<why>]` (logged as `mantle: test-rest <on|off>:
  ok: <the coordinator's words>`).
- For the surface lanes: a surface needs nothing new. Its `Destroy` runs on a rest and `Create` again on the wake
  (AtStart) or on its next use; a surface that keeps state across a rest keeps it outside its window.

## The app bar and the desktop layer

- Electron does NOT release the app bar or the desktop layer for a rest (restNow keeps the bar and its strip, and
  only quiets the desktop page), and releasing the strip would re-lay every maximised window twice per game. So the
  rest keeps them too (a deviation from this round's brief, kept on purpose).
- What main.js does release them for is "put Windows back" (restoreShell / the shutdown): the app bar FIRST, while the
  sidecar can still be asked. Native now does the same: the bar asks through `Shell/Host/AppBarLease` (an `IBarShell`
  around `PlatformBarShell`), which knows whether a strip is reserved; the guard's restore gives it back through
  `ShellGuardOptions.MoreReleases` ("the app bar"), and a quit step "give the app bar back" catches what the restore
  could not (a test run, the link down), still before "stop the sidecar". After that it is not reserved again in this
  run (main.js re-asks only while `appbarHwnd` is set), and the bar's own remove on close answers without a call, so
  the 4 s wait on a stopped sidecar is gone. The desktop layer adds `("the desktop layer", detach)` to the same list
  when it is built (none in this build).
- For the tasks lane (BarWindow.Frame.cs, not edited here): `StopFrame` can drop its `RemoveAsync(...).Wait(4000)`
  once it relies on the lease; it is harmless now.

## The theme and the disk flush (core-5's finding)

- `SafeFile.WriteAtomic` keeps `Flush(true)` before every rename (the laptop has lost power mid-session; a power cut
  leaves the old file or the new one, never a torn one). Only a test's stand-in for an Electron app may turn it off
  (`new SafeFile(gate, flushToDisk: false)`), to write as Helm and Quartz do.
- The theme no longer waits for it: `ThemeCore` (with `behind`, as `LookShell` makes it) tells every listener the
  moment the pick is decided, and theme.json is written behind it by `JsonStore.UpdateBehind`: in memory at once, the
  file one write at a time in the order asked, each writing the newest document, still flushed. A re-read from disk is
  skipped while a write is on its way (it never takes a pick back). The outcome comes back on the host thread under the
  theme lock: a failure is the view's note, the log line and a second tell; `theme.set` answers after the save, so its
  answer still carries a failed save's note (LOOK-010). The quit waits for writes on their way ("save what is still
  being saved", 3 s). Both the command path and another app's look (look sync) take it.
- The bar no longer re-reads theme.json itself once the host's theme is up (the host follows the file and tells it),
  so a save landing on disk never flips the bar back to an older pick.
- Not changed: Mantle's own look.json publish still flushes before its rename, so Helm and Quartz see Mantle's change
  after that flush (up to ~1.3 s on a busy disk; Electron's is faster there). Kept for crash safety: a torn look.json
  would lose the shared look.

## Proof and numbers (cloud, public repo, 0 min billed)

- Full `ci.ps1` at 03a753f (run 42770119126): `NATIVE-CI: ok (10 ok, 0 skipped)` in 12 min 16 s (build 135 s, unit
  522/522 + launcher GOLDEN ok 16 s, smoke 15 s, ui 33/33 192 s, bench 102 s, parity 94 s (light 0.9966, dark 0.9916,
  light-150 0.9949, dark-150 0.9809), launcher 68 s (light 0.8295, dark 0.5864), osd 41 s (12 of 12, lowest
  vol-dead-light 0.5757), strip 44 s (dark 0.9091, light 0.9258), sidecar 30 s). The three new stages add ~2.5 min.
- The 1 s promise on a loaded runner: `test-loop.ps1 -Runs 30` (whole suite in parallel, run 42770129663):
  `TEST-LOOP: 30 of 30 runs clean`; timed on the host thread: Helm's theme as the theme event median 42 / p95 53 /
  max 91 ms; after theme.set max 1 ms; after Windows went dark max 18 ms; while its save was failing max 32 ms.
  Before (core-5, the same suite x40): one 1672 ms (theme.json's save in the path) and 1300 ms (test-side) outliers.
  Two things changed: Mantle's follow path no longer waits for theme.json's save, and the Helm stand-in now writes as
  Electron's Helm does (no fsync), so the clock no longer includes a native flush Helm never pays.
- The rest: `ShellRestPartsTests` (unit, real coordinator + real SurfaceSet) and `Mantle.UiTests/Rest/RestTests`
  (the real app, `--dock`, the stand-ins: windows gone during the rest, the bar kept, a show refused in words, the
  AtStart one back hidden on the wake, the on-first-use one back on use, the work area unchanged before, during, after
  the wake and after the quit, the passive sidecar refusing the app bar, no 4 s wait at quit).
- CI floors: tools/parity/README.md "CI floors" (launcher 0.78 / 0.55, osd 0.55, strip 0.88; the golden exact).
