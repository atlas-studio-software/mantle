'use strict';
/*
 * tools/release-gate.js -- the gate in front of a Mantle PATCH release (lane release-gate, 2026-11-18).
 *
 *   node tools/release-gate.js <commit> [--core=<cloudtest run dir>] [--ui=<cloudtest run dir>]
 *                              [--upgrade=<cloudtest run dir>] [--rollback-of=<bad tag>]
 *                              [--changelog-ok=<12 hex>] [--send]
 *
 * Without the run dirs it checks what it can locally, prints the changelog for review and the exact cloudtest
 * commands, and says NO-GO (no suite evidence yet). With both run dirs (".cloudtest/<id>" folders that cloudtest
 * writes: run.json + log.txt) it judges them. GO needs EVERY gate below; --send then tags and publishes. Without
 * --send nothing outward happens: each outward step prints its AUTONOMY line with result "would" (GO) or
 * "refused" (NO-GO). The last line is "RELEASE GATE: GO|NO-GO (<n> of <m> gates passed)".
 *
 * GATES
 *   commit      resolves to a commit, and origin/master contains it (merged under the merge rules)
 *   version     package.json at the commit is a plain x.y.z
 *   patch       it is exactly the patch after the highest version that exists (published releases and every
 *               v* tag: a release a rollback drafted keeps its tag and its number), with the same major.minor;
 *               and the latest PUBLISHED release's tag (drafts and pre-releases ignored) is an ancestor of the
 *               commit (clones only fast-forward, lib/updater.js)
 *   new tag     v<version> exists neither here nor on origin (a tag is never moved or reused)
 *   48 h        at least 48 h since the last release: the latest published release's publish time, or later
 *               when a release above it was published and then drafted back (its published_at, else its
 *               tag's date). Skipped ONLY with --rollback-of=<bad tag>, and only when all of these hold: <bad>
 *               is a release that is back to DRAFT; the release just below it is the latest published one
 *               (the last good release); and the commit's tree equals that good release's tree apart from the
 *               version in package.json and package-lock.json (what tools/release-rollback.js makes). A
 *               --rollback-of that does not hold fails this gate; it never falls back to the clock.
 *   build       a DRAFT release v<version> exists whose target is this exact commit and which holds
 *               Mantle-Setup.exe (installer.yml builds and install-tests it from that commit)
 *   blockers    every item in BLOCKERS below is done (rollback runbook: release blocked until they exist)
 *   suite       both cloudtest runs (mantle-core-public, mantle-ui-public) are green, judged ONLY from the
 *               TOTAL line inside the job log, never run.json's conclusion or exit code; run.json's base is
 *               the commit, its mode is "committed", the snapshot commit is here and differs from the commit
 *               only in cloudtest's own files, and the job's command is exactly the recipe's (no "; echo")
 *   upgrade     a cloudtest run of installer/ci-upgrade-test.ps1 at the commit (run dir --upgrade=, checked like the
 *               suite runs: base, committed mode, snapshot tree, exact command): exactly one
 *               "UPGRADE TEST: <latest published tag> -> <version>: n passed, 0 failed" line inside the markers
 *               (it installs the latest published Mantle-Setup.exe, takes it live, installs this commit's
 *               build over it while live, and proves the upgraded copy comes up live on the old profile)
 *   review      --changelog-ok equals the changelog's hash (printed below), so the reviewed text is the one shipped
 *
 * AUTONOMOUS MODE (contracts/autonomy.md section 1, read through release-lib modeState): with --send, before any
 * other check, PAUSED refuses, and ON refuses when mode.json "holds" lists "mantle-release". Both print the AUTONOMY
 * lines "refused" with why "paused" or "held", nothing else runs, and the exit code is 3. OFF: as before.
 *
 * Read-only network use without --send: `git fetch origin --tags`, `git ls-remote`, `gh api` GETs.
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const L = require('./release-lib');

/** The rollback runbook's three preconditions. Flip one only in a reviewed commit, with the proof named. */
const BLOCKERS = Object.freeze([
  { id: 'upgrade-check', done: false, what: 'an upgrade-from-previous live check in cloudtest' },
  // done: cloudtest run 44475938920 of 1ae43a7b alone (updater.e2e + toasts): 252 passed, 0 failed; updater.e2e 148/148
  { id: 'updater-e2e', done: true, what: 'lib/updater.e2e.test.js green in cloudtest (1ae43a7b: run 44475938920, 252/0, updater.e2e 148/148)' },
  { id: 'oob-restore', done: false, what: 'an out-of-band restore that works with Mantle not running' },
]);
const RECIPES = { core: 'mantle-core-public', ui: 'mantle-ui-public' };
/** The upgrade check's job command (no recipe: run as `cloudtest --committed ... -- "<this>"`, see the CLOUDTEST lines). */
const UPGRADE_CMD = 'powershell -NoProfile -ExecutionPolicy Bypass -File installer/ci-upgrade-test.ps1 -Out upgrade-out';
/** The files a rollback's tree may differ in from the good release's, and then only in their version fields. */
const VERSION_FILES = ['package.json', 'package-lock.json'];
const UPGRADE_RE = /^UPGRADE TEST: (v\d+\.\d+\.\d+) -> (\d+\.\d+\.\d+): (\d+) passed, (\d+) failed$/;

