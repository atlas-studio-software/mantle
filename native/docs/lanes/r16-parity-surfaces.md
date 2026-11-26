# Lane r16-parity-surfaces (2026-11-26)

Branch `native/r16-parity-surfaces`, off `native/r15-quick-launcher` b1e61bd8 (the Windows Desktop Engineer's S3, on
stage13's r15-bar-light 718d4970). EM ticket t_4ab5fa. The tools half of S3: the restyled quick settings and launcher leave
the Electron image compare (plan v0.2), as the bar and the strip did.

## What changed
- `tools/parity/references.psd1`: `qs = 'native-golden'`, `launcher = 'native-golden'`, with a comment line each.
- `tools/parity/quick-settings/run-qs-parity.ps1`: the reference reader of run-parity.ps1 (no file or no entry =
  electron; a bad value = `QS-PARITY: FAILED (references.psd1: ...)`, exit 4; `goldens/qs/REVIEW.md` must have non-empty
  reviewer:, run: and sha: lines). In native-golden mode: no `MANTLE_TEST_GLASS=electron`, no
  `MANTLE_TEST_QS_NO_GAMEMODE` (the whole panel as users see it), `MANTLE_TEST_WALLPAPER` = the repo's snow-peak still
  (the islands frost from it); native's pop-up is grabbed with the same margins to `<out>/candidate-qs-<theme>.png` and
  scored against `goldens/qs/<theme>.png` (bottom-aligned, centred, same threshold) once reviewed; else
  `QS-PARITY qs-<theme>: golden pending review (...)` and, last, `QS-PARITY: FAILED (qs golden pending review: light,
  dark; candidates in <out>)`, exit 1. Electron mode is unchanged.
- `tools/parity/launcher/run-launcher.ps1`: the same reader for `launcher`. In native-golden mode: no
  `MANTLE_TEST_GLASS=electron` for the native run, `MANTLE_TEST_WALLPAPER` set, the dark pass over the wallpaper (the solid
  black desktop was for Electron's framed window); the Electron run, the opens, the typing, the rows, the `launcher
  PROBLEM:` behaviour checks and `LAUNCHER-LATENCY` are unchanged. The Electron image score is replaced by
  `LAUNCHER-PARITY <theme>: launcher golden pending review (...)` (candidate `<out>/candidate-launcher-<theme>-100.png`) or,
  once reviewed, a score against `goldens/launcher/<theme>-100.png`; no "window sizes differ" problem in this mode. The
  last line is `LAUNCHER-PARITY: FAILED (launcher golden pending review: ...)`, exit 1.
- `tools/ci.ps1`: `Stage-qs` and `Stage-launcher` take the script's `...: FAILED (...)` verdict first, as `Stage-strip`
  does. The floors (qs light 0.89 / dark 0.84, launcher light 0.84 / dark 0.80) are unchanged and apply to golden scores;
  the launcher's "did not work" behaviour check still fails the stage on its own.
- Docs: `docs/parity/look.md` SEE-010 (the tint-lined check now runs only on the OSD half; the launcher half checks the
  island) and `docs/parity/launch.md` LAU-001 (native: the 600x520 island).

No goldens were made or frozen: that waits for the owner's side-by-side review.

## Where this differs from the WDE's notes (r15-quick-settings.md, r15-launcher-look.md)
- qs: Electron's page (electron-qs.cjs) still runs in golden mode, but only for the mock's readings (qs-state.json, which
  native needs as MANTLE_TEST_QS); it is not scored and no pair image is made. Skipping it would leave native without its
  readings.
- launcher: the reference flag is `$nativeGolden`, not `$golden`: `$golden` already holds the ranking golden's check
  (make-golden.cjs --check). The golden name is `<theme>-100.png` (the launcher runs at 100 % only).

## Not verified
Proven only by stage14's working-tree run (logs, no images); the candidate images need a committed run.
