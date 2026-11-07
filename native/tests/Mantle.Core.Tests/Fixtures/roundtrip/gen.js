// Regenerates the round-trip fixtures with Node's own JSON.stringify, so the expected bytes are exactly
// what Electron Mantle writes (each file's indent and trailing newline as its writer does it).
// Run: node native/tests/Mantle.Core.Tests/Fixtures/roundtrip/gen.js   (synthetic values only, no real profile)
'use strict';
const fs = require('fs');
const path = require('path');

// The tricky values every file also carries as an unknown key, so preservation is proved on all of them.
const tricky = {
  text: 'quote " backslash \\ tab \t newline \n bell \u0007 esc \u001b del \u007f é ü 中文 😀 \u2028',
  nums: [0, 1, -1, 0.1, 0.5, 1.25, 12.5, 1e21, 1.5e-7, 123456789012, 9007199254740991, -2.5e-300, 4.35],
  nested: { empty: {}, list: [], deep: [[{ a: null }]], 10: 'ten', 2: 'two', b: true },
  '0': 'index key', 'z-last': false,
};

// [name, indent, trailingNewline, value]: shapes from docs/data-files.md and the writers named there.
const files = [
  ['features.json', 2, false, { clipboard: true, updates: false, topbar: true, futureFeature: true }],
  ['theme.json', 2, false, { mode: 'dark' }],
  ['look-sync.json', 2, false, { lookSync: true, accent: { hue: null }, glass: { dark: { blurPx: 5, dim: 0.5, panel: 0 }, light: { blurPx: 12, dim: 0.45, panel: 0 }, render: 'picture' } }],
  ['first-run.json', 2, false, { state: 'done', how: 'existing-profile', at: 1762300000000, evidence: ['features.json', 'Local Storage'] }],
  ['installed-consent.json', 2, true, { accepted: true, at: '2026-10-31T12:00:00.000Z', version: '0.1.1' }],
  ['crash.json', 0, false, { history: [1762300000000, 1762300100000], last: { at: 1762300100000, what: 'TypeError: x is not a function', restarted: true } }],
  ['keep-ready.json', 2, true, { 'keepReady.settings': false, 'keepReady.taskmgr': true, 'keepReady.files': false, 'keepReady.taskview': false }],
  ['notifications-seen.json', 0, false, { ids: ['n1', 'n2', 'toast:42'] }],
  ['switcher.json', 2, false, { altTab: true, winKeys: false }],
  ['notif-banners.json', 0, false, { apps: { 'com.squirrel.Discord.Discord': { prior: 1 } }, pid: 4242, at: 1762300000000 }],
  ['gamemode-restore.json', 1, false, { v: 1, since: 1762300000000, how: 'auto', game: { file: 'C:\\Games\\x.exe', pid: 77 }, power: { scheme: '381b4222-f694-41f0-9685-ff5bb260df2e' }, saver: null, procs: [], wallpaper: false, lively: null }],
  ['ping-tests.json', 2, false, { version: 1, tests: [{ id: 't1', name: 'Home', at: 1762300000000, targets: ['1.1.1.1'] }] }],
  ['supervisor-main.json', 0, false, { v: 1, pid: 1234, at: 1762300000000, exe: 'C:\\x\\electron.exe', args: ['.', '--live'], cwd: 'C:\\x', root: 'C:\\x', standDown: null, crashNote: null, told: 0 }],
  ['player-library.json', 0, false, { version: 2, at: 1762300000000, roots: ['C:\\Music'], files: [{ path: 'C:\\Music\\a.mp3', size: 123 }] }],
  ['wallpapers\\library.json', 2, false, { version: 1, selected: 'w1', items: [{ id: 'w1', title: 'Hills', type: 'picture', file: 'hills.jpg', thumb: 'thumb.jpg' }], removed: [], lively: null }],
  ['gcal\\cache.json', 0, false, { v: 1, events: [{ id: 'e', start: '2026-11-07T10:00:00Z', title: 'Practice' }], calendars: [{ id: 'primary', on: true }] }],
  ['features\\ramreclaim-journal.json', 1, false, { v: 1, entries: { 'HKCU\\Software\\X\\Y': { was: 1, at: 1762300000000 } } }],
  ['features\\taskmgr-history.json', 0, false, { v: 1, since: 1762300000000, lastAt: null, apps: { 'code.exe': { cpu: 12.5, net: 0 } }, seen: {} }],
  ['features\\files-last.json', 0, false, { v: 1, path: 'C:\\Users\\u\\Downloads', at: 1762300000000, count: 1, items: [{ name: 'a.txt' }] }],
  ['features\\jumplist.json', 2, false, { pins: [{ path: 'C:\\Program Files\\App\\app.exe', name: 'App' }] }],
  ['features\\emoji.json', 2, false, { recents: ['😀', '👍🏽', '❤️'], tone: 2, closeAfterInsert: true, apps: {} }],
  ['features\\layouts.json', 2, false, { layouts: [{ name: 'Work', savedAt: 1762300000000, entries: [{ exe: 'code.exe', rect: [0, 0, 1280, 720] }] }] }],
];

const out = __dirname;
const probe = { added: 'by native', n: 0.1, list: [1, 2.5, 'é😀'] };
for (const [name, indent, nl, value] of files) {
  const v = Object.assign({}, value, { unknownFromTheFuture: tricky });
  const file = path.join(out, ...name.split('\\'));
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(v, null, indent) + (nl ? '\n' : ''));
  const after = JSON.parse(JSON.stringify(v));
  after.nativeProbe = probe;
  fs.writeFileSync(file + '.after', JSON.stringify(after, null, indent) + (nl ? '\n' : ''));
}
console.log('wrote', files.length, 'fixtures');
