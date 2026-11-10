'use strict';
/*
 * motion.test.js -- the top bar's full-screen step-aside (lane mantle-fullscreen-motion, 2026-11-09), pure node,
 * no browser. The page half of main's MOTION (lib/features/topbar.js): ui/topbar/motion.js with a fake class
 * list and a fake clock, plus the static half (topbar.css THE MOTION, index.html's #stage).
 *   STATES:  leave sets the away state (body.away) and reaches 'away' after the OUT; arrive clears it and
 *            reaches 'rest' after the IN; a second leave / arrive is a no-op; the turn-round (arrive mid-leave)
 *            clears .away at once with no stop at 'away'.
 *   LOOK:    while leaving, backdrop / guard / under pushes are held (the newest of each) and applied once
 *            away, or before an arrive starts; a push that changes nothing (the colour under's handover) goes
 *            at once; at rest / away / arriving everything goes at once; a theme change mid-leave snaps.
 *   REDUCED: durations 120 / 120; the stylesheet's reduced block is opacity only, 120 ms, no transform.
 *   CSS:     one layer (#stage holds #bglass, #bar, #menus), the shared tokens (IN --m-spring over --m-dur-l with
 *            the fade on --m-fade-in over 40 %, OUT --m-accel / --m-fade-out over 45 %), only transform and
 *            opacity move, the page loads glass-light.css (the --m-* tokens) before topbar.css.
 *   node ui/topbar/motion.test.js   -> "TOPBAR MOTION TEST: n passed, m failed"
 */
const fs = require('fs');
const path = require('path');
const MOT = require(path.join(__dirname, 'motion.js'));

let passed = 0;
const failures = [];
function check(name, ok, detail) {
  if (ok) passed++;
  else failures.push(name + (detail !== undefined ? ' -- ' + (typeof detail === 'string' ? detail : JSON.stringify(detail)).slice(0, 400) : ''));
  return ok;
}

function rig(opts) {
  const o = opts || {};
  let t = 0;
  const timers = [];
  const classes = new Set();
  const logs = [];
  const mo = MOT.create({
    cls: { add: (c) => classes.add(c), remove: (c) => classes.delete(c) },
    reduced: () => !!o.reduced,
    longMs: () => (o.longMs != null ? o.longMs : 560),
    later: (ms, fn) => { const h = { at: t + ms, fn }; timers.push(h); return h; },
    cancel: (h) => { const i = timers.indexOf(h); if (i >= 0) timers.splice(i, 1); },
    log: (l) => logs.push(l),
  });
  function run(ms) {
    const end = t + ms;
    for (;;) {
      timers.sort((a, b) => a.at - b.at);
      const h = timers[0];
      if (!h || h.at > end) break;
      timers.shift(); t = h.at; h.fn();
    }
    t = end;
  }
  return { mo, classes, logs, run };
}

/* ---- durations ---- */
{
  const d = MOT.durations(560, false);
  check('durations: IN 560 (--m-dur-l), OUT 252 (45 %), fade-in 224 (40 %)', d.in === 560 && d.out === 252 && d.fadeIn === 224, d);
  const r = MOT.durations(560, true);
  check('durations: reduced motion -> 120 in and out', r.in === 120 && r.out === 120 && r.reduced === true, r);
  check('durations: an unreadable token -> 560', MOT.durations(0, false).in === 560);
  check('parseMs: 560ms / 0.56s / junk', MOT.parseMs(' 560ms') === 560 && MOT.parseMs('0.56s') === 560 && MOT.parseMs('x') === 0);
  check("durations: OUT (252) ends before main hides the window (LEAVE_MS 260)", d.out <= 260);
}

