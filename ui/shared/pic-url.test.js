/* pic-url.test.js : node ui/shared/pic-url.test.js (lane mantle-bar-leak, 2026-11-06)
 *
 * The pure half of ui/shared/pic-url.js in node: the data: -> Blob reading (base64 and percent-encoded,
 * parameters, broken input passed through), the cache (one URL per distinct picture, LRU order, the
 * picture and byte limits, URLs revoked as they go), and one-off pictures
 * (once / release). Image is a stand-in that "decodes" on
 * a timer; ui/bar/pics.page.test.js covers the same code in Chromium against the real bar.
 */
'use strict';
const assert = require('assert');

/* a stand-in <img>: src set -> load (or error for a URL marked bad) on the next tick, with a size */
const BAD = new Set();
let SIDE = 64;
class FakeImage {
  constructor() { this.naturalWidth = 0; this.naturalHeight = 0; this.onload = null; this.onerror = null; this._src = ''; }
  set src(u) {
    this._src = u;
    setImmediate(() => {
      if (this._src !== u) return;
      if (BAD.has(u) || /^data:/.test(u)) { if (this.onerror) this.onerror(); return; }
      this.naturalWidth = SIDE; this.naturalHeight = SIDE;
      if (this.onload) this.onload();
    });
  }
  get src() { return this._src; }
  removeAttribute(k) { if (k === 'src') this._src = ''; }
  decode() { return Promise.resolve(); }
}
global.Image = FakeImage;
const revoked = [];
const rv = URL.revokeObjectURL;
URL.revokeObjectURL = (u) => { revoked.push(u); return rv.call(URL, u); };

const P = require('./pic-url.js');

let pass = 0, fail = 0;
async function t(name, fn) {
  try { await fn(); pass++; console.log('ok   ' + name); } catch (e) { fail++; console.log('FAIL ' + name + ': ' + e.message); }
}
const tick = () => new Promise((r) => setImmediate(r));
const b64 = (s) => 'data:image/png;base64,' + Buffer.from(s).toString('base64');

(async () => {
  await t('blobOf: base64, with its type', async () => {
    const b = P.blobOf(b64('hello'));
    assert.ok(b);
    assert.strictEqual(b.type, 'image/png');
    assert.strictEqual(Buffer.from(await b.arrayBuffer()).toString(), 'hello');
  });
  await t('blobOf: parameters before ;base64, whitespace in the payload', async () => {
    const b = P.blobOf('data:image/svg+xml;charset=utf-8;base64,' + Buffer.from('<svg/>').toString('base64').replace(/(.{2})/, '$1 \n'));
    assert.strictEqual(b.type, 'image/svg+xml');
    assert.strictEqual(Buffer.from(await b.arrayBuffer()).toString(), '<svg/>');
  });
  await t('blobOf: percent-encoded (not base64)', async () => {
    const b = P.blobOf('data:image/svg+xml,' + encodeURIComponent('<svg a="1"/>'));
    assert.strictEqual(Buffer.from(await b.arrayBuffer()).toString(), '<svg a="1"/>');
  });
  await t('blobOf: not a data: URL, no comma, broken base64 -> null', () => {
    assert.strictEqual(P.blobOf('file:///x.png'), null);
    assert.strictEqual(P.blobOf('data:image/png;base64'), null);
    assert.strictEqual(P.blobOf('data:image/png;base64,@@@@'), null);
    assert.strictEqual(P.blobOf(null), null);
  });
  await t('set: a non-data: src is assigned as it is', () => {
    const im = new FakeImage();
    assert.strictEqual(P.set(im, 'file:///C:/x.png'), 'file:///C:/x.png');
    assert.strictEqual(im.src, 'file:///C:/x.png');
  });
  await t('set: broken base64 is passed through as the data: URL (the <img> fails as before)', () => {
    const im = new FakeImage();
    const s = 'data:image/png;base64,@@@@';
    assert.strictEqual(P.set(im, s), s);
    assert.strictEqual(im.src, s);
  });
  await t('set: one blob: URL per distinct picture, the same one for the same picture', async () => {
    const a = new FakeImage(), b = new FakeImage(), c = new FakeImage();
    const s0 = P.stats();
    P.set(a, b64('one')); P.set(b, b64('one')); P.set(c, b64('two'));
    assert.ok(/^blob:/.test(a.src));
    assert.strictEqual(a.src, b.src);
    assert.notStrictEqual(a.src, c.src);
    const s1 = P.stats();
    assert.strictEqual(s1.made - s0.made, 2);
    assert.strictEqual(s1.hits - s0.hits, 1);
    assert.ok(P.stats().heldBytes >= 'one'.length + 'two'.length, 'held bytes counted: ' + P.stats().heldBytes);
  });
  await t('cache: past its picture limit the OLDEST go, their URLs revoked', async () => {
    const first = new FakeImage();
    P.set(first, b64('lru-first'));
    const firstUrl = first.src;
    const keep = new FakeImage();
    for (let i = 0; i < P.LIMITS.pictures + 5; i++) {
      const im = new FakeImage();
      P.set(im, b64('lru-' + i));
      if (i % 50 === 0) P.set(keep, b64('lru-first'));   // touched again now and then: never the oldest
    }
    await tick(); await tick();
    const s = P.stats();
    assert.ok(s.cached <= P.LIMITS.pictures, 'cached ' + s.cached);
    assert.ok(s.evicted > 0);
    assert.ok(!revoked.includes(firstUrl), 'a picture used again recently is kept');
    const again = new FakeImage();
    P.set(again, b64('lru-0'));
    assert.ok(revoked.length > 0 && revoked.every((u) => /^blob:/.test(u)));
  });
  await t('cache: past its byte limit the oldest go', async () => {
    const big = (c) => b64(c.repeat(2 * 1048576));   // 2 MB each: two do not fit in 3 MB
    const a = new FakeImage(), b = new FakeImage();
    P.set(a, big('a'));
    P.set(b, big('b'));
    const s = P.stats();
    assert.ok(s.heldBytes <= P.LIMITS.bytes, 'held ' + s.heldBytes);
    assert.ok(revoked.includes(a.src), 'the older big one was let go');
    assert.ok(!revoked.includes(b.src), 'the newest is kept');
  });
  await t('once: a fresh URL each call; release revokes it; a non-data: src comes back as it is', () => {
    const u1 = P.once(b64('x')), u2 = P.once(b64('x'));
    assert.ok(/^blob:/.test(u1) && u1 !== u2);
    P.release(u1); P.release(u2);
    assert.ok(revoked.includes(u1) && revoked.includes(u2));
    assert.strictEqual(P.once('file:///y.png'), 'file:///y.png');
    P.release('file:///y.png');   // ignored: not a blob: URL
    assert.ok(!revoked.includes('file:///y.png'));
  });
  console.log(`\nPIC URL TEST: ${pass} passed, ${fail} failed`);
  process.exit(fail ? 1 : 0);
})();
