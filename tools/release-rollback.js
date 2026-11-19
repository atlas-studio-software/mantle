'use strict';
/*
 * tools/release-rollback.js -- undo a bad Mantle release the way the rollback runbook says (lane release-gate,
 * 2026-11-18). DRY RUN by default: it prints what it would do and changes nothing.
 *
 *   node tools/release-rollback.js <bad tag> [--good=<tag>] [--send]
 *
 *   (a) OUTWARD, only with --send: the bad release goes back to DRAFT (gh release edit <bad> --draft=true). The tag
 *       stays: it is never deleted, moved or reused. GitHub's releases/latest then answers the last good release, so
 *       new installs and copies that have not updated yet stop being offered the bad one. Copies that already
 *       updated stay on it: Mantle's updater never goes down a version (lib/updater.js).
 *   (b) LOCAL, with --send: the fix for those copies, prepared as a commit and never pushed by this tool. A new
 *       worktree from origin/master gets the last good release's TREE (git read-tree -u --reset <good>) with
 *       package.json and package-lock.json set to the NEXT patch, committed on wk/rollback-<bad>. It sits on top of
 *       master (which holds the bad release), so clones fast-forward to it. A tag on the old good commit would do
 *       nothing for them (that commit is already in their history). Everything merged after <good> is left out of
 *       that tree on purpose; it comes back in a later release.
 *       Then, each with the owner's OK first: push that commit to master (installer.yml builds the draft), run the cloudtest
 *       suites, and release it with tools/release-gate.js. The 48 h rule does not stop a rollback's
 *       patch (the gate's 48 h check is against the latest PUBLISHED release, and (a) has drafted the bad one).
 *
 * Every outward step prints one AUTONOMY line (class "mantle-rollback"); the last line is
 * "RELEASE ROLLBACK: ...". Read-only network use without --send: git fetch, gh api GETs.
 */
const fs = require('fs');
const path = require('path');
const L = require('./release-lib');

/**
 * o: { bad, good, send, root, wtRoot, run(file,args,opt), fsx: {exists, read, write}, out(line) }
 * -> { a: 'would'|'done'|'failed'|'refused', b: 'would'|'prepared'|'failed'|'refused', next, good, branch, commit }
 */