/* ---- leave / arrive ---- */
{
  const R = rig();
  check('rest: no .away', !R.classes.has('away') && R.mo.phase() === 'rest');
  check('arrive at rest -> a no-op', R.mo.arrive({ phase: 'arrive', why: 'shown' }) === false && R.mo.phase() === 'rest');
  check('leave -> body.away at once, phase leaving', R.mo.leave({ phase: 'leave', ms: 260, kind: 'video' }) === true && R.classes.has('away') && R.mo.phase() === 'leaving');
  check('leave twice -> a no-op', R.mo.leave({ phase: 'leave', ms: 260, kind: 'video' }) === false);
  R.run(251);
  check('...still leaving at 251 ms', R.mo.phase() === 'leaving');
  R.run(MOT.SLACK_MS + 2);
  check('...away once the OUT (252 ms) and two frames are over', R.mo.phase() === 'away' && R.classes.has('away'));
  check('leave while away -> a no-op', R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' }) === false && R.mo.view().leaves === 1);
  check('arrive -> .away cleared at once, phase arriving', R.mo.arrive({ phase: 'arrive', why: 'shown' }) === true && !R.classes.has('away') && R.mo.phase() === 'arriving');
  check('arrive twice -> a no-op', R.mo.arrive({ phase: 'arrive', why: 'shown' }) === false && R.mo.view().arrives === 1);
  R.run(560 + MOT.SLACK_MS + 1);
  check('...rest once the IN (560 ms) is over', R.mo.phase() === 'rest' && !R.classes.has('away'));
  check('view: the kind and why are kept', R.mo.view().why === 'shown' && R.mo.view().leaves === 1 && R.mo.view().arrives === 1, R.mo.view());
}
{
  // THE TURN-ROUND: back within the leave (the window never hidden)
  const R = rig();
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' });
  R.run(120);
  check('turn-round: arrive mid-leave -> .away cleared at once (the CSS transition turns round from its pose)', R.mo.arrive({ phase: 'arrive', why: 'back while it was leaving' }) && !R.classes.has('away') && R.mo.phase() === 'arriving');
  R.run(200);
  check('turn-round: the old leave\'s timer never moves it to away', R.mo.phase() === 'arriving', R.mo.phase());
  check('turn-round: counted', R.mo.view().turns === 1);
  R.run(600);
  check('turn-round: rest after the IN', R.mo.phase() === 'rest');
  // and a leave again mid-arrive turns round the other way
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' });
  R.mo.arrive({ phase: 'arrive', why: 'shown' });
  R.run(100);
  check('turn-round: leave mid-arrive -> .away again at once', R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' }) && R.classes.has('away') && R.mo.phase() === 'leaving');
}

/* ---- the look while it moves ---- */
{
  const R = rig();
  const applied = [];
  const fn = (k) => (p) => applied.push([k, p.n]);
  const push = (k, n, same) => { const p = { n }; if (R.mo.gate(k, p, fn(k), same)) fn(k)(p); };
  push('backdrop', 1);
  check('look: at rest a push goes at once', applied.length === 1);
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'video' });
  push('backdrop', 2); push('under', 3); push('backdrop', 4); push('guard', 5);
  check('look: while leaving backdrop / under / guard are held', applied.length === 1 && R.mo.view().held.join() === 'under,backdrop,guard', R.mo.view().held);
  push('under', 6, () => true);
  check('look: a push that changes nothing (the handover) goes at once while leaving', applied.length === 2 && applied[1][1] === 6);
  R.run(300);
  check('look: away -> the held pushes applied (invisible now), the newest of each', applied.length === 5 && applied.slice(2).map((a) => a[1]).join() === '3,4,5', applied);
  push('under', 7);
  check('look: away -> a push goes at once', applied.length === 6);
  R.mo.arrive({ phase: 'arrive' });
  push('backdrop', 8);
  check('look: arriving -> at once (the current look)', applied.length === 7);
}
{
  // held pushes are applied BEFORE an arrive starts (the turn-round): the bar comes back in the current look
  const R = rig();
  const order = [];
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' });
  R.mo.gate('under', { n: 1 }, () => order.push(['under', R.classes.has('away')]));
  R.mo.arrive({ phase: 'arrive', why: 'back while it was leaving' });
  check('look: a held push is applied on arrive, while .away is still on (before the spring starts)', order.length === 1 && order[0][1] === true, order);
  check('look: nothing left held', R.mo.view().held.length === 0);
}
{
  // a theme change mid-leave: the leave finishes at once (snap), the held pushes go, .snap is lifted after
  const R = rig();
  const applied = [];
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' });
  R.mo.gate('guard', { n: 1 }, () => applied.push(1));
  check('theme mid-leave -> snapped to away, body.snap on, held applied', R.mo.theme() === true && R.mo.phase() === 'away' && R.classes.has('snap') && applied.length === 1);
  check('theme mid-leave -> said in the log', R.logs.some((l) => /theme changed while the bar was leaving/.test(l)));
  R.run(MOT.SNAP_MS + 1);
  check('...and .snap lifted', !R.classes.has('snap'));
  check('theme at rest / away -> nothing', R.mo.theme() === false);
  // an arrive before the snap is lifted lifts it (the spring must play)
  const R2 = rig();
  R2.mo.leave({ phase: 'leave', ms: 260, kind: 'full' }); R2.mo.theme(); R2.mo.arrive({ phase: 'arrive' });
  check('theme: an arrive right after a snap lifts .snap (the spring plays)', !R2.classes.has('snap') && !R2.classes.has('away'));
}
{
  // main hides the window before this page's leave can end: said
  const R = rig({ longMs: 800 });
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' });
  check('a LEAVE_MS shorter than the OUT is logged', R.logs.some((l) => /hides the window after 260 ms, before the 360 ms leave ends/.test(l)), R.logs);
}
{
  const R = rig({ reduced: true });
  R.mo.leave({ phase: 'leave', ms: 260, kind: 'full' });
  R.run(120 + MOT.SLACK_MS + 1);
  check('reduced: away after 120 ms', R.mo.phase() === 'away' && R.mo.view().reduced === true && R.mo.view().out === 120);
}

