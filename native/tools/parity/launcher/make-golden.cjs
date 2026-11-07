'use strict';
/* make-golden.cjs: Electron's launcher index and ranking, run on a FIXTURE, written out as the spec the
 * native port is tested against (native/docs/lanes/launcher.md).
 *
 * It runs the real lib/launcher-index.js and ui/launcher/groups.js from this checkout, in plain node, on:
 *   - a scratch Start Menu (two roots, .lnk files written here, byte for byte the same in the C# test);
 *   - a scratch home with a PowerShell profile (USERPROFILE/APPDATA/ProgramData point at the scratch
 *     folder BEFORE the module loads, and OneDrive is unset), so the user's own profile is never read;
 *   - a fake `powercfg /a` (child_process.execFile is stubbed: nothing is spawned);
 *   - a fake Get-StartApps list (the module's own test hook).
 * Nothing here reads the user's Start Menu, profile or settings, and nothing is launched.
 *
 * Writes (or with --check, compares and exits 1 on a difference):
 *   native/tests/Mantle.Core.Tests/Search/LauncherGolden.g.cs     the fixture, Electron's build() output,
 *                                                                 and search()+groups for every query
 *   native/src/Mantle.Core/Features/LauncherIndex/SettingsTable.g.cs   SETTINGS, MANTLE_SETTINGS and the
 *                                                                 section claims, as Electron has them
 *
 *   node native/tools/parity/launcher/make-golden.cjs [--check]
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const cp = require('child_process');

const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const CHECK = process.argv.includes('--check');
const SCRATCH = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-launcher-golden-'));
const HOME = path.join(SCRATCH, 'home');
const PD = path.join(SCRATCH, 'pd', 'Microsoft', 'Windows', 'Start Menu', 'Programs');
const AD = path.join(HOME, 'AppData', 'Roaming', 'Microsoft', 'Windows', 'Start Menu', 'Programs');

/* ---- the fixture -------------------------------------------------------------------------------- */

/* A minimal MS-SHLLINK file: the 76-byte header, then StringData (Unicode) for the parts given. */
function lnk({ rel, args, icon } = {}) {
  const F_RELATIVE_PATH = 0x08, F_ARGUMENTS = 0x20, F_ICON_LOCATION = 0x40, F_UNICODE = 0x80;
  let flags = F_UNICODE;
  const parts = [];
  if (rel) { flags |= F_RELATIVE_PATH; parts.push(rel); }
  if (args) { flags |= F_ARGUMENTS; parts.push(args); }
  if (icon) { flags |= F_ICON_LOCATION; parts.push(icon); }
  const head = Buffer.alloc(0x4C);
  head.writeUInt32LE(0x4C, 0);
  Buffer.from([0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46]).copy(head, 4);
  head.writeUInt32LE(flags, 0x14);
  head.writeUInt32LE(1, 0x3C);                       // ShowCommand: normal
  const strings = parts.map((s) => {
    const b = Buffer.alloc(2 + s.length * 2);
    b.writeUInt16LE(s.length, 0);
    b.write(s, 2, 'utf16le');
    return b;
  });
  return Buffer.concat([head, ...strings]);
}

/* [root, relative path, lnk spec]: pd = all users, ad = this user. Names chosen to cover the ranking's
   edges: case, digits, punctuation, accents, words inside names, stand-ins, noise, duplicates. */
