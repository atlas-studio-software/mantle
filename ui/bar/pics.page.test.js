require('../../tools/dev-env.js').lowcpu();
/* pics.page.test.js - the bar shows its new pictures through blob: URLs and lets every one go
 * (lane mantle-bar-leak, 2026-11-06).
 *
 * WHY: an <img> that has shown a data: picture keeps about its decoded size in the renderer for good
 * (tools/img-mem-probe.js, tools/bar-mem-ab.js: the bar renderer grew 36 -> 111 MB over days). The bar
 * gets a new data: picture on every reveal (the backdrop strip), every dark pop-up (its glass is baked
 * from one), every song (artwork, Up next, Library) and every toast picture.
 *
 * WHAT: the real index.html on the mock bridge (page-harness.js, headless Chromium over file://), dark.
 * URL.createObjectURL / revokeObjectURL are counted from the first line of the page, and every case
 * asserts on what is on screen (the <img>'s address, that it decoded, its pixels against the same
 * picture decoded from its data: URL) and on the URLs still held afterwards.
 *
 *   node ui/bar/pics.page.test.js
 * Prints "PICS PAGE TEST: n passed, m failed".
 */
'use strict';
const H = require('./page-harness.js');
const t = H.suite('PICS PAGE TEST');

const DARK = "(function () { var st = function () { if (document.documentElement) document.documentElement.setAttribute('data-theme', 'dark'); }; st(); document.addEventListener('readystatechange', st); })();";
/* every blob: URL made and let go, from the first line of the page */
const COUNT = '(function () { var live = window.__blobLive = new Set(); var mk = URL.createObjectURL, rv = URL.revokeObjectURL;' +
  ' URL.createObjectURL = function (b) { var u = mk.call(URL, b); live.add(u); return u; };' +
  ' URL.revokeObjectURL = function (u) { live.delete(u); return rv.call(URL, u); };' +
  ' var w = console.warn; window.__warns = []; console.warn = function () { window.__warns.push(Array.prototype.join.call(arguments, " ")); return w.apply(console, arguments); }; })();';

/* page side: a picture nobody has seen before (noise, so every call is a new data: URL) */
const NEWPIC = function (a) {
  var c = document.createElement('canvas'); c.width = a.w; c.height = a.h;
  var g = c.getContext('2d'), d = g.createImageData(a.w, a.h);
  for (var i = 0; i < d.data.length; i += 4) { d.data[i] = Math.random() * 255; d.data[i + 1] = Math.random() * 255; d.data[i + 2] = Math.random() * 255; d.data[i + 3] = 255; }
  g.putImageData(d, 0, 0);
  return c.toDataURL('image/png');
};
/* page side: is <img sel> showing exactly the picture `data` decodes to? */
const SAMEPIXELS = async function (a) {
  var el = document.querySelector(a.sel);
  if (!el || !el.complete || !el.naturalWidth) return { ok: false, why: 'not decoded', src: el && String(el.getAttribute('src')).slice(0, 12) };
  var ref = new Image(); ref.src = a.data; await ref.decode();
  var px = function (im) { var c = document.createElement('canvas'); c.width = im.naturalWidth; c.height = im.naturalHeight; var g = c.getContext('2d'); g.drawImage(im, 0, 0); return g.getImageData(0, 0, c.width, c.height).data; };
  var p = px(el), q = px(ref), bad = 0;
  if (p.length !== q.length) return { ok: false, why: 'size ' + el.naturalWidth + 'x' + el.naturalHeight + ' vs ' + ref.naturalWidth + 'x' + ref.naturalHeight };
  for (var i = 0; i < p.length; i++) if (p[i] !== q[i]) bad++;
  return { ok: bad === 0, bad: bad, w: el.naturalWidth, h: el.naturalHeight };
};
const settle = (pg, ms) => pg.eval((m) => new Promise((r) => setTimeout(r, m)), ms || 200);
const live = (pg) => pg.eval(() => Array.from(window.__blobLive));
const srcOf = (pg, sel) => pg.eval((s) => { var e = document.querySelector(s); return e ? String(e.getAttribute('src') || '') : null; }, sel);

