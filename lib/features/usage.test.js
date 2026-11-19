/* usage.test.js - node lib/features/usage.test.js (lane mantle-ps-node, 2026-11-18).
 * The main half of "Power use and screen time" over a FAKE ctx and a scripted sidecar: what reaches the
 * sidecar (the watch, re-sent after a restart), screen time through the test hooks (every pause rule,
 * the Mantle-window rule, the sleep gap), the off states and their words, the refused-sidecar health,
 * power.now / power.history reply shapes, the files on disk (never a title), a damaged file, icons, cost.
 * Local times are built with new Date(y, m, d, h, min) so it passes in any time zone. Pure node. */
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const F = require('./usage.js');
const { validate } = require('../features.js');

let passed = 0;
const failures = [];
function check(name, ok, detail) { if (ok) passed++; else failures.push(detail !== undefined ? name + ' (' + JSON.stringify(detail) + ')' : name); }
const near = (a, b, eps) => typeof a === 'number' && Math.abs(a - b) <= (eps === undefined ? 0.001 : eps);
const keysOf = (o) => Object.keys(o || {}).sort().join(',');
const same = (o, list) => keysOf(o) === list.slice().sort().join(',');
const flushQ = async () => { for (let i = 0; i < 6; i++) await new Promise((r) => setImmediate(r)); };

const MIN = 60000;
const T0 = new Date(2026, 10, 18, 10, 0).getTime();     // 10:00 local
const dk = (t) => { const d = new Date(t); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };

const CHROME = { pid: 100, created: '5', file: 'chrome.exe', path: 'C:\\Apps\\chrome.exe', desc: 'Google Chrome' };
const CODE = { pid: 200, created: '7', file: 'Code.exe', path: 'C:\\Apps\\Code.exe', desc: 'Visual Studio Code' };
const CHROME_KEY = 'chrome.exe', CODE_KEY = 'code.exe';   // the exe FILE name, never the path
const fg = (app, at, extra) => Object.assign({ kind: 'fg', hwnd: String(5000 + app.pid) }, app, { at }, extra || {});

/* ---- fake samples (the wire spec's usage.sample) ------------------------------------------ */
const PROCS = [
  { pid: 4, ppid: 0, file: 'System', created: '1', session: 0, path: null, desc: null, windows: 0 },
  { pid: 300, ppid: 4, file: 'svchost.exe', created: '2', session: 0, path: 'C:\\Windows\\System32\\svchost.exe', desc: 'Host Process', windows: 0 },
  { pid: 900, ppid: 1, file: 'electron.exe', created: '3', session: 1, path: 'C:\\mantle\\electron.exe', desc: 'Electron', windows: 2 },
  { pid: 901, ppid: 900, file: 'Mantle.Sidecar.exe', created: '4', session: 1, path: 'C:\\mantle\\Mantle.Sidecar.exe', desc: null, windows: 0 },
  { pid: 100, ppid: 50, file: 'chrome.exe', created: '5', session: 1, path: 'C:\\Apps\\chrome.exe', desc: 'Google Chrome', windows: 1 },
  { pid: 101, ppid: 100, file: 'chrome.exe', created: '6', session: 1, path: 'C:\\Apps\\chrome.exe', desc: 'Google Chrome', windows: 0 },
  { pid: 200, ppid: 50, file: 'Code.exe', created: '7', session: 1, path: 'C:\\Apps\\Code.exe', desc: 'Visual Studio Code', windows: 1 },
];
const pwh = (w, s) => (w * s) / 3.6e-9;   // W for s seconds -> picowatt-hours
const AC = () => ({ ok: true, present: true, percent: 80, ac: true, charging: true, secondsLeft: null, capacity: { remaining: 40000, full: 50000, rate: 5000 }, reason: null });
const BAT = (rem, rate) => ({ ok: true, present: true, percent: 50, ac: false, charging: false, secondsLeft: 3600, capacity: { remaining: rem, full: 50000, rate }, reason: null });
function sample(o) {
  return {
    t: o.t, cpus: 4, idle: 0,
    procs: PROCS.map((p) => Object.assign({}, p, { cpu: (o.cpu && o.cpu[p.pid]) || 0 })),
    selfPid: 901, parentPid: 900,
    energy: o.noRapl ? { ok: false, time: null, pkg: null, cores: null, gfx: null, dram: null, reason: 'This PC has no energy meter Windows can read' }
      : { ok: true, time: o.etime, pkg: o.pkg, cores: o.cores, gfx: o.gfx, dram: 0, reason: null },
    meter: { ok: true, mw: o.meter || 0, reason: null },
    gpu: { ok: true, byPid: o.gpu || {}, reason: null },
    battery: o.battery || AC(),
    ms: 20, cpuMs: 5,
  };
}
// s0 -> s1 over 1 s: pkg 10 W, cores 6 W, gfx 2 W; CPU: chrome 0.6 (100 + 101), code 0.2, Windows 0.2; GPU chrome 0.75, code 0.25
const E0 = { pkg: pwh(10, 100), cores: pwh(6, 100), gfx: pwh(2, 100) };
function pair(t, battery0, battery1, extra) {
  const s0 = sample(Object.assign({ t, etime: 1000, pkg: E0.pkg, cores: E0.cores, gfx: E0.gfx, gpu: { 100: 0, 200: 0 }, battery: battery0 }, extra || {}));
  const s1 = sample(Object.assign({ t: t + 1000, etime: 2000, pkg: E0.pkg + pwh(10, 1), cores: E0.cores + pwh(6, 1), gfx: E0.gfx + pwh(2, 1),
    cpu: { 100: 2000000, 101: 1000000, 200: 1000000, 4: 500000, 300: 500000 }, gpu: { 100: 300, 200: 100 }, battery: battery1 }, extra || {}));
  return [s0, s1];
}

/* ---- the fake ctx ---------------------------------------------------------------------------- */
const dirs = [];
const instances = [];
const allLogs = [];
function make(o) {
  const opts = o || {};
  const dir = opts.dir || fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-usage-unit-'));
  if (!dirs.includes(dir)) dirs.push(dir);
  const f = {
    // most checks run with screen time switched on; `fresh` = a first run (nothing set: screen time off)
    dir, settings: Object.assign(opts.fresh ? {} : { screentime: true }, opts.settings || {}), sent: [], logs: [], cmds: {}, handlers: {}, health: [],
    readyFns: [], enabledFns: [], optionFns: [], enabled: opts.enabled !== false, fgNow: { kind: 'shell' }, fail: opts.fail || {}, iconCalls: [],
  };
  const sidecar = {
    ready: true,
    async send(cmd, args) {
      f.sent.push({ cmd, args: JSON.parse(JSON.stringify(args || {})) });
      if (f.fail[cmd]) return { ok: false, error: f.fail[cmd] };
      if (cmd === 'usage.watch') return args && args.op === 'status'
        ? { ok: true, result: { watching: true, events: 3, cpuMs: 12, since: 0, displayHeldReadable: true, reason: null } }
        : { ok: true, result: { ok: true, watching: !!(args && args.on), reason: (args && args.on && opts.watchReason) || null } };
      if (cmd === 'usage.fg') return { ok: true, result: Object.assign({ idleMs: 0 }, f.fgNow) };
      if (cmd === 'usage.sample') return { ok: false, error: 'no real readings in this test' };
      if (cmd === 'icon') { f.iconCalls.push(args.path); return { ok: true, result: { icon: /none/i.test(args.path) ? null : /huge/i.test(args.path) ? 'data:image/png;base64,' + 'A'.repeat(30000) : 'data:image/png;base64,QUJD' } }; }
      return { ok: false, error: 'unknown command: ' + cmd };
    },
    on(ev, fn) { (f.handlers[ev] = f.handlers[ev] || []).push(fn); return () => {}; },
    onReady(fn) { f.readyFns.push(fn); if (sidecar.ready) fn(); return () => {}; },
  };
  const ctx = {
    name: 'usage',
    flags: Object.freeze({ test: opts.test !== false, live: false }),
    enabled: () => f.enabled,
    log: (s) => { f.logs.push(s); allLogs.push(s); },
    health: (ok, why) => f.health.push({ ok, why }),
    command: (verb, fn) => { f.cmds[verb] = fn; return 'usage.' + verb; },
    settings: {
      get: (k, d) => (f.settings[k] === undefined ? d : JSON.parse(JSON.stringify(f.settings[k]))),
      set: (k, v) => { f.settings[k] = v; return v; },
      file: path.join(dir, 'features', 'usage.json'),
    },
    onEnabled: (fn) => { f.enabledFns.push(fn); return () => {}; },
    onOption: (k, fn) => { f.optionFns.push(fn); return () => {}; },
    host: { features: { setOption(name, key, value) { if (value === null) delete f.settings[key]; else f.settings[key] = value; for (const fn of f.optionFns) fn(value, key); return { ok: true, reason: null, option: { key, value } }; } } },
    sidecar,
  };
  const h = F.install(ctx);
  f.stopped = false;
  f.stop = () => { if (!f.stopped) { f.stopped = true; h.stop('test'); } };
  f.call = (verb, args) => f.cmds[verb](args);
  f.ready = () => f.readyFns.forEach((fn) => fn());
  f.setEnabled = (on) => { f.enabled = on; f.enabledFns.forEach((fn) => fn(on)); };
  f.setOption = (key, value) => ctx.host.features.setOption('usage', key, value);
  f.watches = () => f.sent.filter((s) => s.cmd === 'usage.watch' && !(s.args && s.args.op));
  f.event = (ev) => (f.handlers.usage || []).forEach((fn) => fn(Object.assign({ ev: 'usage' }, ev)));
  instances.push(f);
  return f;
}
const at = (f, t) => f.call('test.clock', { now: t });
const inject = (f, ...events) => f.call('test.inject', { events });
const get = (f) => f.call('screentime.get');
const secOf = (r, key) => { const a = r.today.apps.find((x) => x.key === key); return a ? a.seconds : 0; };

