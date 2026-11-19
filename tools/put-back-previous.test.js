'use strict';
/*
 * tools/put-back-previous.test.js -- tools/put-back-previous.ps1 OFFLINE (lane release-gate, 2026-11-18): a fake
 * install folder and a release list in a temp folder (-InstallDir, -ReleasesJson), a fake installer file
 * (-SetupFile). Only the modes that install nothing run here: the dry run, -Ask answered "n", -VerifyOnly and the
 * refusals. -Run is NEVER run here (it starts an installer); nothing reaches the network, no process is stopped.
 *
 *   node tools/put-back-previous.test.js   -> "PUT BACK TEST: n passed, m failed"
 */
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const SCRIPT = path.join(__dirname, 'put-back-previous.ps1');
const CMD = path.join(__dirname, 'put-back-previous.cmd');
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-put-back-test-'));
let passed = 0; const failures = [];
function check(name, ok, detail) { if (ok) passed++; else failures.push(`${name}${detail !== undefined ? ` (${String(typeof detail === 'string' ? detail : JSON.stringify(detail)).slice(0, 600)})` : ''}`); }

const SETUP = path.join(DIR, 'Mantle-Setup.exe');
fs.writeFileSync(SETUP, crypto.randomBytes(4096));
const SHA = crypto.createHash('sha256').update(fs.readFileSync(SETUP)).digest('hex');
const asset = (tag, digest) => ({ name: 'Mantle-Setup.exe', size: 4096, digest, browser_download_url: `https://example.invalid/${tag}/Mantle-Setup.exe` });
function install(name, version) {
  const d = path.join(DIR, name);
  fs.mkdirSync(path.join(d, 'resources', 'app'), { recursive: true });
  if (version) fs.writeFileSync(path.join(d, 'resources', 'app', 'package.json'), JSON.stringify({ name: 'mantle', version }));
  return d;
}
function releases(name, list) { const p = path.join(DIR, `${name}.json`); fs.writeFileSync(p, JSON.stringify(list)); return p; }
function ps(args, input) {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', SCRIPT].concat(args),
    { encoding: 'utf8', windowsHide: true, timeout: 60000, input: input == null ? '' : input });
  const out = `${r.stdout || ''}${r.stderr || ''}`;
  const lines = out.split(/\r?\n/).filter(Boolean);
  return { code: r.status, out, last: lines.filter((l) => l.startsWith('PUT BACK: ')).pop() || '', lines };
}
const untouched = (r) => !/installing |uninstalling |downloading /.test(r.out);

const REL = [
  { tag_name: 'v0.1.3', draft: false, prerelease: false, assets: [asset('v0.1.3', `sha256:${'1'.repeat(64)}`)] },
  { tag_name: 'v0.1.2', draft: true, prerelease: false, assets: [asset('v0.1.2', `sha256:${'2'.repeat(64)}`)] },
  { tag_name: 'v0.1.1', draft: false, prerelease: false, assets: [asset('v0.1.1', `sha256:${SHA}`)] },
  { tag_name: 'v0.1.0', draft: false, prerelease: false, assets: [] },
  { tag_name: 'v0.0.9-beta.1', draft: false, prerelease: true, assets: [] },
];
const rel = releases('rel', REL);

