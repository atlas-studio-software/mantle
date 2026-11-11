'use strict';
/* electron-notifs.cjs: Electron's notification centre and tray strip (ui/bar/index.html over its own mock bridge,
 * ui/bar/mock-mantle.js ?tray=tones&notifs=some), rendered by Electron itself in an offscreen window at scale 1, one PNG
 * per theme and part: the centre plus 12 px round it (4 below) and the tray strip plus 4 px round it, over ONE solid
 * colour, so run-notifs-parity.ps1 can put the native ones over the same colour and diff them. Nothing of the user's is
 * read; nothing is written outside <out>.
 *
 *   node_modules/.bin/electron native/tools/parity/notifications/electron-notifs.cjs <out-dir> <bg #rrggbb>
 *
 * Writes <out>/notifs-state.json: the mock's notifs.get answer (read BEFORE the panel opens, so the unread flags are
 * the ones the panel drew) and tray.list answer, with the time they were read, which the native side takes as
 * MANTLE_TEST_NOTIFS / MANTLE_TEST_TRAY so both apps draw the same lists.
 * Prints: ELECTRON-NOTIFS <part>-<theme> <w>x<h> <file>, and ELECTRON-NOTIFS-STATE <file>.
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
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function shoot(win, sel, pad, below, file) {
  const r = await win.webContents.executeJavaScript(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return null; const b = e.getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; })()`);
  if (!r || !r.w) throw new Error('nothing to shoot at ' + sel);
  const clip = { x: Math.max(0, Math.floor(r.x - pad)), y: Math.max(0, Math.floor(r.y - pad)), width: Math.ceil(r.w + 2 * pad), height: Math.ceil(r.h + pad + below) };
  const img = await win.webContents.capturePage(clip);
  fs.writeFileSync(file, img.toPNG());
  return { size: img.getSize(), r };
}

app.whenReady().then(async () => {
  fs.mkdirSync(out, { recursive: true });
  for (const theme of ['light', 'dark']) {
    const win = new BrowserWindow({ show: false, width: 1280, height: 900, useContentSize: true, webPreferences: { offscreen: true } });
    const url = require('url').pathToFileURL(PAGE).href + '?mock=1&mode=pill&tray=tones&notifs=some';
    try { await win.loadURL(url); } catch (e) { await wait(300); await win.loadURL(url); }
    await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
    await wait(1200);   // the tray's tones are measured once each, then the strip is drawn
    if (theme === 'light') {
      const st = await win.webContents.executeJavaScript(`Promise.all([window.mantle.call('notifs.get'), window.mantle.call('tray.list')]).then(([n, t]) => {
        const d = (x) => (x && x.ok !== undefined && x.result !== undefined ? x.result : x);
        return JSON.stringify({ at: Date.now(), notifs: d(n), tray: d(t) });
      })`);
      const f = path.join(out, 'notifs-state.json');
      fs.writeFileSync(f, st);
      console.log(`ELECTRON-NOTIFS-STATE ${f}`);
    }
    await win.webContents.executeJavaScript(`document.querySelectorAll('.tray, .tray *').forEach((e) => { e.style.transition = 'none'; }); true`);
    const t = await shoot(win, '#tray', 4, 4, path.join(out, `electron-tray-${theme}.png`));
    console.log(`ELECTRON-NOTIFS tray-${theme} ${t.size.width}x${t.size.height} strip=${Math.round(t.r.w)}x${Math.round(t.r.h)} ${path.join(out, `electron-tray-${theme}.png`)}`);
    await win.webContents.executeJavaScript(`document.getElementById('notifs').click(); true`);
    for (let i = 0; i < 50; i++) { if (await win.webContents.executeJavaScript(`!!document.querySelector('.pop.ntf') && !document.getElementById('pop').hidden`)) break; await wait(100); }
    await wait(900);   // the entrance and the pictures
    await win.webContents.executeJavaScript(`document.querySelectorAll('.pop.ntf, .pop.ntf *').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
    await wait(200);
    const file = path.join(out, `electron-centre-${theme}.png`);
    const c = await shoot(win, '.pop.ntf', 12, 4, file);
    console.log(`ELECTRON-NOTIFS centre-${theme} ${c.size.width}x${c.size.height} pop=${Math.round(c.r.w)}x${Math.round(c.r.h)} ${file}`);
    win.destroy();
  }
  app.quit();
}).catch((e) => { console.error('ELECTRON-NOTIFS failed: ' + (e && e.stack || e)); app.exit(1); });