// usage.js's timers are unref'd (right for Mantle's main process); this keeps a pure-node run alive through them
const keepAlive = setInterval(() => {}, 1000);

(async () => {
  /* ---- 0. the module contract ---------------------------------------------------------------- */
  check('validate(): the module passes the feature contract', validate(F, 'usage') === null, validate(F, 'usage'));
  const O = Object.fromEntries(F.options.map((x) => [x.key, x]));
  check('options: power bool default true', O.power && O.power.type === 'bool' && O.power.default === true, O.power);
  check('options: screentime bool default FALSE (off until the user turns it on)', O.screentime && O.screentime.type === 'bool' && O.screentime.default === false, O.screentime);
  check('options: idleMin number 1-60 default 5 unit min', O.idleMin && O.idleMin.type === 'number' && O.idleMin.min === 1 && O.idleMin.max === 60 && O.idleMin.default === 5 && O.idleMin.unit === 'min', O.idleMin);
  check('module: label, defaultOn true, never rests', F.label === 'Power use and screen time' && F.defaultOn === true && F.rest === undefined, { label: F.label, rest: F.rest });

  /* ---- 1. the watch at install, after a sidecar restart, on an idleMin change ------------------ */
  let f = make();
  await flushQ();
  let w = f.watches();
  check('install: usage.watch {on:true, idleMs:300000} sent', w.length === 1 && w[0].args.on === true && w[0].args.idleMs === 300000, w);
  check('install: usage.fg asked once', f.sent.filter((s) => s.cmd === 'usage.fg').length === 1, f.sent.map((s) => s.cmd));
  f.ready(); await flushQ();
  w = f.watches();
  check('sidecar restart: the watch is sent again', w.length === 2 && w[1].args.on === true && w[1].args.idleMs === 300000, w);
  check('sidecar restart: usage.fg asked again', f.sent.filter((s) => s.cmd === 'usage.fg').length === 2, f.sent.map((s) => s.cmd));
  f.setOption('idleMin', 10); await flushQ();
  w = f.watches();
  check('idleMin 10: the watch is re-sent with idleMs 600000', w.length === 3 && w[2].args.on === true && w[2].args.idleMs === 600000, w);
  check('test hooks are registered in a test run', ['test.clock', 'test.inject', 'test.sample'].every((k) => typeof f.cmds[k] === 'function'));
  f.stop();
  const live = make({ test: false });
  check('a live run has no test hooks', !live.cmds['test.clock'] && !live.cmds['test.inject'] && !live.cmds['test.sample'], Object.keys(live.cmds));
  live.stop();

  /* ---- 2. screen time: every pause rule, through the test hooks -------------------------------- */
  f = make();
  await flushQ();
  await at(f, T0);
  await inject(f, fg(CHROME, T0));
  let r = await inject(f, { kind: 'idle', idleMs: 5 * MIN, at: T0 + 20 * MIN });
  r = await get(f);
  check('idle: counting stopped at now - idleMs (15 min of 20)', secOf(r, CHROME_KEY) === 900, r.today);
  check('idle: paused idle, the app in front still named', r.paused === 'idle' && r.current && r.current.key === CHROME_KEY && r.current.name === 'Google Chrome', { paused: r.paused, current: r.current });
  await inject(f, { kind: 'active', agoMs: MIN, at: T0 + 30 * MIN });
  await at(f, T0 + 31 * MIN);
  r = await get(f);
  check('active: counting restarted at now - agoMs (+2 min)', secOf(r, CHROME_KEY) === 1020 && r.paused === null, { s: secOf(r, CHROME_KEY), paused: r.paused });
  check('since = when the running interval began', r.since === T0 + 29 * MIN, r.since);
  f.fgNow = Object.assign({}, CHROME);            // what usage.fg says after unlock / resume
  await inject(f, { kind: 'lock', at: T0 + 31 * MIN });
  await at(f, T0 + 39 * MIN);
  r = await get(f);
  check('lock: paused locked, nothing counted while locked', r.paused === 'locked' && secOf(r, CHROME_KEY) === 1020 && r.current === null, { paused: r.paused, s: secOf(r, CHROME_KEY), current: r.current });
  await inject(f, { kind: 'unlock', at: T0 + 40 * MIN });
  await flushQ();
  check('unlock: usage.fg asked again', f.sent.filter((s) => s.cmd === 'usage.fg').length >= 2);
  await inject(f, { kind: 'suspend', at: T0 + 45 * MIN });
  await at(f, T0 + 48 * MIN);
  r = await get(f);
  check('suspend: paused asleep, 5 min counted after the unlock', r.paused === 'asleep' && secOf(r, CHROME_KEY) === 1320, { paused: r.paused, s: secOf(r, CHROME_KEY) });
  await inject(f, { kind: 'resume', at: T0 + 50 * MIN });
  await flushQ();
  await at(f, T0 + 55 * MIN);
  r = await get(f);
  check('resume: counting again from the resume', r.paused === null && secOf(r, CHROME_KEY) === 1620, { paused: r.paused, s: secOf(r, CHROME_KEY) });
  await inject(f, { kind: 'shell', at: T0 + 55 * MIN }, fg(CODE, T0 + 56 * MIN));
  await at(f, T0 + 58 * MIN);
  r = await get(f);
  check('shell: nothing counted while the desktop is in front; then Code counts', secOf(r, CHROME_KEY) === 1620 && secOf(r, CODE_KEY) === 120 && r.today.totalSec === 1740, r.today.apps);
  check('today: apps sorted by seconds, with name and the stored icon, no path', r.today.apps[0].key === CHROME_KEY && r.today.apps[0].icon === 'data:image/png;base64,QUJD' && !('path' in r.today.apps[0]) && r.today.apps[1].name === 'Visual Studio Code', r.today.apps);
  check('today: hours[10] holds it all, 24 hours', r.today.hours.length === 24 && r.today.hours[10] === 1740 && r.today.date === dk(T0), r.today.hours);
  check('days: 7, oldest first, today last', r.days.length === 7 && r.days[6].date === dk(T0) && r.days[0].date === dk(new Date(2026, 10, 12, 12).getTime()) && r.days[6].totalSec === 1740 && r.days[0].totalSec === 0, r.days.map((d) => d.date));
  check('screentime.get: the reply fields', same(r, ['ok', 'on', 'idleMin', 'keptDays', 'today', 'days', 'since', 'current', 'paused', 'notes']) && r.keptDays === 30 && r.on === true && r.idleMin === 5, keysOf(r));
  check('today / days entries: their fields', same(r.today, ['date', 'totalSec', 'hours', 'apps']) && same(r.days[6], ['date', 'totalSec', 'apps']) && same(r.today.apps[0], ['key', 'name', 'icon', 'seconds']));
  f.stop();

  // an interval across 11:00 is split on the hour
  f = make();
  await at(f, T0 + 50 * MIN);
  await inject(f, fg(CHROME, T0 + 50 * MIN));
  await at(f, T0 + 70 * MIN);
  r = await get(f);
  check('hour split: 10:50-11:10 = 600 s in hour 10 and 600 s in hour 11', r.today.hours[10] === 600 && r.today.hours[11] === 600 && r.today.totalSec === 1200, r.today.hours.slice(9, 13));
  f.stop();

  /* ---- 3. Mantle's own windows ---------------------------------------------------------------- */
  F._setWindowsSource(() => [{ hwnd: '111', focusable: true, onTop: false }, { hwnd: '222', focusable: true, onTop: true }, { hwnd: '333', focusable: false, onTop: false }]);
  f = make();
  await at(f, T0);
  const MANTLE_EV = { pid: 900, created: '3', file: 'electron.exe', path: 'C:\\mantle\\electron.exe', desc: 'Electron' };
  await inject(f, Object.assign({ kind: 'fg', hwnd: '111', at: T0 }, MANTLE_EV));
  r = await inject(f, Object.assign({ kind: 'fg', hwnd: 222, at: T0 + MIN }, MANTLE_EV));
  check('an always-on-top Mantle window is the shell (nothing in front)', r.current === null && r.counting === false, r);
  r = await inject(f, Object.assign({ kind: 'fg', hwnd: '333', at: T0 + 2 * MIN }, MANTLE_EV));
  check('a non-focusable Mantle window is the shell', r.current === null && r.counting === false, r);
  await inject(f, Object.assign({ kind: 'fg', hwnd: '111', at: T0 + 3 * MIN }, MANTLE_EV));
  r = await inject(f, { kind: 'fg', hwnd: '444', pid: process.pid, file: 'electron.exe', path: 'C:\\mantle\\electron.exe', at: T0 + 4 * MIN });
  check("a window of Mantle's process that is none of its BrowserWindows is the shell", r.current === null && r.counting === false, r);
  await at(f, T0 + 5 * MIN);
  r = await get(f);
  const m = r.today.apps.find((a) => a.key === 'mantle');
  check('a focusable, not-on-top Mantle window counts as the app "Mantle"', m && m.name === 'Mantle' && m.seconds === 120 && r.today.totalSec === 120, r.today.apps);
  check('no electron.exe row: Mantle is never counted under its exe', !r.today.apps.some((a) => /electron/.test(a.key)), r.today.apps);
  f.stop();
  F._setWindowsSource(null);

  /* ---- 4. the sleep gap: a beat long ago cuts the open interval at that beat ------------------- */
  f = make();
  await at(f, T0);
  await inject(f, fg(CHROME, T0));
  await at(f, T0 + MIN);
  r = await f.call('test.sample', { sample: sample({ t: T0 + MIN, etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  check('a beat with a fake reading: ok, the first base', r.ok === true && r.first === true, r);
  await inject(f, fg(CODE, T0 + MIN + 20 * MIN));
  await at(f, T0 + 22 * MIN);
  r = await get(f);
  check('sleep gap: Chrome counted only up to the last beat (60 s), not the 20 min after', secOf(r, CHROME_KEY) === 60 && secOf(r, CODE_KEY) === 60, r.today.apps);
  f.stop();
  // the same through a plain event (screentime.get after an event 8 min after the beat)
  f = make();
  await at(f, T0);
  await inject(f, fg(CHROME, T0));
  await f.call('test.sample', { sample: sample({ t: T0, etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  await inject(f, { kind: 'active', agoMs: 0, at: T0 + 8 * MIN });
  r = await get(f);
  check('sleep gap via any event: nothing counted between the beat and the late event', secOf(r, CHROME_KEY) === 0 && r.paused === null && r.since === T0 + 8 * MIN, { s: secOf(r, CHROME_KEY), since: r.since });
  f.stop();
  // the tray switched off for 20 min is not a sleep: after it comes back the count is not cut
  f = make();
  await at(f, T0);
  await inject(f, fg(CHROME, T0));
  await f.call('test.sample', { sample: sample({ t: T0, etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  f.fgNow = Object.assign({ kind: 'fg', hwnd: '5100' }, CHROME);   // usage.fg says Chrome is still in front
  await at(f, T0 + MIN); f.setEnabled(false); await flushQ();
  await at(f, T0 + 20 * MIN); f.setEnabled(true); await flushQ();
  const tk = await f.call('test.sample', { sample: sample({ t: T0 + 20 * MIN, etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  check('tray off then on: the next beat starts a fresh power base (not a bucket from before the off)', tk.ok === true && tk.first === true, tk);
  await inject(f, { kind: 'active', agoMs: 0, at: T0 + 21 * MIN });
  r = await get(f);
  check('tray off then on: 1 min before + 1 min after counted, the off time not', secOf(r, CHROME_KEY) === 120 && r.since === T0 + 20 * MIN, { s: secOf(r, CHROME_KEY), since: r.since });
  f.stop();

  /* ---- 5. off states ------------------------------------------------------------------------- */
  f = make();
  await at(f, T0);
  await inject(f, fg(CHROME, T0));
  await at(f, T0 + 10 * MIN);
  r = await f.call('screentime.set', { on: false });
  check('screentime.set {on:false}: {ok, on:false}', r.ok === true && r.on === false && f.settings.screentime === false, r);
  await at(f, T0 + 30 * MIN);
  r = await get(f);
  check('screen time off: closed at once (10 min), paused off, history kept', r.on === false && r.paused === 'off' && secOf(r, CHROME_KEY) === 600 && r.current === null, { on: r.on, paused: r.paused, s: secOf(r, CHROME_KEY) });
  check('screen time off: worded by the page from on/paused (no duplicate note)', !r.notes.some((n) => /Screen time is off/.test(n)), r.notes);
  f.fgNow = Object.assign({}, CHROME);
  r = await f.call('screentime.set', { on: true });
  await flushQ();
  await at(f, T0 + 35 * MIN);
  r = await get(f);
  check('screentime.set {on:true}: counting resumes from the switch (asks usage.fg)', r.on === true && r.paused === null && secOf(r, CHROME_KEY) === 900, { paused: r.paused, s: secOf(r, CHROME_KEY) });
  check('bad screentime.set args: refused in words', (await f.call('screentime.set', { on: 'yes' })).ok === false);
  // the same through Mantle Settings (onOption)
  f.settings.screentime = false; f.optionFns.forEach((fn) => fn(false, 'screentime'));
  await at(f, T0 + 40 * MIN);
  r = await get(f);
  check('screentime option off via Mantle Settings: closed at once, paused off', r.paused === 'off' && secOf(r, CHROME_KEY) === 900, { paused: r.paused, s: secOf(r, CHROME_KEY) });
  f.setOption('power', false); await flushQ();
  r = await f.call('power.now');
  check('power off: power.now {ok:false, reason}', r.ok === false && typeof r.reason === 'string' && /Power use is off/.test(r.reason) && !r.off, r);
  w = f.watches();
  check('both off: usage.watch {on:false} sent', w.length && w[w.length - 1].args.on === false, w);
  r = await f.call('power.history', { range: 'today' });
  check('power off: history still answers, with a note', r.ok === true && r.notes.some((n) => /Power use is off/.test(n)), r.notes);
  f.stop();

  // tray off
  f = make();
  await flushQ();
  f.setEnabled(false); await flushQ();
  w = f.watches();
  check('tray off: usage.watch {on:false} sent', w.length >= 2 && w[w.length - 1].args.on === false, w);
  const offCalls = [['power.now'], ['power.history', { range: 'today' }], ['screentime.get'], ['screentime.clear'], ['screentime.set', { on: true }], ['icons', { paths: ['C:\\a.exe'] }], ['cost']];
  for (const [verb, args] of offCalls) {
    r = await f.call(verb, args);
    check(`tray off: ${verb} answers {ok:false, off:true, reason}`, r.ok === false && r.off === true && /tray menu/.test(r.reason), r);
  }
  r = await f.call('state');
  check('tray off: state answers ok, on:false, off:true, with the reason', r.ok === true && r.on === false && r.off === true && /tray menu/.test(r.reason) && r.power === true && r.screentime === true && r.idleMin === 5, r);
  f.setEnabled(true); await flushQ();
  r = await f.call('state');
  check('tray on again: state {ok, on:true, power, screentime, idleMin, reason:null}', r.ok === true && r.on === true && r.reason === null && !('off' in r) && same(r, ['ok', 'on', 'power', 'screentime', 'idleMin', 'reason']), r);
  w = f.watches();
  check('tray on again: the watch is back on', w[w.length - 1].args.on === true, w);
  f.stop();

  /* ---- 6. a sidecar without usage commands; a passive one ------------------------------------ */
  f = make({ fail: { 'usage.watch': 'unknown command: usage.watch', 'usage.fg': 'unknown command: usage.fg' } });
  await flushQ();
  const bad = f.health.find((h) => h.ok === false);
  check('old sidecar: health(false) with "no usage commands; rebuild it"', bad && /no usage commands/.test(bad.why) && /rebuild/.test(bad.why), f.health);
  r = await get(f);
  check('old sidecar: screentime.get notes say so in words', r.ok === true && r.notes.some((n) => /rebuild/.test(n)), r.notes);
  r = await f.call('state');
  check('old sidecar: state carries the reason', r.on === true && /rebuild/.test(r.reason || ''), r);
  f.fail = {};
  f.ready(); await flushQ();
  check('a later successful call: health(true)', f.health.length >= 2 && f.health[f.health.length - 1].ok === true, f.health);
  f.stop();
  f = make({ fail: { 'usage.watch': 'refused: sidecar is passive (test mode)' } });
  await flushQ();
  check('passive sidecar refusal: health never goes false', !f.health.some((h) => h.ok === false), f.health);
  f.stop();
  f = make({ watchReason: 'lock and unlock are not heard (WTSRegisterSessionNotification, Win32 error 5)' });
  await flushQ();
  r = await get(f);
  check('a watch that runs half blind: healthy, and the page note says what it misses', !f.health.some((h) => h.ok === false) && r.notes.some((n) => /may count too much: lock and unlock are not heard/.test(n)), { health: f.health, notes: r.notes });
  f.stop();

  /* ---- 7. power.now ------------------------------------------------------------------------- */
  async function nowWith(b0, b1, extra) {
    const g = make();
    await at(g, T0);
    const [s0, s1] = pair(T0, b0, b1, extra);
    await g.call('test.sample', { sample: s0, tick: true });     // a reading younger than 10 s: no 1 s wait
    await g.call('test.sample', { sample: s1 });
    const rep = await g.call('power.now');
    g.stop();
    return rep;
  }
  r = await nowWith(AC(), AC());
  check('power.now: the reply fields', same(r, ['ok', 'at', 'windowMs', 'source', 'battery', 'total', 'chip', 'shareOf', 'rows', 'caption', 'notes']), keysOf(r));
  check('power.now: battery / total / chip fields', same(r.battery, ['percent', 'charging', 'chargeMw', 'secondsLeft', 'why']) && same(r.total, ['mw', 'why']) && same(r.chip, ['pkgMw', 'coresMw', 'gfxMw', 'why']), [keysOf(r.battery), keysOf(r.total), keysOf(r.chip)]);
  check('power.now: every row has exactly {key, kind, name, path, share, mw, why}', r.rows.length > 0 && r.rows.every((x) => same(x, ['key', 'kind', 'name', 'path', 'share', 'mw', 'why'])), r.rows.map(keysOf));
  check('power.now AC: at, windowMs 1000, source ac, charging 5000 mW', r.ok === true && r.at === T0 + 1000 && r.windowMs === 1000 && r.source === 'ac' && r.battery.charging === true && r.battery.chargeMw === 5000 && r.battery.percent === 80, r);
  check('power.now AC: shareOf chip, total null with the words', r.shareOf === 'chip' && r.total.mw === null && r.total.why === 'Windows only reports total draw on battery', { shareOf: r.shareOf, total: r.total });
  check('power.now AC: chip 10 W / cores 6 W / graphics 2 W', r.chip.pkgMw === 10000 && r.chip.coresMw === 6000 && r.chip.gfxMw === 2000 && r.chip.why === null, r.chip);
  let row = (k) => r.rows.find((x) => x.key === k);
  check('power.now AC: Chrome = 0.6 x 6 W + 0.75 x 2 W = 5.1 W, share 0.51 of the chip', row(CHROME_KEY) && row(CHROME_KEY).mw === 5100 && near(row(CHROME_KEY).share, 0.51) && row(CHROME_KEY).kind === 'app' && row(CHROME_KEY).name === 'Google Chrome' && row(CHROME_KEY).path === 'C:\\Apps\\chrome.exe', row(CHROME_KEY));
  check('power.now AC: Code 1.7 W, Windows 1.2 W, rest of the processor 2 W', row(CODE_KEY).mw === 1700 && row('windows').mw === 1200 && row('windows').kind === 'windows' && row('chip-rest').mw === 2000 && row('chip-rest').name === 'Rest of the processor', r.rows);
  check('power.now AC: the screen row says why it is not measured', row('pc-rest').mw === null && row('pc-rest').share === null && row('pc-rest').why === 'Windows only reports total draw on battery' && row('pc-rest').name === 'Screen and the rest of the PC', row('pc-rest'));
  check('power.now AC: shares sum to 1', near(r.rows.reduce((s, x) => s + (x.share || 0), 0), 1, 0.002), r.rows.map((x) => x.share));
  check('power.now: apps first, then Windows, rest of processor, screen', r.rows.slice(-3).map((x) => x.kind).join() === 'windows,chip-rest,pc-rest' && r.rows.slice(0, -3).every((x) => x.kind === 'app'), r.rows.map((x) => x.kind));
  check('power.now: the estimated caption', /Estimated/.test(r.caption) && Array.isArray(r.notes), r.caption);

  r = await nowWith(BAT(30000, -25000), BAT(29990, -25000), { meter: 25000 });
  row = (k) => r.rows.find((x) => x.key === k);
  check('power.now battery: source battery, shareOf pc, total 25 W from the meter', r.source === 'battery' && r.shareOf === 'pc' && r.total.mw === 25000 && r.total.why === null && r.battery.chargeMw === null && r.battery.secondsLeft === 3600, { source: r.source, shareOf: r.shareOf, total: r.total, battery: r.battery });
  check('power.now battery: screen and the rest = 25 - 10 = 15 W, share 0.6', row('pc-rest').mw === 15000 && near(row('pc-rest').share, 0.6) && row('pc-rest').why === null, row('pc-rest'));
  check('power.now battery: Chrome share 5.1 / 25', near(row(CHROME_KEY).share, 0.204), row(CHROME_KEY));
  check('power.now battery: shares sum to 1', near(r.rows.reduce((s, x) => s + (x.share || 0), 0), 1, 0.002), r.rows.map((x) => x.share));

  r = await nowWith(AC(), AC(), { noRapl: true });
  row = (k) => r.rows.find((x) => x.key === k);
  check('power.now no RAPL: shareOf cpu, no watts anywhere', r.shareOf === 'cpu' && r.rows.every((x) => x.mw === null) && r.chip.pkgMw === null && /energy meter/.test(r.chip.why), { shareOf: r.shareOf, chip: r.chip });
  check('power.now no RAPL: Chrome 0.6 of CPU time, Windows 0.2; fixed rows say why', near(row(CHROME_KEY).share, 0.6) && near(row('windows').share, 0.2) && row('chip-rest').share === null && typeof row('chip-rest').why === 'string' && row('pc-rest').share === null && typeof row('pc-rest').why === 'string', r.rows);
  check('power.now no RAPL: the caption says shares are of processor time', /processor time/.test(r.caption), r.caption);

  // no reading younger than 10 s: two readings 1 s apart (one real 1 s wait)
  f = make();
  const [q0, q1] = pair(T0, AC(), AC());
  await f.call('test.sample', { sample: q0 });
  await f.call('test.sample', { sample: q1 });
  const t1 = Date.now();
  r = await f.call('power.now');
  const waited = Date.now() - t1;
  check('power.now with no recent reading: reads twice, about 1 s apart, first reply has numbers', r.ok === true && waited >= 900 && r.windowMs === 1000 && r.chip.pkgMw === 10000, { waited, windowMs: r.windowMs, ok: r.ok, reason: r.reason });
  r = await f.call('power.now');
  check('power.now when Windows has no reading: {ok:false, reason} in words', r.ok === false && typeof r.reason === 'string' && !/undefined/.test(r.reason), r);
  f.stop();

  /* ---- 8. buckets on disk and power.history -------------------------------------------------- */
  f = make();
  await at(f, T0);
  const b0 = sample({ t: T0, etime: 1000, pkg: E0.pkg, cores: E0.cores, gfx: E0.gfx, gpu: { 100: 0, 200: 0 }, battery: BAT(30000, -12000) });
  const b1 = sample({ t: T0 + 300000, etime: 301000, pkg: E0.pkg + pwh(8, 300), cores: E0.cores + pwh(5, 300), gfx: E0.gfx + pwh(1, 300),
    cpu: { 100: 2000000, 101: 1000000, 200: 1000000, 4: 500000, 300: 500000 }, gpu: { 100: 300, 200: 100 }, battery: BAT(29000, -12000) });
  await f.call('test.sample', { sample: b0, tick: true });
  await inject(f, { kind: 'active', agoMs: 0, at: T0 + 300000 });     // moves the test clock only
  r = await f.call('test.sample', { sample: b1, tick: true });
  check('second beat 300 s later: a bucket', r.ok === true && r.bucket && r.bucket.dur === 300, r);
  const pfile = path.join(f.dir, 'usage', `power-${dk(T0)}.jsonl`);
  const lines = fs.existsSync(pfile) ? fs.readFileSync(pfile, 'utf8').trim().split('\n') : [];
  const line = lines.length ? JSON.parse(lines[0]) : {};
  check('bucket on disk: one line in power-<local date>.jsonl', lines.length === 1, lines);
  check('bucket fields: t, dur, pkg, cores, gfx, total, totalHow, ac, pct, apps', ['t', 'dur', 'pkg', 'cores', 'gfx', 'total', 'totalHow', 'ac', 'pct', 'apps'].every((k) => k in line), keysOf(line));
  check('bucket: 8 W chip, total 12 W from the capacity drop (1000 mWh in 300 s), on battery at 50 %', line.t === T0 && line.pkg === 8000 && line.cores === 5000 && line.gfx === 1000 && line.total === 12000 && line.totalHow === 'capacity' && line.ac === false && line.pct === 50, line);
  check('bucket apps: [key, name, mW, ...] with Chrome first and Windows', Array.isArray(line.apps) && line.apps[0][0] === CHROME_KEY && line.apps[0][1] === 'Google Chrome' && line.apps[0][2] === 3750 && line.apps.some((a) => a[0] === 'windows'), line.apps);
  r = await f.call('power.history', { range: 'today' });
  check('history: the reply fields', same(r, ['ok', 'range', 'from', 'to', 'shareOf', 'hours', 'rows', 'totals', 'empty', 'emptyWhy', 'caption', 'notes']), keysOf(r));
  check('history today: from local midnight to now, shareOf pc', r.ok && r.range === 'today' && r.from === new Date(2026, 10, 18).getTime() && r.to === T0 + 300000 && r.shareOf === 'pc' && r.empty === false && r.emptyWhy === null, { from: r.from, to: r.to, shareOf: r.shareOf });
  const h10 = r.hours.find((x) => x.t === T0);
  check('history hours: one per local hour, fields {t, chipWh, totalWh, power, covered}', r.hours.length === 11 && r.hours.every((x) => same(x, ['t', 'chipWh', 'totalWh', 'power', 'covered'])), r.hours.length);
  check('history hour 10: 8 W x 5 min = 0.667 Wh chip, 1 Wh total, battery, fully covered', h10 && near(h10.chipWh, 0.667) && near(h10.totalWh, 1) && h10.power === 'battery' && h10.covered === 1, h10);
  check('history: rows {key, kind, name, path, share, wh}', r.rows.length > 0 && r.rows.every((x) => ['key', 'kind', 'name', 'path', 'share', 'wh'].every((k) => k in x)), r.rows.map(keysOf));
  check('history totals: {chipWh, totalWh, totalWhy, coveredMs, spanMs}', same(r.totals, ['chipWh', 'totalWh', 'totalWhy', 'coveredMs', 'spanMs']) && near(r.totals.chipWh, 0.667) && near(r.totals.totalWh, 1) && r.totals.coveredMs === 300000 && r.totals.spanMs === T0 + 300000 - r.from, r.totals);
  const hc = r.rows.find((x) => x.key === CHROME_KEY);
  check('history: Chrome 3.75 W x 5 min = 0.3125 Wh, a share of the PC', hc && near(hc.wh, 0.313, 0.002) && near(hc.share, 0.3125, 0.002) && hc.path === 'C:\\Apps\\chrome.exe', hc);
  r = await f.call('power.history', { range: '24h' });
  check('history 24h: 24 hours, ok', r.ok === true && r.range === '24h' && r.hours.length === 24, r.hours && r.hours.length);
  r = await f.call('power.history', { range: 'week' });
  check('history: a bad range is refused in words', r.ok === false && typeof r.reason === 'string', r);
  r = await f.call('power.history');
  check('history: no range is refused in words', r.ok === false && typeof r.reason === 'string', r);
  f.stop();

  /* ---- 9. flush and privacy ------------------------------------------------------------------ */
  f = make();
  const SECRET = { pid: 400, created: '9', file: 'secretapp.exe', path: 'C:\\Apps\\secretapp.exe', desc: 'Secret App' };
  await at(f, T0);
  await inject(f, fg(SECRET, T0, { title: 'SECRET TITLE - my bank statement' }), fg(CHROME, T0 + 2 * MIN, { title: 'SECRET TITLE 2' }));
  f.event(Object.assign({ kind: 'fg', hwnd: '77', title: 'SECRET TITLE 3' }, CODE));   // a raw sidecar event too
  await at(f, T0 + 4 * MIN);
  await f.call('test.sample', { sample: b0, tick: true });
  f.stop();
  const sfile = path.join(f.dir, 'usage', 'screentime.json');
  const stText = fs.existsSync(sfile) ? fs.readFileSync(sfile, 'utf8') : '';
  let stJson = null; try { stJson = JSON.parse(stText); } catch (_) { stJson = null; }
  check('stop: screentime.json written, v:1, with the secret app counted', stJson && stJson.v === 1 && stJson.apps['secretapp.exe'] && stJson.days[dk(T0)], stText.slice(0, 200));
  const allFiles = fs.readdirSync(path.join(f.dir, 'usage')).filter((n) => /^(screentime\.json|power-.*\.jsonl)$/.test(n));
  const leaks = allFiles.filter((n) => /title|SECRET/.test(fs.readFileSync(path.join(f.dir, 'usage', n), 'utf8')));
  check('privacy: no stored file holds a title', allFiles.length >= 1 && leaks.length === 0, { allFiles, leaks });
  check('stop: no tmp file left behind', !fs.readdirSync(path.join(f.dir, 'usage')).some((n) => /\.tmp$/.test(n)));

  /* ---- 10. a damaged screentime.json --------------------------------------------------------- */
  const cdir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-usage-unit-'));
  fs.mkdirSync(path.join(cdir, 'usage'), { recursive: true });
  fs.writeFileSync(path.join(cdir, 'usage', 'screentime.json'), '{"v":1, "apps": {not json');
  f = make({ dir: cdir });
  check('damaged file: kept as screentime.json.bad', fs.existsSync(path.join(cdir, 'usage', 'screentime.json.bad')) && !fs.existsSync(path.join(cdir, 'usage', 'screentime.json')));
  await at(f, T0);
  r = await get(f);
  check('damaged file: a worded note in the reply, history fresh', r.ok && r.today.totalSec === 0 && r.notes.some((n) => /screentime\.json/.test(n) && /\.bad/.test(n) && /fresh/.test(n)), r.notes);
  check('damaged file: said once in the log by file name', f.logs.some((l) => /screentime\.json/.test(l)), f.logs);
  await inject(f, fg(CHROME, T0), fg(CODE, T0 + MIN));
  r = await f.call('screentime.clear');
  check('screentime.clear: {ok:true, cleared:1}', r.ok === true && r.cleared === 1 && same(r, ['ok', 'cleared']), r);
  check('screentime.clear: the damaged copy is gone too, and no screentime* file is left', !fs.readdirSync(path.join(cdir, 'usage')).some((n) => /^screentime/.test(n)), fs.readdirSync(path.join(cdir, 'usage')));
  await at(f, T0 + 2 * MIN);
  r = await get(f);
  check('after clear: only time since the clear counts', secOf(r, CHROME_KEY) === 0 && secOf(r, CODE_KEY) === 60, r.today.apps);
  f.stop();

  /* ---- 11. icons ---------------------------------------------------------------------------- */
  f = make();
  r = await f.call('icons', { paths: ['C:\\Apps\\chrome.exe', 'C:\\Apps\\none.exe', 'not a path'] });
  check('icons: the reply fields {ok, icons, why}', same(r, ['ok', 'icons', 'why']) && r.ok === true, r);
  check('icons: a data URL for a program', /^data:image\/png;base64,/.test(r.icons['C:\\Apps\\chrome.exe']) && !('C:\\Apps\\chrome.exe' in r.why), r);
  check('icons: null with a reason when Windows has none', r.icons['C:\\Apps\\none.exe'] === null && /no icon/.test(r.why['C:\\Apps\\none.exe']), r);
  check('icons: a non-path is refused per path, never sent', r.icons['not a path'] === null && typeof r.why['not a path'] === 'string' && !f.iconCalls.includes('not a path'), r);
  await f.call('icons', { paths: ['C:\\Apps\\chrome.exe', 'c:\\apps\\CHROME.exe'] });
  check('icons: cached in memory (one sidecar call per program)', f.iconCalls.filter((p) => /chrome/i.test(p)).length === 1, f.iconCalls);
  check('icons: no list is refused in words', (await f.call('icons', {})).ok === false);
  f.stop();

  /* ---- 12. cost ----------------------------------------------------------------------------- */
  f = make();
  await flushQ();
  await f.call('test.sample', { sample: sample({ t: Date.now(), etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  await f.call('test.sample', { sample: sample({ t: Date.now() + 1000, etime: 2000, pkg: 2, cores: 2, gfx: 2 }), tick: true });
  f.event(Object.assign({ kind: 'fg', hwnd: '1' }, CHROME));
  r = await f.call('cost');
  check('cost: the reply fields', same(r, ['ok', 'since', 'samples', 'sampleMsAvg', 'sidecarCpuMs', 'nodeCpuMs', 'watchCpuMs', 'fgEvents', 'avgCpuPct']), keysOf(r));
  check('cost: 2 readings, 20 ms each, 10 ms of sidecar CPU, the watch 12 ms, 1 fg event', r.ok && r.samples === 2 && r.sampleMsAvg === 20 && r.sidecarCpuMs === 10 && r.watchCpuMs === 12 && r.fgEvents === 1 && typeof r.nodeCpuMs === 'number' && typeof r.avgCpuPct === 'number' && r.avgCpuPct >= 0, r);
  check('cost: asked the watch for its status', f.sent.some((s) => s.cmd === 'usage.watch' && s.args.op === 'status'));
  f.stop();

  /* ---- 13. General Counsel's privacy rules (2026-11-18): each numbered as in the requirements ---- */
  const ZPATH = 'C:\\Users\\Zed Tester\\AppData\\Local\\Zebracorn\\Zebracorn.exe';
  const ZPATH2 = 'C:\\Users\\Zed Tester\\Apps\\Zebra2.exe';
  const ZEBRA = { pid: 700, created: '70', file: 'Zebracorn.exe', path: ZPATH, desc: 'Zebracorn Studio' };
  const ZEBRA2 = { pid: 703, created: '73', file: 'Zebra2.exe', path: ZPATH2, desc: 'Zebra Two' };
  const HUGE = { pid: 702, created: '72', file: 'Huge.exe', path: 'C:\\Apps\\huge.exe', desc: 'Huge Icon App' };
  const EXPL = { pid: 701, created: '71', file: 'explorer.exe', path: 'C:\\Windows\\explorer.exe', desc: 'Windows Explorer' };
  const ZKEY = 'zebracorn.exe';
  const PRIVATE = /zebracorn|zebra two|zebra2|zed tester|users\\|appdata|\.exe/i;   // nothing like this in a log line or a reason
  const zsample = (o) => { const x = sample(o); if (!o.noZebra) x.procs.push(Object.assign({ ppid: 50, session: 1, windows: 1, cpu: (o.cpu && o.cpu[700]) || 0 }, ZEBRA)); return x; };
  const usageFiles = (d) => { const u = path.join(d, 'usage'); return fs.existsSync(u) ? fs.readdirSync(u) : []; };
  const readAll = (d) => usageFiles(d).map((n) => { const p = path.join(d, 'usage', n); return fs.statSync(p).isFile() ? fs.readFileSync(p, 'utf8') : ''; }).join('\n');
  const reasons = [];          // every reason, note and per-path why a reply carried (they reach call-log and the page)
  const keep = (x) => { if (x && typeof x === 'object') { if (typeof x.reason === 'string') reasons.push(x.reason); if (Array.isArray(x.notes)) reasons.push(...x.notes); if (x.why && typeof x.why === 'object') reasons.push(...Object.values(x.why).filter((y) => typeof y === 'string')); } return x; };

  // -- 1. screen time OFF on a first run (and for an existing user who never switched it on); power ON
  f = make({ fresh: true });
  await flushQ();
  r = keep(await f.call('state'));
  check('counsel 1: a first run: state says screentime false, power true', r.ok && r.screentime === false && r.power === true, r);
  await at(f, T0);
  r = await inject(f, fg(ZEBRA, T0, { cls: 'Zebra_Wnd' }), fg(CHROME, T0 + MIN));
  check('counsel 1: off by default: an app in front opens no interval', r.counting === false && r.paused === 'off', r);
  await at(f, T0 + 10 * MIN);
  await f.call('test.sample', { sample: zsample({ t: T0 + 10 * MIN, etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  await at(f, T0 + 15 * MIN);
  r = await f.call('test.sample', { sample: zsample({ t: T0 + 15 * MIN, etime: 301000, pkg: E0.pkg, cores: E0.cores, gfx: E0.gfx, cpu: { 700: 1000000, 100: 1000000 } }), tick: true });
  check('counsel 1: power use is on by default: the beat stores a bucket', r.ok === true && !!r.bucket, r);
  r = keep(await get(f));
  check('counsel 1: screentime.get: on false, paused off, nothing counted, nobody current', r.ok && r.on === false && r.paused === 'off' && r.today.totalSec === 0 && r.current === null && r.since === null, r);
  f.stop();
  check('counsel 1: no screentime* file was written (only the power file)', !usageFiles(f.dir).some((n) => /^screentime/.test(n)) && usageFiles(f.dir).some((n) => /^power-/.test(n)), usageFiles(f.dir));
  check('counsel 1: no icon was read for screen time', f.iconCalls.length === 0, f.iconCalls);
  f = make({ fresh: true, settings: { power: true, idleMin: 5 } });   // an existing user: options set, never screen time
  await at(f, T0);
  r = await inject(f, fg(CHROME, T0));
  check('counsel 1: an existing user who never turned it on: still off', r.counting === false && (await f.call('state')).screentime === false, r);
  f.stop();

  // -- 2 + 3. which app = exe name + product name + icon; no path, no title in any file; every write under <userData>/usage
  const writes = [];
  const SPY = ['writeFileSync', 'appendFileSync', 'renameSync', 'mkdirSync', 'rmSync', 'unlinkSync', 'copyFileSync', 'cpSync', 'writeFile', 'appendFile', 'rename', 'mkdir', 'rm', 'unlink', 'copyFile', 'cp', 'createWriteStream'];
  const real = {};
  const spyOn = () => { for (const k of SPY) if (typeof fs[k] === 'function') { real[k] = fs[k]; fs[k] = function (p, ...rest) { writes.push({ k, p: String(p), to: /^(rename|copyFile|cp)/.test(k) ? String(rest[0]) : null }); return real[k].call(fs, p, ...rest); }; } };
  const spyOff = () => { for (const k of Object.keys(real)) fs[k] = real[k]; };
  spyOn();
  try {
    f = make();                       // screen time switched on
    await flushQ();
    await at(f, T0);
    await inject(f, fg(ZEBRA, T0, { cls: 'Zebra_Wnd', title: 'ZEBRA SECRET TITLE' }));
    await flushQ();
    await inject(f, fg(CHROME, T0 + MIN), fg(ZEBRA, T0 + 2 * MIN), fg(CODE, T0 + 3 * MIN), fg(ZEBRA, T0 + 4 * MIN, { title: 'ZEBRA SECRET TITLE 2' }));
    await flushQ();
    check('counsel 2: the icon is read once per app, from the path held in memory', f.iconCalls.filter((p) => p === ZPATH).length === 1, f.iconCalls);
    f.event(Object.assign({ kind: 'fg', hwnd: '7', cls: 'CabinetWClass' }, EXPL));   // a File Explorer window, straight from the sidecar
    await flushQ();
    await inject(f, Object.assign({ kind: 'fg', hwnd: '8', cls: '#32770', at: T0 + 6 * MIN }, EXPL));   // explorer.exe's Run dialog: the shell
    r = await inject(f, fg({ pid: 704, created: '74', file: 'SearchHost.exe', path: 'C:\\Windows\\SystemApps\\SearchHost.exe', desc: 'Search' }, T0 + 6 * MIN + 30000));
    check('counsel 2: Search (and explorer.exe that is not File Explorer) is the shell: nothing counting', r.counting === false && r.current === null, r);
    await inject(f, fg(HUGE, T0 + 7 * MIN));
    await flushQ();
    f.fail.icon = `Icons.ForPath could not read ${ZPATH2} (Zebra2.exe)`;
    await inject(f, fg(ZEBRA2, T0 + 8 * MIN));
    await flushQ();
    f.fail = {};
    await inject(f, fg(CHROME, T0 + 9 * MIN), fg(ZEBRA2, T0 + 9 * MIN + 1000));
    await flushQ();
    const asked2 = f.sent.filter((x) => x.cmd === 'icon' && x.args.path === ZPATH2).length;
    check('counsel 2: a failed icon is not asked again (no retry loop)', asked2 === 1, asked2);
    // power: two beats with Zebracorn running, then a reading without it
    await at(f, T0 + 10 * MIN);
    await f.call('test.sample', { sample: zsample({ t: T0 + 10 * MIN, etime: 1000, pkg: E0.pkg, cores: E0.cores, gfx: E0.gfx }), tick: true });
    await inject(f, { kind: 'active', agoMs: 0, at: T0 + 15 * MIN });
    r = await f.call('test.sample', { sample: zsample({ t: T0 + 15 * MIN, etime: 301000, pkg: E0.pkg + pwh(8, 300), cores: E0.cores + pwh(5, 300), gfx: E0.gfx + pwh(1, 300), cpu: { 700: 3000000, 100: 1000000 } }), tick: true });
    check('counsel 2: a power bucket with Zebracorn in it', r.ok && r.bucket && r.bucket.apps.some((a) => a[0] === ZKEY && a[1] === 'Zebracorn Studio'), r.bucket && r.bucket.apps);
    let hz = keep(await f.call('power.history', { range: 'today' }));
    let zrow = hz.rows && hz.rows.find((x) => x.key === ZKEY);
    check('counsel 2: power history row: keyed by exe name, its path from the live reading (running)', zrow && zrow.path === ZPATH && zrow.name === 'Zebracorn Studio', zrow);
    await f.call('test.sample', { sample: zsample({ t: T0 + 15 * MIN + 1000, etime: 302000, pkg: E0.pkg + pwh(8, 301), cores: E0.cores + pwh(5, 301), gfx: E0.gfx + pwh(1, 301), noZebra: true }) });
    keep(await f.call('power.now'));
    hz = keep(await f.call('power.history', { range: 'today' }));
    zrow = hz.rows && hz.rows.find((x) => x.key === ZKEY);
    check('counsel 2: ...and path null once it is not running (never from disk)', zrow && zrow.path === null, zrow);
    await at(f, T0 + 16 * MIN);
    r = keep(await get(f));
    const rowsAll = [...r.today.apps, ...r.days.flatMap((d) => d.apps)];
    check('counsel 2: screentime.get rows carry icon (data URL or null) and no path', rowsAll.length > 0 && rowsAll.every((a) => !('path' in a) && (a.icon === null || /^data:image\//.test(a.icon))), rowsAll);
    const zt = r.today.apps.find((a) => a.key === ZKEY);
    check('counsel 2: today names Zebracorn by its product name with its icon', zt && zt.name === 'Zebracorn Studio' && zt.icon === 'data:image/png;base64,QUJD', zt);
    check('counsel 2: File Explorer (CabinetWClass) counted as explorer.exe, its dialog and Search not', secOf(r, 'explorer.exe') === 120 && secOf(r, 'searchhost.exe') === 0, r.today.apps);
    f.stop();
  } finally { spyOff(); }
  const ud = path.join(f.dir, 'usage');
  const outside = writes.filter((w) => !(path.resolve(w.p).startsWith(ud + path.sep) || path.resolve(w.p) === ud) || (w.to && !path.resolve(w.to).startsWith(ud + path.sep)));
  check('counsel 3: every file the feature writes, moves or deletes is inside <userData>/usage', writes.length > 0 && outside.length === 0, outside.length ? outside : writes.length);
  check('counsel 3: nothing is written for Helm or Quartz (no look-sync, look.json or badge.json)', !writes.some((w) => /look-sync|look\.json|badge\.json|helm|quartz/i.test(w.p + ' ' + (w.to || ''))), writes.map((w) => w.p));
  const disk = readAll(f.dir);
  check('counsel 2: no stored file holds a path, the user name or a title', disk.length > 0 && !/Users|Zed Tester|AppData|SECRET|title|\\\\/.test(disk), disk.slice(0, 400));
  let stz = null; try { stz = JSON.parse(fs.readFileSync(path.join(ud, 'screentime.json'), 'utf8')); } catch (_) { stz = null; }
  check('counsel 2: screentime.json apps: {name, file, icon} keyed by exe file name', stz && JSON.stringify(stz.apps[ZKEY]) === JSON.stringify({ name: 'Zebracorn Studio', file: 'Zebracorn.exe', icon: 'data:image/png;base64,QUJD' }), stz && stz.apps);
  check('counsel 2: an icon over 24 KB is stored as null, a failed one as null', stz && stz.apps['huge.exe'] && stz.apps['huge.exe'].icon === null && stz.apps['zebra2.exe'] && stz.apps['zebra2.exe'].icon === null, stz && stz.apps);
  check('counsel 2: no tmp, temp or bad file is left behind', !usageFiles(f.dir).some((n) => /\.(tmp|bad)$/.test(n)), usageFiles(f.dir));

  // -- 3 + 7. the source: no network, nothing of Helm's or Quartz's, never SRUM or an elevated command
  const code = (n) => fs.readFileSync(path.join(__dirname, n), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
  for (const n of ['usage.js', '_usage-core.js', '_usage-store.js']) {
    const c = code(n);
    const reqs = [...c.matchAll(/require\(\s*['"]([^'"]+)['"]\s*\)/g)].map((m) => m[1]);
    check(`counsel 3: ${n} requires no network module`, reqs.every((m) => ['fs', 'os', 'path', 'electron', './_usage-core.js', './_usage-store.js', './_taskmgr-core.js'].includes(m)), reqs);
    check(`counsel 3: ${n} has no network call (fetch, XHR, sockets, electron net, openExternal)`, !/\bfetch\s*\(|XMLHttpRequest|WebSocket|EventSource|sendBeacon|\.net\b|\bnet\.|openExternal|https?:\/\//.test(c));
    check(`counsel 3: ${n} names nothing of Helm, Quartz or look-sync`, !/look-sync|look\.json|badge\.json|toprow|\bhelm\b|\bquartz\b|APPDATA|getPath\s*\(/i.test(c));
    check(`counsel 7: ${n} never reads SRUM or runs an elevated command`, !/srum|elevat|apphistory|runas/i.test(c));
  }
  const eUse = [...code('usage.js').matchAll(/\bE\.(\w+)/g)].map((m) => m[1]);
  check("counsel 3: usage.js takes only BrowserWindow from electron (Mantle's own windows)", eUse.length > 0 && eUse.every((m) => m === 'BrowserWindow'), eUse);

  // -- 4. the 30-day and 3-day delete: at start-up and every 24 h, whatever is switched on or off
  const hdir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-usage-unit-'));
  fs.mkdirSync(path.join(hdir, 'usage'), { recursive: true });
  const NOW = Date.now();
  const oldDay = dk(NOW - 40 * 86400000), newDay = dk(NOW);
  const day = (secs) => ({ apps: { 'old.exe': secs }, hours: Object.assign(new Array(24).fill(0), { 9: secs }) });
  fs.writeFileSync(path.join(hdir, 'usage', 'screentime.json'), JSON.stringify({ v: 1, apps: { 'old.exe': { name: 'Old', file: 'old.exe' } }, days: { [oldDay]: day(60), [newDay]: day(30) } }));
  const oldPower = `power-${dk(NOW - 5 * 86400000)}.jsonl`, newPower = `power-${newDay}.jsonl`;
  fs.writeFileSync(path.join(hdir, 'usage', oldPower), JSON.stringify({ t: NOW - 5 * 86400000, dur: 300, apps: [] }) + '\n');
  fs.writeFileSync(path.join(hdir, 'usage', newPower), JSON.stringify({ t: NOW - 60000, dur: 300, apps: [] }) + '\n');
  F._setHousekeepMs(40);
  f = make({ dir: hdir, fresh: true, enabled: false, settings: { power: false } });   // the tray switch, power and screen time all off
  let kept = JSON.parse(fs.readFileSync(path.join(hdir, 'usage', 'screentime.json'), 'utf8'));
  check('counsel 4: at start-up (everything off): the day 40 days back is deleted, today kept', !kept.days[oldDay] && !!kept.days[newDay], Object.keys(kept.days));
  check('counsel 4: at start-up: the power file 5 days back is deleted, today kept', !fs.existsSync(path.join(hdir, 'usage', oldPower)) && fs.existsSync(path.join(hdir, 'usage', newPower)), usageFiles(hdir));
  let hk = await f.call('test.housekeep');
  check('counsel 4: the clean-up ran once and the next is armed, unref\'d', hk.runs === 1 && hk.armed === true && hk.refd === false, hk);
  await f.call('test.clock', { now: NOW + 31 * 86400000 });           // a month on, with nobody opening anything
  await new Promise((res) => setTimeout(res, 150));
  hk = await f.call('test.housekeep');
  check('counsel 4: the timer chain ran again on its own (and re-armed)', hk.runs >= 2 && hk.armed === true && hk.refd === false, hk);
  check('counsel 4: ...and deleted what was then over 30 days (screen time) and 3 days (power) old', !fs.existsSync(path.join(hdir, 'usage', 'screentime.json')) && !fs.existsSync(path.join(hdir, 'usage', newPower)), usageFiles(hdir));
  f.stop();
  F._setHousekeepMs(null);
  hk = make({ fresh: true });
  check('counsel 4: the real beat is 24 h', (await hk.call('test.housekeep')).everyMs === 86400000);
  hk.stop();

  // -- 5. off stops at once and keeps the history; Clear deletes at once and nothing old comes back
  f = make();
  const Y = T0 - 86400000;                    // yesterday 10:00
  await at(f, Y);
  await inject(f, fg(ZEBRA, Y));
  await at(f, Y + 5 * MIN);
  await inject(f, fg(CHROME, Y + 5 * MIN));
  await at(f, Y + 15 * MIN);
  r = await f.call('screentime.set', { on: false });
  const sfile5 = path.join(f.dir, 'usage', 'screentime.json');
  let s5 = fs.existsSync(sfile5) ? JSON.parse(fs.readFileSync(sfile5, 'utf8')) : null;
  check('counsel 5: switching off closes the open interval at once and saves it (history kept)', r.ok && s5 && s5.days[dk(Y)] && s5.days[dk(Y)].apps[CHROME_KEY] === 600 && s5.days[dk(Y)].apps[ZKEY] === 300, s5);
  await at(f, Y + 40 * MIN);
  r = await inject(f, fg(CODE, Y + 40 * MIN));
  check('counsel 5: off: a new app in front is not counted', r.counting === false, r);
  for (const n of ['screentime.json.tmp', 'screentime.json.bad', 'screentime-backup.json']) fs.writeFileSync(path.join(f.dir, 'usage', n), '{"v":1,"apps":{"zebracorn.exe":{"name":"old copy"}},"days":{}}');
  await f.call('screentime.set', { on: true });
  await at(f, T0);
  f.fgNow = Object.assign({ kind: 'fg', hwnd: '5100' }, CHROME);
  await inject(f, fg(CHROME, T0));
  await at(f, T0 + 10 * MIN);
  r = keep(await f.call('screentime.clear'));
  check('counsel 5: clear: {ok, cleared: 1}', r.ok === true && r.cleared === 1, r);
  check('counsel 5: clear: no screentime* file is left (json, tmp, bad, backup: deleted, not moved)', !usageFiles(f.dir).some((n) => /^screentime/i.test(n)), usageFiles(f.dir));
  r = keep(await get(f));
  check('counsel 5: clear: memory too: no day, no app, no icon from before', r.today.totalSec === 0 && r.days.every((d) => d.totalSec === 0), r.days);
  await at(f, T0 + 12 * MIN);
  await f.call('test.sample', { sample: sample({ t: T0 + 12 * MIN, etime: 1000, pkg: 1, cores: 1, gfx: 1 }), tick: true });
  f.stop();                                   // the last flush
  const after = fs.existsSync(sfile5) ? JSON.parse(fs.readFileSync(sfile5, 'utf8')) : null;
  check('counsel 5: a later flush writes only time after the clear (the old day and the pre-clear interval never come back)', after && !after.days[dk(Y)] && after.days[dk(T0)] && after.days[dk(T0)].apps[CHROME_KEY] === 120 && !after.apps[ZKEY], after);
  f = make();
  await at(f, T0);
  r = keep(await f.call('screentime.clear'));
  check('counsel 5: clearing with nothing kept: ok, 0 days, still no file', r.ok === true && r.cleared === 0 && !usageFiles(f.dir).some((n) => /^screentime/.test(n)), r);
  f.stop();

  // -- 6. failures whose words name an app or a path: never in mantle.log, never in a reason
  f = make();
  await at(f, T0);
  await inject(f, fg(ZEBRA, T0));
  f.fail['usage.sample'] = `could not read ${ZPATH} (Zebracorn.exe, pid 700)`;
  f.fail['usage.fg'] = `no window for ${ZPATH}`;
  r = keep(await f.call('power.now'));
  check('counsel 6: a failed reading: ok:false with a reason in plain words', r.ok === false && typeof r.reason === 'string', r);
  keep(await f.call('icons', { paths: [ZPATH, 'C:\\Apps\\none.exe'] }));
  f.ready(); await flushQ();
  keep(await get(f));
  f.stop();
  const leakLogs = f.logs.filter((l) => PRIVATE.test(l));
  check('counsel 6: the failure was logged, scrubbed: no app, exe or path in any log line', f.logs.some((l) => /usage\.sample failed/.test(l)) && leakLogs.length === 0, leakLogs.length ? leakLogs : f.logs);
  const leakWhy = reasons.filter((x) => PRIVATE.test(x));
  check('counsel 6: no reason, note or why in any reply of this file names an app, an exe or a path', reasons.length > 0 && leakWhy.length === 0, leakWhy);
  const leakAll = allLogs.filter((l) => PRIVATE.test(l));
  check('counsel 6: no log line of this whole run names an app, an exe or a path', leakAll.length === 0, leakAll);

  // -- 8. where it lives: <userData>/usage (userData = two folders up from the feature's settings file)
  check('counsel 8: the store is <userData>/usage (live %APPDATA%\\mantle\\usage, --test %TEMP%\\mantle-test-<profile>\\usage)', fs.existsSync(path.join(hdir, 'usage')) && path.dirname(path.dirname(path.join(hdir, 'features', 'usage.json'))) === hdir);

  /* ---- logs carry no app names --------------------------------------------------------------- */
  const named = allLogs.filter((l) => /chrome|secret|visual studio|code\.exe|zebracorn|zed tester|\.exe/i.test(l));
  check('no log line names an app', named.length === 0, named);

  for (const g of instances) g.stop();
  F._setWindowsSource(null);
  F._setHousekeepMs(null);
  for (const d of dirs) { try { fs.rmSync(d, { recursive: true, force: true }); } catch (_) { /* best effort */ } }
  clearInterval(keepAlive);
  for (const x of failures) console.log('  FAIL ' + x);
  console.log('USAGE TEST: ' + passed + ' passed, ' + failures.length + ' failed');
  process.exit(failures.length ? 1 : 0);
})().catch((e) => { console.log('  FAIL the test itself threw: ' + (e && e.stack)); console.log('USAGE TEST: ' + passed + ' passed, ' + (failures.length + 1) + ' failed'); process.exit(1); });
