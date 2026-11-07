# Mantle theme tokens: the design system a XAML ResourceDictionary must reproduce

Baseline: origin/master 6ca499f (2026-11-06). Lane native-plan-inv-shell-tokens.
Primary source: `ui/shared/glass-light.css` (the one theme: "island", light + dark). Also `ui/shared/tokens.css`
(legacy, two names still read), `lib/look-host.js`, `lib/look-sync.js`, `lib/theme.js`, `lib/accent.js`,
`preload.js` (the switch), and the shared consumers in `ui/shared/` (wall-bdrop, glass-adapt, hud-backdrop,
bar-follow, motion-in, unfold, unfold-live, tips, empty-state), plus the bar's material rules in
`ui/bar/bar.css` / `ui/bar/bar.js` and the shared switch in `ui/settings/settings.css` where the stack is defined.

Conventions in this file:
- Values in backticks are copied verbatim from the CSS/JS. "XAML" columns are the same colour as `#AARRGGBB`
  (alpha = round(a x 255)), with `var()` resolved inside the same theme. Blank = not a single colour
  (a shadow, a curve, a font stack, a number).
- Trust the VALUES, not the prose comments: several comments are stale (see 1.4).
- The switch is `<html data-theme="dark">`; absent or `"light"` = the light set (section 6).

---

## 1. Every custom property in glass-light.css, light and dark

Scope column:
- `both`: defined in `:root` (light) AND re-valued in `:root[data-theme="dark"]`.
- `root-only`: defined only in `:root`; dark INHERITS the light value unchanged (all of them are motion).
- `DARK-ONLY`: defined only in the dark block; in light the name is UNDEFINED (see 1.3 for what consumers do).

The note column is the declaration's own inline comment (the dark one when both exist), trimmed.

### 1.1 Table

