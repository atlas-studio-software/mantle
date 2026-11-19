'use strict';
/* qs-power.test.js - the pure half of ui/bar/qs-power.js (lane mantle-ps-qs, 2026-11-18): every state's
 * words and numbers, from the reply shapes in the power-pull design's addendum ("Command replies").
 *   node ui/bar/qs-power.test.js      -> "QS POWER TEST: n passed, m failed"
 * Pure node: no browser, no Electron. */
const P = require('./qs-power.js');

let passed = 0;
const fails = [];
function check(name, ok, got) {
  if (ok) passed++;
  else fails.push(name + (got !== undefined ? ' (got ' + JSON.stringify(got) + ')' : ''));
}
const JARGON = /undefined|null|NaN|\[object|usage\.|mw\b|pkg/i;

// ---- formatters
check('watts 7800 -> 7.8 W', P.watts(7800) === '7.8 W', P.watts(7800));
check('watts 18000 -> 18 W', P.watts(18000) === '18 W', P.watts(18000));
check('watts 200 -> 0.2 W', P.watts(200) === '0.2 W', P.watts(200));
check('watts 9960 -> 10 W, never "10.0 W"', P.watts(9960) === '10 W', P.watts(9960));
check('watts tiny -> <0.1 W', P.watts(30) === '<0.1 W', P.watts(30));
check('watts 0 -> 0 W', P.watts(0) === '0 W');
check('watts null -> empty', P.watts(null) === '' && P.watts(undefined) === '');
check('approxWatts 2600 -> ~2.6 W', P.approxWatts(2600) === '~2.6 W', P.approxWatts(2600));
check('approxWatts null -> empty', P.approxWatts(null) === '');
check('pct 0.34 -> 34 %', P.pct(0.34) === '34 %', P.pct(0.34));
check('pct 0.001 -> <1 %', P.pct(0.001) === '<1 %');
check('pct 1.2 caps at 100 %', P.pct(1.2) === '100 %');
check('pct null -> empty', P.pct(null) === '');
check('timeLeft 3 h 40 min', P.timeLeft(13200) === 'about 3 h 40 min left', P.timeLeft(13200));
check('timeLeft whole hours', P.timeLeft(7200) === 'about 2 h left', P.timeLeft(7200));
check('timeLeft minutes', P.timeLeft(25 * 60) === 'about 25 min left', P.timeLeft(25 * 60));
check('timeLeft unknown -> empty', P.timeLeft(null) === '' && P.timeLeft(0) === '');
check('approxWh 0.8 -> ~0.8 Wh', P.approxWh(0.8) === '~0.8 Wh', P.approxWh(0.8));
check('wh 12.34 -> 12 Wh', P.wh(12.34) === '12 Wh', P.wh(12.34));
check('wh 4.25 -> 4.3 Wh', P.wh(4.25) === '4.3 Wh' || P.wh(4.25) === '4.2 Wh', P.wh(4.25));
check('hourLabel', /^\d\d:00$/.test(P.hourLabel(Date.UTC(2026, 10, 18, 9))), P.hourLabel(Date.UTC(2026, 10, 18, 9)));

// ---- Now: the reply shapes
const rows = [
  { key: 'c:\\code.exe', kind: 'app', name: 'Visual Studio Code', path: 'C:\\code.exe', share: 0.34, mw: 2600, why: null },
  { key: 'c:\\quartz.exe', kind: 'app', name: 'Quartz', path: 'C:\\quartz.exe', share: 0.12, mw: null, why: null },
  { key: 'windows', kind: 'windows', name: 'Windows', path: null, share: 0.08, mw: 600, why: null },
  { key: 'chip-rest', kind: 'chip-rest', name: 'Rest of the processor', path: null, share: 0.1, mw: 800, why: null },
  { key: 'pc-rest', kind: 'pc-rest', name: 'Screen and the rest of the PC', path: null, share: 0.36, mw: 2800, why: null }
];
const base = {
  ok: true, at: 1, windowMs: 2000,
  chip: { pkgMw: 4100, coresMw: 3000, gfxMw: 200, why: null },
  rows, caption: 'Estimated: an app\'s share of processor and graphics time times the measured power.', notes: []
};
function now(over) { return P.nowView(Object.assign({}, base, over)); }

let v = now({ source: 'battery', shareOf: 'pc', battery: { percent: 60, charging: false, chargeMw: null, secondsLeft: 13200, why: null }, total: { mw: 7800, why: null } });
check('battery: big total', v.kind === 'ok' && v.big === '7.8 W', v.big);
check('battery: line with time left', v.line === 'On battery \u00b7 about 3 h 40 min left', v.line);
check('battery: no AC sentence', v.sub === '', v.sub);
check('battery: processor line', v.chip.text === 'Processor 4.1 W \u00b7 Graphics 0.2 W' && !v.chip.why, v.chip);
check('battery: share of the whole PC', v.shareOf === "Share of the whole PC's draw", v.shareOf);
check('battery: app row words', v.rows[0].name === 'Visual Studio Code' && v.rows[0].pct === '34 %' && v.rows[0].amount === '~2.6 W' && v.rows[0].share === 0.34, v.rows[0]);
check('battery: watts omitted when null', v.rows[1].pct === '12 %' && v.rows[1].amount === '', v.rows[1]);
check('battery: fixed rows kept in order', v.rows.map((r) => r.kind).join() === 'app,app,windows,chip-rest,pc-rest');
check('battery: caption from the reply', v.caption === base.caption);
check('battery: no jargon in any word', !JARGON.test([v.big, v.line, v.sub, v.chip.text, v.shareOf].concat(v.rows.map((r) => r.name + r.pct + r.amount + r.why)).join(' ')));

v = now({ source: 'battery', shareOf: 'pc', battery: { percent: 60, charging: false, chargeMw: null, secondsLeft: null, why: null }, total: { mw: 7800, why: null } });
check('battery, time unknown: nothing about time', v.line === 'On battery', v.line);

v = now({ source: 'battery', shareOf: 'chip', battery: { percent: 60, charging: false, secondsLeft: null }, total: { mw: null, why: 'the battery has not reported a rate yet' } });
check('battery, no rate: no big number, the reason', v.big === '' && /the battery has not reported a rate yet/.test(v.sub), v);

const acRows = rows.slice(0, 4).concat([{ key: 'pc-rest', kind: 'pc-rest', name: 'Screen and the rest of the PC', path: null, share: null, mw: null, why: 'Not measured while plugged in' }]);
v = now({ source: 'ac', shareOf: 'chip', rows: acRows, battery: { percent: 80, charging: true, chargeMw: 18000, secondsLeft: null }, total: { mw: null, why: 'Windows only reports total draw on battery' } });
check('AC charging: "Charging at 18 W"', v.line === 'Charging at 18 W' && v.big === '', v);
check('AC charging: the total sentence', v.sub === 'Windows only reports total draw on battery', v.sub);
check('AC charging: processor line still there', v.chip.text === 'Processor 4.1 W \u00b7 Graphics 0.2 W');
check('AC: share of the processor', v.shareOf === "Share of the processor's power");
const pcRest = v.rows[4];
check('AC: the screen row says why, no share', pcRest.share === null && pcRest.pct === '' && pcRest.amount === '' && pcRest.why === 'Not measured while plugged in', pcRest);

v = now({ source: 'ac', shareOf: 'chip', battery: { percent: 100, charging: false, chargeMw: null }, total: { mw: null, why: null } });
check('AC not charging', v.line === 'Plugged in, not charging' && v.sub === 'Windows only reports total draw on battery', v);

v = now({ source: 'ac', shareOf: 'chip', battery: { percent: 50, charging: true, chargeMw: null }, total: { mw: null } });
check('AC charging, rate unknown', v.line === 'Plugged in, charging', v.line);

v = now({ source: 'nobattery', shareOf: 'chip', battery: { percent: null, charging: false, why: 'no battery' }, total: { mw: null, why: null } });
check('no battery: the words', v.line === 'This PC has no battery' && v.big === '', v);
check('no battery: processor line', v.chip.text === 'Processor 4.1 W \u00b7 Graphics 0.2 W');

v = now({ source: 'ac', shareOf: 'cpu', battery: { charging: false }, total: { mw: null },
  chip: { pkgMw: null, coresMw: null, gfxMw: null, why: 'This processor does not report its power to Windows' },
  rows: [{ key: 'a', kind: 'app', name: 'App', path: null, share: 0.5, mw: null }] });
check('no RAPL: the reason instead of the processor line', v.chip.why && v.chip.text === 'This processor does not report its power to Windows', v.chip);
check('no RAPL: share of processor time', v.shareOf === 'Share of processor time (power not measurable here)');
check('no RAPL: share, no watts', v.rows[0].pct === '50 %' && v.rows[0].amount === '');

v = now({ source: 'ac', shareOf: 'chip', battery: {}, total: {}, chip: { pkgMw: null, gfxMw: null, why: null } });
check('chip all null, no why: still worded', v.chip.why && /not measured/.test(v.chip.text), v.chip);

v = now({ source: 'unknown', shareOf: 'chip', battery: { why: 'the battery could not be read: access denied' }, total: {} });
check('unknown source: worded with the reason', v.line === 'Power source not known' && /access denied/.test(v.sub), v);

v = now({ source: 'battery', shareOf: 'pc', battery: {}, total: { mw: 7000 }, caption: '', notes: ['Sampled twice 1 s apart', '', null] });
check('no caption in the reply: the estimate caption still shows', v.caption === P.CAPTION);
check('notes: blanks dropped', v.notes.length === 1 && v.notes[0] === 'Sampled twice 1 s apart', v.notes);

v = P.nowView({ ok: false, off: true, reason: 'Power use and screen time is off in the tray menu' });
check('off: kind and reason', v.kind === 'off' && v.why === 'Power use and screen time is off in the tray menu' && /off/.test(v.title), v);
v = P.nowView({ ok: false, off: true });
check('off with no reason: still worded', v.kind === 'off' && v.why.length > 10);
v = P.nowView({ ok: false, reason: 'the sidecar is restarting' });
check('error: kind, title, reason', v.kind === 'error' && v.why === 'the sidecar is restarting' && /Could not read/.test(v.title), v);
v = P.nowView({ ok: false });
check('error with no reason: says so', v.kind === 'error' && v.why === 'Mantle gave no reason.');
check('no reply at all: an error', P.nowView(null).kind === 'error');

// ---- history
const T0 = new Date(2026, 10, 18, 9).getTime();
const H = (i, chipWh, totalWh, power, covered) => ({ t: T0 + i * 3600e3, chipWh, totalWh, power, covered });
const hist = {
  ok: true, range: 'today', from: T0, to: T0 + 4 * 3600e3, shareOf: 'chip',
  hours: [H(0, 4.2, null, 'ac', 1), H(1, 2.0, 6.5, 'battery', 1), H(2, 1.0, 2.5, 'mixed', 0.5), H(3, 0, null, 'ac', 0)],
  rows: [{ key: 'a', kind: 'app', name: 'Quartz', path: 'C:\\q.exe', share: 0.3, wh: 0.8 }, { key: 'windows', kind: 'windows', name: 'Windows', path: null, share: 0.1, wh: null }],
  totals: { chipWh: 7.2, totalWh: null, totalWhy: 'Windows only reports total draw on battery', coveredMs: 1, spanMs: 2 },
  empty: false, emptyWhy: null, caption: 'Estimated.', notes: []
};
v = P.historyView(hist);
check('history: ok, four columns', v.kind === 'ok' && v.bars.length === 4, v.kind);
check('history: scaled to the tallest (6.5 Wh total)', v.max === 6.5 && Math.abs(v.bars[1].totalH - 1) < 1e-9 && Math.abs(v.bars[0].chipH - 4.2 / 6.5) < 1e-9, v.max);
check('history: total only where on battery', v.bars[0].totalWh === null && v.bars[1].totalWh === 6.5 && v.bars[2].totalWh === 2.5);
check('history: partly covered hours marked', !v.bars[0].part && v.bars[2].part && v.bars[3].part && v.bars[3].none);
check('history: aria summary', /09:00 to 12:00/.test(v.aria) && /most at 09:00, 4.2 Wh/.test(v.aria) && /2 hours on battery/.test(v.aria) && /2 hours were only partly recorded/.test(v.aria), v.aria);
check('history: totals line', v.totals === 'Processor 7.2 Wh', v.totals);
check('history: total why', v.totalsWhy === 'Windows only reports total draw on battery', v.totalsWhy);
check('history: app row "~0.8 Wh"', v.rows[0].pct === '30 %' && v.rows[0].amount === '~0.8 Wh', v.rows[0]);
check('history: Wh omitted when null', v.rows[1].amount === '' && v.rows[1].pct === '10 %');
check('history: share line', v.shareOf === "Share of the processor's power");
check('history: tips worded', /processor 4.2 Wh/.test(v.bars[0].tip) && /partly recorded/.test(v.bars[2].tip) && /not recorded/.test(v.bars[3].tip), v.bars.map((b) => b.tip));
v = P.historyView(Object.assign({}, hist, { totals: { chipWh: 7.2, totalWh: 9, totalWhy: null } }));
check('history: total Wh when known', v.totals === 'Processor 7.2 Wh \u00b7 Whole PC 9.0 Wh' && v.totalsWhy === '', v.totals);
v = P.historyView({ ok: true, range: '24h', hours: [], rows: [], totals: {}, empty: true, emptyWhy: null, caption: 'Estimated.', notes: [] });
check('history empty: the default words', v.kind === 'empty' && v.why === 'No history yet: Mantle records every 5 minutes', v);
v = P.historyView({ ok: true, hours: [], empty: true, emptyWhy: 'Power history is off in Mantle Settings' });
check('history empty: its own reason', v.why === 'Power history is off in Mantle Settings');
v = P.historyView({ ok: false, reason: 'the history file could not be read' });
check('history error', v.kind === 'error' && v.why === 'the history file could not be read' && /power history/.test(v.title), v);
v = P.historyView({ ok: false, off: true, reason: 'off in the tray' });
check('history off', v.kind === 'off' && v.why === 'off in the tray');
// per-app rows only while Screen time is on: the reply's one-line reason is drawn as it is
v = P.historyView(Object.assign({}, hist, { perApp: 'none', perAppWhy: 'Per-app history is kept only while Screen time is on',
  rows: [{ key: 'apps', kind: 'apps', name: 'Apps and Windows', path: null, share: 0.6, wh: 1.2, why: null }] }));
check('history, Screen time off: the reason line, and Apps and Windows as a row', v.perAppWhy === 'Per-app history is kept only while Screen time is on' && v.rows.length === 1 && v.rows[0].kind === 'apps' && v.rows[0].name === 'Apps and Windows' && v.rows[0].amount === '~1.2 Wh', v);
v = P.historyView(hist);
check('history, Screen time on throughout: no reason line', v.perAppWhy === '', v.perAppWhy);
// Clear power history's answer in words
check('cleared: some readings', JSON.stringify(P.clearedView({ ok: true, cleared: 12 })) === JSON.stringify({ ok: true, said: 'Power history cleared' }));
check('cleared: nothing kept', P.clearedView({ ok: true, cleared: 0 }).said === 'There was no power history to clear');
check('cleared: refused, with the reason', P.clearedView({ ok: false, reason: 'a file is in use' }).ok === false && P.clearedView({ ok: false, reason: 'a file is in use' }).said === 'Nothing was cleared: a file is in use');
check('cleared: no answer', P.clearedView(null).said === 'Nothing was cleared: no answer came back');

for (const f of fails) console.log('  FAIL ' + f);
console.log('QS POWER TEST: ' + passed + ' passed, ' + fails.length + ' failed');
process.exitCode = fails.length ? 1 : 0;
