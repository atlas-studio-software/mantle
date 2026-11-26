# Lane r14-ci-look (2026-11-26)

Branch `native/r14-ci-look`, off native/stage11 d949febb. EM ticket t_1a4af8, after the Product Designer failed the bar
golden review of the committed run 20261126-025458-52914a. Nothing is frozen. Mantle.App is untouched.

## 1. The runner's desktop text is masked, not cropped

The runner (Windows Server 2025) draws "Windows Server ... / Build 26100.ge_release.240331-1435" at the bottom right of
the desktop. In the bar candidates it shows wherever no island covers it. Measured on that run's four candidates (node
decode, bright near-grey pixels):

| candidate | crop | islands band in crop | text pixels outside the band, bbox (crop px) |
|---|---|---|---|
| light/dark 100 % | 1920x80 at (0,1000) | x 12..1907, y 24..67 | 117, x 1732..1913, y 36..74 |
| light/dark 150 % | 1920x108 at (0,972) | x 18..1901, y 24..89 | 312, x 1627..1913, y 39..101 |

The text also peeks through the right island's rounded corners (inside the band, the last 4 columns).

The mask (run-parity.ps1, golden path only) is everything right of the band and everything below it, in screen px:
`[bandX+bandW, crop.y, W-bandX-bandW, crop.h]` and `[0, bandY+bandH, W, H-bandY-bandH]`. At 100 % that is x >= 1908 or
crop y >= 68; at 150 % x >= 1902 or crop y >= 90. The diff pads every mask by 4 px, which also covers the corners. Checked
against the four candidates: 0 text pixels left open. The first proposal (x >= 1908 or y >= 69 at 100 %, x >= 1903 or
y >= 91 at 150 %) was one pixel short at each band edge; with the 4 px pad it still left 1 text pixel open at 150 % (crop
1898,85). The masks are logged per theme and written to score.json as `watermarkMasks`.

Not done: removing the text on the runner (PaintDesktopVersion needs an explorer restart, and an evaluation edition
draws its text regardless; the setup file is not in this repo). The candidate PNGs still show the text; only the score
ignores it.

The strip stage needs no mask: it scores the dock island (x 915..1004 at 100 %), far from the text (x >= 1627).

## 2. Why the islands were flat on the runner

Not the GPU: the frost is a CPU Gaussian in Mantle.Core (Look/Levitate/Frost.cs, two copies of a 512 px still). Not a
missing wallpaper: run-parity sets assets/backgrounds/snow-peak.jpg as the runner's wallpaper. The cause is that a `--test`
run never reads Windows' wallpaper, so it publishes no desktop record into its own look-sync folder (the temp profile's),
so there is no still. From the light candidate's native log (run 20261126-025458-52914a):

    desktop: this test run leaves look.json's desktop record as it is and never reads Windows' wallpaper (MANTLE_TEST_WALLPAPER names no picture)
    glass: no desktop still: Mantle hasn't shared a desktop wallpaper yet
    levitate bar left island: the flat fallback colour: Mantle hasn't shared a desktop wallpaper yet

Fix (run-parity.ps1, golden path only): `MANTLE_TEST_WALLPAPER` = the runner's wallpaper (snow-peak.jpg), so the test run
publishes it as its desktop picture (DesktopPublish.TestEnv, lane r10-look) into the run's own look-sync folder, and the
islands frost from the same picture that is on the screen. The strip stage runs run-parity.ps1, so it gets the same.

## 3. The accent 'islands' profile in Core

`Mantle.Core/Look/Accent.cs` is now look-sync accent.js at c29ba47: profiles steel (the default, unchanged) and islands,
the base-hue rule (at a profile's base hue per theme the tokens ARE the authored hexes), `ProfileOf`, `BaseHue`,
`IslandsHue = 185`, and `Tokens(hue, theme, profile = null)`. Existing callers (LookHost) pass no profile: steel, as before.

- `Tokens(185, "dark", "islands")`: acc #a9e7ed, strong #c4f0f4, on #052a2f (the authored hexes, exactly).
- `Tokens(185, "light", "islands")`: acc #4d7f84, strong #3c696d, on #ffffff, line rgba(77, 127, 132, 0.4). This is NOT
  #083f49: look-sync's islands light is steel's light (base 212) until an app builds its light look, so at 185 it is
  steel's light S/L at hue 185. A change to that is look-sync's (the EM's) to make.

Tests: `Look/AccentV2Tests` reads `Look/accent-v2.json` (look-sync test/fixtures/accent-v2.json at c29ba47, byte for byte,
git blob ff637b25) and passes all 124 vectors (31 each for steel/islands x dark/light), plus look-sync's accent-v2.test.js
facts (ice blue at 185, one degree moves at most 1-2 steps, islands light = steel light, steel the default).

## Unverified

- That the frosted islands look right to a reviewer: this recipe uploads logs only, so the images need a committed run.
- The mask on a runner image with another build string (longer text): the mask covers everything outside the band, so
  only text inside an island corner larger than 4 px would be missed.
