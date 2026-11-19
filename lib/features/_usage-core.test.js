/* _usage-core.test.js - node lib/features/_usage-core.test.js (lane mantle-ps-node, 2026-11-18).
 * The pure half of "Power use and screen time" (_usage-core.js) and its files (_usage-store.js):
 * the screen-time accumulator's every rule, the Mantle-window rule, retention and clear, the power
 * attribution maths, buckets and their gap rules, history, cost, and the damaged-file handling.
 * Times are local (new Date(y, m, d, h, min)), so it passes in any time zone. */
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const C = require('./_usage-core.js');
const { createStore } = require('./_usage-store.js');

let passed = 0;
const failures = [];
function check(name, ok, detail) { if (ok) passed++; else failures.push(detail !== undefined ? `${name} (${JSON.stringify(detail)})` : name); }
const near = (a, b, eps) => typeof a === 'number' && Math.abs(a - b) <= (eps === undefined ? 1e-6 : eps);
const T = (d, h, m, s) => new Date(2026, 10, d, h, m || 0, s || 0).getTime();   // November 2026, local
const MIN = 60000;
const fg = (file, extra) => Object.assign({ kind: 'fg', pid: 10, file, path: `C:\\Apps\\${file}`, desc: file.replace(/\.exe$/, '') }, extra || {});

/* ---- screen time ------------------------------------------------------------------------------ */
function screenTime() {
  // midnight and hour split
  let st = new C.ScreenTime();
  st.apply(fg('Code.exe'), T(18, 23, 50));
  st.settle(T(19, 0, 10), 0);
  const d18 = st.data.days['2026-11-18'], d19 = st.data.days['2026-11-19'];
  check('midnight: 23:50-00:10 = 600 s on the 18th in hour 23', d18 && near(d18.apps['code.exe'], 600) && near(d18.hours[23], 600), d18);
  check('midnight: 600 s on the 19th in hour 0', d19 && near(d19.apps['code.exe'], 600) && near(d19.hours[0], 600), d19);
  st = new C.ScreenTime();
  st.apply(fg('Code.exe'), T(18, 10, 50));
  st.settle(T(18, 12, 5), 0);
  const h = st.data.days['2026-11-18'].hours;
  check('hours: 10:50-12:05 = 600 / 3600 / 300 in hours 10, 11, 12', near(h[10], 600) && near(h[11], 3600) && near(h[12], 300) && h.reduce((s, x) => s + x, 0) === 4500, h.slice(9, 13));

  // idle cuts at now - idleMs; active restarts at now - agoMs, never before the cut
  st = new C.ScreenTime();
  st.apply(fg('Chrome.exe'), T(18, 10, 0));
  st.apply({ kind: 'idle', idleMs: 5 * MIN }, T(18, 10, 20));
  const sec = () => (st.data.days['2026-11-18'] ? st.data.days['2026-11-18'].apps['chrome.exe'] || 0 : 0);
  check('idle at 10:20 after 5 min without input: 15 min counted', near(sec(), 900), sec());
  check('idle: paused idle, still counted as in front', st.paused === 'idle' && !st.counting() && st.fgApp && st.fgApp.key === 'chrome.exe');
  st.apply({ kind: 'active', agoMs: MIN }, T(18, 10, 30));
  check('active at 10:30, input 1 min ago: counting from 10:29', st.open && st.open.since === T(18, 10, 29), st.open);
  st.apply({ kind: 'idle', idleMs: 5 * MIN }, T(18, 10, 40));
  check('...idle again at 10:40: 15 + 6 min', near(sec(), 1260), sec());
  st.apply({ kind: 'active', agoMs: 30 * MIN }, T(18, 10, 50));
  check('active with agoMs reaching back past the last cut: starts at the cut (10:35), never double', st.open && st.open.since === T(18, 10, 35), st.open);
  st.apply({ kind: 'lock' }, T(18, 11, 0));
  check('lock at 11:00: 15 + 6 + 25 min, paused locked', near(sec(), 2760) && st.paused === 'locked' && !st.open, sec());
  st.apply(fg('LockApp.exe'), T(18, 11, 1));
  st.apply({ kind: 'unlock' }, T(18, 11, 10));
  st.apply(fg('Chrome.exe'), T(18, 11, 10));
  check('unlock: nothing counted while locked (the lock screen app included)', !st.data.days['2026-11-18'].apps['lockapp.exe'] && st.open && st.open.since === T(18, 11, 10));
  st.apply({ kind: 'suspend' }, T(18, 11, 20));
  check('suspend: closed at 11:20, paused asleep', near(sec(), 3360) && st.paused === 'asleep' && !st.open, sec());
  st.apply({ kind: 'resume' }, T(18, 19, 0));
  check('resume: counting again from the resume, nothing for the night', st.open && st.open.since === T(18, 19, 0) && near(sec(), 3360));
  st.apply({ kind: 'shell' }, T(18, 19, 5));
  check('shell: the desktop in front closes the interval and counts nothing', !st.open && st.paused === null && near(sec(), 3660));
  st.apply(fg('Code.exe'), T(18, 19, 6));
  st.setOn(false, T(18, 19, 16));
  check('switch off: closed at once, paused off', !st.open && st.paused === 'off' && near(st.data.days['2026-11-18'].apps['code.exe'], 600));
  st.apply(fg('Chrome.exe'), T(18, 19, 20));
  check('switch off: an app change counts nothing', !st.open);
  st.setOn(true, T(18, 19, 30));
  check('switch on: counting from now for the app in front', st.open && st.open.key === 'chrome.exe' && st.open.since === T(18, 19, 30));

  // the hold: a settle leaves the last idleMin uncounted, so a later idle event still cuts at the last input
  st = new C.ScreenTime();
  st.apply(fg('Chrome.exe'), T(18, 9, 0));
  st.settle(T(18, 9, 10), 5 * MIN);
  check('settle with a 5 min hold: 9:00-9:05 counted, the interval stays open', near(st.data.days['2026-11-18'].apps['chrome.exe'], 300) && st.open && st.open.since === T(18, 9, 5));
  st.apply({ kind: 'idle', idleMs: 5 * MIN + 30000 }, T(18, 9, 12));
  check('...an idle event at 9:12 (last input 9:06:30) cuts at 9:06:30', near(st.data.days['2026-11-18'].apps['chrome.exe'], 390), st.data.days['2026-11-18'].apps);

  // the sleep gap the events missed: cut at the last beat, count again from now
  st = new C.ScreenTime();
  st.apply(fg('Chrome.exe'), T(18, 9, 0));
  st.cut(T(18, 9, 5), T(18, 17, 0));
  check('cut(at the last beat, now): 5 min counted, open again from now', near(st.data.days['2026-11-18'].apps['chrome.exe'], 300) && st.open && st.open.since === T(18, 17, 0));
  st.cut(T(18, 17, 2));
  check('cut without now: closed', !st.open);

  // view: a copy (nothing stored moves), 7 days oldest first, current / paused / since
  st = new C.ScreenTime();
  st.apply(fg('Chrome.exe'), T(16, 9, 0)); st.cut(T(16, 10, 0));
  st.apply(fg('Code.exe'), T(18, 9, 0)); st.settle(T(18, 9, 30), 0);
  st.dirty = false;
  const before = JSON.stringify(st.toJSON());
  const v = st.view(T(18, 9, 45));
  check('view: the running 15 min shows (30 + 15 min today)', v.today.totalSec === 2700 && v.today.apps[0].seconds === 2700 && v.today.hours[9] === 2700, v.today);
  check('view: nothing stored changed', JSON.stringify(st.toJSON()) === before && st.dirty === false);
  check('view: 7 days, oldest first, today last, keptDays 30', v.days.length === 7 && v.days[0].date === '2026-11-12' && v.days[6].date === '2026-11-18' && v.keptDays === 30, v.days.map((d) => d.date));
  check('view: the 16th has Chrome for an hour (top-8 rows, no hours)', v.days[4].totalSec === 3600 && v.days[4].apps[0].name === 'Chrome' && v.days[4].hours === undefined, v.days[4]);
  check('view: current Code, not paused, since = when it came in front', v.current && v.current.name === 'Code' && v.paused === null && v.since === T(18, 9, 0), v);
  check('view: an app row has key, name, icon, seconds (no path)', JSON.stringify(Object.keys(v.today.apps[0]).sort()) === JSON.stringify(['icon', 'key', 'name', 'seconds']) && v.today.apps[0].icon === null && v.today.apps[0].key === 'code.exe');
  st.apply({ kind: 'lock' }, T(18, 9, 50));
  const vl = st.view(T(18, 9, 55));
  check('view locked: current null, paused locked, since null', vl.current === null && vl.paused === 'locked' && vl.since === null, vl);

  // retention (30 local days, today included) and clear
  st = new C.ScreenTime();
  st.apply(fg('Old.exe'), T(18, 9, 0) - 31 * 86400000); st.settle(T(18, 9, 30) - 31 * 86400000, 0); st.cut(T(18, 9, 30) - 31 * 86400000);
  st.apply(fg('Edge.exe'), T(18, 9, 0) - 29 * 86400000); st.settle(T(18, 9, 30) - 29 * 86400000, 0); st.cut(T(18, 9, 30) - 29 * 86400000);
  st.apply(fg('Code.exe'), T(18, 9, 0));
  const gone = st.prune(T(18, 10, 0));
  check('prune: a day 31 days back goes, 29 days back stays (30 days with today)', !st.data.days['2026-10-18'] && !!st.data.days['2026-10-20'] && gone >= 1, Object.keys(st.data.days));
  check('prune: an app no kept day names is forgotten', !st.data.apps['old.exe'] && !!st.data.apps['edge.exe']);
  const had = st.clear(T(18, 10, 0));
  st.settle(T(18, 10, 10), 0);
  check('clear: every day goes; the app in front counts on from the clear', had === 1 && Object.keys(st.data.days).length === 1 && near(st.data.days['2026-11-18'].apps['code.exe'], 600) && !st.data.apps['edge.exe'], st.data);

  // privacy: a title on the event never reaches the record
  st = new C.ScreenTime();
  st.apply(fg('Chrome.exe', { title: 'SECRET bank page' }), T(18, 9, 0)); st.settle(T(18, 9, 5), 0);
  check('no title in what is stored', !/title|SECRET/.test(JSON.stringify(st.toJSON())));
  const parsed = C.parseScreentime({ v: 1, apps: { a: { name: 'A', file: 'a.exe', path: null, title: 'SECRET' } }, days: { '2026-11-18': { apps: { a: 12, b: -3 }, hours: [1, 2] } } });
  check('parse: a stored title is dropped, a negative mended, hours padded to 24', parsed.data && !/SECRET/.test(JSON.stringify(parsed.data)) && parsed.data.days['2026-11-18'].hours.length === 24 && parsed.data.days['2026-11-18'].apps.b === undefined, parsed);
  check('parse: a wrong version / shape is a problem', !!C.parseScreentime({ v: 2, apps: {}, days: {} }).problem && !!C.parseScreentime([]).problem && !!C.parseScreentime({ v: 1, apps: {}, days: { 'nov 18': {} } }).problem);
}

