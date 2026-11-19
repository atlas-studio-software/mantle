/* usage.js - "Power use and screen time" (lane mantle-ps-node, 2026-11-18; the user approved it 2026-11-18).
 * The main-process half: one shared sampler, the screen-time accounting, the history on disk and the
 * commands the quick-settings "Power use" view (ui/bar/qs-power.js) and the Settings "Screen time"
 * section (ui/settings/sections/screentime.js) call. Design: work/drafts/power-pull-design.md addendum.
 *
 *  - Screen time is event-driven: the sidecar (sidecar/Usage.cs, `usage.watch`) reports the window in
 *    front (pid and exe only, never a title), lock/unlock, suspend/resume and idle/active.
 *  - Power: one `usage.sample` every 300 s (a setTimeout chain, unref'd, never stacked) makes one
 *    5-minute bucket; power.now samples on demand (the QS view polls every 2 s while it is open).
 *  - It never rests: counting while a game runs is the point.
 *
 * PRIVACY (General Counsel, 2026-11-18; every line below has a test in usage.test.js):
 *  - Screen time is OFF until the user turns it on (option `screentime`, default false): no interval,
 *    no screentime.json, no icon read before that. Power use is on by default.
 *  - "Which app" is the exe FILE name (the key), the product name and the icon. A program path holds
 *    the Windows user name, so it never reaches a file: it stays in memory (the icon is read from it
 *    once and stored as a small data URL; power history rows get it from the latest live reading).
 *  - Nothing leaves the PC and nothing is shared with Helm or Quartz: no network code here, and every
 *    file is under <userData>/usage. Mantle's own record only, never Windows' SRUM database.
 *  - 30 days (screen time) and 3 days (power) are deleted on their own: at start-up and every 24 h,
 *    whether or not anything is on. Off stops at once and keeps the history; Clear history deletes it.
 *  - Mantle's log and the replies' reasons never name an app, an exe or a path (C.scrub, no app fields).
 * Pure logic: _usage-core.js. Files: _usage-store.js. Tests: usage.test.js, _usage-core.test.js.
 */
'use strict';
const os = require('os');
const path = require('path');
const C = require('./_usage-core.js');
const { createStore } = require('./_usage-store.js');

const PERIOD = 300000;          // the sampler's beat
const LATE = 150000;            // a beat later than this = the PC slept (or the process was frozen)
const FIRST_MS = 15000;         // the first beat after start-up takes the first base reading
const FLUSH_MS = 300000;
const NOW_FRESH_MS = 10000;     // power.now pairs a new reading with one younger than this
const NOW_GAP_MS = 1000;        // ...else it reads twice, this far apart
const ICONS_MAX = 64;
const DAY_MS = 86400000;
let housekeepMs = DAY_MS;       // the clean-up's beat (tests shorten it: _setHousekeepMs)

const OFF = "Power use and screen time is off: turn it on in Mantle's tray menu";
const NO_CMDS = 'this sidecar has no usage commands; rebuild it (npm run build:sidecar)';
const NO_CMDS_PAGE = "Mantle's helper program is older than this feature, so it cannot measure power use or screen time yet (rebuild it: npm run build:sidecar)";
const POWER_OFF = 'Power use is off in Mantle Settings (Power use and screen time)';
const RESTARTING = "Mantle's helper program is restarting; try again in a moment";

const num = (x) => (typeof x === 'number' && Number.isFinite(x) ? x : null);
const sleep = (ms) => new Promise((r) => { const t = setTimeout(r, ms); if (t.unref) t.unref(); });

/* ---- Mantle's own windows (electron, loaded lazily; absent in pure-node tests) --------------- */

let electron;   // undefined = not looked up yet, null = not in Electron
function electronApi() {
  if (electron === undefined) {
    try { const e = require('electron'); electron = e && typeof e === 'object' && e.BrowserWindow ? e : null; } catch (_) { electron = null; }
  }
  return electron;
}
function electronWindows() {
  const E = electronApi();
  if (!E) return [];
  const out = [];
  for (const w of E.BrowserWindow.getAllWindows()) {
    try {
      if (w.isDestroyed()) continue;
      const b = w.getNativeWindowHandle();
      out.push({ hwnd: b.length >= 8 ? b.readBigUInt64LE(0).toString() : String(b.readUInt32LE(0)), focusable: w.isFocusable(), onTop: w.isAlwaysOnTop() });
    } catch (_) { /* a window closing under us */ }
  }
  return out;
}
let windowsOf = electronWindows;

