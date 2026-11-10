/* motion.js -- the top bar steps aside for a full-screen window (lane mantle-fullscreen-motion, 2026-11-09).
 *
 * The user, on the bar now stepping aside for ANY full-screen window (YouTube, Disney+, Robinhood Legend full
 * screen in his browser): "it needs to look nice when it goes away though, it can't look like some cheap thing".
 *
 * THE PROTOCOL (main: lib/features/topbar.js MOTION). Main pushes `topbar.motion` {phase:'leave', ms, kind}
 * while the bar's window is still up (its menus already closed by `topbar.close`) and hides the WINDOW `ms`
 * (LEAVE_MS) later. Coming back it shows the window FIRST, with this page still in its away frame (so the first
 * frame on screen is the empty one, never a flash of the old bar), then pushes {phase:'arrive', why}. Back
 * within the leave, the window never went: 'arrive' comes while the leave is still playing and the same
 * transition turns round from wherever it is.
 *
 * THE MOTION is Mantle's own (ui/shared/glass-light.css MOTION IN UNISON; the bottom bar, ui/bar/bar.css, is
 * the reference): the bar is a whole-edge surface, so IN = --m-spring over --m-dur-l (560 ms) with the fade on
 * --m-fade-in over its first 40 %, OUT = --m-accel over 45 % of that (252 ms) with --m-fade-out. Only transform
 * and opacity move, and they move ONE layer: #stage holds everything that draws the bar (#bglass with #bdrop
 * and #btint, #bar with #under inside it, #menus), so the glass, its blurred picture, the colour under and the
 * words can never come apart, and nothing is left behind as a band. The leave slides it up off the screen's top
 * edge (the window clips it) and fades it; the arrive is the reverse on the spring. Both are CSS transitions
 * on body.away (topbar.css THE MOTION), which retarget from wherever they are, so a turn-round never jumps.
 * Reduced motion (Windows' "Animation effects" off): opacity only, 120 ms, in and out.
 *
 * THE LOOK WHILE IT MOVES. A look push landing mid-leave (a new desktop picture, new colours under the bar, a
 * new darkening) would visibly change a bar that is on its way out, so while LEAVING it is held (the newest of
 * each kind) and applied the moment the bar is gone (AWAY: invisible), and anything held is applied before an
 * arrive starts, so the bar comes back in the CURRENT look. A push that changes nothing visible is applied at
 * once: the colour under's handover (lib/topbar-under.js handoff: the same colours, native:false, so the page
 * paints them itself as they slide) must land at once, or the masked glass would show a hole where the native
 * layer was. A theme change mid-leave cannot be held (preload applies it), so the leave finishes at once
 * (snap): the bar is gone a few frames early rather than shown half in one theme and half in the other.
 *
 * Pure (no DOM): the page hands it a class list, timers, the reduced-motion flag and the token's duration.
 *   window.TopbarMotion (the page) / module.exports (ui/topbar/motion.test.js)
 */
