require('../../../tools/dev-env.js').lowcpu();
/* screentime.test.js — Mantle Settings > Screen time (lane mantle-ps-st, 2026-11-18).
 *
 * PART 1 (node): the page file loads in an empty vm (only MantleSettings.section exists, as the launcher's
 * claims sandbox runs it) with the right id / group / order / search rows and passes ui/settings/model.js
 * checkSection; its pure half (require of the same file: durations, day names, the app list, the 7-day and
 * by-hour charts, the paused line, the clear words, the privacy line, the state picker) is pinned; the
 * source never reads a `title` field and re-reads through kit.every (the kit skips it while hidden).
 * PART 2 (headless Chromium over file://, never Electron, never a real setting): the REAL page file on the
 * settings mock, usage.* played by mock-mantle.js (?usage=<state>). Every reply's apps, days and current
 * app carry a hidden `title` trap: the page must never read it. Checks: loading, the feature off, recording
 * off, empty, a failed read with Try again, notes, the switch's read-back and a refusal springing back,
 * Clear history's confirm step (Keep it sends nothing; Clear sends clear, re-reads and says how many days),
 * picking a day by click and by keyboard, icons asked once per path with a neutral glyph while missing, and
 * the 60 s re-read never running while the window is hidden. Both themes. Shots: MANTLE_SHOOT_OUT, default
 * screentime-shots in the tools/dev-env.js scratch folder (else the temp folder).
 *
 *   node ui/settings/sections/screentime.test.js            Prints "SCREEN TIME TEST: n passed, m failed".
 *   SCREENTIME_NODE_ONLY=1 node ui/settings/sections/...    PART 1 only (no browser).
 */
'use strict';
const path = require('path');
const fs = require('fs');
const os = require('os');
const vm = require('vm');

const FILE = path.join(__dirname, 'screentime.js');
const PAGE = 'file:///' + path.resolve(__dirname, '..', 'index.html').replace(/\\/g, '/');
let passed = 0;
const failures = [];
const check = (name, ok, detail) => { if (ok) passed++; else failures.push(detail != null ? `${name} (${String(detail).slice(0, 300)})` : name); return ok; };