/** A package.json / package-lock.json with its version fields taken out, as one string to compare. */
function withoutVersion(text) {
  const j = JSON.parse(text);
  delete j.version;
  if (j.packages && j.packages['']) delete j.packages[''].version;
  return JSON.stringify(j);
}

/** --rollback-of=<bad>: is the commit a real rollback of <bad> (see "48 h" above)? -> { why } | { good } */
function rollbackWhy(bad, list, latest, sha, git) {
  const bv = L.parseVersion(bad);
  if (!bv || !String(bad).startsWith('v')) return { why: `--rollback-of=${bad} is not a release tag like v1.2.3` };
  const rel = list.find((r) => r && r.tag_name === bad);
  if (!rel) return { why: `--rollback-of=${bad}: there is no release ${bad}` };
  if (!rel.draft) return { why: `--rollback-of=${bad}: ${bad} is still published; put it back to draft first (node tools/release-rollback.js ${bad} --send)` };
  const good = L.previousPublished(list, bad);
  if (!good) return { why: `--rollback-of=${bad}: there is no published release before ${bad}` };
  if (!latest || latest.r.tag_name !== good.r.tag_name) return { why: `--rollback-of=${bad}: the latest published release is ${latest ? latest.r.tag_name : 'none'}, not ${good.r.tag_name} (the last good one below ${bad})` };
  const gt = `refs/tags/${good.r.tag_name}`;
  const dn = git(L.nameDiffArgs(gt, sha));
  if (dn.code !== 0) return { why: `--rollback-of=${bad}: git could not compare the commit with ${good.r.tag_name} (exit ${dn.code})` };
  const changed = dn.out.split(/\r?\n/).map((x) => x.trim()).filter(Boolean);
  const other = changed.filter((f) => !VERSION_FILES.includes(f));
  if (other.length) return { why: `--rollback-of=${bad}: the tree is not ${good.r.tag_name}'s; it also differs in ${other.slice(0, 5).join(', ')}${other.length > 5 ? ` and ${other.length - 5} more` : ''}` };
  for (const f of changed) {
    const a = git(['show', `${gt}:${f}`]);
    const b = git(['show', `${sha}:${f}`]);
    let same = false;
    try { same = a.code === 0 && b.code === 0 && withoutVersion(a.out) === withoutVersion(b.out); } catch (_) { same = false; }
    if (!same) return { why: `--rollback-of=${bad}: ${f} differs from ${good.r.tag_name}'s in more than its version` };
  }
  return { good: good.r.tag_name };
}

/** When the 48 h clock starts: the latest published release, or a later release that was published and drafted back. */
function clockStart(list, latest, git) {
  let at = Date.parse(latest.r.published_at), what = `${latest.r.tag_name} was published`;
  for (const r of list) {
    const v = r && L.parseVersion(r.tag_name);
    if (!v || L.cmpVersion(v, latest.v) <= 0) continue;
    const t = r.published_at ? Date.parse(r.published_at) : NaN;
    if (Number.isFinite(t) && !(t <= at)) { at = t; what = `${r.tag_name} was published (it is a ${r.draft ? 'draft' : 'pre-release'} now)`; }
  }
  const tl = git(['tag', '--list', 'v*']);
  for (const tag of (tl.code === 0 ? tl.out : '').split(/\r?\n/).map((x) => x.trim())) {
    const v = L.parseVersion(tag);
    if (!v || L.cmpVersion(v, latest.v) <= 0) continue;
    const d = git(['for-each-ref', '--format=%(creatordate:unix)', `refs/tags/${tag}`]);
    const t = d.code === 0 ? Number(d.out.trim()) * 1000 : NaN;
    if (Number.isFinite(t) && t > 0 && !(t <= at)) { at = t; what = `the tag ${tag} was made`; }
  }
  return { at, what };
}

