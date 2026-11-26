# Lane r16-qs-accent

Quick settings crashed on open after lane r16-bar-accent: Theme/Levitate.xaml no longer has LvBarAccent /
LvBarAccentInk (the islands accent is Mantle.Core Accent's "islands" profile, read by Shell/Levitate/IslandsInk.Accent),
but Surfaces/Bar/QuickSettings/QsLook.cs still mapped LgAcc / LgAccLine / LgAccOn to those names.

Why it crashed instead of drawing clear: QsLook.Ink (the brushes set in code) already used IslandsInk.Accent, but
QsPanel.Fill (every glyph of the panel) turned the Lg* name into its twin with QsLook.Key and wrote it into a
`{ThemeResource LvBarAccent}` string for XamlReader.Load. A missing name there is a XAML parse error (0x802B000A), thrown
out of the panel's paint, so the app stopped (exit code 1).

The fix: the accent names have no twin any more; in the islands look Fill sets the brush QsPanel.B gives (QsLook.Ink,
so the accent from IslandsInk.Accent in both themes and any missing LvBar* twin drawn clear by IslandsInk.Token and
said once) on the path in code, never as a {ThemeResource} name. QsLook.Key is gone. The Electron look is unchanged.

The check: Mantle.Core.Tests Look/ResourceKeysTests scans Mantle.App's sources for every key looked up by name (the
lookup helpers, token-shaped string literals, {ThemeResource} / {StaticResource} in XAML and in XAML strings in code) and
fails, with file:line, on any key Theme/Theme.xaml and its merged dictionaries do not define in the theme(s) asked.
