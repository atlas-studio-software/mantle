/* _usage-core.js - the PURE half of "Power use and screen time" (lane mantle-ps-node, 2026-11-18).
 * No fs, no timers, no clock: every function takes `now` (UTC ms). Proved by _usage-core.test.js.
 *
 *  - ScreenTime: the foreground-time accumulator. One open interval at most; it counts while an app
 *    (not the shell) is in front, the screen is not locked, the PC is awake, the user is not idle and
 *    the switch is on. Time is split at local midnight and on every local hour; days are local dates;
 *    30 days are kept. Never a window title: only the exe key, name, file and path.
 *  - attribute / nowReply: per-app power from two `usage.sample` readings (sidecar/Usage.cs). An app's
 *    watts = its share of process CPU time x RAPL cores + its share of GPU running time x RAPL graphics.
 *    Fixed rows: Windows (every system group merged), Rest of the processor (package minus what the
 *    apps got), Screen and the rest of the PC (battery total minus package; battery only).
 *  - makeBucket: one stored 5-minute bucket from two samples, or a gap with its reason.
 *  - history: the "Today" / "Last 24 h" view from stored buckets.
 *  - costReply: Mantle's own cost of measuring.
 *
 * Units: energy counters are cumulative picowatt-hours, so W = dpWh x 3.6e-9 / ds (probed 2026-11-18:
 * two reads 2002 ms apart gave 3,956,510,833 pWh = 7.11 W, the cooked counter said 7113.9 mW). CPU and
 * GPU times are 100 ns units. Watts in replies are mW integers; energy in history is Wh.
 */
'use strict';
const TM = require('./_taskmgr-core.js');

const HOUR = 3600000;
const KEEP_DAYS = 30;
const POWER_KEEP_DAYS = 3;
const BUCKET_MIN_S = 60;
const BUCKET_MAX_S = 450;
const TOP_NOW = 8;
const TOP_STORED = 10;
const TOP_DAY = 8;

const CAPTION = "Estimated from each app's processor and graphics use. Windows' own per-app energy needs administrator access.";
const WHY = {
  ac: 'Windows only reports total draw on battery',
  nobattery: 'This PC has no battery',
  notYet: 'Windows has not measured the battery draw yet',
  noEnergy: "This PC does not report the processor's energy use",
  noSplit: "Without the processor's energy reading the rest cannot be split out",
};
const NAMES = { windows: 'Windows', 'chip-rest': 'Rest of the processor', 'pc-rest': 'Screen and the rest of the PC' };

const num = (x) => (typeof x === 'number' && Number.isFinite(x) ? x : null);
const r4 = (x) => Math.round(x * 10000) / 10000;
const r3 = (x) => Math.round(x * 1000) / 1000;
const pad = (n) => String(n).padStart(2, '0');
const low = (s) => String(s || '').toLowerCase();

/* ---- local calendar ------------------------------------------------------------------------- */

