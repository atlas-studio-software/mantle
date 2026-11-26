# Lane r15-quick-launcher: the launcher's look

The launcher (Win, Win+S, Win+X Quick links) restyled to the approved prototype `apps-r3/mantle-bar/panels/launcher.css`
(light: `apps-r3/light-mantle`, which copies launcher.css unchanged, inks from `apps-r3/LIGHT.md` section 4). A look lane:
search, ranking, keys, Quick links and the latency lines are not changed.

## What is built
- `Surfaces/Launcher/LauncherSurface.Islands.cs`, on when `BarGeometry.Islands` (that is, unless `MANTLE_TEST_GLASS=electron`):
  one pop-over island, `LevitatePanel("launcher", 14)` under the content (frost, dim, the v2.1 film in dark or the lift in
  light, the sheen on hover only), the float-hi shadow (`LevitateShadow`) round it; no edge, ring or tint. 600 x 520 DIP,
  capped at the monitor less 24 / 80 (L3), centred on the primary display (LAU-002 kept). The window is the island plus the
  shadow's room (44, 12, 44, 88 DIP).
- Field (L7 to L13): 56 tall, an 18 search glyph in text-2, the query at 16; no fill, no ring; a 1 px hair under the whole
  width and the accent line 16 in from each side while the query has the keyboard (a 160 ms fade).
- List (L18 to L42): padding 4 8 8; headings 12 / 600 in the text ink; rows 36, padding 0 12 0 8, radius 8, the icon tile
  24 / 6 (a glyph on fill-1 in text-2, a letter on fill-2); Quick links rows 32 with no icon; the selected row is fill-2 plus
  the 3 x 14 accent tick; an armed Quick links row (and its tick) in warn, a note in ok or warn.
- Foot (L59 to L65): 40 tall, a hair on top, the hints at 12 in text-2, 16 apart; keycaps 11 on fill-1, padding 1 6, radius 4.
- Readable text is the text ink or text-2; text-3 is only the placeholder (L13). Inks: `Theme/Levitate.xaml` LvBar* and
  `IslandsInk.Accent` in both themes. Numbers: `Theme/LvLauncher.xaml`, read once by `LauncherSpec.cs`.
- Motion: the pop-over in (opacity 160, translateY from -6 and scale from 0.97 over 260 on ease-lift, about the centre) and
  out (120 / 160 on ease), transform and opacity only, quick fades under reduced motion (`LevitateMotion`); nothing at rest.
- The old glass look (the `MANTLE_TEST_GLASS=electron` path, the unfold) is unchanged. The see-through `GlassLayer` path is
  gone from the launcher: it ran exactly when the islands look now does.

## New UIA ids
- `field-line` (Group): the accent line; ItemStatus `focused` while the query has the keyboard.
- `row-tick` (Group) in each row: ItemStatus `on` on the selected row only.

## Tests
- Unit `Mantle.Core.Tests/Look/LauncherSpecTests.cs`: every LvLauncher.xaml number against launcher.css / look.css (lines in
  the comments), the type / radius / spacing scales, the shadow room against `LevitateTokens.FloatHi`, the pop-over motion
  against `PopupLevitate`, reader and dictionary keys equal, no `--` in a XAML comment, every LvBar ink used present in both
  themes, text-3 used once (the placeholder).
- UI `Mantle.UiTests/Launcher/LauncherLookTests.cs`: the island's size and centre, the field line (on with the keyboard,
  the island less 32 wide), rows 36, the tick 3 x 14 on the selected row alone and following Down. It is Inconclusive
  (visible) if the run has the islands look off. No pinned size or position moved in the existing launcher tests (none
  pinned one).

## Needs a change outside this lane (not made)
- `tests/Mantle.UiTests/Glass/GlassTests.cs` `The_osd_and_the_launcher_paint_the_desktop_still_sharp_lined_up_under_their_own_tint`
  (lines 325 to 333) waits for `glass launcher: painted the desktop still`. The islands launcher no longer uses GlassLayer, so
  that half fails. Its owner should replace it with the islands check (`launcher: islands look: one island` and
  `levitate launcher: frost`) or keep it as `Assert.Inconclusive("the launcher wears the islands look (lane r15-quick-launcher), no GlassLayer")`.
- `docs/parity/launch.md` LAU-001 says 680 x 460 inset 10; in the islands look it is the 600 x 520 island in a window with
  the shadow's room. LAU-026 (the fold) is the old look only; the islands look pops out instead.