const START_MENU = [
  ['pd', 'Visual Studio Code\\Visual Studio Code.lnk'],
  ['pd', 'Windows Terminal.lnk'],
  ['pd', 'Google Chrome.lnk'],
  ['pd', 'Firefox.lnk'],
  ['pd', 'Microsoft Edge.lnk'],
  ['pd', '7-Zip\\7-Zip File Manager.lnk'],
  ['pd', '7-Zip\\7-Zip Help.lnk'],
  ['pd', 'Notepad++.lnk'],
  ['pd', 'PowerShell\\PowerShell 7 (x64).lnk'],
  ['pd', 'Windows PowerShell\\Windows PowerShell.lnk'],
  ['pd', 'Windows PowerShell\\Windows PowerShell ISE.lnk'],
  ['pd', 'Windows PowerShell\\Windows PowerShell (x86).lnk'],
  ['pd', 'System Tools\\Command Prompt.lnk'],
  ['pd', 'System Tools\\Task Manager.lnk', { icon: '%windir%\\system32\\Taskmgr.exe,-0' }],
  ['pd', 'System Tools\\File Explorer.lnk'],
  ['pd', 'System Tools\\Control Panel.lnk'],
  ['pd', 'System Tools\\Run.lnk'],
  ['pd', 'Accessories\\Paint.lnk'],
  ['pd', 'Accessories\\Snipping Tool.lnk'],
  ['pd', 'Accessories\\Remote Desktop Connection.lnk'],
  ['pd', 'Accessories\\Steps Recorder.lnk'],
  ['pd', 'Accessories\\Character Map.lnk'],
  ['pd', 'Administrative Tools\\Services.lnk'],
  ['pd', 'Administrative Tools\\Event Viewer.lnk'],
  ['pd', 'Administrative Tools\\Disk Cleanup.lnk'],
  ['pd', 'Administrative Tools\\Registry Editor.lnk'],
  ['pd', 'Administrative Tools\\Resource Monitor.lnk'],
  ['pd', 'Administrative Tools\\System Information.lnk'],
  ['pd', 'Steam\\Steam.lnk'],
  ['pd', 'Steam\\Steam Support Center.lnk'],
  ['pd', 'Steam\\Uninstall Steam.lnk'],
  ['pd', 'OBS Studio\\OBS Studio (64bit).lnk'],
  ['pd', 'Blender\\Blender 4.2.lnk'],
  ['pd', 'Blender\\blender documentation.lnk'],
  ['pd', 'Git\\Git Bash.lnk'],
  ['pd', 'Git\\Git CMD.lnk'],
  ['pd', 'Git\\Git GUI.lnk'],
  ['pd', 'Git\\Git Release Notes.lnk'],
  ['pd', 'Node.js\\Node.js.lnk'],
  ['pd', 'Node.js\\Node.js command prompt.lnk'],
  ['pd', 'Node.js\\Node.js website.lnk'],
  ['pd', 'Python 3.12\\Python 3.12 (64-bit).lnk'],
  ['pd', 'Python 3.12\\IDLE (Python 3.12 64-bit).lnk'],
  ['pd', 'Python 3.12\\Python 3.12 Manuals (64-bit).lnk'],
  ['pd', 'Microsoft Office\\Word.lnk'],
  ['pd', 'Microsoft Office\\Excel.lnk'],
  ['pd', 'Microsoft Office\\PowerPoint.lnk'],
  ['pd', 'Microsoft Office\\Outlook (classic).lnk'],
  ['pd', 'Microsoft Office\\OneNote.lnk'],
  ['pd', 'Audacity.lnk'],
  ['pd', 'VLC media player.lnk'],
  ['pd', 'Zoom\\Zoom Workplace.lnk'],
  ['pd', 'Épicerie Locale.lnk'],
  ['pd', 'éditeur de texte.lnk'],
  ['pd', 'Ändern Tool.lnk'],
  ['pd', 'a-b test.lnk'],
  ['pd', 'ab test.lnk'],
  ['pd', 'A B test.lnk'],
  ['pd', '_underscore app.lnk'],
  ['pd', 'Zed (Preview).lnk'],
  ['pd', 'paint.net.lnk'],
  ['pd', 'Deep\\1\\2\\3\\4\\5\\6\\7\\Depth Seven.lnk'],
  ['pd', 'Deep\\1\\2\\3\\4\\5\\6\\7\\8\\9\\Too Deep.lnk'],
  ['pd', 'Duplicate Name.lnk'],
  ['pd', 'not a shortcut.txt'],
  ['ad', 'Spotify.lnk'],
  ['ad', 'Discord Inc\\Discord.lnk'],
  ['ad', 'Slack Technologies\\Slack.lnk'],
  ['ad', 'Duplicate Name.lnk'],
  ['ad', 'Mantle.lnk'],
  ['ad', 'Helm.lnk'],
  ['ad', 'Quartz.lnk'],
  ['ad', 'Chrome Apps\\YouTube.lnk'],
  ['ad', 'Chrome Apps\\Google Docs.lnk'],
  ['ad', 'Programs Helper\\Settings.lnk', { rel: '..\\..\\..\\..\\..\\..\\..\\..\\Windows\\ImmersiveControlPanel\\SystemSettings.exe' }],
  ['ad', 'Epic Games Launcher.lnk'],
  ['ad', 'Minecraft Launcher.lnk'],
  ['ad', 'Battle.net.lnk'],
  ['ad', 'WinRAR\\WinRAR.lnk'],
  ['ad', 'WinRAR\\WinRAR help.lnk'],
  ['ad', 'Obsidian.lnk'],
  ['ad', 'Postman\\Postman.lnk'],
  ['ad', 'GitHub, Inc\\GitHub Desktop.lnk'],
  ['ad', 'Windows Sandbox.lnk'],
  ['ad', 'Sound Settings Helper.lnk'],
  ['ad', 'Read Me First.lnk'],
];