function dateKey(t) { const d = new Date(t); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`; }
function dayStart(t) { const d = new Date(t); return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime(); }
function hourStart(t) { const d = new Date(t); return new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours()).getTime(); }
/** The next local hour boundary after t (never more than an hour on, never t itself). */
function nextHour(t) {
  const d = new Date(t);
  const n = new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours() + 1).getTime();
  return n > t && n - t <= HOUR ? n : t - (((t % HOUR) + HOUR) % HOUR) + HOUR;
}
/** The local date `days` before t's date (negative = after). */
function dateBefore(t, days) { const d = new Date(t); return dateKey(new Date(d.getFullYear(), d.getMonth(), d.getDate() - days, 12).getTime()); }
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

/* ---- the Mantle-window rule ----------------------------------------------------------------- */

/** "123" / 123 / "0x7b" -> "123" (decimal string), else null. */
function normHwnd(h) {
  if (h === null || h === undefined || h === '') return null;
  try {
    const b = typeof h === 'bigint' ? h : typeof h === 'number' ? BigInt(Math.trunc(h)) : BigInt(String(h).trim());
    return b > 0n ? b.toString() : null;
  } catch (_) { return null; }
}

/**
 * Is the window in front one of Mantle's own? wins = [{hwnd, focusable, onTop}] (Mantle's
 * BrowserWindows). -> 'app' (a focusable, not always-on-top Mantle window: Mantle Settings, Files, task
 * manager: counts as the app "Mantle"), 'shell' (its bar, desktop, launcher, pop-ups, or any other
 * window of Mantle's process), or null (not Mantle's).
 */
function mantleWindow(ev, wins, mantlePid) {
  const h = normHwnd(ev && ev.hwnd);
  const w = h ? (wins || []).find((x) => x && normHwnd(x.hwnd) === h) : null;
  if (w) return w.focusable && !w.onTop ? 'app' : 'shell';
  if (mantlePid && ev && ev.pid === mantlePid) return 'shell';
  return null;
}

/** The app an `fg` event names: {key, name, file, path}. key = lower-cased exe path, else file:<name>. */
function appOf(ev) {
  if (ev && ev.mantle === 'app') return { key: 'mantle', name: 'Mantle', file: ev.file ? String(ev.file) : null, path: ev.path ? String(ev.path) : null };
  const p = { pid: ev && ev.pid, file: ev && ev.file ? String(ev.file) : '', path: ev && ev.path ? String(ev.path) : null, desc: ev && ev.desc ? String(ev.desc) : null };
  if (!p.path && !p.file) return { key: 'file:unknown', name: 'Unknown app', file: null, path: null };
  return { key: TM.identityOf(p), name: TM.nameOf(p), file: p.file || null, path: p.path };
}

/* ---- screen time ---------------------------------------------------------------------------- */

function freshScreentime() { return { v: 1, apps: {}, days: {} }; }

/**
 * Check a parsed screentime.json. -> {data} or {problem: words}. Small value faults (a negative
 * number, a short hours list) are mended; a wrong shape is a problem (the store keeps the file as .bad).
 */
function parseScreentime(obj) {
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return { problem: 'it is not a screen time record' };
  if (obj.v !== 1) return { problem: `it is version ${JSON.stringify(obj.v)}, not 1` };
  if (!obj.apps || typeof obj.apps !== 'object' || Array.isArray(obj.apps)) return { problem: 'its app list is missing' };
  if (!obj.days || typeof obj.days !== 'object' || Array.isArray(obj.days)) return { problem: 'its day list is missing' };
  const data = freshScreentime();
  for (const [k, a] of Object.entries(obj.apps)) {
    if (!a || typeof a !== 'object') continue;
    // only these three fields are ever kept (a stray title from anywhere is dropped here)
    data.apps[k] = { name: String(a.name || k), file: a.file ? String(a.file) : null, path: a.path ? String(a.path) : null };
  }
  for (const [date, d] of Object.entries(obj.days)) {
    if (!DATE_RE.test(date) || !d || typeof d !== 'object') return { problem: `its day "${String(date).slice(0, 20)}" is not readable` };
    const day = { apps: {}, hours: new Array(24).fill(0) };
    for (const [k, s] of Object.entries(d.apps || {})) { const v = num(s); if (v !== null && v > 0) day.apps[k] = v; }
    if (Array.isArray(d.hours)) for (let h = 0; h < 24; h++) { const v = num(d.hours[h]); day.hours[h] = v !== null && v > 0 ? v : 0; }
    data.days[date] = day;
  }
  return { data };
}

class ScreenTime {
  constructor(data) {
    this.data = data && data.v === 1 ? data : freshScreentime();
    this.fgApp = null;                     // {key, name, file, path} of the app in front, null = the shell
    this.off = false;                      // the switch (or the tray) is off
    this.locked = false;
    this.asleep = false;
    this.idle = false;
    this.open = null;                      // {key, name, file, path, since, startedAt}
    this.floor = 0;                        // nothing is ever counted before this (the last stop)
    this.dirty = false;
    this.lastAt = 0;                       // the last time anything happened (events, settles)
  }

  counting() { return !!this.fgApp && !this.off && !this.locked && !this.asleep && !this.idle; }
  get paused() { return this.off ? 'off' : this.asleep ? 'asleep' : this.locked ? 'locked' : this.idle ? 'idle' : null; }

  _add(app, from, to, into) {
    if (!(to > from)) return;
    const data = into || this.data;
    data.apps[app.key] = { name: app.name, file: app.file || null, path: app.path || null };
    let t = from;
    while (t < to) {
      const end = Math.min(to, nextHour(t));
      const date = dateKey(t);
      const day = data.days[date] || (data.days[date] = { apps: {}, hours: new Array(24).fill(0) });
      const s = (end - t) / 1000;
      day.apps[app.key] = r3((day.apps[app.key] || 0) + s);
      const h = new Date(t).getHours();
      day.hours[h] = r3((day.hours[h] || 0) + s);
      t = end;
    }
    if (!into) this.dirty = true;
  }

  _start(at) {
    if (this.open || !this.counting()) return;
    const since = Math.max(at, this.floor);
    this.open = Object.assign({}, this.fgApp, { since, startedAt: since });
  }

  _stop(at) {
    if (!this.open) return;
    const end = Math.max(this.open.since, at);
    this._add(this.open, this.open.since, end);
    this.floor = Math.max(this.floor, end);
    this.open = null;
  }

  /**
   * Count the open interval up to now - holdMs (and keep it open). holdMs = the idle threshold: an
   * `idle` event can still cut the interval back to the last input, so that much stays uncounted.
   */
  settle(now, holdMs) {
    const upto = now - Math.max(0, holdMs || 0);
    if (this.open && upto > this.open.since) { this._add(this.open, this.open.since, upto); this.open.since = upto; this.floor = Math.max(this.floor, upto); }
    if (now > this.lastAt) this.lastAt = now;
  }

  /**
   * Close the open interval at `at` (a sleep the events missed: the sampler's tick was late, or stop).
   * With `now`, counting starts again at now if nothing pauses it.
   */
  cut(at, now) { this._stop(at); if (now !== undefined) this._start(now); }

  /**
   * One event at `now`. ev.kind: fg {app | pid,file,path,desc,mantle} | shell | lock | unlock |
   * suspend | resume | idle {idleMs} | active {agoMs}. Returns true when it changed anything.
   */
  apply(ev, now) {
    const kind = ev && ev.kind;
    const was = this.counting();
    let stopAt = now, startAt = now;
    switch (kind) {
      case 'fg': {
        const app = ev.app || appOf(ev);
        if (this.fgApp && this.fgApp.key === app.key) { this.fgApp = app; if (this.open) Object.assign(this.open, { name: app.name, file: app.file, path: app.path }); this.lastAt = Math.max(this.lastAt, now); return false; }
        if (was) this._stop(now);
        this.fgApp = app;
        this._start(now);
        this.lastAt = Math.max(this.lastAt, now);
        return true;
      }
      case 'shell': this.fgApp = null; break;
      case 'lock': this.locked = true; break;
      case 'unlock': this.locked = false; break;
      case 'suspend': this.asleep = true; break;
      case 'resume': this.asleep = false; break;
      case 'idle': {
        this.idle = true;
        const ago = num(ev.idleMs);
        stopAt = now - (ago !== null && ago > 0 ? ago : 0);
        break;
      }
      case 'active': {
        this.idle = false;
        const ago = num(ev.agoMs);
        startAt = now - (ago !== null && ago > 0 ? ago : 0);
        break;
      }
      default: return false;
    }
    this.lastAt = Math.max(this.lastAt, now);
    const is = this.counting();
    if (was && !is) this._stop(stopAt);
    else if (!was && is) this._start(Math.min(startAt, now));
    return was !== is;
  }

  /** The switch: off closes the open interval at once; history stays. */
  setOn(on, now) {
    const was = this.counting();
    this.off = !on;
    if (was && this.off) this._stop(now);
    else if (!was && this.counting()) this._start(now);
  }

  /** Keep 30 local days (today included); forget apps no kept day names. */
  prune(now) {
    const cutoff = dateBefore(now, KEEP_DAYS - 1);
    let gone = 0;
    for (const date of Object.keys(this.data.days)) if (date < cutoff) { delete this.data.days[date]; gone++; }
    const used = new Set();
    for (const d of Object.values(this.data.days)) for (const k of Object.keys(d.apps)) used.add(k);
    if (this.open) used.add(this.open.key);
    for (const k of Object.keys(this.data.apps)) if (!used.has(k)) { delete this.data.apps[k]; gone++; }
    if (gone) this.dirty = true;
    return gone;
  }

  /** Clear history: every day and app goes; an app in front keeps counting from now. */
  clear(now) {
    const had = Object.keys(this.data.days).length;
    this.data = freshScreentime();
    if (this.open) { this.open.since = now; this.open.startedAt = now; }
    this.floor = Math.max(this.floor, now);
    this.dirty = true;
    return had;
  }

  _appRow(key, seconds, data) {
    const a = data.apps[key] || (this.open && this.open.key === key ? this.open : null) || {};
    return { key, name: a.name || key, path: a.path || null, seconds: Math.round(seconds) };
  }

  _day(date, top, data) {
    const d = data.days[date];
    const apps = d ? Object.entries(d.apps).map(([k, s]) => this._appRow(k, s, data)).filter((a) => a.seconds > 0) : [];
    apps.sort((a, b) => b.seconds - a.seconds || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    const totalSec = d ? Math.round(Object.values(d.apps).reduce((s, x) => s + x, 0)) : 0;
    return { date, totalSec, hours: d ? d.hours.map((x) => Math.round(x)) : new Array(24).fill(0), apps: top ? apps.slice(0, top) : apps };
  }

  /**
   * The screentime.get body (without ok/on/idleMin/notes). The open interval is added to a copy up to
   * now (nothing stored changes), so the page sees the running minute.
   */
  view(now) {
    if (now > this.lastAt) this.lastAt = now;
    const dates = [];
    for (let i = 6; i >= 0; i--) dates.push(dateBefore(now, i));
    const v = { apps: Object.assign({}, this.data.apps), days: {} };
    for (const d of dates) { const x = this.data.days[d]; if (x) v.days[d] = { apps: Object.assign({}, x.apps), hours: x.hours.slice() }; }
    if (this.open && now > this.open.since) this._add(this.open, this.open.since, now, v);
    const today = this._day(dateKey(now), 0, v);
    const days = dates.map((date) => { const d = this._day(date, TOP_DAY, v); delete d.hours; return d; });
    const paused = this.paused;
    const current = this.fgApp && (paused === null || paused === 'idle') ? { key: this.fgApp.key, name: this.fgApp.name } : null;
    return { keptDays: KEEP_DAYS, today, days, since: this.open ? this.open.startedAt : null, current, paused };
  }

  /** What goes to disk: {v, apps, days} only. */
  toJSON() { return { v: 1, apps: this.data.apps, days: this.data.days }; }
}

/* ---- power: energy, shares, rows ------------------------------------------------------------ */

/** mW averages from two cumulative RAPL reads. -> {ok, pkgMw, coresMw, gfxMw, why, reset} */
function energyMw(prev, cur) {
  const a = prev && prev.energy, b = cur && cur.energy;
  if (!a || !b || !a.ok || !b.ok) return { ok: false, pkgMw: null, coresMw: null, gfxMw: null, why: (b && !b.ok && b.reason) || (a && !a.ok && a.reason) || WHY.noEnergy };
  const ta = num(a.time), tb = num(b.time);
  const dt = ta !== null && tb !== null && tb > ta ? (tb - ta) / 1000 : (num(cur.t) - num(prev.t)) / 1000;
  for (const k of ['pkg', 'cores', 'gfx']) {
    if (num(a[k]) !== null && num(b[k]) !== null && b[k] < a[k]) return { ok: false, reset: true, pkgMw: null, coresMw: null, gfxMw: null, why: 'the energy meter restarted' };
  }
  if ((ta !== null && tb !== null && tb < ta)) return { ok: false, reset: true, pkgMw: null, coresMw: null, gfxMw: null, why: 'the energy meter restarted' };
  if (!(dt > 0) || num(a.pkg) === null || num(b.pkg) === null) return { ok: false, pkgMw: null, coresMw: null, gfxMw: null, why: 'the energy meter did not move' };
  const mw = (k) => (num(a[k]) !== null && num(b[k]) !== null ? ((b[k] - a[k]) * 3.6e-9 / dt) * 1000 : null);
  return { ok: true, pkgMw: mw('pkg'), coresMw: mw('cores'), gfxMw: mw('gfx'), why: null };
}

/** Where the PC gets its power now: 'battery' | 'ac' | 'nobattery' | 'unknown'. */
function sourceOf(b) {
  if (!b || b.ok === false) return 'unknown';
  if (b.present === false) return 'nobattery';
  if (b.ac === true) return 'ac';
  if (b.ac === false) return 'battery';
  return 'unknown';
}

const capOf = (b) => (b && b.capacity && typeof b.capacity === 'object' ? b.capacity : null);
const rateOf = (b) => { const c = capOf(b); return c ? num(c.rate) : null; };

/** The battery block of power.now. */
function batteryBlock(b) {
  const rate = rateOf(b);
  if (!b || b.ok === false) return { percent: null, charging: false, chargeMw: null, secondsLeft: null, why: (b && b.reason) || 'Windows did not report the battery' };
  if (b.present === false) return { percent: null, charging: false, chargeMw: null, secondsLeft: null, why: WHY.nobattery };
  return { percent: num(b.percent), charging: !!b.charging, chargeMw: rate !== null && rate > 0 ? rate : null, secondsLeft: num(b.secondsLeft), why: b.reason || null };
}

/** A short-window total (power.now): the Power Meter (= discharge rate), else the battery rate. */
function nowTotal(cur) {
  const src = sourceOf(cur && cur.battery);
  if (src === 'ac') return { mw: null, why: WHY.ac };
  if (src === 'nobattery') return { mw: null, why: WHY.nobattery };
  if (src === 'unknown') return { mw: null, why: (cur && cur.battery && cur.battery.reason) || 'Windows did not say whether this PC runs on battery' };
  const m = cur.meter;
  if (m && m.ok && num(m.mw) > 0) return { mw: Math.round(m.mw), why: null };
  const rate = rateOf(cur.battery);
  if (rate !== null && rate < 0) return { mw: -rate, why: null };
  return { mw: null, why: WHY.notYet };
}

function cpuDeltas(prev, cur) {
  const before = new Map();
  for (const p of (prev && prev.procs) || []) if (p && p.pid > 0) before.set(TM.keyOf(p), num(p.cpu));
  const havePrev = before.size > 0;
  const cap = Math.max(0, (num(cur.t) - num(prev.t)) * 10000 * Math.max(1, cur.cpus || 1));
  const d = new Map();
  for (const p of cur.procs || []) {
    if (!p || !(p.pid > 0) || num(p.cpu) === null) continue;
    const b = before.get(TM.keyOf(p));
    // a process the earlier read did not have started inside the window: all of its time is new
    const x = b !== undefined && b !== null ? p.cpu - b : havePrev ? Math.min(p.cpu, cap) : 0;
    if (x > 0) d.set(p.pid, x);
  }
  return d;
}

function gpuDeltas(prev, cur) {
  const a = prev && prev.gpu, b = cur && cur.gpu;
  const d = new Map();
  if (!b || !b.ok || !b.byPid || !a || !a.ok || !a.byPid) return { ok: false, d, why: (b && !b.ok && b.reason) || (a && !a.ok && a.reason) || 'Windows did not report graphics use per app' };
  const cap = Math.max(0, (num(cur.t) - num(prev.t)) * 10000 * 16);
  for (const [pid, v] of Object.entries(b.byPid)) {
    const x = num(v); if (x === null) continue;
    const p = num(a.byPid[pid]);
    const dx = p !== null ? x - p : Math.min(x, cap);
    if (dx > 0) d.set(Number(pid), dx);
  }
  return { ok: true, d, why: null };
}

/**
 * Per-app attribution between two samples. o: {mantlePid, totalMw (null = unknown), totalWhy}.
 * -> {shareOf, energy, totalMw, apps:[row...] (all, sorted), windows:row, chipRest:row, pcRest:row, notes}
 * row = {key, kind, name, path, share, mw, why, cpuShare}.
 */
function attribute(prev, cur, o) {
  const opts = o || {};
  const notes = [];
  const e = energyMw(prev, cur);
  const hasProcs = Array.isArray(cur.procs) && cur.procs.length > 0 && Array.isArray(prev.procs) && prev.procs.length > 0;
  const groups = hasProcs ? TM.buildGroups(cur, null, { mantlePid: opts.mantlePid }).groups : [];
  const cpu = hasProcs ? cpuDeltas(prev, cur) : new Map();
  const gpu = gpuDeltas(prev, cur);
  if (!hasProcs) notes.push('Windows did not list the running apps this time, so nothing is split per app');

  const rows = new Map();
  const known = new Set();
  for (const g of groups) {
    const kind = g.kind === 'windows' ? 'windows' : 'app';
    const key = g.kind === 'mantle' ? 'mantle' : g.kind === 'windows' ? 'windows' : g.identity;
    let r = rows.get(key);
    if (!r) rows.set(key, r = { key, kind, name: kind === 'windows' ? NAMES.windows : g.name, path: kind === 'windows' ? null : g.path || null, cpu: 0, gpu: 0 });
    for (const m of g.procs) { r.cpu += cpu.get(m.pid) || 0; r.gpu += gpu.d.get(m.pid) || 0; known.add(m.pid); }
  }
  if (!rows.has('windows')) rows.set('windows', { key: 'windows', kind: 'windows', name: NAMES.windows, path: null, cpu: 0, gpu: 0 });
  let cpuSum = 0, gpuSum = 0;
  for (const r of rows.values()) { cpuSum += r.cpu; gpuSum += r.gpu; }   // GPU time of pids not listed is left out of the split

  const totalMw = num(opts.totalMw);
  let shareOf;
  let pkg = null, cores = null, gfx = null, attributed = 0;
  if (e.ok) {
    pkg = Math.max(0, e.pkgMw);
    gfx = e.gfxMw !== null ? Math.max(0, e.gfxMw) : 0;
    cores = e.coresMw !== null ? Math.max(0, e.coresMw) : Math.max(0, pkg - gfx);
    if (e.coresMw === null) notes.push('The processor does not report its cores separately, so app power is split from the whole package');
    if (!gpu.ok && hasProcs) notes.push(`Graphics use per app could not be read (${gpu.why}), so graphics power is in "${NAMES['chip-rest']}"`);
    shareOf = totalMw !== null ? 'pc' : 'chip';
  } else {
    shareOf = 'cpu';
  }
  for (const r of rows.values()) {
    r.cpuShare = cpuSum > 0 ? r.cpu / cpuSum : 0;
    r.gpuShare = gpu.ok && gpuSum > 0 ? r.gpu / gpuSum : 0;
    r.mwRaw = e.ok ? r.cpuShare * cores + r.gpuShare * gfx : null;
    if (e.ok) attributed += r.mwRaw;
  }
  const chipRestMw = e.ok ? Math.max(0, pkg - attributed) : null;
  const pcRestMw = e.ok && totalMw !== null ? Math.max(0, totalMw - pkg) : null;
  const denom = shareOf === 'pc' ? Math.max(totalMw, pkg, attributed) : shareOf === 'chip' ? Math.max(pkg, attributed) : 0;

  const out = (r) => ({
    key: r.key, kind: r.kind, name: r.name, path: r.path,
    share: shareOf === 'cpu' ? r4(r.cpuShare) : denom > 0 ? r4(r.mwRaw / denom) : 0,
    mw: e.ok ? Math.round(r.mwRaw) : null, why: null, cpuShare: r4(r.cpuShare),
  });
  const apps = [...rows.values()].filter((r) => r.kind === 'app').map(out)
    .sort((a, b) => b.share - a.share || (b.mw || 0) - (a.mw || 0) || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const windows = out(rows.get('windows'));
  const chipRest = { key: 'chip-rest', kind: 'chip-rest', name: NAMES['chip-rest'], path: null,
    share: e.ok && denom > 0 ? r4(chipRestMw / denom) : null, mw: e.ok ? Math.round(chipRestMw) : null, why: e.ok ? null : e.why };
  let pcWhy = null;
  if (!e.ok) pcWhy = totalMw !== null ? WHY.noSplit : opts.totalWhy || WHY.noEnergy;
  else if (totalMw === null) pcWhy = opts.totalWhy || WHY.notYet;
  const pcRest = { key: 'pc-rest', kind: 'pc-rest', name: NAMES['pc-rest'], path: null,
    share: pcRestMw !== null && denom > 0 ? r4(pcRestMw / denom) : null, mw: pcRestMw !== null ? Math.round(pcRestMw) : null, why: pcWhy };
  return { shareOf, energy: e, pkg, cores, gfx, totalMw, apps, windows, chipRest, pcRest, notes, gpuOk: gpu.ok };
}

function captionFor(shareOf) {
  if (shareOf === 'pc') return `${CAPTION} Shares are of the whole PC.`;
  if (shareOf === 'chip') return `${CAPTION} Shares are of the processor: Windows only reports the whole PC's draw on battery.`;
  if (shareOf === 'cpu') return `${CAPTION} This PC does not report its processor's energy, so shares are of processor time and there are no watts.`;
  return CAPTION;
}

