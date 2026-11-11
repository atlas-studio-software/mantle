/* electron-desk.cjs: Electron's desktop layer (ui/desktop/index.html) over a FAKE bridge with a frozen clock, light and
 * dark, offscreen at scale 1 (lane native-r7-desktop-2). Run by run-desk-parity.ps1:
 *   electron electron-desk.cjs <out> <bg> <fixture.json> <nowMs>
 * The fixture is the same MANTLE_TEST_DESKTOP the native side takes: its stats, its Mission Control answer and its strip
 * rows are what the fake bridge answers (sys.stats, net.probe, and a 'state' whose failing pieces are those rows), and
 * the page's Date is frozen at <nowMs> by a preload that runs before any page script (contextIsolation off for this
 * capture window only). No 'desktop.backdrop' picture is ever sent, so the panes wear the fallback tint, as the native
 * layer does. Shots per theme, each over one colour with 12 px around:
 *   electron-desk-<theme>-stats.png   the System widget
 *   electron-desk-<theme>-strip.png   the Needs attention strip
 *   electron-desk-<theme>-menu.png    the right-click menu (its own window, as Electron opens it: the panel plus its inset)
 * Prints: ELECTRON-DESK <theme> <part> <w>x<h> <file>.
 */
const path = require('path');
const fs = require('fs');
const { app, BrowserWindow } = require('electron');
const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const PAGE = path.join(REPO, 'ui', 'desktop', 'index.html');
const args = process.argv.slice(process.argv.indexOf(__filename) + 1);
const [out, bg, fixtureFile, nowArg] = args;
const NOW = Number(nowArg);
if (!out || !fixtureFile || !Number.isFinite(NOW)) { console.error('ELECTRON-DESK failed: usage electron-desk.cjs <out> <bg> <fixture.json> <nowMs>'); process.exit(2); }
const FX = JSON.parse(fs.readFileSync(fixtureFile, 'utf8'));
app.commandLine.appendSwitch('force-device-scale-factor', '1');
app.disableHardwareAcceleration();
app.on('window-all-closed', () => { /* one window per theme */ });

// The labels the page gives its state pieces (ui/desktop/desktop.js PIECES): a fixture row whose label is one of these
// becomes that piece, failing with the row's reason; any other label becomes a failing feature (label, reason).
const PIECE = { 'Sidecar': ['sidecar', (r) => ({ status: 'exited', reason: r })], 'Desktop layer': ['desktop', (r) => ({ attached: false, reason: r })],
  'Windows taskbar': ['taskbar', (r) => ({ autohide: false, reason: r })], 'Win key': ['hotkey', (r) => ({ winTap: false, reason: r })],
  'Notification area': ['tray', (r) => ({ status: 'failed', reason: r })] };
const state = { mode: 'test', features: {} };
(FX.strip || []).forEach(([label, reason], i) => {
  const p = PIECE[label];
  if (p) state[p[0]] = p[1](reason); else state.features['f' + i] = { on: true, ok: false, label, reason };
});
const stats = Object.assign({ battery: null, batteryReason: 'this machine reports no battery' }, FX.stats || {});
const probe = !FX.mc || FX.mc === 'ok' ? { ok: true } : { ok: false, reason: FX.mc };

// before any page script: the frozen clock and the fake bridge (window.__emit fires a channel at the page)
const PRELOAD = `(() => {
  const Real = Date, T = ${NOW};
  class Frozen extends Real { constructor(...a) { if (a.length === 0) super(T); else super(...a); } static now() { return T; } }
  window.Date = Frozen;
  if (window.opener) return;   // the menu's own window: no bridge (it has none in Electron either)
  const STATE = ${JSON.stringify(state)}, STATS = ${JSON.stringify(stats)}, PROBE = ${JSON.stringify(probe)};
  const L = Object.create(null);
  window.__emit = (ch, p) => (L[ch] || []).slice().forEach((f) => f(p));
  window.__dmPlace = null;
  window.mantle = {
    mode: 'harness',
    on(ch, cb) { (L[ch] || (L[ch] = [])).push(cb); if (ch === 'state') setTimeout(() => cb(STATE), 40); return () => { L[ch] = (L[ch] || []).filter((f) => f !== cb); }; },
    call(ch, p) {
      if (ch === 'sys.stats') return Promise.resolve(STATS);
      if (ch === 'net.probe') return Promise.resolve(PROBE);
      if (ch === 'deskmenu.place') { window.__dmPlace = p; return Promise.resolve({ ok: true }); }
      return Promise.resolve({ ok: true });
    },
  };
})();`;

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, ms) { for (let t = 0; t < ms; t += 100) { if (await fn()) return true; await wait(100); } return false; }