/* ---- privacy (General Counsel, 2026-11-18): exe names, never paths; the shell; icons ------------- */
function privacy() {
  const PATH = 'C:\\Users\\Zed Tester\\AppData\\Local\\Zebracorn\\Zebracorn.exe';
  let st = new C.ScreenTime();
  st.apply({ kind: 'fg', pid: 7, file: 'Zebracorn.exe', path: PATH, desc: 'Zebracorn Studio' }, T(18, 9, 0)); st.settle(T(18, 9, 10), 0);
  const disk = JSON.stringify(st.toJSON());
  check('privacy: the key is the exe file name; the path is held in memory only', st.fgApp.key === 'zebracorn.exe' && st.fgApp.path === PATH && st.data.apps['zebracorn.exe'].name === 'Zebracorn Studio');
  check('privacy: what is stored holds no path and no user name', !/Users|Zed Tester|AppData|\\\\/.test(disk) && JSON.stringify(Object.keys(st.data.apps['zebracorn.exe']).sort()) === '["file","name"]', disk);
  check('privacy: the view has no path either', !/Users|path/.test(JSON.stringify(st.view(T(18, 9, 20)))));
  // icons: kept with the app, capped, never asked twice
  check('icon: not asked yet', st.iconKnown('zebracorn.exe') === false);
  check('icon: a data URL is kept with the app', st.setIcon('zebracorn.exe', 'data:image/png;base64,QUJD') && st.data.apps['zebracorn.exe'].icon === 'data:image/png;base64,QUJD' && st.iconKnown('zebracorn.exe') && st.view(T(18, 9, 20)).today.apps[0].icon === 'data:image/png;base64,QUJD');
  st.setIcon('zebracorn.exe', 'data:image/png;base64,' + 'A'.repeat(C.ICON_MAX));
  check('icon: bigger than ICON_MAX (24 KB) = null, and known (never asked again)', st.data.apps['zebracorn.exe'].icon === null && st.iconKnown('zebracorn.exe') && C.ICON_MAX === 24 * 1024);
  check('icon: not a picture = null', C.iconOf('javascript:alert(1)') === null && C.iconOf(null) === null && C.iconOf('data:image/svg+xml,<svg/>') === 'data:image/svg+xml,<svg/>');
  st.apply({ kind: 'fg', pid: 7, file: 'Zebracorn.exe', path: PATH }, T(18, 9, 11)); st.settle(T(18, 9, 12), 0);
  check('icon: a later count keeps the stored icon', st.data.apps['zebracorn.exe'].icon === null && 'icon' in st.data.apps['zebracorn.exe']);
  const gen = st.gen; st.clear(T(18, 9, 30));
  check('clear: the icons go with the apps, and the generation moves (an icon asked before is dropped)', st.gen === gen + 1 && !st.iconKnown('zebracorn.exe') && Object.keys(st.data.days).length === 0);
  // the shell: Start, Search, the lock screen, explorer.exe unless it is File Explorer
  for (const file of ['StartMenuExperienceHost.exe', 'SearchHost.exe', 'SearchApp.exe', 'ShellExperienceHost.exe', 'ShellHost.exe', 'LockApp.exe', 'explorer.exe']) {
    st = new C.ScreenTime();
    st.apply({ kind: 'fg', pid: 8, file, path: 'C:\\Windows\\' + file, cls: 'Windows.UI.Core.CoreWindow' }, T(18, 9, 0)); st.settle(T(18, 9, 5), 0);
    check(`shell: ${file} is not counted`, !st.open && st.fgApp === null && !st.data.days['2026-11-18'], st.data);
  }
  st = new C.ScreenTime();
  st.apply({ kind: 'fg', pid: 9, file: 'explorer.exe', path: 'C:\\Windows\\explorer.exe', desc: 'Windows Explorer', cls: 'CabinetWClass' }, T(18, 9, 0)); st.settle(T(18, 9, 5), 0);
  check('shell: a File Explorer window (CabinetWClass) counts as the app explorer.exe', near(st.data.days['2026-11-18'].apps['explorer.exe'], 300));
  st.apply({ kind: 'fg', pid: 9, file: 'explorer.exe', cls: '#32770' }, T(18, 9, 5)); st.settle(T(18, 9, 8), 0);
  check("shell: explorer.exe's other windows (a dialog) stop the count", near(st.data.days['2026-11-18'].apps['explorer.exe'], 300) && !st.open);
  // two programs, one exe name: one row (accepted)
  st = new C.ScreenTime();
  st.apply({ kind: 'fg', pid: 1, file: 'app.exe', path: 'C:\\A\\app.exe', desc: 'First' }, T(18, 9, 0));
  st.apply({ kind: 'fg', pid: 2, file: 'App.exe', path: 'D:\\B\\App.exe', desc: 'Second' }, T(18, 9, 1)); st.settle(T(18, 9, 2), 0);
  check('two programs with one exe name share one row (accepted)', Object.keys(st.data.days['2026-11-18'].apps).join() === 'app.exe' && near(st.data.days['2026-11-18'].apps['app.exe'], 120));
  // a file written before 2026-11-18: path keys and path fields are re-keyed and dropped
  const old = C.parseScreentime({ v: 1, apps: { 'c:\\users\\zed\\z.exe': { name: 'Z', file: 'z.exe', path: 'C:\\Users\\Zed\\z.exe' }, 'file:y.exe': { name: 'Y', file: 'y.exe', path: null }, 'd:\\z.exe': { name: 'Z2', file: 'z.exe', path: 'D:\\z.exe' } },
    days: { '2026-11-17': { apps: { 'c:\\users\\zed\\z.exe': 60, 'd:\\z.exe': 30, 'file:y.exe': 5 }, hours: [] } } });
  check('old file: re-keyed by exe name, seconds added up, paths dropped, mended', old.mended === true && near(old.data.days['2026-11-17'].apps['z.exe'], 90) && near(old.data.days['2026-11-17'].apps['y.exe'], 5)
    && !/Users|Zed|path|\\\\/.test(JSON.stringify(old.data)), JSON.stringify(old.data));
  check('a current file is not mended', C.parseScreentime({ v: 1, apps: { 'z.exe': { name: 'Z', file: 'z.exe', icon: null } }, days: {} }).mended === false);
  const ob = C.checkBucket({ t: 1, dur: 300, apps: [['c:\\users\\zed\\z.exe', 'Z', 5, 0.5], ['windows', 'Windows', 1, 0.1]] });
  check('migration: a power line from before the per-app rule (no perApp) loses its app rows, keeps its totals, marked mended', ob.apps.length === 0 && ob.perApp === false && ob.mended === true && ob.t === 1 && ob.dur === 300 && !/z\.exe|windows|Zed/i.test(JSON.stringify(ob)), JSON.stringify(ob));
  const ob2 = C.checkBucket({ t: 1, dur: 300, perApp: true, apps: [['c:\\users\\zed\\z.exe', 'Z', 5, 0.5], ['windows', 'Windows', 1, 0.1]] });
  check('a line recorded with Screen time on keeps its rows; a path key becomes the exe name, marked mended', ob2.perApp === true && ob2.apps[0][0] === 'z.exe' && ob2.apps.length === 2 && ob2.mended === true && !JSON.stringify(ob2).includes('mended'));
  const ob3 = C.checkBucket({ t: 1, dur: 300, perApp: false, apps: [] });
  check('a totals-only line is left as it is (not mended)', ob3.perApp === false && ob3.apps.length === 0 && ob3.mended === undefined);
  const ob4 = C.checkBucket({ t: 1, dur: 300, perApp: false, apps: [['z.exe', 'Z', 5, 0.5]] });
  check('a line that says perApp false but names an app: the rows are dropped, mended', ob4.apps.length === 0 && ob4.mended === true);
  // the log's words
  check('scrub: a path and an exe name never reach the log', C.scrub('icon failed: C:\\Users\\Zed\\Zebracorn.exe was not found') === 'icon failed: <a path>' && C.scrub('Zebracorn.EXE exited') === '<a program> exited' && C.scrub('within 3s') === 'within 3s');
}

