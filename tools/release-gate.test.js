'use strict';
/*
 * tools/release-gate.test.js -- tools/release-gate.js and tools/release-lib.js on a FAKE git, a FAKE GitHub and
 * fake cloudtest run folders (lane release-gate, 2026-11-18). Pure node: nothing here runs git, gh or cloudtest,
 * and nothing reaches the network.
 *
 *   node tools/release-gate.test.js   -> "RELEASE GATE TEST: n passed, m failed"
 */
const path = require('path');
const L = require('./release-lib');
const G = require('./release-gate');

let passed = 0;
const failures = [];
function check(name, ok, detail) { if (ok) passed++; else failures.push(`${name}${detail !== undefined ? ` (${String(typeof detail === 'string' ? detail : JSON.stringify(detail)).slice(0, 400)})` : ''}`); }

const C = 'c'.repeat(40);          // the candidate
const PREV = 'a'.repeat(40);       // v0.1.1's commit
const SNAP = 's'.repeat(40);       // cloudtest's snapshot commit
const SNAP_UI = 'u'.repeat(40);
const SNAP_UP = 'e'.repeat(40);    // the upgrade run's snapshot
const UP_LINE = 'UPGRADE TEST: v0.1.1 -> 0.1.2: 41 passed, 0 failed';
const GOOD_PKG = JSON.stringify({ name: 'mantle', version: '0.1.1', main: 'main.js' });
const GOOD_LOCK = JSON.stringify({ name: 'mantle', version: '0.1.1', lockfileVersion: 3, packages: { '': { name: 'mantle', version: '0.1.1' } } });
const NOW = Date.parse('2026-11-20T12:00:00Z');
const CORE_CMD = 'node tools/run-suite.js --skip=ui/ --out=suite-out --lane=cloud-core';
const UI_CMD = 'node tools/run-suite.js --only=ui/ --out=suite-out --lane=cloud-ui';
const GREEN = 'TOTAL: 156 files: 154 pass, 0 FAIL, 0 known-env, 0 could-not-start, 0 timeout, 2 skipped | checks 12707 passed, 0 failed (+0 known-env)';