const PROFILE = [
  '# fixture profile',
  'function dash { Start-Process mission-control }',
  'function bot { python bot.py }',
  'function notes { code ~/notes }',
  'function Get-Weather($city) { }',
  'function _private { }',
  '  function Indented-Helper { }',
  'function dash { "second definition, ignored" }',
  'Set-Alias gs Get-Status',
  'Set-Alias -Name ll -Value Get-ChildItem',
  'set-alias lower-case-alias Get-Thing',
  'function Deploy.Site { }',
  'function wifi-reset { }',
  'function sleepy { }',
  'Function Capital-F { }',
].join('\r\n');

const POWERCFG = [
  'The following sleep states are available on this system:',
  '    Standby (S0 Low Power Idle) Network Connected',
  '    Hibernate',
  '    Fast Startup',
  '',
  'The following sleep states are not available on this system:',
  '    Standby (S1)',
  '        The system firmware does not support this standby state.',
  '',
  '    Hybrid Sleep',
  '        Standby (S3) is not available.',
  '',
].join('\r\n');

const STORE = [
  ['Calculator', 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App'],
  ['Notepad', 'Microsoft.WindowsNotepad_8wekyb3d8bbwe!App'],
  ['Paint', 'Microsoft.Paint_8wekyb3d8bbwe!App'],
  ['Photos', 'Microsoft.Windows.Photos_8wekyb3d8bbwe!App'],
  ['Microsoft Store', 'Microsoft.WindowsStore_8wekyb3d8bbwe!App'],
  ['Media Player', 'Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic'],
  ['Settings', 'windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel'],
  ['Windows Terminal', 'Microsoft.WindowsTerminal_8wekyb3d8bbwe!App'],
  ['Clock', 'Microsoft.WindowsAlarms_8wekyb3d8bbwe!App'],
  ['Xbox', 'Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App'],
  ['Get Help', 'Microsoft.GetHelp_8wekyb3d8bbwe!App'],
  ['Snipping Tool', 'Microsoft.ScreenSketch_8wekyb3d8bbwe!App'],
  ['Phone Link', 'Microsoft.YourPhone_8wekyb3d8bbwe!App'],
  ['Clipchamp', 'Clipchamp.Clipchamp_yxz26nhyzhsrt!App'],
  ['Bad Path', 'C:\\Program Files\\x.exe'],
  ['calculator dup', 'microsoft.windowscalculator_8wekyb3d8bbwe!app'],
];

/* ---- run Electron's module on it ------------------------------------------------------------------ */

const files = START_MENU.map(([root, rel, spec]) => ({ root, rel, b64: rel.endsWith('.lnk') ? lnk(spec).toString('base64') : Buffer.from('plain text').toString('base64') }));
for (const f of files) {
  const full = path.join(f.root === 'pd' ? PD : AD, f.rel);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, Buffer.from(f.b64, 'base64'));
}
const profileFile = path.join(HOME, 'Documents', 'PowerShell', 'Microsoft.PowerShell_profile.ps1');
fs.mkdirSync(path.dirname(profileFile), { recursive: true });
fs.writeFileSync(profileFile, PROFILE);

