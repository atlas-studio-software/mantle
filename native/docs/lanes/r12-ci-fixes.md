# Lane r12-ci-fixes (2026-11-25)

Branch `native/r12-ci-fixes`, off native/stage10 438a52dc. EM ticket t_5d720b. It follows up the first full committed native
CI on 438a52dc (mantle-native-public, cloudtest 20261125-173955-a0fe66): `NATIVE-CI: FAILED (parity, sidecar failed; 15 ok)`.

## Parity at 150 %: the cause

The 150 % line was light-150 ssim 0.9790, diffpx 0.54 % and dark-150 ssim 0.9847, diffpx 0.50 %; the threshold is
ssim >= 0.98 and diffpx <= 0.5 %, and 100 % passed. A node copy of `ParityNative.Diff`/`Ssim`, run over that run's
electron.png and native.png with the same padded masks (run-parity.ps1:304), reproduces both scores exactly. Of the
pixels over the threshold, light-150 has 742, and 712 of them are in the right-most 192 px of the 1728 px crop. Dark-150
has 687, and 644 are in that strip. Every SSIM window below 0.6 is in that strip too (138 light, 90 dark).
A 188 x 26 px box at crop (1540, 62) holds 560 (light) and 566 (dark) of those pixels. That box is where the runner's
desktop text "Windows Server 2025 / Build 26100..." lies under the right end of the pill. At 150 % the pill is 1680 px wide
(x 120-1800). At 100 % it ends at x 1520, and the text is clear of it. With that box left out, the scores would be light
0.9924 / 0.136 % and dark 0.991 / 0.091 %. That is a diagnosis only, and nothing is masked.

Why the text shows only in native: Electron's bar paints the screen strip behind it at a QUARTER of its resolution
(main.js BACKDROP_SCALE = 4; sidecar/Backdrop.cs downscales with HighQualityBilinear; bar.css `.bdrop img` stretches it
back, `object-fit: fill`), so the text is smeared before any CSS blur: gone in light, faint in dark. Native's
`Backdrop.Blurred` painted the grab 1:1 at blur 0 (the header says "ported as is", but the downscale was missing), so the
text showed sharp. That is a real native-vs-Electron difference wherever fine detail is behind the bar. It only shows in
the parity scene at 150 %, because there the runner's text sits under the pill.

Fix: `Backdrop.BarStripScale = 4`, and `Backdrop.Strip` (an area-average downscale to round(w/4) x round(h/4), then a
bilinear stretch back with pixel centres aligned) runs before the blur for the bar's two callers (BarWindow.xaml.cs
RenderGlass, BarWindow.Frame.cs light loop). The launcher's glass is unchanged (stripScale 1). The threshold, the masks and
the parity reference are unchanged.

LIGHT ONLY (`Backdrop.StripFor(dark)`): in dark, Electron's bar glass is not the screen. It is the WALLPAPER cut to the
pill (main.js backdropPicture: dark -> wallStill.rect, lane mantle-seethru-bar-desktop), so the runner's text is not in
Electron's dark glass at all. The first run with the strip in both themes (cloudtest 20261125-184007-ca2d34, gh
45677512004, on 06f00146) gave light-150 ssim 0.9856, diffpx 0.46 % (ok; it was 0.9790 / 0.54 %) and light 0.9969 /
0.02 %. Dark-150 got WORSE, 0.9838 / 0.59 % (it was 0.9847 / 0.50 %), because the smeared text covers more pixels. So
dark keeps the 1:1 grab. The real dark fix is to port the wallpaper cut, so the native bar's dark glass is the still,
not the screen. That is not done here. The bar-islands lane (Levitate over the still) replaces this glass anyway.

Not copied: Electron's light strip is also stretched over a box 14 CSS px bigger than the pill on every side (bar.css
`.bdrop img`, left/top -14px), so its picture is slightly magnified. Native lays the picture 1:1 over the pill.

## Sidecar: the 2 failures and the 2 skips

Read from the trx (`sidecar/tests/sidecar-tests.trx`). Both failures are ClipCaptureTests (the clipboard history, lane
r9-clipemoji-2). They are not SnapPlanTests: all 30 SnapPlanTests passed, the snap-strand ones from 8272f253 included.

- `A_private_skip_records_no_format_names_and_no_text_anywhere`: "Assert.AreEqual failed. Expected:<skipped>.
  Actual:<moved>." The TEST was wrong: it read `recent[recent.Count - 1]` as the newest entry, but ClipHistory.Recent
  inserts at 0 and keeps 8 (sidecar/ClipHistory.cs:674-677), so the last entry is the oldest, an earlier test's
  "moved". It now reads `recent[0]`. The code is unchanged.
- `Twenty_five_pins_are_accepted_the_26th_is_refused_and_a_too_large_item_cannot_be_pinned`: ArgumentOutOfRangeException
  at `items[25]`. The TEST was wrong: it made 26 copies before pinning, but the history keeps 25 unpinned items
  (CLIP-032, the test just above it, ClipHistory.TrimLocked), so the 26th copy dropped the oldest and only 25 items were
  listed. It now makes 25 copies, pins all 25, then copies a 26th (a pin does not count toward the 25, so it stays) and
  checks that pinning it is refused in words. The code is unchanged.
- `BothExes_AnswerTheSameShape` and `Recorder_SameOutcomeAsCsc` are skipped (Assert.Inconclusive): "MANTLE_SIDECAR_CSC_EXE
  is not set to an existing exe (run check.ps1 -Compare)". This is by design: ci.ps1 runs check.ps1 without -Compare, so
  the two exes they compare are not built.

`check.ps1` now names every test that did not pass, with its message, read from the trx. It prints each one as a log line
and adds the list in [...] to the tests part of the SIDECAR-LINK line, which becomes the STAGE line's detail. The console
filter that prints the first 40 Passed/Failed/Skipped lines stopped before both failures, which is why sidecar.log showed
only Passed lines.

`elevate.status: ERROR no administrator step has ticket 0 ...` in inproc.txt is EXPECTED on any machine, with or without
admin. The in-process probe calls `elevate.status` with no ticket, and Elevate.cs:347 answers an unknown ticket that way.
Electron's sidecar/test-elevate.js:207 checks for the same words. It is not counted as a failure.
