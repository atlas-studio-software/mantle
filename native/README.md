# native/: Mantle in C# + WinUI 3

Mantle is being rebuilt as a native Windows app: same features, look and settings, far lighter. The
Electron app (repo root: `main.js`, `lib/`, `ui/`, `sidecar/`) stays the shipping Mantle, untouched, until
the native one reaches full parity. Everything native lives under this folder. The only exception is
`.github/workflows/native-ci.yml`.

The bar spike that started it (branch `spike/winui-bar`, `native-spike/README.md`) measured one process
at about 45 MB private and 0.05 % CPU for the native bar. All of Electron Mantle on the same runner was 15
processes and about 700 MB.

## Layout

| path | what |
|---|---|
| `Mantle.Native.sln`, `Directory.Build.props`, `global.json` | the solution and the shared build settings (.NET 10 LTS, Windows App SDK pinned) |
| `src/Mantle.App/` | the WinUI 3 app, `MantleNative.exe`: unpackaged, single instance per profile; the bar is the first surface. UI Automation ids: `AUTOMATION.md` |
| `src/Mantle.Core/` | non-UI services: arguments, data paths, logging, single instance, and the read-only reader for the Electron app's `%APPDATA%\mantle` files (inventory: `DATA.md`) |
| `src/Mantle.Sidecar/` | `sidecar/*.cs` compiled **linked** (not copied) under .NET 10, so Electron and native share one source during the port; the verdict and the blocker list are in its `README.md` |
| `tests/Mantle.Core.Tests/` | unit tests of Mantle.Core |
| `tests/Mantle.Sidecar.Tests/` | in-process behaviour tests of the linked sidecar code |
| `tests/Mantle.UiTests/` | FlaUI UI automation of the real app (real clicks; runner only) |
| `bench/` | memory / CPU / first paint, Electron vs native, and the build-mode measurements |
| `tools/ci.ps1` | the one CI entry point (stages below) |
| `tools/publish.ps1` | publish the app: `-Mode sc` (self-contained, ReadyToRun), `fd` (framework-dependent), `aot` |
| `tools/parity/` | screenshot parity: the same surface from Electron and native, diff image + score |
| `tools/desk/` | the runner desktop between GUI stages: `desk.ps1 -Reset` (taskbar out of the way, leftovers closed, windows minimized), `-Watch` (taskbar attention log), and `Desk.cs` helpers (`IsAbove` z-order, `MinimizeOthers`) |

Plans and the parity checklist (`PLAN.md`, `PARITY.md`, `docs/`) and the window-primitive spikes
(`spikes/windowing/`) come from their own branches.

## Build, test and measure: on a GitHub runner, not on the dev laptop

The dev machine has no .NET SDK and must not lag, so every build and test runs on a GitHub Windows
runner. The repo is public, so standard runners are free. Each tool ends with one summary line; judge a
run by that line.

`tools/ci.ps1 [-Stages build,unit,smoke,ui,bench,parity,sidecar] [-Out native/out] [-BuildMode aot|sc]` runs the stages in that
order. Each one prints `STAGE <name>: ok|FAILED|skipped (<why>)`, and the run ends with `NATIVE-CI: ok (...)`
or `NATIVE-CI: FAILED (...)`.

**How a lane runs it (two lines):** commit and push your `native/*` branch, then from its worktree run
`cloudtest --recipe mantle-native-public` (all stages; npm + the csc sidecar are set up for the Electron side of parity/bench)
or `cloudtest --committed --os windows --no-install --timeout 30 --artifacts "native/out/test/**" -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit"` (fast).

The stages are `build` (publish `MantleNative.exe` Native AOT, or self-contained with `-BuildMode sc`, the
fallback; + build the solution), `unit` (Mantle.Core.Tests), `smoke` (launch the exe: first frame, log, the
five data files from the synthetic fixture profile, ids, z-order, live theme change, single instance,
`--quit`, quiet `--test`), `ui` (Mantle.UiTests), `bench` (`bench/run-bench.ps1 -Mode smoke`), `parity`
(`tools/parity/run-parity.ps1`, needs the Electron setup) and `sidecar` (`src/Mantle.Sidecar/check.ps1`: the
three linked build variants + the sidecar tests).

- A stage subset with the recipe: `cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,ui,parity"`.
- Never `--artifacts "native/out/**"`: that downloads the published app (~220 MB). The recipe's list leaves
  `native/out/bin` out.