const strip = (r) => ({ key: r.key, kind: r.kind, name: r.name, path: r.path, share: r.share, mw: r.mw, why: r.why });

/** The usage.power.now reply from two samples. o: {mantlePid}. */
function nowReply(prev, cur, o) {
  const total = nowTotal(cur);
  const a = attribute(prev, cur, { mantlePid: o && o.mantlePid, totalMw: total.mw, totalWhy: total.why });
  const e = a.energy;
  return {
    ok: true, at: num(cur.t), windowMs: num(cur.t) - num(prev.t),
    source: sourceOf(cur.battery), battery: batteryBlock(cur.battery), total,
    chip: { pkgMw: e.ok ? Math.round(a.pkg) : null, coresMw: e.ok ? Math.round(a.cores) : null, gfxMw: e.ok && e.gfxMw !== null ? Math.round(a.gfx) : null, why: e.ok ? null : e.why },
    shareOf: a.shareOf,
    rows: [...a.apps.slice(0, TOP_NOW).map(strip), strip(a.windows), strip(a.chipRest), strip(a.pcRest)],
    caption: captionFor(a.shareOf), notes: a.notes,
  };
}

/* ---- power: stored buckets ------------------------------------------------------------------ */

/**
 * One 5-minute bucket from two samples. o: {mantlePid, slept (a suspend/resume came in between),
 * acFlip (a charger change came in between), rates: [battery rate readings in between, mW]}.
 * -> {bucket} or {gap: words}. bucket = {t (start, UTC ms), dur (s), pkg, cores, gfx, rest (mW, the
 * "Rest of the processor"), total|null, totalHow, ac, pct, apps:[[key, name, mW|null, cpuShare]]}.
 */
