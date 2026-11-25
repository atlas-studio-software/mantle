# Lane r13-strip-golden (2026-11-25)

Branch `native/r13-strip-golden`, off the WDE's native/r13-bar-islands at 9ebb700f (references.psd1 from 66188e13).
EM ticket t_e11994. The strip stage reads `native/tools/parity/references.psd1` the way run-parity.ps1 does.

## What changed

- `tools/parity/bar-tasks/run-strip-parity.ps1` reads `references.psd1` with the same reader as run-parity.ps1
  (no file / no entry = `electron`; anything other than `electron` / `native-golden` = `STRIP-PARITY: FAILED (...)`,
  exit 4). The rule is mirrored, not shared: a dot-sourced helper would have meant moving run-parity's reader, which
  the WDE's lane owns, so the merge stays line-local.
- `strip` must name the same reference as `bar` (exit 4 otherwise): the strip is cropped out of run-parity's bar run,
  which shoots Electron only for an `electron` bar and records the dock island only for a `native-golden` one.
- `electron`: unchanged (the pill's centre 440 DIP, Electron vs native, the 0.90 floor in ci.ps1).
- `native-golden`: the strip is the dock island. run-parity.ps1 records its screen rect (UI Automation
  `bar.island.dock`, new helper `Native-IdRect`) in score.json as `dock`. The strip script crops it out of native's
  candidate, writes `<Out>/<theme>/strip-candidate-<theme>-<pct>.png`, and scores it against
  `goldens/strip/<theme>-<pct>.png` with the same `STRIP-PARITY <tag>: ssim=` line, so ci.ps1's floor (0.90)
  applies unchanged. A golden counts only once `goldens/strip/REVIEW.md` has non-empty `reviewer:`, `run:` and
  `sha:` lines. Until then each theme prints `STRIP-PARITY <tag>: golden pending review (...)` and the summary is
  `STRIP-PARITY: FAILED (strip golden pending review: light, dark; candidates in ...)`, exit 1. A golden of another
  size than the dock island is a problem, not a score (the dock's width follows the running apps).
- `ci.ps1` Stage-strip puts the script's own `STRIP-PARITY: FAILED (...)` verdict first in the STAGE line.
- Nothing writes into `goldens/`. No threshold changed. `references.psd1` values unchanged (its strip comment updated).

## Not verified

- That the dock island's UIA rect is in the same screen px as the capture at 150 % (the stage runs 100 % only; the
  clock mask uses the same assumption).
- A reviewed golden: none exists, so the scoring branch has not run on a runner (only the pending branch).
- Freezing: the golden comes from a committed run's `strip-candidate-*.png` after the side-by-side review.
