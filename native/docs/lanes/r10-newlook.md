# Lane notes: the levitated look, ground layer (native/r10-newlook)

Brief: the owner's decision of 2026-11-24 (EM ticket t_c268b8): Quartz's new "levitated" look is Mantle's look too, and
the native rebuild carries it. This lane builds the GROUND LAYER only: one shared style layer every native surface uses.
The bar's and the panels' own layouts are NOT restyled here; they come from a separate bar/panel design prototype, as their
own stage. Spec: the design prototype quartz-home/app.js + style.css (2026-11-24); its numbers are the spec.

State: written, not built and not run. This laptop has no .NET SDK and the window's rule was no dotnet, no cloudtest.
Every signature called was re-read from the tree; the held cloud command is in the last section.

## What is where

Pure (Mantle.Core, namespace `Mantle.Core.Look.Levitate`, folder `src/Mantle.Core/Look/Levitate/`):
- `LevitateTokens.cs`: the C# mirror of `Theme/Levitate.xaml`, plus the numbers only the code needs: islands and gaps (gap
  8, island radius 12, card radius 16 = r-l, r-m 10, card padding 16), control fills (white at .00/.07/.12/.18, pressed
  .05), the frost (512 px wide, sigma 6 light / 15 soft, BIG = min side >= 48 and area >= 20000 DIP), the dim (default
  .20, range .20..0.90), the film (rgb 10 12 14, cap .56, step .04, target .163), the fallback #2c5258, the float /
  float-hi / text-lift shadow layers, and the motion curves and durations.
- `Frost.cs`: `BaseSize` (512 wide, height by aspect), `Pad` (ceil(3 sigma)), `Kernel` (normalised Gaussian), `Padded`
  (edge rows and columns stretched into the padding), `Blur` (pad, separable Gaussian across then down, crop), `Both`
  (light and soft). BGRA bytes in and out, every channel alike, rounded half up.
- `Island.cs`: `IsBig`, `Cover` (s = max(vw/iw, vh/ih), cw, ch, ox, oy, no rounding), `Draw` (the copy at (ox - left,
  oy - top), size (cw, ch)), `ToBitmap` (a monitor point to a bitmap pixel: floor((sx - ox) / cw * bw), clamped).
- `Legibility.cs`: the v2 brightness check. `Film(Rgb[] samples, double dim) -> double` is THE function (pure, never
  throws); `Film(bool auto, ...)` (manual = 0), `Grid` (8 x 5, entry i * 5 + j), `Samples` (off a blurred copy), `ForPanel`
  (picks the copy the panel shows), `Show` (2 decimals, JS rounding), `Lin` / `L` / `LumOf`.
- `LevitateDim.cs`: `Resolve(double? saved)` (default .20, clamp .20..0.90) and `Read(look, theme)` (glass.<theme>.dim and
  dimAuto, the shared() shape too).