/* ---- the Mantle-window rule and app names ----------------------------------------------------- */
function mantleRule() {
  const wins = [{ hwnd: '123', focusable: true, onTop: false }, { hwnd: '456', focusable: true, onTop: true }, { hwnd: '789', focusable: false, onTop: false }];
  check('hwnd forms: "0x7b", 123 and "123" are the same window', C.normHwnd('0x7b') === '123' && C.normHwnd(123) === '123' && C.normHwnd('123') === '123' && C.normHwnd('nope') === null && C.normHwnd(0) === null);
  check('a focusable, not-on-top Mantle window is the app Mantle', C.mantleWindow({ hwnd: '0x7b', pid: 5 }, wins, 5) === 'app');
  check('an always-on-top Mantle window (bar, pop-up) is the shell', C.mantleWindow({ hwnd: 456 }, wins, 5) === 'shell');
  check('a non-focusable Mantle window (desktop) is the shell', C.mantleWindow({ hwnd: '789' }, wins, 5) === 'shell');
  check("another window of Mantle's process is the shell", C.mantleWindow({ hwnd: '999', pid: 5 }, wins, 5) === 'shell');
  check('anyone else is not Mantle', C.mantleWindow({ hwnd: '999', pid: 6 }, wins, 5) === null);
  const m = C.appOf({ mantle: 'app', file: 'electron.exe', path: 'C:\\m\\electron.exe' });
  check('appOf: Mantle is key mantle, name Mantle', m.key === 'mantle' && m.name === 'Mantle');
  check('appOf: the description names it, the lower-cased exe FILE name keys it', C.appOf({ file: 'chrome.exe', path: 'C:\\P\\Chrome.exe', desc: 'Google Chrome' }).key === 'chrome.exe' && C.appOf({ file: 'chrome.exe', path: 'C:\\P\\Chrome.exe', desc: 'Google Chrome' }).name === 'Google Chrome');
  check('appOf: no path = the same key, named by the file', C.appOf({ file: 'Game.exe', path: null }).key === 'game.exe' && C.appOf({ file: 'Game.exe' }).name === 'Game');
  const st = new C.ScreenTime();
  st.apply({ kind: 'fg', mantle: 'app', file: 'electron.exe', path: 'C:\\m\\electron.exe' }, T(18, 9, 0)); st.settle(T(18, 9, 1), 0);
  check('screen time: a Mantle window counts under "mantle", not electron.exe', near(st.data.days['2026-11-18'].apps.mantle, 60) && st.data.apps.mantle.name === 'Mantle' && !st.data.days['2026-11-18'].apps['electron.exe']);
}

