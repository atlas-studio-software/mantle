'use strict';
/*
 * tools/rest-cost.js - what an idle Mantle costs, measured the same way twice: this checkout against a
 * base commit, on the same machine, back to back (lane mantle-slim, 2026-11-06).
 *
 *   node tools/rest-cost.js [--base=<commit>] [--files=a.js,b.js] [--rounds=2] [--cpu=60] [--bench=8]
 *                           [--out=<file.json>]
 *
 * For each round it measures THIS checkout (label A) and, with --base, the same checkout with --files put
 * back to the base commit's version (label B), then restores them (git checkout HEAD -- <files>). Each
 * measurement is one invisible `electron . --test` copy (lib/test-quiet.js: alpha 0, click-through, out of
 * every list) that is left alone:
 *   - main's own `mantle: memory (30 s after start)` line: private and working set, per window by page;
 *   - then the CPU of every Mantle process over --cpu seconds (nothing happening), and private/working set
 *     again at the end of it, from Windows (Get-Process), main and sidecars named by their command line;
 *   - with --bench=n, a second copy runs MANTLE_TASKVIEW_BENCH=n (Task View opened n times, each logged
 *     open -> painted) with MANTLE_TASKVIEW_LINGER_MS=0, so with Keep Task View ready off every open is
 *     cold; the base's are warm after the first (its window is made at start).
 * Each copy is stopped by pid through tools/kill-test.ps1 (never taskkill by name: the user's other
 * Electron apps). Windows only. CPU-heavy (two Electron copies a round): run it on a cloud runner, not on a
 * machine someone is using.
 */
const cp = require('child_process');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const arg = (k, d) => { const a = process.argv.find((x) => x.startsWith(`--${k}=`)); return a ? a.slice(k.length + 3) : d; };
const BASE = arg('base', '');
const FILES = arg('files', '').split(',').map((s) => s.trim()).filter(Boolean);
const ROUNDS = Math.max(1, Number(arg('rounds', '1')) || 1);
const CPU_S = Math.max(5, Number(arg('cpu', '60')) || 60);
const BENCH = Math.max(0, Number(arg('bench', '0')) || 0);
const OUT = arg('out', '');
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const say = (m) => process.stdout.write(`rest-cost: ${m}\n`);

function git(args) { return cp.execFileSync('git', args, { cwd: ROOT, encoding: 'utf8' }).trim(); }

function ps(script) {
  return cp.execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', maxBuffer: 8 << 20 });
}
/** Every process of this copy: electron.exe and Mantle.Sidecar.exe whose command line names the profile, plus
 *  its renderers / utilities (children of its main) and the sidecar's own children. */
function snapshot(profile) {
  const out = ps(`$all = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'electron.exe' -or $_.Name -eq 'Mantle.Sidecar.exe' }
$main = $all | Where-Object { $_.Name -eq 'electron.exe' -and $_.CommandLine -like '*--profile-name=${profile}*' -and $_.CommandLine -notlike '*--type=*' } | Select-Object -First 1
if (-not $main) { '[]'; return }
$ids = @{}; $ids[[int]$main.ProcessId] = 'main'
foreach ($p in $all) { if ([int]$p.ParentProcessId -eq [int]$main.ProcessId) { $t = if ($p.Name -eq 'Mantle.Sidecar.exe') { 'sidecar' } elseif ($p.CommandLine -match '--type=([a-z-]+)') { $matches[1] } else { '?' }; if ($p.CommandLine -match 'utility-sub-type=([A-Za-z.]+)') { $t = $t + ':' + $matches[1] }; $ids[[int]$p.ProcessId] = $t } }
foreach ($p in $all) { if ($ids.ContainsKey([int]$p.ParentProcessId) -and $ids[[int]$p.ParentProcessId] -eq 'sidecar' -and -not $ids.ContainsKey([int]$p.ProcessId)) { $ids[[int]$p.ProcessId] = 'sidecar-child' } }
$rows = foreach ($k in $ids.Keys) { $g = Get-Process -Id $k -ErrorAction SilentlyContinue; if ($g) { [pscustomobject]@{ pid = $k; kind = $ids[$k]; cpuMs = [math]::Round($g.TotalProcessorTime.TotalMilliseconds); privMB = [math]::Round($g.PrivateMemorySize64 / 1MB, 1); wsMB = [math]::Round($g.WorkingSet64 / 1MB, 1) } } }
ConvertTo-Json -Compress -InputObject @($rows)`);
  try { const v = JSON.parse(out.trim() || '[]'); return Array.isArray(v) ? v : [v]; } catch (_) { return []; }
}

function start(profile, env) {
  const electron = require(path.join(ROOT, 'node_modules', 'electron'));
  const child = cp.spawn(electron, ['.', '--test', `--profile-name=${profile}`], {
    cwd: ROOT, env: Object.assign({}, process.env, env || {}), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
  const lines = [];
  const onData = (b) => { for (const l of String(b).split(/\r?\n/)) if (l.trim()) lines.push(l); };
  child.stdout.on('data', onData);
  child.stderr.on('data', onData);
  return { child, lines };
}
function stop(profile) {
  try { cp.execFileSync('powershell', ['-NoProfile', '-File', path.join(ROOT, 'tools', 'kill-test.ps1'), '-Match', profile], { stdio: 'ignore' }); } catch (_) { /* gone */ }
}
async function until(lines, re, ms) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) { const l = lines.find((x) => re.test(x)); if (l) return l; await wait(250); }
  return null;
}