async function rollback(o) {
  const out = o.out || ((l) => process.stdout.write(l + '\n'));
  const run = o.run || L.realRun;
  const root = o.root || path.join(__dirname, '..');
  const wtRoot = o.wtRoot || path.dirname(root);
  const fsx = o.fsx || { exists: (p) => fs.existsSync(p), read: (p) => fs.readFileSync(p, 'utf8'), write: (p, s) => fs.writeFileSync(p, s) };
  const git = (args, cwd) => run('git', args, { cwd: cwd || root });
  const gh = (args) => run('gh', args, { cwd: root });
  const res = { a: 'refused', b: 'refused', next: null, good: null, branch: null, commit: null };
  const done = () => { out(`RELEASE ROLLBACK: (a) ${res.a}, (b) ${res.b}${res.next ? `, next patch v${res.next}` : ''}${res.good ? `, good ${res.good}` : ''}`); return res; };

  const bad = String(o.bad || '');
  if (!L.parseVersion(bad) || !bad.startsWith('v')) {
    out(`refused: "${bad}" is not a release tag like v1.2.3`);
    L.autonomy('mantle-rollback', `${bad || '?'} back to draft`, 'refused', out);
    return done();
  }
  git(['fetch', '--quiet', 'origin', '--tags']);
  const rl = gh(['api', `repos/${L.REPO}/releases?per_page=100`]);
  let list = null; try { list = rl.code === 0 ? JSON.parse(rl.out) : null; } catch (_) { list = null; }
  if (!list) {
    out(`refused: GitHub's release list could not be read (${(rl.err || rl.out || '').trim().slice(0, 160) || `gh exit ${rl.code}`})`);
    L.autonomy('mantle-rollback', `${bad} back to draft`, 'refused', out);
    return done();
  }
  const rel = list.find((r) => r && r.tag_name === bad);
  const goodRel = o.good ? list.find((r) => r && r.tag_name === o.good && !r.draft && !r.prerelease) : (L.previousPublished(list, bad) || {}).r;
  res.good = goodRel ? goodRel.tag_name : null;

  // ---- (a) the bad release back to draft, the tag kept
  const aTarget = `${bad} back to draft (tag kept)`;
  if (!rel) { out(`(a) refused: there is no release ${bad}`); L.autonomy('mantle-rollback', aTarget, 'refused', out); }
  else if (!goodRel) { out(`(a) refused: no published release before ${bad} (or --good is not one): drafting it would leave no release at all`); L.autonomy('mantle-rollback', aTarget, 'refused', out); }
  else if (rel.draft) { out(`(a) ${bad} is already a draft: nothing to do`); res.a = 'done'; L.autonomy('mantle-rollback', aTarget, 'done', out); }
  else if (!o.send) { out(`(a) would run: gh release edit ${bad} --repo ${L.REPO} --draft=true`); res.a = 'would'; L.autonomy('mantle-rollback', aTarget, 'would', out); }
  else {
    const r = gh(['release', 'edit', bad, '--repo', L.REPO, '--draft=true']);
    res.a = r.code === 0 ? 'done' : 'failed';
    if (r.code !== 0) out(`(a) gh said: ${(r.err || r.out || '').trim().slice(0, 300)}`);
    L.autonomy('mantle-rollback', aTarget, res.a, out);
    if (r.code === 0) {
      const lt = gh(['api', `repos/${L.REPO}/releases/latest`]);
      let name = null; try { name = JSON.parse(lt.out).tag_name; } catch (_) { name = null; }
      out(name && name !== bad ? `(a) check: releases/latest is ${name} now` : `(a) check FAILED: releases/latest is ${name || 'unreadable'}`);
      const tg = git(['ls-remote', '--tags', 'origin', `refs/tags/${bad}`]);
      out(tg.code === 0 && tg.out.trim() ? `(a) check: the tag ${bad} is still on origin` : `(a) check FAILED: the tag ${bad} is not on origin any more`);
    }
  }

  // ---- (b) the last good tree as the next patch, a local commit on top of master
  let maxV = null;
  for (const r of list) { const v = r && L.parseVersion(r.tag_name); if (v && (!maxV || L.cmpVersion(v, maxV) > 0)) maxV = v; }
  const tags = git(['tag', '--list', 'v*']);
  for (const t of (tags.code === 0 ? tags.out : '').split(/\r?\n/)) { const v = L.parseVersion(t.trim()); if (v && (!maxV || L.cmpVersion(v, maxV) > 0)) maxV = v; }
  const next = maxV ? { major: maxV.major, minor: maxV.minor, patch: maxV.patch + 1 } : null;
  res.next = next ? L.vstr(next) : null;
  const branch = `wk/rollback-${bad}`;
  const wt = path.join(wtRoot, `rollback-${bad}`);
  res.branch = branch;
  const bWhy = !goodRel ? 'no good release to go back to'
    : !next ? 'no version to count from'
    : git(['merge-base', '--is-ancestor', `refs/tags/${bad}`, 'origin/master']).code !== 0 ? `origin/master does not contain ${bad}, so a commit on it would not reach the copies that took ${bad}`
    : git(['rev-parse', '--verify', '--quiet', `refs/heads/${branch}`]).code === 0 ? `the branch ${branch} already exists`
    : fsx.exists(wt) ? `${wt} already exists`
    : null;
  if (bWhy) { out(`(b) refused: ${bWhy}`); res.b = 'refused'; }
  else if (!o.send) {
    out(`(b) would prepare locally, in ${wt} on ${branch} from origin/master: the tree of ${goodRel.tag_name}, version ${res.next}, one commit`);
    res.b = 'would';
  } else {
    const step = (args, cwd) => { const r = git(args, cwd); if (r.code !== 0) throw new Error(`git ${args.join(' ')}: ${(r.err || r.out || '').trim().slice(0, 200)}`); return r; };
    try {
      step(['worktree', 'add', '-b', branch, wt, 'origin/master']);
      step(['read-tree', '-u', '--reset', `refs/tags/${goodRel.tag_name}`], wt);
      for (const f of ['package.json', 'package-lock.json']) {
        const p = path.join(wt, f);
        if (!fsx.exists(p)) continue;
        const j = JSON.parse(fsx.read(p));
        j.version = res.next;
        if (f === 'package-lock.json' && j.packages && j.packages['']) j.packages[''].version = res.next;
        fsx.write(p, JSON.stringify(j, null, 2) + '\n');
      }
      step(['add', '-A'], wt);
      step(['commit', '-q', '-m', `Rollback: ship ${goodRel.tag_name}'s tree as v${res.next} (undoes ${bad})`, '-m', `Made by tools/release-rollback.js. The tree is ${goodRel.tag_name}'s with the version set to ${res.next}; it sits on top of master, so copies on ${bad} fast-forward to it.`], wt);
      res.commit = step(['rev-parse', 'HEAD'], wt).out.trim();
      res.b = 'prepared';
      out(`(b) prepared ${String(res.commit).slice(0, 8)} on ${branch} in ${wt} (local only; nothing pushed)`);
    } catch (err) { res.b = 'failed'; out(`(b) failed: ${err.message}`); }
  }
  if (res.b === 'prepared' || res.b === 'would') {
    out('NEXT (each needs the owner\'s OK first; this tool never does them):');
    out(`  1. push ${branch}'s commit to master (installer.yml then builds the draft v${res.next})`);
    out(`  2. node tools/release-gate.js <that commit>: it prints the cloudtest commands and judges the runs`);
    out(`  3. node tools/release-gate.js <that commit> --core=... --ui=... --changelog-ok=... --send`);
    out(`USER NOTICE (release notes / README / Discord): "Mantle ${bad} was pulled. v${res.next} puts back ${res.good}. If Mantle does not start: installed copy, download Mantle-Setup.exe from the v${res.next} release page and run it; git clone, run: git fetch --tags && git merge --ff-only v${res.next}. To get the taskbar back now: Start > Mantle > Restore Windows taskbar (installed) or npm run restore (clone)."`);
  }
  return done();
}

function argsOf(argv) {
  const o = { bad: null, good: null, send: false };
  for (const a of argv) {
    if (a === '--send') o.send = true;
    else if (a.startsWith('--good=')) o.good = a.slice(7);
    else if (!a.startsWith('--') && !o.bad) o.bad = a;
    else throw new Error(`unknown argument ${a}`);
  }
  if (!o.bad) throw new Error('usage: node tools/release-rollback.js <bad tag> [--good=<tag>] [--send]');
  return o;
}

if (require.main === module) {
  let o;
  try { o = argsOf(process.argv.slice(2)); } catch (err) { console.log(`RELEASE ROLLBACK: refused (${err.message})`); process.exit(2); }
  rollback(o).then((r) => process.exit(r.a === 'failed' || r.b === 'failed' ? 1 : 0), (err) => { console.log(`RELEASE ROLLBACK: failed (it threw: ${err.message})`); process.exit(1); });
}

module.exports = { rollback, argsOf };
