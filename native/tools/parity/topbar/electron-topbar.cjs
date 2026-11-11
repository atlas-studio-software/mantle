'use strict';
/* electron-topbar.cjs (lane native-r7-topbar): Electron's top bar strip (ui/topbar/index.html over its own mock bridge,
 * ui/topbar/mock-mantle.js, app=npp&stats=ok) rendered by Electron itself in a window that is never shown, one PNG per theme: the
 * 28 DIP strip at the screen's width, over ONE solid colour, so run-topbar-parity.ps1 can put the native strip over the
 * same colour and diff the two. Nothing of the user's is read; nothing is written outside <out>.
 *
 *   node_modules/electron/dist/electron.exe native/tools/parity/topbar/electron-topbar.cjs <out-dir> <bg #rrggbb> <screen px> <scale> <pct>
 *
 * The page is laid out at <screen px> / <scale> CSS px wide (the native window's width in DIP) on a display the caller has
 * already set to <scale>, so both strips are the same physical size. The theme is set on <html data-theme>
 * before the first paint (this file is also the window's preload, as shoot.js's darkInit does). Like with like: no
 * ?flat= / ?scene=, so the dark strip is its own fill (--lg-fill-pop), not the dark glass over a desktop picture (the
 * Glass section is not native yet; docs/lanes/topbar.md).
 * The window is a normal one that is never shown (offscreen rendering ignores the scale). Prints: ELECTRON-TOPBAR
 * <theme>-<pct> <w>x<h> <file>.
 */
const path = require('path');

if (process.type === 'renderer') {
  // the preload: the theme before the page's first paint
  const a = process.argv.find((x) => x.startsWith('--topbar-theme='));
  const theme = a ? a.slice('--topbar-theme='.length) : 'light';
  const set = () => { if (document.documentElement) document.documentElement.setAttribute('data-theme', theme); };
  set();
  document.addEventListener('readystatechange', set);
} else {
  const fs = require('fs');
  const { app, BrowserWindow } = require('electron');
  const REPO = path.resolve(__dirname, '..', '..', '..', '..');
  const PAGE = path.join(REPO, 'ui', 'topbar', 'index.html');
  const args = process.argv.slice(process.argv.indexOf(__filename) + 1);
  const [out, bg, screenPx, scaleArg, pct] = args;
  const scale = Number(scaleArg) || 1;
  const cssW = Math.round(Number(screenPx) / scale);
  // no force-device-scale-factor: run-topbar-parity.ps1 sets the display itself to the scale first, and forcing it on
  // top laid the 150 % page out 853 CSS px wide (two scalings) where the screen is 1280 DIP
  app.disableHardwareAcceleration();
  app.on('window-all-closed', () => { /* one window per shot */ });

  app.whenReady().then(async () => {
    fs.mkdirSync(out, { recursive: true });
    for (const theme of ['light', 'dark']) {
      const win = new BrowserWindow({
        show: false, frame: false, width: cssW, height: 60, useContentSize: true, x: 0, y: 0,
        // a normal window that is never shown, not an offscreen one: offscreen rendering paints at scale 1 whatever
        // force-device-scale-factor says (its 150 % shots came back 1280x28 for a 1920 px screen)
        webPreferences: { sandbox: false, preload: __filename, additionalArguments: ['--topbar-theme=' + theme], backgroundThrottling: false },
      });
      const url = require('url').pathToFileURL(PAGE).href + '?app=npp&stats=ok';
      try { await win.loadURL(url); } catch (e) { await new Promise((r) => setTimeout(r, 300)); await win.loadURL(url); }
      await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
      await new Promise((r) => setTimeout(r, 1200));   // fonts, the first stats paint and the fit
      await win.webContents.executeJavaScript(`document.querySelectorAll('*').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
      await new Promise((r) => setTimeout(r, 200));
      const got = await win.webContents.executeJavaScript(`(() => { const b = document.getElementById('bar').getBoundingClientRect(); return { w: b.width, h: b.height, theme: document.documentElement.getAttribute('data-theme'), inner: innerWidth, screenW: screen.width, screenX: screenX, dpr: devicePixelRatio }; })()`);
      const img = await win.webContents.capturePage({ x: 0, y: 0, width: cssW, height: 28 }, { stayHidden: true });
      const file = path.join(out, `electron-topbar-${theme}-${pct}.png`);
      fs.writeFileSync(file, img.toPNG());
      const sz = img.getSize();
      fs.writeFileSync(path.join(out, `electron-topbar-${theme}-${pct}.json`), JSON.stringify(Object.assign({ png: sz }, got)));   // Electron's stdout does not reach the CI log
      console.log(`ELECTRON-TOPBAR ${theme}-${pct} ${sz.width}x${sz.height} bar=${Math.round(got.w)}x${Math.round(got.h)} css, inner ${got.inner}, theme ${got.theme} ${file}`);
      win.destroy();
    }
    app.quit();
  }).catch((e) => { console.error('ELECTRON-TOPBAR failed: ' + (e && e.stack || e)); app.exit(1); });
}
