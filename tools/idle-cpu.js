#!/usr/bin/env node
'use strict';
/*
 * tools/idle-cpu.js - how much CPU an off-screen --test Mantle uses AT REST, and what wakes it
 * (lane mantle-slim-cpu, 2026-11-06). The user: "making the quality better and having it use less of my
 * computer". PERF.md's CPU-at-rest budgets: a visible surface <= 0.5 % of a core, a sidecar watcher
 * <= 0.1 %, a hidden window 0.
 *
 *   node tools/idle-cpu.js [--label=head] [--settle=60] [--sample=60] [--rounds=2] [--out=suite-out]
 *                          [--timers] [--allow-local]
 *
 * What it does: starts `electron . --test --profile-name=idlecpu-<label> --test-live-bar` (test-quiet
 * keeps every window invisible and click-through) with a probe in its main process (NODE_OPTIONS
 * --require of this file, MANTLE_IDLE_PROBE=1, never in a live Mantle), waits --settle seconds, then for
 * each of --rounds samples of --sample seconds, with nothing touching it, records:
 *   - every Electron process's CPU (app.getAppMetrics), renderers named by their page;
 *   - each of this instance's sidecars (found by --parent-pid=<this main pid>): process CPU and the
 *     busiest threads with their priority (Get-Process, read only);
 *   - what woke main: sidecar commands sent (by name), sidecar events received (by ev), pushes to each
 *     page (by event name), and with --timers every main-process timer that fired (by the file:line that
 *     set it; costs a stack per timer, so the CPU numbers of a --timers run are not comparable);
 *   - the top bar sampler's own CPU per tick (topbarstats costMs).
 * Writes <out>/idle-cpu-<label>.json and prints one summary block.
 *
 * TEST ONLY and CLOUD BY DEFAULT: it starts a whole Mantle test copy, so it refuses to run unless CI or
 * GITHUB_ACTIONS is set (the cloud runner), or --allow-local is passed. Stops its copy by pid and with
 * tools/kill-test.ps1 for its own --profile-name; never an image-name kill.
 */
const fs = require('fs');
const os = require('os');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

