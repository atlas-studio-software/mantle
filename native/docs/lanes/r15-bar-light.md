# Lane r15-bar-light: the light look of the bar's islands and its levitated pop-ups

Branch native/r15-bar-light, base 2d867bf5 (native/stage12). Follows lane r14-bar-review (the light lift, the LvBar* inks).
Spec: apps-r3/LIGHT.md section 4 (with 4.1 and 4.2). Only the islands look (BarGeometry.Islands, every run but
MANTLE_TEST_GLASS=electron); the electron face keeps Tokens.xaml's Lg* inks and the steel accent. Dark does not change.

## 1. One theme-dependent token set (Theme/Levitate.xaml)

The LvBar* ThemeDictionaries block is now the whole islands set (light = LIGHT.md 4; every Dark value is what the
islands showed before, so dark is the same):

| key | dark | light |
|---|---|---|
| LvBarInk / 2 / 3 / 4 | #EEF6F7, rgba(230,242,244,.76 / .54 / .34) | #0F1E21, #0F1E21 at .76 / .56 / .36 |
| LvBarWarn, LvBarOk, LvBarDown | #E3C47E, #8FE3B4, #FF9D95 | #4C3300, #124028, #6F1813 |
| LvBarFillHover / On / Strong / Pressed | white .07 / .12 / .18 / .05 | black .05 / .08 / .12 / .04 |
| LvBarHair | white .09 | black .08 |
| LvBarSheen / Big (hover only) | white .07 / .05 | white .16 / .12 |
| LvBarRun (the dock's run dot) | LgInk2 #A9AAA7 (kept) | text-3, #0F1E21 at .56 |
| LvBarTile / TileInk, LvBarSunk | LgTile / LgInk, LgSunk (kept) | black .08 / #0F1E21, black .05 |
| LvBarAccent / Hover / Ink | #A9E7ED / #C4F0F4 / #052A2F (Core's mirror) | #083F49 / #05313A / #F4F7F8 |

Not drawn: the island text shadow (light 0 1px 1px rgba(255,255,255,.28)). Native draws no text shadows on the islands in
either theme (LevitateTokens.IslandText is not used by the App), so there is nothing to switch. Bare text on the picture
keeps its light ink (the islands have none).

## 2. Where they are laid

- `Shell/Levitate/IslandsInk.cs` (new): reads an LvBar* key for a theme (nested merged dictionaries, as
  BarWindow.ThemeToken does) and gives the islands accent.
- The bar (`Surfaces/Bar/Frame/BarWindow.Islands.cs` PaintIslandInks): hover and pressed fills (through Root's LgHover /
  LgPress), the clock's time and date, the status island's separator and the dock's (shared `_isHair`), the dock's run
  dot, letter tile and letter, "+N", the pip (IslandPipInk), the focus underline. The app logs the set once per theme:
  `bar islands inks (light): text #FF0F1E21, text-2 #C20F1E21, hover #0D000000, ...`.
- `Shell/Levitate/LevitatePanel.cs` Hover: in light the sheen is LvBarSheen / LvBarSheenBig (.16 / .12). Core has no
  light sheen constants (LevitateTokens.SheenHover / SheenHoverBig are dark's), so light comes from the XAML. Dark still
  uses LevitateTokens. This reaches every LevitatePanel, the bar's islands and the levitated pop-ups.
- The media chip (`BarWindow.Media.cs` MTok) now searches the nested dictionaries, so an LvBar* key follows the BAR's
  theme (before, it fell back to the app's theme); its art tile is LvBarTile.
- The Downloads shelf (`Surfaces/Bar/Downloads/DownloadsShelf.cs` Ink): in the islands look each LOOK.md ink it asks for
  is read as its LvBar* twin (LvText -> LvBarInk, LvCtlFillHover -> LvBarFillHover, LvHair -> LvBarHair, ...) in both
  themes; dark resolves to the same colours. In light it also gives the title the light ink (dark keeps the default)
  and the "could not read" line the light warn #4C3300 (dark keeps LgWarn).

## 3. The islands accent

- Dark: Mantle.Core `Accent.Tokens(Accent.BaseHue("islands", "dark"), "dark", "islands")`, i.e. #a9e7ed, strong
  #c4f0f4 as the hover, on #052a2f. The XAML's Dark LvBarAccent* are its mirror (BarLightTokensTests).
- Light: the local LvBarAccent #083F49, hover #05313A, ink #F4F7F8. **It switches to Core's islands light when
  native/r15-accent-v22 (the look-sync v2.2 port: light #083f49 / #05313a / ink #f4f7f8, base 189) merges**; Core's
  islands light is still steel's #4d6784 here. The one place to change is `IslandsInk.Accent`.
- Used for: the dock's focus underline, the downloads button's dot, a new Downloads row's dot and the bell's count
  badge (and the ink on it). The custom accent hue is not applied (as before: native draws Mantle's own accent).
  Not changed (still steel LgAcc): the dock cell's kept-on-top pin disc and Helm count disc, and every surface that is
  not the islands look (calendar, quick settings, notification centre, ...).

## Tests

- Mantle.Core.Tests `Look/BarLightTokensTests.cs` (new, parses Levitate.xaml as LevitateTokensTests does): each light
  value above, the dark accent equal to Core's islands dark, the light accent, both themes having the same keys, and
  every dark value equal to the LOOK.md token / LevitateTokens sheen / Lg token it replaced.
- Mantle.UiTests `BarFrame/IslandsInkTests.cs` (new): the "bar islands inks" line in light and in dark, word for word,
  and the clock's time read in the pixels (light: dark ink 60+ under the island; dark: 60+ over).
- StripTests' light running-line test: the light run dot is now text-3 (#0F1E21 at .56), about 81 luminance under the
  lifted fallback (LgInk2 was 112); the check's threshold is 30, so no number moved. No other UI test reads an ink
  that changed.

Unverified (no local build): the compile, the hover sheen's light value on screen (no test hovers an island and reads
the pixels), the Downloads shelf's light inks on screen.