| Token | Light value (`:root`) | Dark value (`[data-theme="dark"]`) | XAML light | XAML dark | Scope | Inline note |
|---|---|---|---|---|---|---|
| `--lg-fill` | `rgba(255,255,255,.45)` | `rgba(19,20,21,.83)` | #73FFFFFF | #D4131415 | both | Helm content panel: wash (9,10,11,.50) + panel (22,23,24,.66) |
| `--lg-fill-fb` | `rgba(255,255,255,.62)` | `var(--lg-fill)` | #9EFFFFFF | #D4131415 | both | only when the picture failed (was .86) |
| `--lg-fill-strong` | `rgba(255,255,255,.72)` | `var(--lg-fill)` | #B8FFFFFF | #D4131415 | both | popovers carry LISTS: they get a real surface |
| `--lg-fill-pop` | `rgba(255,255,255,.88)` | `var(--lg-fill)` | #E0FFFFFF | #D4131415 | both | the guard's CEILING and the no-picture face (was .88) |
| `--lg-edge-in` | `rgba(255,255,255,.62)` | `rgba(255,255,255,.07)` | #9EFFFFFF | #12FFFFFF | both | Helm panel lip |
| `--lg-edge-out` | `rgba(11,14,26,.10)` | `rgba(255,255,255,.09)` | #1A0B0E1A | #17FFFFFF | both | Helm --hairline: a faint WHITE line, never black |
| `--lg-shadow` | `0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22), 0 26px 56px -28px rgba(14,18,38,.30)` | `0 4px 12px rgba(0,0,0,.34), 0 24px 64px rgba(0,0,0,.42)` |  |  | both |  |
| `--lg-shadow-up` | `0 -1px 1px rgba(14,18,38,.05), 0 -10px 28px -12px rgba(14,18,38,.22), 0 -26px 56px -28px rgba(14,18,38,.30)` | `0 -4px 12px rgba(0,0,0,.34), 0 -24px 64px rgba(0,0,0,.42)` |  |  | both |  |
| `--lg-lip-up` | `inset 0 1px 0 rgba(255,255,255,.62)` | `inset 0 1px 0 rgba(255,255,255,.07)` |  |  | both |  |
| `--lg-lip-up-0` | `inset 0 1px 0 rgba(255,255,255,0)` | `inset 0 1px 0 rgba(255,255,255,0)` |  |  | both |  |
| `--lg-lip-dn` | `inset 0 -1px 0 rgba(255,255,255,.62)` | `inset 0 -1px 0 rgba(255,255,255,.07)` |  |  | both |  |
| `--lg-lip-dn-0` | `inset 0 -1px 0 rgba(255,255,255,0)` | `inset 0 -1px 0 rgba(255,255,255,0)` |  |  | both |  |
| `--lg-drop-up` | `0 -1px 1px rgba(14,18,38,.05), 0 -10px 28px -12px rgba(14,18,38,.22), 0 -26px 56px -28px rgba(14,18,38,.30)` | `0 -2px 6px rgba(0,0,0,.30), 0 -12px 32px rgba(0,0,0,.32)` |  |  | both |  |
| `--lg-drop-up-0` | `0 -1px 1px rgba(14,18,38,0),   0 -10px 28px -12px rgba(14,18,38,0),   0 -26px 56px -28px rgba(14,18,38,0)` | `0 -2px 6px rgba(0,0,0,0),   0 -12px 32px rgba(0,0,0,0)` |  |  | both |  |
| `--lg-drop-dn` | `0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22), 0 26px 56px -28px rgba(14,18,38,.30)` | `0 2px 6px rgba(0,0,0,.30), 0 12px 32px rgba(0,0,0,.32)` |  |  | both |  |
| `--lg-drop-dn-0` | `0 1px 1px rgba(14,18,38,0),   0 10px 28px -12px rgba(14,18,38,0),   0 26px 56px -28px rgba(14,18,38,0)` | `0 2px 6px rgba(0,0,0,0),   0 12px 32px rgba(0,0,0,0)` |  |  | both |  |
| `--lg-fill-0` | `rgba(255,255,255,0)` | `rgba(19,20,21,0)` | #00FFFFFF | #00131415 | both |  |
| `--lg-edge-0` | `rgba(11,14,26,0)` | `rgba(255,255,255,0)` | #000B0E1A | #00FFFFFF | both |  |
| `--lg-ink-0` | `rgba(51,58,78,0)` | `rgba(169,170,167,0)` | #00333A4E | #00A9AAA7 | both |  |
| `--lg-ink` | `#0B0E1A` | `#ececea` | #FF0B0E1A | #FFECECEA | both | the readout: the clock, an app's letter tile |
| `--lg-ink-2` | `#333A4E` | `#a9aaa7` | #FF333A4E | #FFA9AAA7 | both | secondary readouts, glyphs, the running dot |
| `--lg-ink-3` | `#5B6478` | `#75767a` | #FF5B6478 | #FF75767A | both | quiet detail; never the only carrier of meaning |
| `--lg-acc` | `#4436E8` | `#9fb4cc` | #FF4436E8 | #FF9FB4CC | both | Helm steel blue; state only, never on a row |
| `--lg-acc-on` | `#FFFFFF` | `#11161c` | #FFFFFFFF | #FF11161C | both |  |
| `--lg-acc-soft` | `rgba(68,54,232,.12)` | `rgba(159,180,204,.14)` | #1F4436E8 | #249FB4CC | both |  |
| `--lg-focus` | `inset 0 0 0 1.5px var(--lg-acc), inset 0 0 0 3px rgba(255,255,255,.9)` | `inset 0 0 0 1.5px rgba(159,180,204,.95), inset 0 0 0 3px rgba(20,21,22,.8)` | `inset 0 0 0 1.5px #4436E8, inset 0 0 0 3px rgba(255,255,255,.9)` |  | both |  |
| `--m-spring` | `linear(...41 points, see 4.1)` | (inherits light) |  |  | root-only |  |
| `--m-accel` | `cubic-bezier(.3, 0, .8, .15)` | (inherits light) |  |  | root-only |  |
| `--m-fade-in` | `cubic-bezier(.2, 0, 0, 1)` | (inherits light) |  |  | root-only |  |
| `--m-fade-out` | `cubic-bezier(.4, 0, 1, 1)` | (inherits light) |  |  | root-only |  |
| `--m-dur-xs` | `240ms` | (inherits light) |  |  | root-only | tooltips, chips, small menus |
| `--m-dur-s` | `320ms` | (inherits light) |  |  | root-only | pop-ups, popovers, flyouts |
| `--m-dur-m` | `440ms` | (inherits light) |  |  | root-only | panels, sheets, dialogs |
| `--m-dur-l` | `560ms` | (inherits light) |  |  | root-only | the bar, docks and other whole-edge surfaces |
| `--lg-hover` | `rgba(11,14,26,.06)` | `rgba(40,41,43,.62)` | #0F0B0E1A | #9E28292B | both | Helm row hover = surface-2 |
| `--lg-press` | `rgba(11,14,26,.10)` | `rgba(62,63,66,.62)` | #1A0B0E1A | #9E3E3F42 | both | Helm row selected / menu item active = surface-3 |
| `--lg-tile` | `rgba(11,14,26,.08)` | `var(--lg-card)` | #140B0E1A | #9E28292B | both | the letter tile behind an app with no icon |
| `--lg-sunk` | `rgba(11,14,26,.05)` | `var(--lg-field)` | #0D0B0E1A | #750A0B0C | both | a chip: a recess, the reference quiet-button fill |
| `--lg-card` | `rgba(255,255,255,.70)` | `rgba(40,41,43,.62)` | #B3FFFFFF | #9E28292B | both | Helm surface-2: cards inside a panel, row hover |
| `--lg-field` | `rgba(11,14,26,.05)` | `rgba(10,11,12,.46)` | #0D0B0E1A | #750A0B0C | both | Helm --code-bg: inputs, search fields |
| `--lg-scrim` | `rgba(30,31,32,.22)` | `rgba(6,7,8,.48)` | #381E1F20 | #7A060708 | both | Helm --scrim: behind a modal dialog |
| `--lg-edge-strong` | `rgba(11,14,26,.18)` | `rgba(255,255,255,.16)` | #2E0B0E1A | #29FFFFFF | both |  |
| `--lg-edge-inner` | `rgba(255,255,255,.62)` | `rgba(255,255,255,.05)` | #9EFFFFFF | #0DFFFFFF | both |  |
| `--lg-divider` | `rgba(11,14,26,.10)` | `rgba(255,255,255,.09)` | #1A0B0E1A | #17FFFFFF | both |  |
| `--lg-acc-strong` | `#3628D6` | `#b7c8db` | #FF3628D6 | #FFB7C8DB | both |  |
| `--lg-acc-line` | `rgba(68,54,232,.40)` | `rgba(159,180,204,.42)` | #664436E8 | #6B9FB4CC | both |  |
| `--lg-acc-select` | `rgba(68,54,232,.22)` | `rgba(159,180,204,.32)` | #384436E8 | #529FB4CC | both |  |
| `--lg-scroll` | `rgba(11,14,26,.20)` | `rgba(255,255,255,.14)` | #330B0E1A | #24FFFFFF | both |  |
| `--lg-scroll-hover` | `rgba(11,14,26,.34)` | `rgba(255,255,255,.24)` | #570B0E1A | #3DFFFFFF | both |  |
| `--lg-disabled` | `.45` | `.45` |  |  | both |  |
| `--lg-dur-ui` | `120ms` | `120ms` |  |  | both |  |
| `--lg-dur-ui-slow` | `200ms` | `200ms` |  |  | both |  |
| `--lg-ease-ui` | `cubic-bezier(.2,0,0,1)` | `cubic-bezier(.2,0,0,1)` |  |  | both |  |
| `--elev-lip` | `inset 0 1px 0 var(--lg-edge-in)` | `inset 0 1px 0 var(--lg-edge-in)` | `inset 0 1px 0 rgba(255,255,255,.62)` | `inset 0 1px 0 rgba(255,255,255,.07)` | both |  |
| `--elev-raised` | `var(--elev-lip), 0 1px 1px rgba(14,18,38,.05), 0 2px 8px rgba(14,18,38,.06)` | `var(--elev-lip), 0 1px 2px rgba(0,0,0,.28), 0 2px 8px rgba(0,0,0,.16)` | `inset 0 1px 0 rgba(255,255,255,.62), 0 1px 1px rgba(14,18,38,.05), 0 2px 8px rgba(14,18,38,.06)` | `inset 0 1px 0 rgba(255,255,255,.07), 0 1px 2px rgba(0,0,0,.28), 0 2px 8px rgba(0,0,0,.16)` | both |  |
| `--elev-panel` | `var(--elev-lip), 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22)` | `var(--elev-lip), 0 2px 6px rgba(0,0,0,.30), 0 12px 32px rgba(0,0,0,.32)` | `inset 0 1px 0 rgba(255,255,255,.62), 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22)` | `inset 0 1px 0 rgba(255,255,255,.07), 0 2px 6px rgba(0,0,0,.30), 0 12px 32px rgba(0,0,0,.32)` | both |  |
| `--elev-tip` | `var(--elev-lip), 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22)` | `var(--elev-lip), 0 2px 6px rgba(0,0,0,.30), 0 12px 32px rgba(0,0,0,.32)` | `inset 0 1px 0 rgba(255,255,255,.62), 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22)` | `inset 0 1px 0 rgba(255,255,255,.07), 0 2px 6px rgba(0,0,0,.30), 0 12px 32px rgba(0,0,0,.32)` | both |  |
| `--elev-float` | `var(--elev-lip), var(--lg-shadow)` | `var(--elev-lip), var(--lg-shadow)` | `inset 0 1px 0 rgba(255,255,255,.62), 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22), 0 26px 56px -28px rgba(14,18,38,.30)` | `inset 0 1px 0 rgba(255,255,255,.07), 0 4px 12px rgba(0,0,0,.34), 0 24px 64px rgba(0,0,0,.42)` | both |  |
| `--shadow-ctl` | `0 1px 2px rgba(14,18,38,.14)` | `0 1px 2px rgba(0,0,0,.26)` |  |  | both |  |
| `--shadow-ctl-tight` | `0 1px 1px rgba(14,18,38,.14)` | `0 1px 1px rgba(0,0,0,.22)` |  |  | both |  |
| `--shadow-knob` | `0 0 0 .5px rgba(14,18,38,.10), 0 1px 3px rgba(14,18,38,.20)` | `0 1px 3px rgba(0,0,0,.36)` |  |  | both |  |
| `--lg-dot-ring` | `rgba(255,255,255,.7)` | `rgba(0,0,0,.55)` | #B3FFFFFF | #8C000000 | both | UNUSED since the dot became a line (which needs no ring); kept for any round pip a later surface adds |
| `--lg-warn` | `#8A4B00` | `#d6b874` | #FF8A4B00 | #FFD6B874 | both |  |
| `--lg-warn-soft` | `rgba(138,75,0,.10)` | `rgba(214,184,116,.14)` | #1A8A4B00 | #24D6B874 | both |  |
| `--lg-r` | `8px` | `10px` |  |  | both |  |
| `--lg-r-pill` | `24px` | `18px` |  |  | both | the bar island's ends: Helm's largest radius |
| `--lg-r-ctl` | `6px` | `6px` |  |  | both | buttons, chips, icon cells |
| `--lg-r-pop` | `16px` | `14px` |  |  | both | popovers: the quick-settings and calendar panels |
| `--lg-dur` | `var(--m-dur-l)` | (inherits light) | `560ms` | `560ms` | root-only |  |
| `--lg-dur-out` | `260ms` | (inherits light) |  |  | root-only |  |
| `--lg-ease` | `cubic-bezier(.22,.68,.30,1)` | (inherits light) |  |  | root-only |  |
| `--lg-ground` | `#F4F6FA` | `#131415` | #FFF4F6FA | #FF131415 | both |  |
| `--lg-blur` | `blur(12px) saturate(150%)` | `blur(5px)` |  |  | both | the user's Helm blur; no saturate, no dim |
| `--lg-fill-bar` | (undefined) | `rgba(9,10,11,.50)` |  | #80090A0B | DARK-ONLY | Helm rail / top strip: NO fill of their own (helm ui/shell/shell.css .rail/.strip), so only the .50 wash. Both bars. |
| `--lg-guard-bar` | (undefined) | `4.2` |  |  | DARK-ONLY |  |
| `--lg-ink-on-light` | (undefined) | `#0B0E1A` |  | #FF0B0E1A | DARK-ONLY |  |
| `--lg-ink-2-on-light` | (undefined) | `#333A4E` |  | #FF333A4E | DARK-ONLY |  |
| `--lg-ink-3-on-light` | (undefined) | `#5B6478` |  | #FF5B6478 | DARK-ONLY |  |
| `--lg-hover-on-light` | (undefined) | `rgba(11,14,26,.06)` |  | #0F0B0E1A | DARK-ONLY |  |
| `--lg-press-on-light` | (undefined) | `rgba(11,14,26,.10)` |  | #1A0B0E1A | DARK-ONLY |  |
| `--lg-warn-on-light` | (undefined) | `#8A4B00` |  | #FF8A4B00 | DARK-ONLY |  |
| `--lg-acc-on-light` | (undefined) | `#4436E8` |  | #FF4436E8 | DARK-ONLY |  |
| `--lg-lift` | (undefined) | `#FFFFFF` |  | #FFFFFFFF | DARK-ONLY |  |
| `--lg-ok` | (undefined) | `#8fbf9b` |  | #FF8FBF9B | DARK-ONLY |  |
| `--lg-danger` | (undefined) | `#e0877c` |  | #FFE0877C | DARK-ONLY |  |
| `--lg-r-xl` | (undefined) | `18px` |  |  | DARK-ONLY |  |
| `--lg-r-chip` | (undefined) | `999px` |  |  | DARK-ONLY |  |
| `--lg-font` | (undefined) | `"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif` |  |  | DARK-ONLY |  |
| `--lg-font-display` | (undefined) | `"Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif` |  |  | DARK-ONLY |  |
| `--lg-mono` | (undefined) | `"Cascadia Code", "Cascadia Mono", Consolas, monospace` |  |  | DARK-ONLY |  |