/* ---- power ------------------------------------------------------------------------------------ */
const PWH = (joules) => joules / 3.6e-9;   // W = dpWh x 3.6e-9 / ds
function procs(scale) {
  const k = scale || 1;
  // [pid, ppid, file, cpu (100 ns), session, path, windows, created]
  return [
    [4, 0, 'System', 1e7 * k, 0, null, 0, '1'],
    [50, 4, 'svchost.exe', 0, 0, 'C:\\Windows\\System32\\svchost.exe', 0, '2'],
    [60, 1, 'explorer.exe', 0, 1, 'C:\\Windows\\explorer.exe', 1, '3'],
    [100, 60, 'electron.exe', 0, 1, 'C:\\mantle\\electron.exe', 3, '4'],
    [200, 60, 'chrome.exe', 4e7 * k, 1, 'C:\\P\\Chrome.exe', 2, '10'],
    [201, 200, 'chrome.exe', 2e7 * k, 1, 'C:\\P\\Chrome.exe', 0, '11'],
    [300, 60, 'Code.exe', 3e7 * k, 1, 'C:\\P\\Code.exe', 1, '12'],
  ].map(([pid, ppid, file, cpu, session, p, windows, created]) => ({ pid, ppid, file, cpu, session, path: p, desc: file === 'chrome.exe' ? 'Google Chrome' : null, windows, created }));
}
function sample(t, o) {
  const x = o || {};
  const s = x.secs || 0;   // how far the counters have run
  return {
    t, cpus: 4, idle: 0, procs: x.noProcs ? [] : procs(s), selfPid: 101, parentPid: 100,
    energy: x.noEnergy ? { ok: false, reason: 'no energy meter on this PC' } : { ok: true, time: s * 1000, pkg: PWH((x.pkgW === undefined ? 10 : x.pkgW) * s), cores: PWH(6 * s), gfx: PWH(2 * s), dram: 0, reason: null },
    meter: { ok: !!x.meter, mw: x.meter || 0, reason: null },
    gpu: x.noGpu ? { ok: false, byPid: {}, reason: 'no GPU counters' } : { ok: true, byPid: { 200: 3e6 * s, 300: 1e6 * s }, reason: null },
    battery: x.bat === 'none' ? { ok: true, present: false, percent: null, ac: true, charging: false, secondsLeft: null, capacity: null, reason: 'this machine has no battery' }
      : { ok: true, present: true, percent: x.pct === undefined ? 50 : x.pct, ac: x.bat !== 'battery', charging: x.bat !== 'battery', secondsLeft: x.bat === 'battery' ? 3600 : null,
        capacity: { remaining: x.remaining === undefined ? 50000 : x.remaining, full: 60000, rate: x.rate === undefined ? (x.bat === 'battery' ? -12000 : 5000) : x.rate }, reason: null },
    ms: 20, cpuMs: 5,
  };
}
const sum = (rows) => rows.reduce((a, r) => a + (r.share || 0), 0);

function power() {
  check('energy: 7.11 W from the 2026-11-18 probe (3,956,510,833 pWh over 2002 ms)', near(C.energyMw({ t: 0, energy: { ok: true, time: 0, pkg: 0 } }, { t: 2002, energy: { ok: true, time: 2002, pkg: 3956510833 } }).pkgMw, 7114.6, 0.5));

  // on AC: shares of the chip
  let r = C.nowReply(sample(1000), sample(11000, { secs: 10 }));
  const row = (rr, key) => rr.rows.find((x) => x.key === key);
  check('AC: source ac, shareOf chip, total null with why', r.source === 'ac' && r.shareOf === 'chip' && r.total.mw === null && r.total.why === C.WHY.ac, r.total);
  check('AC: chip 10 / 6 / 2 W, window 10 s', r.chip.pkgMw === 10000 && r.chip.coresMw === 6000 && r.chip.gfxMw === 2000 && r.windowMs === 10000, r.chip);
  check('AC: Chrome = .6 x 6 W + .75 x 2 W = 5.1 W (its two processes grouped), share .51', row(r, 'chrome.exe').mw === 5100 && near(row(r, 'chrome.exe').share, 0.51) && row(r, 'chrome.exe').name === 'Google Chrome', row(r, 'chrome.exe'));
  check('AC: Code 2.3 W, Windows 0.6 W (System), rest of the processor 2 W', row(r, 'code.exe').mw === 2300 && row(r, 'windows').mw === 600 && row(r, 'chip-rest').mw === 2000, r.rows.map((x) => [x.key, x.mw]));
  check('AC: shares sum to 1 within rounding', near(sum(r.rows), 1, 0.001), sum(r.rows));
  check('AC: the screen row has no number and says why', row(r, 'pc-rest').mw === null && row(r, 'pc-rest').share === null && row(r, 'pc-rest').why === C.WHY.ac);
  check('AC: charging 5 W is the charge rate, not a draw', r.battery.chargeMw === 5000 && r.battery.charging === true);
  check('rows: apps by share first, then Windows, rest of the processor, screen', r.rows[0].key === 'chrome.exe' && r.rows.slice(-3).map((x) => x.kind).join() === 'windows,chip-rest,pc-rest', r.rows.map((x) => x.kind));
  check('rows: Mantle (no CPU) and Explorer are apps; Mantle is named Mantle', r.rows.some((x) => x.key === 'mantle' && x.name === 'Mantle' && x.kind === 'app'));
  check('caption: the permanent estimate words + shares of the processor', r.caption.startsWith(C.CAPTION) && /processor/.test(r.caption));

  // on battery: shares of the PC
  r = C.nowReply(sample(1000, { bat: 'battery', meter: 25000 }), sample(11000, { secs: 10, bat: 'battery', meter: 25000 }));
  check('battery: total 25 W from the meter, shareOf pc', r.source === 'battery' && r.shareOf === 'pc' && r.total.mw === 25000, r.total);
  check('battery: screen and the rest = 25 - 10 W = 15 W, share .6', row(r, 'pc-rest').mw === 15000 && near(row(r, 'pc-rest').share, 0.6));
  check('battery: Chrome 5.1 / 25 W', near(row(r, 'chrome.exe').share, 0.204));
  check('battery: shares sum to 1', near(sum(r.rows), 1, 0.001), sum(r.rows));
  r = C.nowReply(sample(1000, { bat: 'battery' }), sample(11000, { secs: 10, bat: 'battery', rate: -18000 }));
  check('battery, no meter: total from the battery rate (18 W)', r.total.mw === 18000 && r.shareOf === 'pc');
  r = C.nowReply(sample(1000, { bat: 'battery' }), sample(11000, { secs: 10, bat: 'battery', rate: 0 }));
  check('battery, no reading yet: shareOf chip, the screen row says Windows has not measured it', r.shareOf === 'chip' && r.total.mw === null && row(r, 'pc-rest').why === C.WHY.notYet && near(sum(r.rows), 1, 0.001));
  r = C.nowReply(sample(1000, { bat: 'none' }), sample(11000, { secs: 10, bat: 'none' }));
  check('no battery: source nobattery, "This PC has no battery"', r.source === 'nobattery' && r.total.why === C.WHY.nobattery && r.battery.why === C.WHY.nobattery && row(r, 'pc-rest').why === C.WHY.nobattery);

  // no RAPL: CPU-time shares, no watts
  r = C.nowReply(sample(1000, { noEnergy: true }), sample(11000, { secs: 10, noEnergy: true }));
  check('no RAPL: shareOf cpu, no watts in any row', r.shareOf === 'cpu' && r.rows.every((x) => x.mw === null) && r.chip.pkgMw === null && r.chip.why === 'no energy meter on this PC', r.chip);
  check('no RAPL: Chrome .6, Code .3, Windows .1 of processor time (sum 1)', near(row(r, 'chrome.exe').share, 0.6) && near(row(r, 'code.exe').share, 0.3) && near(row(r, 'windows').share, 0.1) && near(sum(r.rows), 1, 0.001));
  check('no RAPL: the fixed rows say why', row(r, 'chip-rest').share === null && !!row(r, 'chip-rest').why && !!row(r, 'pc-rest').why);

  // no GPU counters: graphics power stays in the rest of the processor
  r = C.nowReply(sample(1000, { noGpu: true }), sample(11000, { secs: 10, noGpu: true }));
  check('no GPU counters: apps get cores only, graphics in the rest of the processor (4 W), a note', row(r, 'chrome.exe').mw === 3600 && row(r, 'chip-rest').mw === 4000 && r.notes.some((n) => /Graphics use per app/.test(n)) && near(sum(r.rows), 1, 0.001));
  // more than 8 apps: top 8 shown
  const many = (t, secs) => { const s = sample(t, { secs }); for (let i = 0; i < 12; i++) s.procs.push({ pid: 1000 + i, ppid: 60, file: `app${i}.exe`, cpu: 1e6 * (i + 1) * secs, session: 1, path: `C:\\A\\app${i}.exe`, desc: null, windows: 1, created: String(20 + i) }); return s; };
  r = C.nowReply(many(1000, 0), many(11000, 10));
  check('top 8 apps, then the three fixed rows', r.rows.filter((x) => x.kind === 'app').length === 8 && r.rows.length === 11);
  const a = C.attribute(many(1000, 0), many(11000, 10), {});
  check('the full split (all apps) still sums to 1', near(a.apps.reduce((s, x) => s + x.share, 0) + a.windows.share + a.chipRest.share, 1, 0.001));
}

