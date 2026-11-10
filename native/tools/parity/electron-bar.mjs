// electron-bar.mjs - run Electron Mantle as a VISIBLE --test instance on a CI runner, raise its bar the
// way the hover reveal does, and report where its pill and clock are on screen. Used by run-parity.ps1.
//
//   node native/tools/parity/electron-bar.mjs --root=<repo> --theme=light|dark --out=<file.json> [--hold=<s>]
//
// RUNNER ONLY (refuses unless CI / GITHUB_ACTIONS is set, or MANTLE_PARITY_LOCAL=1): the test window is
// made visible (MANTLE_TEST_VISIBLE=1), which must never happen on the user's own screen.
// The instance is a normal Mantle --test instance (its own profile, the sidecar in --passive, nothing on
// the shell touched), with --test-live-bar so the bar is the live kind (frameless, transparent, placed by
// the sidecar), --no-desktop so no window covers the wallpaper, and MANTLE_BAR_TEST_CURSOR=1 so the hover
// poll never reads the real cursor; bar.testMove {up:true} raises it.
// Writes <out> once the bar is up, then keeps running (holding the CDP socket) until Electron exits,
// --hold seconds pass (default 180), or it is killed. Exit code 0 = reported, 2 = refused, 3 = failed.
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const args = {};
for (const a of process.argv.slice(2)) {
  const m = /^--([^=]+)(?:=(.*))?$/.exec(a);
  if (m) args[m[1]] = m[2] === undefined ? true : m[2];
}
const root = path.resolve(String(args.root || '.'));
const theme = args.theme === 'dark' ? 'dark' : 'light';
const out = args.out ? path.resolve(String(args.out)) : path.join(root, 'native', 'out', 'parity', `electron-${theme}.json`);
const profile = String(args.profile || `parity-${theme}`);
const holdMs = 1000 * (Number(args.hold) || 180);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const say = (m) => process.stdout.write(`electron-bar ${theme}: ${m}\n`);
function fail(code, why) {
  say(`FAILED: ${why}`);
  try { fs.writeFileSync(out, JSON.stringify({ ok: false, why }, null, 2)); } catch (_) { /* best effort */ }
  process.exit(code);
}

if (!process.env.CI && !process.env.GITHUB_ACTIONS && process.env.MANTLE_PARITY_LOCAL !== '1') {
  fail(2, 'refused: not on a CI runner (set MANTLE_PARITY_LOCAL=1 only on a machine nobody is using: the test bar is made visible)');
}

const req = createRequire(path.join(root, 'package.json'));
let electronExe;
try { electronExe = req('electron'); } catch (e) { fail(3, `electron is not installed in ${root} (npm install first): ${e.message}`); }
const userData = path.join(os.tmpdir(), `mantle-test-${profile}`);
const portFile = path.join(userData, 'DevToolsActivePort');
try { fs.unlinkSync(portFile); } catch (_) { /* none yet */ }

const env = { ...process.env, MANTLE_TEST_VISIBLE: '1', MANTLE_BAR_TEST_CURSOR: '1' };
const argv = ['.', '--test', '--test-live-bar', `--profile-name=${profile}`, `--theme=${theme}`, '--no-desktop', '--remote-debugging-port=0'];
const logPath = out.replace(/\.json$/i, '') + '.log';
const log = fs.createWriteStream(logPath);
const child = spawn(electronExe, argv, { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
child.stdout.pipe(log, { end: false });
child.stderr.pipe(log, { end: false });
let exited = null;
child.on('exit', (code) => { exited = { code }; });
say(`started pid ${child.pid}: ${path.basename(electronExe)} ${argv.join(' ')}`);

async function until(fn, ms, what) {
  const end = Date.now() + ms;
  for (;;) {
    if (exited) fail(3, `Electron exited (code ${exited.code}) while waiting for ${what}; see ${path.basename(logPath)}`);
    try { const v = await fn(); if (v) return v; } catch (_) { /* not yet */ }
    if (Date.now() > end) fail(3, `timed out after ${ms} ms waiting for ${what}`);
    await sleep(250);
  }
}

const port = await until(() => {
  const t = fs.readFileSync(portFile, 'utf8').split(/\r?\n/)[0].trim();
  return /^\d+$/.test(t) ? Number(t) : null;
}, 90000, 'DevToolsActivePort');
const target = await until(async () => {
  const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  return list.find((t) => t.type === 'page' && /\/ui\/bar\/index\.html/.test(t.url));
}, 30000, 'the bar page');

const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); });
let n = 0;
const pend = new Map();
ws.addEventListener('message', (m) => {
  const j = JSON.parse(m.data);
  if (j.id && pend.has(j.id)) { const p = pend.get(j.id); pend.delete(j.id); if (j.error) p.rej(new Error(j.error.message)); else p.res(j.result); }
});
const send = (method, params) => new Promise((res, rej) => { const i = ++n; pend.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params: params || {} })); });
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text || 'page threw');
  return r.result.value;
};
const call = (cmd, arg) => ev(`window.mantle.call(${JSON.stringify(cmd)}, ${JSON.stringify(arg || {})}).then((r) => r, (e) => ({ ok: false, threw: String(e && e.message || e) }))`);