process.env.USERPROFILE = HOME;
process.env.HOME = HOME;
process.env.APPDATA = path.join(HOME, 'AppData', 'Roaming');
process.env.ProgramData = path.join(SCRATCH, 'pd');
delete process.env.OneDrive;
delete process.env.OneDriveConsumer;
delete process.env.OneDriveCommercial;
cp.execFile = function (file, args, opts, cb) {
  if (typeof opts === 'function') cb = opts;
  if (/powercfg\.exe$/i.test(String(file))) { setImmediate(() => cb(null, POWERCFG, '')); return { kill() {} }; }
  setImmediate(() => cb(new Error('make-golden: nothing is spawned (' + path.basename(String(file)) + ')'), '', ''));
  return { kill() {} };
};

const idx = require(path.join(REPO, 'lib', 'launcher-index.js'));
const groups = require(path.join(REPO, 'ui', 'launcher', 'groups.js'));
const MA = require(path.join(REPO, 'lib', 'mantle-apps.js'));
idx._setStartMenuRootsForTest([{ dir: PD, scope: 'all users' }, { dir: AD, scope: 'you' }]);
const storeText = STORE.map(([n, a]) => n + '\t' + a).join('\r\n');
idx._setStoreRunnerForTest(() => Promise.resolve({ ok: true, list: idx._internals.parseStartApps(storeText), reason: '' }));

const ICON_KEYS = {};
for (const k of ['settings', 'taskmgr', 'files', 'player']) { const u = MA.iconUrl(k, 64); if (u) ICON_KEYS[u] = 'mantle:' + k; }

function rel(p) {
  if (typeof p !== 'string') return p;
  return p.split(PD).join('<pd>').split(AD).join('<ad>');
}
function norm(it) {
  const o = { id: it.id, kind: it.kind, name: it.name, subtitle: it.subtitle, path: rel(it.path) };
  if (it.keywords !== undefined) o.keywords = it.keywords;
  for (const k of ['secondary', 'mantleApp', 'standsFor', 'aumid', 'glyph', 'confirm']) if (it[k] !== undefined && it[k] !== null) o[k] = it[k];
  if (it.windowsPath) o.windowsPath = rel(it.windowsPath);
  o.icon = it.icon == null ? null : (ICON_KEYS[it.icon] || 'other');
  return o;
}

