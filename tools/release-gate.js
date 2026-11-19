'use strict';
/*
 * tools/release-gate.js -- the gate in front of a Mantle PATCH release (lane release-gate, 2026-11-18).
 *
 *   node tools/release-gate.js <commit> [--core=<cloudtest run dir>] [--ui=<cloudtest run dir>]
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
 *   48 h        the latest published release is at least 48 h old
 *   build       a DRAFT release v<version> exists whose target is this exact commit and which holds
 *               Mantle-Setup.exe (installer.yml builds and install-tests it from that commit)
 *   blockers    every item in BLOCKERS below is done (rollback runbook: release blocked until they exist)
 *   suite       both cloudtest runs (mantle-core-public, mantle-ui-public) are green, judged ONLY from the
 *               TOTAL line inside the job log, never run.json's conclusion or exit code; run.json's base is
 *               the commit, its mode is "committed", the snapshot commit is here and differs from the commit
 *               only in cloudtest's own files, and the job's command is exactly the recipe's (no "; echo")
 *   review      --changelog-ok equals the changelog's hash (printed below), so the reviewed text is the one shipped
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
  { id: 'updater-e2e', done: false, what: 'lib/updater.e2e.test.js green in cloudtest (fix in 1ae43a7b, not proven yet)' },
  { id: 'oob-restore', done: false, what: 'an out-of-band restore that works with Mantle not running' },
]);
const RECIPES = { core: 'mantle-core-public', ui: 'mantle-ui-public' };

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
  if (latest) {
    const ageH = (now() - Date.parse(latest.r.published_at)) / 3600000;
    if (!(ageH >= 48)) fail('48 h', `${latest.r.tag_name} was published ${Number.isFinite(ageH) ? `${ageH.toFixed(1)} h` : 'at an unreadable time'} ago (at most one release per 48 h)`);
    else pass('48 h', `${latest.r.tag_name} is ${ageH.toFixed(1)} h old`);
  } else fail('48 h', 'no published release to measure from');

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
  out(`  then: node tools/release-gate.js ${sha || o.commit} --core="${wt}/.cloudtest/<id>" --ui="${wt}/.cloudtest/<id>"`);

  // suite
  for (const k of ['core', 'ui']) {
    const id = `suite ${k}`;
    const dir = o[k];
    if (!dir) { fail(id, `no --${k}=<cloudtest run dir> given`); continue; }
    let rj = null, log = null;
    try { rj = JSON.parse(readFile(path.join(dir, 'run.json'))); } catch (err) { fail(id, `run.json could not be read (${err.message})`); continue; }
    try { log = readFile(path.join(dir, 'log.txt')); } catch (err) { fail(id, `log.txt could not be read (${err.message})`); continue; }
    if (rj.base !== sha) { fail(id, `the run tested ${String(rj.base).slice(0, 8)}, not ${String(sha).slice(0, 8)}`); continue; }
    if (rj.mode !== 'committed') { fail(id, `the run's mode is "${rj.mode}", not "committed" (a working-tree snapshot is not the commit)`); continue; }
    const snap = typeof rj.snapshot === 'string' ? rj.snapshot : '';
    if (git(['cat-file', '-e', `${snap}^{commit}`]).code !== 0) { fail(id, `the snapshot commit ${snap.slice(0, 8) || '(none)'} is not here, so its tree cannot be compared`); continue; }
    const dn = git(['diff', '--name-only', sha, snap]);
    if (dn.code !== 0) { fail(id, `git could not compare the snapshot with the commit (exit ${dn.code})`); continue; }
    const tw = L.harnessOnlyWhy(dn.out.split(/\r?\n/).filter(Boolean));
    if (tw) { fail(id, tw); continue; }
    const cmd = git(['show', `${snap}:.cloudtest-job/command.sh`]);
    const cw = L.commandWhy(cmd.code === 0 ? cmd.out : '', expected[k]);
    if (cw) { fail(id, cw); continue; }
    const j = L.judgeLog(log);
    if (!j.ok) { fail(id, `${j.why}${j.total ? ` (${j.total})` : ''}`); continue; }
    pass(id, `${j.total} (run.json's conclusion and exit code are not used)`);
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

function argsOf(argv) {
  const o = { commit: null, send: false };
  for (const a of argv) {
    if (a === '--send') o.send = true;
    else if (a.startsWith('--core=')) o.core = a.slice(7);
    else if (a.startsWith('--ui=')) o.ui = a.slice(5);
    else if (a.startsWith('--changelog-ok=')) o.changelogOk = a.slice(15);
    else if (!a.startsWith('--') && !o.commit) o.commit = a;
    else throw new Error(`unknown argument ${a}`);
  }
  if (!o.commit) throw new Error('usage: node tools/release-gate.js <commit> [--core=<dir>] [--ui=<dir>] [--changelog-ok=<hash>] [--send]');
  return o;
}

if (require.main === module) {
  let o;
  try { o = argsOf(process.argv.slice(2)); } catch (err) { console.log(`RELEASE GATE: NO-GO (${err.message})`); process.exit(2); }
  gate(o).then((r) => process.exit(r.go ? 0 : 1), (err) => { console.log(`RELEASE GATE: NO-GO (it threw: ${err.message})`); process.exit(1); });
}

module.exports = { gate, argsOf, BLOCKERS, RECIPES, recipeCommand };
