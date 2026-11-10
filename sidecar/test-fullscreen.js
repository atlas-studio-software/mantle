// test-fullscreen.js - sidecar/Foreground.cs's ONE definition of full screen (lane mantle-fullscreen, 2026-11-09).
//   MANTLE_SIDECAR_EXE=<exe> node sidecar/test-fullscreen.js
//
// The user: "the top bar doesnt go away for like youtube and robinhood legend if i go fullscreen ...
// and like disney i think most things". Chromium's full screen (F11, a page's element full screen, a
// <webview>'s, synced to its embedder) never restores a maximised window first: it strips WS_CAPTION
// and WS_THICKFRAME, KEEPS WS_MAXIMIZE and moves the window onto rcMonitor. The old rule ("IsZoomed =
// not full screen") said no to all of it whenever the browser had been maximised, which is how the
// user's browser always is (read 2026-11-09: Quartz style 0x15C70000, rect -9,26,1929,1089 on a
// 1920x1080 monitor whose work area starts at 35, IsZoomed true).
//
// PART 1 (always, passive sidecar, no window touched): the rule on numbers, through `foreground`
// {judge}: maximised vs full screen, element full screen from maximised, F11 from restored, a second
// monitor, a borderless window that only fills the work area.
// PART 2 (MANTLE_TEST_VISIBLE=1 only, a real monitor: the cloud runner, or the user idle 3 min): a real
// Electron window with Quartz's own frame (titleBarStyle 'hidden'), maximised, then setFullScreen, then
// a <webview>'s element full screen, read back through the sidecar's own `focus` snapshot.
'use strict';
const path = require('path');
const fs = require('fs');
const os = require('os');
const cp = require('child_process');
const T = require('./test-lib');

const WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
const QUARTZ = 0x15C70000;                                  // the user's maximised Quartz, read live
const QUARTZ_FS = (QUARTZ & ~(WS_CAPTION | WS_THICKFRAME)) >>> 0;   // what Chromium leaves of it in full screen
const MON = [0, 0, 1920, 1080];

(async () => {
  const t = T.checker('FULLSCREEN');
  const s = await T.startPassive();
  try {
    const judge = async (zoomed, style, rect, monitor) => {
      const r = await s.send('foreground', { judge: { zoomed, style, rect, monitor: monitor || MON } }, 4000);
      if (!r.ok || !r.result || r.result.judged !== true) throw new Error('foreground {judge} did not answer: ' + JSON.stringify(r).slice(0, 200));
      return r.result.fullscreen;
    };
    t.check('maximised browser under the strip (the live numbers) is NOT full screen', await judge(true, QUARTZ, [-9, 26, 1929, 1089]) === false);
    t.check('maximised browser with the strip let go and an auto-hide taskbar (its rect overhangs the whole monitor) is NOT full screen',
      await judge(true, QUARTZ, [-9, -9, 1929, 1089]) === false);
    t.check('element / F11 full screen FROM MAXIMISED (WS_MAXIMIZE kept, frame stripped, rcMonitor) IS full screen  <- the bug',
      await judge(true, QUARTZ_FS, [0, 0, 1920, 1080]) === true, `style 0x${QUARTZ_FS.toString(16)}`);
    t.check('full screen from a RESTORED window (not zoomed, frame stripped) is full screen, as before', await judge(false, QUARTZ_FS & ~0x01000000, [0, 0, 1920, 1080]) === true);
    t.check('a borderless game covering the monitor is full screen, as before', await judge(false, 0x16000000, [0, 0, 1920, 1080]) === true);
    t.check('a game a pixel past the edge still covers it', await judge(false, 0x16000000, [-1, -1, 1921, 1081]) === true);
    t.check('a restored window that does not cover the monitor is not', await judge(false, QUARTZ & ~0x01000000, [100, 100, 1200, 800]) === false);
    t.check('a borderless MAXIMISED window that only fills the work area (35 px short) is not', await judge(true, QUARTZ_FS, [0, 35, 1920, 1080]) === false);
    t.check('a maximised window with only a thick frame (no caption) is still "maximised", not full screen', await judge(true, (QUARTZ_FS | WS_THICKFRAME) >>> 0, [-8, -8, 1928, 1088]) === false);
    const MON2 = [1920, 0, 3840, 1080];
    t.check('second monitor: full screen there is judged against THAT monitor', await judge(true, QUARTZ_FS, [1920, 0, 3840, 1080], MON2) === true);
    t.check('second monitor: a window on it does not cover the primary', await judge(true, QUARTZ_FS, [1920, 0, 3840, 1080], MON) === false);
    const bad = await s.send('foreground', { judge: 5 }, 4000);
    t.check('a judge that is not an object is refused with the reason', !bad.ok && /judge must be an object/.test(bad.error || ''), JSON.stringify(bad));
    const plain = await s.send('foreground', {}, 4000);
    t.check('plain foreground still reads the real window', plain.ok && typeof plain.result.fullscreen === 'boolean' && !('judged' in plain.result));

    if (process.env.MANTLE_TEST_VISIBLE === '1') await realChromium(t, s);
    else console.log('FULLSCREEN: part 2 (a real Electron window going full screen on the monitor) NOT RUN: set MANTLE_TEST_VISIBLE=1 on a runner or when the user is away');
  } finally {
    await s.stop();
  }
  process.exit(t.summary());
})().catch((e) => { console.log('FULLSCREEN TEST: 0 passed, 1 failed\n  FAIL threw: ' + (e.stack || e.message)); process.exit(1); });

