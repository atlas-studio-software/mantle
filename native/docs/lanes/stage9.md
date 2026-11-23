# Lane stage 9 (native/stage9)

Stage 9 merges every native/* branch that was not on native/main: r7-idle (1ecccef7) and r8-darkidle (b74c5b7a)
on top of r7-media3, r7-topbar, r8-dl and r8-startup. The probe branches native/r8-dl-probe and
native/r8-dl-probe-old are left out on purpose: their only change is a test wait for "topbar: window activated",
which would hide the keyboard steal instead of fixing it.

## The two failures that stopped the 11-16 stage

- DownloadsKeysTests, the first Down. Two causes, one after the other:
  1. The top bar's start-time show activated its window and took the keyboard from a pop-up opened in the first
     second (native/docs/lanes/startup.md, run 43859364384). Fixed by TOP-221 from r7-topbar: the one Activate
     runs at the making, off-screen (TopBarSurface.Prime), and the first show is inactive
     (Surfaces/TopBar/TopBarSurface.cs:476-509). Stage 9 run 45267680314 shows it holding: "first show: shown
     inactive, the foreground stays where it was", and the shelf was not closed.
  2. With the steal gone, run 45267680314 still failed: "the first Down did not reach the newest row". The shelf
     took the keyboard ("the shelf has the keyboard") BEFORE its rows were painted ("the shelf shows 3 rows"
     came after), so nothing in the shelf had the XAML focus. DlKeyboard's `_dl.Root.Focus(...)` could not help:
     Root is a Grid, which takes no focus, and it ran only when rows already existed. A key reaches DlKey_ only by
     routing up from a focused element, so the Down went nowhere. Fix: DlSeatFocus
     (Surfaces/Bar/Downloads/BarWindow.Downloads.cs), called at the end of every PaintDl. When the shelf holds the
     keyboard, the user has not moved, and nothing in the shelf is focused, the first row takes the focus
     programmatically. _dlUser stays false, so DownloadsKeys.From still reads it as "none" and the first Down is
     still the newest row (BAR-888). No test or guard changed.
- The idle budget (about 1.2 % light against 1.0 %): measurement scatter from the charged-tick method, not a
  burner (native/docs/lanes/idle-compare.md). Fixed by r7-idle e4173c25: exact cycles over 45 s, with the limit
  unchanged at 1.0 % of one core. Run 45267680314 passed it (the ui stage's only failure was DownloadsKeysTests).

## Runs

- 45267680314 at b74c5b7a: `NATIVE-CI: FAILED (ui failed; 16 ok, 0 skipped)`; ui 101 of 102 (DownloadsKeysTests,
  cause 2 above).
- 45274695800 at 0858bb9f (the DlSeatFocus fix): `NATIVE-CI: ok (17 ok, 0 skipped: )`; unit 1047 of 1047, ui 102
  of 102. The counts are in PARITY.md "Native status by prefix, stage 9", and the stage is in ledger.md "Stages".