- `Motion.cs`: `Bezier` (cubic-bezier, `Y(x)` solved Newton then bisection), `Track`, `LiftIn`, `SinkOut`, the sink
  origin 50 % 35 %, `Slide` (translateX + width, 320 ms ease-lift), `SlideDuration(instant, reduce)`, `TotalMs`, `At`
  (a track's value at a time; reduce-motion = the end state).

WinUI (Mantle.App, folder `src/Mantle.App/Shell/Levitate/`), kept thin:
- `LevitateStill.cs`: the frost of the look's still, made ONCE per (file, stamp) and shared. It reuses DesktopGlass's
  still loading: `DesktopGlass.Look.Still` (look.json desktop.still), its file watch and its `Changed` event. It only adds
  a 512 px-wide decode (the same BitmapDecoder calls as `DesktopGlass.Decode`, without the cover cut) and the two blurs on
  a pool thread, then two small WriteableBitmaps on the UI thread. A failure is said in the log once per still.
- `LevitatePanel.cs`: one panel's three layers, `Picture` (the copy through an ImageBrush, scaled to (cw, ch) and
  translated to (ox - left, oy - top), or the flat #2c5258 until ready / on failure), `Dim` (black at dim) and `Film`
  (rgba(10, 12, 14, a)). `Place(vw, vh, x, y, w, h, dark)` or `Place(hwnd, element, dark)` (the element's layout
  position via `GlassLayer.At`, its layout size). Logs its state on change ("frost soft", "frost light", "fallback: why").
- `LevitateShadow.cs`: float and float-hi as `CssShadow` layers (the OSD's Chromium-exact box-shadow bitmap).
- `LevitateMotion.cs`: `LiftIn`, `SinkOut`, `Play`, `Slide` on composition; each curve sampled from Core's `Bezier`
  into linear keyframes (as PopupWindow plays the bar's spring), so ease-lift's overshoot is Core's exactly.
- `Theme/Levitate.xaml`: the tokens (`LvGap`, `LvIslandR`/`LvIslandRCorner`, `LvCardR`/`LvCardRCorner`, `LvRM`/`LvRMCorner`,
  `LvCardPadding`/`LvCardPaddingThickness`, `LvGapThickness`, `LvCtlFillRest/Hover/On/Strong/Pressed`, `LvFallback`,
  `LvEaseBezier`, `LvEaseLiftBezier`, `LvDurMs`, `LvDurSlowMs`, `LvSlideMs`), merged once by Theme.xaml after Tokens.xaml.

How a surface uses it: make a `LevitatePanel(name, radius)` per island, put `Picture`, `Dim` and `Film` (in that order)
under the island's own content in a Grid, call `Place(hwnd, islandElement, dark)` when the window is placed / laid out /
moved and `Theme(dark)` on a theme change, `Down()` when hidden, `Release()` when closed. Draw the island's shadow with
`LevitateShadow.Bitmap(...)` in an Image behind it (the window needs room: up to 60 DIP below for float, 88 for
float-hi). Bind radii, gaps, padding and control fills to the `Lv*` resources. Switch panels with
`LevitateMotion.SinkOut(old)` / `LevitateMotion.LiftIn(new)`; move a selection indicator with `LevitateMotion.Slide(...,
instant: true)` the first time, then `instant: false`.

No surface is wired to the layer in this lane (a proof wiring was optional; without a build it would only add risk to a
live surface). Nothing here uses or builds on the top 100 DIP guard band, and nothing adds a `region` field.

## Tests (all written, not run)

`tests/Mantle.Core.Tests/Look/`:
- `LevitateTests.cs`: IsBig edges (47/48, 19999/20000, 19968/20016), cover geometry (16:9 on 16:9, square on 16:9,
  2:1 on 4:3, degenerate sizes), the panel's draw offset, `ToBitmap` mapping and clamps, the 8 x 5 grid (cell centres,
  i-major order) and its clamps off the picture, BGRA to RGB sampling, the dim (default, clamp both ends, NaN, saved value
  wins) and its look.json read (dimAuto, shared() shape), `BaseSize`, `Pad` and `Kernel`, the pad stretch on a 2 x 2, a
  uniform picture staying uniform to its borders at sigma 1 / 6 / 15, one bright pixel spreading by the kernel (7 x 1 at
  sigma 1: 1 14 62 102 62 14 1), no-blur copy, bad bitmap, both curves' key values (and ease-lift's peak 1.003737 near
  x .7047), the lift/sink tracks, delays, reduce-motion end state, the slide and its instant mode.
- `LevitateFilmTests.cs`: luminance, dark = 0, white at dim .20 = the 12th step (0.4799999999999999, bit for bit the JS
  accumulation, shown 0.48, one step less does not read), the .56 cap, more dim = less film (.50 -> 0.16; pale sky .30 ->
  0.2), the top 10 % ignored (4 of 40 white -> 0; 5 of 40 -> as all white), manual = 0, no samples = 0, a panel using the
  soft copy when big and the light one when small, and the data-driven `Every_v2_vector_file_matches` over every JSON file
  in `Fixtures/look/v2/` (tolerance `FilmTolerance` = 1e-9, or the file's own `tolerance` if larger).
- `LevitateTokensTests.cs`: Levitate.xaml against LevitateTokens (every key), the fallback, the curves' strings, and
  Theme.xaml merging Levitate.xaml after Tokens.xaml.

The v2 vectors: `tests/Mantle.Core.Tests/Fixtures/look/v2/film.json`, 6 cases computed by
`tests/Mantle.Core.Tests/Look/gen-levitate-vectors.js` from the formula, marked `"source": "hand-computed, replace with
look-sync v2 vectors"`. Shape: `{ source, tolerance?, cases: [ { name, dim, film, samples: [[r, g, b] x 40], auto? } ] }`
(`auto: false` expects 0). look-sync v2's vectors drop in as another file of that shape (or replace film.json; then
`The_placeholder_vectors_say_they_are_placeholders` goes with it). If v2 ships a luminance grid instead of colours, add a
second `Film` overload over `double[]` luminances in Legibility.cs; the step loop is shared.

## Deviations and choices

- Dim range: the prototype's own slider is 0..0.60 in 5 % steps; this follows the contract range 0.20..0.90 (clamp), as
  the brief says. `LevitateDim.Resolve(0.05)` is 0.20; a saved 0.75 (beyond the prototype's slider) stays 0.75.
- Dim default: .20 here; the 2026-11-11 look's `Wash.Alpha` seeds a missing or out-of-range Dim at .30 and does not clamp.
  `LevitatePanel` reads the dim through `DesktopGlass.Look` (Wash's answer: its Dim and mode), because the look host does
  not expose the raw `glass.<theme>` to the App. So until it does, a missing Dim paints .30, not .20, and an out-of-range
  one paints .30, not the clamp. `LevitateDim.Read` is ready for the raw values; the swap is one line in
  `LevitatePanel.FromGlass`. `LevitatePanel.Look(dim, auto)` overrides it meanwhile.
- The cover is computed on the blurred copy's own size (512 x h), not the still's original size: only the aspect enters
  the geometry, and the bitmap's height is rounded, so the two can differ by under one bitmap row.
- The sampling map floors (bitmap pixel = floor((sx - ox) / cw * bw)) and clamps; the prototype's exact rounding was not
  read in this lane (the vectors feed colours straight into `Film`, so they do not depend on it).
- Ties in the luminance sort keep the input order (JS's sort is stable; C#'s Array.Sort is not, so the comparison breaks
  ties by index).
- "Gaussian blur 6 px" is read as CSS / canvas `blur(6px)`, i.e. sigma 6 in bitmap px, kernel radius = the padding.
- text-lift is a token only (`LevitateTokens.TextLift`): WinUI text has no CSS text-shadow; the next stage picks how a
  label wears it (a composition DropShadow from the text's alpha mask, or none).
- Control fills are XAML brushes; Levitate.xaml is therefore the second file allowed colour literals (one line in
  TokensTests' lint).

## Parity rows

None. `docs/parity/` has no row for the levitated look (grep frost / glass / dim / legib: the look rows SEE-001..022 are
the 2026-11-11 sharp-painted look; the others are other features' dims and blurs). None added. Note for the next stage:
SEE-005 ("paint the still SHARP, no blur") and the guard rows SEE-017/020 describe the look this one replaces; they need
a decision when the surfaces move over.

## Shared files touched (small, marked `lane r10-newlook`)

- `src/Mantle.App/Theme/Theme.xaml`: one merged dictionary (Levitate.xaml) after Tokens.xaml.
- `tests/Mantle.Core.Tests/Look/TokensTests.cs`: one line, the colour lint skips Theme/Levitate.xaml.
No App.xaml, service registration or csproj change (the static layer needs none; the SDK globs pick the files up).

## Proposed contract changes

None needed: the layer uses look.json's existing `glass.<theme>.dim` and `glass.<theme>.dimAuto`. For the EM to decide:
- the dim range: the contract's 0.20..0.90 vs the prototype's 0..0.60 slider (one of them should move);
- the default: .20 (the levitated spec) vs the 2026-11-11 seed .30 (look-sync SEED.glass, r10-look's seed);
- whether `desktop.guard` (the top-band guard record) retires with v2, since the film is now per panel and local.

## What the next stage needs from the bar / panel prototype

- Each island's layout rect and radius (which surfaces are islands, which are cards, what is BIG), and the bar's own
  shape: the top 100 DIP guard band goes away with v2, so the bar is an island like the rest.
- Where the shadows go (float vs float-hi per surface and state) and the room each window leaves for them.
- Which elements use the sliding highlight and the sink-and-lift, and what counts as a "switch".
- Text: which labels wear text-lift, and the ink colours over the frost in light and dark (the prototype's ink tokens are
  not in this layer).
- The Settings > Look row for the levitated look (the slider's range and steps; Automatic = the check on).

## Held

The cloud check, not run in this window:
`cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit,ui"`

## LOOK.md + v2 (lane r10-newlook-2)

The first spec (the prototype, 14:27) is replaced by LOOK.md (look-sync design commit 97aa001; its numbers reached this
lane through the brief, the file itself is not in a local checkout), look-sync v2's test vectors (look-sync 9a53e89, not
merged yet: `test/lift-v2.json`, `test/fixtures/lift-v2.gen.js`, the CONTRACT v2 section, refreshed with 953d039's "No
Blur control") and the coordinator's notice of 14:50 (read, applied, not committed).

### Every number changed (old -> new)

- Gap, island to window / screen edge and between big panels: 8 -> 12 (`Gap`, `LvGap`, `LvGapThickness`).
- New: islands in a row 6 (`GapRow`); content edge = chrome edge 12 (`ContentInset`); a panel opens 12 above the bar
  (`GapAboveBar`, `LvGapAboveBar`).
- r-m 10 -> dropped (`RadiusM`, `LvRM`, `LvRMCorner`: LOOK.md has no 10).
- New radii: controls 4 / 6 / 8, pop-overs 14, a floating page 14, the big chart panel 20, pills and round buttons 999;
  toolbar / tab islands 12 and cards 16 unchanged; `Nested(outer, gap)` = outer - gap (never below 0).
- New: spacing grid 4 / 8 / 12 / 16 / 24 / 32; rows 32 (lists) / 36 (menus) / 40 (tray); toolbar buttons 32 (28 small),
  round controls 36 (28 small, 44 main), pill buttons 28. Card padding 16 unchanged.
- New colours (Lv* only; Tokens.xaml untouched): text #EEF6F7; text-2 / -3 / -4 rgba(230,242,244,.76 / .54 / .34);
  accent #A9E7ED, ink on it #052A2F; ok #8FE3B4; down #FF9D95; hair white .09 (dividers inside an island only). Fills
  inside an island unchanged (.00 / .07 / .12 / .18, pressed .05).
- New: white sheen on HOVER only .07 (.05 on big panels); none at rest (`SheenHover`, `LvSheenHover`, `LvSheenHoverBig`,
  and `LevitatePanel.Sheen` + `Hover(bool)`).
- New: focus ring 2 px accent, offset 2. Type families Segoe UI Variable Text / Display (with Tokens.xaml's fallbacks);
  weights 400 / 500 / 600, 300 only on a hero clock.
- Shadows: float and float-hi unchanged; new sheet `0 2px 4px .18, 0 24px 48px -12px .45, 0 60px 100px -40px .60`
  (rgba(0,12,16,a)); new text on islands `0 1px 2px rgba(0,0,0,.22)` (`IslandText`); text-lift renamed `BareText`
  (values unchanged).
- Curves: ease and ease-lift unchanged; new morph curve cubic-bezier(0.22, 1, 0.36, 1) (`EaseMorph`). Press scales new:
  0.94 / 0.97 / 0.985. Hover / press 160 unchanged.
- Lift in: opacity 280 ms after 60 (ease) -> 240 after 40 (ease-lift); translateY from 10 -> 28, 420 after 40 -> 460
  after 20; new scale 0.965 -> 1 (460 after 20). `TotalMs(LiftIn)` 460 -> 480. Sink out unchanged (200 / 300, 12, 0.955).
- Sliding highlight: translateX + WIDTH -> translateX + scaleX (`Slide(fromX, fromW, toX, toW, baseW)`); new accent
  underline 16 x 2, translateX only, centred (`SlideUnderline`, `UnderlineX`). 320 ms ease-lift unchanged.
- New: pop-over in (opacity 160, translate + scale 260 from translateY -6 and 0.97, ease-lift) and out (120 / 160);
  press / release (160); first load (rise 10 with opacity over 520, staggered 45, never replayed); morph (480; old fades
  200, new 300 after 90, scrim 360; fallback fade + translateY 12 + scale 0.97 in 320).
- Reduced motion: "the end state at once" -> no travel (a transform is at its end at once) and quick fades only (a fade
  at most 160 ms, no delay). `TotalMs(LiftIn, reduced)` 0 -> 160.
- Brightness check: the step was `a += 0.04` (0.4799999999999999) -> `round2(k * 0.04)` (0.48 exact); a dim outside
  0..1 was used as given (-5 gave the .56 cap) -> checked as 0.20 (0.48); new: channels clamped to 0..255, malformed
  samples (null, under 3 entries, a non-finite channel) skipped, none left = 0, the index floor(n x 0.1) counts valid
  samples only. New pure entry `Legibility.LiftAlpha(double[][], dim)` (+ a dimAuto overload); `Film(Rgb[])` uses the
  same core.
- Unchanged: Dim (default .20, the slider clamp 0.20..0.90, nothing below .20 written), the frost (512 wide, light 6 / soft
  15, pad 3 x the radius by stretching edge pixels), BIG (min side 48 and area 20000, layout size), the film rgb(10,12,14),
  cap .56, target .163.

### The v2 vectors

`Fixtures/look/v2/lift-v2.json` is a byte copy of look-sync 9a53e89 `test/lift-v2.json` (in its tree
`test/fixtures/lift-v2.json`; git stores it with LF; it is not
hand-edited); the placeholder `film.json` is deleted and `gen-levitate-vectors.js` no longer writes it (it keeps the
curve and kernel reference values). Shape: `{ about, lift: { cols 8, rows 5, quantile .1, rgb [10,12,14], target .163,
step .04, max .56 }, vectors: [ { name, dim, samples: [[r, g, b] x 40] before Dim, alpha } x 27 ] }`.
The Engineering Manager's port rules equal look-sync's own `liftAlpha` (9a53e89) to the letter. `lift-v2.gen.js`'s REF
(the prototype's code) differs in what it does not do: no dim range check, no clamp, no skip of malformed samples (it
would throw on no samples), and it accumulates `a += 0.04` and rounds only at the end (`toFixed(2)`). On the 27 vectors
the two agree: the decisive luminance never comes closer than 4.7e-4 to 0.163, far beyond the ~1e-16 between an
accumulated and a rounded step. A JS re-do of the C# formula, line for line, gives 27 of 27 exactly (run once, scratch
script not kept).

### Tests (all written, not run)

- `LevitateFilmTests`: kept `Luminance_is_WCAG_relative_luminance`, `A_dark_picture_gets_no_film`,
  `More_dim_means_less_film` (now exact), `The_brightest_10_percent_is_ignored_index_4_of_40_decides`; changed
  `Bright_white_gets_the_first_step_that_reads` (0.48 exact), `The_film_stops_at_056`,
  `A_panel_samples_the_copy_it_shows_soft_if_big_else_light` (+ the 19968 / 20016 edges); new
  `The_steps_are_rounded_to_two_decimals_not_added_up`, `A_dim_outside_0_to_1_is_checked_as_020_and_0_to_1_is_taken_as_given`,
  `The_index_counts_valid_samples_only`, `Channels_are_clamped_to_0_255`, `Malformed_samples_are_skipped_and_none_left_is_0`,
  `Manual_is_no_film` (was `Manual_is_no_film_and_no_samples_is_no_film`), `Every_lift_v2_vector_matches_exactly` (was
  `Every_v2_vector_file_matches`; exact, no tolerance), `The_lift_v2_constants_are_ours`, `Only_look_syncs_file_is_in_the_v2_folder`;
  removed `The_placeholder_vectors_say_they_are_placeholders`.
- `LevitateTokensTests`: new `Gaps_are_12_to_the_edge_and_between_big_panels_6_in_a_row`, `Radii_are_LOOK_mds_and_r_m_10_is_gone`,
  `A_nested_shape_is_the_outer_radius_less_the_gap`, `Spacing_rows_and_control_sizes`, `Colours_are_one_neutral_family_and_one_accent`,
  `The_white_sheen_is_007_on_hover_005_on_big_panels_and_none_at_rest`, `Focus_is_one_2px_accent_ring_offset_2`,
  `Type_is_Segoe_UI_Variable_Text_and_Display`, `The_shadows_are_LOOK_mds_tinted_0_12_16`, `The_film_the_frost_and_the_dim`;
  changed `Motion_tokens_match_the_mirror`; kept the control fills, the fallback and the Theme.xaml merge; removed
  `Islands_and_gaps_match_the_mirror` and `The_shadows_and_the_film_are_the_prototypes` (split into the new ones).
- `LevitateTests`: changed `Durations_and_the_sink_and_lift_tracks`, `A_track_holds_its_start_through_the_delay_then_follows_its_curve`;
  new `Motion_is_transform_and_opacity_only_never_width_or_blur`, `Reduced_motion_is_no_travel_and_quick_fades_only` (was
  `Reduce_motion_is_the_end_state_at_once`), `The_sliding_highlight_is_translate_and_scaleX_over_320ms_on_ease_lift_carrying_a_16x2_underline`
  (was `..._moves_x_and_width_...`), `Pop_overs_press_and_the_first_load`, `The_morph_grows_the_panel_out_of_the_box_in_480ms`.

### Choices and deviations

- Curves LOOK.md (as the brief gives it) does not name: lift-in's opacity rides ease-lift with its transform (the list
  ends ", ease-lift"; the first lane had ease); pop-over out ease; first load opacity ease, rise ease-lift; the morph's
  fades on the morph curve; its fallback ease. Origins: sink 50 % 35 % (first spec), pop-overs top centre, press
  centre, morph top-left.
- Reduced motion's "quick" fade is 160 ms (`ReducedFadeMs` = the hover / press duration): LOOK.md names no number.
- Page-to-page cross-fade: no duration given, no token added.
- Re-blur: the copies are 512 wide whatever the monitor, so they are made again only when the picture changes; a window
  or monitor size change re-lays them (the cover geometry) without new pixel work. That is inside LOOK.md's "only when".
- The brightness check now runs at most once per frame while a panel moves (`Place` queues one recompute on
  CompositionTarget.Rendering, hooked only while one is pending), never at rest. Composition plays the motion: no loop
  of our own.
- Tabular figures have no token (WinUI sets them per text element); the type SCALE is not set by LOOK.md, so the native
  scale in Tokens.xaml stays.
- No island edge anywhere (no hairline, ring or top highlight); `Hair` is for dividers inside an island only.
- The first lane's open questions on the Dim range and default are settled (20 %-90 %, default 20 %, the owner).
- `DurSlowMs` 340 is kept: the brief's LOOK.md list neither has nor drops it.

### No Blur control; retiring blurPx later

The new look's only look settings are Dim (slider 20-90 %, default 20) and the brightness check (on / off, on by
default, `glass.<theme>.dimAuto`). `docs/parity/settings-mantle.md`: SETM-171's Blur slider is marked a documented
difference ("no Blur control in the new look (the user, 2026-11-24)"), SETM-172 resets Dim and the check only. There is
no See-through / render SETM row in that file (the `glass` key table near its end is not a parity row; left). This
layer has no setter (`LevitateDim` only reads), so nothing here can write `dimAuto` on a Dim change; the first writer
(Settings) must write dimAuto only from the check switch, with a test. What retiring blurPx will need later: the native
glass writer spreads the stored entry and never writes or removes blurPx (v1 apps still read it); no native Settings row
for it; once no native surface paints with blurPx (the levitated look never reads it), `Mantle.Core/Look`'s existing
blurPx reading (another lane's, Electron-compat) can stop exposing it, in that lane; look-sync keeps its seed and range
until every app is on v2.

### Left

Wiring the surfaces (a later stage); the raw `glass.<theme>` from the look host (`LevitatePanel.FromGlass` still sees
Wash's .30 seed, see above); how text wears `IslandText` / `BareText` in WinUI; the focus ring and press / hover
plumbing on real controls; the cloud check (Held, above).