/* ---- the "nothing visible changes" checks ---- */
{
  const cur = { on: true, cols: [1, 2, 3, 4, 5, 6], cover: [1, 0.5] };
  check('sameUnder: the handover (same colours, native flipped) -> same', MOT.sameUnder({ cols: [1, 2, 3, 4, 5, 6], cover: [1, 0.5], n: 2, native: false }, cur));
  check('sameUnder: new colours -> not', !MOT.sameUnder({ cols: [1, 2, 3, 4, 5, 7], cover: [1, 0.5], n: 2 }, cur));
  check('sameUnder: a new cover -> not', !MOT.sameUnder({ cols: [1, 2, 3, 4, 5, 6], cover: [1, 0.4], n: 2 }, cur));
  check('sameUnder: off that clears -> not; off that keeps -> same', !MOT.sameUnder({ off: true, why: 'x' }, cur) && MOT.sameUnder({ off: true, keep: true }, cur));
  check('sameUnder: nothing drawn yet -> not', !MOT.sameUnder({ cols: [1, 2, 3, 4, 5, 6], cover: [1, 0.5] }, { on: false }));
  check('sameBackdrop: the same picture -> same; a new one -> not', MOT.sameBackdrop({ png: 'data:a' }, { png: 'data:a', under: null }) && !MOT.sameBackdrop({ png: 'data:b' }, { png: 'data:a' }));
  check('sameBackdrop: hidden-keep with a picture -> same; off -> not', MOT.sameBackdrop({ off: true, keep: true }, { png: 'data:a' }) && !MOT.sameBackdrop({ off: true }, { png: 'data:a' }));
}