function buckets() {
  // a 5-minute bucket on battery: total from the capacity drop
  // Screen time OFF (the default: no perApp, or anything but true): the totals only, no app name, no exe key
  for (const o of [{}, { perApp: false }, { perApp: 'yes' }, undefined]) {
    const t = C.makeBucket(sample(T(18, 10, 0), { bat: 'battery' }), sample(T(18, 10, 5), { secs: 300, bat: 'battery', remaining: 49000 }), o);
    check(`bucket with Screen time off (${JSON.stringify(o)}): totals only, perApp false, apps []`, t.bucket && t.bucket.perApp === false && Array.isArray(t.bucket.apps) && t.bucket.apps.length === 0
      && t.bucket.total === 12000 && t.bucket.pkg === 10000 && t.bucket.cores === 6000 && t.bucket.gfx === 2000 && t.bucket.rest === 2000, t);
    check(`bucket with Screen time off (${JSON.stringify(o)}): the stored line names no app and no exe`, !/\.exe|chrome|code|windows|"name"/i.test(JSON.stringify(t.bucket)), JSON.stringify(t.bucket));
  }
  // Screen time ON: the app rows are kept
  let b = C.makeBucket(sample(T(18, 10, 0), { bat: 'battery' }), sample(T(18, 10, 5), { secs: 300, bat: 'battery', remaining: 49000 }), { perApp: true });
  check('bucket: fields', b.bucket && JSON.stringify(Object.keys(b.bucket)) === JSON.stringify(['t', 'dur', 'pkg', 'cores', 'gfx', 'rest', 'total', 'totalHow', 'ac', 'pct', 'perApp', 'apps']) && b.bucket.perApp === true, b);
  check('bucket: 1000 mWh in 300 s = 12 W by capacity, chip 10 W', b.bucket.total === 12000 && b.bucket.totalHow === 'capacity' && b.bucket.pkg === 10000 && b.bucket.dur === 300 && b.bucket.t === T(18, 10, 0) && b.bucket.ac === false && b.bucket.pct === 50);
  check('bucket: apps [key, name, mW, cpu share], Chrome first, Windows last', b.bucket.apps[0][0] === 'chrome.exe' && b.bucket.apps[0][2] === 5100 && near(b.bucket.apps[0][3], 0.6) && b.bucket.apps[b.bucket.apps.length - 1][0] === 'windows', b.bucket.apps);
  check('bucket: rest of the processor 2 W', b.bucket.rest === 2000);
  b = C.makeBucket(sample(T(18, 10, 0), { bat: 'battery', rate: -11000 }), sample(T(18, 10, 5), { secs: 300, bat: 'battery', rate: -13000 }), { rates: [-12000] });
  check('bucket: no capacity drop -> the mean of the rate readings (12 W)', b.bucket.total === 12000 && b.bucket.totalHow === 'rate', b.bucket);
  b = C.makeBucket(sample(T(18, 10, 0), { bat: 'battery', remaining: 50000 }), sample(T(18, 10, 5), { secs: 300, bat: 'battery', remaining: 49000, rate: -9000 }), { acFlip: true });
  check('bucket: a charger flip in between -> not the capacity, the rate', b.bucket && b.bucket.totalHow === 'rate' && b.bucket.total === 10500, b);
  b = C.makeBucket(sample(T(18, 10, 0)), sample(T(18, 10, 5), { secs: 300 }), {});
  check('bucket on AC: total null, chip only', b.bucket.total === null && b.bucket.totalHow === null && b.bucket.ac === true);
  const gap = (why, o) => check(`gap: ${why}`, !!C.makeBucket(o.a, o.b, o.o).gap, C.makeBucket(o.a, o.b, o.o));
  gap('longer than 450 s', { a: sample(T(18, 10, 0)), b: sample(T(18, 10, 8), { secs: 480 }) });
  gap('shorter than 60 s', { a: sample(T(18, 10, 0)), b: sample(T(18, 10, 0, 30), { secs: 30 }) });
  gap('a suspend/resume in between', { a: sample(T(18, 10, 0)), b: sample(T(18, 10, 5), { secs: 300 }), o: { slept: true } });
  gap('the energy counter going backwards', { a: sample(T(18, 10, 0), { secs: 900 }), b: sample(T(18, 10, 5), { secs: 300 }) });
  gap('an AC flip with the capacity rising', { a: sample(T(18, 10, 0), { bat: 'battery', remaining: 40000 }), b: sample(T(18, 10, 5), { secs: 300, remaining: 41000 }) });
  check('the stored check refuses junk lines', C.checkBucket({ t: 1, dur: 300, apps: [] }) && !C.checkBucket({ t: 'x', dur: 300, apps: [] }) && !C.checkBucket(null));

  // history from buckets
  const now = T(18, 11, 30);
  const mk = (t, o) => Object.assign({ t, dur: 300, pkg: 8000, cores: 5000, gfx: 1000, rest: 2000, total: 12000, totalHow: 'capacity', ac: false, pct: 50, perApp: true,
    apps: [['c:\\p\\chrome.exe', 'Google Chrome', 4000, 0.6], ['game.exe', 'Game', 1500, 0.3], ['windows', 'Windows', 500, 0.1]] }, o || {});
  const list = [mk(T(17, 9, 0)), mk(T(18, 10, 0)), mk(T(18, 10, 5)), mk(T(18, 10, 57, 30)), mk(T(18, 11, 10), { total: null, totalHow: null, ac: true })];
  let h = C.history(list, 'today', now, new Map([['chrome.exe', 'C:\\P\\chrome.exe']]));
  check('history today: from local midnight to now, yesterday left out', h.from === T(18, 0, 0) && h.to === now && h.totals.coveredMs === 4 * 300000, h.totals);
  check('history today: 12 hours, one per local hour up to now', h.hours.length === 12 && h.hours[0].t === T(18, 0, 0) && h.hours[11].t === T(18, 11, 0), h.hours.length);
  check('history hour 10: 8 W x 12.5 min = 1.667 Wh chip, 2.5 Wh total, battery, 12.5 of 60 min', near(h.hours[10].chipWh, 1.667, 0.001) && near(h.hours[10].totalWh, 2.5, 0.001) && h.hours[10].power === 'battery' && near(h.hours[10].covered, 0.208, 0.001), h.hours[10]);
  check('history hour 11: the bucket over 11:00 is split; an AC bucket = mixed, total unknown', near(h.hours[11].chipWh, 1, 0.001) && h.hours[11].totalWh === null && h.hours[11].power === 'mixed' && near(h.hours[11].covered, 450 / 1800, 0.001), h.hours[11]);
  check('history: an empty hour has no numbers', h.hours[3].chipWh === null && h.hours[3].power === null && h.hours[3].covered === 0, h.hours[3]);
  check('history: part on mains -> shares of the chip, the screen row says why', h.shareOf === 'chip' && h.totals.totalWh === null && /Part of this time was on mains power/.test(h.totals.totalWhy) && /mains/.test(h.rows.find((x) => x.kind === 'pc-rest').why), h.totals);
  check('history: Chrome 4 W x 20 min = 1.333 Wh, share of 8 W x 20 min', near(h.rows[0].wh, 1.333, 0.001) && near(h.rows[0].share, 0.5) && h.rows[0].key === 'chrome.exe' && h.rows[0].path === 'C:\\P\\chrome.exe', h.rows[0]);
  check('history: an old path key counts under its exe name; the path only from the live map (Game is not running: null); fixed rows last', h.rows[1].key === 'game.exe' && h.rows[1].path === null && !h.rows.some((x) => /\\/.test(x.key)) && h.rows.slice(-3).map((x) => x.kind).join() === 'windows,chip-rest,pc-rest');
  check('history: shares sum to 1 (apps + Windows + rest of the processor)', near(sum(h.rows), 1, 0.001), sum(h.rows));
  h = C.history(list.slice(0, 4), '24h', now);
  check('history 24h: 24 hours from 12:00 yesterday, all on battery -> shares of the PC', h.hours.length === 24 && h.from === T(17, 12, 0) && h.shareOf === 'pc' && near(h.totals.totalWh, 3, 0.001) && h.totals.coveredMs === 3 * 300000, h.totals);
  check('history 24h: the screen and the rest = 4 W of 12 W', near(h.rows.find((x) => x.kind === 'pc-rest').share, 4 / 12, 0.001) && near(sum(h.rows), 1, 0.001));
  h = C.history([mk(T(18, 10, 0), { pkg: null, cores: null, gfx: null, rest: null, apps: [['c:\\p\\chrome.exe', 'Google Chrome', null, 0.75], ['windows', 'Windows', null, 0.25]] })], 'today', now);
  check('history without RAPL: CPU-time shares, no Wh', h.shareOf === 'cpu' && near(h.rows[0].share, 0.75) && h.rows[0].wh === null && h.totals.chipWh === null);
  h = C.history([], 'today', now);
  check('history empty: empty, worded, no rows, shareOf null', h.empty === true && /Nothing measured yet today/.test(h.emptyWhy) && h.rows.length === 0 && h.shareOf === null);
}

