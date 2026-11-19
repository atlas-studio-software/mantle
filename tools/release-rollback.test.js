'use strict';
/*
 * tools/release-rollback.test.js -- tools/release-rollback.js on a FAKE git, a FAKE GitHub and an in-memory file
 * system (lane release-gate, 2026-11-18). Pure node: nothing here runs git or gh or touches a real folder.
 *
 *   node tools/release-rollback.test.js   -> "RELEASE ROLLBACK TEST: n passed, m failed"
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const R = require('./release-rollback');

let passed = 0;
const failures = [];
function check(name, ok, detail) { if (ok) passed++; else failures.push(`${name}${detail !== undefined ? ` (${String(typeof detail === 'string' ? detail : JSON.stringify(detail)).slice(0, 400)})` : ''}`); }

const ROOT = path.join('Z:', 'wt', 'repo');
const WTROOT = path.join('Z:', 'wt');
function world(over) {
  const w = Object.assign({
    releases: [
      { tag_name: 'v0.1.2', draft: false, prerelease: false },
      { tag_name: 'v0.1.1', draft: false, prerelease: false },
      { tag_name: 'v0.1.0', draft: false, prerelease: false },
    ],
    tags: 'v0.1.0\nv0.1.1\nv0.1.2\n', masterHasBad: true, branchExists: false, editOk: true, latestAfter: 'v0.1.1', tagOnOrigin: true,
    files: {},
  }, over || {});
  w.calls = [];
  w.run = (file, args, opt) => {
    w.calls.push([file].concat(args).join(' '));
    const a = args;
    if (file === 'git') {
      if (a[0] === 'fetch') return { code: 0, out: '' };
      if (a[0] === 'tag' && a[1] === '--list') return { code: 0, out: w.tags };
      if (a[0] === 'merge-base') return { code: w.masterHasBad ? 0 : 1, out: '' };
      if (a[0] === 'rev-parse' && a[1] === '--verify') return { code: w.branchExists ? 0 : 1, out: '' };
      if (a[0] === 'ls-remote') return { code: 0, out: w.tagOnOrigin ? 'x\trefs/tags/v0.1.2\n' : '' };
      if (a[0] === 'worktree' && a[1] === 'add') {
        const wt = a[4];
        w.files[path.join(wt, 'package.json')] = JSON.stringify({ name: 'mantle', version: '0.1.2' }, null, 2) + '\n';
        w.files[path.join(wt, 'package-lock.json')] = JSON.stringify({ name: 'mantle', version: '0.1.2', lockfileVersion: 3, packages: { '': { name: 'mantle', version: '0.1.2' } } }, null, 2) + '\n';
        return { code: 0, out: '' };
      }
      if (a[0] === 'read-tree') {
        // the good tree: v0.1.1's package files
        w.files[path.join(opt.cwd, 'package.json')] = JSON.stringify({ name: 'mantle', version: '0.1.1' }, null, 2) + '\n';
        w.files[path.join(opt.cwd, 'package-lock.json')] = JSON.stringify({ name: 'mantle', version: '0.1.1', lockfileVersion: 3, packages: { '': { name: 'mantle', version: '0.1.1' } } }, null, 2) + '\n';
        return { code: 0, out: '' };
      }
      if (a[0] === 'add' || a[0] === 'commit') return { code: 0, out: '' };
      if (a[0] === 'rev-parse' && a[1] === 'HEAD') return { code: 0, out: 'r'.repeat(40) + '\n' };
    }
    if (file === 'gh') {
      if (a[0] === 'api' && /\/releases\?per_page=100$/.test(a[1])) return { code: 0, out: JSON.stringify(w.releases) };
      if (a[0] === 'api' && /\/releases\/latest$/.test(a[1])) return { code: 0, out: JSON.stringify({ tag_name: w.latestAfter }) };
      if (a[0] === 'release' && a[1] === 'edit') return { code: w.editOk ? 0 : 1, out: '', err: w.editOk ? '' : 'HTTP 403' };
    }
    return { code: 99, out: '', err: `the fake has no answer for ${file} ${a.join(' ')}` };
  };
  w.fsx = { exists: (p) => Object.prototype.hasOwnProperty.call(w.files, p), read: (p) => w.files[p], write: (p, s) => { w.files[p] = s; } };
  return w;
}
// autonomy folders (contracts/autonomy.md section 1): an empty one (OFF) for every case, and made-up states
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-rollback-autonomy-'));
function modeDir(name, mode) {
  const d = path.join(TMP, name); fs.mkdirSync(d);
  if (mode) {
    const pol = '{"v":1}\n';
    fs.writeFileSync(path.join(d, 'policy.json'), pol);
    fs.writeFileSync(path.join(d, 'mode.json'), JSON.stringify(Object.assign({ v: 1, on: true, stage: 'supervised', length: { kind: 'open' }, policySha: crypto.createHash('sha256').update(pol).digest('hex'), paused: null }, mode)));
  }
  return d;
}
const OFFDIR = modeDir('off', null);
async function go(w, extra) {
  const lines = [];
  const r = await R.rollback(Object.assign({ bad: 'v0.1.2', root: ROOT, wtRoot: WTROOT, run: w.run, fsx: w.fsx, out: (l) => lines.push(l), autonomyDir: OFFDIR }, extra || {}));
  return { r, lines, auto: lines.filter((l) => l.startsWith('AUTONOMY: ')).map((l) => JSON.parse(l.slice(10))) };
}
const writes = (w) => w.calls.filter((c) => /^gh release |^git (worktree add|read-tree|commit|push|tag -d|tag -a)/.test(c));
const destroys = (w) => w.calls.filter((c) => /release delete|tag -d|push .*--delete|push .*:refs\/tags/.test(c));

(async () => {
  {
    const w = world();
    const { r, lines, auto } = await go(w);
    check('dry run: (a) would, (b) would, next patch v0.1.3 after the drafted-to-be v0.1.2, good v0.1.1', r.a === 'would' && r.b === 'would' && r.next === '0.1.3' && r.good === 'v0.1.1', r);
    check('dry run: nothing is written anywhere (no gh release edit, no worktree, no commit)', writes(w).length === 0, writes(w));
    check('dry run: one AUTONOMY line, class mantle-rollback, result would', auto.length === 1 && auto[0].class === 'mantle-rollback' && auto[0].result === 'would' && /v0\.1\.2 back to draft \(tag kept\)/.test(auto[0].target), auto);
    check('dry run: the next steps and the user notice are printed', lines.some((l) => /^NEXT/.test(l)) && lines.some((l) => /^USER NOTICE/.test(l) && /git merge --ff-only v0\.1\.3/.test(l)), lines.slice(-6));
    check('dry run: the last line is the summary', /^RELEASE ROLLBACK: \(a\) would, \(b\) would, next patch v0\.1\.3, good v0\.1\.1$/.test(lines[lines.length - 1]), lines[lines.length - 1]);
    check('dry run: the gate steps pass --rollback-of=v0.1.2 and --upgrade (the only way past the 48 h cap)', lines.filter((l) => /release-gate\.js <that commit> --rollback-of=v0\.1\.2/.test(l)).length === 2 && lines.some((l) => /--upgrade=\.\.\./.test(l)), lines.filter((l) => /release-gate/.test(l)));
  }
  {
    const w = world();
    const { r, lines, auto } = await go(w, { send: true });
    const wt = path.join(WTROOT, 'rollback-v0.1.2');
    const pj = JSON.parse(w.files[path.join(wt, 'package.json')]);
    const lk = JSON.parse(w.files[path.join(wt, 'package-lock.json')]);
    check('--send: (a) gh release edit v0.1.2 --draft=true, done, and nothing deletes or moves a tag', r.a === 'done' && w.calls.some((c) => /^gh release edit v0\.1\.2 --repo atlas-studio-software\/mantle --draft=true$/.test(c)) && destroys(w).length === 0 && auto.length === 1 && auto[0].result === 'done', { calls: w.calls, auto });
    check('--send: (a) checks releases/latest and that the tag is still on origin', lines.includes('(a) check: releases/latest is v0.1.1 now') && lines.includes('(a) check: the tag v0.1.2 is still on origin'), lines);
    check('--send: (b) a worktree from origin/master on wk/rollback-v0.1.2, the good tree read in, then one commit', r.b === 'prepared' && w.calls.some((c) => c === `git worktree add -b wk/rollback-v0.1.2 ${wt} origin/master`) && w.calls.some((c) => c === 'git read-tree -u --reset refs/tags/v0.1.1') && w.calls.some((c) => /^git commit -q -m Rollback: ship v0\.1\.1's tree as v0\.1\.3/.test(c)), w.calls);
    check('--send: (b) package.json and package-lock.json (both version fields) say 0.1.3', pj.version === '0.1.3' && lk.version === '0.1.3' && lk.packages[''].version === '0.1.3', { pj, lk });
    check('--send: (b) is never pushed by this tool', !w.calls.some((c) => /^git push/.test(c)), w.calls);
  }
  {
    const w = world({ editOk: false });
    const { r, auto } = await go(w, { send: true });
    check('--send: gh refusing the edit = (a) failed, said in the AUTONOMY line', r.a === 'failed' && auto[0].result === 'failed', { r, auto });
  }
  {
    const w = world();
    w.releases[0].draft = true;
    const { r } = await go(w, { send: true });
    check('already a draft: (a) done with no gh edit', r.a === 'done' && !w.calls.some((c) => /^gh release edit/.test(c)), w.calls);
  }
  {
    const w = world();
    const { r, auto } = await go(w, { bad: 'v0.1.0', send: true });
    check('the first release has nothing to fall back to: (a) and (b) refused, nothing written', r.a === 'refused' && r.b === 'refused' && auto[0].result === 'refused' && writes(w).length === 0, { r, w: writes(w) });
  }
  {
    const w = world({ masterHasBad: false });
    const { r } = await go(w, { send: true });
    check('origin/master without the bad tag: (b) refused (its commit would not reach those copies)', r.b === 'refused' && !w.calls.some((c) => /worktree add/.test(c)), r);
  }
  {
    const w = world({ branchExists: true });
    const { r } = await go(w, { send: true });
    check('the rollback branch already exists: (b) refused', r.b === 'refused', r);
  }
  {
    const w = world();
    const { r } = await go(w, { good: 'v0.1.0' });
    check('--good picks the release to go back to', r.good === 'v0.1.0' && r.a === 'would', r);
  }
  {
    const w = world();
    const { r, auto } = await go(w, { bad: 'latest' });
    check('a non-tag is refused with an AUTONOMY line', r.a === 'refused' && auto.length === 1 && auto[0].result === 'refused' && w.calls.length === 0, { r, calls: w.calls });
  }
  {
    let threw = null; try { R.argsOf([]); } catch (err) { threw = err.message; }
    check('cli: no tag = usage', /usage/.test(threw || ''));
    const a = R.argsOf(['v0.1.2', '--good=v0.1.1', '--send']);
    check('cli: arguments parse', a.bad === 'v0.1.2' && a.good === 'v0.1.1' && a.send === true, a);
  }

  // ---- autonomous mode
  for (const [name, mode, why] of [['PAUSED', { paused: { reason: 'usage' } }, 'paused'], ['ON, holds mantle-rollback', { holds: ['mantle-rollback'] }, 'held']]) {
    const w = world();
    const { r, auto, lines } = await go(w, { send: true, autonomyDir: modeDir(name.replace(/\W+/g, '-'), mode) });
    check(`autonomy: ${name}: --send refused (${why}): one AUTONOMY refused line with why, (a) and (b) not done, nothing run, exit 3`,
      r.refused === why && r.a === 'refused' && r.b === 'refused' && auto.length === 1 && auto[0].result === 'refused' && auto[0].why === why && auto[0].class === 'mantle-rollback' && w.calls.length === 0 && R.exitCode(r) === 3 && /, refused: /.test(lines[lines.length - 1]), { r, auto, calls: w.calls });
  }
  {
    const w = world();
    const { r } = await go(w, { send: true, autonomyDir: modeDir('on-held-release', { holds: ['mantle-release'] }) });
    check('autonomy: ON holding only mantle-release: the rollback --send goes on ((a) done)', !r.refused && r.a === 'done' && R.exitCode(r) === 0, r);
  }
  {
    const w = world();
    const { r } = await go(w, { autonomyDir: modeDir('paused-dry', { paused: 'mail' }) });
    check('autonomy: PAUSED without --send: the dry run runs ((a) would, (b) would)', !r.refused && r.a === 'would' && r.b === 'would', r);
  }
  try { fs.rmSync(TMP, { recursive: true, force: true }); } catch (_) { /* a temp folder */ }

  for (const f of failures) console.log('FAIL ' + f);
  console.log(`RELEASE ROLLBACK TEST: ${passed} passed, ${failures.length} failed`);
  process.exit(failures.length ? 1 : 0);
})().catch((err) => { console.log(`FAIL threw: ${err.stack}`); console.log(`RELEASE ROLLBACK TEST: ${passed} passed, ${failures.length + 1} failed`); process.exit(1); });