async function measureRest(label) {
  const profile = `restcost-${label.toLowerCase()}`;
  const { lines } = start(profile);
  const mem = await until(lines, /mantle: memory \(30 s after start\)/, 120000);
  if (!mem) { stop(profile); return { label, ok: false, why: 'no memory line within 120 s', tail: lines.slice(-8) }; }
  await wait(5000);
  const a = snapshot(profile);
  const t0 = Date.now();
  await wait(CPU_S * 1000);
  const b = snapshot(profile);
  const el = Date.now() - t0;
  stop(profile);
  const byPid = new Map(a.map((p) => [p.pid, p]));
  const procs = b.map((p) => {
    const p0 = byPid.get(p.pid);
    return { pid: p.pid, kind: p.kind, cpuPct: p0 ? Math.round((10000 * (p.cpuMs - p0.cpuMs)) / el) / 100 : null, privMB: p.privMB, wsMB: p.wsMB };
  });
  const sum = (k, f) => Math.round(procs.filter(f).reduce((s, p) => s + (p[k] || 0), 0) * 10) / 10;
  const isEl = (p) => !/sidecar/.test(p.kind);
  const windows = /electron private total (\d+) MB/.exec(mem);
  return {
    label, ok: true,
    memoryLine: mem.replace(/^.*?mantle: /, ''),
    electronPrivateTotalMB30s: windows ? Number(windows[1]) : null,
    processes: procs.length,
    renderers: procs.filter((p) => p.kind === 'renderer').length,
    electronPrivMB: sum('privMB', isEl), electronWsMB: sum('wsMB', isEl),
    sidecarPrivMB: sum('privMB', (p) => !isEl(p)), sidecarWsMB: sum('wsMB', (p) => !isEl(p)),
    electronCpuPct: sum('cpuPct', isEl), sidecarCpuPct: sum('cpuPct', (p) => !isEl(p)),
    cpuSeconds: Math.round(el / 1000), procs,
  };
}

async function measureBench(label) {
  const profile = `restcost-bench-${label.toLowerCase()}`;
  const { lines } = start(profile, { MANTLE_TASKVIEW_BENCH: String(BENCH), MANTLE_TASKVIEW_LINGER_MS: '0' });
  const l = await until(lines, /taskview: bench: /, 150000);
  const opens = lines.filter((x) => /taskview: opened \((cold|warm)\)/.test(x)).map((x) => x.replace(/^.*?taskview: /, '').slice(0, 260));
  stop(profile);
  return { label, bench: l ? l.replace(/^.*?taskview: /, '') : 'no bench line within 150 s', opens };
}

(async () => {
  if (process.platform !== 'win32') { say('Windows only'); process.exit(2); }
  if (BASE && !FILES.length) { say('--base needs --files (the paths put back to the base for label B)'); process.exit(2); }
  const head = git(['rev-parse', '--short', 'HEAD']);
  const results = [];
  const runs = [];
  try {
    for (let r = 1; r <= ROUNDS; r++) {
      runs.push(['A' + r, false]);
      if (BASE) runs.push(['B' + r, true]);
    }
    for (const [label, base] of runs) {
      if (base) git(['checkout', BASE, '--', ...FILES]); else if (BASE) git(['checkout', 'HEAD', '--', ...FILES]);
      say(`${label}: ${base ? `${FILES.join(', ')} at ${BASE}` : `HEAD ${head}`}`);
      const rest = await measureRest(label);
      say(`${label} rest: ${JSON.stringify(Object.assign({}, rest, { procs: undefined }))}`);
      say(`${label} procs: ${JSON.stringify(rest.procs || [])}`);
      let bench = null;
      if (BENCH) { bench = await measureBench(label); say(`${label} bench: ${JSON.stringify(bench)}`); }
      results.push({ label, base, rest, bench });
    }
  } finally {
    if (BASE) { try { git(['checkout', 'HEAD', '--', ...FILES]); } catch (_) { /* said below */ } }
  }
  const pick = (k) => results.map((x) => `${x.label}=${x.rest && x.rest[k]}`).join(' ');
  say(`SUMMARY head ${head}${BASE ? ` vs base ${BASE} (${FILES.join(', ')})` : ''}: electron private ${pick('electronPrivMB')} | ws ${pick('electronWsMB')} | renderers ${pick('renderers')} | electron cpu% ${pick('electronCpuPct')} | sidecar cpu% ${pick('sidecarCpuPct')} | sidecar private ${pick('sidecarPrivMB')}`);
  if (OUT) { fs.mkdirSync(path.dirname(path.resolve(OUT)), { recursive: true }); }
  if (OUT) fs.writeFileSync(path.resolve(OUT), JSON.stringify({ head, base: BASE || null, files: FILES, results }, null, 1));
})().catch((err) => { say(`crashed: ${err.stack}`); process.exit(1); });
