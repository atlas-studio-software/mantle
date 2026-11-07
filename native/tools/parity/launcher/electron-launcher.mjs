// electron-launcher.mjs - run Electron Mantle as a VISIBLE --test instance on a CI runner, open its launcher with a
// query typed, and measure open-to-first-frame and keystroke-to-rows in the launcher page. Used by run-launcher.ps1.
//
//   node native/tools/parity/launcher/electron-launcher.mjs --root=<repo> --theme=light|dark --fixture=<dir>
//        --out=<file.json> [--query=steam] [--opens=5] [--hold=<s>]
//
// RUNNER ONLY (refuses unless CI / GITHUB_ACTIONS is set, or MANTLE_PARITY_LOCAL=1): the test windows are visible.
// The index reads the scratch fixture make-golden.cjs --fixture wrote: ProgramData, APPDATA, USERPROFILE and HOME
// point into it (as make-golden does), so both apps list the same Start Menu. Store apps and powercfg are the runner's.
// Timing, in the launcher page (performance.now): an open = mantle.call('launcher.show') -> the first animation frame
// after #results changed once launcher:visible arrived; a keystroke = the input event -> the first frame after #results
// changed. Writes <out> with the numbers and the launcher's screen rect, then holds (CDP open) until killed or --hold.
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
const fixture = path.resolve(String(args.fixture || ''));
const query = String(args.query || 'steam');
const opens = Math.max(1, Number(args.opens) || 5);
const out = path.resolve(String(args.out || path.join(root, 'native', 'out', 'parity', 'launcher', `electron-${theme}.json`)));
const profile = String(args.profile || `parity-launcher-${theme}`);
const holdMs = 1000 * (Number(args.hold) || 240);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const say = (m) => process.stdout.write(`electron-launcher ${theme}: ${m}\n`);
function fail(code, why) {
  say(`FAILED: ${why}`);
  try { fs.writeFileSync(out, JSON.stringify({ ok: false, why }, null, 2)); } catch (_) { /* best effort */ }
  process.exit(code);
}

if (!process.env.CI && !process.env.GITHUB_ACTIONS && process.env.MANTLE_PARITY_LOCAL !== '1') {
  fail(2, 'refused: not on a CI runner (the test launcher is made visible)');
}
if (!fs.existsSync(path.join(fixture, 'pd'))) fail(3, `no fixture at ${fixture} (node native/tools/parity/launcher/make-golden.cjs --fixture=<dir>)`);

const req = createRequire(path.join(root, 'package.json'));
let electronExe;
try { electronExe = req('electron'); } catch (e) { fail(3, `electron is not installed in ${root} (npm install first): ${e.message}`); }
const userData = path.join(os.tmpdir(), `mantle-test-${profile}`);
const portFile = path.join(userData, 'DevToolsActivePort');
try { fs.unlinkSync(portFile); } catch (_) { /* none yet */ }

const home = path.join(fixture, 'home');
const env = { ...process.env, MANTLE_TEST_VISIBLE: '1', MANTLE_BAR_TEST_CURSOR: '1',
  ProgramData: path.join(fixture, 'pd'), APPDATA: path.join(home, 'AppData', 'Roaming'), USERPROFILE: home, HOME: home };
for (const k of ['OneDrive', 'OneDriveConsumer', 'OneDriveCommercial']) delete env[k];
const argv = ['.', '--test', `--profile-name=${profile}`, `--theme=${theme}`, '--no-desktop', '--remote-debugging-port=0'];
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
    await sleep(200);
  }
}

const port = await until(() => {
  const t = fs.readFileSync(portFile, 'utf8').split(/\r?\n/)[0].trim();
  return /^\d+$/.test(t) ? Number(t) : null;
}, 90000, 'DevToolsActivePort');
const target = await until(async () => {
  const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  return list.find((t) => t.type === 'page' && /\/ui\/launcher\/index\.html/.test(t.url));
}, 45000, 'the launcher page');

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
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ' ' + (r.exceptionDetails.exception && r.exceptionDetails.exception.description || ''));
  return r.result.value;
};