function queriesFor(items) {
  const qs = new Set(['', 'a', 'al', 'boom', 'calc', 'ch', 'contacts access', 'dash', 'file explorer', 'files',
    'github.com/Owen-Silva', 'hello world', 'https://example.com', 'lock', 'log out', 'mantle settings', 'many', 'mic',
    'mouse keys', 'notepad', 'power', 'reboot', 'restart', 's', 'se', 'session', 'sett', 'settings', 'sleep',
    'task manager', 'taskmgr', 'wallpaper', 'win', 'x', 'zzzzzqqqq', 'dark mode', 'desktop background', 'uninstall', 'bt',
    'wifi', 'night', 'startup', 'default browser', 'update', 'hdr', 'vpn', 'screen timeout', 'bluetooth', 'resolution',
    'speakers', 'indexing', 'hotspot', 'do not disturb', 'antivirus', 'pin', 'time zone', 'volume mixer', 'power mode',
    'snap', 'disk cleanup', 'taskbar', 'rename pc', 'mode dark', 'chr', 'vsc', 'wt', 'ps', 'np', 'term', 'vs code',
    'shut', 'rebo', 'down', 'off', 'turn off', 'sign', 'lo', 'loc', 'sl', 'sle', 'hib', 'pow', 'ses',
    'example.com', 'example.com/path?x=1', 'http://a', 'a.b', 'a.bc', 'foo bar.com', '1.2.3.4', 'localhost:3000', 'x.io#top',
    '  Calc  ', 'CALC', 'Visual Studio Code', 'é', 'épi', 'ä', 'and', '7', '7-z', '(x64)', '++', '&', 'a b', 'a-b', 'ab',
    'media player', 'player', 'music', 'groove', 'mantle', 'windows', 'windows settings', 'control panel', 'explorer',
    'win+e', 'win+i', 'ctrl+shift+esc', 'free ram', 'game', 'clips', 'wallpapers', 'get ready', 'git', 'node', 'python',
    'py', 'office', 'store', 'microsoft', 'micro', 'ms', 'edge', 'browser', 'run', 'cmd', 'prompt', 'help', 'readme',
    'gs', 'll', 'deploy', 'deploy.site', 'get-weather', 'weather', 'capital', 'sleepy', 'zz', 'qq', 'zed', 'preview',
    'tool', 'tools', 'system', 'admin', 'deep', 'depth', 'too deep', 'duplicate', 'dup', 'steam', 'st', 'sp', 'spo',
    'disc', 'sla', 'obs', 'blend', 'zoom', 'battle', 'epic', 'mine', 'craft', 'rar', 'obsid', 'post', 'github', 'sandbox',
    'sound', 'sound settings', 'helper', 'first', 'calculator', 'clock', 'xbox', 'phone', 'clipchamp', 'photos',
    'accessibility', 'narrator', 'privacy', 'camera', 'location', 'display', 'brightness', 'keyboard', 'mouse',
    'printer', 'about', 'storage', 'battery', 'network', 'ethernet', 'proxy', 'airplane', 'developer', 'for developers',
  ]);
  const add = (s) => { s = String(s || '').toLowerCase().trim(); if (s) qs.add(s); };
  for (const it of items) {
    const name = String(it.name).toLowerCase();
    for (let n = 1; n <= 4 && n <= name.length; n++) add(name.slice(0, n));
    for (const w of name.split(/[^a-z0-9]+/).filter(Boolean)) { add(w.slice(0, 3)); add(w); }
    if (name.length > 3) add(name.slice(1, 4));
    const sub = String(it.subtitle || '').toLowerCase();
    if (sub) add(sub.split(/\s+/)[0]);
    if (typeof it.keywords === 'string') { const kw = it.keywords.split(' '); add(kw[0]); if (kw.length > 1) add(kw[0] + ' ' + kw[1]); }
  }
  return Array.from(qs);
}