- Before each GUI stage (smoke, ui, bench, parity) `tools/desk/desk.ps1 -Reset` puts the runner desktop back and
  prints `DESK <stage> before / reset / after`; a watcher logs every taskbar attention request and move, summed up
  per stage as `DESK <stage> during: ...` (full log `native/out/desk-watch.log`). A window that asks for attention
  lights its taskbar button, and the runner's auto-hide taskbar then stays up over the bar until it is cleared.
- Parity masks no known gaps any more: the two it masked (the right cluster ~18 px right, the orange dot on "more")
  were closed in round 2, so the gate (0.98 SSIM / 0.5 %) covers the whole crop. A new gap goes in `$gapList` in
  `tools/parity/run-parity.ps1` and prints its own `PARITY known-gap` line per theme.
- The workflow `.github/workflows/native-ci.yml` runs the same `ci.ps1` on pushes to `native/main` and on
  pull requests that touch `native/`, with every stage except `parity` (a bare runner has no Electron
  setup), and uploads `native/out` without `bin/`.
- Each tool also runs alone. The exact commands are in `bench/README.md`, `tools/parity/README.md`,
  `tests/Mantle.UiTests/README.md` and `src/Mantle.Sidecar/README.md`.

## Rules for every lane

- Test instances: `MantleNative.exe --test --profile-name=<x> [--user-data=<dir>]` never writes the user's
  data, and its windows are invisible (alpha 0, click-through, never activated) unless
  `MANTLE_TEST_VISIBLE=1`. Only a CI runner sets that. The UI tests and the parity tool refuse to run
  unless `CI` / `GITHUB_ACTIONS` is set. Nothing a test starts may ever appear on the user's screen.
- The native app only READS the Electron app's files (`%APPDATA%\mantle`) for now. It never writes them.
- The repo is public: fixtures are synthetic, no user data, no machine paths, no personal names in
  committed text. Commits pass the oss-guard hooks (never `--no-verify`).

## Build mode: Native AOT (what CI builds)

Measured on the same runner (`bench/README.md`, modes run 42357655845, app 511a8ff, light theme):

| mode | folder | first frame (median of 3) | private / working set at 45 s | needs installed |
|---|---|---|---|---|
| self-contained + ReadyToRun (`sc`, today's CI default) | 217.7 MB, 495 files | 314 ms | 43.7 / 112.6 MB | nothing |
| self-contained, no ReadyToRun | 173.9 MB | 588 ms | 36.3 / 115.3 MB | nothing |
| framework-dependent + ReadyToRun (`fd`) | 50.0 MB, 40 files | 406 ms | 38.8 / 116.5 MB | .NET 10 + Windows App Runtime 1.7 (67.9 MB installer), updated outside the app |
| Native AOT + trimming (`aot`), with the fix below | 105.6 MB, 276 files | 141 ms | 31.0 / 77.1 MB | nothing |

AOT is the lowest on memory and the fastest to start, at half the self-contained size, with nothing to
install. CI builds and tests it (`ci.ps1 -BuildMode aot`, the default): the build, smoke (incl. the single-instance
hand-off, `--quit`, the quiet `--test` window and the five data files from the synthetic fixture profile), UI
tests, bench and parity all run against the AOT exe. In the full pipeline (bench smoke mode) the AOT bar measured
first frame 151 / 143 ms and 32.1 / 27.5 MB private (light / dark), against 469 / 391 ms and 43.7 / 40.3 MB for the
self-contained build in the same pipeline before.

AOT rules (they are build errors in `Mantle.App.csproj`, `WarningsAsErrors`): a class that implements a WinRT
interface (derives from a WinUI type, or `IDisposable` = WinRT `IClosable`) is `partial` (CsWinRT1028; without it the
app dies at start); no reflection the trimmer cannot follow (IL2xxx) or code that needs the JIT (IL3050/IL3051).
`ci.ps1 -BuildMode sc` (or `NATIVE_CI_BUILD_MODE=sc`) is the self-contained fallback if AOT ever blocks a lane.

The full list is in `bench/README.md`.

For comparison, the same runner measured Electron Mantle at 16 processes and 700-717 MB private (all
surfaces), against 39-44 MB for the native bar in one process (full run 42358360469).