### 1.2 Derived composites worth precomputing

| What | Expression | Resolved |
|---|---|---|
| Light tooltip face (opaque) | `--lg-ground` under `linear-gradient(var(--lg-fill-strong), ...)` = rgba(255,255,255,.72) over #F4F6FA | `#FFFCFCFE` (252,252,254) |
| Dark settings / files / gallery wall tint | `linear-gradient(var(--lg-fill-bar) 0 40px, var(--lg-fill) 40px)` | 40 px header band `#80090A0B`, then `#D4131415` below |
| Dark task view wall tint | `--wall-tint: var(--lg-fill)` | `#D4131415` |
| Dark task manager wall tint | `--wall-tint: transparent` | `#00000000` |
| Unfold moving-half shade end colour | `color-mix(in srgb, var(--unfold-shade) 55%, transparent)` with shade #000000 | `rgba(0,0,0,.55)` = `#8C000000` |

### 1.3 Dark-only names in light (what a light consumer actually gets)

In light these 17 names are undefined, so a `var()` WITHOUT a fallback is the CSS "guaranteed-invalid" value and
the property falls back to its initial/inherited value; WITH a fallback, the fallback is the light value, and the
fallbacks differ per consumer. A native dictionary must either define a light value per name or reproduce each
consumer's fallback. Observed consumers (counts across `ui/**`):

| Name | Light behaviour seen in consumers |
|---|---|
| `--lg-font` | `var(--lg-font, sans-serif)` x5, `var(--lg-font, 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif)` x5, bare x4 (inherits page font). The bar's own body font is `'Segoe UI Variable Text', 'Segoe UI Variable', 'Segoe UI', system-ui, sans-serif` at 12.5px (ui/bar/bar.css) |
| `--lg-font-display` | fallback `"Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif` x6, `"Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif` x3, `sans-serif` x2 |
| `--lg-mono` | `var(--lg-mono, Consolas, monospace)` x2, `var(--lg-mono, "Cascadia Mono", Consolas, monospace)` x1 |
| `--lg-r-xl` | bare x10 (most inside dark-scoped rules; unscoped ones in ui/player/player.css `.main`, `.cover .art`, `.dropzone`, `.dbox` get radius 0 in light), `var(--lg-r-xl, 12px)` x3, `var(--lg-r-xl, 20px)` x1 |
| `--lg-r-chip` | bare x10 (most inside dark-scoped rules, e.g. ui/bar/corners.css `.hcchip`, ui/clipboard/clipboard.css `.chip`; the unscoped ui/player/player.css `.chip` gets radius 0 in light), `999px` x1, `6px` x1 |
| `--lg-ok` | `var(--lg-ok, var(--lg-acc))` x2, `var(--lg-ok, #2e7d4f)` x1 |
| `--lg-danger` | `var(--lg-danger, #b3261e)` x5, `var(--lg-danger, #c4314b)` x2, bare x5 (mostly dark-scoped, e.g. screenrec `--rec`; the unscoped ui/player/player.css `.wb.close:hover` gets no background in light) |
| `--lg-fill-bar` | 11 consumers, all inside `:root[data-theme="dark"]` rules (e.g. ui/topbar/topbar.css `body.glass #bar { background-color: var(--tb-tint, var(--lg-fill-bar)) }`) |
| `--lg-guard-bar` | read by JS only (glass-adapt.js guardTokens): light has none, so the guard's default need 4.7 applies |
| `--lg-*-on-light`, `--lg-lift` | read by ui/topbar/topbar.css `.ink-dk` and topbar.js / under.js (dark top bar over a bright window) |

Also referenced but defined nowhere: `--lg-text` (ui/settings/sections/personalization.js swatch outline, always
falls back to `currentColor`).

### 1.4 Stale prose to ignore (value wins)
- Comments say dark `--lg-fill` is ".50" (glass-adapt.js header, hud-backdrop.js) and `--lg-fill-bar` is ".75"
  (bar.css, glass-adapt.js guardTokens). The live values are `.83` and `.50`.
- bar.css says dark `--lg-blur` "also dims it a fifth"; the live dark value is `blur(5px)` (no saturate, no dim).
- unfold-live.css says `--ul-blur-min-big` default 4; unfold-live.js `RMIN_BIG = 6` is what runs.
- `--lg-dot-ring`, `--elev-panel`, `--elev-tip`, `--elev-float`, `--lg-shadow-up`, `--shadow-ctl-tight`,
  `--lg-fill-0`, `--lg-ink-0` are defined but no `var()` reads them today (keep them: look-sync carries the names).
- glass-adapt.js `adaptFor` / `--bd-adapt` (brightness-dimming the picture) has no consumer left; the only live
  readability mechanism is the tint guard (2.6).

### 1.5 Non-token global rules in glass-light.css

