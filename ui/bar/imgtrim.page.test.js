require('../../tools/dev-env.js').lowcpu();
/* imgtrim.page.test.js - THE PICTURE CACHE (ui/bar/bar.js, lane mantle-slim-bar, 2026-11-06): the bar drops
 * Chromium's cache of the pictures it has shown a moment after a pop-up closes or the bar hides, only when
 * that cache holds more than 8 MB, never while a pop-up is open, at most once per 20 s; a bridge that
 * cannot is said once and then left alone. The mock's window.mantle.imageCache records each ask and answers
 * from window.__MOCK_IMAGE_CACHE__ (what Electron's webFrame measures is tools/bar-mem-probe.js's job).
 *
 *   node ui/bar/imgtrim.page.test.js   -> "IMGTRIM PAGE TEST: n passed, m failed"
 */
'use strict';
const H = require('./page-harness.js');
const t = H.suite('IMGTRIM PAGE TEST');

const MB = 1048576;
const FULL = { count: 140, bytes: 21 * MB };

async function run() {
  if (H.gaming()) { console.log('a game is running: the browser half is skipped'); t.check('not skipped (a game was running)', false); return; }
  await H.withBrowser(async (open) => {
    const W = { w: 1280, h: 720 };
    const Q = '?mode=pill&media=playing&spq=queue';
    const asks = async (pg) => (await pg.calls('imageCache')).map((c) => c.p);
    const state = (pg) => pg.eval(() => JSON.parse(JSON.stringify(window.__barImgTrim || null)));
    const fill = (pg, c) => pg.eval((x) => { window.__MOCK_IMAGE_CACHE__ = x; }, c);
    const popOpen = (pg) => pg.eval(() => !document.getElementById('pop').hidden);

    /* ---- a pop-up closing, the cache over the line */
    const pg = await open(Q, W);
    await fill(pg, FULL);
    t.check('nothing is asked while the bar just sits there', (await asks(pg)).length === 0, await asks(pg));
    await pg.click('#mchip', { wait: 400 });
    t.check('the media card is open', await popOpen(pg));
    await pg.wait(2200);
    t.check('nothing is asked while a pop-up is open', (await asks(pg)).length === 0, await asks(pg));
    await pg.click('#mchip', { wait: 300 });
    t.check('the card is shut', !(await popOpen(pg)));
    await pg.wait(700);
    t.check('...not asked at once (it waits a moment after the close)', (await asks(pg)).length === 0, await asks(pg));
    await pg.wait(1300);
    let a = await asks(pg);
    t.check('a moment after the close: ONE ask, to clear over 8 MB', a.length === 1 && a[0] && a[0].clearOver === 8 * MB, a);
    let s = await state(pg);
    t.check('...the cache over the line was cleared, and the bar counted it', s && s.cleared === 1 && s.runs === 1 && s.freedBytes === FULL.bytes && s.last && s.last.cleared === true && s.last.why === 'a pop-up closed', s);
    t.check('...the mock cache is empty now', (await pg.eval(() => window.__MOCK_IMAGE_CACHE__.bytes)) === 0);
    t.check('...the chip still shows its art', (await pg.eval(() => { const i = document.querySelector('#mchip .mart img'); return !!i && i.complete && i.naturalWidth > 0; })));

    /* ---- at most once per 20 s: a close 3 s later waits for the gap */
    await fill(pg, FULL);
    await pg.click('#mchip', { wait: 400 });
    await pg.click('#mchip', { wait: 300 });
    await pg.wait(2500);
    t.check('a second close inside 20 s is not asked at once', (await asks(pg)).length === 1, await asks(pg));
    await pg.wait(16500);
    a = await asks(pg);
    t.check('...but is asked once the 20 s are up', a.length === 2, a);

    /* ---- under the line: asked, nothing cleared */
    await fill(pg, { count: 12, bytes: 2 * MB });
    await pg.wait(20500 - 2500);
    await pg.click('#mchip', { wait: 400 });
    await pg.click('#mchip', { wait: 300 });
    await pg.wait(2200);
    a = await asks(pg);
    s = await state(pg);
    t.check('a cache under 8 MB is read, not cleared', a.length === 3 && s.cleared === 2 && s.last && s.last.cleared === false && (await pg.eval(() => window.__MOCK_IMAGE_CACHE__.bytes)) === 2 * MB, { a, s });
    t.check('no page errors', pg.errors.length === 0, pg.errors);
    await pg.close();

    /* ---- the bar hiding */
    const p2 = await open('?mode=reveal&media=playing', W);
    await fill(p2, FULL);
    await p2.eval(() => window.__mockMain.reveal('test'));
    await p2.wait(600);
    await p2.eval(() => window.__mockMain.hide('test'));
    await p2.wait(3000);
    a = await asks(p2);
    s = await state(p2);
    t.check('the bar hiding asks once, after it is gone', a.length === 1 && s && s.last && s.last.why === 'the bar hid' && s.cleared === 1, { a, s });
    t.check('no page errors (hide)', p2.errors.length === 0, p2.errors);
    await p2.close();

    /* ---- a bridge that cannot: said once, then left alone */
    const p3 = await open(Q, Object.assign({ init: 'window.__MOCK_IMAGE_CACHE__ = "throw";' }, W));
    const warns = [];
    p3.page.on('console', (m) => { if (/image cache/.test(m.text())) warns.push(m.type() + ': ' + m.text()); });
    await p3.click('#mchip', { wait: 400 });
    await p3.click('#mchip', { wait: 300 });
    await p3.wait(2200);
    s = await state(p3);
    t.check('a picture cache that throws is said, with its reason', warns.length === 1 && /^warning: /.test(warns[0]) && /stay in memory/.test(warns[0]) && /could not be read/.test(warns[0]) && /could not be read/.test(s.off || ''), { warns, s });
    await p3.wait(20500);
    await p3.click('#mchip', { wait: 400 });
    await p3.click('#mchip', { wait: 300 });
    await p3.wait(2200);
    t.check('...once: the bar stops asking', (await asks(p3)).length === 1 && warns.length === 1, { asks: await asks(p3), warns });
    t.check('...and the bar works on (the card opens and shuts)', !(await popOpen(p3)) && p3.errors.length === 0, p3.errors);
    await p3.close();
  });
}

run().then(() => t.done(), (e) => { t.check('the run itself', false, e && e.stack || e); t.done(); });
