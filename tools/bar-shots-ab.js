'use strict';
/*
 * tools/bar-shots-ab.js - the bar drawn by a base commit and by this tree, light and dark, compared pixel
 * for pixel (lane mantle-bar-leak, 2026-11-06).
 *
 * Unpacks <base>'s ui/ and lib/ (git archive) into a temp folder, then ONE ui/bar/shoot.js run (one
 * headless browser, BAR_PASSES) shoots the same shots four times: base light, base dark, head light,
 * head dark, with the page's clock stood still (BAR_FIXED_TIME) so the clock and the song position are
 * the same in every pass. Then tools/png-same.js compares base against head per theme.
 *
 *   node tools/bar-shots-ab.js [base ref, default origin/master] [out dir, default bar-shots-ab]
 *   env: BAR_AB_ONLY=<comma list of shot-name parts> (default: the shots with the backdrop strip, the
 *        dark pop-up glass, the artwork, the Spotify covers and the toast pictures)
 * Prints shoot.js's lines, png-same's lines, and:
 *   bar-shots-ab TOTAL: light 20/20 same, dark 20/20 same
 * Exit 1 when any shot differs or is missing. Headless Chromium: run it on a cloud runner (cloudtest).
 */
const path = require('path');
const fs = require('fs');
const os = require('os');
const { execFileSync, spawnSync } = require('child_process');
const { pathToFileURL } = require('url');

const REPO = path.join(__dirname, '..');
const BASE = process.argv[2] || 'origin/master';
const OUT = path.resolve(process.argv[3] || 'bar-shots-ab');
const ONLY = (process.env.BAR_AB_ONLY || 'bar-1920,bar-1280,media-chip,media-card,media-both,spq-long-1x,spq-library,notifs-panel,notifs-many,qsx-main')
  .split(',').map((s) => s.trim()).filter(Boolean);
const AT = Date.UTC(2026, 10, 6, 17, 41, 0);   // any fixed moment: the same in every pass

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-shots-ab-'));
const tar = path.join(dir, 'base.tar');
execFileSync('git', ['-C', REPO, 'archive', '--format=tar', '-o', tar, BASE, 'ui', 'lib'], { stdio: 'inherit' });
execFileSync('tar', ['-xf', tar, '-C', dir], { stdio: 'inherit' });
fs.rmSync(tar, { force: true });

const basePage = pathToFileURL(path.join(dir, 'ui', 'bar', 'index.html')).href;
const headPage = pathToFileURL(path.join(REPO, 'ui', 'bar', 'index.html')).href;
const passes = [];
for (const theme of ['light', 'dark']) {
  passes.push({ out: path.join(OUT, 'base-' + theme), page: basePage, theme, only: ONLY });
  passes.push({ out: path.join(OUT, 'head-' + theme), page: headPage, theme, only: ONLY });
}
fs.mkdirSync(OUT, { recursive: true });
const passFile = path.join(OUT, 'passes.json');
fs.writeFileSync(passFile, JSON.stringify(passes, null, 1));
const env = Object.assign({}, process.env, { BAR_PASSES: passFile, BAR_FIXED_TIME: String(AT) });
const r = spawnSync(process.execPath, [path.join(REPO, 'ui', 'bar', 'shoot.js')], { env, stdio: 'inherit', timeout: 40 * 60 * 1000 });
if (r.status !== 0) process.stdout.write('bar-shots-ab: shoot.js exited ' + r.status + (r.error ? ' (' + r.error.message + ')' : '') + '\n');
try { fs.rmSync(dir, { recursive: true, force: true }); } catch (_) { /* temp */ }

const sum = [];
let bad = r.status !== 0;
for (const theme of ['light', 'dark']) {
  const c = spawnSync(process.execPath, [path.join(__dirname, 'png-same.js'), path.join(OUT, 'base-' + theme), path.join(OUT, 'head-' + theme)], { encoding: 'utf8' });
  const text = (c.stdout || '') + (c.stderr || '');
  process.stdout.write(text.split(/\r?\n/).map((l) => (l ? theme + '| ' + l : l)).join('\n') + '\n');
  const m = /png-same TOTAL: (\d+) files: (\d+) same, (\d+) differ, (\d+) missing/.exec(text);
  sum.push(theme + ' ' + (m ? m[2] + '/' + m[1] + ' same' + (Number(m[3]) ? ', ' + m[3] + ' differ' : '') + (Number(m[4]) ? ', ' + m[4] + ' missing' : '') : 'no reading'));
  if (c.status !== 0) bad = true;
}
process.stdout.write('bar-shots-ab TOTAL: ' + sum.join(', ') + '\n');
process.exit(bad ? 1 : 0);