(function (root, factory) {
  var M = factory();
  if (typeof module === 'object' && module.exports) module.exports = M;
  else root.TopbarMotion = M;
}(this, function () {
  'use strict';
  var LONG_MS = 560;       // --m-dur-l, the fallback when the token cannot be read
  var OUT_K = 0.45;        // OUT = 45 % of the IN
  var FADE_IN_K = 0.4;     // the IN's fade: its first 40 %
  var REDUCED_MS = 120;    // reduced motion: opacity only, in and out
  var SLACK_MS = 34;       // two frames past a transition's end before the phase moves on (its last frame drawn)
  var SNAP_MS = 50;        // a snap's `transition: none` is lifted after this (the values no longer change)

  /** The durations for this --m-dur-l (ms) and reduced-motion flag. */
  function durations(longMs, reduced) {
    var l = longMs > 0 ? longMs : LONG_MS;
    if (reduced) return { out: REDUCED_MS, in: REDUCED_MS, fadeIn: REDUCED_MS, reduced: true };
    return { out: Math.round(l * OUT_K), in: l, fadeIn: Math.round(l * FADE_IN_K), reduced: false };
  }
  /** '560ms' / '0.56s' -> 560; anything else -> 0 */
  function parseMs(v) {
    var m = /^\s*([\d.]+)\s*(ms|s)\s*$/.exec(String(v || ''));
    if (!m) return 0;
    return m[2] === 's' ? Math.round(parseFloat(m[1]) * 1000) : Math.round(parseFloat(m[1]));
  }
  function sameNums(a, b, tol) {
    if (!a || !b || a.length !== b.length) return false;
    for (var i = 0; i < a.length; i++) if (Math.abs(Number(a[i]) - Number(b[i])) > tol) return false;
    return true;
  }
  /** Pure: does this `topbar.under` payload leave the bar looking as it is? cur = {on, cols, cover} as drawn.
   * The handover (the same colours with only `native` changed) and a paused-off that keeps the paint do. */
  function sameUnder(p, cur) {
    if (!p) return false;
    if (p.off) return !!p.keep;
    return !!(cur && cur.on && sameNums(p.cols, cur.cols, 0) && sameNums(p.cover, cur.cover, 0.006));
  }
  /** Pure: does this `topbar.backdrop` payload leave the glass as it is? cur = {png, under}. */
  function sameBackdrop(p, cur) {
    if (!p || !cur) return false;
    if (p.off) return !!p.keep && !!cur.png;   // hidden: the last picture waits (glassOff keeps it)
    return p.png === cur.png && (p.under || null) === (cur.under || null);
  }

  /** o = {cls: {add(c), remove(c)}, reduced(): bool, longMs(): ms, later(ms, fn), cancel(h), log?(line)} */
  function create(o) {
    var S = { phase: 'rest', kind: null, why: null, t: null, snapT: null, held: [], leaves: 0, arrives: 0, turns: 0, snaps: 0, heldN: 0, d: durations(0, false) };
    function log(line) { try { if (o.log) o.log(line); } catch (_) { /* the log's business */ } }
    function timer(ms, fn) {
      if (S.t) o.cancel(S.t);
      S.t = o.later(ms, function () { S.t = null; fn(); });
    }
    function flush() {
      var h = S.held;
      S.held = [];
      for (var i = 0; i < h.length; i++) {
        try { h[i].fn(h[i].p); } catch (e) { log('motion: a held ' + h[i].kind + ' push threw: ' + (e && e.message)); }
      }
      return h.length;
    }
    function dur() { S.d = durations(o.longMs ? o.longMs() : 0, !!(o.reduced && o.reduced())); return S.d; }
    var api = {
      /** {phase:'leave', ms, kind}: slide away. false when it is already leaving or away. */
      leave: function (ev) {
        if (S.phase === 'leaving' || S.phase === 'away') return false;
        var d = dur();
        S.phase = 'leaving'; S.kind = (ev && ev.kind) || null; S.why = 'a full-screen window (' + (S.kind || 'full') + ')'; S.leaves++;
        if (S.snapT) { o.cancel(S.snapT); S.snapT = null; o.cls.remove('snap'); }
        o.cls.add('away');
        if (ev && ev.ms > 0 && ev.ms < d.out) log('motion: main hides the window after ' + ev.ms + ' ms, before the ' + d.out + ' ms leave ends');
        timer(d.out + SLACK_MS, function () { S.phase = 'away'; flush(); });
        return true;
      },
      /** {phase:'arrive', why}: the spring back in, from the away frame or from wherever the leave is. */
      arrive: function (ev) {
        if (S.phase === 'rest' || S.phase === 'arriving') return false;
        var turning = S.phase === 'leaving';
        if (turning) S.turns++;
        var d = dur();
        flush();   // the CURRENT look first: the bar comes back in it
        if (S.snapT) { o.cancel(S.snapT); S.snapT = null; }
        o.cls.remove('snap');
        o.cls.remove('away');
        S.phase = 'arriving'; S.why = (ev && ev.why) || (turning ? 'back while it was leaving' : 'shown'); S.arrives++;
        timer(d.in + SLACK_MS, function () { S.phase = 'rest'; S.kind = null; });
        return true;
      },
      /** A look push: true = apply it now; false = held until the bar is gone (fn(p) is called then). */
      gate: function (kind, p, fn, same) {
        if (S.phase !== 'leaving') return true;
        if (same && same(p)) return true;
        S.held = S.held.filter(function (h) { return h.kind !== kind; });
        S.held.push({ kind: kind, p: p, fn: fn });
        S.heldN++;
        return false;
      },
      /** The theme changed: mid-leave, finish the leave at once (it cannot be held). true = snapped. */
      theme: function () {
        if (S.phase !== 'leaving') return false;
        o.cls.add('snap');
        S.snaps++;
        if (S.t) { o.cancel(S.t); S.t = null; }
        S.phase = 'away';
        flush();
        S.snapT = o.later(SNAP_MS, function () { S.snapT = null; o.cls.remove('snap'); });
        log('motion: the theme changed while the bar was leaving: it finished leaving at once');
        return true;
      },
      phase: function () { return S.phase; },
      view: function () {
        return { phase: S.phase, kind: S.kind, why: S.why, held: S.held.map(function (h) { return h.kind; }), leaves: S.leaves, arrives: S.arrives,
          turns: S.turns, snaps: S.snaps, heldN: S.heldN, out: S.d.out, in: S.d.in, reduced: S.d.reduced };
      },
    };
    return api;
  }

  return { LONG_MS: LONG_MS, OUT_K: OUT_K, FADE_IN_K: FADE_IN_K, REDUCED_MS: REDUCED_MS, SLACK_MS: SLACK_MS, SNAP_MS: SNAP_MS,
    durations: durations, parseMs: parseMs, sameUnder: sameUnder, sameBackdrop: sameBackdrop, create: create };
}));