module.exports = {
  name: 'usage',
  label: 'Power use and screen time',
  defaultOn: true,
  chords: [],
  observe: [],
  // no `rest`: it keeps counting while a game runs
  options: [
    { key: 'power', label: 'Power use in quick settings', help: "Which apps use the most power, from each app's processor and graphics use", type: 'bool', default: true },
    { key: 'screentime', label: 'Screen time', help: 'Off until you turn it on. How long each app is in front, kept only on this PC for 30 days', type: 'bool', default: false },
    { key: 'idleMin', label: 'Away after no input for', help: 'Screen time stops counting after this long without a key or mouse move, unless a video keeps the screen on', type: 'number', min: 1, max: 60, step: 1, unit: 'min', default: 5 },
  ],

  /** Tests only: where Mantle's windows come from ([{hwnd, focusable, onTop}]); null = Electron's. */
  _setWindowsSource(fn) { windowsOf = typeof fn === 'function' ? fn : electronWindows; },
  /** Tests only: the clean-up's beat in ms (null = every 24 h). */
  _setHousekeepMs(ms) { housekeepMs = typeof ms === 'number' && ms > 0 ? ms : DAY_MS; },

  install(ctx) {
    const test = !!(ctx.flags && ctx.flags.test);
    let fakeNow = null;
    const clock = () => (fakeNow !== null ? fakeNow : Date.now());
    const say = (t) => ctx.log(C.scrub(t));   // never an exe name or a path in mantle.log
    const userData = path.dirname(path.dirname(ctx.settings.file));   // <userData>/features/usage.json
    const store = createStore(path.join(userData, 'usage'), { log: say });
    const st = new C.ScreenTime(store.loadScreentime());
    st.setOn(false, clock());          // nothing opens before apply() has read the switch
    const S = {
      stopped: false, timer: null, ticking: false, lastTickAt: null, lastFlush: clock(),
      base: null,                      // the sampler's last reading: the next bucket starts here
      last: null, lastAt: 0,           // the newest reading of any kind, and when it came
      slept: false, acFlip: false, rates: [],
      watchSent: null,                 // what the sidecar was last asked to watch ('on:<min>' | 'off'), null = ask again
      watchWhy: null, watchNote: null, passive: false, health: { cmds: null, watch: null }, healthSaid: null,
      stOn: null, nowInflight: null, fakeSamples: [], icons: new Map(), cost: C.newCost(clock()),
      livePaths: new Map(),            // exe file name -> path, from the latest reading (memory only)
      iconAsking: new Set(),           // screen time: apps whose icon is being read now
      hkTimer: null, hkRuns: 0,        // the clean-up (30 / 3 days): at start-up, then every 24 h
    };

    const clampIdle = (v) => { const n = Number(v); return Number.isFinite(n) ? Math.min(60, Math.max(1, n)) : 5; };
    const opt = () => ({
      power: ctx.settings.get('power', true) !== false,
      screentime: ctx.settings.get('screentime', false) === true,   // off until the user turns it on
      idleMin: clampIdle(ctx.settings.get('idleMin', 5)),
    });
    const wanted = () => { const o = opt(); return !S.stopped && ctx.enabled() && (o.power || o.screentime); };
    const mantlePid = () => (electronApi() ? process.pid : undefined);   // undefined: the reading's parentPid says (it is our sidecar's parent)
    const addCpu = (c0) => { const d = process.cpuUsage(c0); S.cost.nodeCpuUs += d.user + d.system; };

    /* ---- health: a sidecar without usage commands, or a watch Windows refused ---------------- */
    function sayHealth() {
      const why = S.health.cmds || S.health.watch || null;
      if (why === S.healthSaid) return;
      S.healthSaid = why;
      ctx.health(!why, why || undefined);
    }
    /** A failed sidecar call -> the words for a page; the technical detail goes to the log. */
    function refused(err, what) {
      const e = String(err || '');
      if (/unknown command|unknown cmd|no such command/i.test(e)) { S.health.cmds = NO_CMDS; sayHealth(); return NO_CMDS_PAGE; }
      if (/passive/i.test(e)) { S.passive = true; return 'This is a test run: the helper program does not watch Windows'; }
      say(`${what} failed: ${e}`);
      return /within \d+ ?ms|no reply/i.test(e) ? 'Windows did not answer in time; try again in a moment' : !ctx.sidecar.ready ? RESTARTING : 'Windows did not hand over the reading; try again in a moment';
    }
    const cmdsOk = () => { if (S.health.cmds) { S.health.cmds = null; sayHealth(); } };

    /* ---- the watch (screen time's events) ----------------------------------------------------- */
    async function sendWatch(on, idleMin) {
      const key = on ? `on:${idleMin}` : 'off';
      S.watchSent = key;
      if (!ctx.sidecar.ready) { S.watchSent = null; return; }   // onReady sends it
      const r = await ctx.sidecar.send('usage.watch', { on, idleMs: Math.round(idleMin * 60000) });
      if (S.stopped) return;
      if (!r.ok) { if (S.watchSent === key) S.watchSent = null; S.watchWhy = refused(r.error, 'usage.watch'); return; }
      cmdsOk();
      const res = r.result || {};
      if (on && res.ok === false) {
        S.watchWhy = `Windows did not let Mantle watch which app is in front (${res.reason || 'no reason given'}), so screen time is not counted`;
        S.health.watch = `screen time cannot watch the window in front: ${res.reason || 'no reason given'}`;
      } else { S.watchWhy = null; S.health.watch = null; }
      // a watch that runs but hears less than it should (no lock events, no idle time): said on the page
      S.watchNote = on && res.ok !== false && res.reason ? `Screen time may count too much: ${res.reason}` : null;
      sayHealth();
    }

    async function refreshFg() {
      const o = opt();
      if (S.stopped || !ctx.enabled() || !o.screentime || !ctx.sidecar.ready) return;
      const r = await ctx.sidecar.send('usage.fg', {});
      if (S.stopped) return;
      if (!r.ok) { refused(r.error, 'usage.fg'); return; }
      cmdsOk();
      const f = r.result || {};
      const now = clock();
      if (f.kind === 'shell' || f.shell === true || !(f.pid > 0)) onEvent({ kind: 'shell' }, now, true);
      else onEvent(Object.assign({}, f, { kind: 'fg' }), now, true);
      const idle = num(f.idleMs);
      const c0 = process.cpuUsage();
      if (idle !== null && idle >= o.idleMin * 60000) st.apply({ kind: 'idle', idleMs: idle }, now);
      else if (idle !== null && st.idle) st.apply({ kind: 'active', agoMs: idle }, now);
      addCpu(c0);
    }

    /* ---- events ------------------------------------------------------------------------------- */
    /** A tick (or an event) long after the last beat = a sleep the events missed: cut at the last beat. */
    function checkGap(now) {
      if (S.lastTickAt === null || now - S.lastTickAt <= PERIOD + LATE) return false;
      st.cut(Math.max(S.lastTickAt, st.open ? st.open.since : 0), now);
      S.slept = true;
      S.lastTickAt = now;
      return true;
    }

    function onEvent(ev, now, quiet) {
      if (S.stopped || !ev || typeof ev.kind !== 'string') return;
      const c0 = process.cpuUsage();
      try {
        checkGap(now);
        const kind = ev.kind;
        if (kind === 'suspend' || kind === 'resume') S.slept = true;
        if (kind === 'fg') {
          if (!quiet) S.cost.fgEvents++;
          const m = C.mantleWindow(ev, windowsOf(), process.pid);
          // only these fields go on: never a title, whatever the sender added
          if (m === 'shell') st.apply({ kind: 'shell' }, now);
          else st.apply({ kind: 'fg', pid: ev.pid, file: ev.file, path: ev.path, desc: ev.desc, cls: ev.cls, mantle: m === 'app' ? 'app' : undefined }, now);
          fetchIcon();
        } else st.apply({ kind, idleMs: ev.idleMs, agoMs: ev.agoMs }, now);
        if (kind === 'unlock' || kind === 'resume') refreshFg().catch(() => {});
      } finally { addCpu(c0); }
    }

    /**
     * The icon of the app being counted, read ONCE (the sidecar's `icon` from the path held in memory)
     * and stored with the app as a data URL; bigger than C.ICON_MAX or failed = null, never asked again.
     * Only while screen time is on and counting; an answer that comes after a clear or the switch going
     * off is dropped. With the helper restarting nothing is asked (the next time the app comes in front).
     */
    function fetchIcon() {
      const app = st.fgApp;
      if (S.stopped || !app || !app.path || app.key === C.UNKNOWN || !st.counting() || !opt().screentime || !ctx.enabled()) return;
      if (st.iconKnown(app.key) || S.iconAsking.has(app.key) || !ctx.sidecar.ready) return;
      const key = app.key, gen = st.gen;
      S.iconAsking.add(key);
      Promise.resolve(ctx.sidecar.send('icon', { path: app.path }, { timeoutMs: 3000 })).then((r) => {
        S.iconAsking.delete(key);
        if (S.stopped || st.gen !== gen || st.off) return;
        const url = r && r.ok && r.result && typeof r.result.icon === 'string' ? r.result.icon : null;
        if (r && !r.ok) refused(r.error, 'icon');
        st.setIcon(key, url);                  // C.iconOf: too big or not a picture = null
      }, () => { S.iconAsking.delete(key); });
    }

    /* ---- readings ----------------------------------------------------------------------------- */
    /** exe file name -> path from a reading's process list: power history's icons (memory only). */
    function notePaths(s) {
      if (!Array.isArray(s.procs) || !s.procs.length) return;
      const m = new Map();
      for (const p of s.procs) if (p && p.path && (p.file || p.path)) { const k = C.exeKey(p.file, p.path); if (!m.has(k)) m.set(k, String(p.path)); }
      S.livePaths = m;
    }
    function record(s) {
      notePaths(s);
      S.last = s; S.lastAt = clock();
      S.cost.samples++; S.cost.sampleMs += num(s.ms) || 0; S.cost.sidecarCpuMs += num(s.cpuMs) || 0;
    }
    /** A power.now reading inside the current bucket: its battery rate feeds the bucket's "rate" total. */
    function keepRate(s) {
      const c = s && s.battery && s.battery.capacity;
      if (S.base && c && num(c.rate) !== null && c.rate < 0) { S.rates.push(c.rate); if (S.rates.length > 200) S.rates.shift(); }
    }
    async function takeSample() {
      if (test && S.fakeSamples.length) { const s = S.fakeSamples.shift(); record(s); return { ok: true, sample: s }; }
      if (!ctx.sidecar.ready) return { ok: false, why: RESTARTING };
      const r = await ctx.sidecar.send('usage.sample', { procs: true }, { timeoutMs: 8000 });
      if (!r.ok) return { ok: false, why: refused(r.error, 'usage.sample') };
      const s = r.result;
      if (!s || typeof s !== 'object' || !(num(s.t) > 0)) { say('usage.sample: an unreadable reading'); return { ok: false, why: 'Windows sent an unreadable reading; try again in a moment' }; }
      cmdsOk();
      record(s);
      return { ok: true, sample: s };
    }

    /** The sampler's reading -> one stored bucket (or a gap, or the first base). */
    function bucketFrom(cur, now) {
      const prev = S.base;
      const b = { mantlePid: mantlePid(), slept: S.slept, acFlip: S.acFlip, rates: S.rates };
      S.base = cur; S.slept = false; S.acFlip = false; S.rates = [];
      if (!prev) return { first: true };
      const c0 = process.cpuUsage();
      const r = C.makeBucket(prev, cur, b);
      if (r.bucket) store.appendBucket(r.bucket, now);
      addCpu(c0);
      return r;
    }

    function flush(now, force) {
      if (!force && now - S.lastFlush < FLUSH_MS - 5000) return;
      const c0 = process.cpuUsage();
      st.prune(now);
      store.prune(now);
      if (st.dirty) { const err = store.saveScreentime(st.toJSON()); if (!err) st.dirty = false; }
      S.lastFlush = now;
      addCpu(c0);
    }

    /**
     * The clean-up on its own (General Counsel, 2026-11-18): screen-time days older than 30 and power
     * files older than 3 are deleted at start-up and then every 24 h (a setTimeout chain, unref'd),
     * whatever the switches say: screen time off, power off, the tray switch off (installed while off).
     */
    function housekeep() {
      if (S.stopped) return;
      const now = clock(), c0 = process.cpuUsage();
      try {
        st.prune(now);
        store.prune(now);
        if (st.dirty) { const err = store.saveScreentime(st.toJSON()); if (!err) st.dirty = false; }
        S.hkRuns++;
      } catch (e) { say(`the daily clean-up failed: ${e && e.message}`); }
      addCpu(c0);
    }
    function armHousekeep() {
      if (S.stopped) return;
      S.hkTimer = setTimeout(() => { S.hkTimer = null; housekeep(); armHousekeep(); }, housekeepMs);
      if (S.hkTimer.unref) S.hkTimer.unref();
    }

    /** One beat: the sleep check, screen time up to now (less the idle hold), one power reading, the flush. */
    async function runTick(now) {
      checkGap(now);
      S.lastTickAt = now;
      const o = opt();
      const c0 = process.cpuUsage();
      st.settle(now, o.idleMin * 60000);
      addCpu(c0);
      let result = { skipped: 'power use is off' };
      if (o.power && ctx.enabled()) {
        const s = await takeSample();
        result = s.ok ? bucketFrom(s.sample, now) : { failed: s.why };
      }
      if (S.watchSent === null && wanted()) sendWatch(true, o.idleMin).catch(() => {});
      flush(clock());
      return result;
    }

    function schedule() {
      if (S.timer || S.ticking || !wanted()) return;
      const delay = S.lastTickAt === null || fakeNow !== null ? (S.lastTickAt === null ? FIRST_MS : PERIOD) : Math.max(1000, S.lastTickAt + PERIOD - Date.now());
      S.timer = setTimeout(tick, delay);
      if (S.timer.unref) S.timer.unref();
    }
    function unschedule() { if (S.timer) { clearTimeout(S.timer); S.timer = null; } }
    async function tick() {
      S.timer = null;
      if (!wanted()) return;
      if (fakeNow === null) {      // a test clock drives its own beats (usage.test.sample {tick:true})
        S.ticking = true;
        try { await runTick(clock()); } catch (e) { say(`the 5-minute beat failed: ${e && e.message}`); }
        S.ticking = false;
      }
      schedule();
    }

    /** Bring everything in line with the tray switch and the options. */
    function apply() {
      if (S.stopped) return;
      const o = opt(), on = ctx.enabled(), now = clock();
      const stOn = on && o.screentime;
      st.setOn(stOn, now);
      const want = on && (o.power || o.screentime);
      const key = want ? `on:${o.idleMin}` : 'off';
      if (key !== S.watchSent && (want || S.watchSent !== null)) sendWatch(want, o.idleMin).catch(() => {});
      if (want) schedule();
      else {
        unschedule();
        if (st.dirty) flush(now, true);
        S.lastTickAt = null;   // off is not a sleep: the next beat after on starts the count afresh
        S.base = null; S.rates = [];
      }
      if (!o.power) { S.base = null; S.rates = []; }
      if (stOn && S.stOn === false) refreshFg().catch(() => {});
      if (!stOn && st.dirty) flush(now, true);
      S.stOn = stOn;
    }

    /* ---- commands ----------------------------------------------------------------------------- */
    const guard = (fn) => async (args) => {
      if (!ctx.enabled()) return { ok: false, off: true, reason: OFF };
      try { return await fn(args); } catch (e) { say(`a command failed: ${e && e.message}`); return { ok: false, reason: C.scrub(`Something went wrong: ${e && e.message ? e.message : e}`) }; }
    };
    const notesOf = (re) => store.notes.filter((n) => re.test(n));

    async function powerNow() {
      if (!opt().power) return { ok: false, reason: POWER_OFF };
      if (S.nowInflight) return S.nowInflight;
      S.nowInflight = (async () => {
        let prev = S.last && clock() - S.lastAt < NOW_FRESH_MS ? S.last : null;
        if (!prev) {
          const a = await takeSample();
          if (!a.ok) return { ok: false, reason: a.why };
          prev = a.sample;
          keepRate(prev);
          await sleep(NOW_GAP_MS);
        }
        const b = await takeSample();
        if (!b.ok) return { ok: false, reason: b.why };
        keepRate(b.sample);
        const c0 = process.cpuUsage();
        const rep = C.nowReply(prev, b.sample, { mantlePid: mantlePid() });
        addCpu(c0);
        rep.notes = rep.notes.concat(notesOf(/^Power/));
        return rep;
      })();
      try { return await S.nowInflight; } finally { S.nowInflight = null; }
    }

    function powerHistory(args) {
      const range = args && (args.range === 'today' || args.range === '24h') ? args.range : null;
      if (!range) return { ok: false, reason: 'Pick a range: today or the last 24 hours' };
      const now = clock();
      const c0 = process.cpuUsage();
      const h = C.history(store.buckets(now), range, now, S.livePaths);   // paths: the latest reading's, never the disk's
      addCpu(c0);
      const notes = notesOf(/^Power/);
      if (!opt().power) {
        notes.push('Power use is off in Mantle Settings, so nothing new is measured');
        if (h.empty) h.emptyWhy = 'Power use is off in Mantle Settings, so nothing was measured';
      }
      return Object.assign({ ok: true }, h, { notes });
    }

    function screentimeGet() {
      const now = clock();
      const o = opt();
      checkGap(now);
      const c0 = process.cpuUsage();
      const v = st.view(now);
      addCpu(c0);
      const notes = notesOf(/^Screen/);
      if (S.health.cmds) notes.push(NO_CMDS_PAGE);
      else if (S.watchWhy) notes.push(S.watchWhy);
      if (S.watchNote && o.screentime) notes.push(S.watchNote);
      // off: the page words it from on:false / paused:'off' (and invites turning it on)
      return { ok: true, on: o.screentime, idleMin: o.idleMin, keptDays: v.keptDays, today: v.today, days: v.days, since: v.since, current: v.current, paused: v.paused, notes };
    }

    /** Clear history, at once: the days, the apps, their icons and the open interval's past leave
     *  memory, and screentime.json with every screentime* copy is deleted from disk (not moved). */
    function screentimeClear() {
      const now = clock();
      const days = st.clear(now);
      S.iconAsking.clear();
      const err = store.clearScreentime();
      // nothing from before the clear is ever written back; a file that would not go is deleted again
      // at the next save (the record is empty then), and the reply says so now
      st.dirty = !!err;
      if (err) return { ok: false, reason: err };
      say(`screen time history cleared (${days} day${days === 1 ? '' : 's'})`);
      return { ok: true, cleared: days };
    }

    async function screentimeSet(args) {
      if (!args || typeof args.on !== 'boolean') return { ok: false, reason: 'Say whether screen time should be on or off' };
      const host = ctx.host && ctx.host.features;
      try {
        if (host && typeof host.setOption === 'function') {
          const r = await host.setOption(ctx.name, 'screentime', args.on);
          if (r && r.ok === false) return { ok: false, reason: r.reason || 'The setting was not saved' };
        } else ctx.settings.set('screentime', args.on);
      } catch (e) { return { ok: false, reason: e && e.message ? e.message : String(e) }; }
      apply();
      return { ok: true, on: opt().screentime };
    }

    async function icons(args) {
      const paths = args && Array.isArray(args.paths) ? args.paths.filter((p) => typeof p === 'string') : null;
      if (!paths) return { ok: false, reason: 'No program paths were given' };
      const out = { ok: true, icons: {}, why: {} };
      for (const p of paths.slice(0, ICONS_MAX)) {
        if (p.length > 1024 || !/^([a-zA-Z]:\\|\\\\)/.test(p)) { out.icons[p] = null; out.why[p] = 'This is not a program on this PC'; continue; }
        const k = p.toLowerCase();
        let c = S.icons.get(k);
        if (!c) {
          const r = await ctx.sidecar.send('icon', { path: p }, { timeoutMs: 3000 });
          if (r.ok) {
            c = r.result && r.result.icon ? { icon: r.result.icon, why: null } : { icon: null, why: 'Windows had no icon for it' };
            if (S.icons.size >= 500) S.icons.clear();
            S.icons.set(k, c);                                      // memory only; a failed call is asked again next time
          } else c = { icon: null, why: refused(r.error, 'icon') };
        }
        out.icons[p] = c.icon;
        if (!c.icon) out.why[p] = c.why;
      }
      if (paths.length > ICONS_MAX) for (const p of paths.slice(ICONS_MAX)) { out.icons[p] = null; out.why[p] = `Only ${ICONS_MAX} icons are handed over at a time`; }
      return out;
    }

    async function cost() {
      let watchCpuMs = null;
      if (ctx.sidecar.ready && S.watchSent && S.watchSent !== 'off') {
        const r = await ctx.sidecar.send('usage.watch', { op: 'status' }, { timeoutMs: 3000 });
        if (r.ok && r.result) watchCpuMs = num(r.result.cpuMs);
      }
      return C.costReply(S.cost, clock(), os.cpus().length, watchCpuMs);
    }

    ctx.command('state', async () => {
      const o = opt(), on = ctx.enabled();
      const r = { ok: true, on, power: o.power, screentime: o.screentime, idleMin: o.idleMin, reason: !on ? OFF : S.health.cmds ? NO_CMDS_PAGE : null };
      if (!on) r.off = true;
      return r;
    });
    ctx.command('power.now', guard(powerNow));
    ctx.command('power.history', guard(powerHistory));
    ctx.command('screentime.get', guard(screentimeGet));
    ctx.command('screentime.clear', guard(screentimeClear));
    ctx.command('screentime.set', guard(screentimeSet));
    ctx.command('icons', guard(icons));
    ctx.command('cost', guard(cost));

    if (test) {
      // deterministic e2e: a fake clock, injected events, fake readings (test instances only)
      ctx.command('test.clock', async (a) => {
        const n = a && a.now;
        fakeNow = typeof n === 'number' && Number.isFinite(n) ? n : null;
        S.lastTickAt = null;        // a beat from the other clock means nothing on this one
        S.lastFlush = clock();      // ...and so does the last flush and the cost's start
        S.cost.since = Math.min(S.cost.since, clock());
        return { ok: true, now: clock(), fake: fakeNow !== null };
      });
      ctx.command('test.inject', async (a) => {
        const events = a && Array.isArray(a.events) ? a.events : null;
        if (!events) return { ok: false, reason: 'events must be a list' };
        let applied = 0;
        for (const ev of events) {
          if (!ev || typeof ev.kind !== 'string') continue;
          if (typeof ev.at === 'number' && Number.isFinite(ev.at)) fakeNow = ev.at;
          onEvent(ev, clock());
          applied++;
        }
        return { ok: true, applied, paused: st.paused, current: st.fgApp ? { key: st.fgApp.key, name: st.fgApp.name } : null, counting: st.counting() };
      });
      ctx.command('test.sample', async (a) => {
        if (!a || !a.sample || typeof a.sample !== 'object') return { ok: false, reason: 'sample must be an object' };
        S.fakeSamples.push(a.sample);
        if (!a.tick) return { ok: true, queued: S.fakeSamples.length };
        if (!ctx.enabled()) return { ok: false, off: true, reason: OFF };
        const r = await runTick(clock());
        return Object.assign({ ok: true }, r);
      });
      ctx.command('test.housekeep', async () => ({ ok: true, runs: S.hkRuns, armed: !!S.hkTimer, refd: S.hkTimer && typeof S.hkTimer.hasRef === 'function' ? S.hkTimer.hasRef() : null, everyMs: housekeepMs }));
    }

    /* ---- wiring ------------------------------------------------------------------------------- */
    ctx.sidecar.on('usage', (ev) => onEvent(ev, clock()));
    ctx.sidecar.on('battery', (ev) => { if (ev && ev.via === 'ac') S.acFlip = true; });
    ctx.sidecar.onReady(() => { S.watchSent = null; apply(); refreshFg().catch(() => {}); });
    ctx.onEnabled(() => apply());
    if (typeof ctx.onOption === 'function') ctx.onOption('*', () => apply());
    apply();
    housekeep();          // the 30-day / 3-day delete runs at start-up whatever is on...
    armHousekeep();       // ...and every 24 h after

    return {
      stop() {
        if (S.stopped) return;
        const now = clock();
        try { st.cut(now); } catch (_) { /* nothing open */ }
        try { if (st.dirty) store.saveScreentime(st.toJSON()); } catch (_) { /* said in the note */ }
        S.stopped = true;
        unschedule();
        if (S.hkTimer) { clearTimeout(S.hkTimer); S.hkTimer = null; }
        if (ctx.sidecar.ready && S.watchSent && S.watchSent !== 'off') ctx.sidecar.send('usage.watch', { on: false }, { timeoutMs: 1500 }).catch(() => {});
      },
    };
  },
};