await until(() => ev('!!(window.mantle && window.mantle.call)'), 20000, 'window.mantle.call');
const moved = await call('bar.testMove', { up: true });
say(`bar.testMove up -> ${JSON.stringify(moved).slice(0, 200)}`);
await sleep(1500);
const state = await until(async () => {
  const s = await call('bar.testCursor', {});
  return s && s.ok && s.rect ? s : null;
}, 15000, 'bar.testCursor to report the bar rect');
// The tray readings. bar.js draws "not read yet" first (#network data-state "unknown": a dimmed globe; #battery
// data-state "loading": a dash) and the sidecar's first 'net' / 'battery' pushes replace it. A shot before them
// holds the globe and the dash where native (whose wired glyph and battery slot are drawn from the first frame,
// BarWindow.xaml.cs BuildGlyphs) has its real cluster ~30 px further right: staging run 42785589408, bar light
// 0.9784 vs 0.9966 when both had readings. So wait, max 30 s, until the network has a reading, the battery shows
// no loading dash (#battery starts hidden and a machine with no battery hides it again, so hidden alone cannot
// say it was read), and that cluster has then held for 1.5 s. An "error" is not a reading here: run 42792223889's
// first battery read timed out ("the sidecar did not answer within 3s") while the sidecar started, and main
// replaces that dash with the real reading once the sidecar's watch is armed (main.js sidecar.on('ready')).
const readState = () => ev(`(() => {
  const n = document.getElementById('network'), b = document.getElementById('battery');
  return { net: n ? n.getAttribute('data-state') : null, battery: !b ? null : b.hidden ? 'hidden' : b.getAttribute('data-state'),
    key: [n && n.getAttribute('data-state'), n && n.innerHTML.length, b && b.hidden, b && b.getAttribute('data-state'), b && b.textContent].join('|') };
})()`);
const settled = (r) => r.net !== null && r.net !== 'unknown' && r.net !== 'error' && r.battery !== null && r.battery !== 'loading' && r.battery !== 'error';
const readT0 = Date.now();
let readings = await readState(), heldSince = settled(readings) ? Date.now() : 0;
while (!exited && Date.now() - readT0 < 30000 && !(heldSince && Date.now() - heldSince >= 1500)) {
  await sleep(250);
  const r = await readState();
  if (!settled(r)) heldSince = 0;
  else if (!heldSince || r.key !== readings.key) heldSince = Date.now();
  readings = r;
}
readings.ready = settled(readings) && !!heldSince && Date.now() - heldSince >= 1500;
readings.waitedMs = Date.now() - readT0;
delete readings.key;
say(`tray readings: ${JSON.stringify(readings)}`);
const geo = await ev(`(() => {
  const r = (el) => { if (!el) return null; const b = el.getBoundingClientRect(); return [b.left, b.top, b.width, b.height]; };
  return { dpr: window.devicePixelRatio, inner: [innerWidth, innerHeight], pill: r(document.getElementById('bar')),
    clock: r(document.getElementById('clock')), html: document.documentElement.className,
    theme: document.documentElement.dataset ? Object.assign({}, document.documentElement.dataset) : null };
})()`);
// Screen rects in DIP: the window's own rect (main's getBounds) + the element's rect in the page.
const toScreen = (e) => e && [state.rect.x + e[0], state.rect.y + e[1], e[2], e[3]];
const report = {
  ok: true, pid: child.pid, profile, theme, electronExe, argv, userData,
  windowRect: state.rect, rest: state.rest, up: state.up, revealed: state.revealed, screen: state.screen,
  dpr: geo.dpr, pageInner: geo.inner, html: geo.html, dataset: geo.theme,
  pillDip: toScreen(geo.pill), clockDip: toScreen(geo.clock), readings,
};
fs.writeFileSync(out, JSON.stringify(report, null, 2));
say(`reported: pill ${JSON.stringify(report.pillDip)} clock ${JSON.stringify(report.clockDip)} dpr ${geo.dpr}`);

const end = Date.now() + holdMs;
while (!exited && Date.now() < end) await sleep(500);
try { ws.close(); } catch (_) { /* closing */ }
say(exited ? `Electron exited (code ${exited.code})` : 'hold time over; leaving Electron to the caller');
process.exit(0);