function cost() {
  const c = C.newCost(1000);
  c.samples = 4; c.sampleMs = 80; c.sidecarCpuMs = 40; c.nodeCpuUs = 20000; c.fgEvents = 7;
  const r = C.costReply(c, 1000 + 600000, 4, 20);
  check('cost: fields', JSON.stringify(Object.keys(r)) === JSON.stringify(['ok', 'since', 'samples', 'sampleMsAvg', 'sidecarCpuMs', 'nodeCpuMs', 'watchCpuMs', 'fgEvents', 'avgCpuPct']), Object.keys(r));
  check('cost: 80 ms of CPU in 10 min on 4 cores = 0.0033 %', r.sampleMsAvg === 20 && r.nodeCpuMs === 20 && near(r.avgCpuPct, 0.0033, 0.0001), r);
}

/* ---- files ------------------------------------------------------------------------------------ */
function files() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-usage-core-'));
  try {
    const logs = [];
    let s = createStore(path.join(dir, 'usage'), { log: (t) => logs.push(t) });
    check('store: no file yet = a fresh record, no note', JSON.stringify(s.loadScreentime()) === JSON.stringify(C.freshScreentime()) && s.notes.length === 0);
    const st = new C.ScreenTime(s.loadScreentime());
    st.apply(fg('Chrome.exe', { title: 'SECRET' }), T(18, 9, 0)); st.settle(T(18, 9, 10), 0);
    check('store: saved atomically, no tmp left', s.saveScreentime(st.toJSON()) === null && fs.existsSync(path.join(dir, 'usage', 'screentime.json')) && !fs.existsSync(path.join(dir, 'usage', 'screentime.json.tmp')));
    s = createStore(path.join(dir, 'usage'), {});
    const back = s.loadScreentime();
    check('store: read back the same', near(back.days['2026-11-18'].apps['chrome.exe'], 600) && !/SECRET|title/.test(fs.readFileSync(path.join(dir, 'usage', 'screentime.json'), 'utf8')));
    fs.writeFileSync(path.join(dir, 'usage', 'screentime.json'), '{"v":1,"apps":{},"days":{');
    s = createStore(path.join(dir, 'usage'), { log: (t) => logs.push(t) });
    const fresh = s.loadScreentime();
    check('damaged screentime.json: fresh, kept as .bad, a worded note, said in the log', Object.keys(fresh.days).length === 0 && fs.existsSync(path.join(dir, 'usage', 'screentime.json.bad')) && !fs.existsSync(path.join(dir, 'usage', 'screentime.json')) && /screentime\.json is damaged.*kept as screentime\.json\.bad/.test(s.notes[0]) && logs.length === 1, s.notes);
    check('store: clear deletes the damaged copy (no screentime* file left)', s.clearScreentime() === null && !fs.readdirSync(path.join(dir, 'usage')).some((n) => /^screentime/.test(n)));
    fs.mkdirSync(path.join(dir, 'usage', 'screentime.json'));
    s = createStore(path.join(dir, 'usage'), {});
    s.loadScreentime();
    check('an unreadable screentime.json (a folder) is moved aside, then saving works', fs.existsSync(path.join(dir, 'usage', 'screentime.json.bad')) && s.saveScreentime(C.freshScreentime()) === null, s.notes);
    fs.rmSync(path.join(dir, 'usage', 'screentime.json.bad'), { recursive: true, force: true });

    // power files: append by the bucket's local date, a damaged line, pruning after 3 days
    const now = T(18, 12, 0);
    const b = (t) => ({ t, dur: 300, pkg: 8000, cores: 5000, gfx: 1000, rest: 2000, total: null, totalHow: null, ac: true, pct: 50, apps: [] });
    s = createStore(path.join(dir, 'usage'), {});
    check('append: into power-<local date of its start>.jsonl', s.appendBucket(b(T(18, 10, 0)), now) === null && s.appendBucket(b(T(17, 23, 58)), now) === null
      && fs.readFileSync(path.join(dir, 'usage', 'power-2026-11-18.jsonl'), 'utf8').trim().split('\n').length === 1 && fs.existsSync(path.join(dir, 'usage', 'power-2026-11-17.jsonl')));
    fs.appendFileSync(path.join(dir, 'usage', 'power-2026-11-18.jsonl'), '{"t":12,"du\n' + JSON.stringify(b(T(18, 10, 5))) + '\n');
    fs.writeFileSync(path.join(dir, 'usage', 'power-2026-11-14.jsonl'), JSON.stringify(b(T(14, 10, 0))) + '\n');
    fs.writeFileSync(path.join(dir, 'usage', 'power-2026-11-14.jsonl.bad'), 'x');
    s = createStore(path.join(dir, 'usage'), {});
    const got = s.buckets(now);
    check('a damaged line is left out, the good ones used, oldest first', got.length === 3 && got[0].t === T(17, 23, 58) && got[2].t === T(18, 10, 5), got.map((x) => x.t));
    check('...the damaged line is deleted (no .bad copy: it could name apps), the file rewritten with the good lines, and the note says so', !fs.existsSync(path.join(dir, 'usage', 'power-2026-11-18.jsonl.bad')) && fs.readFileSync(path.join(dir, 'usage', 'power-2026-11-18.jsonl'), 'utf8').trim().split('\n').length === 2 && /1 damaged line was deleted$/.test(s.notes[0]), s.notes);
    const pruned = s.prune(now);
    const left = fs.readdirSync(path.join(dir, 'usage')).sort();
    check('prune: power files older than 3 local days go (their .bad too); the 16th-18th stay', pruned === 2 && !left.includes('power-2026-11-14.jsonl') && !left.includes('power-2026-11-14.jsonl.bad') && left.includes('power-2026-11-17.jsonl'), left);
    check('no stored file names a title', left.every((n) => !/title/i.test(fs.readFileSync(path.join(dir, 'usage', n), 'utf8'))));
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
}

