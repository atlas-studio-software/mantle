'use strict';
/* electron-osd.cjs: Electron's volume pop-up page (ui/osd/index.html over its own mock bridge, ui/osd/mock-mantle.js,
 * the page ui/osd/shoot.js shoots), rendered by Electron itself in an offscreen window at scale 1, one PNG per state and
 * theme: the pane plus the 18 DIP margin (--m) round it, over ONE solid colour, so run-osd-parity.ps1 can put the native
 * window over the same colour and diff the two. Nothing of the user's is read; nothing is written outside <out>.
 *
 *   node_modules/.bin/electron native/tools/parity/osd-toasts/electron-osd.cjs <out-dir> <bg #rrggbb> <name=query> ...
 *   e.g. vol-62=level=62  vol-muted=level=38&muted=1  vol-dead=dead=1  bright-70=kind=brightness&level=70
 * Prints one line per shot: ELECTRON-OSD <name> <theme> <w>x<h> <file>
 */
const path = require('path');
const fs = require('fs');
const { app, BrowserWindow } = require('electron');
const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const PAGE = path.join(REPO, 'ui', 'osd', 'index.html');
const args = process.argv.slice(process.argv.indexOf(__filename) + 1);
const [out, bg, ...specs] = args;
app.commandLine.appendSwitch('force-device-scale-factor', '1');
app.disableHardwareAcceleration();

app.whenReady().then(async () => {
  fs.mkdirSync(out, { recursive: true });
  for (const theme of ['light', 'dark']) {
    for (const spec of specs) {
      const eq = spec.indexOf('=');
      const name = spec.slice(0, eq), q = spec.slice(eq + 1);
      const win = new BrowserWindow({ show: false, width: 440, height: 560, useContentSize: true, webPreferences: { offscreen: true } });
      await win.loadFile(PAGE, { search: q });
      await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; true`);
      for (let i = 0; i < 50; i++) { if (await win.webContents.executeJavaScript(`!document.getElementById('osd').hidden`)) break; await new Promise((r) => setTimeout(r, 100)); }
      await new Promise((r) => setTimeout(r, 400));
      const r = await win.webContents.executeJavaScript(`(() => { const b = document.getElementById('osd').getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; })()`);
      const clip = { x: Math.floor(r.x - 18), y: Math.floor(r.y - 18), width: Math.ceil(r.w + 36), height: Math.ceil(r.h + 36) };
      const img = await win.webContents.capturePage(clip);
      const file = path.join(out, `electron-${name}-${theme}.png`);
      fs.writeFileSync(file, img.toPNG());
      const sz = img.getSize();
      console.log(`ELECTRON-OSD ${name} ${theme} ${sz.width}x${sz.height} ${file}`);
      win.destroy();
    }
  }
  app.quit();
}).catch((e) => { console.error('ELECTRON-OSD failed: ' + (e && e.stack || e)); app.exit(1); });
