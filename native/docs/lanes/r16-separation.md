# Lane r16-separation (2026-11-26)

Branch `native/r16-separation`, off native/stage13 c2bfad28. EM ticket t_48dd9f. The owner approved "separation" for the
islands look: an island stands out from the picture under it.

Contract: look-sync b96576b (the Engineering Manager's branch, on top of 6e7e729), CONTRACT.md "2026-11-26: v2.3:
separation", FROZEN at proto/designs 7bfde5f (apps-r3/SEPARATION.md).

## What is in Core

`Mantle.Core/Look/Levitate/Legibility.cs`, next to the v2.1 `LiftAlpha` / `PanelLift` port:

- `Separate`: look-sync's `SEPARATE` (target 1.30, splitDark 0.039, splitLight 0.55, step 0.02, max 0.16, the dark lift
  white, the light shade #0A0C0E). The film keeps LIFT's step 0.04, cap 0.72 and text targets 0.066 / 0.386.
- `Legibility.Separation(samples, dim, theme)` gives `SeparationResult { Alpha, Sep, SepRgb, LP, LI, S }`, one to one
  with look-sync's `separation()`:
  - P is the median sample (index n/2, darkest first, after the dim), and the guard is the check's own pick.
  - Dark: a white lift where the film is 0 and L(P) <= 0.039, else more film.
  - Light: more lift where it already lifted or L(P) <= 0.55, else a #0A0C0E shade.
  - The lift or shade stops one step before the guard sample would cross the text target. If it stops short of 1.30,
    the film goes on instead when that separates more (the fallback).
  - Every step is rounded to 2 decimals as look-sync does (floor(v * 100 + 0.5) / 100). Sorts break ties by index,
    because JS's sort is stable and C#'s is not.
  - No usable sample gives 0 / 0 with LP, LI and S null.
- `Legibility.PanelLift(look, theme, samples, separate = false)` is the opt-in:
  - `separate: true`: alpha comes from `Separation`, and the answer's `Sep` / `SepRgb` are set. With the check off they
    are Sep 0 and the theme's colour.
  - Without it: the v2.1 answer exactly, with `Sep` and `SepRgb` null. Existing callers and their record equality are
    unchanged.

Nothing App-side changes: the Windows Desktop Engineer wires the layer into Levitate in its own lane.

## Vectors

`native/tests/Mantle.Core.Tests/Look/separation-v23.json` is look-sync's `test/fixtures/separation-v23.json` at
b96576b, byte for byte (git blob 5ee6017f, LF in the index like accent-v2.json). It holds 70 vectors (36 dark, 34
light). `Look/SeparationV23Tests` checks:

- all 70 vectors: alpha and sep exact, S within 1e-6, SepRgb per theme, and S = ratio(LI, LP);
- the 8 worked examples of SEPARATION.md, including the finished colour;
- the guard rule (the lift never pushes the guard past 0.066);
- direction first: an island already darkened never gets the white lift;
- the PanelLift opt-in shape: off equals v2.1 field for field, and dimAuto false gives 0 / 0;
- bad input never throws.

A node mirror of this port's algorithm (index tie-breaks, the same rounding) passed 70 of 70 vectors before the C# run.

2026-11-26 update (EM t_d8c327): ported look-sync f4356d6 (SEPARATION.md 6051417) instead of b96576b: the second
fallback (d), the lift / shade on top of the .72 film when the film ends at its cap still short (its best step), and the
overall highest S (a tie keeps the earlier). separation-v23.json is now f4356d6's rev 2, 76 vectors (the 70 earlier
unchanged), git blob 2b016ca6. b96576b's "already darkened never gets the white lift" test is replaced by look-sync's
second-fallback cases (5 dark, 1 light).