/** PART 2: a real Chromium full screen, the way Quartz goes there, read by the sidecar. */
async function realChromium(t, s) {
  const root = path.join(__dirname, '..');
  const electron = path.join(root, 'node_modules', 'electron', 'dist', 'electron.exe');
  if (!fs.existsSync(electron)) { t.fail('part 2: no electron at ' + electron); return; }
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-fs-'));
  const script = path.join(dir, 'main.js');
  fs.writeFileSync(script, PROBE_MAIN);
  const kid = cp.spawn(electron, [script, '--user-data-dir=' + path.join(dir, 'ud')], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: false });
  let buf = '';
  const lines = [];
  kid.stdout.on('data', (d) => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { lines.push(buf.slice(0, i).trim()); buf = buf.slice(i + 1); } });
  const waitLine = async (re, ms) => { const end = Date.now() + ms; while (Date.now() < end) { const l = lines.find((x) => re.test(x)); if (l) return l; await T.sleep(50); } return null; };
  const say = (cmd) => kid.stdin.write(cmd + '\n');
  const snap = async () => { const r = await s.send('topbar.focus', {}, 4000); return r.ok ? (r.result.focus || r.result) : { error: r.error }; };
  const settle = async (want, ms) => { const end = Date.now() + ms; let f; do { await T.sleep(100); f = await snap(); } while (Date.now() < end && !want(f)); return f; };
  try {
    const ready = await waitLine(/^READY /, 15000);
    if (!t.check('part 2: the probe window came up', !!ready, lines.slice(-5).join(' | '))) return;
    const hwnd = ready.split(' ')[1];
    say('max');
    await waitLine(/^MAX /, 5000);
    let f = await settle((x) => x && x.hwnd === hwnd && x.zoomed === true, 4000);
    t.check('part 2: the maximised Quartz-framed window is in front, zoomed, and NOT full screen', f.hwnd === hwnd && f.zoomed === true && f.fullscreen === false, JSON.stringify(f).slice(0, 300));
    say('fs on');
    await waitLine(/^FS on/, 5000);
    f = await settle((x) => x && x.hwnd === hwnd && x.fullscreen === true, 4000);
    t.check('part 2: setFullScreen from maximised: Chromium KEEPS WS_MAXIMIZE (zoomed true) and the sidecar now says full screen',
      f.hwnd === hwnd && f.fullscreen === true && f.zoomed === true, JSON.stringify(f).slice(0, 300));
    say('fs off');
    await waitLine(/^FS off/, 5000);
    f = await settle((x) => x && x.hwnd === hwnd && x.fullscreen === false && x.zoomed === true, 4000);
    t.check('part 2: out of full screen: maximised again, not full screen', f.fullscreen === false && f.zoomed === true, JSON.stringify(f).slice(0, 300));
    say('el on');
    const el = await waitLine(/^EL on/, 8000);
    f = await settle((x) => x && x.hwnd === hwnd && x.fullscreen === true, 5000);
    t.check('part 2: a <webview> page\'s ELEMENT full screen (YouTube\'s button, Legend\'s) takes the window full screen and the sidecar says so',
      !!el && f.fullscreen === true, `${el} ${JSON.stringify(f).slice(0, 300)}`);
    say('el off');
    await waitLine(/^EL off/, 8000);
    f = await settle((x) => x && x.hwnd === hwnd && x.fullscreen === false, 5000);
    t.check('part 2: the page leaving element full screen: not full screen again', f.fullscreen === false, JSON.stringify(f).slice(0, 300));
  } finally {
    try { say('quit'); } catch (_) { /* gone */ }
    await T.sleep(800);
    try { kid.kill(); } catch (_) { /* gone */ }
  }
}

