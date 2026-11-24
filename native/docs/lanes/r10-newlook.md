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
