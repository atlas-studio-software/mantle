// Generates Theme/Tokens.xaml from native/docs/theme-tokens.md section 1.1 (the --lg-* table, one row per
// custom property of ui/shared/glass-light.css). Run: node native/src/Mantle.App/Theme/gen-tokens.js
// The table's "XAML light" / "XAML dark" columns are the colours; the non-colour tokens (radii, timings,
// blur, fonts) are mapped below by name. TokensTests checks the result against the table, 1:1.
'use strict';
const fs = require('fs');
const path = require('path');

const md = fs.readFileSync(path.join(__dirname, '..', '..', '..', 'docs', 'theme-tokens.md'), 'utf8');
const start = md.indexOf('### 1.1 Table');
const end = md.indexOf('### 1.2', start);
const rows = md.slice(start, end).split('\n').filter((l) => /^\| `--/.test(l)).map((l) => l.split('|').map((c) => c.trim()));
// columns: '', token, light, dark, xamlLight, xamlDark, scope, note, ''
const keyOf = (name) => name.replace(/^--/, '').split('-').map((p) => p[0].toUpperCase() + p.slice(1)).join('');
const c = (t) => t.replace(/--/g, '-');   // XML comments may not hold "--"
const isArgb = (v) => /^#[0-9A-F]{8}$/.test(v);

// Dark-only names get an explicit light value (PLAN.md 3.8: a deviation that fixes a known Electron gap,
// where light consumers fell back to per-page values). Chosen from theme-tokens.md 1.3.
const LIGHT_FOR_DARK_ONLY = {
  '--lg-fill-bar': ['#73FFFFFF', 'light bars share the one white --lg-fill (LOOK-075)'],
  '--lg-ink-on-light': ['#FF0B0E1A', 'ink on a light surface is the light ink'],
  '--lg-ink-2-on-light': ['#FF333A4E', 'as --lg-ink-2 in light'],
  '--lg-ink-3-on-light': ['#FF5B6478', 'as --lg-ink-3 in light'],
  '--lg-hover-on-light': ['#0F0B0E1A', 'as --lg-hover in light'],
  '--lg-press-on-light': ['#1A0B0E1A', 'as --lg-press in light'],
  '--lg-warn-on-light': ['#FF8A4B00', 'as --lg-warn in light'],
  '--lg-acc-on-light': ['#FF4436E8', 'as --lg-acc in light'],
  '--lg-lift': ['#FFFFFFFF', 'white lift, as dark'],
  '--lg-ok': ['#FF2E7D4F', "the light fallback consumers use (var(--lg-ok, #2e7d4f))"],
  '--lg-danger': ['#FFB3261E', "the most used light fallback (var(--lg-danger, #b3261e)) x5"],
};

const px = (v) => (v.match(/(-?[\d.]+)px/) || [])[1];
const ms = (v) => (v.match(/(\d+)ms/) || [])[1];
const bez = (v) => { const m = v.match(/cubic-bezier\(([^)]*)\)/); return m ? m[1].split(',').map((x) => Number(x.trim())).join(',') : null; };

const light = [], dark = [], shared = [];
const note = [];
for (const r of rows) {
  const name = r[1].replace(/`/g, ''), lv = r[2].replace(/`/g, ''), dv = r[3].replace(/`/g, ''), xl = r[4], xd = r[5], scope = r[6];
  const key = keyOf(name);
  if (isArgb(xl) || isArgb(xd)) {
    let l = xl, why = null;
    if (!isArgb(l)) { const f = LIGHT_FOR_DARK_ONLY[name]; if (!f) throw new Error('no light value for ' + name); [l, why] = f; }
    const d = isArgb(xd) ? xd : xl;
    light.push(`      <SolidColorBrush x:Key="${key}" Color="${l}"/>${why ? `  <!-- dark-only in CSS: ${c(why)} -->` : ''}`);
    dark.push(`      <SolidColorBrush x:Key="${key}" Color="${d}"/>`);
    continue;
  }
  const dvv = /inherits/.test(dv) ? lv : dv;
  if (/^--lg-r/.test(name)) {
    const a = px(lv) || px(dvv), b = px(dvv);
    const la = name === '--lg-r-xl' || name === '--lg-r-chip' ? b : a;   // dark-only radii: light takes the dark value
    light.push(`      <x:Double x:Key="${key}">${la}</x:Double>`, `      <CornerRadius x:Key="${key}Corner">${la}</CornerRadius>`);
    dark.push(`      <x:Double x:Key="${key}">${b}</x:Double>`, `      <CornerRadius x:Key="${key}Corner">${b}</CornerRadius>`);
  } else if (ms(lv) || ms(dvv) || ms(xl)) {
    shared.push(`  <x:Double x:Key="${key}Ms">${ms(lv) || ms(dvv) || ms(xl)}</x:Double>`);
  } else if (bez(lv)) {
    shared.push(`  <x:String x:Key="${key}Bezier">${bez(lv)}</x:String>`);
  } else if (name === '--lg-blur') {
    light.push('      <x:Double x:Key="LgBlurPx">12</x:Double>', '      <x:Double x:Key="LgBlurSaturate">1.5</x:Double>');
    dark.push('      <x:Double x:Key="LgBlurPx">5</x:Double>', '      <x:Double x:Key="LgBlurSaturate">1</x:Double>');
  } else if (name === '--lg-disabled') {
    shared.push('  <x:Double x:Key="LgDisabled">0.45</x:Double>');
  } else if (name === '--lg-guard-bar') {
    light.push('      <x:Double x:Key="LgGuardBar">4.7</x:Double>  <!-- dark-only in CSS: light uses the guard default need 4.7 -->');
    dark.push('      <x:Double x:Key="LgGuardBar">4.2</x:Double>');
  } else if (name === '--lg-font' || name === '--lg-font-display' || name === '--lg-mono') {
    const fam = dvv.split(',').map((s) => s.trim().replace(/"/g, '')).filter((s) => !/^(system-ui|sans-serif|monospace)$/.test(s)).join(', ');
    shared.push(`  <FontFamily x:Key="${key}">${fam}</FontFamily>`);
  } else {
    note.push(`${name} (${scope})`);
  }
}

const legacyLight = [
  '      <!-- the bar\'s own names (BarWindow): the same values as the tokens above -->',
  '      <SolidColorBrush x:Key="LgPaneFill" Color="#73FFFFFF"/>',
  '      <SolidColorBrush x:Key="LgPaneFillFallback" Color="#9EFFFFFF"/>',
  '      <SolidColorBrush x:Key="LgLip" Color="#9EFFFFFF"/>',
  '      <CornerRadius x:Key="LgPillCorner">24</CornerRadius>',
  '      <CornerRadius x:Key="LgLipCorner">23</CornerRadius>',
  '      <x:Double x:Key="LgBodySize">12.5</x:Double>',
  '      <x:Double x:Key="LgDateSize">12.5</x:Double>',
];
const legacyDark = [
  '      <!-- the bar\'s own names (BarWindow): its .50 wash (-lg-fill-bar), fallback -lg-fill .83 -->',
  '      <SolidColorBrush x:Key="LgPaneFill" Color="#80090A0B"/>',
  '      <SolidColorBrush x:Key="LgPaneFillFallback" Color="#D4131415"/>',
  '      <SolidColorBrush x:Key="LgLip" Color="#12FFFFFF"/>',
  '      <CornerRadius x:Key="LgPillCorner">18</CornerRadius>',
  '      <CornerRadius x:Key="LgLipCorner">17</CornerRadius>',
  '      <x:Double x:Key="LgBodySize">13</x:Double>',
  '      <x:Double x:Key="LgDateSize">12</x:Double>',
];

const xaml = `<?xml version="1.0" encoding="utf-8"?>
<!--
  GENERATED by Theme/gen-tokens.js from native/docs/theme-tokens.md 1.1; do not edit by hand.
  The island theme of ui/shared/glass-light.css, one key per -lg-* custom property (-lg-ink-2 = LgInk2),
  Light = :root, Dark = :root[data-theme="dark"]. rgba(r,g,b,a) is #AARRGGBB with a = round(a * 255).
  Radii are LgR* (a double) and LgR*Corner; timings are *Ms; cubic-bezier curves are *Bezier ("x1,y1,x2,y2").
  Not mapped here (composite CSS values the surfaces draw themselves): ${c(note.join(', '))}.
  This is the ONLY file with colour literals in Mantle.App (TokensTests lints the rest).
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
${light.concat(legacyLight).join('\n')}
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
${dark.concat(legacyDark).join('\n')}
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
${shared.join('\n')}
</ResourceDictionary>
`;
fs.writeFileSync(path.join(__dirname, 'Tokens.xaml'), xaml.replace(/\n/g, '\r\n'));
console.log(`Tokens.xaml: ${light.length} light, ${dark.length} dark, ${shared.length} shared; not mapped: ${note.length}`);