function nodeTests() {
  const src = fs.readFileSync(FILE, 'utf8');
  let def = null;
  try { vm.runInNewContext(src, { MantleSettings: { section: (d) => { def = d; } } }, { timeout: 2000 }); } catch (e) { failures.push('the page file threw while loading: ' + e.message); }
  check('page: defines its section while loading', !!def);
  if (def) {
    check('page: id screentime, title Screen time, Mantle group, before Keyboard shortcuts', def.id === 'screentime' && def.title === 'Screen time' && def.group === 'mantle' && def.order > 1006 && def.order < 1010,
      JSON.stringify({ id: def.id, group: def.group, order: def.order }));
    const rows = (def.search || []).map((s) => s.row);
    check('page: search rows for the switch, today, the week and Clear history', ['screentime.record', 'screentime.today', 'screentime.week', 'screentime.clear'].every((r) => rows.includes(r)), rows.join(','));
    const words = (def.search || []).map((s) => s.label + ' ' + s.keywords).join(' ') + ' ' + def.keywords;
    check('page: searchable by app time / usage / foreground / history', ['app time', 'usage', 'screen time', 'foreground', 'history'].every((w) => words.includes(w)));
    check('page: one page drawn directly', typeof def.render === 'function' && !def.pages);
    try {
      const why = require(path.join(__dirname, '..', 'model.js')).checkSection(def, 'screentime');
      check('page: passes model.checkSection', why == null, why);
    } catch (e) { failures.push('model.js did not load: ' + e.message); }
  }
  check('source: never reads a title field', !/\.title\b|\[\s*['"`]title['"`]\s*\]|\btitle\s*\}\s*=|\{\s*[^}]*\btitle\b[^}]*\}\s*=/.test(src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '')));
  check('source: re-reads every 60 s through kit.every (skipped while hidden)', /kit\.every\(\s*60000\s*,\s*update\s*\)/.test(src));
  check('source: no colour literals', !/#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/i.test(src.replace(/\/\*[\s\S]*?\*\//g, '')));
  check('source: no animation or transition', !/animation|transition|animate\(/i.test(src.replace(/\/\*[\s\S]*?\*\//g, '')));

  let M;
  try { M = require(FILE); } catch (e) { failures.push('the page file did not load in node: ' + e.message); return; }
  // durations
  const D = [[0, '0 min'], [null, '0 min'], [-5, '0 min'], [1, 'under a minute'], [59, 'under a minute'], [60, '1 min'], [89, '1 min'], [90, '2 min'],
    [480, '8 min'], [3569, '59 min'], [3570, '1 h'], [3600, '1 h'], [4320, '1 h 12 min'], [7200, '2 h'], [11520, '3 h 12 min'], [36000 + 60, '10 h 1 min']];
  for (const [s, want] of D) check(`dur(${s}) = "${want}"`, M.dur(s) === want, M.dur(s));
  // days
  check('dayName: today / yesterday / weekday date', M.dayName('2026-11-18', '2026-11-18') === 'Today' && M.dayName('2026-11-17', '2026-11-18') === 'Yesterday' && M.dayName('2026-11-16', '2026-11-18') === 'Mon 16 Nov',
    [M.dayName('2026-11-18', '2026-11-18'), M.dayName('2026-11-17', '2026-11-18'), M.dayName('2026-11-16', '2026-11-18')].join('|'));
  check('dayName: across a month and a year', M.dayName('2026-10-31', '2026-11-02') === 'Sat 31 Oct' && M.dayName('2025-12-31', '2026-01-01') === 'Yesterday');
  check('dayShort: Today, else the weekday', M.dayShort('2026-11-18', '2026-11-18') === 'Today' && M.dayShort('2026-11-12', '2026-11-18') === 'Thu');
  check('date: a bad date is null', M.date('2026-02-31') === null && M.date('nope') === null && M.date(undefined) === null);
  check('hourName: 12 am, 1 pm, 12 pm, 11 pm', M.hourName(0) === '12 am' && M.hourName(13) === '1 pm' && M.hourName(12) === '12 pm' && M.hourName(23) === '11 pm' && M.hourName(24) === '12 am');
  // app list
  const apps = [{ key: 'b', name: 'Browser', path: 'C:\\b.exe', seconds: 4320 }, { key: 'e', name: 'Editor', path: 'C:\\e.exe', seconds: 5400 },
    { key: 'z', name: 'Zero', path: null, seconds: 0 }, { key: 's', name: '', path: null, seconds: 40 }];
  const rows = M.appRows(apps, 11520);
  check('appRows: most time first, nothing at 0 s', rows.map((r) => r.key).join(',') === 'e,b,s', rows.map((r) => r.key).join(','));
  check('appRows: share of the day total, text, a nameless app', rows[0].pct === 47 && rows[0].text === '1 h 30 min' && rows[2].name === 'An app' && rows[2].text === 'under a minute', JSON.stringify(rows[0]));
  check('appRows: a total smaller than the apps uses their sum', M.appRows([{ key: 'a', name: 'A', seconds: 60 }, { key: 'b', name: 'B', seconds: 60 }], 30)[0].pct === 50);
  check('appRows: junk in, empty out', M.appRows(null, 0).length === 0 && M.appRows([null, 5, 'x'], 0).length === 0);
  // a title in the data never comes out
  let reads = 0;
  const trap = { key: 'k', name: 'N', path: null, seconds: 120 };
  Object.defineProperty(trap, 'title', { enumerable: false, get() { reads++; return 'SECRET WINDOW TITLE'; } });
  const out = M.appRows([trap], 120);
  check('appRows: never reads or passes on a title', reads === 0 && !JSON.stringify(out).includes('SECRET') && !('title' in out[0]));
  // the week
  const days = ['2026-11-12', '2026-11-13', '2026-11-14', '2026-11-15', '2026-11-16', '2026-11-17', '2026-11-18'].map((date, i) => ({ date, totalSec: [7200, 9000, 0, 12600, 5400, 10800, 11520][i], apps: [] }));
  const w = M.week(days, '2026-11-18', null);
  check('week: 7 columns, oldest left, today marked and picked by default', w.cols.length === 7 && w.cols[0].date === '2026-11-12' && w.cols[6].today && w.cols[6].selected && w.selected === '2026-11-18' && w.cols.filter((c) => c.selected).length === 1);
  check('week: heights are of the biggest day', w.cols[3].frac === 1 && w.cols[2].frac === 0 && Math.abs(w.cols[0].frac - 7200 / 12600) < 1e-9);
  check('week: labels', w.cols[0].label === 'Thu' && w.cols[6].label === 'Today' && w.cols[5].full === 'Yesterday' && w.cols[2].text === '0 min');
  check('week: the summary names every day', w.aria === 'Screen time, last 7 days: Thu 12 Nov 2 h; Fri 13 Nov 2 h 30 min; Sat 14 Nov 0 min; Sun 15 Nov 3 h 30 min; Mon 16 Nov 1 h 30 min; Yesterday 3 h; Today 3 h 12 min', w.aria);
  check('week: a picked day stays picked', M.week(days, '2026-11-18', '2026-11-14').cols[2].selected && M.week(days, '2026-11-18', '2026-11-14').selected === '2026-11-14');
  check('week: a picked day that left the week falls back to today', M.week(days, '2026-11-18', '2026-11-01').selected === '2026-11-18');
  check('week: nothing to chart', M.week([], '2026-11-18', null).cols.length === 0 && /nothing recorded/.test(M.week(null, null, null).aria));
  // by hour
  const hrs = Array(24).fill(0); hrs[10] = 3000; hrs[9] = 2400;
  const hm = M.hours(hrs);
  check('hours: 24 columns, labels every 6 hours, the busiest hour said', hm.cols.length === 24 && hm.cols[0].label === '12 am' && hm.cols[6].label === '6 am' && hm.cols[12].label === '12 pm' && hm.cols[1].label === ''
    && hm.cols[10].frac === 1 && hm.aria === 'Today by hour: most time 10 am to 11 am (50 min)', hm.aria);
  check('hours: nothing yet', M.hours(null).aria === 'Today by hour: nothing yet' && M.hours([]).cols.every((c) => c.frac === 0));
  // the quiet line
  check('nowLine: recording the current app', M.nowLine({ ok: true, on: true, paused: null, current: { key: 'k', name: 'Code Editor' } }) === 'Recording now: Code Editor');
  check('nowLine: paused locked / asleep / idle (with its minutes)', M.nowLine({ ok: true, on: true, paused: 'locked' }) === 'Paused: the PC is locked' && M.nowLine({ ok: true, on: true, paused: 'asleep' }) === 'Paused: the PC is asleep'
    && M.nowLine({ ok: true, on: true, paused: 'idle', idleMin: 7 }) === 'Paused: no input for 7 min');
  check('nowLine: off says nothing is being recorded', /^Nothing is being recorded/.test(M.nowLine({ ok: true, on: false, paused: 'off' })) && /^Nothing is being recorded/.test(M.nowLine({ ok: true, on: true, paused: 'off' })));
  check('nowLine: no answer, no line', M.nowLine({ ok: false, reason: 'x' }) === null && M.nowLine(null) === null);
  // clear words
  check('cleared: days in words', M.cleared({ ok: true, cleared: 12 }) === 'Cleared 12 days of screen time' && M.cleared({ ok: true, cleared: 1 }) === 'Cleared 1 day of screen time'
    && M.cleared({ ok: true, cleared: 0 }) === 'There was no screen time to clear' && M.cleared({ ok: true }) === 'Cleared your screen time history');
  check('cleared: a dry run says so, a failure is not a result', M.cleared({ ok: true, dryRun: true, reason: 'Test run: nothing was cleared' }) === 'Test run: nothing was cleared' && M.cleared({ ok: false, reason: 'x' }) === null);
  // privacy
  check('privacy: the promise, with the idle minutes and kept days', M.privacy(5, 30) === 'Recorded only on this PC and never sent anywhere. Only app names and times are kept, never window titles. Kept for 30 days. Time while the PC is locked, asleep, or idle (no input for 5 min) doesn’t count.'
    && /no input for 9 min/.test(M.privacy(9)) && /Kept for 30 days/.test(M.privacy(undefined, undefined)));
  // states
  check('view: loading / feature off / error / empty / ok', M.view(undefined).kind === 'loading' && M.view({ ok: false, off: true, reason: 'off' }).kind === 'feature-off'
    && M.view({ ok: false, reason: 'boom' }).kind === 'error' && M.view({ ok: false, reason: 'boom' }).reason === 'boom' && M.view({ ok: false }).reason === 'no answer came back'
    && M.view({ ok: true, on: true, today: { totalSec: 0 }, days: [{ date: 'x', totalSec: 0 }] }).kind === 'empty'
    && M.view({ ok: true, on: true, today: { totalSec: 0 }, days: [{ date: 'x', totalSec: 60 }] }).kind === 'ok');
  check('view: recording off keeps the history', M.view({ ok: true, on: false, paused: 'off', today: { totalSec: 600 }, days: [] }).kind === 'ok' && M.view({ ok: true, on: false, paused: 'off', today: { totalSec: 600 }, days: [] }).recording === false);
}

/* Runs IN THE PAGE (addInitScript): lists only the screentime file, and plants a hidden `title` trap in every
   usage.screentime.get reply (apps, days, today, current). The usage.* answers are mock-mantle.js's own. */
function TRAP() {
  window.__titleReads = 0;
  window.__getAt = [];
  const plant = (o) => {
    if (o && typeof o === 'object') Object.defineProperty(o, 'title', { enumerable: false, configurable: true, get() { window.__titleReads++; return 'SECRET WINDOW TITLE'; } });
    return o;
  };
  let real;
  Object.defineProperty(window, 'mantle', {
    configurable: true,
    get() { return real; },
    set(m) {
      real = { on: m.on.bind(m), call(name, args) {
        if (name === 'settings.sections') return Promise.resolve({ ok: true, reason: null, sections: [{ id: 'screentime', file: 'sections/screentime.js', down: null, main: null }] });
        if (name === 'usage.screentime.get') window.__getAt.push(Date.now());
        return m.call(name, args).then((r) => {
          if (name === 'usage.screentime.get' && r && r.ok) {
            plant(r); plant(r.today); plant(r.current);
            (r.today && r.today.apps || []).forEach(plant);
            (r.days || []).forEach((d) => { plant(d); (d.apps || []).forEach(plant); });
          }
          return r;
        });
      } };
    },
  });
}

async function browserTests() {
  const { chromium } = require('../../../tools/dev-env.js').playwright();
  const OUT = process.env.MANTLE_SHOOT_OUT || path.join(require('../../../tools/dev-env.js').scratch() || os.tmpdir(), 'screentime-shots');
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await require('../test-browser.js').launch(chromium);
  try {
    for (const theme of ['light', 'dark']) {
      const T = theme;
      const ctx = await browser.newContext({ viewport: { width: 1040, height: 1100 }, deviceScaleFactor: 1 });
      await ctx.addInitScript(TRAP);
      const open = async (scn, extra, o) => {
        const page = await ctx.newPage();
        const problems = [];
        page.on('pageerror', (e) => problems.push('pageerror: ' + e.message));
        page.on('console', (m) => { if (m.type() === 'error') problems.push('console: ' + m.text()); });
        if (o && o.clock) await page.clock.install();
        await page.goto(PAGE + `?section=screentime&usage=${scn}&theme=${theme}${extra || ''}`, { waitUntil: 'load' });
        try { await page.waitForSelector('html[data-ready="1"]', { timeout: 6000 }); } catch (_) { problems.push('never reached data-ready'); }
        if (!(o && o.early)) await page.waitForTimeout(400);
        return { page, problems };
      };
      const row = (p, id) => p.locator(`[data-row="screentime.${id}"]`);
      const txt = (p, id) => row(p, id).first().textContent();
      const sw = (p) => row(p, 'record').locator('button.sw');
      const calls = (p, n) => p.evaluate((nn) => (window.__mockCalls || []).filter((c) => c.name === nn).map((c) => c.args), n);
      const all = (p) => p.evaluate(() => (document.getElementById('page') || document.body).textContent);
      const shown = (p, sel) => p.evaluate((s) => { const e = document.querySelector(s); return !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length); }, sel);
      const appNames = (p, list) => p.locator(`[data-list="${list}"] .st-app .row-t`).allTextContents();
      const appVals = (p, list) => p.locator(`[data-list="${list}"] .st-app .val`).allTextContents();
      const PRIV = 'Recorded only on this PC and never sent anywhere. Only app names and times are kept, never window titles. Kept for 30 days. Time while the PC is locked, asleep, or idle (no input for 5 min) doesn’t count.';

      // ---- normal: today, the week, icons, picking a day, the switch, clear
      let { page, problems } = await open('normal');
      check(`${T}: the switch is on and live, with the current app`, (await sw(page).getAttribute('aria-checked')) === 'true' && !(await sw(page).isDisabled()) && /Recording now: Code Editor/.test(await txt(page, 'record')));
      check(`${T}: the privacy line, word for word`, (await txt(page, 'privacy')).includes(PRIV), await txt(page, 'privacy'));
      check(`${T}: today's total`, /3 h 12 min/.test(await txt(page, 'today')), await txt(page, 'today'));
      const names = await appNames(page, 'today'), vals = await appVals(page, 'today');
      check(`${T}: today's apps, most time first`, names.join('|') === 'Code Editor|Example Browser|Tunes|Chatter|File Explorer|Sketchpad', names.join('|'));
      check(`${T}: today's times in words`, vals.join('|') === '1 h 30 min|1 h 12 min|20 min|8 min|1 min|under a minute', vals.join('|'));
      check(`${T}: a static share bar per app, its share said`, (await page.locator('[data-list="today"] .st-share').count()) === 6 && (await page.locator('[data-list="today"] .st-share').first().getAttribute('aria-label')) === '47% of today');
      check(`${T}: today by hour, its busiest hour said`, (await page.locator('.st-hours').getAttribute('aria-label')) === 'Today by hour: most time 10 am to 11 am (50 min)');
      const ic = await calls(page, 'usage.icons');
      const asked = ic.flatMap((c) => c.paths);
      check(`${T}: icons asked once per path, never for an app with no path`, asked.length === new Set(asked).size && asked.length === 5 && !asked.includes(null), JSON.stringify(ic));
      const tiles = page.locator('[data-list="today"] .st-app .tile');
      check(`${T}: an icon where there is one, the neutral glyph (with why) where there is none`, (await tiles.nth(0).locator('img').count()) === 1 && (await tiles.nth(2).locator('svg').count()) === 1
        && /No icon: made up/.test((await tiles.nth(2).getAttribute('title')) || '') && (await tiles.nth(5).locator('svg').count()) === 1);
      const cols = page.locator('.st-week [data-date]');
      const hourText = await page.locator('.st-hours').textContent();
      check(`${T}: the by-hour chart has its hour labels and bars`, /12 am/.test(hourText) && /6 pm/.test(hourText) && (await page.locator('.st-hours [data-h]').count()) === 24, hourText);
      const barH = await page.evaluate(() => [...document.querySelectorAll('.st-week [data-date]')].map((b) => { const bar = b.firstElementChild && b.firstElementChild.firstElementChild; return bar ? Math.round(bar.getBoundingClientRect().height) : -1; }));
      check(`${T}: each day column draws its bar, the biggest day tallest, an empty day flat`, barH.length === 7 && barH[3] === Math.max(...barH) && barH[3] > 60 && barH[2] === 0 && barH.every((x) => x >= 0), barH.join(','));
      check(`${T}: 7 day columns, today last and picked`, (await cols.count()) === 7 && (await cols.nth(6).getAttribute('aria-pressed')) === 'true' && /Today/.test(await cols.nth(6).textContent()));
      const aria = (await page.locator('.st-week').getAttribute('aria-label')) || '';
      check(`${T}: the week chart has a summary`, /^Screen time, last 7 days: /.test(aria) && /Today 3 h 12 min$/.test(aria) && /Yesterday 3 h;/.test(aria), aria);
      check(`${T}: the picked day's apps (today)`, /Today/.test(await txt(page, 'day')) && (await appNames(page, 'day')).length === 6);
      await page.screenshot({ path: path.join(OUT, `st-normal-${T}.png`), fullPage: true });
      await cols.nth(0).click();
      await page.waitForTimeout(100);
      check(`${T}: clicking a day picks it and lists its apps`, (await cols.nth(0).getAttribute('aria-pressed')) === 'true' && (await cols.nth(6).getAttribute('aria-pressed')) === 'false'
        && /2 h/.test(await txt(page, 'day')) && (await appNames(page, 'day'))[0] === 'Code Editor' && (await appVals(page, 'day'))[0] === '48 min', (await txt(page, 'day')) + ' ' + (await appVals(page, 'day')).join('|'));
      await cols.nth(0).focus();
      await page.keyboard.press('ArrowRight');
      await page.keyboard.press('ArrowRight');
      await page.waitForTimeout(100);
      check(`${T}: arrow keys pick the next day, and a day with nothing says so`, (await page.locator('.st-week [aria-pressed="true"]').getAttribute('data-date')) === (await cols.nth(2).getAttribute('data-date'))
        && /0 min/.test(await txt(page, 'day')) && /Nothing recorded that day/.test(await page.locator('[data-list="day"]').textContent())
        && (await page.evaluate(() => document.activeElement && document.activeElement.dataset.date)) === (await cols.nth(2).getAttribute('data-date')));
      await page.keyboard.press('End');
      await page.waitForTimeout(100);
      check(`${T}: End picks today`, (await cols.nth(6).getAttribute('aria-pressed')) === 'true');
      // the switch: off, read back
      await sw(page).click();
      await page.waitForTimeout(300);
      const st = (await calls(page, 'usage.screentime.set')).pop();
      check(`${T}: the switch sends set {on:false} and reads back off`, st && st.on === false && (await sw(page).getAttribute('aria-checked')) === 'false' && /Nothing is being recorded/.test(await txt(page, 'record')), JSON.stringify(st));
      check(`${T}: recording off still shows the history`, /3 h 12 min/.test(await txt(page, 'today')) && await shown(page, '.st-content'));
      // clear: the confirm step
      const clearBtn = row(page, 'clear').locator('button.btn');
      check(`${T}: Clear history is live`, !(await clearBtn.isDisabled()));
      await clearBtn.click();
      await page.waitForTimeout(100);
      check(`${T}: Clear history asks first, in the page`, await shown(page, '[data-row="screentime.clear.confirm"]') && /can’t be undone/.test(await txt(page, 'clear.confirm')) && (await calls(page, 'usage.screentime.clear')).length === 0);
      await page.screenshot({ path: path.join(OUT, `st-confirm-${T}.png`), fullPage: true });
      await row(page, 'clear.confirm').locator('button', { hasText: 'Keep it' }).click();
      await page.waitForTimeout(100);
      check(`${T}: Keep it closes the question and clears nothing`, !(await shown(page, '[data-row="screentime.clear.confirm"]')) && (await calls(page, 'usage.screentime.clear')).length === 0);
      const getsBefore = (await calls(page, 'usage.screentime.get')).length;
      await clearBtn.click();
      await page.waitForTimeout(100);
      await row(page, 'clear.confirm').locator('button', { hasText: 'Clear history' }).click();
      await page.waitForTimeout(500);
      check(`${T}: Clear sends clear once, re-reads, and says how many days`, (await calls(page, 'usage.screentime.clear')).length === 1 && (await calls(page, 'usage.screentime.get')).length > getsBefore
        && /Cleared 12 days of screen time/.test(await txt(page, 'clear')), await txt(page, 'clear'));
      check(`${T}: after clearing, the empty state`, await shown(page, '[data-row="screentime.empty"]') && !(await shown(page, '.st-content')));
      check(`${T}: no window title was read or shown`, (await page.evaluate(() => window.__titleReads)) === 0 && !(await all(page)).includes('SECRET'));
      check(`${T}: normal: no page errors`, problems.length === 0, problems.join(' | '));
      await page.close();

      // ---- idle, with a note
      ({ page, problems } = await open('idle'));
      check(`${T}: idle is said with its minutes, and a note is shown`, /Paused: no input for 5 min/.test(await txt(page, 'record')) && (await all(page)).includes('yesterday from 2 pm to 4 pm was not recorded'));
      await page.close();

      // ---- recording off from the start
      ({ page, problems } = await open('recoff'));
      check(`${T}: recording off: the switch is off and says nothing is recorded, the history stays`, (await sw(page).getAttribute('aria-checked')) === 'false' && !(await sw(page).isDisabled())
        && /Nothing is being recorded/.test(await txt(page, 'record')) && /3 h 12 min/.test(await txt(page, 'today')));
      await page.screenshot({ path: path.join(OUT, `st-recoff-${T}.png`), fullPage: true });
      await page.close();

      // ---- empty
      ({ page, problems } = await open('empty'));
      check(`${T}: empty: the words, no charts, nothing to clear`, (await txt(page, 'empty')).includes('No screen time yet: it builds up as you use apps') && !(await shown(page, '.st-content'))
        && (await row(page, 'clear').locator('button.btn').isDisabled()) && /Nothing is kept yet/.test(await txt(page, 'clear')));
      check(`${T}: empty: the privacy line still shows`, (await txt(page, 'privacy')).includes(PRIV));
      await page.screenshot({ path: path.join(OUT, `st-empty-${T}.png`), fullPage: true });
      await page.close();

      // ---- the feature off in the tray
      ({ page, problems } = await open('off'));
      const to = await all(page);
      check(`${T}: feature off: its reason and where the switch is`, to.includes('Power use and screen time is switched off in Mantle’s features') && to.includes('tray menu > Features'));
      check(`${T}: feature off: the switch and Clear are locked, no charts, the privacy line stays`, (await sw(page).isDisabled()) && (await row(page, 'clear').locator('button.btn').isDisabled())
        && !(await shown(page, '.st-content')) && (await txt(page, 'privacy')).includes(PRIV));
      await page.screenshot({ path: path.join(OUT, `st-off-${T}.png`), fullPage: true });
      await page.close();

      // ---- a failed read, Try again
      ({ page, problems } = await open('fail'));
      check(`${T}: a failed read says why and offers Try again`, await shown(page, '[data-row="screentime.retry"]') && /the screen time file could not be read/.test(await txt(page, 'retry')) && (await sw(page).isDisabled()) && !(await shown(page, '.st-content')));
      await page.screenshot({ path: path.join(OUT, `st-fail-${T}.png`), fullPage: true });
      await page.close();
      ({ page, problems } = await open('failonce'));
      const g0 = (await calls(page, 'usage.screentime.get')).length;
      await row(page, 'retry').locator('button.btn').click();
      await page.waitForTimeout(400);
      check(`${T}: Try again reads again and draws the page`, (await calls(page, 'usage.screentime.get')).length === g0 + 1 && !(await shown(page, '[data-row="screentime.retry"]')) && /3 h 12 min/.test(await txt(page, 'today')));
      await page.close();

      // ---- a refused switch springs back
      ({ page, problems } = await open('refuse'));
      await sw(page).click();
      await page.waitForTimeout(400);
      check(`${T}: a refused switch springs back with its reason`, (await sw(page).getAttribute('aria-checked')) === 'true' && /could not save the switch/.test(await txt(page, 'record')), await txt(page, 'record'));
      await page.close();

      // ---- a test copy: the switch and clear are dry runs
      ({ page, problems } = await open('normal', '&test=1'));
      await sw(page).click();
      await page.waitForTimeout(300);
      check(`${T}: a test copy: the switch springs back with the dry run's words`, (await sw(page).getAttribute('aria-checked')) === 'true' && /Test run: nothing was changed/.test(await txt(page, 'record')));
      await page.close();

      // ---- loading
      ({ page, problems } = await open('slow', '', { early: true }));
      check(`${T}: loading is said and the switch waits`, (await all(page)).includes('Reading your screen time…') && (await sw(page).isDisabled()));
      await page.close();

      // ---- the 60 s re-read: while visible, never while hidden
      if (T === 'light') {
        let pc = null;
        try { pc = await open('normal', '', { clock: true }); } catch (e) { pc = null; console.log('SKIP the 60 s re-read check: this playwright has no page.clock (' + e.message.split('\n')[0] + ')'); }
        if (pc) {
          const n0 = (await calls(pc.page, 'usage.screentime.get')).length;
          await pc.page.clock.fastForward(61000); await pc.page.waitForTimeout(200);
          const n1 = (await calls(pc.page, 'usage.screentime.get')).length;
          await pc.page.evaluate(() => Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }));
          await pc.page.clock.fastForward(61000); await pc.page.waitForTimeout(200);
          await pc.page.clock.fastForward(61000); await pc.page.waitForTimeout(200);
          const n2 = (await calls(pc.page, 'usage.screentime.get')).length;
          check('re-reads after 60 s while visible', n1 === n0 + 1, `${n0} -> ${n1}`);
          check('never re-reads while hidden', n2 === n1, `${n1} -> ${n2}`);
          await pc.page.close();
        }
      }
      await ctx.close();
    }
  } finally { await browser.close(); }
  console.log(`shots in ${process.env.MANTLE_SHOOT_OUT || 'the screentime-shots folder'}`);
}

(async () => {
  try { nodeTests(); } catch (e) { failures.push('part 1 threw: ' + e.message.split('\n')[0]); }
  if (process.env.SCREENTIME_NODE_ONLY !== '1') { try { await browserTests(); } catch (e) { failures.push('part 2 threw: ' + e.message.split('\n')[0]); } }
  failures.forEach((f) => console.log('FAIL ' + f));
  console.log(`SCREEN TIME TEST: ${passed} passed, ${failures.length} failed`);
  process.exitCode = failures.length ? 1 : 0;
})();