/* Per-app power history only while Screen time is on (General Counsel, 2026-11-18; the lead's decision (b) + (c)). */
function perAppHistory() {
  const now = T(18, 11, 0);
  const on = (t) => ({ t, dur: 300, pkg: 8000, cores: 5000, gfx: 1000, rest: 2000, total: 12000, totalHow: 'capacity', ac: false, pct: 50, perApp: true,
    apps: [['chrome.exe', 'Google Chrome', 4000, 0.6], ['windows', 'Windows', 500, 0.1]] });
  const off = (t) => ({ t, dur: 300, pkg: 8000, cores: 5000, gfx: 1000, rest: 2000, total: 12000, totalHow: 'capacity', ac: false, pct: 50, perApp: false, apps: [] });
  // none: Screen time off the whole time -> totals, one "Apps and Windows" row, the reason, no app row
  let h = C.history([off(T(18, 10, 0)), off(T(18, 10, 5))], 'today', now, new Map());
  check('history, Screen time off throughout: perApp none, the one-line reason', h.perApp === 'none' && h.perAppWhy === 'Per-app history is kept only while Screen time is on' && h.perAppWhy === C.PER_APP_WHY, { perApp: h.perApp, why: h.perAppWhy });
  check('history, Screen time off throughout: no app row and no Windows row; Apps and Windows = cores + graphics (6 W x 10 min = 1 Wh)',
    !h.rows.some((r) => r.kind === 'app' || r.kind === 'windows') && h.rows[0].kind === 'apps' && h.rows[0].name === 'Apps and Windows' && near(h.rows[0].wh, 1, 0.001) && near(h.rows[0].share, 0.5, 0.001), h.rows);
  check('history, Screen time off throughout: the totals are all there (chip 1.333 Wh, PC 2 Wh)', near(h.totals.chipWh, 1.333, 0.001) && near(h.totals.totalWh, 2, 0.001) && h.shareOf === 'pc', h.totals);
  check('history, Screen time off throughout: the rows add up to the whole PC', near(h.rows.reduce((s, r) => s + (r.share || 0), 0), 1, 0.001), h.rows.map((r) => [r.kind, r.share]));
  // part: app rows from the Screen-time-on bucket only, the rest as Apps and Windows, the reason
  h = C.history([off(T(18, 10, 0)), on(T(18, 10, 5))], 'today', now, new Map());
  const ch = h.rows.find((r) => r.key === 'chrome.exe');
  check('history, part: perApp part, the reason says which rows', h.perApp === 'part' && h.perAppWhy === C.PER_APP_PART && /only the time Screen time was on/.test(h.perAppWhy) && /kept only while Screen time is on/.test(h.perAppWhy), h.perAppWhy);
  check('history, part: Chrome only from the on bucket (4 W x 5 min = 0.333 Wh); Apps and Windows = the off bucket (6 W x 5 min = 0.5 Wh)', ch && near(ch.wh, 0.333, 0.001) && near(h.rows.find((r) => r.kind === 'apps').wh, 0.5, 0.001) && h.rows.some((r) => r.kind === 'windows'), h.rows);
  // all: no reason, no Apps and Windows row
  h = C.history([on(T(18, 10, 0))], 'today', now, new Map());
  check('history, Screen time on throughout: perApp all, no reason, no Apps and Windows row', h.perApp === 'all' && h.perAppWhy === null && !h.rows.some((r) => r.kind === 'apps') && h.rows[0].key === 'chrome.exe', h);
  h = C.history([], 'today', now, new Map());
  check('history, empty: perApp null, no reason', h.empty === true && h.perApp === null && h.perAppWhy === null);
  // no energy meter (shares from CPU time): the Apps and Windows row has no number, only the reason
  const cpuOnly = Object.assign(off(T(18, 10, 0)), { pkg: null, cores: null, gfx: null, rest: null, total: null });
  h = C.history([cpuOnly], 'today', now, new Map());
  const aw = h.rows.find((r) => r.kind === 'apps');
  check('history, no energy meter + Screen time off: Apps and Windows says why, no number', h.shareOf === 'cpu' && aw && aw.share === null && aw.wh === null && aw.why === C.PER_APP_WHY, aw);
}