function world(over) {
  const w = Object.assign({
    version: '0.1.2', tags: 'v0.1.0\nv0.1.1\n', onMaster: true, prevAncestor: true, localTag: false, remoteTag: '',
    releases: [
      { tag_name: 'v0.1.1', draft: false, prerelease: false, published_at: '2026-11-10T00:00:00Z', target_commitish: PREV },
      { tag_name: 'v0.1.0', draft: false, prerelease: false, published_at: '2026-10-22T00:00:00Z' },
      { tag_name: 'v0.1.2', draft: true, prerelease: false, published_at: null, target_commitish: C, assets: [{ name: 'Mantle-Setup.exe', state: 'uploaded' }] },
    ],
    snaps: { [SNAP]: { diff: '.cloudtest-job/command.sh\n.cloudtest/setup.ps1\n.github/workflows/cloudtest.yml\n.github/workflows/oss-guard.yml\n', cmd: CORE_CMD },
      [SNAP_UI]: { diff: '.cloudtest-job/test.sh\n', cmd: UI_CMD },
      [SNAP_UP]: { diff: '.cloudtest-job/command.sh\n', cmd: G.UPGRADE_CMD } },
    runs: {
      core: { json: { base: C, mode: 'committed', snapshot: SNAP, conclusion: 'failure', exitCode: 1 }, log: `x\n@@cloudtest-begin@@\n2026-11-20T01:00:00Z ${GREEN}\n@@cloudtest-exit=0@@\n` },
      ui: { json: { base: C, mode: 'committed', snapshot: SNAP_UI, conclusion: 'success', exitCode: 0 }, log: `@@cloudtest-begin@@\nTOTAL: 92 files: 92 pass, 0 FAIL, 0 known-env, 0 could-not-start, 0 timeout, 0 skipped | checks 13830 passed, 0 failed\n@@cloudtest-exit=0@@\n` },
      upgrade: { json: { base: C, mode: 'committed', snapshot: SNAP_UP, conclusion: 'success', exitCode: 0 }, log: `@@cloudtest-begin@@\nok   upgraded: Mantle comes up live on the old profile\n2026-11-20T02:00:00.1234567Z ${UP_LINE}\n@@cloudtest-exit=0@@\n` },
    },
    // a rollback candidate's tree against the good release (git diff --name-only refs/tags/<good> <commit>)
    rollbackDiff: 'package.json\npackage-lock.json\n', goodPkg: GOOD_PKG, goodLock: GOOD_LOCK, lock: null, pkg: null, tagDates: {},
    pushOk: true, editOk: true, latestAfter: 'v0.1.2',
  }, over || {});
  w.calls = []; w.gitOpts = []; w.diffFlags = [];
  w.run = (file, args) => {
    w.calls.push([file].concat(args).join(' '));
    let a = args;
    if (file === 'git') {
      // git's own options first ("-c core.quotePath=false"), then diff's flags: recorded, then taken out
      if (a[0] === '-c') { w.gitOpts.push(a[1]); a = a.slice(2); }
      if (a[0] === 'diff') { w.diffFlags.push(a.filter((x) => /^--/.test(x)).join(' ')); a = a.filter((x) => !/^--(no-renames|name-only)$/.test(x)); a.splice(1, 0, '--name-only'); }
      if (a[0] === 'fetch') return { code: 0, out: '' };
      if (a[0] === 'rev-parse' && a[3] === `${C}^{commit}`) return { code: 0, out: C + '\n' };
      if (a[0] === 'rev-parse' && /^refs\/tags\//.test(a[3])) return { code: w.localTag ? 0 : 1, out: '' };
      if (a[0] === 'rev-parse') return { code: 1, out: '' };
      if (a[0] === 'merge-base' && a[3] === 'origin/master') return { code: w.onMaster ? 0 : 1, out: '' };
      if (a[0] === 'merge-base' && a[2] === 'v0.1.1') return { code: w.prevAncestor ? 0 : 1, out: '' };
      if (a[0] === 'show' && a[1] === `${C}:package.json`) return { code: 0, out: w.pkg || JSON.stringify({ name: 'mantle', version: w.version, main: 'main.js' }) };
      if (a[0] === 'show' && a[1] === `${C}:package-lock.json`) return { code: 0, out: w.lock || GOOD_LOCK.split('0.1.1').join(w.version) };
      if (a[0] === 'show' && /^refs\/tags\/v[\d.]+:package\.json$/.test(a[1])) return { code: 0, out: w.goodPkg };
      if (a[0] === 'show' && /^refs\/tags\/v[\d.]+:package-lock\.json$/.test(a[1])) return { code: 0, out: w.goodLock };
      if (a[0] === 'diff' && /^refs\/tags\//.test(a[2])) return { code: 0, out: w.rollbackDiff };
      if (a[0] === 'for-each-ref') { const t = a[2].replace('refs/tags/', ''); return { code: 0, out: w.tagDates[t] ? `${w.tagDates[t]}\n` : '' }; }
      if (a[0] === 'show' && /:\.cloudtest-job\/command\.sh$/.test(a[1])) { const s = w.snaps[a[1].split(':')[0]]; return s ? { code: 0, out: s.cmd + '\n' } : { code: 128, out: '' }; }
      if (a[0] === 'ls-remote') return { code: 0, out: w.remoteTag };
      if (a[0] === 'log') return { code: 0, out: '- Top bar: steps aside for full screen (cc81d79)\n- Task View on demand (512ec27)\n' };
      if (a[0] === 'cat-file') return { code: w.snaps[a[2].replace('^{commit}', '')] ? 0 : 1, out: '' };
      if (a[0] === 'diff') {
        const s = w.snaps[a[3]];
        if (!s) return { code: 128, out: '' };
        // a rename [old, new]: real git lists only the new path unless --no-renames
        const noRen = /--no-renames/.test(w.diffFlags[w.diffFlags.length - 1] || '');
        const ren = (s.renames || []).map(([o, n]) => (noRen ? `${o}\n${n}\n` : `${n}\n`)).join('');
        return { code: 0, out: s.diff + ren };
      }
      if (a[0] === 'tag' && a[1] === '--list') return { code: 0, out: w.tags };
      if (a[0] === 'tag') return { code: 0, out: '' };
      if (a[0] === 'push') return { code: w.pushOk ? 0 : 1, out: '', err: w.pushOk ? '' : 'rejected' };
    }
    if (file === 'gh') {
      if (a[0] === 'api' && /\/releases\?per_page=100$/.test(a[1])) return { code: 0, out: JSON.stringify(w.releases) };
      if (a[0] === 'api' && /\/releases\/latest$/.test(a[1])) return { code: 0, out: JSON.stringify({ tag_name: w.latestAfter }) };
      if (a[0] === 'release' && a[1] === 'edit') return { code: w.editOk ? 0 : 1, out: '' };
    }
    return { code: 99, out: '', err: `the fake has no answer for ${file} ${a.join(' ')}` };
  };
  w.readFile = (p) => {
    const k = path.basename(path.dirname(p)); const r = w.runs[k];
    if (!r) throw new Error('ENOENT ' + p);
    if (path.basename(p) === 'run.json') return JSON.stringify(r.json);
    if (path.basename(p) === 'log.txt') return r.log;
    throw new Error('ENOENT ' + p);
  };
  return w;
}
const DONE = [{ id: 'x', done: true, what: 'x' }];
async function go(w, extra) {
  const lines = [];
  const o = Object.assign({ commit: C, root: path.join('Z:', 'repo'), core: path.join('Z:', 'runs', 'core'), ui: path.join('Z:', 'runs', 'ui'), upgrade: path.join('Z:', 'runs', 'upgrade'), now: () => NOW, run: w.run, readFile: w.readFile,
    out: (l) => lines.push(l), expected: { core: CORE_CMD, ui: UI_CMD }, blockers: DONE }, extra || {});
  const first = await G.gate(Object.assign({}, o, { send: false }));
  if (o.changelogOk === undefined) o.changelogOk = first.hash;
  const lines2 = []; o.out = (l) => lines2.push(l);
  w.calls.length = 0;
  const r = await G.gate(o);
  return { r, lines: lines2, auto: lines2.filter((l) => l.startsWith('AUTONOMY: ')).map((l) => JSON.parse(l.slice(10))) };
}
const outward = (w) => w.calls.filter((c) => /^git push |^git tag -a |^gh release /.test(c));
const failed = (r) => r.gates.filter((g) => !g.ok).map((g) => g.id);

(async () => {
  // ---- release-lib, pure
  check('lib: patchWhy accepts exactly the next patch', L.patchWhy(L.parseVersion('v0.1.1'), L.parseVersion('0.1.2')) === null);
  check('lib: patchWhy refuses a minor bump', /not a patch/.test(L.patchWhy(L.parseVersion('0.1.1'), L.parseVersion('0.2.0'))));
  check('lib: patchWhy refuses a skipped patch', /not the next patch/.test(L.patchWhy(L.parseVersion('0.1.1'), L.parseVersion('0.1.3'))));
  check('lib: parseVersion refuses a pre-release', L.parseVersion('0.1.2-beta.1') === null);
  check('lib: latestPublished ignores drafts and pre-releases', L.latestPublished([{ tag_name: 'v0.2.0', draft: true }, { tag_name: 'v0.1.9', prerelease: true }, { tag_name: 'v0.1.1' }, { tag_name: 'v0.1.0' }]).r.tag_name === 'v0.1.1');
  const lines = []; L.autonomy('mantle-release', 'tag v0.1.2 on origin', 'would', (l) => lines.push(l));
  check('lib: the AUTONOMY line is exactly the agreed shape', lines[0] === 'AUTONOMY: {"class":"mantle-release","target":"tag v0.1.2 on origin","result":"would"}', lines[0]);
  check('lib: judgeLog reads a green TOTAL inside the markers', L.judgeLog(`@@cloudtest-begin@@\n${GREEN}\n@@cloudtest-exit=0@@`).ok === true);
  check('lib: judgeLog refuses 1 FAIL whatever the exit marker says', /1 FAIL/.test(L.judgeLog(`@@cloudtest-begin@@\n${GREEN.replace('154 pass, 0 FAIL', '153 pass, 1 FAIL')}\n@@cloudtest-exit=0@@`).why || ''));
  check('lib: judgeLog refuses two begin markers', /2 "@@cloudtest-begin@@" markers/.test(L.judgeLog(`@@cloudtest-begin@@\n@@cloudtest-begin@@\n${GREEN}\n@@cloudtest-exit=0@@`).why || ''));
  check('lib: judgeLog refuses two exit markers (an echoed second exit after a green TOTAL)', /2 "@@cloudtest-exit=N@@" markers/.test(L.judgeLog(`@@cloudtest-begin@@\n${GREEN}\n@@cloudtest-exit=1@@\n@@cloudtest-exit=0@@`).why || ''));
  check('lib: judgeLog refuses an exit marker before the begin', /comes before/.test(L.judgeLog(`@@cloudtest-exit=0@@\n@@cloudtest-begin@@\n${GREEN}`).why || ''));
  check('lib: judgeLog refuses no exit marker', /no "@@cloudtest-exit=N@@" marker/.test(L.judgeLog(`@@cloudtest-begin@@\n${GREEN}`).why || ''));
  check('lib: judgeLog still reads timestamp-prefixed markers', L.judgeLog(`2026-11-20T01:00:00Z @@cloudtest-begin@@\n2026-11-20T01:00:01Z ${GREEN}\n2026-11-20T01:00:02Z @@cloudtest-exit=0@@`).ok === true);
  check('lib: nameDiffArgs = git -c core.quotePath=false diff --no-renames --name-only', L.nameDiffArgs('A', 'B').join(' ') === '-c core.quotePath=false diff --no-renames --name-only A B');
  check('lib: judgeLog refuses a TOTAL only after the exit marker (an appended echo)', !L.judgeLog(`@@cloudtest-begin@@\nsomething\n@@cloudtest-exit=0@@\n${GREEN}`).ok);
  check('lib: judgeLog refuses two TOTAL lines', /2 TOTAL lines/.test(L.judgeLog(`@@cloudtest-begin@@\n${GREEN}\n${GREEN}\n@@cloudtest-exit=0@@`).why || ''));
  check('lib: judgeLog refuses counts that do not add up', /do not add up/.test(L.judgeLog(`@@cloudtest-begin@@\n${GREEN.replace('156 files', '160 files')}\n@@cloudtest-exit=0@@`).why || ''));
  check('lib: judgeLog refuses could-not-start and timeout', /could-not-start/.test(L.judgeLog(`@@cloudtest-begin@@\nTOTAL: 3 files: 1 pass, 0 FAIL, 0 known-env, 1 could-not-start, 1 timeout, 0 skipped | checks 1 passed, 0 failed\n@@cloudtest-exit=0@@`).why || ''));
  check('lib: commandWhy refuses "; echo X=$?" appended', !!L.commandWhy(`${CORE_CMD}; echo X=$?`, `${CORE_CMD}; echo X=$?`));
  check('lib: harnessOnlyWhy names a real file that differs', /lib\/x\.js/.test(L.harnessOnlyWhy(['.cloudtest-job/a', 'lib/x.js']) || ''));

  // ---- the gate
  {
    const w = world();
    const { r, auto } = await go(w);
    check('gate: everything green, no --send = GO and both steps "would", nothing outward run', r.go && auto.length === 2 && auto.every((a) => a.result === 'would' && a.class === 'mantle-release') && outward(w).length === 0, { failed: failed(r), auto, out: outward(w) });
    check('gate: run.json\'s conclusion "failure" / exitCode 1 is NOT used (core is green by its TOTAL line)', r.gates.find((g) => g.id === 'suite core').ok);
  }
  {
    const w = world();
    const { r, auto, lines: ls } = await go(w, { send: true });
    const oc = outward(w);
    check('gate: --send and GO = tag, push the tag, publish the draft, in that order, each "done"', r.go && auto.map((a) => a.result).join() === 'done,done' && oc.length === 3 && /^git tag -a v0\.1\.2/.test(oc[0]) && /^git push origin refs\/tags\/v0\.1\.2$/.test(oc[1]) && /^gh release edit v0\.1\.2 .*--draft=false/.test(oc[2]), { auto, oc });
    check('gate: --send checks releases/latest afterwards', ls.includes('check: releases/latest is v0.1.2'), ls.slice(-4));
  }
  {
    const w = world({ pushOk: false });
    const { auto } = await go(w, { send: true });
    check('gate: the tag push fails = "failed", and publishing is then refused', auto.map((a) => a.result).join() === 'failed,refused' && !outward(w).some((c) => /^gh release/.test(c)), { auto, oc: outward(w) });
  }
  const refusedCase = async (name, over, extra, wantGate) => {
    const w = world(over);
    const { r, auto } = await go(w, Object.assign({ send: true }, extra || {}));
    check(`gate: ${name} = NO-GO on "${wantGate}", both steps refused, nothing outward`, !r.go && failed(r).includes(wantGate) && auto.every((a) => a.result === 'refused') && outward(w).length === 0, { failed: failed(r), auto, oc: outward(w) });
  };
  await refusedCase('a minor bump', { version: '0.2.0' }, null, 'patch');
  await refusedCase('a skipped patch', { version: '0.1.3' }, null, 'patch');
  await refusedCase('the last release is not an ancestor', { prevAncestor: false }, null, 'patch');
  await refusedCase('not on origin/master', { onMaster: false }, null, 'commit');
  await refusedCase('the tag exists on origin', { remoteTag: `${C}\trefs/tags/v0.1.2\n` }, null, 'new tag');
  await refusedCase('the tag exists here', { localTag: true }, null, 'new tag');
  await refusedCase('a release less than 48 h ago', {}, { now: () => Date.parse('2026-11-11T12:00:00Z') }, '48 h');
  {
    const w = world();
    w.releases = w.releases.map((x) => (x.draft ? Object.assign({}, x, { target_commitish: PREV }) : x));
    const { r } = await go(w, { send: true });
    check('gate: a draft built from another commit = NO-GO on "build"', !r.go && failed(r).includes('build'), failed(r));
  }
  {
    const w = world();
    w.releases = w.releases.map((x) => (x.draft ? Object.assign({}, x, { assets: [] }) : x));
    const { r } = await go(w);
    check('gate: a draft without Mantle-Setup.exe = NO-GO on "build"', !r.go && failed(r).includes('build'), failed(r));
  }
  {
    const w = world();
    const { r } = await go(w, { blockers: undefined });
    check('gate: the real BLOCKERS are open today, so the gate is NO-GO on "blockers"', !r.go && failed(r).includes('blockers') && G.BLOCKERS.length === 3 && G.BLOCKERS.every((b) => !b.done), failed(r));
  }
  {
    const w = world();
    w.runs.core.json.conclusion = 'success'; w.runs.core.json.exitCode = 0;
    w.runs.core.log = `@@cloudtest-begin@@\n${GREEN.replace('154 pass, 0 FAIL', '152 pass, 2 FAIL')}\n@@cloudtest-exit=0@@\n`;
    const { r } = await go(w);
    check('gate: run.json says success / exit 0 but the TOTAL line has 2 FAIL = NO-GO on "suite core"', !r.go && failed(r).join() === 'suite core', failed(r));
  }
  {
    const w = world();
    w.snaps[SNAP].cmd = `${CORE_CMD}; echo X=$?`;
    const { r } = await go(w);
    check('gate: a job command with "; echo X=$?" = NO-GO on "suite core"', !r.go && failed(r).join() === 'suite core', r.gates.find((g) => g.id === 'suite core'));
  }
  {
    const w = world();
    w.snaps[SNAP].diff += 'lib/updater.js\n';
    const { r } = await go(w);
    check('gate: a snapshot that differs from the commit in lib/ (a working-tree snapshot) = NO-GO', !r.go && /outside cloudtest's own files: lib\/updater\.js/.test(r.gates.find((g) => g.id === 'suite core').why || ''), r.gates.find((g) => g.id === 'suite core'));
  }
  {
    const w = world();
    w.runs.ui.json.mode = 'working-tree';
    const { r } = await go(w);
    check('gate: a run not in committed mode = NO-GO on "suite ui"', !r.go && failed(r).join() === 'suite ui', failed(r));
  }
  {
    const w = world();
    w.runs.ui.json.base = PREV;
    const { r } = await go(w);
    check('gate: a run of another commit = NO-GO on "suite ui"', !r.go && failed(r).join() === 'suite ui', failed(r));
  }
  {
    const w = world();
    delete w.snaps[SNAP_UI];
    const { r } = await go(w);
    check('gate: a snapshot commit that is not here = NO-GO (the tree cannot be compared)', !r.go && /is not here/.test(r.gates.find((g) => g.id === 'suite ui').why || ''), r.gates.find((g) => g.id === 'suite ui'));
  }
  {
    const w = world();
    const { r } = await go(w, { core: undefined, ui: undefined });
    check('gate: no run dirs = NO-GO on both suite gates', !r.go && failed(r).join() === 'suite core,suite ui', failed(r));
  }
  {
    const w = world();
    const { r } = await go(w, { changelogOk: '000000000000' });
    check('gate: a changelog hash that does not match = NO-GO on "review"', !r.go && failed(r).join() === 'review', failed(r));
  }
  {
    const w = world();
    const { lines: ls } = await go(w);
    const i = ls.findIndex((l) => l.startsWith('CHANGELOG v0.1.1..'));
    check('gate: the changelog is printed between CHANGELOG and END CHANGELOG with its hash', i >= 0 && ls[i + 1].startsWith('- Top bar') && ls.includes('END CHANGELOG') && /hash [0-9a-f]{12}/.test(ls[i]), ls.slice(i, i + 4));
    check('gate: the exact cloudtest commands are printed (both public recipes, from a worktree at the commit)', ls.some((l) => /cloudtest --recipe mantle-core-public$/.test(l)) && ls.some((l) => /cloudtest --recipe mantle-ui-public$/.test(l)) && ls.some((l) => /worktree add --detach .* c{40}$/.test(l)));
    check('gate: the last line is the summary', /^RELEASE GATE: GO \(\d+ of \d+ gates passed\)$/.test(ls[ls.length - 1]), ls[ls.length - 1]);
  }
  {
    // a rollback's patch: v0.1.2 was drafted back (its tag stays), so the candidate is v0.1.3 after v0.1.2
    const w = world({ version: '0.1.3', tags: 'v0.1.0\nv0.1.1\nv0.1.2\n' });
    w.releases = [
      { tag_name: 'v0.1.1', draft: false, prerelease: false, published_at: '2026-11-10T00:00:00Z' },
      { tag_name: 'v0.1.2', draft: true, prerelease: false, published_at: '2026-11-19T00:00:00Z', target_commitish: PREV },
      { tag_name: 'v0.1.3', draft: true, prerelease: false, target_commitish: C, assets: [{ name: 'Mantle-Setup.exe', state: 'uploaded' }] },
    ];
    w.runs.upgrade.log = w.runs.upgrade.log.replace('-> 0.1.2', '-> 0.1.3');
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    const p = r.gates.find((g) => g.id === 'patch');
    check('gate: a rollback patch (v0.1.3 after a drafted v0.1.2) passes "patch", counted from the highest tag', r.go && /v0\.1\.3 after v0\.1\.2 \(the highest tag; the latest published is v0\.1\.1\)/.test(p.why || ''), { failed: failed(r), p });
    const w2 = world({ version: '0.1.2', tags: 'v0.1.0\nv0.1.1\nv0.1.2\n', localTag: true });
    const { r: r2 } = await go(w2);
    check('gate: reusing a drafted-back number (v0.1.2 again) = NO-GO on "patch" and "new tag"', !r2.go && failed(r2).includes('patch') && failed(r2).includes('new tag'), failed(r2));
  }
  // ---- the 48 h cap and --rollback-of (spec v1.0 2.1: a rollback has the same gates minus the 48 h cap)
  const rbWorld = (over) => {
    const w = world(Object.assign({ version: '0.1.3', tags: 'v0.1.0\nv0.1.1\nv0.1.2\n' }, over || {}));
    w.releases = [
      { tag_name: 'v0.1.1', draft: false, prerelease: false, published_at: '2026-11-10T00:00:00Z' },
      { tag_name: 'v0.1.2', draft: true, prerelease: false, published_at: '2026-11-19T00:00:00Z', target_commitish: PREV },
      { tag_name: 'v0.1.3', draft: true, prerelease: false, target_commitish: C, assets: [{ name: 'Mantle-Setup.exe', state: 'uploaded' }] },
    ];
    w.runs.upgrade.log = w.runs.upgrade.log.replace('-> 0.1.2', '-> 0.1.3');
    return w;
  };
  const g48 = (r) => r.gates.find((g) => g.id === '48 h') || {};
  {
    const { r } = await go(rbWorld(), { rollbackOf: 'v0.1.2' });
    check('48 h: --rollback-of a drafted-back release with the good tree = skipped, and GO', r.go && g48(r).ok && /^skipped: the rollback of v0\.1\.2 \(back to draft\); the tree is v0\.1\.1's apart from the version$/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const { r } = await go(rbWorld());
    check('48 h: the same candidate WITHOUT --rollback-of = NO-GO on "48 h" (the clock starts at the drafted-back v0.1.2, 36 h ago)', !r.go && failed(r).join() === '48 h' && /v0\.1\.2 was published \(it is a draft now\) 36\.0 h ago/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const w = rbWorld();
    w.releases[1] = Object.assign({}, w.releases[1], { published_at: null });
    w.tagDates = { 'v0.1.2': String(Math.round(Date.parse('2026-11-20T02:00:00Z') / 1000)) };
    const { r } = await go(w);
    check('48 h: a drafted-back release with no published_at: the clock starts at its tag (10 h ago) = NO-GO', !r.go && failed(r).join() === '48 h' && /the tag v0\.1\.2 was made 10\.0 h ago/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const w = rbWorld();
    w.releases[1] = Object.assign({}, w.releases[1], { draft: false });
    w.releases[0] = Object.assign({}, w.releases[0]);
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    check('48 h: --rollback-of a release that is still published = NO-GO on "48 h"', !r.go && failed(r).includes('48 h') && /still published; put it back to draft first/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const w = rbWorld({ rollbackDiff: 'package.json\nlib/updater.js\n' });
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    check('48 h: --rollback-of with a tree that also differs in lib/ = NO-GO on "48 h" (no fallback to the clock)', !r.go && failed(r).join() === '48 h' && /the tree is not v0\.1\.1's; it also differs in lib\/updater\.js/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const w = rbWorld({ pkg: JSON.stringify({ name: 'mantle', version: '0.1.3', main: 'lib/other.js' }) });
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    check('48 h: --rollback-of where package.json differs in more than its version = NO-GO', !r.go && failed(r).join() === '48 h' && /package\.json differs from v0\.1\.1's in more than its version/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const w = rbWorld({ lock: JSON.stringify({ name: 'mantle', version: '0.1.3', lockfileVersion: 3, packages: { '': { name: 'mantle', version: '0.1.3' }, 'node_modules/x': { version: '9.9.9' } } }) });
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    check('48 h: --rollback-of where package-lock.json changed a dependency = NO-GO', !r.go && /package-lock\.json differs from v0\.1\.1's/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    // v0.1.2 was drafted back, but v0.1.3 is published above it: a v0.1.4 is not a rollback of v0.1.2 any more
    const w = rbWorld({ version: '0.1.4', tags: 'v0.1.0\nv0.1.1\nv0.1.2\nv0.1.3\n' });
    w.releases[2] = { tag_name: 'v0.1.3', draft: false, prerelease: false, published_at: '2026-11-01T00:00:00Z' };
    w.releases.push({ tag_name: 'v0.1.4', draft: true, prerelease: false, target_commitish: C, assets: [{ name: 'Mantle-Setup.exe', state: 'uploaded' }] });
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    check('48 h: --rollback-of when a release above the bad one is the latest published = NO-GO on "48 h"', !r.go && failed(r).includes('48 h') && /the latest published release is v0\.1\.3, not v0\.1\.1/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const { r } = await go(world(), { rollbackOf: 'v0.1.1' });
    check('48 h: --rollback-of the latest published release itself (not drafted) = NO-GO', !r.go && failed(r).includes('48 h') && /still published/.test(g48(r).why || ''), { failed: failed(r), g: g48(r) });
  }
  {
    const w = world();
    w.releases[0] = Object.assign({}, w.releases[0], { published_at: '2026-11-19T00:00:00Z' });
    const { r } = await go(w, { rollbackOf: 'v0.1.9' });
    check('48 h: --rollback-of a release that does not exist = NO-GO', !r.go && /there is no release v0\.1\.9/.test(g48(r).why || ''), g48(r));
  }
  check('lib: withoutVersion drops only the version fields', G.withoutVersion(GOOD_LOCK) === G.withoutVersion(GOOD_LOCK.split('0.1.1').join('0.1.3')) && G.withoutVersion('{"version":"1.0.0","a":1}') !== G.withoutVersion('{"version":"1.0.0","a":2}'));

  {
    // a source file MOVED into .cloudtest/ in the snapshot: without --no-renames git lists only .cloudtest/x.js
    const w = world();
    w.snaps[SNAP].renames = [['lib/updater.js', '.cloudtest/updater.js']];
    const { r } = await go(w);
    const g = r.gates.find((x) => x.id === 'suite core') || {};
    check('gate: a snapshot that moved lib/updater.js into .cloudtest/ = NO-GO on "suite core" (the old path is named)', !r.go && failed(r).join() === 'suite core' && /outside cloudtest's own files: lib\/updater\.js/.test(g.why || ''), { failed: failed(r), g });
    check('gate: every name diff runs with --no-renames and core.quotePath=false', w.diffFlags.length > 0 && w.diffFlags.every((f) => /--no-renames/.test(f)) && w.gitOpts.filter((o) => o === 'core.quotePath=false').length === w.diffFlags.length, { flags: w.diffFlags, opts: w.gitOpts });
  }
  {
    const w = rbWorld({ rollbackDiff: 'package.json\n' });
    const { r } = await go(w, { rollbackOf: 'v0.1.2' });
    check('48 h: the rollback tree check runs with --no-renames too', r.go && w.diffFlags.some((f) => /--no-renames/.test(f)), { failed: failed(r), flags: w.diffFlags });
  }

  // ---- the upgrade gate (the eleventh)
  {
    const w = world();
    const { r } = await go(w);
    check('upgrade: a green run from the latest published release to this version passes', r.go && r.gates.length === 11 && (r.gates.find((g) => g.id === 'upgrade') || {}).why === UP_LINE, r.gates.find((g) => g.id === 'upgrade'));
  }
  const upCase = async (name, mut, want) => {
    const w = world(); mut(w);
    const { r } = await go(w);
    const u = r.gates.find((g) => g.id === 'upgrade') || {};
    check(`upgrade: ${name} = NO-GO on "upgrade" only`, !r.go && failed(r).join() === 'upgrade' && want.test(u.why || ''), { failed: failed(r), u });
  };
  await upCase('1 failed check', (w) => { w.runs.upgrade.log = w.runs.upgrade.log.replace('41 passed, 0 failed', '40 passed, 1 failed'); }, /not green: 40 passed, 1 failed/);
  await upCase('an upgrade from v0.1.0 (not the latest published)', (w) => { w.runs.upgrade.log = w.runs.upgrade.log.replace('v0.1.1 ->', 'v0.1.0 ->'); }, /upgraded from v0.1.0, not from the latest published release v0.1.1/);
  await upCase('an upgrade to another version', (w) => { w.runs.upgrade.log = w.runs.upgrade.log.replace('-> 0.1.2', '-> 0.1.4'); }, /upgraded to 0.1.4, not to 0.1.2/);
  await upCase('the line only after the exit marker', (w) => { w.runs.upgrade.log = `@@cloudtest-begin@@\nx\n@@cloudtest-exit=0@@\n${UP_LINE}\n`; }, /0 UPGRADE TEST lines/);
  await upCase('two exit markers', (w) => { w.runs.upgrade.log = w.runs.upgrade.log.replace('@@cloudtest-exit=0@@', '@@cloudtest-exit=1@@\n@@cloudtest-exit=0@@'); }, /2 "@@cloudtest-exit=N@@" markers/);
  await upCase('two begin markers', (w) => { w.runs.upgrade.log = `@@cloudtest-begin@@\n${w.runs.upgrade.log}`; }, /2 "@@cloudtest-begin@@" markers/);
  await upCase('a changed job command', (w) => { w.snaps[SNAP_UP].cmd = `${G.UPGRADE_CMD} -From v0.1.0`; }, /the job ran/);
  await upCase('no --upgrade run dir', (w) => { delete w.runs.upgrade; }, /run.json could not be read/);
  {
    const w = world();
    const { r } = await go(w, { upgrade: undefined });
    check('upgrade: no --upgrade = NO-GO, named', !r.go && failed(r).join() === 'upgrade' && /no --upgrade=<cloudtest run dir> given/.test(r.gates.find((g) => g.id === 'upgrade').why), failed(r));
  }
  {
    const w = world();
    const { lines: ls } = await go(w);
    check('upgrade: its cloudtest command is printed (committed mode, the exact job command)', ls.some((l) => l.endsWith(`cloudtest --committed --os windows --timeout 50 --artifacts "upgrade-out/**" -- "${G.UPGRADE_CMD}"`)), ls.filter((l) => /cloudtest/.test(l)));
  }

  {
    let threw = null; try { G.argsOf([]); } catch (err) { threw = err.message; }
    check('cli: no commit = usage', /usage/.test(threw || ''));
    const a = G.argsOf(['abc', '--core=x', '--ui=y', '--upgrade=z', '--rollback-of=v0.1.2', '--changelog-ok=h', '--send']);
    check('cli: arguments parse', a.commit === 'abc' && a.core === 'x' && a.ui === 'y' && a.upgrade === 'z' && a.rollbackOf === 'v0.1.2' && a.changelogOk === 'h' && a.send === true, a);
  }

  for (const f of failures) console.log('FAIL ' + f);
  console.log(`RELEASE GATE TEST: ${passed} passed, ${failures.length} failed`);
  process.exit(failures.length ? 1 : 0);
})().catch((err) => { console.log(`FAIL threw: ${err.stack}`); console.log(`RELEASE GATE TEST: ${passed} passed, ${failures.length + 1} failed`); process.exit(1); });