function makeBucket(prev, cur, o) {
  const opts = o || {};
  if (!prev || !cur) return { gap: 'there is no earlier reading' };
  const dur = (num(cur.t) - num(prev.t)) / 1000;
  if (!(dur >= BUCKET_MIN_S) || dur > BUCKET_MAX_S) return { gap: `the readings are ${Math.round(dur)} s apart, not about 5 minutes` };
  if (opts.slept) return { gap: 'the PC slept in between' };
  const e = energyMw(prev, cur);
  if (e.reset) return { gap: 'the energy meter restarted' };
  const pb = prev.battery, cb = cur.battery;
  const pc = capOf(pb), cc = capOf(cb);
  const flip = !!opts.acFlip || (pb && cb && pb.ac !== cb.ac);
  if (flip && pc && cc && num(cc.remaining) > num(pc.remaining)) return { gap: 'the charger was plugged in' };

  let total = null, totalHow = null, totalWhy = null;
  const src = sourceOf(cb);
  if (src === 'battery') {
    const drop = pc && cc ? num(pc.remaining) - num(cc.remaining) : null;
    if (sourceOf(pb) === 'battery' && !flip && drop !== null && drop > 0) { total = Math.round((drop * 3600) / dur); totalHow = 'capacity'; }
    else {
      const rates = [...(opts.rates || []), rateOf(pb), rateOf(cb)].filter((x) => num(x) !== null && x < 0);
      if (rates.length) { total = Math.round(-rates.reduce((s, x) => s + x, 0) / rates.length); totalHow = 'rate'; }
      else totalWhy = WHY.notYet;
    }
  } else totalWhy = src === 'ac' ? WHY.ac : src === 'nobattery' ? WHY.nobattery : 'Windows did not say whether this PC runs on battery';

  const a = attribute(prev, cur, { mantlePid: opts.mantlePid, totalMw: total, totalWhy });
  const apps = a.apps.filter((r) => (r.mw || 0) > 0 || r.cpuShare > 0).slice(0, TOP_STORED).map((r) => [r.key, r.name, r.mw, r.cpuShare]);
  apps.push(['windows', NAMES.windows, a.windows.mw, a.windows.cpuShare]);
  return { bucket: {
    t: num(prev.t), dur: Math.round(dur * 10) / 10,
    pkg: e.ok ? Math.round(a.pkg) : null, cores: e.ok ? Math.round(a.cores) : null, gfx: e.ok && e.gfxMw !== null ? Math.round(a.gfx) : null,
    rest: e.ok ? a.chipRest.mw : null,
    total, totalHow, ac: cb && typeof cb.ac === 'boolean' ? cb.ac : null, pct: cb ? num(cb.percent) : null, apps,
  } };
}

