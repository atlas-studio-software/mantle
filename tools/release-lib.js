'use strict';
/*
 * tools/release-lib.js -- what tools/release-gate.js and tools/release-rollback.js share (lane release-gate,
 * 2026-11-18). Pure functions plus thin runners; every runner is handed in, so the tests (tools/*.test.js) give
 * fakes and never reach git's remote, GitHub or cloudtest.
 *
 * THE AUTONOMY LINE. Every outward step (and every step that WOULD be outward) prints exactly one stdout line:
 *   AUTONOMY: {"class":"mantle-release"|"mantle-rollback","target":"<what>","result":"done|failed|refused|would"}
 * A caller reads `result`, never the exit code.
 *
 * Rules these tools keep (docs: the release rollback runbook):
 *   - a release is a PATCH: same major.minor as the latest published release, patch + 1;
 *   - a tag is never moved or reused: a tag that exists locally or on origin refuses;
 *   - a rollback is (a) the bad release back to DRAFT, tag kept, then (b) the last good tree shipped as the NEXT
 *     patch, a commit on top of master (clones update by fast-forward only, lib/updater.js).
 */
const crypto = require('crypto');
const { spawnSync } = require('child_process');

const REPO = 'atlas-studio-software/mantle';
const ASSET = 'Mantle-Setup.exe';
/** Paths cloudtest adds to (or drops from) a snapshot; anything else differing means it did not test the commit. */
const HARNESS = [/^\.cloudtest-job\//, /^\.cloudtest\//, /^\.github\/workflows\//];

/** "v1.2.3" / "1.2.3" -> { major, minor, patch } or null. Pre-release or build suffixes are refused (not a release). */
function parseVersion(s) {
  const m = /^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$/.exec(String(s == null ? '' : s).trim());
  return m ? { major: Number(m[1]), minor: Number(m[2]), patch: Number(m[3]) } : null;
}
const vstr = (v) => `${v.major}.${v.minor}.${v.patch}`;
function cmpVersion(a, b) {
  for (const k of ['major', 'minor', 'patch']) if (a[k] !== b[k]) return a[k] < b[k] ? -1 : 1;
  return 0;
}
/** null when `next` is exactly the patch after `prev`, else why not. */
function patchWhy(prev, next) {
  if (!prev) return 'the latest published release has no version tag like v1.2.3';
  if (!next) return 'package.json has no plain version like 1.2.3';
  if (next.major !== prev.major || next.minor !== prev.minor) return `${vstr(next)} is not a patch of ${vstr(prev)} (major or minor changed)`;
  if (next.patch !== prev.patch + 1) return `${vstr(next)} is not the next patch after ${vstr(prev)} (want ${prev.major}.${prev.minor}.${prev.patch + 1})`;
  return null;
}
/** The newest published (not draft, not pre-release) release with a version tag, from GitHub's list. */
function latestPublished(list) {
  let best = null;
  for (const r of Array.isArray(list) ? list : []) {
    if (!r || r.draft || r.prerelease) continue;
    const v = parseVersion(r.tag_name);
    if (v && (!best || cmpVersion(v, best.v) > 0)) best = { v, r };
  }
  return best;
}
/** The published release just below `tag` (the last good one for a rollback), or null. */
function previousPublished(list, tag) {
  const t = parseVersion(tag);
  let best = null;
  for (const r of Array.isArray(list) ? list : []) {
    if (!r || r.draft || r.prerelease) continue;
    const v = parseVersion(r.tag_name);
    if (v && t && cmpVersion(v, t) < 0 && (!best || cmpVersion(v, best.v) > 0)) best = { v, r };
  }
  return best;
}

/** The one stdout line for an outward step. */
function autonomy(cls, target, result, out) {
  const line = `AUTONOMY: ${JSON.stringify({ class: cls, target, result })}`;
  (out || ((l) => process.stdout.write(l + '\n')))(line);
  return line;
}

/**
 * Judge one cloudtest run from its JOB LOG only (never run.json's conclusion or exitCode): exactly one
 * `TOTAL:` line between "@@cloudtest-begin@@" and "@@cloudtest-exit=" with 0 FAIL, 0 could-not-start and
 * 0 timeout. -> { ok, why, total, counts }
 */
/**
 * git's arguments for "which paths differ between two commits": --no-renames, so a file moved (say into .cloudtest/)
 * lists BOTH its old and its new path (porcelain diff detects renames by default and would list only the new one),
 * and core.quotePath=false, so an odd file name comes out as itself, not as a quoted octal string.
 */
const nameDiffArgs = (a, b) => ['-c', 'core.quotePath=false', 'diff', '--no-renames', '--name-only', a, b];

/**
 * cloudtest's markers in a job log: exactly ONE line with "@@cloudtest-begin@@" and exactly ONE
 * "@@cloudtest-exit=<n>@@" in the whole log, the exit after the begin. -> { b, e } (line indexes) or { why }
 */
const EXIT_RE = /@@cloudtest-exit=\d+@@/;
function markers(lines) {
  const begins = [], exits = [];
  lines.forEach((l, i) => { if (l.includes('@@cloudtest-begin@@')) begins.push(i); if (EXIT_RE.test(l)) exits.push(i); });
  if (begins.length !== 1) return { why: begins.length ? `the job log has ${begins.length} "@@cloudtest-begin@@" markers, not exactly one` : 'the job log has no "@@cloudtest-begin@@" marker, so the suite\'s own output cannot be found' };
  if (exits.length !== 1) return { why: exits.length ? `the job log has ${exits.length} "@@cloudtest-exit=N@@" markers, not exactly one` : 'the job log has no "@@cloudtest-exit=N@@" marker: the suite did not finish inside the job' };
  if (exits[0] < begins[0]) return { why: 'the job log\'s "@@cloudtest-exit=N@@" marker comes before its "@@cloudtest-begin@@"' };
  return { b: begins[0], e: exits[0] };
}

const TOTAL_RE = /TOTAL: (\d+) files: (\d+) pass, (\d+) FAIL, (\d+) known-env, (\d+) could-not-start, (\d+) timeout(?:, (\d+) skipped)? \| checks (\d+) passed, (\d+) failed/;
function judgeLog(text) {
  const lines = String(text || '').split(/\r?\n/);
  const mk = markers(lines);
  if (mk.why) return { ok: false, why: mk.why };
  const { b, e } = mk;
  const totals = lines.slice(b + 1, e).filter((l) => /\bTOTAL: /.test(l));
  if (totals.length !== 1) return { ok: false, why: `the suite's output has ${totals.length} TOTAL lines, not exactly one` };
  const m = TOTAL_RE.exec(totals[0]);
  if (!m) return { ok: false, why: `the TOTAL line is not in run-suite's format: ${totals[0].trim().slice(0, 200)}` };
  const n = m.slice(1).map((x) => (x === undefined ? 0 : Number(x)));
  const counts = { files: n[0], pass: n[1], fail: n[2], knownEnv: n[3], couldNotStart: n[4], timeout: n[5], skipped: n[6], checksPassed: n[7], checksFailed: n[8] };
  const total = totals[0].replace(/^.*?(TOTAL: )/, '$1').trim();
  const bad = [];
  if (counts.fail) bad.push(`${counts.fail} FAIL`);
  if (counts.couldNotStart) bad.push(`${counts.couldNotStart} could-not-start`);
  if (counts.timeout) bad.push(`${counts.timeout} timeout`);
  if (counts.pass + counts.fail + counts.knownEnv + counts.couldNotStart + counts.timeout + counts.skipped !== counts.files) bad.push('the file counts do not add up');
  return bad.length ? { ok: false, why: `not green: ${bad.join(', ')}`, total, counts } : { ok: true, why: null, total, counts };
}

/** null when every path differing between the commit and the snapshot is cloudtest's own, else why not. */
function harnessOnlyWhy(changed) {
  const other = (changed || []).filter((p) => p && !HARNESS.some((re) => re.test(p)));
  return other.length ? `the snapshot differs from the commit outside cloudtest's own files: ${other.slice(0, 5).join(', ')}${other.length > 5 ? ` and ${other.length - 5} more` : ''}` : null;
}
/** null when the snapshot's command is exactly the expected suite command (no `; echo`, no `|| true`), else why. */
function commandWhy(got, want) {
  const g = String(got || '').trim();
  if (!want) return 'no expected command to compare with';
  if (g !== String(want).trim()) return `the job ran "${g.slice(0, 160)}", not the expected "${String(want).slice(0, 160)}"`;
  if (/[;&|`$]|\n/.test(g)) return 'the job command chains or substitutes shell commands, so its exit and output cannot be trusted';
  return null;
}

const sha12 = (s) => crypto.createHash('sha256').update(String(s), 'utf8').digest('hex').slice(0, 12);

/** A runner: (file, args, opt) -> { code, out, err }. Never throws; code null = it did not start. */
function realRun(file, args, opt) {
  const r = spawnSync(file, args, Object.assign({ encoding: 'utf8', windowsHide: true, timeout: 120000, maxBuffer: 64 * 1024 * 1024 }, opt || {}));
  if (r.error) return { code: null, out: '', err: r.error.message };
  return { code: r.status, out: r.stdout || '', err: r.stderr || '' };
}

module.exports = { REPO, ASSET, HARNESS, parseVersion, vstr, cmpVersion, patchWhy, latestPublished, previousPublished, autonomy, markers, nameDiffArgs, judgeLog, harnessOnlyWhy, commandWhy, sha12, realRun, TOTAL_RE };
