'use strict';
/* electron-qs.cjs: Electron's quick-settings pop-up (ui/bar/index.html over its own mock bridge, ui/bar/mock-mantle.js),
 * rendered by Electron itself in an offscreen window at scale 1, one PNG per theme: the pop-up plus 12 px round it over
 * ONE solid colour, so run-qs-parity.ps1 can put the native pop-up over the same colour and diff the two. Nothing of the
 * user's is read; nothing is written outside <out>.
 *
 *   node_modules/.bin/electron native/tools/parity/quick-settings/electron-qs.cjs <out-dir> <bg #rrggbb> [part1|full]
 *
 * part1 (default): the rows native part 1 does not draw yet are taken out of Electron's panel before the shot (the
 * qs-tiles row, the three ">" buttons, game mode, top bar and hot corners), so the pair compares like with like; full:
 * the whole panel, for the eye. Writes <out>/qs-state.json: the mock's qs.get and powermode.get answers, which the native
 * side takes as MANTLE_TEST_QS so both apps draw the same readings.
 * Prints: ELECTRON-QS <theme> <w>x<h> <file>, and ELECTRON-QS-STATE <file>.
 */
const path = require('path');
const fs = require('fs');
const { app, BrowserWindow } = require('electron');
const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const PAGE = path.join(REPO, 'ui', 'bar', 'index.html');
const args = process.argv.slice(process.argv.indexOf(__filename) + 1);
const [out, bg, mode = 'part1'] = args;
app.commandLine.appendSwitch('force-device-scale-factor', '1');
app.disableHardwareAcceleration();
app.on('window-all-closed', () => { /* one window per shot */ });

const PART2_OUT = `(() => {
  const pop = document.querySelector('.pop.qs');
  if (!pop) return 'no .pop.qs';
  const keep = new Set(['qsgrid', 'qsrow', 'qsnotes', 'pmrow', 'qsfoot']);
  let gone = 0;
  for (const c of Array.from(pop.children)) if (![...c.classList].some((k) => keep.has(k))) { c.remove(); gone++; }
  const grid = pop.querySelector('.qsgrid');
  if (grid) Array.from(grid.children).slice(4).forEach((c) => { c.remove(); gone++; });
  // the ">" buttons qs-more.js adds beside the Wi-Fi and Bluetooth tiles (each tile wrapped with its chevron in a
  // .qsx-split: unwrapped here, so the tile keeps its own width, as native draws it) and after the mute button
  // (class "qsmute qsx-more"). The first CI run (42786941393) removed the tiles themselves with ".qsgrid > * > button".
  pop.querySelectorAll('.qsx-chev, .qsx-more').forEach((b) => { b.remove(); gone++; });
  pop.querySelectorAll('.qsx-split').forEach((s) => { while (s.firstChild) s.parentNode.insertBefore(s.firstChild, s); s.remove(); });
  pop.querySelectorAll('.pmplans, .pmplantoggle').forEach((b) => { b.remove(); gone++; });
  return 'removed ' + gone;
})()`;

app.whenReady().then(async () => {
  fs.mkdirSync(out, { recursive: true });
  for (const theme of ['light', 'dark']) {
    const win = new BrowserWindow({ show: false, width: 1280, height: 900, useContentSize: true, webPreferences: { offscreen: true } });
    const url = require('url').pathToFileURL(PAGE).href + '?mock=1&mode=pill&apps=3';
    try { await win.loadURL(url); } catch (e) { await new Promise((r) => setTimeout(r, 300)); await win.loadURL(url); }
    await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
    await new Promise((r) => setTimeout(r, 900));
    if (theme === 'light') {
      const st = await win.webContents.executeJavaScript(`Promise.all([window.mantle.call('qs.get'), window.mantle.call('powermode.get')]).then(([q, p]) => {
        const d = (x) => (x && x.ok !== undefined && x.result !== undefined ? x.result : x);
        const qs = d(q), pm = d(p), o = {};
        for (const k of ['volume', 'wifi', 'bluetooth', 'brightness', 'nightlight', 'power']) if (qs && qs[k]) o[k] = qs[k];
        o.powermode = pm;
        return JSON.stringify(o);
      })`);
      const f = path.join(out, 'qs-state.json');
      fs.writeFileSync(f, st);
      console.log(`ELECTRON-QS-STATE ${f}`);
    }
    await win.webContents.executeJavaScript(`document.getElementById('volume').click(); true`);
    for (let i = 0; i < 50; i++) { if (await win.webContents.executeJavaScript(`!!document.querySelector('.pop.qs') && !document.getElementById('pop').hidden`)) break; await new Promise((r) => setTimeout(r, 100)); }
    await new Promise((r) => setTimeout(r, 900));   // the entrance (560 ms) and the first qs.get paint
    if (mode === 'part1') console.log('ELECTRON-QS part1: ' + await win.webContents.executeJavaScript(PART2_OUT));
    await win.webContents.executeJavaScript(`document.querySelectorAll('.pop.qs, .pop.qs *').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
    await new Promise((r) => setTimeout(r, 200));
    const r = await win.webContents.executeJavaScript(`(() => { const b = document.querySelector('.pop.qs').getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; })()`);
    const clip = { x: Math.max(0, Math.floor(r.x - 12)), y: Math.max(0, Math.floor(r.y - 12)), width: Math.ceil(r.w + 24), height: Math.ceil(r.h + 12 + 4) };   // 4 below: the bar is 7 under it
    const img = await win.webContents.capturePage(clip);
    const file = path.join(out, `electron-qs-${theme}${mode === 'full' ? '-full' : ''}.png`);
    fs.writeFileSync(file, img.toPNG());
    const sz = img.getSize();
    console.log(`ELECTRON-QS ${theme} ${sz.width}x${sz.height} pop=${Math.round(r.w)}x${Math.round(r.h)} ${file}`);
    win.destroy();
  }
  app.quit();
}).catch((e) => { console.error('ELECTRON-QS failed: ' + (e && e.stack || e)); app.exit(1); });