/** A stored line back to a bucket, or null when it is not one. */
function checkBucket(b) {
  if (!b || typeof b !== 'object' || num(b.t) === null || !(num(b.dur) > 0) || !Array.isArray(b.apps)) return null;
  return b;
}

/* ---- power: history ------------------------------------------------------------------------- */

function rangeOf(range, now) {
  if (range === 'today') return { from: dayStart(now), to: now };
  const h = new Date(hourStart(now));
  return { from: new Date(h.getFullYear(), h.getMonth(), h.getDate(), h.getHours() - 23).getTime(), to: now };
}

/**
 * The usage.power.history body (without ok/notes) from stored buckets.
 * range 'today' = local midnight to now; '24h' = the 24 local hours up to now (the current one partial).
 */
function history(buckets, range, now) {
  const rg = range === 'today' ? 'today' : '24h';
  const { from, to } = rangeOf(rg, now);
  const slots = [];
  for (let t = from; t < to;) { const end = Math.min(nextHour(t), to); slots.push({ t, end, n: 0, chip: 0, chipNull: false, total: 0, totalNull: false, ac: 0, bat: 0, ms: 0 }); t = end; }
  const inRange = [];
  for (const b of buckets || []) {
    const s = b.t, e = b.t + b.dur * 1000;
    if (e > from && s < to) inRange.push(b);
  }
  const rows = new Map();
  let chipWh = 0, totalWh = 0, coveredMs = 0, cpuSec = 0;
  const allTotal = inRange.length > 0 && inRange.every((b) => b.total !== null && b.total !== undefined && b.pkg !== null && b.pkg !== undefined);
  const allPkg = inRange.length > 0 && inRange.every((b) => b.pkg !== null && b.pkg !== undefined);
  const shareOf = !inRange.length ? null : allTotal ? 'pc' : allPkg ? 'chip' : 'cpu';
  let chipRestWh = 0, pcRestWh = 0;
  for (const b of inRange) {
    const s = b.t, e = b.t + b.dur * 1000;
    const ov = Math.max(0, Math.min(e, to) - Math.max(s, from));
    if (!ov) continue;
    const h = ov / HOUR;
    coveredMs += ov;
    if (b.pkg !== null && b.pkg !== undefined) chipWh += (b.pkg * h) / 1000;
    if (b.total !== null && b.total !== undefined) totalWh += (b.total * h) / 1000;
    const rest = b.rest !== null && b.rest !== undefined ? b.rest : b.pkg !== null && b.pkg !== undefined ? Math.max(0, b.pkg - (b.cores || 0) - (b.gfx || 0)) : null;
    if (rest !== null) chipRestWh += (rest * h) / 1000;
    if (b.total !== null && b.total !== undefined && b.pkg !== null && b.pkg !== undefined) pcRestWh += (Math.max(0, b.total - b.pkg) * h) / 1000;
    cpuSec += ov / 1000;
    for (const tup of b.apps) {
      if (!Array.isArray(tup) || typeof tup[0] !== 'string') continue;
      const [key, name, mw, share] = tup;
      let r = rows.get(key);
      if (!r) rows.set(key, r = { key, name: key === 'windows' ? NAMES.windows : String(name || key), wh: 0, cpu: 0 });
      if (num(mw) !== null) r.wh += (mw * h) / 1000;
      if (num(share) !== null) r.cpu += share * (ov / 1000);
    }
    for (const sl of slots) {
      const o2 = Math.max(0, Math.min(e, sl.end) - Math.max(s, sl.t));
      if (!o2) continue;
      const h2 = o2 / HOUR;
      sl.n++; sl.ms += o2;
      if (b.pkg !== null && b.pkg !== undefined) sl.chip += (b.pkg * h2) / 1000; else sl.chipNull = true;
      if (b.total !== null && b.total !== undefined) sl.total += (b.total * h2) / 1000; else sl.totalNull = true;
      if (b.ac === false) sl.bat++; else sl.ac++;
    }
  }
  const hours = slots.map((sl) => ({
    t: sl.t,
    chipWh: sl.n && !sl.chipNull ? r3(sl.chip) : null,
    totalWh: sl.n && !sl.totalNull ? r3(sl.total) : null,
    power: !sl.n ? null : sl.bat && sl.ac ? 'mixed' : sl.bat ? 'battery' : 'ac',
    covered: sl.end > sl.t ? Math.min(1, r3(sl.ms / (sl.end - sl.t))) : 0,
  }));
  const denom = shareOf === 'pc' ? Math.max(totalWh, chipWh) : shareOf === 'chip' ? chipWh : cpuSec;
  const shareOfRow = (r) => (shareOf === 'cpu' ? (denom > 0 ? r4(r.cpu / denom) : 0) : denom > 0 ? r4(r.wh / denom) : 0);
  const pathOf = (key) => (key === 'windows' || key === 'mantle' || key.startsWith('file:') ? null : key);
  const app = [...rows.values()].filter((r) => r.key !== 'windows').map((r) => ({ key: r.key, kind: 'app', name: r.name, path: pathOf(r.key), share: shareOfRow(r), wh: shareOf === 'cpu' ? null : r3(r.wh), why: null }))
    .sort((a, b) => b.share - a.share || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const w = rows.get('windows') || { key: 'windows', wh: 0, cpu: 0 };
  const anyBat = inRange.some((b) => b.ac === false), anyAc = inRange.some((b) => b.ac !== false);
  const sources = !anyBat ? (inRange.length && inRange.every((b) => b.pct === null || b.pct === undefined) ? WHY.nobattery : WHY.ac)
    : anyAc ? 'Part of this time was on mains power, and Windows only reports total draw on battery'
      : 'Windows had not measured the battery draw for part of this time';
  const fixed = [
    { key: 'windows', kind: 'windows', name: NAMES.windows, path: null, share: shareOfRow(w), wh: shareOf === 'cpu' ? null : r3(w.wh), why: null },
    { key: 'chip-rest', kind: 'chip-rest', name: NAMES['chip-rest'], path: null, share: shareOf && shareOf !== 'cpu' && denom > 0 ? r4(chipRestWh / denom) : null, wh: shareOf && shareOf !== 'cpu' ? r3(chipRestWh) : null, why: shareOf === 'cpu' ? WHY.noEnergy : null },
    { key: 'pc-rest', kind: 'pc-rest', name: NAMES['pc-rest'], path: null, share: shareOf === 'pc' && denom > 0 ? r4(pcRestWh / denom) : null, wh: shareOf === 'pc' ? r3(pcRestWh) : null, why: shareOf === 'pc' ? null : shareOf === 'cpu' ? WHY.noSplit : sources },
  ];
  const empty = coveredMs === 0;
  return {
    range: rg, from, to, shareOf, hours,
    rows: empty ? [] : [...app.slice(0, TOP_NOW), ...fixed],
    totals: { chipWh: allPkg ? r3(chipWh) : null, totalWh: allTotal ? r3(totalWh) : null, totalWhy: allTotal ? null : empty ? null : sources, coveredMs, spanMs: to - from },
    empty, emptyWhy: empty ? (rg === 'today' ? 'Nothing measured yet today: Mantle measures power use every 5 minutes while the PC is awake' : 'Nothing measured in the last 24 hours: Mantle measures power use every 5 minutes while the PC is awake') : null,
    caption: captionFor(shareOf),
  };
}

/* ---- cost ------------------------------------------------------------------------------------ */

function newCost(now) { return { since: now, samples: 0, sampleMs: 0, sidecarCpuMs: 0, nodeCpuUs: 0, fgEvents: 0 }; }

/** usage.cost's reply. watchCpuMs = the sidecar watch thread's own CPU (its status), null if unknown. */
function costReply(c, now, cpus, watchCpuMs) {
  const elapsed = Math.max(1, now - c.since);
  const nodeCpuMs = Math.round(c.nodeCpuUs / 1000);
  const w = num(watchCpuMs);
  const used = c.sidecarCpuMs + nodeCpuMs + (w || 0);
  return {
    ok: true, since: c.since, samples: c.samples, sampleMsAvg: c.samples ? Math.round(c.sampleMs / c.samples) : null,
    sidecarCpuMs: Math.round(c.sidecarCpuMs), nodeCpuMs, watchCpuMs: w === null ? null : Math.round(w), fgEvents: c.fgEvents,
    // of the whole machine (all cores), as Windows' Task Manager counts
    avgCpuPct: Math.round((used / (elapsed * Math.max(1, cpus || 1))) * 100 * 10000) / 10000,
  };
}

module.exports = {
  HOUR, KEEP_DAYS, POWER_KEEP_DAYS, BUCKET_MIN_S, BUCKET_MAX_S, CAPTION, WHY, NAMES,
  dateKey, dayStart, hourStart, nextHour, dateBefore, normHwnd, mantleWindow, appOf, low,
  freshScreentime, parseScreentime, ScreenTime,
  energyMw, sourceOf, batteryBlock, nowTotal, attribute, nowReply, captionFor, makeBucket, checkBucket, history, rangeOf,
  newCost, costReply,
};