/** The checks a cloudtest run dir must pass before its log is read. -> { why } | { log } */
function runDirWhy(dir, k, sha, expected, readFile, git) {
  if (!dir) return { why: `no --${k}=<cloudtest run dir> given` };
  let rj = null, log = null;
  try { rj = JSON.parse(readFile(path.join(dir, 'run.json'))); } catch (err) { return { why: `run.json could not be read (${err.message})` }; }
  try { log = readFile(path.join(dir, 'log.txt')); } catch (err) { return { why: `log.txt could not be read (${err.message})` }; }
  if (rj.base !== sha) return { why: `the run tested ${String(rj.base).slice(0, 8)}, not ${String(sha).slice(0, 8)}` };
  if (rj.mode !== 'committed') return { why: `the run's mode is "${rj.mode}", not "committed" (a working-tree snapshot is not the commit)` };
  const snap = typeof rj.snapshot === 'string' ? rj.snapshot : '';
  if (git(['cat-file', '-e', `${snap}^{commit}`]).code !== 0) return { why: `the snapshot commit ${snap.slice(0, 8) || '(none)'} is not here, so its tree cannot be compared` };
  const dn = git(L.nameDiffArgs(sha, snap));
  if (dn.code !== 0) return { why: `git could not compare the snapshot with the commit (exit ${dn.code})` };
  const tw = L.harnessOnlyWhy(dn.out.split(/\r?\n/).filter(Boolean));
  if (tw) return { why: tw };
  const cmd = git(['show', `${snap}:.cloudtest-job/command.sh`]);
  const cw = L.commandWhy(cmd.code === 0 ? cmd.out : '', expected);
  if (cw) return { why: cw };
  return { log };
}

/** The upgrade run's log: exactly one UPGRADE TEST line inside cloudtest's markers, from `from` to `to`, 0 failed. */
function judgeUpgrade(text, from, to) {
  const lines = String(text || '').split(/\r?\n/);
  const mk = L.markers(lines);
  if (mk.why) return { ok: false, why: mk.why };
  const { b, e } = mk;
  const hits = lines.slice(b + 1, e).map((l) => l.replace(/^\S+Z /, '').trim()).filter((l) => l.startsWith('UPGRADE TEST:'));
  if (hits.length !== 1) return { ok: false, why: `the job log has ${hits.length} UPGRADE TEST lines, not exactly one` };
  const m = UPGRADE_RE.exec(hits[0]);
  if (!m) return { ok: false, why: `the UPGRADE TEST line is not in its format: ${hits[0].slice(0, 200)}` };
  if (m[1] !== from) return { ok: false, why: `it upgraded from ${m[1]}, not from the latest published release ${from}`, line: hits[0] };
  if (m[2] !== to) return { ok: false, why: `it upgraded to ${m[2]}, not to ${to}`, line: hits[0] };
  if (Number(m[4]) !== 0 || Number(m[3]) === 0) return { ok: false, why: `not green: ${m[3]} passed, ${m[4]} failed`, line: hits[0] };
  return { ok: true, line: hits[0] };
}

function recipeCommand(name, dir) {
  const d = dir || process.env.CLOUDTEST_RECIPES_DIR || path.join(os.homedir(), '.claude', 'tools', 'cloudtest', 'recipes.local');
  try { const j = JSON.parse(fs.readFileSync(path.join(d, `${name}.json`), 'utf8')); return typeof j.command === 'string' ? j.command : null; } catch (_) { return null; }
}

/**
 * o: { commit, root, core, ui, send, changelogOk, now(), run(file,args,opt), readFile(p), out(line),
 *      expected: {core, ui} (the recipes' commands), blockers }
 * -> { go, gates: [{id, ok, why}], changelog, hash, version, tag }
 */