async function shoot(wc, clip, file, label) {
  const img = await wc.capturePage(clip);
  fs.writeFileSync(file, img.toPNG());
  const sz = img.getSize();
  console.log(`ELECTRON-DESK ${label} ${sz.width}x${sz.height} ${file}`);
}

app.whenReady().then(async () => {
  fs.mkdirSync(out, { recursive: true });
  const preload = path.join(out, 'desk-preload.cjs');
  fs.writeFileSync(preload, PRELOAD);
  for (const theme of ['light', 'dark']) {
    const prefs = { offscreen: true, preload, contextIsolation: false, sandbox: false, nodeIntegration: false };
    const win = new BrowserWindow({ show: false, width: 960, height: 540, useContentSize: true, webPreferences: prefs });
    let child = null;
    win.webContents.setWindowOpenHandler(() => ({ action: 'allow', overrideBrowserWindowOptions: { show: false, width: 400, height: 400, useContentSize: true, frame: false, webPreferences: prefs } }));
    win.webContents.on('did-create-window', (w) => { child = w; });
    const url = require('url').pathToFileURL(PAGE).href;
    try { await win.loadURL(url); } catch (e) { await wait(300); await win.loadURL(url); }
    const wc = win.webContents;
    if (await wc.executeJavaScript('Date.now()') !== NOW) throw new Error('the page\'s Date is not frozen');
    await wc.executeJavaScript(`document.documentElement.setAttribute('data-theme', ${JSON.stringify(theme)}); document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; window.dispatchEvent(new Event('mantle-theme')); true`);
    if (!await until(() => wc.executeJavaScript(`!document.getElementById('strip').hidden && document.getElementById('cpu').textContent.endsWith('%')`), 6000)) throw new Error('the strip or the System numbers never drew');
    await wait(900);
    await wc.executeJavaScript(`document.querySelectorAll('*').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
    await wait(200);
    for (const [part, sel] of [['stats', '#stats-tile'], ['strip', '#strip']]) {
      const r = await wc.executeJavaScript(`(() => { const b = document.querySelector(${JSON.stringify(sel)}).getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; })()`);
      const clip = { x: Math.max(0, Math.floor(r.x - 12)), y: Math.max(0, Math.floor(r.y - 12)), width: Math.ceil(r.w + 24), height: Math.ceil(r.h + 24) };
      await shoot(wc, clip, path.join(out, `electron-desk-${theme}-${part}.png`), `${theme} ${part} pane=${Math.round(r.w)}x${Math.round(r.h)}`);
    }
    // the menu: a right-click began (prepare: the page opens its window), then ended (show: it draws and asks for its size)
    await wc.executeJavaScript(`window.__emit('deskmenu.prepare'); true`);
    if (!await until(() => !!child, 3000)) throw new Error('the page opened no menu window');
    await wc.executeJavaScript(`window.__emit('deskmenu.show', { seq: 1 }); true`);
    if (!await until(() => wc.executeJavaScript('!!window.__dmPlace'), 4000)) throw new Error('the menu never asked for its size (deskmenu.place)');
    const place = await wc.executeJavaScript('window.__dmPlace');
    if (place.error) throw new Error('the menu said: ' + place.error);
    child.setContentSize(place.w, place.h);
    await child.webContents.executeJavaScript(`document.documentElement.style.background = ${JSON.stringify(bg)}; document.body.style.background = 'transparent'; true`);
    await wait(900);   // the pop-in
    await child.webContents.executeJavaScript(`document.querySelectorAll('*').forEach((e) => { e.style.animation = 'none'; e.style.transition = 'none'; }); true`);
    await wait(300);
    const [cw, ch] = child.getContentSize();
    await shoot(child.webContents, { x: 0, y: 0, width: cw, height: ch }, path.join(out, `electron-desk-${theme}-menu.png`), `${theme} menu place=${place.w}x${place.h} inset=${place.inset}`);
    child.destroy();
    win.destroy();
  }
  app.quit();
}).catch((e) => { console.error('ELECTRON-DESK failed: ' + (e && e.stack || e)); app.exit(1); });