/* The store: Screen time off strips, Clear / Power use off deletes, the start-up migration. */
function perAppFiles() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-usage-core-'));
  const u = (n) => path.join(dir, 'usage', n);
  const NAMES_RE = /\.exe|chrome|zebra|windows|"name"/i;
  try {
    fs.mkdirSync(path.join(dir, 'usage'), { recursive: true });
    const now = T(18, 12, 0);
    const legacy = (t) => ({ t, dur: 300, pkg: 8000, cores: 5000, gfx: 1000, rest: 2000, total: null, totalHow: null, ac: true, pct: 50, apps: [['zebra.exe', 'Zebra', 3000, 0.4], ['windows', 'Windows', 500, 0.1]] });
    const onB = (t) => Object.assign(legacy(t), { perApp: true, apps: [['chrome.exe', 'Google Chrome', 3000, 0.4], ['windows', 'Windows', 500, 0.1]] });
    // the migration: a file from before the rule, plus a leftover .bad and .tmp copy that name apps
    fs.writeFileSync(u('power-2026-11-17.jsonl'), JSON.stringify(legacy(T(17, 10, 0))) + '\n' + JSON.stringify(legacy(T(17, 10, 5))) + '\n');
    fs.writeFileSync(u('power-2026-11-17.jsonl.bad'), JSON.stringify(legacy(T(17, 9, 0))) + '\n');
    fs.writeFileSync(u('power-2026-11-18.jsonl.tmp'), JSON.stringify(legacy(T(18, 9, 0))) + '\n');
    fs.writeFileSync(u('power-2026-11-18.jsonl'), JSON.stringify(onB(T(18, 10, 0))) + '\n');
    let s = createStore(path.join(dir, 'usage'), {});
    const n = s.migrate(now);
    const t17 = fs.readFileSync(u('power-2026-11-17.jsonl'), 'utf8');
    check('migration: the old file keeps both lines with their totals, perApp false, apps []', n === 3 && t17.trim().split('\n').map((l) => JSON.parse(l)).every((b) => b.perApp === false && b.apps.length === 0 && b.pkg === 8000), t17);
    check('migration: no app name or exe key is left in the old file', !NAMES_RE.test(t17), t17);
    check('migration: the leftover .bad and .tmp copies are deleted', !fs.existsSync(u('power-2026-11-17.jsonl.bad')) && !fs.existsSync(u('power-2026-11-18.jsonl.tmp')), fs.readdirSync(path.join(dir, 'usage')));
    check('migration: a line recorded with Screen time on keeps its rows (Screen time is on here)', /chrome\.exe/.test(fs.readFileSync(u('power-2026-11-18.jsonl'), 'utf8')));
    // Screen time switched off: the per-app rows already stored go, the totals stay, memory too
    fs.writeFileSync(u('power-2026-11-18.jsonl.bad'), 'chrome.exe');
    check('strip: -> null (all done)', s.stripPower() === null, s.notes);
    const t18 = fs.readFileSync(u('power-2026-11-18.jsonl'), 'utf8');
    check('strip: the Screen-time-on line keeps its totals but not its apps', JSON.parse(t18).perApp === false && JSON.parse(t18).apps.length === 0 && JSON.parse(t18).pkg === 8000 && !NAMES_RE.test(t18), t18);
    check('strip: a power .bad copy is deleted (it could name apps)', !fs.existsSync(u('power-2026-11-18.jsonl.bad')));
    check('strip: the buckets in memory lost their apps too', s.buckets(now).every((b) => b.perApp === false && b.apps.length === 0) && s.buckets(now).length === 3);
    const all = fs.readdirSync(path.join(dir, 'usage')).map((x) => fs.readFileSync(u(x), 'utf8')).join('\n');
    check('strip: no power file on disk names an app or an exe', !NAMES_RE.test(all), all);
    // Clear power history: every power* file, any copy, at once
    fs.writeFileSync(u('power-2026-11-18.jsonl.tmp'), 'x');
    fs.writeFileSync(u('power-2026-11-16.jsonl.bad'), 'x');
    fs.writeFileSync(u('power-backup.jsonl'), 'x');
    fs.writeFileSync(u('screentime.json'), JSON.stringify(C.freshScreentime()));
    check('clearPower: -> null', s.clearPower() === null);
    const left = fs.readdirSync(path.join(dir, 'usage'));
    check('clearPower: every power file and copy is deleted; screentime.json is not touched', !left.some((x) => /^power/i.test(x)) && left.includes('screentime.json'), left);
    check('clearPower: nothing stays in memory', s.buckets(now).length === 0);
    // a file Windows will not let go of: the words come back, stay in the notes, and leave at the next good pass
    fs.writeFileSync(u('power-2026-11-18.jsonl'), JSON.stringify(onB(T(18, 10, 0))) + '\n');
    const realRm = fs.rmSync;
    fs.rmSync = function (p, ...rest) { if (/power-2026-11-18\.jsonl$/.test(String(p))) throw Object.assign(new Error('busy'), { code: 'EBUSY' }); return realRm.call(fs, p, ...rest); };
    let why;
    try { why = s.clearPower(); } finally { fs.rmSync = realRm; }
    check('clearPower refused: the words name the file and the reason, and a note says so', /^Power history was not fully deleted: power-2026-11-18\.jsonl \(EBUSY\)/.test(why || '') && s.notes.includes(why), { why, notes: s.notes });
    check('clearPower again: done, and the old note is gone', s.clearPower() === null && !s.notes.some((x) => /not fully deleted/.test(x)) && !fs.existsSync(u('power-2026-11-18.jsonl')), s.notes);
    check('clearPower with no folder: -> null',createStore(path.join(dir, 'nope'), {}).clearPower() === null);
    check('stripPower with no folder: -> null', createStore(path.join(dir, 'nope'), {}).stripPower() === null);
    // a power file with nothing usable left is deleted by the strip, not left empty
    fs.writeFileSync(u('power-2026-11-18.jsonl'), '{"t":1\n');
    s = createStore(path.join(dir, 'usage'), {});
    check('strip: a file of damaged lines only is deleted', s.stripPower() === null && !fs.existsSync(u('power-2026-11-18.jsonl')), fs.readdirSync(path.join(dir, 'usage')));
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
}

for (const [name, fn] of [['screen time', screenTime], ['privacy', privacy], ['mantle rule', mantleRule], ['power', power], ['buckets', buckets], ['cost', cost], ['files', files], ['per-app history', perAppHistory], ['per-app files', perAppFiles]]) {
  try { fn(); } catch (e) { failures.push(`${name} threw: ${e && e.stack}`); }
}
for (const x of failures) console.log('  FAIL ' + x);
console.log(`USAGE CORE TEST: ${passed} passed, ${failures.length} failed`);
process.exit(failures.length ? 1 : 0);
