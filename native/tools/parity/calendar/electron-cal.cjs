/* electron-cal.cjs: Electron's clock calendar (ui/bar/index.html over its own mock bridge, ui/bar/mock-mantle.js
 * ?gcal=events) at one frozen instant, light and dark, offscreen at scale 1 (lane native-r7-calendar).
 * Run by run-cal-parity.ps1:  electron electron-cal.cjs <out> <bg> <nowMs>
 * The page's Date is frozen at <nowMs> by a preload that runs before any page script (contextIsolation off for this
 * capture window only), so the mock's "relative to today" events, the month, today's ring and the "synced ... ago"
 * line are the same every run. The clock is clicked, the entrance is let finish, and the pop-up plus 12 px (4 below)
 * is shot over one colour. It also writes what the mock replayed on gcal.status / gcal.events as cal-state.json:
 * the native side takes it as MANTLE_TEST_GCAL, and the same instant as MANTLE_TEST_CAL_NOW, so both draw one month.
 * Prints: ELECTRON-CAL <theme> <w>x<h> <file>, and ELECTRON-CAL-STATE <file>.
 */
const path = require('path');
const fs = require('fs');
const { app, BrowserWindow } = require('electron');
const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const PAGE = path.join(REPO, 'ui', 'bar', 'index.html');
const args = process.argv.slice(process.argv.indexOf(__filename) + 1);
const [out, bg, nowArg] = args;
const NOW = Number(nowArg);
if (!out || !Number.isFinite(NOW)) { console.error('ELECTRON-CAL failed: usage electron-cal.cjs <out> <bg> <nowMs>'); process.exit(2); }
app.commandLine.appendSwitch('force-device-scale-factor', '1');
app.disableHardwareAcceleration();
app.on('window-all-closed', () => { /* one window per shot */ });

// the page's clock, frozen: new Date() and Date.now() are NOW; every other form (a given time) is untouched
const FREEZE = `(() => {
  const Real = Date, T = ${NOW};
  class Frozen extends Real { constructor(...a) { if (a.length === 0) super(T); else super(...a); } static now() { return T; } }
  window.Date = Frozen;
})();`;

app.whenReady().then(async () => {
  fs.mkdirSync(out, { recursive: true });
  const preload = path.join(out, 'cal-freeze-preload.cjs');
  fs.writeFileSync(preload, FREEZE);
  for (const theme of ['light', 'dark']) {
    const win = new BrowserWindow({ show: false, width: 1280, height: 900, useContentSize: true,
      webPreferences: { offscreen: true, preload, contextIsolation: false, sandbox: false, nodeIntegration: false } });
    const url = require('url').pathToFileURL(PAGE).href + '?mock=1&mode=pill&apps=3&gcal=events';
    try { await win.loadURL(url); } catch (e) { await new Promise((r) => setTimeout(r, 300)); await win.loadURL(url); }
    const now = await win.webContents.executeJavaScript('Date.now()');
    if (now !== NOW) throw new Error(`the page's Date is not frozen (${now} vs ${NOW})`);
    await win.webContents.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
    await new Promise((r) => setTimeout(r, 900));
    if (theme === 'light') {
      const st = await win.webContents.executeJavaScript(`new Promise((done) => {
        const o = {};
        const fin = () => { if (o.status && o.events) done(JSON.stringify(o)); };
        window.mantle.on('gcal.status', (s) => { o.status = s; fin(); });
        window.mantle.on('gcal.events', (e) => { o.events = e; fin(); });
      })`);
      const f = path.join(out, 'cal-state.json');
      fs.writeFileSync(f, st);
      console.log(`ELECTRON-CAL-STATE ${f} (${JSON.parse(st).events.events.length} events)`);
    }
    await win.webContents.executeJavaScript(`document.getElementById('clock').click(); true`);
    for (let i = 0; i < 50; i++) { if (await win.webContents.executeJavaScript(`!!document.querySelector('.pop.cal') && !document.getElementById('pop').hidden`)) break; await new Promise((r) => setTimeout(r, 100)); }
    await new Promise((r) => setTimeout(r, 900));   // the entrance (560 ms) and the agenda's paint
    await win.webContents.executeJavaScript(`document.querySelectorAll('.pop.cal, .pop.cal *').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); if (document.activeElement && document.activeElement.blur) document.activeElement.blur(); true`);
    await new Promise((r) => setTimeout(r, 200));
    const r = await win.webContents.executeJavaScript(`(() => { const b = document.querySelector('.pop.cal').getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; })()`);
    const clip = { x: Math.max(0, Math.floor(r.x - 12)), y: Math.max(0, Math.floor(r.y - 12)), width: Math.ceil(r.w + 24), height: Math.ceil(r.h + 12 + 4) };
    const img = await win.webContents.capturePage(clip);
    const file = path.join(out, `electron-cal-${theme}.png`);
    fs.writeFileSync(file, img.toPNG());
    const sz = img.getSize();
    console.log(`ELECTRON-CAL ${theme} ${sz.width}x${sz.height} pop=${Math.round(r.w)}x${Math.round(r.h)} ${file}`);
    win.destroy();
  }
  app.quit();
}).catch((e) => { console.error('ELECTRON-CAL failed: ' + (e && e.stack || e)); app.exit(1); });