await until(() => ev('!!(window.mantle && window.mantle.call && window.mantle.on && document.getElementById("results"))'), 20000, 'window.mantle in the launcher page');
// the hooks: one MutationObserver on #results, one launcher:visible listener; each measure ends at the next animation frame
await ev(`(() => {
  const q = document.getElementById('q'), res = document.getElementById('results');
  const L = window.__parity = { opens: [], keys: [], open: null, key: null };
  const frame = (rec, list) => requestAnimationFrame(() => { rec.t1 = performance.now(); list.push(Math.round((rec.t1 - rec.t0) * 10) / 10); });
  new MutationObserver(() => {
    const o = L.open; if (o && o.visible && !o.done) { o.done = true; frame(o, L.opens); }
    const k = L.key; if (k && !k.done) { k.done = true; frame(k, L.keys); }
  }).observe(res, { childList: true, subtree: true, characterData: true });
  window.mantle.on('launcher:visible', (v) => { if (v && L.open && !L.open.visible) L.open.visible = performance.now(); });
  L.show = () => { L.open = { t0: performance.now() }; return window.mantle.call('launcher.show'); };
  L.hide = () => window.mantle.call('launcher.hide');
  L.type = (text) => { L.key = { t0: performance.now(), text }; q.value = text; q.dispatchEvent(new Event('input', { bubbles: true })); };
  return true;
})()`);

// let the index build (the first open waits on it otherwise)
await sleep(3000);
for (let i = 0; i < opens; i++) {
  const before = await ev('window.__parity.opens.length');
  await ev('window.__parity.show()');
  await until(() => ev(`window.__parity.opens.length > ${before}`), 8000, `open ${i + 1}'s first frame with rows`);
  await sleep(400);
  await ev('window.__parity.hide()');
  await sleep(600);
}
// the shot: open, then the query typed one letter at a time (each a keystroke measure)
{
  const before = await ev('window.__parity.opens.length');
  await ev('window.__parity.show()');
  await until(() => ev(`window.__parity.opens.length > ${before}`), 8000, 'the shot open');
  await sleep(500);
}
for (let i = 1; i <= query.length; i++) {
  const before = await ev('window.__parity.keys.length');
  await ev(`window.__parity.type(${JSON.stringify(query.slice(0, i))})`);
  await until(() => ev(`window.__parity.keys.length > ${before}`), 5000, `rows for "${query.slice(0, i)}"`);
  await sleep(350);
}
await sleep(800);
const page = await ev(`(() => ({
  dpr: devicePixelRatio, rect: [screenX, screenY, outerWidth, outerHeight], inner: [innerWidth, innerHeight],
  theme: document.documentElement.dataset.theme || null, q: document.getElementById('q').value,
  focused: document.hasFocus(), active: document.activeElement && document.activeElement.id,
  rows: Array.from(document.querySelectorAll('#results .row .name')).map((e) => e.textContent),
  groups: Array.from(document.querySelectorAll('#results .grp')).map((e) => e.textContent),
  hint: document.getElementById('hint').textContent,
  opens: window.__parity.opens, keys: window.__parity.keys,
}))()`);
const report = { ok: true, pid: child.pid, profile, theme, electronExe, argv, userData, query, ...page };
fs.writeFileSync(out, JSON.stringify(report, null, 2));
say(`reported: rect ${JSON.stringify(page.rect)} dpr ${page.dpr}; rows ${JSON.stringify(page.rows)}; opens ${JSON.stringify(page.opens)} keys ${JSON.stringify(page.keys)}`);

const end = Date.now() + holdMs;
while (!exited && Date.now() < end) await sleep(500);
try { ws.close(); } catch (_) { /* closing */ }
say(exited ? `Electron exited (code ${exited.code})` : 'hold time over; leaving Electron to the caller');
process.exit(0);