// The probe: Quartz's own frame options (titleBarStyle 'hidden' keeps WS_CAPTION | WS_THICKFRAME), a
// <webview> guest like Quartz's tabs; commands on stdin, one line each.
const PROBE_MAIN = String.raw`
const { app, BrowserWindow } = require('electron');
app.whenReady().then(async () => {
  const w = new BrowserWindow({ width: 900, height: 600, x: 60, y: 120, titleBarStyle: 'hidden', show: false,
    title: 'Mantle full-screen probe', webPreferences: { webviewTag: true, contextIsolation: true } });
  await w.loadURL('data:text/html,' + encodeURIComponent('<body style="margin:0;background:#334"><webview id="v" style="position:absolute;inset:0" src="data:text/html,<body style=background:#a55>guest</body>"></webview></body>'));
  await w.webContents.executeJavaScript('new Promise((r) => { const v = document.getElementById("v"); if (v.getWebContentsId) { try { v.getWebContentsId(); return r(1); } catch (e) {} } v.addEventListener("dom-ready", () => r(1), { once: true }); })');
  w.show(); w.focus();
  const hwnd = w.getNativeWindowHandle().readBigUInt64LE(0).toString();
  console.log('READY ' + hwnd);
  const guest = () => w.webContents.executeJavaScript('document.getElementById("v").getWebContentsId()').then((id) => require('electron').webContents.fromId(id));
  let buf = '';
  process.stdin.on('data', async (d) => {
    buf += d; let i;
    while ((i = buf.indexOf('\n')) >= 0) {
      const cmd = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
      try {
        if (cmd === 'max') { w.maximize(); setTimeout(() => console.log('MAX ' + w.isMaximized()), 500); }
        else if (cmd === 'fs on') { w.setFullScreen(true); setTimeout(() => console.log('FS on ' + w.isFullScreen()), 700); }
        else if (cmd === 'fs off') { w.setFullScreen(false); setTimeout(() => console.log('FS off ' + w.isFullScreen()), 700); }
        else if (cmd === 'el on') { const g = await guest(); await g.executeJavaScript('document.body.requestFullscreen().then(() => "ok", (e) => "refused " + e.message)', true).then((r) => setTimeout(() => console.log('EL on ' + r + ' ' + w.isFullScreen()), 900)); }
        else if (cmd === 'el off') { const g = await guest(); await g.executeJavaScript('document.exitFullscreen().then(() => "ok", (e) => "refused " + e.message)', true).then((r) => setTimeout(() => console.log('EL off ' + r + ' ' + w.isFullScreen()), 900)); }
        else if (cmd === 'quit') app.quit();
      } catch (e) { console.log('ERR ' + cmd + ' ' + e.message); }
    }
  });
});
`;
