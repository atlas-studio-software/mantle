# Lane r16-sep-wiring: look-sync v2.3 separation in every Levitate island

Base: native/r16-separation 20dce8f6 (Core's port of look-sync f4356d6, 76 vectors), with native/r15-quick-launcher
(b1e61bd8, S3) merged in. App only; Core is unchanged.

## What it does
- `Shell/Levitate/LevitateLift.cs` (pure C#, no WinUI): `Legibility.PanelLift(look, theme, samples, separate: true)` for
  one island, passed through unchanged (film alpha and colour, sep alpha and colour, Core's dim). The look handed to Core
  is `{ glass: { <theme>: { dim, dimAuto } } }` built from the dim and switch the panel already resolved (LevitateDim).
- `LevitatePanel`: a new `Sep` layer and `Layers` = Picture, Dim, Film, Sep, Sheen (bottom to top, `LevitateLift.Order`).
  The film and the layer are painted in Core's colours at Core's alphas. Recomputed at the same moments the check ran
  before (a Place that moved or resized, the theme, the still, the look); never per frame at rest.
- The brightness check off (dimAuto false): Core answers film 0 and sep 0, so the one switch turns both off. No new
  Settings row.
- Hosts add `lev.Layers` as it is: the bar's three islands (`BarWindow.Islands.cs`), the pop-up host
  (`PopupWindow.cs`: Downloads and quick settings), the launcher (`LauncherSurface.Islands.cs`).
- The log line `levitate <name>: frost ...` now ends with `, sep 0.00 #ffffff` (dark) or `#0a0c0e` (light).

## Both themes
Every Levitate island has its light look built (the bar r15-bar-light, Downloads and quick settings via the pop-up host,
the launcher in S3), so all get separation in dark and light. No Levitate surface is left on the old answer in light.
Surfaces not on Levitate (top bar, toasts, notification centre, media, jump list, OSD) are unchanged.

## Kept as before
- The flat #2c5258 fallback in DARK still shows the flat colour alone (no dim, film or layer), as LOOK.md's "before the
  blur is ready" rule had it; Core would answer film .40 there. Light's fallback goes through PanelLift: at the seed dim
  .20 it is lift .60, sep 0 (the same as v2.1; checked against look-sync's panelLift).

## Tests (unit, Mantle.Core.Tests)
`LevitateLiftTests` (LevitateLift.cs is linked into the test project): the layer order; every `new LevitatePanel(` host
adds `.Layers` and picks no single layer; film / sep / colours / dim equal Core's PanelLift for 7 pictures x 4 dims in
both themes; the 62 shared v2.3 vectors with a dim in 0.20..0.90 come through exactly (13 with a layer); check off =
film 0 and sep 0; white in dark, #0A0C0E in light, 0..0.16 in 0.02 steps.

## Not seen
Never seen over a real dark or bright wallpaper: test runs have no desktop still (the islands show the fallback), so the
ui stage does not exercise a non-zero layer.
