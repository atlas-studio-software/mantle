require('../../tools/dev-env.js').lowcpu();
'use strict';
/* qspower.page.test.js - lane mantle-ps-qs (2026-11-18): "Power use" in quick settings CLICKED in the real
 * bar (ui/bar/qs-power.js). Headless Chromium over file:// with the mock bridge (ui/bar/mock-mantle.js,
 * ?pw= / ?pwstate= / ?pwh= and __mockMain.usage()); nothing real is touched.
 *   node ui/bar/qspower.page.test.js        (through the heavy-test queue / cloudtest: one browser)
 * Covers: the row (shown, off, usage.state failing), Now on battery / AC charging / AC full / no battery /
 * no processor counters / error + Try again / feature off / a refresh failing under numbers, the 2 s
 * re-read and every way it stops (tab change, back, panel close), Today and Last 24 h with data, empty
 * and failing history, the tabs by keyboard, and back to the main view. */
const H = require('./page-harness.js');

const JARGON = (s) => /sidecar|\bIPC\b|undefined|\bnull\b|NaN|\[object|usage\.|\bmw\b|pkgMw/i.test(s);
const POLL = 2000;

(async () => {
  const t = H.suite('QS POWER PAGE TEST');
  if (H.gaming && H.gaming()) { console.log('a game is running: skipped'); process.exit(0); }
  await H.withBrowser(async (open) => {
    const qs = async (q) => { const pg = await open('?mode=pill' + (q ? '&' + q : ''), { h: 900 }); await pg.click('#network', { wait: 400 }); return pg; };
    const view = async (q) => { const pg = await qs(q); await pg.click('.qspw-open', { wait: 400 }); return pg; };
    const nowCalls = async (pg) => (await pg.calls('usage.power.now')).length;
    const rows = (pg) => pg.eval(() => Array.from(document.querySelectorAll('.qspw-view .qspw-row')).map((r) => {
      const vis = (sel) => { const e = r.querySelector(sel); return !!e && !e.hidden && getComputedStyle(e).display !== 'none'; };
      return { kind: r.dataset.kind, name: r.querySelector('.qspw-name').textContent, pct: r.querySelector('.qspw-pct').textContent,
        amt: r.querySelector('.qspw-amt').textContent, why: r.querySelector('.qspw-why').textContent, bar: vis('.qspw-bar'),
        width: r.querySelector('.qspw-fill').style.width, img: !!r.querySelector('.qspw-ico img'), icoTitle: r.querySelector('.qspw-ico').title };
    }));
    const sum = (pg) => pg.eval(() => {
      const txt = (sel) => { const e = document.querySelector('.qspw-view ' + sel); return e && !e.hidden ? e.textContent : ''; };
      return { big: txt('.qspw-big'), line: txt('.qspw-line'), sub: txt('.qspw-sum .qspw-subline'), chip: txt('.qspw-chip'), of: txt('.qspw-of'),
        cap: txt('.qspw-cap'), state: txt('.qspw-state'), notes: txt('.qspw-notes') };
    });
    const allText = (pg) => pg.eval(() => (document.querySelector('.qspw-view') || {}).textContent || '');

    // 1) the row, and Now on battery
    let pg = await qs('');
    t.check('row: shown when on && power', await pg.visible('.qspw-open'));
    t.check('row: "Power use" + subtitle', /Power use/.test(await pg.text('.qspw-open')) && /What is using power/.test(await pg.text('.qspw-osub')), await pg.text('.qspw-open'));
    t.check('row: asked usage.state once', (await pg.calls('usage.state')).length === 1);
    t.check('row: nothing read before it is clicked', (await nowCalls(pg)) === 0);
    await pg.click('.qspw-open', { wait: 400 });
    t.check('view: open, main view hidden', await pg.visible('.qspw-view') && !(await pg.visible('.qspw-open')) && await pg.eval(() => document.querySelector('.pop.qs').classList.contains('qsx-sub')));
    t.check('view: header says Power use', (await pg.text('.qspw-view .qsx-title')) === 'Power use');
    t.check('view: three tabs, Now selected', await pg.eval(() => Array.from(document.querySelectorAll('.qspw-tab')).map((b) => b.textContent + ':' + b.getAttribute('aria-selected')).join('|')) === 'Now:true|Today:false|Last 24 h:false');
    let s = await sum(pg);
    t.check('battery: big total 7.8 W', s.big === '7.8 W', s);
    t.check('battery: on battery + time left', s.line === 'On battery · about 3 h 40 min left', s.line);
    t.check('battery: processor line', s.chip === 'Processor 4.1 W · Graphics 0.2 W', s.chip);
    t.check('battery: share of the whole PC', s.of === "Share of the whole PC's draw", s.of);
    t.check('battery: the estimate caption', /^Estimated/.test(s.cap), s.cap);
    let rs = await rows(pg);
    t.check('battery: 3 apps then 3 fixed rows', rs.map((r) => r.kind).join() === 'app,app,app,windows,chip-rest,pc-rest', rs.map((r) => r.kind));
    t.check('battery: first row words', rs[0].name === 'Quartz' && rs[0].pct === '34 %' && rs[0].amt === '~2.6 W' && rs[0].width === '34%', rs[0]);
    t.check('battery: icon drawn for Quartz, neutral glyph + reason for Helm', rs[0].img && !rs[1].img && /^No icon: /.test(rs[1].icoTitle), [rs[0], rs[1]]);
    t.check('icons asked once for the three apps', (await pg.calls('usage.icons')).length === 1 && (await pg.calls('usage.icons'))[0].p.paths.length === 3);
    t.check('share bars are static (no transition, no animation)', await pg.eval(() => {
      const f = document.querySelector('.qspw-fill'); const c = getComputedStyle(f);
      return c.transitionDuration.split(',').every((d) => parseFloat(d) === 0) && c.animationName === 'none';
    }));
    t.check('battery: no jargon', !JARGON(await allText(pg)), await allText(pg));
    let n0 = await nowCalls(pg);
    await pg.wait(POLL * 2 + 600);
    let n1 = await nowCalls(pg);
    t.check('Now re-reads every 2 s while shown', n1 - n0 >= 2 && n1 - n0 <= 3, { n0, n1 });
    t.check('icons not asked again on refresh', (await pg.calls('usage.icons')).length === 1);
    t.check('room asked for the sub-view', (await pg.calls('bar.popover')).some((c) => c.p && c.p.open && c.p.key === 'qs'));

    // 2) the tabs by keyboard: arrows move, Enter selects
    await pg.eval(() => document.querySelector('.qspw-tab[data-tab="now"]').focus());
    await pg.key('ArrowRight');
    t.check('ArrowRight moves the focus, not the selection', await pg.eval(() => document.activeElement.dataset.tab === 'today' && document.querySelector('.qspw-tab[data-tab="now"]').getAttribute('aria-selected') === 'true'));
    await pg.key('Enter', 500);
    t.check('Enter selects Today', await pg.eval(() => document.querySelector('.qspw-tab[data-tab="today"]').getAttribute('aria-selected') === 'true'));
    let hc = await pg.calls('usage.power.history');
    t.check('Today: one history read, range today', hc.length === 1 && hc[0].p.range === 'today', hc);
    n0 = await nowCalls(pg);
    await pg.wait(POLL + 800);
    t.check('Today: the Now re-read stopped', (await nowCalls(pg)) === n0);
    t.check('Today: history read only once', (await pg.calls('usage.power.history')).length === 1);
    let ch = await pg.eval(() => {
      const c = document.querySelector('.qspw-chart');
      return c && { cols: c.querySelectorAll('.qspw-col').length, part: c.querySelectorAll('.qspw-col.part').length, tot: c.querySelectorAll('.qspw-tot').length,
        aria: c.getAttribute('aria-label'), role: c.getAttribute('role') };
    });
    t.check('Today: six hourly columns, one partly recorded', ch && ch.cols === 6 && ch.part === 1, ch);
    t.check('Today: whole-PC bars where on battery', ch && ch.tot >= 1, ch);
    t.check('Today: chart has a summary for screen readers', ch && ch.role === 'img' && /^Power by hour/.test(ch.aria), ch);
    t.check('Today: totals line', (await pg.text('.qspw-totals')) === 'Processor 18 Wh', await pg.text('.qspw-totals'));
    rs = await rows(pg);
    t.check('Today: app rows with ~Wh', rs[1].name === 'Helm' && rs[1].pct === '18 %' && rs[1].amt === '~0.8 Wh', rs[1]);
    t.check('Today: no jargon', !JARGON(await allText(pg)), await allText(pg));

    // 3) Last 24 h by click
    await pg.click('.qspw-tab[data-tab="24h"]', { wait: 400 });
    hc = await pg.calls('usage.power.history');
    t.check('Last 24 h: read with range 24h', hc.length === 2 && hc[1].p.range === '24h', hc.map((c) => c.p));
    t.check('Last 24 h: 24 columns', (await pg.count('.qspw-col')) === 24, await pg.count('.qspw-col'));
    t.check('Last 24 h, Screen time on: no per-app reason line', !(await pg.visible('.qspw-perapp')));

    // 3b) Screen time off: totals, "Apps and Windows", the one-line reason; then Clear power history
    await pg.eval(() => window.__mockMain.usage({ pwh: 'totals' }));
    await pg.click('.qspw-tab[data-tab="today"]', { wait: 400 });
    t.check('Screen time off: the reason line', (await pg.text('.qspw-perapp')) === 'Per-app history is kept only while Screen time is on', await pg.text('.qspw-perapp'));
    rs = await rows(pg);
    t.check('Screen time off: no app rows, Apps and Windows as one total', rs.length === 2 && rs[0].name === 'Apps and Windows' && !rs.some((r) => /Quartz|Helm/.test(r.name)), rs);
    t.check('Clear: the button is there, nothing asked yet', await pg.visible('.qspw-clearbtn') && !(await pg.visible('.qspw-confirm')) && (await pg.calls('usage.power.clear')).length === 0);
    await pg.click('.qspw-clearbtn', { wait: 200 });
    t.check('Clear asks first, in the view', await pg.visible('.qspw-confirm') && (await pg.eval(() => document.querySelector('.qspw-confirm').getAttribute('role'))) === 'alertdialog'
      && /can\u2019t be undone/.test(await pg.text('.qspw-confirm')) && (await pg.calls('usage.power.clear')).length === 0, await pg.text('.qspw-clear'));
    t.check('Clear: Keep it has the focus', await pg.eval(() => document.activeElement && document.activeElement.classList.contains('qspw-keep')));
    await pg.click('.qspw-keep', { wait: 200 });
    t.check('Keep it: the question closes, nothing cleared', !(await pg.visible('.qspw-confirm')) && await pg.visible('.qspw-clearbtn') && (await pg.calls('usage.power.clear')).length === 0);
    await pg.eval(() => window.__mockMain.usage({ pwclear: 'fail' }));
    await pg.click('.qspw-clearbtn', { wait: 200 });
    await pg.click('.qspw-clearyes', { wait: 400 });
    t.check('Clear refused: the reason stays on screen', /Nothing was cleared: Power history was not fully deleted/.test(await pg.text('.qspw-cleared.bad')) && (await pg.calls('usage.power.clear')).length === 1, await pg.text('.qspw-clear'));
    await pg.eval(() => window.__mockMain.usage({ pwclear: 'ok' }));
    const hBefore = (await pg.calls('usage.power.history')).length;
    await pg.click('.qspw-clearbtn', { wait: 200 });
    await pg.click('.qspw-clearyes', { wait: 500 });
    t.check('Clear: sent once more, the tab re-read', (await pg.calls('usage.power.clear')).length === 2 && (await pg.calls('usage.power.history')).length === hBefore + 1);
    t.check('Clear: the history is empty and says it was cleared', /No power history/.test(await pg.text('.qspw-state')) && (await pg.text('.qspw-cleared')) === 'Power history cleared', await pg.text('.qspw-view'));
    await pg.eval(() => window.__mockMain.usage({ pwh: 'data' }));

    // 4) back to Now restarts the re-read; back to the main view stops it
    await pg.click('.qspw-tab[data-tab="now"]', { wait: 400 });
    t.check('Now again: numbers back', (await sum(pg)).big === '7.8 W');
    await pg.click('.qspw-view .qsx-back', { wait: 300 });
    t.check('back: the main view again', !(await pg.visible('.qspw-view')) && await pg.visible('.qspw-open') && !(await pg.eval(() => document.querySelector('.pop.qs').classList.contains('qsx-sub'))));
    n0 = await nowCalls(pg);
    await pg.wait(POLL + 800);
    t.check('back: the re-read stopped', (await nowCalls(pg)) === n0);
    t.check('no page errors (battery run)', pg.errors.length === 0, pg.errors);
    await pg.close();

    // 5) closing the panel stops the re-read
    pg = await view('');
    await pg.key('Escape', 300);
    t.check('Escape closed the panel', (await pg.eval(() => window.__barDebug && window.__barDebug.popKey())) !== 'qs');
    n0 = await nowCalls(pg);
    await pg.wait(POLL + 800);
    t.check('panel closed: the re-read stopped', (await nowCalls(pg)) === n0, { n0, n1: await nowCalls(pg) });
    t.check('no page errors (close)', pg.errors.length === 0, pg.errors);
    await pg.close();

    // 6) AC charging, AC full, no battery, no processor counters
    pg = await view('pw=ac');
    s = await sum(pg);
    t.check('AC: no big total', s.big === '', s);
    t.check('AC: "Charging at 18 W"', s.line === 'Charging at 18 W', s.line);
    t.check('AC: total only on battery', s.sub === 'Windows only reports total draw on battery', s.sub);
    t.check('AC: processor line kept', s.chip === 'Processor 4.1 W · Graphics 0.2 W', s.chip);
    t.check('AC: share of the processor', s.of === "Share of the processor's power", s.of);
    rs = await rows(pg);
    const rest = rs.find((r) => r.kind === 'pc-rest');
    t.check('AC: the screen row says why, no share or bar', rest && rest.pct === '' && !rest.bar && /Not measured while plugged in/.test(rest.why), rest);
    await pg.close();

    pg = await view('pw=acfull');
    t.check('AC full: "Plugged in, not charging"', (await sum(pg)).line === 'Plugged in, not charging');
    await pg.close();

    pg = await view('pw=nobattery');
    s = await sum(pg);
    t.check('no battery: the words + processor line', s.line === 'This PC has no battery' && s.chip === 'Processor 4.1 W · Graphics 0.2 W', s);
    await pg.close();

    pg = await view('pw=norapl');
    s = await sum(pg);
    t.check('no RAPL: the reason instead of the processor line', s.chip === 'This processor does not report its power to Windows', s.chip);
    t.check('no RAPL: share of processor time', s.of === 'Share of processor time (power not measurable here)', s.of);
    rs = await rows(pg);
    t.check('no RAPL: shares, no watts', rs[0].pct === '34 %' && rs[0].amt === '', rs[0]);
    t.check('no page errors (states)', pg.errors.length === 0, pg.errors);
    await pg.close();

    // 7) error + Try again; off; a refresh failing under numbers
    pg = await view('pw=error');
    s = await sum(pg);
    t.check('error: the reason in words', /Could not read power use/.test(s.state) && /the power counters could not be read/.test(s.state), s.state);
    t.check('error: Try again offered', await pg.visible('.qspw-retry'));
    n0 = await nowCalls(pg);
    await pg.wait(POLL + 800);
    t.check('error: no re-read chain behind it', (await nowCalls(pg)) === n0);
    await pg.eval(() => window.__mockMain.usage({ pw: 'battery' }));
    await pg.click('.qspw-retry', { wait: 400 });
    t.check('Try again: numbers after a good read', (await sum(pg)).big === '7.8 W', await sum(pg));
    await pg.close();

    pg = await view('pw=off');
    s = await sum(pg);
    t.check('off: says it is switched off, and where', /switched off/.test(s.state) && /tray menu/.test(s.state), s.state);
    t.check('off: no Try again', !(await pg.visible('.qspw-retry')));
    await pg.close();

    pg = await view('pw=flaky');
    await pg.wait(POLL + 600);
    s = await sum(pg);
    t.check('refresh failed: the numbers stay', s.big === '7.8 W', s);
    t.check('refresh failed: a note says why', /The last refresh failed: the power counters could not be read/.test(s.notes), s.notes);
    await pg.close();

    // 8) history: empty, failing (+ Try again)
    pg = await view('pwh=empty');
    await pg.click('.qspw-tab[data-tab="today"]', { wait: 400 });
    s = await sum(pg);
    t.check('empty history: the words', /No history yet: Mantle records every 5 minutes/.test(s.state), s.state);
    t.check('empty history: still captioned', /^Estimated/.test(s.cap), s.cap);
    await pg.close();

    pg = await view('pwh=error');
    await pg.click('.qspw-tab[data-tab="24h"]', { wait: 400 });
    s = await sum(pg);
    t.check('history error: reason + Try again', /Could not read power history/.test(s.state) && /not valid JSON/.test(s.state) && await pg.visible('.qspw-retry'), s.state);
    await pg.eval(() => window.__mockMain.usage({ pwh: 'data' }));
    await pg.click('.qspw-retry', { wait: 400 });
    t.check('history Try again: the chart', (await pg.count('.qspw-col')) === 24);
    t.check('no page errors (history)', pg.errors.length === 0, pg.errors);
    await pg.close();

    // 9) the row: switched off, usage.state failing
    pg = await qs('pwstate=off');
    t.check('row: hidden when the user turned it off', !(await pg.visible('.qspw-open')) && (await pg.count('.qspw-open')) === 1);
    await pg.close();
    pg = await qs('pwstate=fail');
    t.check('row: usage.state failing shows the reason', await pg.visible('.qspw-open') && /Could not check if it is on: the usage feature did not answer/.test(await pg.text('.qspw-osub')), await pg.text('.qspw-osub'));
    t.check('no page errors (row)', pg.errors.length === 0, pg.errors);
    await pg.close();
  });
  t.done();
})().catch((e) => { console.log('QS POWER PAGE TEST: 0 passed, 1 failed'); console.log('  FAIL ' + (e && e.stack || e)); process.exitCode = 1; });