## Parity (tools owner; not made here)
Today the launcher stage still passes as before: `run-launcher.ps1` line 189 sets `MANTLE_TEST_GLASS = 'electron'`, which
keeps native on the old glass look, so the Electron pixel compare never sees the new look. Under plan v0.2 the restyled
launcher leaves the Electron pixel compare; behaviour rows and latency still follow Electron. The change:
1. `native/tools/parity/references.psd1` lines 14 to 17: add `launcher = 'native-golden'` (and a comment line
   `# launcher: read by launcher/run-launcher.ps1`).
2. `native/tools/parity/launcher/run-launcher.ps1`: read the reference as `run-parity.ps1` lines 58 to 70 do
   (`$refFile = Join-Path $PSScriptRoot '..\references.psd1'`, `$golden = $reference -eq 'native-golden'`, the REVIEW.md
   check on `..\goldens\launcher\REVIEW.md`). When golden: at line 189 do not set `MANTLE_TEST_GLASS` for the native run
   (shoot the islands look); keep the Electron run (lines 147 to 181) and the native opens, typing and rows unchanged, so
   the behaviour lines and `LAUNCHER-LATENCY` (lines 239 to 242) still print; replace the score block (lines 227 to 238)
   with `Write-Host "LAUNCHER-PARITY ${t}: launcher golden pending review"` until REVIEW.md has reviewer:, run: and sha:
   lines, then score native against `goldens/launcher/<theme>-<pct>.png` at the same thresholds; do not print the
   "window sizes differ" problem (line 229) in golden mode.
3. `native/tools/ci.ps1` `Stage-launcher` (lines 452 to 462): a theme whose log line says `launcher golden pending review`
   is reported as such and fails the stage the way the bar's pending golden does; the floors `'launcher light' = 0.84` and
   `'launcher dark' = 0.80` (lines 417, 418) stay, never loosened.
No goldens were made, frozen or regenerated.

## Behaviour: prototype (panels/launcher.js) against native (not built here)
| Behaviour | Native | Why |
|---|---|---|
| Grouped results with headings, groups ordered by best row, web row last | present | LauncherGroups (Electron's groups.js) |
| Empty box: your commands, then apps, twelve rows | present | LauncherGroups keeps twelve on an empty query |
| Calculator row (answer, "no answer" reason, Enter copies, "Copied" note) | missing | LauncherGroups takes a calc row, but the page never passes one |
| Power rows that ask first (Restart, Shut down, Sign out) in the search | missing | items carry Confirm, but Enter sends apps.launch at once (no arm, no Esc cancels) |
| Quick links (Win+X): 18 rows, letters, Shut down submenu, ask-first 5 s, notes | present | QuickMenu; the 5 s run-out hairline (.lch-arm) is not drawn |
| Files group, "No files match" status, Ctrl+Enter Show in folder | missing | file rows are not searched yet; the Ctrl+Enter hint appears only for a file row |
| Website row for a typed URL | not checked | the Website group exists; native refuses URLs live (launcher.md) |
| Web row "Search the web for ..." | different | shown, but Enter only logs that web search is not built |
| No-match line above the web row | missing | native shows its empty line only when there are no rows at all, and the web row is always there |
| Hint line: Enter verb, Ctrl+Enter, Tab Next group, Esc Close; Quick links Letter / Enter / Esc Back or Close | present | renderHint words; armed Enter Confirm / Esc Cancel and calc Copy answer are missing with their rows |
| Arrows | different | native wraps at the ends (LAU-048, Electron); the prototype stops |
| PageUp / PageDown / Home / End | different | native has them (LAU-049, LAU-050); the prototype has none |
| Tab / Shift+Tab between groups, Enter, Esc | present | |
| Hover | different | native selects the row under the pointer (LAU-056); the prototype only tints it fill-1 |
| Typed text kept on reopen | different | native keeps it and selects it; the prototype keeps it 90 s |
| Placement | different | native centres on the primary display (LAU-002); the prototype opens above the M |
| Sliding highlight, sink and lift between views, Quick links page cross-fade | missing (look) | per-row fills swap in place; left for a later pass |

## Not verified
No .NET SDK on this laptop: nothing was built or run, no test, no screenshot. Not seen on screen in either theme. Unchecked:
WinUI unboxing of the x:Double / Thickness / CornerRadius resources through `ResourceDictionary.TryGetValue` (a miss falls
back to the prototype number and is logged), the float-hi shadow bitmap's cost on the first build (made at Create), the
pop-over origin on the first show, and that clicks in the shadow's room (now up to 88 DIP below the island) land on the
launcher window instead of closing it.
