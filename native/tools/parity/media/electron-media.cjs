'use strict';
/* electron-media.cjs (lane native-r7-media): Electron's now-playing card (ui/bar/index.html?media=noctl over its own mock
 * bridge, ui/bar/mock-mantle.js), rendered by Electron itself offscreen at scale 1, one PNG per theme: the card plus
 * 12 px round it (4 below) over ONE solid colour, so run-media-parity.ps1 can put the native card over the same colour.
 * The session is the mock's "Media Player" one (no Spotify: the card's Spotify section is not native yet). Writes
 * <out>/media-state.json = {sessions:[the shown session]}, which the native side takes as MANTLE_TEST_MEDIA so both apps
 * draw the same session. Nothing of the user's is read; nothing is written outside <out>.
 *
 *   node_modules/.bin/electron native/tools/parity/media/electron-media.cjs <out-dir> <bg #rrggbb>
 *
 * Prints: ELECTRON-MEDIA <theme> <w>x<h> <file>, and ELECTRON-MEDIA-STATE <file>.
 */
const path = require('path');
const fs = require('fs');
const { app, BrowserWindow } = require('electron');
const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const PAGE = path.join(REPO, 'ui', 'bar', 'index.html');
const args = process.argv.slice(process.argv.indexOf(__filename) + 1);
const [out, bg] = args;
app.commandLine.appendSwitch('force-device-scale-factor', '1');
app.disableHardwareAcceleration();
app.on('window-all-closed', () => { /* one window per shot */ });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

app.whenReady().then(async () => {
  fs.mkdirSync(out, { recursive: true });
  for (const theme of ['light', 'dark']) {
    const win = new BrowserWindow({ show: false, width: 1280, height: 900, useContentSize: true, webPreferences: { offscreen: true } });
    const url = require('url').pathToFileURL(PAGE).href + '?mock=1&mode=pill&apps=3&media=noctl';
    try { await win.loadURL(url); } catch (e) { await sleep(300); await win.loadURL(url); }
    await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
    await sleep(900);
    if (theme === 'light') {
      const st = await win.webContents.executeJavaScript(`window.mantle.call('media.get').then((p) => {
        const d = (x) => (x && x.ok !== undefined && x.result !== undefined ? x.result : x);
        const m = d(p);
        const s = Object.assign({}, m.shown, { current: true });
        return JSON.stringify({ sessions: [s] });
      })`);
      const f = path.join(out, 'media-state.json');
      fs.writeFileSync(f, st);
      console.log(`ELECTRON-MEDIA-STATE ${f}`);
    }
    await win.webContents.executeJavaScript(`(document.getElementById('mchip') || document.querySelector('#media button')).click(); true`);
    for (let i = 0; i < 50; i++) { if (await win.webContents.executeJavaScript(`!!document.querySelector('.pop.mcard') && !document.getElementById('pop').hidden`)) break; await sleep(100); }
    await sleep(900);   // the entrance and the first paint
    await win.webContents.executeJavaScript(`document.querySelectorAll('.pop.mcard, .pop.mcard *').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
    await sleep(200);
    const r = await win.webContents.executeJavaScript(`(() => { const p = document.querySelector('.pop.mcard'); if (!p) return null; const b = p.getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; })()`);
    if (!r) throw new Error('the card did not open (no .pop.mcard)');
    const clip = { x: Math.max(0, Math.floor(r.x - 12)), y: Math.max(0, Math.floor(r.y - 12)), width: Math.ceil(r.w + 24), height: Math.ceil(r.h + 12 + 4) };
    const img = await win.webContents.capturePage(clip);
    const file = path.join(out, `electron-media-${theme}.png`);
    fs.writeFileSync(file, img.toPNG());
    const sz = img.getSize();
    console.log(`ELECTRON-MEDIA ${theme} ${sz.width}x${sz.height} card=${Math.round(r.w)}x${Math.round(r.h)} ${file}`);
    win.destroy();
  }
  // The Game mode row (lane native-r7-media3): quick settings opened from #volume over the mock's default game mode
  // state (?gm=off: watching, nothing on), the row's head and its switch line plus 8 px round it (6 above and below).
  for (const theme of ['light', 'dark']) {
    const win = new BrowserWindow({ show: false, width: 1280, height: 900, useContentSize: true, webPreferences: { offscreen: true } });
    const url = require('url').pathToFileURL(PAGE).href + '?mock=1&mode=pill&apps=3&media=noctl';
    try { await win.loadURL(url); } catch (e) { await sleep(300); await win.loadURL(url); }
    await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
    await sleep(900);
    await win.webContents.executeJavaScript(`document.getElementById('volume').click(); true`);
    for (let i = 0; i < 50; i++) { if (await win.webContents.executeJavaScript(`!!document.querySelector('.pop.qs .gmrow .gmswitch') && !document.getElementById('pop').hidden`)) break; await sleep(100); }
    await sleep(900);
    await win.webContents.executeJavaScript(`document.querySelectorAll('.pop.qs, .pop.qs *').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
    await sleep(200);
    const r = await win.webContents.executeJavaScript(`(() => { const row = document.querySelector('.pop.qs .gmrow'); const sw = row && row.querySelector('.gmswitch'); if (!row || !sw) return null; const a = row.getBoundingClientRect(), b = sw.getBoundingClientRect(); return { x: a.x, y: a.y, w: a.width, bottom: b.bottom }; })()`);
    if (!r) throw new Error('the game mode row did not show (no .pop.qs .gmrow)');
    const clip = { x: Math.max(0, Math.floor(r.x - 8)), y: Math.max(0, Math.floor(r.y - 6)), width: Math.ceil(r.w + 16), height: Math.ceil(r.bottom - r.y + 12) };
    const img = await win.webContents.capturePage(clip);
    const file = path.join(out, `electron-gamemode-${theme}.png`);
    fs.writeFileSync(file, img.toPNG());
    const sz = img.getSize();
    console.log(`ELECTRON-MEDIA gamemode-${theme} ${sz.width}x${sz.height} row=${Math.round(r.w)}x${Math.round(r.bottom - r.y)} ${file}`);
    win.destroy();
  }
  app.quit();
}).catch((e) => { console.error('ELECTRON-MEDIA failed: ' + (e && e.stack || e)); app.exit(1); });
