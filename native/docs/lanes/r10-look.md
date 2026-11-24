# Lane notes: the seed check and the desktop record publisher (native/r10-look)

Brief: stage-10 round, two parts. 1: check that native seeds a missing look.json with look-sync's 2026-11-11 glass
(SEE-018). 2: native Mantle publishes the look-sync `desktop` record, so Helm and Quartz stop falling back to Windows'
wallpaper (CONTRACT.md section 2 "Shared store", section 4, the user's 2026-10-10 answers, "Lane 0 freeze", "desktop.guard",
"2026-11-11"). No .NET SDK on the build laptop this round: every C# change and test here is **written, not run**.

## Part 1: the seed (SEE-018)
- 11703dda had it all: LookSyncCore.Seed() and GlassSettings.Seed (LookHost's first glass, the local copy's default via
  LocalLook.Default) are dark and light {blurPx 0, dim .30, panel 0}, theme system, accent null, render picture,
  matching look-sync.js SEED. gen-look-golden.js swaps the vendored module's SEED glass in memory and names it in
  golden.json `nativeDifferences`; re-running it (0.4 s) gives the committed golden.json byte for byte.
- The roundtrip fixture `look-sync.json` (dark 5 / .50, light 12 / .45) and the profile fixture's look.json are stored
  values round-tripped as written, not seeds, so they do not change.
- This round only moved SEE-018 from `tested` to `written, not run` (its tests have not run since the seed change).

## Part 2: what is where
- `Mantle.Core/Look/DesktopPublisher.cs`: lib/look-desktop.js's publisher, the part native needs. Pure apart from files
  through SafeFile, behind `IDesktopSource` (Current / Still / Picture). A check:
  1. reads the wallpaper (Current) and the glass Mantle shows; the same wallpaper and the same dims and blurs publish
     nothing;
  2. asks the gate first: a folder this run may not write (the user's own look-sync folder outside `--live` as the only
     shell) makes no still and no record, said once;
  3. writes the still FIRST: `<look-sync dir>\stills\wall-<doc rev + 1>.jpg` through SafeFile.WriteAtomic (tmp +
     rename, retried on EBUSY / EPERM / EACCES after 50 / 100 / 200 ms);
  4. works out `guard.dark` and `guard.light` from the still as the desktop shows it (DesktopPublisher.Entry: NativeGuard's
     rule over the contract's top 100 DIP), else `guardReason` / `guardLightReason`;
  5. writes the record through LookSync.PublishDesktopAsync after LookSync.Refresh on the same queue (the module's own
     writer: re-read, rev + 1, `look.json.mantle.<pid>.<hex>.tmp` + rename, retried 50 / 100 / 200 ms). It is written
     with sync ON or OFF; only `desktop` and the doc's rev and writer change;
  6. keeps the last 3 stills (never the one the record names) and removes leftover .tmp files in stills.
  A glass change republishes the same still with a new guard and a new rev. At the first check, a record Mantle wrote last
  run that still names this wallpaper and a still that is there is adopted, not republished. One check at a time, at most
  one waits. Nothing throws: a failure is a reason in the record or the log, each reason said once until a publish works.
- `Mantle.App/Shell/Glass/DesktopPublish.cs`: `DesktopPublish` (start, wallpaper change, glass change) and
  `WindowsWallpaper`, the source. Native has no wallpaper library yet, so the desktop's picture is Windows' own wallpaper
  (SPI_GETDESKWALLPAPER): type `picture`, id `windows:<12 hex of sha1(path | size | mtime)>` (Windows rewrites
  TranscodedWallpaper in place, so the id follows the picture), title the file name (or "Windows wallpaper"), file the
  path. The still is a WinRT decode scaled to the cover size of the primary screen at most (DesktopPublisher.CoverSize,
  never upscaled) and a JPEG at quality .9; the guard's picture is DesktopGlass.Decode, the decode the glass layers use.
- A `--test` run never reads Windows' wallpaper and by default leaves look.json's desktop record alone (the UI tests,
  GlassTests among them, write their own record and still and must keep them); `MANTLE_TEST_WALLPAPER=<picture>` makes a
  test run publish that picture instead (said in the log either way).
- Shared-file blocks (each marked `lane r10-look`):
  - `Mantle.App/App.xaml.cs`: two lines after `DesktopGlass.Feed`: `DesktopPublish.Start(_look.Look, Host.Files)`.
  - `Mantle.App/Surfaces/Bar/Frame/BarWindow.Frame.cs`: one line in `case 0x001A` (WM_SETTINGCHANGE
    SPI_SETDESKWALLPAPER), next to the live glass's own re-cut: `DesktopPublish.WallpaperChanged()`.
- Tests: `Mantle.Core.Tests/Look/DesktopPublisherTests.cs` (16 tests, one a 6-row theory; temp folders and a fake source only; the
  LOOK_SYNC_DIR case resolves the folder through DataPaths with a fake APPDATA).
- LOOK_SYNC_DIR: the publisher has no folder of its own; it uses LookSync.Dir, which App gets from
  DataPaths.LookSyncDir (LOOK_SYNC_DIR, else `<test root>\look-sync` in a --test run, else %APPDATA%\look-sync).

## Decisions and differences
- **Unknown keys.** Everything the publisher does not own is kept as the shared module keeps it: theme, accent and glass
  entries byte for byte (rev and by included), and every key inside a glass value (dimAuto, keys look-sync does not
  know). A key OUTSIDE look-sync's schema (an unknown top-level key, an unknown look field) is dropped on Mantle's write,
  because the module's parse rebuilds the doc on every write in every app (look-sync.js parse, Electron Mantle too).
  Keeping them in Mantle alone would not keep them (Helm's and Quartz's next write drops them), so it is a module change
  for the Engineering Manager if wanted. The test pins today's behaviour.
- **Band and region.** The record has no top-level `region`. Each guard entry is the contract's shape, which includes
  `region {topDip 0, heightDip 100}` (CONTRACT "desktop.guard", LOOK-119). The published band stays 100 DIP;
  NativeGuard.Entry's 140 (look.md "Publishing") is not used for the record.
- **The guard's rule** is native's (NativeGuard: sharp still, glyph box and 95th percentile, 4.5:1), the "better guard"
  the 2026-11-11 section waits for, over the contract's band; Electron's is the 48 brightest blurred pixels at 4.2:1.
- **No Windows wallpaper set, no lock screen, no wait for video/web, no game-rest hold.** Native does not own the
  wallpaper, so there is nothing to push to Windows and nothing to unload (LOOK-104..108, WALL-311..318 stay todo).

## Left
- A monitor change (resolution, primary) does not republish the guard yet (WM_DISPLAYCHANGE is not wired; LOOK-121).
- Settings' desktop status text (LOOK-099) and LookHost.SetDesktop are not fed from the publisher yet.
- The wallpaper change is heard through the bar's frame only; with no bar window, only start and glass changes check.
  The session window (App.xaml.cs SessionWindow) also hears WM_SETTINGCHANGE and could be the one place.
- Run the tests (DesktopPublisherTests, and the SEE-018 ones) on a machine with the SDK.

## Proposed contract changes (Engineering Manager; none made here)
- None needed for this publisher. If native should publish its 140 DIP band (look.md "Publishing"), that is a contract
  change with matching Helm and Quartz jobs (their freshness check and LOOK-122's stale key compare the band).
