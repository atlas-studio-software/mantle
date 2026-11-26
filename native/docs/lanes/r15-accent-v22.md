# Lane r15-accent-v22 (2026-11-26)

Branch `native/r15-accent-v22`, off native/stage12 2d867bf5. Ports look-sync accent v2.2 (look-sync 6e7e729 on c29ba47,
approved by the owner) into `Mantle.Core/Look/Accent.cs`, the r14-ci-look port.

- islands LIGHT is LIGHT.md's deep teal: acc #083f49, strong #05313a, on #f4f7f8 (on is no longer white).
- Light base hue 189 (#083f49's own hue, 189.23, rounded): `Accent.BaseHue("islands", "light")` = 189. Dark islands
  (185) and steel (212) are unchanged. `IslandsHue` stays 185 (the dark base, as accent.js ISLANDS_HUE).
- The per-theme rule (CONTRACT v2.2) is a caller rule, written in Accent's doc comment: pass "islands" only in a theme
  whose islands look is built, and steel (or nothing) in the other. LookHost passes no profile, so nothing visible changes;
  the Windows Desktop Engineer wires the bar onto the profile in its own lane.
- `Look/accent-v2.json` is look-sync's test/fixtures/accent-v2.json at 6e7e729, rev 2, byte for byte (git blob cb1a0669),
  136 vectors (hues 188, 189 and 190 added).
- AccentV2Tests: all 136 vectors; islands light at 189 is the deep teal exactly and 189 is its own hue; 188 and 190 move at
  most 1 step (acc, on) or 2 (strong); an app without a light look passes steel in light (#4d6784). The old test that pinned
  islands light = steel light (#4d7f84 at 185) is replaced.

Not verified here: any painted surface (nothing calls the islands profile yet).
