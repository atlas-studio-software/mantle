'use strict';
/*
 * tools/bar-mem-ab.js - the bar page's memory growth, a base commit against this tree, on ONE machine
 * (lane mantle-bar-leak, 2026-11-06).
 *
 * Unpacks <base>'s ui/, lib/ and preload.js (git archive) into a temp folder, then runs
 * tools/bar-mem-probe.js in Mantle's own Electron twice: once on that copy (MEM_PROBE_ROOT), once on
 * this tree. Same runner, same Electron, same scenarios, same pictures (the probe's PNGs come from a
 * fixed seed), so the difference is the page's code. Prints both probes' lines, then one line per
 * scenario and a TOTAL:
 *
 *   bar-mem-ab reveals: base +31.2 MB, head +2.1 MB over 60 cycles (520 -> 35 KB a cycle)
 *   bar-mem-ab TOTAL: base +98.0 MB, head +7.3 MB (-90.7 MB) over 3 scenarios x 60 cycles
 *
 *   node tools/bar-mem-ab.js [base ref, default origin/master]
 *   env: MEM_PROBE (scenarios, default reveals,popups,tracks), MEM_PROBE_N (cycles, default 60),
 *        MEM_AB_MAX_KB=<n>: exit 1 when head keeps more than n KB a cycle in any scenario (default: report only)
 *
 * Electron, hidden: the probe runs its windows through lib/test-quiet.js (alpha 0, click-through,
 * never focused). It is CPU-heavy (one Electron per scenario, minutes of driving): run it on a cloud
 * runner (cloudtest), not on a machine somebody is using.
 */
const path = require('path');
const fs = require('fs');
const os = require('os');
const { execFileSync, spawnSync } = require('child_process');

const REPO = path.join(__dirname, '..');
const BASE = process.argv[2] || 'origin/master';
const SCEN = process.env.MEM_PROBE || 'reveals,popups,tracks';
const N = process.env.MEM_PROBE_N || '60';
const MAX_KB = Number(process.env.MEM_AB_MAX_KB) || 0;

function say(s) { process.stdout.write(s + '\n'); }

function unpack(ref) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-bar-ab-'));
  const tar = path.join(dir, 'base.tar');
  execFileSync('git', ['-C', REPO, 'archive', '--format=tar', '-o', tar, ref, 'ui', 'lib', 'preload.js'], { stdio: 'inherit' });
  execFileSync('tar', ['-xf', tar, '-C', dir], { stdio: 'inherit' });
  fs.rmSync(tar, { force: true });
  return dir;
}

function probe(label, root) {
  const electron = path.join(REPO, 'node_modules', 'electron', 'cli.js');
  const env = Object.assign({}, process.env, { MEM_PROBE: SCEN, MEM_PROBE_N: N });
  if (root) env.MEM_PROBE_ROOT = root; else delete env.MEM_PROBE_ROOT;
  say('bar-mem-ab: ---- ' + label + (root ? ' (' + BASE + ', unpacked at ' + root + ')' : ' (this tree)'));
  const r = spawnSync(process.execPath, [electron, path.join(__dirname, 'bar-mem-probe.js')], { env, encoding: 'utf8', maxBuffer: 64 * 1048576, timeout: 30 * 60 * 1000 });
  const out = (r.stdout || '') + (r.stderr || '');
  process.stdout.write(out.split(/\r?\n/).map((l) => (l ? label + '| ' + l : l)).join('\n') + '\n');
  // "bar-mem reveals: warm -> 60: priv 40.1 -> 71.3 MB (31.2), ..."
  const got = {};
  for (const m of out.matchAll(/^bar-mem (\w+): warm -> \d+: priv ([\d.]+|null) -> ([\d.]+|null) MB \(([-\d.]+|n\/a)\)/gm)) {
    got[m[1]] = { warm: Number(m[2]), end: Number(m[3]), grew: Number(m[4]) };
  }
  if (r.status !== 0 || r.error) say('bar-mem-ab: ' + label + ' probe exited ' + r.status + (r.error ? ' (' + r.error.message + ')' : ''));
  return got;
}

const baseDir = unpack(BASE);
let base, head;
try {
  base = probe('base', baseDir);
  head = probe('head', null);
} finally {
  try { fs.rmSync(baseDir, { recursive: true, force: true }); } catch (_) { /* temp */ }
}

const r1 = (x) => Math.round(x * 10) / 10;
let tb = 0, th = 0, n = 0, over = [];
const cycles = Number(N);
for (const s of SCEN.split(',').map((x) => x.trim()).filter(Boolean)) {
  const b = base[s], h = head[s];
  if (!b || !h || !isFinite(b.grew) || !isFinite(h.grew)) { say('bar-mem-ab ' + s + ': no reading (base ' + JSON.stringify(b) + ', head ' + JSON.stringify(h) + ')'); over.push(s + ' unread'); continue; }
  tb += b.grew; th += h.grew; n++;
  const kb = (mb) => Math.round(mb * 1024 / cycles);
  say('bar-mem-ab ' + s + ': base ' + (b.grew >= 0 ? '+' : '') + b.grew + ' MB (' + b.warm + ' -> ' + b.end + '), head ' + (h.grew >= 0 ? '+' : '') + h.grew +
    ' MB (' + h.warm + ' -> ' + h.end + ') over ' + cycles + ' cycles (' + kb(b.grew) + ' -> ' + kb(h.grew) + ' KB a cycle)');
  if (MAX_KB && kb(h.grew) > MAX_KB) over.push(s + ' keeps ' + kb(h.grew) + ' KB a cycle (limit ' + MAX_KB + ')');
}
say('bar-mem-ab TOTAL: base +' + r1(tb) + ' MB, head +' + r1(th) + ' MB (' + (th - tb >= 0 ? '+' : '') + r1(th - tb) + ' MB) over ' + n + ' scenarios x ' + cycles + ' cycles' +
  (over.length ? '; FAIL: ' + over.join('; ') : ''));
process.exit(MAX_KB && over.length ? 1 : 0);
