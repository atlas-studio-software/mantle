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

## 3-9. Review findings 3-9 (the islands look only; MANTLE_TEST_GLASS=electron is unchanged)

3. **The amber dot on More** was the "more pip", warn: in every parity log the last line is
   `more pip: warn (notifications off: Windows is not letting apps read your notifications ...; Desktop not attached: ...)`.
   Two runner conditions, neither the "Bar may not hide" pip:
   - "notifications off" (tone warn): the runner's Windows has Settings > Privacy & security > Notifications > "Let apps
     access your notifications" off, so UserNotificationListener says Denied (Chips.NotificationAccess). That is true of
     the runner and ClusterTests `More_pip_shows_and_says_why_when_notifications_cannot_be_read` depends on it, so the app
     keeps saying it. The parity capture should run without it (tools are read only; the exact change):
     `native/tools/parity/run-parity.ps1`, after line 140 (`if ($golden) { Remove-Item Env:MANTLE_TEST_GLASS ... }`):
     ```
     # the runner does not let apps read notifications, so More would wear the "notifications off" warn pip; the golden
     # capture is the prototype, which has no problem: an empty, readable test list (the centre's own fixture) instead
     if ($golden) { $env:MANTLE_TEST_NOTIFS = '[]' }
     ```
     (NotificationCentre takes the fixture as ok with no toasts; BarWindow.NotifsHealth then clears the chip.)
   - "Desktop not attached" (tone ""): a `--test` run never attaches the desktop layer on purpose
     (DesktopLayer.TestNeverAttached), so the chip was false in every test run. Chips.List now skips it in Test mode, as
     it already skipped the hotkey chip. Not a hidden problem: a live run still raises it.
   The islands pip is now the prototype's (barhide.js `#more .bh-pip`): 6 x 6 at top 8 / right 8, no ring, text-2
   (LvBarInk2) for tone "", the design's warn (LvBarWarn: dark #E3C47E, light #4C3300) only for tone "warn".
4. **The lone divider**: RightSep shows only when now playing or the tray strip shows before it (IslandSep, called from
   PaintMediaChip and the tray's paint). The prototype has the b-sep after the now-playing group; Electron has #tray, then
   .sep, so a shown tray keeps it too.
5. **The 77 px gap** was the always-present 32 x 36 battery box (+ its 2 gap) plus the clock's 80 px time box. With no
   battery BatteryBtn is Collapsed (no room, not in UIA); the clock's time has no min-width in the islands look.
   ClusterTests checks network to clock is the one 2 DIP gap and, with no battery, ends Inconclusive for the battery's own
   box; QuickSettingsTests skips the battery click with an Inconclusive reason on a machine with no battery.
6. **The clock** was BarWindow.Tick's fixed `h:mm:ss AM/PM` (bar.js, not the locale). The islands look reads h:mm with
   no seconds, 24-hour when the user's short-time pattern has H, else h:mm and the locale's AM / PM. The time and date
   are right-aligned and the button is as wide as its text plus 8 each side. BAR-416's 80 px box test now launches the
   electron face; the islands clock has its own test (h:mm, right edges 8 in, left 8 in).
7. **The M** is already the design's: the prototype draws MARK (viewBox 24, the mark's square 3..21) in a 22 px svg,
   native in a 22 DIP Viewbox round the same 24 canvas; both show a 16.5 DIP square. No change; the 14,3 16x5 probe stays
   clear.
8. **Glyph inks**: every bar glyph (M, downloads, volume, network incl. offline, battery, bell, more, the media chip's
   note and play / pause, "+N") is LvBarInk in the islands look (look.css `.btn { color: var(--text) }`: dark #EEF6F7,
   light #0F1E21). The bell no longer drops to LgInk3 when notifications cannot be read (the pip says that). A media
   control that cannot act stays LgInk3 at .5 (disabled). The clock is LvBarInk over LvBarInk2.
9. **The focus underline**: Core has no islands accent profile (no "islands" / 185 in an accent file in Mantle.Core).
   The cell's active line takes a local brush, LvBarAccent (dark #A9E7ED, light #083F49), in Theme/Levitate.xaml's new
   ThemeDictionaries (TokensTests' lint allows colour literals there only). **Core: needs the islands accent profile**
   (look-sync accent.js 'islands', base hue 185); LvBarAccent moves to it when the port lands. The downloads dot and the
   bell's count still use LgAcc.

## Later

Lane r15-bar-light (docs/lanes/r15-bar-light.md) finished the light inks (LIGHT.md section 4) for the islands and the
levitated Downloads shelf, and gave the dock underline, the downloads dot and the bell's count the islands accent (dark from
Core's islands profile, light the local #083F49 until native/r15-accent-v22 merges).