(async () => {
  const built = await idx.build({});
  await new Promise((r) => setTimeout(r, 20));          // a late Store merge has had its turn
  const items = built.items;
  const pos = new Map(items.map((it, i) => [it.id, i]));
  const queries = queriesFor(items);
  const cases = [];
  for (const q of queries) {
    const rows = idx.search(q, items);
    const ids = rows.map((r) => (pos.has(r.id) && r.kind !== 'url') ? pos.get(r.id) : r.id);
    const scores = rows.map((r) => (typeof r.score === 'number' ? r.score : null));
    const urlRow = rows.find((r) => r.kind === 'url');
    const g = groups.build({ q, appRows: rows, fileRows: [], fileState: { status: 'idle' }, filePending: false, calc: null });
    cases.push({
      q, ids, scores,
      url: urlRow ? { id: urlRow.id, name: urlRow.name, path: urlRow.path } : null,
      groups: g.groups.map((x) => x.key + ':' + x.rows.length).join(','),
      flat: g.flat.map((r) => r.id),
    });
  }
  const golden = {
    note: 'Generated by native/tools/parity/launcher/make-golden.cjs from lib/launcher-index.js and ui/launcher/groups.js. Do not edit.',
    fixture: { files, profile: PROFILE, powercfg: POWERCFG, store: storeText },
    items: items.map(norm),
    sources: built.sources.map((s) => ({ name: s.name, count: s.count, status: s.status, reason: rel(s.reason) })),
    stableIds: [['app', 'C:\\X\\Y.lnk'], ['set', 'ms-settings:display'], ['power', 'lock'], ['cmd', 'Dash'], ['url', 'https://example.com'], ['app', 'aumid:Microsoft.Paint_8wekyb3d8bbwe!App'], ['layout', 'work']]
      .map(([k, v]) => [k, v, idx._internals.stableId(k, v)]),
    sleep: idx._internals.parseSleepStates(POWERCFG),
    urls: ['example.com', 'a.b', 'http://a', 'foo bar.com', '1.2.3.4', 'localhost:3000', 'x.io#top', 'HTTPS://X', 'a.b c', 'a..bc', '-a.bc', 'a.b1']
      .map((q) => [q, idx._internals.looksLikeUrl(q)]),
    cases,
  };
  const json = JSON.stringify(golden);
  const goldenCs = '// <auto-generated> by native/tools/parity/launcher/make-golden.cjs from Electron\'s lib/launcher-index.js. Do not edit.\n' +
    'namespace Mantle.Core.Tests.Search;\n\ninternal static class LauncherGolden\n{\n    public const string Json = """"\n' + json + '\n"""";\n}\n';

  /* the Settings table as Electron has it, for the native index */
  const I = idx._internals;
  const claims = I.sectionClaims().claims;
  const cs = (s) => (s == null ? 'null' : JSON.stringify(String(s)));
  const lines = [];
  lines.push('// <auto-generated> by native/tools/parity/launcher/make-golden.cjs from Electron\'s lib/launcher-index.js');
  lines.push('// (SETTINGS, MANTLE_SETTINGS) and the pages ui/settings/sections/*.js claim. Do not edit: re-run the script.');
  lines.push('namespace Mantle.Core.Features.LauncherIndex;');
  lines.push('');
  lines.push('internal static partial class SettingsTable');
  lines.push('{');
  lines.push('    /// [name, ms-settings uri, keywords], in Electron\'s order (lib/launcher-index.js#SETTINGS).');
  lines.push('    public static readonly string[][] Pages =');
  lines.push('    [');
  for (const [n, u, k] of I.SETTINGS) lines.push('        [' + cs(n) + ', ' + cs(u) + ', ' + cs(k || '') + '],');
  lines.push('    ];');
  lines.push('');
  lines.push('    /// lib/launcher-index.js#MANTLE_SETTINGS: uri -> (section, row).');
  lines.push('    public static readonly (string Uri, string Section, string? Row)[] Covered =');
  lines.push('    [');
  for (const [u, v] of Object.entries(I.MANTLE_SETTINGS)) lines.push('        (' + cs(u) + ', ' + cs(v.section) + ', ' + cs(v.row) + '),');
  lines.push('    ];');
  lines.push('');
  lines.push('    /// The pages Mantle Settings\' section modules claim (lib/launcher-index.js#sectionClaims), in Electron\'s order.');
  lines.push('    public static readonly (string Uri, string Section, string? Page, string? Row, string Name, string Keywords, string Where)[] Claims =');
  lines.push('    [');
  for (const c of Object.values(claims)) lines.push('        (' + [cs(c.uri), cs(c.section), cs(c.page), cs(c.row), cs(c.name), cs(c.keywords || ''), cs(c.where || '')].join(', ') + '),');
  lines.push('    ];');
  lines.push('}');
  const tableCs = lines.join('\n') + '\n';

  const outs = [
    [path.join(REPO, 'native', 'tests', 'Mantle.Core.Tests', 'Search', 'LauncherGolden.g.cs'), goldenCs],
    [path.join(REPO, 'native', 'src', 'Mantle.Core', 'Features', 'LauncherIndex', 'SettingsTable.g.cs'), tableCs],
  ];
  let bad = 0;
  for (const [file, text] of outs) {
    if (CHECK) {
      const was = fs.existsSync(file) ? fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n') : '';
      if (was !== text) { bad++; console.log('GOLDEN DRIFT: ' + path.relative(REPO, file) + ' differs from what Electron gives now'); }
    } else {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, text);
    }
  }
  fs.rmSync(SCRATCH, { recursive: true, force: true });
  console.log('GOLDEN: ' + (CHECK ? (bad ? 'FAILED' : 'ok') : 'written') + ' (' + items.length + ' items, ' + cases.length + ' queries, ' + Object.keys(claims).length + ' claimed pages)');
  process.exit(bad ? 1 : 0);
})().catch((e) => { console.error('GOLDEN: FAILED ' + (e && e.stack || e)); process.exit(1); });
