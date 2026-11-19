'use strict';
/*
 * tools/release-autonomy.test.js -- release-lib's autonomous-mode reader against the contract's shared vectors
 * (contracts/autonomy.md section 1; tools/autonomy-mode-vectors.json is a byte-for-byte copy of
 * contracts/autonomy-mode-vectors.json). Each case is written into its own temp folder (modeJson / policyJson
 * null = the file is absent, stop = an empty STOP file) and evaluated at the file's "now". Pure node: temp
 * folders only, never the real autonomy folder.
 *
 *   node tools/release-autonomy.test.js   -> "RELEASE AUTONOMY TEST: n passed, m failed"
 */
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const L = require('./release-lib');

let passed = 0; const failures = [];
function check(name, ok, detail) { if (ok) passed++; else failures.push(`${name}${detail !== undefined ? ` (${JSON.stringify(detail).slice(0, 300)})` : ''}`); }

const FILE = path.join(__dirname, 'autonomy-mode-vectors.json');
const bytes = fs.readFileSync(FILE);
const V = JSON.parse(bytes.toString('utf8'));
const NOW = Date.parse(V.now);
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-autonomy-vectors-'));
console.log(`vectors: ${path.basename(FILE)} sha256 ${crypto.createHash('sha256').update(bytes).digest('hex')} (CR-stripped ${crypto.createHash('sha256').update(bytes.toString('utf8').split(String.fromCharCode(13)).join('')).digest('hex')})`);

try {
  check('the vectors file has 21 cases', Array.isArray(V.cases) && V.cases.length === 21, V.cases && V.cases.length);
  V.cases.forEach((c, i) => {
    const d = path.join(TMP, `case-${String(i).padStart(2, '0')}`);
    fs.mkdirSync(d);
    if (c.modeJson !== null) fs.writeFileSync(path.join(d, 'mode.json'), c.modeJson, 'utf8');
    if (c.policyJson !== null) fs.writeFileSync(path.join(d, 'policy.json'), c.policyJson, 'utf8');
    if (c.stop) fs.writeFileSync(path.join(d, 'STOP'), '');
    const got = L.modeState({ dir: d, now: NOW });
    check(`vector ${i + 1} "${c.name}": ${c.expect}`, got === c.expect, { got, expect: c.expect });
  });

  // the refusal on top of the states (the release and rollback classes)
  const write = (name, mode) => {
    const d = path.join(TMP, name); fs.mkdirSync(d);
    const pol = '{"v":1}\n';
    fs.writeFileSync(path.join(d, 'policy.json'), pol);
    fs.writeFileSync(path.join(d, 'mode.json'), JSON.stringify(Object.assign({ v: 1, on: true, length: { kind: 'open' }, policySha: crypto.createHash('sha256').update(pol).digest('hex'), paused: null }, mode)));
    return d;
  };
  const ref = (d, cls) => L.autonomyRefusal(cls, { dir: d, now: NOW });
  const paused = write('paused', { paused: 'mail' });
  check('refusal: PAUSED refuses both classes, why "paused"', (ref(paused, 'mantle-release') || {}).why === 'paused' && (ref(paused, 'mantle-rollback') || {}).why === 'paused');
  const held = write('held', { holds: ['mantle-release'] });
  check('refusal: ON holding mantle-release refuses it ("held") and not mantle-rollback', (ref(held, 'mantle-release') || {}).why === 'held' && ref(held, 'mantle-rollback') === null);
  check('refusal: ON with holds not an array = no holds', ref(write('str', { holds: 'mantle-release' }), 'mantle-release') === null);
  check('refusal: ON with no holds = none', ref(write('none', {}), 'mantle-release') === null);
  check('refusal: a platform word does not hold mantle-* ("x", "site")', ref(write('words', { holds: ['x', 'site', 'discord'] }), 'mantle-release') === null);
  check('refusal: OFF (empty folder) = none', ref(fs.mkdtempSync(path.join(TMP, 'empty-')), 'mantle-release') === null);
  check('refusal: OFF (a folder that does not exist) = none', ref(path.join(TMP, 'no-such-folder'), 'mantle-release') === null);

  // the folder: from the home folder, never a hard-coded user; the env override is for tests
  const saved = process.env.MANTLE_TEST_AUTONOMY_DIR;
  delete process.env.MANTLE_TEST_AUTONOMY_DIR;
  check('folder: <home>/helm-team/atlas-studio/autonomy', L.autonomyDir() === path.join(os.homedir(), 'helm-team', 'atlas-studio', 'autonomy'), L.autonomyDir());
  process.env.MANTLE_TEST_AUTONOMY_DIR = paused;
  check('folder: MANTLE_TEST_AUTONOMY_DIR replaces it (and modeState reads it by default)', L.autonomyDir() === paused && L.modeState({ now: NOW }) === 'PAUSED');
  if (saved === undefined) delete process.env.MANTLE_TEST_AUTONOMY_DIR; else process.env.MANTLE_TEST_AUTONOMY_DIR = saved;
  const src = ['release-lib.js', 'release-gate.js', 'release-rollback.js'].map((f) => fs.readFileSync(path.join(__dirname, f), 'utf8')).join('\n');
  let user = ''; try { user = os.userInfo().username.toLowerCase(); } catch (_) { user = ''; }
  check('folder: no user path and not this machine\'s user name in the tools', !/[A-Za-z]:[\\/]+Users[\\/]/i.test(src) && !/\/home\/[a-z]/i.test(src) && !(user.length > 2 && src.toLowerCase().includes(user)));
} finally {
  try { fs.rmSync(TMP, { recursive: true, force: true }); } catch (_) { /* a temp folder */ }
}
for (const f of failures) console.log('FAIL ' + f);
console.log(`RELEASE AUTONOMY TEST: ${passed} passed, ${failures.length} failed`);
process.exit(failures.length ? 1 : 0);
