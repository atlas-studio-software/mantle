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
const CHROME_KEY = 'c:\\apps\\chrome.exe', CODE_KEY = 'c:\\apps\\code.exe';
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
    dir, settings: Object.assign({}, opts.settings || {}), sent: [], logs: [], cmds: {}, handlers: {}, health: [],
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
      if (cmd === 'icon') { f.iconCalls.push(args.path); return { ok: true, result: { icon: /none/i.test(args.path) ? null : 'data:image/png;base64,QUJD' } }; }
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
  check('options: screentime bool default true', O.screentime && O.screentime.type === 'bool' && O.screentime.default === true, O.screentime);
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
  check('today: apps sorted by seconds, with name and path', r.today.apps[0].key === CHROME_KEY && r.today.apps[0].path === 'C:\\Apps\\chrome.exe' && r.today.apps[1].name === 'Visual Studio Code', r.today.apps);
  check('today: hours[10] holds it all, 24 hours', r.today.hours.length === 24 && r.today.hours[10] === 1740 && r.today.date === dk(T0), r.today.hours);
  check('days: 7, oldest first, today last', r.days.length === 7 && r.days[6].date === dk(T0) && r.days[0].date === dk(new Date(2026, 10, 12, 12).getTime()) && r.days[6].totalSec === 1740 && r.days[0].totalSec === 0, r.days.map((d) => d.date));
  check('screentime.get: the reply fields', same(r, ['ok', 'on', 'idleMin', 'keptDays', 'today', 'days', 'since', 'current', 'paused', 'notes']) && r.keptDays === 30 && r.on === true && r.idleMin === 5, keysOf(r));
  check('today / days entries: their fields', same(r.today, ['date', 'totalSec', 'hours', 'apps']) && same(r.days[6], ['date', 'totalSec', 'apps']) && same(r.today.apps[0], ['key', 'name', 'path', 'seconds']));
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
  check('screen time off: a worded note', r.notes.some((n) => /Screen time is off/.test(n)), r.notes);
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
  check('history: Chrome 3.75 W x 5 min = 0.3125 Wh, a share of the PC', hc && near(hc.wh, 0.313, 0.002) && near(hc.share, 0.3125, 0.002) && hc.path === CHROME_KEY, hc);
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
  check('stop: screentime.json written, v:1, with the secret app counted', stJson && stJson.v === 1 && stJson.apps['c:\\apps\\secretapp.exe'] && stJson.days[dk(T0)], stText.slice(0, 200));
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
  check('screentime.clear: the damaged copy is gone too, a fresh file written', !fs.existsSync(path.join(cdir, 'usage', 'screentime.json.bad')) && fs.existsSync(path.join(cdir, 'usage', 'screentime.json')));
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

  /* ---- logs carry no app names --------------------------------------------------------------- */
  const named = allLogs.filter((l) => /chrome|secret|visual studio|code\.exe/i.test(l));
  check('no log line names an app', named.length === 0, named);

  for (const g of instances) g.stop();
  F._setWindowsSource(null);
  for (const d of dirs) { try { fs.rmSync(d, { recursive: true, force: true }); } catch (_) { /* best effort */ } }
  clearInterval(keepAlive);
  for (const x of failures) console.log('  FAIL ' + x);
  console.log('USAGE TEST: ' + passed + ' passed, ' + failures.length + ' failed');
  process.exit(failures.length ? 1 : 0);
})().catch((e) => { console.log('  FAIL the test itself threw: ' + (e && e.stack)); console.log('USAGE TEST: ' + passed + ' passed, ' + (failures.length + 1) + ' failed'); process.exit(1); });
