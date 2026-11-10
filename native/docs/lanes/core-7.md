# Lane native-r5-ci: notes (round 5, CI)

## Two more parity stages: `qs` and `previews` (ci.ps1 now has 12 stages)

- `qs` runs `tools/parity/quick-settings/run-qs-parity.ps1 -Exe <published exe> -Out <Out>/parity/qs` and gates on
  `QS-PARITY summary: 2 scored of 2` plus a floor per theme. Its first run (42786941393) scored 0.4559 / 0.5250 on
  the wrong pair: part1's `.qsgrid > * > button` removed the Wi-Fi and Bluetooth tiles themselves (qs-more.js wraps
  each with its chevron in a `.qsx-split`), which native does draw, and it kept the volume chevron (`qsmute qsx-more`).
  electron-qs.cjs now removes only the chevrons and unwraps the splits; both halves of the pair hold the same rows.
- With the same rows the score is lower (0.3872 light, 0.4519 dark, run 42789738354), and honestly so: native part 1's
  pop-up is ~44 px taller (more room around the sliders) and ~20 px wider than Electron's, the power-mode line reads
  "Balanced · locked" where Electron reads "When plugged in" with the Balanced segment in the accent, native's
  sliders have a ringed thumb and a grey track, and the footer says "estimating time..." where Electron reads
  "3 hr 4 min left" (the mock's battery reading is not in MANTLE_TEST_QS). The pair is compared bottom-aligned,
  so the height alone shifts every row. These are app-side (the quick-settings lane); the floors only catch a regression.
- `previews` runs `tools/parity/bar-tasks/run-preview-parity.ps1 -Exe <exe> -Theme both -Out <Out>/parity/previews`
  (the script took no -Exe before: it now takes -Exe, else MANTLE_NATIVE_EXE, else the published exe) and gates each
  theme on its floor and on `PREVIEW-PARITY: FAILED`. 0.9545 light / 0.9746 dark in both runs.

## The bar pair's two flakes (run-parity.ps1)

- **A shot with no bar.** Shoot now also compares the crop with the whole screen grabbed just before that app started
  (the launcher's guard) and takes it again, up to 6 times, while they still match (mean abs under `-MinVsEmpty`, 4).
  With a bar in it the crop reads 11-79 against the empty screen (dark native lowest), the empty screen ~0. Both apps.
- **Electron's tray not read yet.** Staging run 42785589408 scored bar light 0.9784: Electron's right cluster still
  showed the not-read-yet globe and battery dash, native its real cluster ~30 px further right. electron-bar.mjs now
  waits, over the CDP link it already holds, until `#network` has a data-state other than `unknown`, `#battery`
  shows no `loading` dash, and that cluster has held for 1.5 s (max 30 s), and reports `readings` in electron.json;
  run-parity.ps1 prints them and makes an unsettled cluster a problem. On the runner it settles in 1.5-1.7 s
  (net=connected, battery hidden). Native needs no wait: BarWindow.xaml.cs BuildGlyphs draws its wired glyph and
  battery slot from the first frame. (`#battery` starts hidden and a machine with no battery hides it again, so
  "hidden" alone cannot prove a reading: hence the hold.)

## desk.ps1 -Reset closes "System Properties"

Desk.cs `CloseDialogs` posts WM_CLOSE to any top-level `#32770` titled "System Properties" (a property sheet takes it
as Cancel), whatever process hosts it; desk.ps1 also stops the `SystemProperties*` tools by name.

## Floors raised (ci.ps1 `$Floors`, tools/parity/README.md "CI floors")

| Floor | Was | Now | From (runs 42786941393, 42789738354) |
|---|---|---|---|
| launcher light | 0.78 | 0.84 | 0.8586, 0.8581 |
| launcher dark | 0.55 | 0.80 | 0.8175, 0.8170 |
| strip | 0.88 | 0.90 | dark 0.9091 / 0.9091, light 0.9258 / 0.9580 |
| qs light / dark | - | 0.36 / 0.43 | 0.3872 / 0.4519 (run 42789738354) |
| previews light / dark | - | 0.93 / 0.95 | 0.9545 / 0.9746, both runs |
