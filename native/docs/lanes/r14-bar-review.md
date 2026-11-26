# Lane r14-bar-review: two findings of the bar design review

Branch native/r14-bar-review, base d949febb (native/stage11). Evidence: the committed CI run 20261126-025458-52914a.

## 1. The islands are flat #2c5258 in both themes: runner side

The islands DO call Place and Paint, and LevitateStill is asked; there is simply no still to frost in a test run.
From the run's logs (parity's `parity/dark/native-bench/native-log.txt`, the same in light and at 150 %):

```
desktop: this test run leaves look.json's desktop record as it is and never reads Windows' wallpaper (MANTLE_TEST_WALLPAPER names no picture)
levitate bar left island: the flat fallback colour: the look has not been read yet
glass: no desktop still: Mantle hasn't shared a desktop wallpaper yet
levitate bar left island: the flat fallback colour: Mantle hasn't shared a desktop wallpaper yet
```

and the smoke profile (`logs/smoke-profile-mantle-native.log`), whose look.json has a record with no picture:

```
glass: no desktop still: the desktop record names no picture
levitate bar left island: the flat fallback colour: the desktop record names no picture
```

No "levitate: the frosted still ..." line and no "the frosted still could not be made" line appear anywhere: the frost
was never tried, because DesktopGlass.Look.Still is null. `parity.log` says `wallpaper True` (run-parity.ps1 sets the
runner's Windows wallpaper to assets/backgrounds/snow-peak.jpg), but a `--test` run never reads Windows' wallpaper
(Shell/Glass/DesktopPublish.cs, lane r10-look) and the parity profile's look-sync folder starts empty.

What a test run needs (no App change; tools are not this lane's):
- parity: `native/tools/parity/run-parity.ps1`, before the native Start-Process (line ~333), set
  `$env:MANTLE_TEST_WALLPAPER = $wall`. DesktopPublish then publishes that picture as the desktop record and still into
  the run's own look-sync folder, DesktopGlass follows it and LevitateStill frosts it.
- a UI test: either the same env through BarApp.Launch's `env`, or a LOOK_SYNC_DIR look.json with a desktop record and
  a still of its own, as GlassTests' Fixture writes.

## 2. The light lift (apps-r3/LIGHT.md section 4b): wired

Core already had the light path: `Legibility.Film(bool auto, Rgb[] samples, double dim, string theme)` with
theme "light" (darkest first, index 4, #F4F7F8, target 0.386, cap .72), `Legibility.Samples`, `Legibility.FilmRgb`
and `LevitateTokens.LightFilm*`. Only `Legibility.ForPanel` is dark only, so LevitatePanel composes the light case
itself from the same pieces.

- `Shell/Levitate/LevitatePanel.cs`: in light the film layer is rgba(244, 247, 248, a), a = the light check over the
  copy the panel shows (soft if big, else light), after the dim; dark is unchanged; the check off (dimAuto false) = 0.
  Before the frost is ready (or when it fails) a LIGHT panel gets the dim and the lift over the flat #2c5258, as
  LIGHT.md 4.2 says ("the light check reads that colour too"); a dark one keeps the bare fallback. The log says
  "light lift 0.60" in place of "film 0.00" in light.
- LIGHT.md 4.2's ".40" for the fallback is the v2.0 target's (0.226) answer: at v2.1 (0.386) and the seed dim 0.20 the
  lift is 0.60 (0.56 undimmed, 0.64 at dim 0.45 and up).
- Inks: Theme/Levitate.xaml has dark inks only (LvText #EEF6F7 ...); in light the islands keep today's Lg tokens
  (LgInk #0B0E1A, LgInk2 #333A4E). LIGHT.md's #0F1E21 and .76 / .56 are not here; that is the inks lane's. The lift does
  not need them: over the lifted fallback LgInk2 is about 112 luminance apart (it was 13).

## Tests (Mantle.UiTests)

The three visible SKIPs "light lift not built" are real tests now; each passes on the lifted flat fallback a test run
shows today, and on a real frost (the lift brings the darkest tenth to L >= 0.386):
- StripTests `Pins_come_first_light_islands_running_line_reads_against_the_frost`: the order / running-line test in
  light (one body, `PinsComeFirst(theme, profile)`, for both themes).
- BarTests `Dark_theme_islands_tint_follows_the_theme`: the left island above the M, light vs dark, dark + 30 < light.
- ThemeTests `Islands_theme_change_reaches_the_islands_within_1_s`: the islands look (MANTLE_TEST_GLASS=islands),
  theme.json light, then `--test-theme=dark` from a second start; the left island goes clearly darker within 1 s.