/* ---- the stylesheet and the page ---- */
{
  const css = fs.readFileSync(path.join(__dirname, 'topbar.css'), 'utf8').replace(/\r\n/g, '\n');
  const html = fs.readFileSync(path.join(__dirname, 'index.html'), 'utf8').replace(/\r\n/g, '\n');
  const gl = fs.readFileSync(path.join(__dirname, '..', 'shared', 'glass-light.css'), 'utf8');
  const rule = (sel, from) => { const i = css.indexOf(sel + ' {', from || 0); if (i < 0) return null; return css.slice(i, css.indexOf('}', i) + 1); };
  const motionAt = css.indexOf('THE MOTION: THE FULL-SCREEN STEP-ASIDE');
  const rest = rule('#stage', motionAt), away = rule('body.away #stage', motionAt);
  check('css: #stage and body.away #stage rules exist', !!rest && !!away);
  const props = (r) => (/transition:\s*([^;]+);/.exec(r || '') || [, ''])[1].split(',').map((x) => x.trim().split(/\s+/)[0]);
  check('css: only transform and opacity transition', props(rest).join() === 'transform,opacity' && props(away).join() === 'transform,opacity', [props(rest), props(away)]);
  check('css: IN = --m-spring over --m-dur-l, fade over 40 % on --m-fade-in', /transform var\(--m-dur-l\) var\(--m-spring\)/.test(rest) && /opacity\s+calc\(var\(--m-dur-l\) \* \.4\) var\(--m-fade-in\)/.test(rest), rest);
  check('css: OUT = --m-accel and --m-fade-out over 45 %', /transform calc\(var\(--m-dur-l\) \* \.45\) var\(--m-accel\)/.test(away) && /opacity\s+calc\(var\(--m-dur-l\) \* \.45\) var\(--m-fade-out\)/.test(away), away);
  check('css: away = up off the top edge and clear, no clicks', /transform: translateY\(calc\(-1 \* var\(--tb-h\)\)\)/.test(away) && /opacity: 0/.test(away) && /pointer-events: none/.test(away));
  check('css: at rest no transform / opacity / will-change on #stage (the glass is as before)', !/(^|\s)transform:|opacity:\s*[\d.]|will-change/.test(rest.replace(/transition:[^;]+;/, '')), rest);
  const red = css.slice(css.indexOf('@media (prefers-reduced-motion: reduce)', motionAt));
  check('css: reduced motion = opacity only, 120 ms, no transform away', /#stage, body\.away #stage \{ transition: opacity 120ms linear; \}/.test(red) && /body\.away #stage \{ transform: none; \}/.test(red));
  check('css: .snap = no transition (also under reduced motion)', /body\.snap #stage \{ transition: none; \}/.test(css.slice(motionAt, css.indexOf('@media', motionAt))) && /body\.snap #stage \{ transition: none; \}/.test(red));
  check('tokens: glass-light.css defines --m-spring, --m-accel, --m-fade-in, --m-fade-out, --m-dur-l 560ms',
    ['--m-spring:', '--m-accel:', '--m-fade-in:', '--m-fade-out:'].every((t) => gl.includes(t)) && /--m-dur-l:\s*560ms/.test(gl));
  const iGl = html.indexOf('../shared/glass-light.css'), iTb = html.indexOf('href="topbar.css"');
  check('page: loads glass-light.css (the --m-* tokens) before topbar.css', iGl > 0 && iTb > iGl);
  const st = html.indexOf('<div id="stage">'), stEnd = html.indexOf('</div>\n<script', st);
  const inside = (id) => { const i = html.indexOf(`id="${id}"`); return i > st && i < stEnd; };
  check('page: #stage holds #bglass (+ #bdrop, #btint), #bar and #menus: one layer', st > 0 && ['bglass', 'bdrop', 'btint', 'bar', 'menus'].every(inside));
  check('page: motion.js loads before topbar.js', html.indexOf('src="motion.js"') > 0 && html.indexOf('src="motion.js"') < html.indexOf('src="topbar.js"'));
  const js = fs.readFileSync(path.join(__dirname, 'topbar.js'), 'utf8');
  check("page: topbar.js listens for 'topbar.motion' and gates backdrop / guard / under through look()",
    /mantle\.on\('topbar\.motion'/.test(js) && /mantle\.on\('topbar\.backdrop', \(p\) => look\('backdrop'/.test(js) && /mantle\.on\('topbar\.guard', \(g\) => look\('guard'/.test(js) && /mantle\.on\('topbar\.under', \(p\) => look\('under'/.test(js));
}

for (const f of failures) console.log('  FAIL ' + f);
console.log(`TOPBAR MOTION TEST: ${passed} passed, ${failures.length} failed`);
process.exit(failures.length ? 1 : 0);