try {
  const parse = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    `$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('${SCRIPT.replace(/'/g, "''")}',[ref]$t,[ref]$e);$e.Count`], { encoding: 'utf8', windowsHide: true });
  check('the script parses (Windows PowerShell 5.1)', String(parse.stdout).trim() === '0', parse.stdout + parse.stderr);

  {
    const inst = install('bad012', '0.1.2');
    const r = ps(['-InstallDir', inst, '-ReleasesJson', rel]);
    check('dry run: on the drafted-back 0.1.2 the target is v0.1.1 (the newest PUBLISHED below it), exit 0', r.code === 0 && /^PUT BACK: would \(dry run: nothing downloaded or changed/.test(r.last) && r.lines.some((l) => /^target: Mantle v0\.1\.1, .*sha256 [0-9a-f]{64}\)$/.test(l)), r.out);
    check('dry run: it names the newer published v0.1.3 as a possible fix', r.lines.some((l) => /^note: v0\.1\.3 is published and newer than the installed 0\.1\.2/.test(l)), r.lines);
    check('dry run: nothing downloaded, uninstalled or installed', untouched(r), r.out);
    check('dry run: the install folder is unchanged', fs.readdirSync(inst).join() === 'resources' && fs.readdirSync(path.join(inst, 'resources', 'app')).join() === 'package.json');
  }
  {
    const r = ps(['-InstallDir', install('ask', '0.1.2'), '-ReleasesJson', rel, '-Ask'], 'n\n\n');
    check('-Ask answered "n": stops, nothing done, exit 0', r.code === 0 && /^PUT BACK: would \(stopped: nothing downloaded or changed\)$/.test(r.last) && untouched(r) && /Type Y and press Enter/.test(r.out), r.out);
  }
  {
    const r = ps(['-InstallDir', install('v013', '0.1.3'), '-ReleasesJson', rel, '-Tag', 'v0.1.1', '-VerifyOnly', '-SetupFile', SETUP]);
    check('-VerifyOnly with the right file: verified, nothing installed, exit 0', r.code === 0 && /^PUT BACK: verified \(.*is v0\.1\.1's Mantle-Setup\.exe; nothing installed\)$/.test(r.last) && untouched(r), r.out);
  }
  {
    const other = path.join(DIR, 'Other-Setup.exe');
    fs.writeFileSync(other, 'not the installer');
    const r = ps(['-InstallDir', install('v013b', '0.1.3'), '-ReleasesJson', rel, '-Tag', 'v0.1.1', '-VerifyOnly', '-SetupFile', other]);
    check('-VerifyOnly with a file whose sha256 is not the release\'s: refused, exit 1, nothing ran', r.code === 1 && /^PUT BACK: refused \(the installer's sha256 [0-9a-f]{64} is not v0\.1\.1's /.test(r.last) && untouched(r), r.out);
    check('a -SetupFile that fails the check is left where it was (only its own downloads are deleted)', fs.existsSync(other));
  }
  {
    const r = ps(['-InstallDir', install('v011', '0.1.1'), '-ReleasesJson', rel]);
    check('installed v0.1.1: the only older release (v0.1.0) has no Mantle-Setup.exe = refused, exit 2', r.code === 2 && /^PUT BACK: refused \(v0\.1\.0 has no Mantle-Setup\.exe\)$/.test(r.last), r.out);
  }
  {
    const r = ps(['-InstallDir', install('v010', '0.1.0'), '-ReleasesJson', rel]);
    check('installed v0.1.0: nothing published below it (the beta is a pre-release) = refused', r.code === 2 && /no published release is older than the installed 0\.1\.0/.test(r.last), r.out);
  }
  {
    const r = ps(['-InstallDir', install('d1', '0.1.3'), '-ReleasesJson', rel, '-Tag', 'v0.1.2']);
    check('-Tag of a release pulled back to draft = refused', r.code === 2 && /v0\.1\.2 is not a published release/.test(r.last), r.out);
  }
  {
    const nod = releases('nodigest', [{ tag_name: 'v0.1.1', draft: false, prerelease: false, assets: [asset('v0.1.1', null)] }]);
    const r = ps(['-InstallDir', install('nd', '0.1.2'), '-ReleasesJson', nod]);
    check('an asset without a sha256 digest = refused (a download could not be checked)', r.code === 2 && /has no sha256 digest/.test(r.last), r.out);
  }
  {
    const r = ps(['-InstallDir', install('none', null), '-ReleasesJson', rel]);
    check('no installed Mantle and no -Tag = refused', r.code === 2 && /no installed Mantle in /.test(r.last), r.out);
  }
  {
    const clone = install('clone', '0.1.2');
    fs.writeFileSync(path.join(clone, '.git'), 'gitdir: C:/elsewhere/.git/worktrees/x\n');
    const r = ps(['-InstallDir', clone, '-ReleasesJson', rel, '-Run']);
    check('-InstallDir pointing at a git checkout or worktree = refused before anything, even with -Run', r.code === 2 && /is a git checkout, not an installed Mantle/.test(r.last) && untouched(r), r.out);
  }
  {
    const r = ps(['-InstallDir', install('both', '0.1.2'), '-ReleasesJson', rel, '-Run', '-VerifyOnly']);
    check('-Run with -VerifyOnly = refused (one mode at a time)', r.code === 2 && /use one of -Run, -Ask or -VerifyOnly/.test(r.last) && untouched(r), r.out);
  }
  {
    const text = fs.readFileSync(CMD, 'utf8');
    const work = text.split(/\r?\n/).filter((l) => l.trim() && !/^\s*(rem |@echo off|setlocal|set ")/i.test(l));
    check('the .cmd does the whole run on ONE line that ends in exit (the installer replaces its folder)', work.length === 1 && /-File "%MANTLE_PB%" -Ask %\* & del "%MANTLE_PB%" .* & exit \/b$/.test(work[0]), work);
    check('the .cmd runs a COPY in %TEMP%, with -ExecutionPolicy Bypass', /copy \/y "%~dp0put-back-previous\.ps1" "%MANTLE_PB%"/.test(text) && /set "MANTLE_PB=%TEMP%\\/.test(text) && /-ExecutionPolicy Bypass/.test(text));
  }
} finally {
  try { fs.rmSync(DIR, { recursive: true, force: true }); } catch (_) { /* a temp folder */ }
}
for (const f of failures) console.log('FAIL ' + f);
console.log(`PUT BACK TEST: ${passed} passed, ${failures.length} failed`);
process.exit(failures.length ? 1 : 0);