async function gate(o) {
  const out = o.out || ((l) => process.stdout.write(l + '\n'));
  const run = o.run || L.realRun;
  const root = o.root || path.join(__dirname, '..');
  const readFile = o.readFile || ((p) => fs.readFileSync(p, 'utf8'));
  const now = o.now || Date.now;
  const git = (args) => run('git', args, { cwd: root });
  const gh = (args) => run('gh', args, { cwd: root });
  if (o.send) {
    const ref = L.autonomyRefusal('mantle-release', { dir: o.autonomyDir, now: now() });
    if (ref) {
      out(`refused: autonomous mode is ${ref.state}${ref.why === 'held' ? ' and holds mantle-release' : ''}, so --send does nothing (no check, no tag, no publish)`);
      for (const t of [`tag for ${o.commit} on origin`, `release for ${o.commit} published`]) L.autonomy('mantle-release', t, 'refused', out, ref.why);
      out(`RELEASE GATE: NO-GO (refused: ${ref.why})`);
      return { go: false, refused: ref.why, gates: [], changelog: '', hash: null, version: null, tag: null };
    }
  }
  const gates = [];
  const pass = (id, why) => { gates.push({ id, ok: true, why: why || null }); out(`ok    ${id}${why ? `: ${why}` : ''}`); };
  const fail = (id, why) => { gates.push({ id, ok: false, why }); out(`FAIL  ${id}: ${why}`); };

  // commit
  git(['fetch', '--quiet', 'origin', '--tags']);
  const rc = git(['rev-parse', '--verify', '--quiet', `${o.commit}^{commit}`]);
  const sha = rc.code === 0 ? rc.out.trim() : null;
  if (!sha) fail('commit', `"${o.commit}" is not a commit here`);
  else if (git(['merge-base', '--is-ancestor', sha, 'origin/master']).code !== 0) fail('commit', `${sha.slice(0, 8)} is not on origin/master`);
  else pass('commit', `${sha.slice(0, 8)} is on origin/master`);

  // version
  let ver = null;
  if (sha) {
    const pj = git(['show', `${sha}:package.json`]);
    try { ver = L.parseVersion(JSON.parse(pj.out).version); } catch (_) { ver = null; }
  }
  if (ver) pass('version', L.vstr(ver)); else fail('version', 'package.json at the commit has no plain x.y.z version');
  const tag = ver ? `v${L.vstr(ver)}` : null;

  // patch + 48 h (the published list)
  let list = null;
  const rl = gh(['api', `repos/${L.REPO}/releases?per_page=100`]);
  try { list = rl.code === 0 ? JSON.parse(rl.out) : null; } catch (_) { list = null; }
  const latest = list ? L.latestPublished(list) : null;
  // the version to count from: the highest that exists anywhere (a published release, or a tag: a release drafted
  // back by a rollback keeps its tag, so its number is used up and the rollback's patch comes after it)
  let highest = latest ? latest.v : null;
  const tl = git(['tag', '--list', 'v*']);
  for (const t of (tl.code === 0 ? tl.out : '').split(/\r?\n/)) { const v = L.parseVersion(t.trim()); if (v && (!highest || L.cmpVersion(v, highest) > 0)) highest = v; }
  if (!list) fail('patch', `GitHub's release list could not be read (${(rl.err || rl.out || '').trim().slice(0, 160) || `gh exit ${rl.code}`})`);
  else if (!latest) fail('patch', 'there is no published release to patch');
  else {
    const why = L.patchWhy(highest, ver);
    if (why) fail('patch', why);
    else if (git(['merge-base', '--is-ancestor', latest.r.tag_name, sha]).code !== 0) fail('patch', `${latest.r.tag_name} is not an ancestor of the commit: clones could not fast-forward to it`);
    else pass('patch', `${tag} after v${L.vstr(highest)}${L.cmpVersion(highest, latest.v) ? ` (the highest tag; the latest published is ${latest.r.tag_name})` : ''}`);
  }
  if (!latest) fail('48 h', 'no published release to measure from');
  else if (o.rollbackOf) {
    const rb = sha ? rollbackWhy(o.rollbackOf, list, latest, sha, git) : { why: 'no commit' };
    if (rb.why) fail('48 h', rb.why);
    else pass('48 h', `skipped: the rollback of ${o.rollbackOf} (back to draft); the tree is ${rb.good}'s apart from the version`);
  } else {
    const c = clockStart(list, latest, git);
    const ageH = (now() - c.at) / 3600000;
    if (!(ageH >= 48)) fail('48 h', `${c.what} ${Number.isFinite(ageH) ? `${ageH.toFixed(1)} h` : 'at an unreadable time'} ago (at most one release per 48 h; a rollback passes --rollback-of=<bad tag>)`);
    else pass('48 h', `${c.what} ${ageH.toFixed(1)} h ago`);
  }

  // new tag
  if (tag) {
    const local = git(['rev-parse', '--verify', '--quiet', `refs/tags/${tag}`]).code === 0;
    const rr = git(['ls-remote', '--tags', 'origin', `refs/tags/${tag}`]);
    if (rr.code !== 0) fail('new tag', `origin's tags could not be read (git exit ${rr.code})`);
    else if (local || rr.out.trim()) fail('new tag', `${tag} already exists ${[local ? 'here' : '', rr.out.trim() ? 'on origin' : ''].filter(Boolean).join(' and ')}: a tag is never moved or reused`);
    else pass('new tag', `${tag} is unused`);
  } else fail('new tag', 'no version, so no tag');

  // build: the draft installer.yml made from this exact commit
  const draft = list && tag ? list.find((r) => r && r.tag_name === tag && r.draft) : null;
  if (!draft) fail('build', `no DRAFT release ${tag || '(no tag)'}: push the commit to master so installer.yml builds it`);
  else if (draft.target_commitish !== sha) fail('build', `the draft ${tag} was built from ${String(draft.target_commitish).slice(0, 8)}, not ${String(sha).slice(0, 8)}`);
  else if (!(draft.assets || []).some((a) => a && a.name === L.ASSET && a.state === 'uploaded')) fail('build', `the draft ${tag} holds no uploaded ${L.ASSET}`);
  else pass('build', `draft ${tag} built from ${sha.slice(0, 8)} holds ${L.ASSET}`);

  // blockers
  const open = (o.blockers || BLOCKERS).filter((b) => !b.done);
  if (open.length) fail('blockers', `still open: ${open.map((b) => `${b.id} (${b.what})`).join('; ')}`);
  else pass('blockers');

  // changelog (for the Engineering Manager's review)
  let changelog = '';
  if (sha && latest) {
    const lg = git(['log', '--no-merges', '--format=- %s (%h)', `${latest.r.tag_name}..${sha}`]);
    changelog = lg.code === 0 ? lg.out.trim() : '';
  }
  const hash = L.sha12(changelog);
  out(`CHANGELOG ${latest ? latest.r.tag_name : '?'}..${sha ? sha.slice(0, 8) : '?'} (hash ${hash}; review it, then pass --changelog-ok=${hash}):`);
  out(changelog || '(empty)');
  out('END CHANGELOG');

  // the cloudtest commands
  const expected = o.expected || { core: recipeCommand(RECIPES.core), ui: recipeCommand(RECIPES.ui) };
  const wt = path.join(path.dirname(root), `gate-${sha ? sha.slice(0, 8) : 'x'}`);
  out('CLOUDTEST (each pushes a throwaway cloudtest/* branch, so each needs the owner\'s OK first):');
  out(`  git -C "${root}" worktree add --detach "${wt}" ${sha || o.commit}`);
  out(`  cd "${wt}" && cloudtest --recipe ${RECIPES.core}`);
  out(`  cd "${wt}" && cloudtest --recipe ${RECIPES.ui}`);
  out(`  cd "${wt}" && cloudtest --committed --os windows --timeout 50 --artifacts "upgrade-out/**" -- "${UPGRADE_CMD}"`);
  out(`  then: node tools/release-gate.js ${sha || o.commit} --core="${wt}/.cloudtest/<id>" --ui="${wt}/.cloudtest/<id>" --upgrade="${wt}/.cloudtest/<id>"${o.rollbackOf ? ` --rollback-of=${o.rollbackOf}` : ''}`);

  // suite
  for (const k of ['core', 'ui']) {
    const id = `suite ${k}`;
    const rd = runDirWhy(o[k], k, sha, expected[k], readFile, git);
    if (rd.why) { fail(id, rd.why); continue; }
    const j = L.judgeLog(rd.log);
    if (!j.ok) { fail(id, `${j.why}${j.total ? ` (${j.total})` : ''}`); continue; }
    pass(id, `${j.total} (run.json's conclusion and exit code are not used)`);
  }

  // upgrade: the latest published release, then this commit's build over it, live
  {
    const rd = runDirWhy(o.upgrade, 'upgrade', sha, o.expectedUpgrade || UPGRADE_CMD, readFile, git);
    if (rd.why) fail('upgrade', rd.why);
    else if (!latest || !ver) fail('upgrade', 'no latest published release or no version to check the run against');
    else {
      const u = judgeUpgrade(rd.log, latest.r.tag_name, L.vstr(ver));
      if (!u.ok) fail('upgrade', `${u.why}${u.line ? ` (${u.line})` : ''}`);
      else pass('upgrade', u.line);
    }
  }

  // review
  if (!o.changelogOk) fail('review', `no --changelog-ok (the changelog's hash is ${hash})`);
  else if (o.changelogOk !== hash) fail('review', `--changelog-ok=${o.changelogOk} is not this changelog's hash ${hash}: the text changed since it was reviewed`);
  else pass('review', `changelog ${hash} reviewed`);

  const go = gates.every((g) => g.ok);
  // the outward steps
  const steps = [
    { target: `tag ${tag || '?'} on origin`, file: 'git', args: ['push', 'origin', `refs/tags/${tag}`], before: ['tag', '-a', tag, '-m', `Mantle ${tag}`, sha] },
    { target: `release ${tag || '?'} published`, file: 'gh', args: ['release', 'edit', tag, '--repo', L.REPO, '--draft=false', '--latest', '--verify-tag'] },
  ];
  let ok = go;
  for (const s of steps) {
    if (!ok) { L.autonomy('mantle-release', s.target, 'refused', out); continue; }
    if (!o.send) { L.autonomy('mantle-release', s.target, 'would', out); continue; }
    if (s.before && git(s.before).code !== 0) { ok = false; L.autonomy('mantle-release', s.target, 'failed', out); continue; }
    const r = run(s.file, s.args, { cwd: root });
    if (r.code !== 0) { ok = false; out(`  ${s.file} said: ${(r.err || r.out || '').trim().slice(0, 300)}`); }
    L.autonomy('mantle-release', s.target, r.code === 0 ? 'done' : 'failed', out);
  }
  if (go && o.send && ok) {
    const lt = gh(['api', `repos/${L.REPO}/releases/latest`]);
    let lname = null; try { lname = JSON.parse(lt.out).tag_name; } catch (_) { lname = null; }
    out(lname === tag ? `check: releases/latest is ${tag}` : `check FAILED: releases/latest is ${lname || 'unreadable'}, not ${tag}`);
  }
  out(`RELEASE GATE: ${go ? 'GO' : 'NO-GO'} (${gates.filter((g) => g.ok).length} of ${gates.length} gates passed)`);
  return { go, gates, changelog, hash, version: ver ? L.vstr(ver) : null, tag };
}