// ------------------------------------------------------------------ the probe (inside the test copy's main)
function installProbe() {
  const Module = require('module');
  const http = require('http');
  delete process.env.NODE_OPTIONS;               // never into the sidecar, PowerShell or any child node
  const T = { since: Date.now(), sc: {}, ev: {}, push: {}, timers: {}, stats: { n: 0, costMs: 0, max: 0 } };
  const inc = (o, k, by) => { o[k] = (o[k] || 0) + (by || 1); };
  const reset = () => { T.since = Date.now(); T.sc = {}; T.ev = {}; T.push = {}; T.timers = {}; T.stats = { n: 0, costMs: 0, max: 0 }; };
  const pageOf = (wc) => { try { const u = String(wc.getURL() || '').replace(/\\/g, '/'); const m = u.match(/\/ui\/(.+?)(\?|#|$)/); return m ? m[1].replace(/\/index\.html$/, '') : u.slice(0, 40); } catch (_) { return '?'; } };

  if (process.env.MANTLE_IDLE_TIMERS === '1') {
    const here = __filename.replace(/\\/g, '/');
    const site = () => {
      const st = (new Error().stack || '').split('\n').slice(2);
      for (const l of st) {
        const s = l.replace(/\\/g, '/');
        if (s.includes(here) || s.includes('node:internal') || s.includes('(node:')) continue;
        const m = s.match(/([^/(\s]+\/[^/(\s]+\.js):(\d+)/);
        if (m) return m[1] + ':' + m[2];
      }
      return '?';
    };
    for (const k of ['setTimeout', 'setInterval']) {
      const orig = global[k];
      global[k] = function (fn, ms, ...rest) {
        if (typeof fn !== 'function') return orig.call(this, fn, ms, ...rest);
        const key = `${k === 'setInterval' ? 'every' : 'after'} ${Math.round(Number(ms) || 0)}ms ${site()}`;
        return orig.call(this, function () { inc(T.timers, key); return fn.apply(this, arguments); }, ms, ...rest);
      };
    }
  }

  let el = null;
  const origLoad = Module._load;
  Module._load = function (req) {
    const r = origLoad.apply(this, arguments);
    if (!el && req === 'electron' && r && r.app && r.BrowserWindow) { el = r; boot(); }
    if (/lib[\\/]sidecar(\.js)?$/.test(req) && r && r.Sidecar && r.Sidecar.prototype && !r.Sidecar.prototype.__idle) {
      const P = r.Sidecar.prototype; P.__idle = true;
      for (const m of ['call', 'send']) {
        if (typeof P[m] !== 'function') continue;
        const o = P[m];
        P[m] = function (cmd) { inc(T.sc, String(cmd).slice(0, 60)); return o.apply(this, arguments); };
      }
      const emit = P.emit;
      P.emit = function (name, msg) {
        if (name === 'event' && msg && typeof msg.ev === 'string') {
          inc(T.ev, msg.ev.slice(0, 60));
          if (msg.ev === 'topbarstats' && typeof msg.costMs === 'number') { T.stats.n++; T.stats.costMs += msg.costMs; T.stats.max = Math.max(T.stats.max, msg.costMs); }
        }
        return emit.apply(this, arguments);
      };
    }
    return r;
  };

  function boot() {
    const { app, BrowserWindow } = el;
    app.on('browser-window-created', (_e, win) => {
      const wc = win.webContents;
      const send = wc.send;
      wc.send = function (channel, payload) {
        inc(T.push, pageOf(wc) + ' ' + (channel === 'mantle:event' && payload && payload.name ? payload.name : channel));
        return send.apply(this, arguments);
      };
    });
    const wins = () => BrowserWindow.getAllWindows().filter((w) => !w.isDestroyed());
    const routes = {
      ping: () => ({ ok: true, now: Date.now(), pid: process.pid }),
      metrics: () => ({
        at: Date.now(),
        procs: app.getAppMetrics().map((p) => ({ pid: p.pid, type: p.type, name: p.name || null,
          privateKB: (p.memory && p.memory.privateBytes) || 0,
          cpuSec: p.cpu && typeof p.cpu.cumulativeCPUUsage === 'number' ? p.cpu.cumulativeCPUUsage : null })),
        windows: wins().map((w) => { let pid = null; try { pid = w.webContents.getOSProcessId(); } catch (_) { /* gone */ }
          return { page: pageOf(w.webContents), pid, visible: w.isVisible(), bounds: w.getBounds() }; }),
      }),
      tally: (q) => { const out = JSON.parse(JSON.stringify(T)); out.until = Date.now(); if (q.reset === '1') reset(); return out; },
    };
    const server = http.createServer((req, res) => {
      const u = new URL(req.url, 'http://127.0.0.1');
      const fn = routes[u.pathname.slice(1)];
      let body;
      try { body = fn ? fn(Object.fromEntries(u.searchParams)) : { error: 'no such route' }; } catch (err) { body = { error: String(err && err.message) }; }
      res.setHeader('content-type', 'application/json');
      res.end(JSON.stringify(body));
    });
    server.listen(0, '127.0.0.1', () => {
      const file = process.env.MANTLE_IDLE_PORT_FILE;
      if (file) { try { fs.writeFileSync(file, String(server.address().port)); } catch (_) { /* the CLI times out and says so */ } }
      process.stdout.write(`mantle: idle-cpu probe: listening on 127.0.0.1:${server.address().port} (test only)\n`);
    });
    server.unref();
  }
}

// ------------------------------------------------------------------ the CLI
function parseArgs(argv) {
  const A = { label: 'run', settle: 60, sample: 60, rounds: 2, out: path.join(ROOT, 'suite-out'), timers: false, allowLocal: false };
  for (const a of argv) {
    const m = a.match(/^--([\w-]+)(?:=(.*))?$/);
    if (!m) continue;
    const [, k, v] = m;
    if (k === 'label') A.label = String(v).replace(/[^\w-]/g, '').slice(0, 30) || 'run';
    else if (k === 'settle' || k === 'sample' || k === 'rounds') A[k] = Math.max(1, Number(v) || A[k]);
    else if (k === 'out') A.out = path.resolve(v);
    else if (k === 'timers') A.timers = true;
    else if (k === 'allow-local') A.allowLocal = true;
  }
  return A;
}

/** This instance's sidecars (found by --parent-pid) and their threads, read only. */
function sidecarSnap(spawnSync, mainPid) {
  const ps = `$ErrorActionPreference='SilentlyContinue'; $out=@(); ` +
    `foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='Mantle.Sidecar.exe'")) { ` +
    `if (-not $p.CommandLine -or -not $p.CommandLine.Contains('--parent-pid=${mainPid}')) { continue } ` +
    `$g = Get-Process -Id $p.ProcessId; if (-not $g) { continue }; ` +
    `$kind = if ($p.CommandLine.Contains('--supervise')) { 'supervise' } elseif ($p.CommandLine.Contains('--taskbar-guard')) { 'taskbar-guard' } else { 'main' }; ` +
    `$th = @($g.Threads | ForEach-Object { [pscustomobject]@{ id = $_.Id; prio = [string]$_.PriorityLevel; ms = [math]::Round($_.TotalProcessorTime.TotalMilliseconds, 1) } }); ` +
    `$out += [pscustomobject]@{ pid = $p.ProcessId; kind = $kind; ms = [math]::Round($g.TotalProcessorTime.TotalMilliseconds, 1); threads = $th } }; ` +
    `ConvertTo-Json -Depth 4 -Compress @($out)`;
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', ps], { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  try { const j = JSON.parse(String(r.stdout || '').trim() || '[]'); return Array.isArray(j) ? j : [j]; } catch (_) { return []; }
}

const r2 = (x) => Math.round(x * 100) / 100;

function diffRound(m0, m1, s0, s1, tally) {
  const wall = (m1.at - m0.at) / 1000;
  const before = new Map(m0.procs.map((p) => [p.pid, p]));
  const pageByPid = new Map();
  for (const w of m1.windows) if (w.pid) pageByPid.set(w.pid, (pageByPid.has(w.pid) ? pageByPid.get(w.pid) + '+' : '') + w.page + (w.visible ? '' : ' (hidden)'));
  const electron = [];
  for (const p of m1.procs) {
    const a = before.get(p.pid);
    if (!a || typeof a.cpuSec !== 'number' || typeof p.cpuSec !== 'number') continue;
    const name = p.type === 'Browser' ? 'main' : p.type === 'Tab' ? (pageByPid.get(p.pid) || 'renderer ' + p.pid) : (p.name || p.type);
    electron.push({ name, pid: p.pid, pct: r2((p.cpuSec - a.cpuSec) / wall * 100) });
  }
  electron.sort((x, y) => y.pct - x.pct);
  const sidecars = [];
  for (const b of s1) {
    const a = s0.find((x) => x.pid === b.pid);
    if (!a) continue;
    const t0 = new Map((a.threads || []).map((t) => [t.id, t.ms]));
    const threads = (b.threads || []).map((t) => ({ id: t.id, prio: t.prio, ms: r2(t.ms - (t0.has(t.id) ? t0.get(t.id) : 0)), isNew: !t0.has(t.id) }))
      .filter((t) => t.ms > 0).sort((x, y) => y.ms - x.ms).slice(0, 12);
    sidecars.push({ kind: b.kind, pid: b.pid, pct: r2((b.ms - a.ms) / 1000 / wall * 100), threads });
  }
  const perMin = (o) => Object.fromEntries(Object.entries(o).sort((x, y) => y[1] - x[1]).map(([k, v]) => [k, r2(v / wall * 60)]));
  const total = r2(electron.reduce((s, e) => s + e.pct, 0) + sidecars.reduce((s, e) => s + e.pct, 0));
  return {
    wallS: r2(wall), totalPct: total, electron, sidecars,
    perMinute: { sidecarCalls: perMin(tally.sc), sidecarEvents: perMin(tally.ev), pushes: perMin(tally.push), timers: perMin(tally.timers) },
    topbarStats: tally.stats.n ? { ticks: tally.stats.n, avgCostMs: r2(tally.stats.costMs / tally.stats.n), maxCostMs: r2(tally.stats.max) } : null,
  };
}

async function cli() {
  const { spawn, spawnSync } = require('child_process');
  const A = parseArgs(process.argv.slice(2));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const say = (s) => process.stdout.write(s + '\n');
  if (!(process.env.CI || process.env.GITHUB_ACTIONS) && !A.allowLocal) {
    say('idle-cpu: refused: this starts a whole Mantle test copy; it runs on the cloud runner (CI set) or with --allow-local');
    process.exit(2);
  }
  const PROFILE = 'idlecpu-' + A.label;
  const exe = path.join(ROOT, 'node_modules', 'electron', 'dist', 'electron.exe');
  if (!fs.existsSync(exe)) { say(`idle-cpu: no Electron at ${exe}`); process.exit(2); }
  fs.mkdirSync(A.out, { recursive: true });
  const portFile = path.join(os.tmpdir(), `mantle-idle-port-${PROFILE}.txt`);
  try { fs.rmSync(portFile, { force: true }); } catch (_) { /* fresh */ }
  const env = Object.assign({}, process.env, {
    NODE_OPTIONS: `--require=${__filename.replace(/\\/g, '/')}`, MANTLE_IDLE_PROBE: '1', MANTLE_IDLE_PORT_FILE: portFile,
    MANTLE_IDLE_TIMERS: A.timers ? '1' : '0',
  });
  delete env.MANTLE_TEST_VISIBLE;
  delete env.ELECTRON_RUN_AS_NODE;
  const ownExe = path.join(ROOT, 'sidecar', 'bin', 'Mantle.Sidecar.exe');
  if (!process.env.MANTLE_SIDECAR_EXE && fs.existsSync(ownExe)) env.MANTLE_SIDECAR_EXE = ownExe;
  const logFile = path.join(A.out, `idle-cpu-${A.label}.log`);
  const logFd = fs.openSync(logFile, 'w');
  const child = spawn(exe, ['.', '--test', `--profile-name=${PROFILE}`, '--test-live-bar'], { cwd: ROOT, env, stdio: ['ignore', logFd, logFd], windowsHide: true });
  const stop = () => {
    try { if (child.exitCode === null) spawnSync('taskkill.exe', ['/PID', String(child.pid), '/F'], { stdio: 'ignore', windowsHide: true }); } catch (_) { /* gone */ }
    spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(ROOT, 'tools', 'kill-test.ps1'), '-Match', PROFILE], { stdio: 'ignore', windowsHide: true });
  };
  let port = null;
  const t00 = Date.now();
  while (!port && Date.now() - t00 < 90000 && child.exitCode === null) {
    try { port = Number(fs.readFileSync(portFile, 'utf8')) || null; } catch (_) { /* not yet */ }
    if (!port) await sleep(250);
  }
  if (!port) { stop(); say(`idle-cpu: the probe never answered (exit ${child.exitCode}); see ${logFile}`); process.exit(2); }
  const api = async (route, q) => (await fetch(`http://127.0.0.1:${port}/${route}` + (q ? '?' + new URLSearchParams(q) : ''))).json();
  const mainPid = (await api('ping')).pid;
  say(`idle-cpu: ${A.label}: test copy up in ${Date.now() - t00} ms (main pid ${mainPid}); settling ${A.settle} s`);
  await sleep(A.settle * 1000);
  const result = { label: A.label, at: new Date().toISOString(), commit: null, settleS: A.settle, sampleS: A.sample, timers: A.timers, cores: os.cpus().length, rounds: [] };
  try { result.commit = spawnSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: ROOT, encoding: 'utf8' }).stdout.trim(); } catch (_) { /* no git */ }
  for (let i = 0; i < A.rounds; i++) {
    await api('tally', { reset: '1' });
    const s0 = sidecarSnap(spawnSync, mainPid);
    const m0 = await api('metrics');
    await sleep(A.sample * 1000);
    const m1 = await api('metrics');
    const s1 = sidecarSnap(spawnSync, mainPid);
    const tally = await api('tally');
    const d = diffRound(m0, m1, s0, s1, tally);
    d.windows = m1.windows.map((w) => `${w.page}${w.visible ? '' : ' (hidden)'} ${w.bounds.width}x${w.bounds.height}@${w.bounds.x},${w.bounds.y}`);
    result.rounds.push(d);
  }
  stop();
  try { fs.closeSync(logFd); } catch (_) { /* closed */ }
  fs.writeFileSync(path.join(A.out, `idle-cpu-${A.label}.json`), JSON.stringify(result, null, 1));
  const top = (o, n) => Object.entries(o).slice(0, n).map(([k, v]) => `${k} ${v}`).join(', ') || 'none';
  for (const [i, d] of result.rounds.entries()) {
    say(`idle-cpu: ${A.label} (${result.commit}) round ${i + 1}: TOTAL ${d.totalPct} % of one core over ${d.wallS} s`);
    say(`  electron: ${d.electron.filter((e) => e.pct > 0).map((e) => `${e.name} ${e.pct}`).join(' | ')}`);
    for (const s of d.sidecars) say(`  sidecar ${s.kind}: ${s.pct} % (threads ms: ${s.threads.map((t) => `${t.ms}${t.prio === 'Normal' ? '' : ' ' + t.prio}${t.isNew ? ' new' : ''}`).join(', ')})`);
    if (d.topbarStats) say(`  topbar sampler: ${d.topbarStats.ticks} ticks, ${d.topbarStats.avgCostMs} ms avg, ${d.topbarStats.maxCostMs} ms max`);
    say(`  sidecar calls/min: ${top(d.perMinute.sidecarCalls, 12)}`);
    say(`  sidecar events/min: ${top(d.perMinute.sidecarEvents, 12)}`);
    say(`  pushes/min: ${top(d.perMinute.pushes, 12)}`);
    if (A.timers) say(`  main timers fired/min: ${top(d.perMinute.timers, 25)}`);
  }
  say(`idle-cpu: windows: ${result.rounds[0] ? result.rounds[0].windows.join('; ') : '-'}`);
}

module.exports = { parseArgs, diffRound };

if (process.versions.electron && process.type === 'browser' && process.env.MANTLE_IDLE_PROBE === '1') installProbe();
else if (require.main === module) cli().catch((e) => { process.stdout.write(`idle-cpu: failed: ${e && e.stack}\n`); process.exit(2); });