| Rule | Light | Dark |
|---|---|---|
| `color-scheme` | not set (light) | `color-scheme: dark;` (native form controls, default scrollbars) |
| `::selection` | none (system highlight) | `background: var(--lg-acc-select)` = `#529FB4CC`, zero specificity (`:where`) |
| `::-webkit-scrollbar` | none global (each page's own, see 5.4) | `width: 10px; height: 10px` |
| `::-webkit-scrollbar-track` | none | `background: transparent` |
| `::-webkit-scrollbar-thumb` | none | `background: var(--lg-scroll); border: 3px solid transparent; background-clip: padding-box; border-radius: 10px;` (visible thumb 4 px wide, `#24FFFFFF`) |
| `::-webkit-scrollbar-thumb:hover` | none | `background-color: var(--lg-scroll-hover)` = `#3DFFFFFF` |
| `:where(:focus-visible)` | `outline: 2px solid var(--lg-acc); outline-offset: -2px;` (both themes, zero specificity: the default ring for anything with no rule of its own) | same, with the dark accent |
| `@keyframes m-fade` | `from { opacity: 0; }` (the shared pop-in fade, listed LAST after a surface's rise keyframe) | same |

### 1.6 tokens.css (legacy dark "instrument glass"): only where still used

Linked by `ui/bar/index.html`, `ui/desktop/index.html`, `ui/launcher/index.html`, `ui/wallpapers/index.html`
(always BEFORE glass-light.css). `ui/switcher/index.html` links glass-light.css only. Of its 54 declarations,
only two are read anywhere outside tokens.css itself (grep of every `var(--name)` across `ui/**`):

| Token | Value | Consumer |
|---|---|---|
| `--ui-font` | `'Bahnschrift', 'Segoe UI Variable', 'Segoe UI', sans-serif` | ui/desktop/deskmenu.css `font-family` (the desktop right-click menu) |
| `--icon-font` | `'Segoe Fluent Icons', 'Segoe MDL2 Assets', sans-serif` | ui/desktop/deskmenu.css `.dm-ic` (16px wide, 14px glyph, colour `--lg-ink-2`) |

Everything else in tokens.css (`--ink-*`, `--fg*`, `--glass*`, `--ion*`, `--amber*`, `--sheen`, `--pane-glass*`,
`--aurora-*`, `--bevel*`, `--rim*`, `--pip-*`, `--titlebar-h` 40px, `--nav-h` 44px, `--readout-stretch` 87.5%, ...)
has no consumer: do not port it. The comments in index.html say "for fonts and geometry only".

---

## 2. The material stack

CSS `backdrop-filter` can never reach the desktop (a transparent Electron window samples only its own page),
and DWM acrylic / blur-behind were both rejected live (acrylic = "near-opaque white slab", blur-behind over a dark
desktop = "black slab"). So every glass surface paints its OWN picture of what is behind it, blurred, then ONE tint
layer with the hairlines, then content. `backdrop-filter: var(--lg-blur)` survives only where the picture is
already IN the page under the element: the dark top bar over its own `#bdrop` (ui/topbar/topbar.css), desktop
panes over the in-page wallpaper (`body.has-wall .pane`, ui/desktop/desktop.css), dark Files / Task Manager menus
over the wall-bdrop canvas (ui/files/files.css, ui/taskmgr/taskmgr.css), and the screenshot overlay over its
frozen shot (ui/screenshot/overlay.css). Natively those are all "blur of my own picture" too. The fill "is a breath, not a pane": the pane's shape is carried by
the two hairlines and the shadow, never by opacity. No `text-shadow` anywhere.

### 2.1 Layer order (the bar, ui/bar/bar.css; the model every surface copies)

| z | Element | What it paints |
|---|---|---|
| (box) | `.bar` | `box-shadow: var(--pane-drop)` (cast outside), `transform: translateY(var(--pane-y)) scale(var(--pane-s))`, `opacity: var(--pane-o)`, `color: var(--pane-ink)` |
| 0 | `.bdrop` (`position:absolute; inset:0; border-radius:inherit; overflow:hidden; opacity:0`, `.on` = 1, `transition: opacity 150ms var(--lg-ease)`; no transition while `body:not(.up)`) | the clip for the picture |
| 0 | `.bdrop img` | the captured picture, `filter: var(--lg-blur)`, `object-fit: fill`, overscanned (2.3) and counter-transformed (2.4) |
| 1 | `.bar::after` (`inset:0; border: 1px solid var(--pane-edge); border-radius: inherit; background-color: var(--pane-fill); box-shadow: var(--pane-lip); transition: background-color 150ms var(--lg-ease)`) | the tint + outer hairline + inner lip. It is a pseudo-element because a child `<img>` paints above its parent's background |
| 2 | `.left/.dock/.right` | content |

Pane variables per state:

| Var | Light | Dark |
|---|---|---|
| `--pane-fill` | `var(--lg-fill)` (.45 white) | `var(--bar-tint, var(--lg-fill-bar))` (.50 #090A0B, or the guard's thicker alpha) |
| `--pane-fill` with `body.no-backdrop` | `var(--lg-fill-fb)` (.62 white) | `var(--lg-fill-fb)` (= .83 #131415) |
| `--pane-edge` | `var(--lg-edge-out)` | `var(--lg-edge-out)`; docked (`body.edge`): `var(--lg-edge-0)` (no line) |
| `--pane-lip` | `var(--lg-lip-up)`; top-docked `var(--lg-lip-dn)` | same; docked: `var(--lg-lip-up-0)` / `var(--lg-lip-dn-0)` (no lip on the side facing windows) |
| `--pane-drop` | `var(--lg-drop-up)`; top-docked `var(--lg-drop-dn)` | `var(--lg-drop-up-0)` / `var(--lg-drop-dn-0)`: NO drop shadow in dark (the window ends 1 DIP past the pill, so the drop was cut into a hard dark band) |
| `--pane-ink` | `var(--lg-ink-2)` | `var(--lg-ink-2)` |

Shapes: docked bottom `border-radius: var(--lg-r) var(--lg-r) 0 0`, `::after border-width: 1px 1px 0`; docked top
`0 0 var(--lg-r) var(--lg-r)`, `border-width: 0 1px 1px`; island (`body.pill`) `border-radius: var(--lg-r-pill)`
(light 24px, dark 18px), `border-width: 1px`. Both the picture and the tint are clipped to these radii.

### 2.2 The picture, per surface family

| Surface | Light | Dark |
|---|---|---|
| Bottom bar | main sends a PNG of the SCREEN strip behind the bar about 4x a second while up (never during the morph, never hidden), a 4x downscale of the bar rect, stretched back (`object-fit: fill`), `filter: blur(12px) saturate(150%)` | the WALLPAPER still (lib/wall-still.js), cut 16 DIP past the pill on every side, sent with `at` (fractions of `win`, DIP), `filter: blur(5px)` |
| Bar pop-ups, top bar dropdowns | no picture: `--lg-fill-strong` (.72) lists / `--lg-fill-pop` (.88) cards | one wallpaper cut per open, blurred, under `--lg-fill` (.83) + guard |
| HUDs (volume, clipboard, notes, screenshot card, on-top, layouts, timer, snap): hud-backdrop.js | nothing (own light face) | one protected shot per show (`mantle.call('hud.backdrop', {key})` -> `<key>.backdrop {png,w,h,at,win,ms}`), baked (2.5) |
| App windows (Files, Task Manager, Settings, Task View, gallery): wall-bdrop.js | nothing (light keeps its own face; DWM is not used) | the desktop wallpaper still, baked once onto a canvas at z -1 (2.4) |
| Tooltips (tips.css) | opaque: `--lg-ground` + `--lg-fill-strong` = `#FFFCFCFE` | one layer of `--lg-fill` (.83), nothing blurred |

The no-picture fallback is always a THICKER tint of the same colour: light `--lg-fill-fb` .62 (bar) /
`--lg-fill-pop` .88 (cards); dark `--lg-fill-fb` = `--lg-fill-pop` = `--lg-fill` .83 (Helm's panel; at .83 over pure
white `--lg-ink-2` still reads 4.74:1 and `--lg-ink` 9.35:1, so dark needs no separate fallback). `body.no-backdrop`
is ON at boot, OFF from the first strip, and back ON the moment the bar hides (a stale photograph must never flash on
the next reveal). A picture that arrives while the island is down shows at once; one that arrives late, with the
island up, fades in over 150ms `--lg-ease`.

### 2.3 Guard band (overscan) so the blur never pulls transparency in at the rim

| Where | Mechanism | Numbers |
|---|---|---|
| Bar, light (screen strip, no `at`) | the `<img>` is larger than the clip on every side | `left:-14px; top:-14px; width: calc(100% + 28px); height: calc(100% + 28px)` |
| Bar, dark (wallpaper cut with `at`) | the cut itself is 16 DIP past the pill on every side, laid exactly over the screen it came from (bar.js placeBdrop overrides left/top/width/height) | 16 DIP |
| App windows (wall-bdrop.js bake) | the still's edge pixels are smeared out into a margin before blurring (top/bottom rows, left/right columns, four corner pixels), then the canvas is drawn at `-m,-m` | `m = ceil(blurCanvasPx x 3)` |
| Readability model (glass-adapt.js) | gaussian with MIRRORED edges; a transparent pixel counts as white (worst case) | kernel radius `ceil(sigma x 3)` |

### 2.4 See-through alignment (the picture stays still on the screen)

Bar (bar.js `placeBdrop` + `barRestBox`):
```
W,H   = window.innerWidth/innerHeight;  ww,wh = at.win.w/h (else W,H)
ax = at.x*ww; ay = at.y*wh; aw = at.w*ww; ah = at.h*wh
if edge != top:  ay = H - (wh - ay)          // the page is pinned to the bottom edge
box = the bar's RESTING layout rect: from getBoundingClientRect minus its transform
      (L = r.left - m.e - ox*(1 - m.a); T = r.top - m.f - oy*(1 - m.d), about transform-origin ox,oy)
img.left = ax - box.left; img.top = ay - box.top; img.width = aw; img.height = ah
img.transform-origin = (box.ox - left)px (box.oy - top)px
```
Re-placed on the reveal's `transitionend` and on window resize. While the island rises the picture is held still
by the exact inverse of `.bar`'s transform on the same clock and curve:
`transform: translateZ(0) scale(calc(1 / var(--pane-s))) translateY(calc(-1 * var(--pane-y)))`,
`transition: transform var(--dur) var(--bar-move)`, origin `50% calc(100% - 14px)` (top-docked `50% 14px`).
The blur is never animated. Test: bar.page.test.js 'reveal glass' fails on 1 px of glass with no picture under it.

App windows (wall-bdrop.js, dark only):
- Input `<feature>.wall` = `{png, w, h, display:{x,y,width,height} (DIP), source, key}`; `<feature>.wallpos` =
  `{x, y, t}` = the window's content origin in DIP, pushed on every move/resize.
- Bake once per still: canvas `w x h` px (p.w/p.h else the image's natural size), CSS size `display.width x
  display.height` px; filter = the `--lg-blur` token with its px scaled by `w / display.width` (canvas px).
- `#wall-bdrop` is `position: fixed; inset: 0; z-index: -1; overflow: hidden; contain: strict`; `.wall-pic` at
  `left:0; top:0; transform-origin: 0 0; will-change: transform`, moved by
  `translate(round(display.x - pos.x)px, round(display.y - pos.y)px)`: one transform write per push, no paint.
  Until main says where, pos = `window.screenX/screenY`.
- Over it ONE `.wall-tint` (`inset:0; background: var(--wall-tint, var(--lg-fill))`). `html.wall-on` stands the
  root on the OPAQUE `--lg-ground` (#131415) so where the window runs off the display the ground, not a hole,
  shows under the tint, and clears `body` (`!important`).
- States: `idle` (light) / `wait` ("waiting for the wallpaper picture") / `on` / `off` (+ reason, fired as the
  `mantle-wall` event). Light drops the picture at once.

### 2.5 HUD bake (hud-backdrop.js / .css, dark only)
- `SCALE = 0.5` (the bake is half the pane's CSS size), `TIMEOUT = 1500` ms (an unanswered request is a failure),
  `HOLD = 150` ms (`{hold}`: the pane stays `opacity: 0 !important` via `.hud-hold` until baked, at most this long).
- `where = {x: at.x*W, y: at.y*H, w: at.w*W, h: at.h*H}`; canvas `round(rect.width*0.5) x round(rect.height*0.5)`,
  filter = `--lg-blur` px x 0.5, drawn at `((where.x - rect.left)*0.5, (where.y - rect.top)*0.5)`, PNG data URL.
- Face: `background-color: transparent; background-image: linear-gradient(var(--hud-tint, var(--lg-fill)),
  var(--hud-tint, var(--lg-fill))), var(--hud-bd-img); background-size: auto, 100% 100%` (origin/clip border-box);
  the pane keeps its own hairlines and shadow. `--hud-tint` is set only when the guard engages.

### 2.6 The readability guard (glass-adapt.js; dark surfaces with a picture)
- Model: `ground = picture*(1 - a) + tint*a` in sRGB (Chromium compositing); contrast = WCAG
  `(Lhi + .05)/(Llo + .05)` with the sRGB linearisation `c<=0.04045 ? c/12.92 : ((c+.055)/1.055)^2.4`,
  `L = .2126R + .7152G + .0722B`.
- The picture is modelled as drawn: gaussian `sigma = blurPx x (picture px per CSS px)`, mirrored edges, then the
  Filter Effects saturate matrix `[.213+.787s, .715-.715s, .072-.072s; .213-.213s, .715+.285s, .072-.072s;
  .213-.213s, .715-.715s, .072+.928s]`, then brightness, each clamped 0..255.
- `GUARD = { need: 4.7, step: 0.02, hold: 4 }`. Ink = `--lg-ink-2`. Candidates = the 48 brightest pixels.
  The alpha walks from floor (`--lg-fill`'s alpha; bars: `--lg-fill-bar`'s) up by .02 to ceiling
  (`max(floor, --lg-fill-pop alpha)`) and stops at the first that clears `need`; else ceiling with `short: true`.
- Bars use `need = --lg-guard-bar` (dark 4.2) and floor `--lg-fill-bar`; everything else 4.7 / `--lg-fill`. In dark
  ceiling = fill (.83), so a pop-up's guard can only say whether .83 holds, never thicken it; the bars (.50 floor)
  can thicken up to .83.
- Hysteresis for a moving picture (`glassSettle`): thicker at once; thinner only by >= .10 at once, or >= .04 held
  for 4 pictures. Output colour `rgba(r,g,b,alpha)` of the floor colour -> `--bar-tint` / `--hud-tint` /
  `--pop-tint` / `--dd-tint` / `--tb-tint`.
- Runs once per picture that arrives (bar: 4 fps max, a 360x12 PNG), never on a timer.

---

## 3. Geometry, type, shadows, hairlines

### 3.1 Radii

| Token | Light | Dark | Use |
|---|---|---|---|
| `--lg-r` | 8px | 10px | rows, menus, tips, docked bar's two corners |
| `--lg-r-ctl` | 6px | 6px | buttons, chips, icon cells, inputs |
| `--lg-r-pop` | 16px | 14px | popovers / pop-ups (quick settings, calendar) |
| `--lg-r-pill` | 24px | 18px | the island bar's ends; switch track |
| `--lg-r-xl` | (undefined) | 18px | palette / modal |
| `--lg-r-chip` | (undefined) | 999px | Helm pill chips |
| literal | 12px | 12px | empty-state tile |
| literal | 4px | 4px | Settings segmented button inner |

### 3.2 Spacing
There is NO spacing scale token. Spacing is per-surface literals. The shared ones: bar `--bar-h: 48px`,
`--cell: 36px`, `--cell-icon: 24px`, `--cell-gap: 6px`, `--gap: 4px`, `--lift: 14px`; tooltip padding
`6px 9px 7px`, max-width 320px; empty-state tile 40x40, 12px below it; switch 32x18.

### 3.3 Type

| Item | Value |
|---|---|
| Body face (dark `--lg-font`; light pages' literal / fallback) | `"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif` |
| Display (`--lg-font-display`) | `"Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif` |
| Mono (`--lg-mono`) | `"Cascadia Code", "Cascadia Mono", Consolas, monospace` |
| Legacy desktop menu (`--ui-font`) | `'Bahnschrift', 'Segoe UI Variable', 'Segoe UI', sans-serif` |
| Icons (`--icon-font`) | `'Segoe Fluent Icons', 'Segoe MDL2 Assets', sans-serif` |
| Bar body | 12.5px, `-webkit-font-smoothing: antialiased` |
| Tooltip | light 12.5px / note 11.5px; dark 13px / note 12px; weight 400; line-height 1.35; `white-space: pre-line` |
| Size scale in use (count of declarations across ui/**) | 12px x154, 12.5px x90, 13px x83, 11.5px x71, 11px x62, 14px x26, 10px x10, 10.5px x8, 15px x7, 13.5px x6, 16px x5, 20px x3, 18px x2, 9px x1 |
| Weights in use | 600 x149, 500 x41, 400 x14, 650 x1, 300 x1 |
| Line-heights in use | 1.4 x37, 1.45 x25, 1.35 x25, 1.5 x20, 16px x14, 1.3 x13, 18px x9, 1 x7 |
| Numerals | `font-variant-numeric: tabular-nums` x73 |

### 3.4 Shadows and where dark drops them
All values are in the 1.1 table. Recipes:
- `--lg-shadow` (popovers, floats downward) / `--lg-shadow-up` (bottom-docked, thrown upward: same layers, y negated).
- `--lg-drop-up` / `--lg-drop-dn` = the bar's drop (a separate value from the lip because the lip lives on the
  tint layer and the drop on the box). Every `-0` twin repeats the stack layer for layer at alpha 0, same order,
  same inset-ness: a box-shadow only interpolates against a list of the same shape; a mismatch snaps.
- Elevation: `--elev-lip` = `inset 0 1px 0 var(--lg-edge-in)`; `--elev-raised` (card, empty-state tile),
  `--elev-panel` (pane on the desktop), `--elev-tip` (tooltip, pill over a scroller), `--elev-float` (= lip +
  `--lg-shadow`: pop-ups, menus, dialogs). Controls: `--shadow-ctl`, `--shadow-ctl-tight` (parts 1 px apart),
  `--shadow-knob`. Light is ink-tinted (14,18,38) at about half the dark strength.
- DARK DROPS THE OUTER SHADOW wherever the window cuts it (the "clipped-shadow" rule, ui/shared/clipped-shadow.test.js):
  the bar (`--lg-drop-*-0`), dark tooltips (`box-shadow: var(--lg-lip-up)` only), and any surface whose window ends
  just past its edge keeps the lip only. Light keeps its shadows.

### 3.5 Hairlines and lips
- Outer hairline `--lg-edge-out`: light `rgba(11,14,26,.10)` (ink, NOT black .35: that read as a drawn box);
  dark `rgba(255,255,255,.09)` (faint WHITE, never black).
- Inner lip `--lg-edge-in` (1 px inset at the top edge): light `.62` white, dark `.07` white. As a box-shadow:
  `--lg-lip-up` (top edge) / `--lg-lip-dn` (bottom edge, top-docked bar).
- `--lg-edge-strong` (hover edge of fields/buttons, dark switch track), `--lg-edge-inner` (button inner lip),
  `--lg-divider` (every line).
- Dark docked bar: no edge and no lip on the side facing the windows.

---

## 4. Motion

### 4.1 Shared motion set (glass-light.css `:root`, identical in light and dark; same names in Helm and Quartz)

| Token | Value | Role |
|---|---|---|
| `--m-spring` | `linear(0, 0.0208, 0.0739, 0.1478, 0.2337, 0.3245, 0.4153, 0.5025, 0.5838, 0.6577, 0.7235, 0.7808, 0.8301, 0.8716, 0.906, 0.9341, 0.9566, 0.9742, 0.9876, 0.9977, 1.0049, 1.0098, 1.0128, 1.0145, 1.0151, 1.015, 1.0143, 1.0132, 1.012, 1.0106, 1.0092, 1.0079, 1.0066, 1.0055, 1.0044, 1.0035, 1.0028, 1.0021, 1.0015, 1.0011, 1)` | IN for every pop-in: a damped spring (ratio 0.8), 41 points at even normalised time (point i at t = i/40); 90 % in at 35 % of the time, 1.5 % overshoot (peak 1.0151 at t = .60). Same points over a shorter duration = the same spring, quicker |
| `--m-accel` | `cubic-bezier(.3, 0, .8, .15)` | OUT: a plain accelerate at about 45 % of the IN duration |
| `--m-fade-in` | `cubic-bezier(.2, 0, 0, 1)` | opacity over the first 40 % of the IN |
| `--m-fade-out` | `cubic-bezier(.4, 0, 1, 1)` | opacity with the exit |
| `--m-dur-xs` | `240ms` | tooltips, chips, small menus, switch knob |
| `--m-dur-s` | `320ms` | pop-ups, popovers, flyouts |
| `--m-dur-m` | `440ms` | panels, sheets, dialogs |
| `--m-dur-l` | `560ms` | the bar, docks, whole-edge surfaces |
| `--lg-dur` | `var(--m-dur-l)` | the bar's reveal; overwritten per event by bar.js with the duration main animates the WINDOW with |
| `--lg-dur-out` | `260ms` | the bar's hide (main drops the window ~350 ms after the event; 260 leaves 90 ms margin) |
| `--lg-ease` | `cubic-bezier(.22,.68,.30,1)` | the reference site's nav detach; also bar tint/backdrop fades (150ms) |
| `--lg-dur-ui` | `120ms` | UI state: hover, press, colour cross-fades |
| `--lg-dur-ui-slow` | `200ms` | slower UI state |
| `--lg-ease-ui` | `cubic-bezier(.2,0,0,1)` | UI state curve |

Only transform and opacity ever move in a pop-in. Reduced motion (Windows Animation effects off =
`prefers-reduced-motion: reduce`): opacity only, 120 ms, in and out.

### 4.2 The bar's reveal (ui/bar/bar.css)
- `body { --dur: var(--lg-dur-out, 260ms) }`, `body.up { --dur: var(--lg-dur, var(--m-dur-l)) }`.
- Down: `--bar-move: var(--m-accel); --bar-fade: var(--m-fade-out); --bar-fade-k: 1`. Up: `--m-spring`,
  `--m-fade-in`, `--bar-fade-k: .4`. `transition: transform var(--dur) var(--bar-move), opacity
  calc(var(--dur) * var(--bar-fade-k)) var(--bar-fade)`.
- Down pose: `--pane-y: calc(var(--bar-h) * .75)` (36px), `--pane-s: .94`, `--pane-o: 0`; docked: y = 48px, s = 1;
  top-docked: y = -36px / -48px, `transform-origin: 50% 0`. Up: y 0, s 1, o 1.
- Reduced motion: no y, no scale, `--dur: 120ms`, fade `linear`, k 1.
- `body.pre` (before the first event): no transitions at all, so the first reveal is a detach.
- Hover/active fades on the bar are `steps(2)` (two paints, not a ramp: Electron CPU).

### 4.3 bar-follow.css (surfaces that glide with the bar)
`--lift: 0px; --glide-ms: 240ms; transform: translateY(calc(-1 * var(--lift)))`; down `transition: transform
var(--glide-ms) var(--m-accel, cubic-bezier(.3, 0, .8, .15))`; `.glide-up` on `--m-spring` (same 41 points as
fallback); `.still` none; reduced motion none. `--lift` and `--glide-ms` come from main (lib/bar-follow.js).

### 4.4 motion-in.js
Adds `html.m-shown` on the NEXT animation frame after `visibilitychange` -> visible (the first paint must be the
"from" pose); removes it at once when hidden (snap, free). `MantleMotionIn.replay()` = remove, reflow, add.

### 4.5 unfold.css (".unfold": the iPhone-Duo fold, one version, no light/dark variant, no tint)

| Token | Value |
|---|---|
| `--unfold-in` | `var(--m-spring, cubic-bezier(.2, .9, .25, 1))` |
| `--unfold-out` | `var(--m-accel, cubic-bezier(.3, 0, .8, .15))` |
| `--unfold-full-ms` / `-blur` / `-stretch` | `1100ms` / `28px` / `1` (unlock, wake, look-back) |
| `--unfold-pop-ms` / `-blur` / `-stretch` | `340ms` / `8px` / `1` (launcher, task view: the default) |
| `--unfold-quick-ms` / `-blur` / `-stretch` | `240ms` / `6px` / `1` (maximize / restore) |
| `--unfold-hinge` | `cubic-bezier(.2, .1, .05, .4)` (full-screen WebGL page only) |
| `--unfold-edge-blur` | `48px` (WebGL; px of a 1920-wide picture) |
| `--unfold-edge-dark` | `1` |
| `--unfold-fold-ms` | `420ms` (open -> blurred) |
| `--unfold-cover-blur` | `30px` |
| `--unfold-reveal-ms` | `200ms` (blurred -> open) |
| `--unfold-tint` | `#4436E8` (name only; unused by the look) |
| `--unfold-shade` | `#000000` |
| `--unfold-dark` | `.66` (leaf dimming at its free edge) |
| `--unfold-dark-cover` | `0` |
| `--unfold-dark-blur` | `.24` |
| `--unfold-dim` | `.86` (`brightness()` while folded or blurred) |
| `--unfold-sheen` / `-w` | `rgba(255, 255, 255, 0)` / `0px` (unused) |

States: `folded` = `clip-path: inset(0 0 0 50%); filter: blur(var(--u-blur)) brightness(var(--unfold-dim));
transform: scaleX(var(--u-stretch)); opacity: 0`, on `--unfold-out`, opacity delayed `calc(var(--u-ms) * .6)`
(fade is the LAST 40 %). `open` = `inset(0 0 0 0)`, `blur(0) brightness(1)`, none, 1. `blurred` = `--u-ms:
--unfold-fold-ms`, `inset(0 0 0 0)`, `blur(var(--unfold-cover-blur)) brightness(var(--unfold-dim))`, opacity 1.
Transitions: clip-path, filter, transform over `--u-ms` on `--unfold-in`; opacity over `--u-ms * .4`.
Shade layers (`.unfold-cast`, width 50 %, z 2147483000, transform + opacity only): moving half `-m` (left,
origin 100% 50%, `linear-gradient(90deg, var(--unfold-shade), color-mix(in srgb, var(--unfold-shade) 55%,
transparent))`): folded scaleX(0) opacity .66, open scaleX(1) 0, blurred scaleX(1) .24; cover half `-s` (right,
flat black): folded 0, open 0, blurred `.24 * .7`. `.unfold-snap`: no transitions. Reduced motion: `transition:
opacity 120ms linear`, no clip, no transform, folded no filter, no shade layers.

### 4.6 unfold-live.css / unfold-live.js (".unfold-live": the Duo fold on LIVE DOM, launcher + Task View)

| Token / constant | Value |
|---|---|
| `--ul-blur-ratio` | `.033` (free-edge radius mid-motion, share of pane width) |
| `--ul-blur-cap` | `72` (px) |
| `--ul-bezel` | `1px` (frame hairline left out of the blur, top/bottom/free edge) |
| `--ul-ease-in` | `cubic-bezier(.4, 0, .1, 1)` (the leaf; NOT `--m-spring`) |
| `--ul-ease-out` | `cubic-bezier(.9, 0, .6, 1)` (the opening run backwards) |
| JS `BANDS` / `BANDS_BIG` / `BIG_W` | `[0.04, 0.15, 0.3, 0.45, 0.62]` / `[0.04, 0.25, 0.62]` / `1000` px |
| JS `RELEASE` / `LAND` / `PAD` | `0.15` / `0.55` / `2.5` |
| JS `RMIN` / `RMIN_BIG` | `0.5` / `6` px |
| JS reduced motion | `RM_MS = 120` (opacity only, linear) |
| Leaf shade | `linear-gradient(to right, rgb(0 0 0 / 0) 0%, rgb(0 0 0 / .08) 7%, rgb(0 0 0 / .12) 20%, rgb(0 0 0 / .15) 34%, rgb(0 0 0 / .18) 48%, rgb(0 0 0 / .2) 62%, rgb(0 0 0 / .32) 76%, rgb(0 0 0 / .57) 89%, rgb(0 0 0 / .6) 100%)` |

Mechanism: the hinge is the centre; the LEFT half stays, the RIGHT half swings open (opposite side to unfold.css);
progressive blur = stacked strips of `backdrop-filter`, each fading in from the previous level, masks set per frame;
blur and dim linger and release together over the last 15 %. At rest `.ul-fx` is `display: none`: zero cost.
Duration comes from unfold.css `--unfold-<size>-ms`. `folded` = `opacity: 0; pointer-events: none`.

---

## 5. Component states

### 5.1 Focus ring
- `--lg-focus` (use: `box-shadow: var(--lg-focus); outline: none`), drawn INSIDE the control's box so packed rows,
  tabs and buttons never paint over a neighbour (tools/overlap-audit.js fails any ring that reaches another control):
  light `inset 0 0 0 1.5px var(--lg-acc), inset 0 0 0 3px rgba(255,255,255,.9)` (1.5 px #4436E8, then 1.5 px
  `#E6FFFFFF` inside it); dark `inset 0 0 0 1.5px rgba(159,180,204,.95), inset 0 0 0 3px rgba(20,21,22,.8)`.
- Default ring for anything without its own rule: `outline: 2px solid var(--lg-acc); outline-offset: -2px`.
- Look-sync accent hue set (dark only) REPLACES `--lg-focus` with an OUTSET ring:
  `0 0 0 1px rgba(20,21,22,.9), 0 0 0 3px rgba(R,G,B,.55)` (see 6.2). This contradicts the inside-the-box rule; it
  is what the code does today.
- Fields (dark Settings): focus = `border-color: var(--lg-acc-line)` + `box-shadow: var(--lg-focus)`.

### 5.2 Hover / pressed / disabled

| State | Light | Dark |
|---|---|---|
| Hover | `--lg-hover` rgba(11,14,26,.06) `#0F0B0E1A` (ink at low alpha, never white: a white wash on white glass is invisible) | `--lg-hover` rgba(40,41,43,.62) `#9E28292B` (Helm row hover = surface-2) |
| Pressed / selected | `--lg-press` rgba(11,14,26,.10) `#1A0B0E1A` | `--lg-press` rgba(62,63,66,.62) `#9E3E3F42` (Helm surface-3); selected rows add `inset 0 0 0 1px var(--lg-edge-out)` |
| Letter tile / chip recess | `--lg-tile` .08 ink, `--lg-sunk` .05 ink | `--lg-tile` = `--lg-card`, `--lg-sunk` = `--lg-field` |
| Disabled | `--lg-disabled: .45` (opacity); literal exceptions: Settings `.btn:disabled .5`, `.sw:disabled .4`, `.ibtn:disabled .45`; `.pending` .6 | same |
| Dark buttons (Settings) | light: `.btn:hover --lg-tile`, `:active --lg-press`, `.acc` = `--lg-acc` / `--lg-acc-on` | `border 1px --lg-edge-out; background --lg-card; box-shadow inset 0 1px 0 --lg-edge-inner; radius --lg-r-ctl; weight 500`; hover `--lg-press` + `--lg-edge-strong`; active back to `--lg-card`; `.acc` hover `--lg-acc-strong` |
| Timing | bar: `steps(2)`; elsewhere `--lg-dur-ui` 120ms `--lg-ease-ui` | same |

### 5.3 Accent use
`--lg-acc` marks the active window and focus, nowhere else (dark: "state only, never on a row"). White on light
accent is 7.2:1. Warning ink `--lg-warn` light `#8A4B00` (4.5:1 on the pane; tokens.css `--amber` #e0a059 is ~2:1).

### 5.4 Scrollbars
Dark: the global rule in 1.5 (10 px, 3 px transparent border, thumb `--lg-scroll` .14 white, hover .24, radius 10).
Light: no global rule; pages style their own, e.g. Settings `.scroller` 8 px wide, thumb `--lg-tile`, radius 4, 2 px
transparent border, padding-box; `.nav` 6 px, thumb transparent until the nav is hovered, then `--lg-tile`,
radius 3; no scrollbar buttons. The light `--lg-scroll` (.20 ink) / `--lg-scroll-hover` (.34 ink) exist for pages
that opt in.

### 5.5 Selection
Dark `::selection { background: var(--lg-acc-select) }` (rgba(159,180,204,.32)); light: system default.
`--lg-acc-select` light rgba(68,54,232,.22) is used by pages for selected rows/rings, not text selection.

### 5.6 Switch (one switch across Mantle; ui/settings/settings.css `.sw`, tested by ui/shared/switches.test.js)
- Track 32 x 18, `border: 1px solid`, `border-radius: var(--lg-r-pill)`, transitions background-color and
  border-color over `--lg-dur-ui` `--lg-ease-ui`.
- Knob `::after`: 12 x 12 circle at `top: 2px; left: 2px` (3 px from the outer edge), `box-shadow:
  var(--shadow-knob)`; on = `transform: translateX(14px)`; knob travels on `--m-spring` over `--m-dur-xs` (240ms),
  its colour over `--lg-dur-ui`. Reduced motion: the knob does not animate its travel.

| Part | Light | Dark |
|---|---|---|
| Track off | border `--lg-ink-3` (#5B6478), background transparent; hover `--lg-hover` | border `--lg-edge-strong`, background `--lg-press`; hover `--lg-press` |
| Track on (`aria-checked="true"`) | background + border `--lg-acc` | background `--lg-acc`, border transparent |
| Knob off / on | `--lg-ink-2` / `--lg-acc-on` | same tokens (#a9aaa7 / #11161c) |
| Disabled / pending | opacity .4 / .6 | same |
| Label `.sw-l` | min-width 24px, right-aligned, 12.5px, `--lg-ink-2` | same |

Segmented control (`.seg`): inline-flex, gap 2px, padding 2px, radius `--lg-r-ctl`, background `--lg-sunk`; buttons
radius 4px, padding `5px 11px`, 12.5px, `--lg-ink-2`; dark selected = `--lg-press` + inset 1px `--lg-edge-out`.

### 5.7 Tooltip panel (tips.css `.mtip`)

| Property | Light | Dark |
|---|---|---|
| Position | `position: fixed`, `z-index: 2147483000`, never takes pointer/focus (`pointer-events: none; user-select: none`) | same |
| Box | `max-width: 320px` (tips.js lowers it to fit), `padding: 6px 9px 7px`, `border: 1px solid var(--lg-edge-out)`, `border-radius: var(--lg-r)` (8px) | same box, radius 10px |
| Face | OPAQUE: `background-color: var(--lg-ground)` + `background-image: linear-gradient(var(--lg-fill-strong), var(--lg-fill-strong))` = `#FFFCFCFE` | `background-color: transparent; background-image: linear-gradient(var(--lg-fill), var(--lg-fill))` (.83) |
| Shadow | `inset 0 1px 0 var(--lg-edge-in), var(--lg-shadow)` | `var(--lg-lip-up)` only (the bar window cuts outer shadows) |
| Text | `--lg-ink`, 12.5px, 400, line-height 1.35, `white-space: pre-line`, `overflow-wrap: anywhere` | 13px |
| Note line `.mtip-note` | block, margin-top 4px, `--lg-warn`, 11.5px (says when the room above the bar could not be opened) | 12px |
| Show / hide | `opacity 0 -> 1` over `100ms steps(2)`; `visibility` flips after the fade (`0s linear 100ms`) on hide, at once on show; `.now` = no transition (hand-off between icons) | same |

### 5.8 Empty state (empty-state.css `.es-tile`)
40 x 40 box, `margin: 0 auto 12px` (12 px above the title), `border: 1px solid var(--lg-edge-out)`,
`border-radius: 12px`, `background: var(--lg-card)`, `box-shadow: var(--elev-raised)`, `color: var(--lg-ink-2)`,
centred flex. Icon: 18 x 18 svg, `fill: none; stroke: currentColor; stroke-width: 1.3; stroke-linecap: round;
stroke-linejoin: round` (a 16-unit glyph at 18 px = ~1.46 px stroke). Only the tile is shared; title and body keep
each surface's own classes. No animation, no backdrop-filter. Same tokens in both themes.

---

## 6. How look.json and the theme switch map onto the tokens

### 6.1 Light / dark switch
- `lib/theme.js`: `mode` in `<userData>/theme.json` `{ "mode": ... }`, one of `system` (default, "Match Windows"),
  `light`, `dark`. `resolve(mode, windows)`: light/dark as picked; `system` = Windows APP mode (HKCU
  `...\Themes\Personalize AppsUseLightTheme`, via Electron nativeTheme `updated`); Windows unknown = light (and
  says so). `--theme=dark|light|system` pins it for a `--test` instance only.
- `preload.js`: synchronous IPC `mantle:theme-now` before first paint -> `<html data-theme="dark|light">` plus the
  look overrides stamped INLINE on `<html>` (`style.setProperty`). If `<html>` does not exist yet, a
  MutationObserver stamps it the moment the parser inserts it (before any frame). Live changes arrive as the
  `theme` event: attribute set, overrides set/removed (only names it stamped itself are ever removed; a key must
  match `^--lg-[a-z0-9-]+$`, string < 200 chars), then `mantle-theme` `{detail:{theme}}` fires on window so each
  surface re-bakes its glass (wall-bdrop, hud-backdrop, tips.js, bar/top bar guards).
- No theme-level transition and no speed target in code: the swap is one attribute write; only properties that
  already carry their own transition animate (e.g. the bar tint `background-color 150ms --lg-ease`, switch colours
  120ms). Surfaces with pictures drop them on a flip to light and wait for main to push a new still on a flip to dark.

### 6.2 look.json -> overrides (lib/look-host.js `vars(theme)`)
Shared file `%APPDATA%\look-sync\look.json` (or `LOOK_SYNC_DIR`); Mantle's local copy `<userData>/look-sync.json`
`{ lookSync, accent, glass }` is used while sync is OFF. Seed (lib/look-sync.js `SEED`):
`theme: 'system'`, `accent: { hue: null }`, `glass: { dark: { blurPx: 5, dim: 0.50, panel: 0 }, light: { blurPx: 12,
dim: 0.45, panel: 0 }, render: 'picture' }`. Validation `RANGES = { blurPx: [0, 64], dim: [0.20, 0.90], panel:
[-0.30, 0.15] }`; hue null or integer 0..360; `render` 'picture' | 'seeThrough' (validated, NOT read by Mantle).
`vars()` is EMPTY at the seed values, so the default look is the stylesheet's own value for value.

```
a2(n)        = String(Math.round(n * 100) / 100)          // 2-decimal formatting
PANEL_CLAMP  = [0.30, 0.97]

DARK (theme === 'dark'), g = glass.dark:
  blurPx != 5   -> --lg-blur     = `blur(${a2(blurPx)}px)`
  dim    != .50 -> --lg-fill-bar = `rgba(9,10,11,${a2(dim)})`        (the bars' guard may still raise it)
  panel  != 0   -> for each of  --lg-fill [19,20,21,.83]  --lg-fill-pop [19,20,21,.83]
                                --lg-card [40,41,43,.62]  --lg-field    [10,11,12,.46]
                   = `rgba(r,g,b,${a2(clamp(alpha + panel, .30, .97))})`
  hue integer   -> t = accent.tokens(hue, 'dark'):
                   --lg-acc = t.acc, --lg-acc-strong = t.strong, --lg-acc-on = t.on,
                   --lg-acc-soft = t.soft, --lg-acc-line = t.line, --lg-acc-select = t.select,
                   --lg-focus = `0 0 0 1px rgba(20,21,22,.9), 0 0 0 3px rgba(${t.rgb}, .55)`   (outset)

LIGHT (theme === 'light'), g = glass.light:
  blurPx != 12  -> --lg-blur = `blur(${a2(blurPx)}px) saturate(150%)`
  dim    != .45 -> --lg-fill = `rgba(255,255,255,${a2(dim)})`   (light bars and panes are ONE --lg-fill; the guard floors at it)
  panel  != 0   -> --lg-fill-strong [255,255,255,.72], --lg-fill-pop [255,255,255,.88]
                   = `rgba(255,255,255,${a2(clamp(alpha + panel, .30, .97))})`
  hue           -> NOT applied in light (Mantle keeps its indigo #4436E8; unifying it is the user's call)
  light --lg-card / --lg-field are NOT panel-shifted.
```
Note `--lg-fill-fb` is never overridden: the dark no-picture face stays .83 even when `panel` moves `--lg-fill`
(it is `var(--lg-fill)` in the stylesheet, so in dark it DOES follow the inline override through the var()).

### 6.3 Accent hue -> colours (lib/accent.js, vendored from look-sync; HSL, not oklch)
A custom hue keeps the EXACT saturation and lightness of the authored steel-blue hexes; only H changes:

| Token | Dark source hex | S, L (exact) | Light source hex | S, L |
|---|---|---|---|---|
| acc | `#9fb4cc` | 0.3061, 0.7118 | `#4d6784` | 0.2632, 0.4098 |
| strong | `#b7c8db` | 0.3333, 0.7882 | `#3c536d` | 0.2899, 0.3314 |
| on | `#11161c` | 0.2444, 0.0882 | (white `#ffffff`) | - |

Derived alphas of `acc`: dark `soft .14, line .42, select .32, glow .22`; light `soft .11, line .40, select .20,
glow .22`. Output format `rgba(r, g, b, a)` (with spaces). `hslToRgb` = standard HSL sextant formula, each channel
`Math.round((v + m) * 255)`; hue taken mod 360. `DEFAULT_HUE = 212` reproduces the stylesheet's dark accent exactly
(`#9fb4cc` / `#b7c8db` / `#11161c`). Examples, dark: hue 0 -> acc `#cc9f9f`, strong `#dbb7b7`, on `#1c1111`;
hue 120 -> `#9fcc9f` / `#b7dbb7` / `#111c11`; hue 265 -> `#b29fcc` / `#c6b7db` / `#16111c`. `glow` is computed but
no Mantle token takes it. Light tokens are computable (hue 212 -> `#4d6784`) but Mantle never applies them.

### 6.4 Other inputs that write tokens at runtime
| Writer | Token | Value |
|---|---|---|
| bar.js | `--lg-dur` | the duration main animates the bar window with, per event |
| bar.js / top bar / pop-ups / dropdowns / HUDs (guard) | `--bar-tint`, `--tb-tint`, `--pop-tint`, `--dd-tint`, `--hud-tint`, `--pane-tint` | `rgba(floor colour, guard alpha)` (2.6) |
| hud-backdrop.js | `--hud-bd-img` | `url("data:image/png...")` |
| main via bar-follow.js | `--lift`, `--glide-ms` | px, ms |
| pages (dark) | `--wall-tint` | see 1.2 |

### 6.5 Not covered here
- Per-surface tokens outside ui/shared (bar `--cell*`, top bar `--tb-*`, pop-up `--pop-bd-*`, Settings layout):
  only the ones the material stack needs are quoted. Per-surface type and spacing belong to each surface's parity file.
- lib/look-guard.js (the shared top-band alpha when look sync is on) and ui/topbar/under.js (the text-band scrim
  maths over a bright window): named, not specified.
- The JS logic of tips.js, unfold.js, unfold-live.js (strip/mask maths per frame), and bar pop-up picture placement:
  only their CSS and named constants are here.
- Light consumers' fallbacks were counted by grep, not checked rule by rule beyond the examples given.

---

## 7. XAML mapping notes

| CSS construct | Where | Why WinUI 3 has no direct equivalent | What to build instead |
|---|---|---|---|
| `filter: var(--lg-blur)` on a captured PICTURE (`blur(12px) saturate(150%)` light, `blur(5px)` dark) | bar `.bdrop img`, wall-bdrop canvas, hud bake, pop-ups | XAML has no filter on Image; `AcrylicBrush` / `DesktopAcrylicController` blur the real backdrop with fixed tint/noise (the exact DWM look the user rejected) | Win2D / Composition `CompositionEffectBrush`: `GaussianBlurEffect` (CSS blur(Npx) is a standard deviation of N CSS px = N DIP; set BlurAmount accordingly and verify the Win2D sigma convention) over a `CompositionSurfaceBrush` of the PNG, `BorderMode.Hard` to stand in for the overscan/edge clamp. `SaturationEffect` only goes 0..1, so saturate(150%) needs a `ColorMatrixEffect` with the Filter Effects matrix in 2.6 (s = 1.5). Or bake once on the CPU like wall-bdrop/hud do |
| Picture alignment (translate by display origin minus window origin, counter-transform during the reveal) | 2.4 | none built in | a Visual offset driven from window-move events; during the reveal an ExpressionAnimation inverse of the pane's transform |
| Readability guard | glass-adapt.js | none | port the maths 1:1 to C# (pure functions; existing tests give the expected numbers), feeding a mutable SolidColorBrush |
| `color-mix(in srgb, #000 55%, transparent)` | unfold.css | no colour functions in XAML | precomputed `#8C000000` |
| `oklch()` | nowhere in scope (accent uses HSL) | - | none needed |
| `linear(...)` 41-point spring | `--m-spring`, bar reveal, bar-follow, switch knob | XAML easings are fixed shapes; no piecewise-linear easing | Composition `KeyFrameAnimation` with 41 `InsertKeyFrame(i/40, value, linearEasing)` frames (exact), or `SpringVector3NaturalMotionAnimation` DampingRatio 0.8 tuned to the curve (approximate) |
| `cubic-bezier(...)` | all other curves | - | `CompositionCubicBezierEasingFunction` / `KeySpline` (exact) |
| `steps(2)` | bar hovers, tooltips | - | `DiscreteDoubleKeyFrame` at 0 and 50 % |
| Multi-layer `box-shadow` with negative spread (`0 10px 28px -12px ...`) | `--lg-shadow`, `--lg-drop-*`, `--elev-*` | `ThemeShadow` is not tunable; Composition `DropShadow` has no spread and is one layer | one `DropShadow` SpriteVisual per layer, spread emulated by insetting the shadow mask; `-0` twins become an opacity animation of each layer |
| Clipped-shadow rule | dark bar, dark tips | - | keep it: dark draws lip only wherever the window ends just past the pane |
| Inset `box-shadow` lips (`inset 0 1px 0 ...`) | `--lg-lip-*`, `--elev-lip`, button inner lip | no inset shadow | a 1 px top (or bottom) `Border` inside the rounded clip, or a `LinearGradientBrush` border with a hard stop |
| Inset focus ring (`inset 0 0 0 1.5px acc, inset 0 0 0 3px white`) | `--lg-focus` | system focus visuals draw outside by default | two nested Borders (1.5 px accent, then 1.5 px white/dark inside) or `FocusVisualPrimary/SecondaryBrush` + thickness 1.5 with a margin that keeps it inside; the dark accent-override ring is outset (5.1) |
| `background-clip: padding-box` thumb with transparent border | dark scrollbar | ScrollBar template differs | retemplate the ScrollBar thumb: 4 px visible inside a 10 px lane, radius per 1.5 |
| `clip-path: inset(...)` | unfold | - | `InsetClip` on the Visual (animatable) |
| `filter: blur() brightness()` on LIVE content | unfold `folded`/`blurred` | no element filter | `CompositionVisualSurface` of the element into an effect brush (costly) or drop the blur and keep dim + clip |
| stacked `backdrop-filter` strips (progressive blur) | unfold-live | no per-element backdrop blur in XAML | `Compositor.CreateBackdropBrush()` + GaussianBlurEffect on stacked SpriteVisuals with gradient masks (it samples the window's own visuals, which is what this effect needs); candidate to simplify |
| `var()` fallbacks for dark-only names | 1.3 | ResourceDictionary keys need a value in every theme | define a light value per dark-only name, matching what each light consumer shows today (radius 0 where there is no fallback) |
| Inline overrides from look.json | 6.2 | `ThemeDictionaries` are static | keep brushes as mutable resources and update `SolidColorBrush.Color` in place, then raise one "re-bake" event (the `mantle-theme` equivalent) |
| Font stacks with `system-ui` / `sans-serif` | 3.3 | XAML FontFamily takes comma lists of real family names only | `"Segoe UI Variable Text, Segoe UI"`; `tabular-nums` -> `Typography.NumeralAlignment="Tabular"`; unitless line-height -> `LineHeight = size x factor` with `LineStackingStrategy="BlockLineHeight"` |
| `color-scheme: dark` | dark root | - | `RequestedTheme="Dark"` on the root so stock controls follow |

Count basis: glass-light.css declarations per block. 62 are re-valued in both blocks, 11 root-only motion tokens are
inherited by dark, and 17 are dark-only, so 90 names resolve in dark and 73 in light. unfold/unfold-live tokens
(4.5, 4.6) and the 2 live tokens.css names (1.6) are not counted.

Tokens: 73 light / 79 dark