/** 0 GO, 3 refused by autonomous mode, 1 otherwise. */
function exitCode(r) { return r && r.refused ? L.REFUSED_EXIT : r && r.go ? 0 : 1; }

function argsOf(argv) {
  const o = { commit: null, send: false };
  for (const a of argv) {
    if (a === '--send') o.send = true;
    else if (a.startsWith('--core=')) o.core = a.slice(7);
    else if (a.startsWith('--ui=')) o.ui = a.slice(5);
    else if (a.startsWith('--upgrade=')) o.upgrade = a.slice(10);
    else if (a.startsWith('--rollback-of=')) o.rollbackOf = a.slice(14);
    else if (a.startsWith('--changelog-ok=')) o.changelogOk = a.slice(15);
    else if (!a.startsWith('--') && !o.commit) o.commit = a;
    else throw new Error(`unknown argument ${a}`);
  }
  if (!o.commit) throw new Error('usage: node tools/release-gate.js <commit> [--core=<dir>] [--ui=<dir>] [--upgrade=<dir>] [--rollback-of=<bad tag>] [--changelog-ok=<hash>] [--send]');
  return o;
}

if (require.main === module) {
  let o;
  try { o = argsOf(process.argv.slice(2)); } catch (err) { console.log(`RELEASE GATE: NO-GO (${err.message})`); process.exit(2); }
  gate(o).then((r) => process.exit(exitCode(r)), (err) => { console.log(`RELEASE GATE: NO-GO (it threw: ${err.message})`); process.exit(1); });
}

module.exports = { gate, argsOf, exitCode, BLOCKERS, RECIPES, UPGRADE_CMD, VERSION_FILES, recipeCommand, rollbackWhy, judgeUpgrade, withoutVersion };
