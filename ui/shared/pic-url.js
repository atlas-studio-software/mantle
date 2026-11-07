/* pic-url.js : pictures that arrive as data: URLs, shown through blob: URLs (lane mantle-bar-leak,
 * 2026-11-06).
 *
 * WHY. The bar's renderer grew with use and never gave it back: 36 -> 46 -> 90 -> 111 MB private over
 * a multi-day run, while every other page stayed flat. Measured in Mantle's own Electron
 * (tools/img-mem-probe.js, tools/bar-mem-probe.js): every NEW data: URL an <img> shows keeps about its
 * decoded size in the renderer for the life of the page, after forced GC, a critical memory-pressure
 * signal and webFrame.clearCache(). The same bytes as a Blob behind a blob: URL that is revoked stay
 * flat. The bar shows a new data: picture on every reveal (the backdrop strip), every dark pop-up
 * (the picture its glass is baked from), and every song (artwork, Up next and Library covers).
 *
 * So a data: picture is turned into a Blob here and shown through its own blob: URL. Same bytes, same
 * decoder, same pixels; only the address changes. Two ways in:
 *
 *   MantlePic.set(img, src)    for pictures that come back (artwork, list covers): one blob: URL per
 *       distinct picture, kept in a small cache with one decoded holder each, so a list that is drawn
 *       again with the same covers gets them from memory at once (no empty frame), and the cache's
 *       oldest are let go (URL revoked, holder dropped) past CACHE_MAX pictures or CACHE_BYTES.
 *   MantlePic.once(src)        for one-off pictures (the backdrop strip, a pop-up's picture): a fresh
 *       blob: URL; the caller lets it go with MantlePic.release(url) once its element has loaded it.
 *       Never before: an element given a URL that was already revoked never loads (measured, Electron 44).
 *
 * Timing is unchanged: a data: picture is not on screen the moment its src is set either; both land on
 * the next frame (tools/img-mem-probe.js's sync line). A cached picture whose holder has decoded it is
 * complete at once, so a list drawn again never shows a frame without its covers.
 * Anything that is not a data: URL is passed straight through.
 * A data: URL that cannot be read as bytes (bad base64) is passed through too, so the <img> fails the
 * way it always did and its own onerror fallback runs; never a silent blank.
 * The page's CSP needs `blob:` in img-src (ui/bar/index.html).
 */
(function (root) {
  'use strict';
  var CACHE_MAX = 160;                 // distinct pictures (Up next 32 + Library 40 + search + the card, with room)
  var CACHE_BYTES = 6 * 1048576;       // decoded bytes the cache may hold (a 64 px cover is 16 KB, a 640 px one 1.6 MB)

  function isData(src) { return typeof src === 'string' && src.slice(0, 5).toLowerCase() === 'data:'; }

  /* data:[<mime>][;param]*[;base64],<payload> -> Blob, or null when it is not one or does not read */
  function blobOf(src) {
    if (!isData(src) || typeof Blob === 'undefined') return null;
    var comma = src.indexOf(',');
    if (comma < 0) return null;
    var head = src.slice(5, comma), body = src.slice(comma + 1);
    var parts = head.split(';');
    var mime = parts[0] || 'text/plain';
    var b64 = parts.slice(1).some(function (x) { return x.trim().toLowerCase() === 'base64'; });
    try {
      if (b64) {
        var s = atob(body.replace(/\s+/g, ''));
        var a = new Uint8Array(s.length);
        for (var i = 0; i < s.length; i++) a[i] = s.charCodeAt(i);
        return new Blob([a], { type: mime });
      }
      return new Blob([decodeURIComponent(body)], { type: mime });
    } catch (_) {
      return null;   // not base64 / not percent-encoding: the caller hands the data: URL over as it is
    }
  }

  var stats = { made: 0, revoked: 0, hits: 0, misses: 0, evicted: 0, passed: 0 };
  function make(blob) { stats.made++; return URL.createObjectURL(blob); }
  function release(url) {
    if (typeof url === 'string' && url.slice(0, 5) === 'blob:') { try { URL.revokeObjectURL(url); stats.revoked++; } catch (_) { /* gone */ } }
  }

  /* ---------------------------------------------------------------- the cache (pictures that come back) */
  var cache = new Map();   // data: string -> { url, holder, bytes }; Map order = least recently used first
  var held = 0;
  function sizeOf(e) {
    var h = e.holder;
    return h && h.naturalWidth > 0 ? h.naturalWidth * h.naturalHeight * 4 : 0;
  }
  function trim() {
    var it = cache.keys();
    while (cache.size > CACHE_MAX || (held > CACHE_BYTES && cache.size > 1)) {
      var k = it.next().value;
      var e = cache.get(k);
      cache.delete(k);
      held -= e.bytes;
      // an <img> that already has the picture keeps showing it; only a NEW element would need the URL
      release(e.url);
      try { e.holder.removeAttribute('src'); } catch (_) { /* nothing to drop */ }
      stats.evicted++;
    }
  }
  function entryFor(src) {
    var e = cache.get(src);
    if (e) { cache.delete(src); cache.set(src, e); stats.hits++; return e; }
    var blob = blobOf(src);
    if (!blob) return null;
    stats.misses++;
    e = { url: make(blob), holder: new Image(), bytes: 0 };
    e.holder.onload = function () {
      if (cache.get(src) !== e) return;
      e.bytes = sizeOf(e); held += e.bytes; trim();
    };
    e.holder.onerror = function () {   // the same failure the <img> gets: let the entry go, nothing to hold
      if (cache.get(src) !== e) return;
      cache.delete(src); release(e.url);
    };
    e.holder.src = e.url;
    cache.set(src, e);
    trim();
    return e;
  }

  /* img.src = src, a data: picture through the cache. Returns what was assigned. */
  function set(img, src) {
    if (!isData(src)) { img.src = src; return src; }
    var e = entryFor(src);
    if (!e) { stats.passed++; img.src = src; return src; }
    img.src = e.url;
    return e.url;
  }

  /* ---------------------------------------------------------------- one-off pictures */
  /* a fresh blob: URL (the caller release()s it once its element has loaded it), or src itself when it
     is not a data: picture that reads */
  function once(src) {
    var blob = blobOf(src);
    if (!blob) { stats.passed++; return src; }
    return make(blob);
  }

  var api = {
    isData: isData, blobOf: blobOf, set: set, once: once, release: release,
    stats: function () { return { made: stats.made, revoked: stats.revoked, hits: stats.hits, misses: stats.misses, evicted: stats.evicted, passed: stats.passed, cached: cache.size, heldBytes: held }; },
    LIMITS: { pictures: CACHE_MAX, bytes: CACHE_BYTES }
  };
  root.MantlePic = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