async function run() {
  if (H.gaming()) { console.log('a game is running: the browser half is skipped'); t.check('not skipped (a game was running)', false); return; }
  await H.withBrowser(async (open) => {
    const pg = await open('?mode=pill&media=playing&spq=long', { w: 1280, h: 560, settle: 900, init: COUNT + DARK });
    t.check('the page has MantlePic (ui/shared/pic-url.js loaded under the CSP)', await pg.eval(() => !!window.MantlePic));

    // ---- the backdrop strip: a new picture each reveal
    const strips = [];
    for (let i = 0; i < 4; i++) {
      const png = await pg.eval(NEWPIC, { w: 320, h: 16 });
      strips.push(png);
      await pg.emit('bar.backdrop', { png, w: 320, h: 16, rect: { x: 0, y: 0, w: 1280, h: 64 }, ms: 1 });
      await settle(pg, 150);
      const src = await srcOf(pg, '#backdrop');
      t.check(`strip ${i + 1}: shown through a blob: URL, not the data: one`, /^blob:/.test(src), src && src.slice(0, 16));
      const same = await pg.eval(SAMEPIXELS, { sel: '#backdrop', data: png });
      t.check(`strip ${i + 1}: the same pixels as the data: picture`, same.ok, same);
      t.check(`strip ${i + 1}: its URL is let go once the <img> has it`, !(await live(pg)).includes(src), await live(pg));
    }
    t.check('the bar is not left in its no-picture state', await pg.eval(() => !document.body.classList.contains('no-backdrop')));

    // two strips at once: the later one wins, the earlier one's URL is let go too
    const a = await pg.eval(NEWPIC, { w: 320, h: 16 }), b = await pg.eval(NEWPIC, { w: 320, h: 16 });
    await pg.eval(([x, y]) => { window.__mockMain.emit('bar.backdrop', { png: x, w: 320, h: 16, rect: { x: 0, y: 0, w: 1280, h: 64 } }); window.__mockMain.emit('bar.backdrop', { png: y, w: 320, h: 16, rect: { x: 0, y: 0, w: 1280, h: 64 } }); }, [a, b]);
    await settle(pg, 200);
    t.check('two strips in a row: the second is the one on screen', (await pg.eval(SAMEPIXELS, { sel: '#backdrop', data: b })).ok);
    // the only blob: URLs a page may hold are the cache's own (covers that come back); one-off pictures hold none
    const held = async () => ({ live: (await live(pg)).length, cached: await pg.eval(() => window.MantlePic.stats().cached) });
    let hd = await held();
    t.check('two strips in a row: no blob: URL is left held', hd.live === hd.cached, hd);

    // a strip that arrives and is cleared (the bar hid) before it decoded never comes back
    const c = await pg.eval(NEWPIC, { w: 320, h: 16 });
    await pg.eval((x) => { window.__mockMain.emit('bar.backdrop', { png: x, w: 320, h: 16, rect: { x: 0, y: 0, w: 1280, h: 64 } }); window.__mockMain.hide('pics test: away before the strip decoded'); }, c);
    await settle(pg, 900);
    t.check('a hide right after a strip arrived: the hidden bar holds no picture', !(await srcOf(pg, '#backdrop')), (await srcOf(pg, '#backdrop') || '').slice(0, 16));
    hd = await held();
    t.check('...and its URL is let go', hd.live === hd.cached, hd);
    await pg.eval(() => window.__mockMain.reveal('pics test: back'));
    await settle(pg, 700);

    // a strip that is not a picture says so (never a silent blank)
    await pg.eval(() => { window.__warns.length = 0; });
    await pg.emit('bar.backdrop', { png: 'data:image/png;base64,bm90IGEgcGljdHVyZQ==', w: 1, h: 1, rect: { x: 0, y: 0, w: 1280, h: 64 } });
    await settle(pg, 300);
    const warns = await pg.eval(() => window.__warns.slice());
    t.check('a strip that does not decode raises "Glass not blurred" with the reason', warns.some((w) => /Glass not blurred/.test(w) && /did not decode/.test(w)), warns);
    hd = await held();
    t.check('...and its URL is let go', hd.live === hd.cached, hd);
    await pg.emit('bar.backdrop', { png: strips[0], w: 320, h: 16, rect: { x: 0, y: 0, w: 1280, h: 64 }, ms: 1 });
    await settle(pg, 200);
    t.check('the next good strip clears it and is shown', (await pg.eval(SAMEPIXELS, { sel: '#backdrop', data: strips[0] })).ok);

    // ---- the dark pop-up's picture: an Image decoded once per open, its URL let go on load
    const before = (await live(pg)).length;
    for (let i = 0; i < 3; i++) {
      await pg.click('#mchip', { wait: 500 });
      const st = await pg.eval(() => ({ open: !document.getElementById('pop').hidden, bd: document.getElementById('pop').classList.contains('bd'), url: document.getElementById('pop').style.getPropertyValue('--pop-bd').slice(0, 20), glass: window.__barGlass && window.__barGlass.pop }));
      t.check(`dark pop-up ${i + 1}: opens with its baked glass`, st.open && st.bd && /url\(/.test(st.url), st);
      await pg.click('#mchip', { wait: 350 });
    }
    t.check('dark pop-ups: no blob: URL is left held by their pictures', (await live(pg)).length === before, { before, now: (await live(pg)).length });

    // ---- artwork: the card's art and Up next's covers through the cache
    await pg.click('#mchip', { wait: 500 });
    const head = await pg.eval(() => { const h = document.querySelector('#pop .spq-head'); return h ? h.getAttribute('aria-expanded') : null; });
    if (head === 'false') await pg.click('#pop .spq-head', { wait: 500 });
    const covers = await pg.eval(() => Array.from(document.querySelectorAll('#pop .spq-art img')).map((i) => ({ src: String(i.getAttribute('src') || '').slice(0, 5), ok: i.complete && i.naturalWidth > 0 })));
    t.check('Up next covers: shown, every one through a blob: URL', covers.length > 0 && covers.every((c) => c.src === 'blob:'), covers.slice(0, 4));
    t.check('Up next covers: every one decoded', covers.length > 0 && covers.every((c) => c.ok), covers.filter((c) => !c.ok).length + ' not decoded');
    // the card closed and opened again: the list is drawn anew with the same covers, which come from the
    // cache at once (no new URL, each <img> complete the moment it is made)
    const s0 = await pg.eval(() => window.MantlePic.stats());
    await pg.click('#mchip', { wait: 350 });
    await pg.click('#mchip', { wait: 500 });
    const s1 = await pg.eval(() => window.MantlePic.stats());
    const covers2 = await pg.eval(() => Array.from(document.querySelectorAll('#pop .spq-art img')).map((i) => i.complete && i.naturalWidth > 0));
    t.check('Up next drawn again: its covers come from the cache (hits, no new URL)', covers2.length > 0 && s1.made === s0.made && s1.hits > s0.hits, { s0, s1, n: covers2.length });
    t.check('Up next drawn again: every cover decoded', covers2.length > 0 && covers2.every(Boolean));
    const sync = await pg.eval(() => {
      var src = document.querySelector('#pop .spq-art img');
      var im = document.createElement('img'); im.alt = '';
      var u = src && src.getAttribute('src');
      if (!u) return null;
      im.src = u; document.body.appendChild(im);
      var r = { complete: im.complete, w: im.naturalWidth }; im.remove(); return r;
    });
    t.check('a cached cover is complete the moment an <img> is given it (no frame without it)', sync && sync.complete && sync.w > 0, sync);
    t.check('the cache stays within its limits', s1.cached <= 160 && s1.heldBytes <= 6 * 1048576, s1);
    hd = await held();
    t.check('every blob: URL still held is one the cache holds (none leaked by a one-off picture)', hd.live === hd.cached, hd);
    await pg.click('#mchip', { wait: 350 });

    // the chip's artwork: a new song is a new picture through the cache; the same song again is a hit
    const art1 = await pg.eval(NEWPIC, { w: 96, h: 96 });
    const push = (art) => pg.eval(async (x) => {
      const p = await window.mantle.call('media.get', {});
      const pick = (s) => { if (s) s.artwork = x; };
      pick(p.shown); (p.sessions || []).forEach(pick); p.rev = (Number(p.rev) || 0) + 1;
      window.__mockMain.emit('media', p);
    }, art);
    await push(art1);
    await settle(pg, 250);
    const chip = await pg.eval(() => { const i = document.querySelector('#mchip .mart img'); return i ? { src: String(i.getAttribute('src') || '').slice(0, 5), ok: i.complete && i.naturalWidth > 0 } : null; });
    t.check('the chip\'s artwork: through a blob: URL and decoded', chip && chip.src === 'blob:' && chip.ok, chip);
    const sameArt = await pg.eval(SAMEPIXELS, { sel: '#mchip .mart img', data: art1 });
    t.check('the chip\'s artwork: the same pixels as its data: picture', sameArt.ok, sameArt);
    const m0 = await pg.eval(() => ({ made: window.MantlePic.stats().made, img: document.querySelector('#mchip .mart img') && (window.__artImg = document.querySelector('#mchip .mart img'), true) }));
    await push(art1);
    await settle(pg, 150);
    const m1 = await pg.eval(() => ({ made: window.MantlePic.stats().made, same: document.querySelector('#mchip .mart img') === window.__artImg }));
    t.check('the same artwork pushed again: the same <img>, no new URL', m0.img && m1.same && m1.made === m0.made, { m0, m1 });
    const art2 = await pg.eval(NEWPIC, { w: 96, h: 96 });
    await push(art2);
    await settle(pg, 250);
    t.check('a new song\'s artwork replaces it, the same pixels as its data: picture', (await pg.eval(SAMEPIXELS, { sel: '#mchip .mart img', data: art2 })).ok);

    t.check('no page errors', pg.errors.length === 0, pg.errors);
    await pg.close();
  });
}

run().then(() => t.done(), (e) => { t.check('the run itself', false, e && e.stack || e); t.done(); });
