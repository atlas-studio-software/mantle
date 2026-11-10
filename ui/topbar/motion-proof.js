'use strict';
/*
 * motion-proof.js -- the proof for the top bar's full-screen step-aside (lane mantle-fullscreen-motion,
 * 2026-11-09; ui/topbar/motion.js). The user: "it needs to look nice when it goes away though, it can't look
 * like some cheap thing". Headless Chromium through playwright-core, the page over file:// with its mock
 * (mock-mantle.js), a made-up wallpaper and a made-up maximised window (#mock-maxwin) under the bar. CPU-HEAVY:
 * run it through cloudtest, never on the laptop.
 *
 *   FRAMES: per case (dark / light x normal / reduced motion) one PNG strip: the bar at rest, the leave at
 *           0/25/50/75/100 %, the arrive at 0/25/50/75/100 %, and a turn-round (an arrive at 50 % of the
 *           leave) at 0/25/50/75/100 % of the way back. Deterministic: the transitions are paused and their
 *           currentTime set. Dark plays the real handover: the colour under is NATIVE at rest (the sidecar's
 *           layer, played here by #mock-native, a plain strip of the same colour behind the bar's window), and
 *           at the leave's start the layer goes and the page paints the same colours itself (lib/topbar-under.js
 *           handoff), so rest and leave 0 % must be the same picture. The window under the bar goes full screen
 *           at the leave (its top row moves to the screen's top) and back before the arrive.
 *   CHECKS: rest == leave 0 % and rest == arrive 100 % (no swap, no pop), leave 100 % == the screen with no bar
 *           (no band, no strip left behind), the turn-round's first frame == the leave frame it turned at (no
 *           jump), the frames in between move monotonically; reduced motion never moves (opacity only); while
 *           leaving a new desktop picture is held and applied once the bar is gone; the page's away state.
 *   CPU:    a real-time leave + arrive, 6 times, against the same time idle: CDP SystemInfo.getProcessInfo
 *           cpuTime per process type (browser / renderer / GPU), the renderer's Performance metrics, and the
 *           frames the page drew (rAF) with the longest gap. Dark (the blurred glass) and light (no blur).
 *   node ui/topbar/motion-proof.js   -> "TOPBAR MOTION PROOF: n passed, m failed" (+ strips in MOTION_OUT)
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', '..');
const OUT = process.env.MOTION_OUT || path.join(ROOT, '.proof');
const PAGE = 'file:///' + path.join(__dirname, 'index.html').replace(/\\/g, '/');
const VW = 1536, VH = 48, DPR = 1.25;   // the bar (28) and 20 px of the screen under it
const FR = [0, 0.25, 0.5, 0.75, 1];

let passed = 0;
const failures = [];
const results = { cases: {}, cpu: {} };
function check(name, ok, detail) {
  if (ok) passed++;
  else failures.push(name + (detail !== undefined ? ' -- ' + (typeof detail === 'string' ? detail : JSON.stringify(detail)).slice(0, 500) : ''));
  return ok;
}

/* a made-up wallpaper: colour bands and a few bright blobs, so the blur and any band left behind show */
async function makeScene(browser) {
  const p = await browser.newPage();
  const b64 = await p.evaluate(({ w, h }) => {
    const c = document.createElement('canvas'); c.width = w; c.height = h;
    const g = c.getContext('2d');
    const lg = g.createLinearGradient(0, 0, w, h);
    lg.addColorStop(0, '#2b4c7e'); lg.addColorStop(0.35, '#c2763a'); lg.addColorStop(0.7, '#3d7a5a'); lg.addColorStop(1, '#1d2433');
    g.fillStyle = lg; g.fillRect(0, 0, w, h);
    const blobs = [[120, 10, 60, '#ffe9a8'], [520, 30, 90, '#7fd0ff'], [900, 0, 70, '#ffffff'], [1300, 20, 110, '#ff8fa3']];
    for (const [x, y, r, col] of blobs) { const rg = g.createRadialGradient(x, y, 0, x, y, r); rg.addColorStop(0, col); rg.addColorStop(1, 'rgba(0,0,0,0)'); g.fillStyle = rg; g.fillRect(x - r, y - r, 2 * r, 2 * r); }
    for (let x = 0; x < w; x += 48) { g.fillStyle = x % 96 ? 'rgba(255,255,255,.08)' : 'rgba(0,0,0,.10)'; g.fillRect(x, 0, 24, h); }
    return c.toDataURL('image/png').split(',')[1];
  }, { w: VW, h: 864 });
  await p.close();
  const f = path.join(OUT, 'scene.png');
  fs.writeFileSync(f, Buffer.from(b64, 'base64'));
  return 'file:///' + f.replace(/\\/g, '/');
}

const darkInit = () => { const set = () => { if (document.documentElement) document.documentElement.setAttribute('data-theme', 'dark'); }; set(); document.addEventListener('readystatechange', set); };

async function openCase(browser, scene, dark, reduced) {
  const p = await browser.newPage({ viewport: { width: VW, height: VH }, screen: { width: VW, height: 864 }, deviceScaleFactor: DPR, bypassCSP: true, reducedMotion: reduced ? 'reduce' : 'no-preference' });
  if (dark) await p.addInitScript(darkInit);
  const errs = [];
  p.on('pageerror', (e) => errs.push(e.message));
  await p.goto(PAGE + '?app=chrome&maxwin=1&scene=' + encodeURIComponent(scene));
  await p.waitForFunction(() => document.documentElement.dataset.ready === '1' && document.documentElement.dataset.scene === '1', null, { timeout: 10000 });
  await p.waitForFunction(() => { const G = window.__topbarGlass || {}; return !document.body.classList.contains('glass') || G.tint != null; }, null, { timeout: 8000 }).catch(() => {});
  if (dark) {
    // a maximised window (#202020) under the bar: the colour under is its top row, NATIVE (the sidecar's layer)
    await p.evaluate(() => {
      const n = 192, cols = new Array(n * 3).fill(32), cover = new Array(n).fill(1);
      window.__under = { cols, cover, n, blocked: 0, why: null, t: Date.now() };
      const nat = document.createElement('div');
      nat.id = 'mock-native';
      for (const [k, v] of [['position', 'fixed'], ['left', '0'], ['right', '0'], ['top', '0'], ['height', '28px'], ['background', 'rgb(32,32,32)']]) nat.style.setProperty(k, v);
      document.body.insertBefore(nat, document.getElementById('stage'));
      window.mantle.emit('topbar.under', Object.assign({}, window.__under, { native: true }));
    });
  }
  await p.waitForTimeout(400);
  p._errs = errs;
  return p;
}

/* in the page: main's leave (one task, as main sends it), then every transition on #stage paused at 0 */
const LEAVE = () => {
  const stage = document.getElementById('stage');
  window.mantle.emit('topbar.close', {});
  window.mantle.emit('topbar.motion', { phase: 'leave', ms: 260, kind: 'video' });
  const nat = document.getElementById('mock-native');
  if (nat) { nat.remove(); window.mantle.emit('topbar.under', Object.assign({}, window.__under, { native: false })); }   // the handoff
  getComputedStyle(stage).transform;
  const a = stage.getAnimations();
  a.forEach((x) => { x.pause(); x.currentTime = 0; });
  // a new desktop picture right after the leave's start (phase 'leaving'): it must be held
  let probe = null;
  if (window.__probe) {
    const before = document.getElementById('bdrop').style.backgroundImage;
    const c = document.createElement('canvas'); c.width = 384; c.height = 7; const g = c.getContext('2d'); g.fillStyle = '#ff00ff'; g.fillRect(0, 0, 384, 7);
    window.mantle.emit('topbar.backdrop', { png: c.toDataURL('image/png'), ms: 0 });
    probe = { same: document.getElementById('bdrop').style.backgroundImage === before, held: window.__topbarMotion.view().held, phase: window.__topbarMotion.phase() };
    window.__probe = false;
  }
  return { probe, a: a.map((x) => ({ p: x.transitionProperty, d: Math.round(x.effect.getTiming().duration) })) };
};
const FULL = (on) => { const mw = document.getElementById('mock-maxwin'); if (mw) mw.style.top = on ? '0px' : '28px'; };   // the window under the bar goes full screen / back
const ARRIVE = (turn) => {
  const stage = document.getElementById('stage');
  window.mantle.emit('topbar.motion', { phase: 'arrive', why: turn ? 'back while it was leaving' : 'shown' });
  if (window.__under) window.mantle.emit('topbar.under', Object.assign({}, window.__under, { native: false }));   // the first read after the show
  getComputedStyle(stage).transform;
  const a = stage.getAnimations().filter((x) => x.playState !== 'finished');
  a.forEach((x) => { x.pause(); x.currentTime = 0; });
  return a.map((x) => ({ p: x.transitionProperty, d: Math.round(x.effect.getTiming().duration) }));
};
/* every paused transition on #stage at `ms` (capped at its own duration) */
const AT = (ms) => {
  const stage = document.getElementById('stage');
  stage.getAnimations().forEach((x) => { const d = x.effect.getTiming().duration; x.currentTime = Math.min(ms, d); });
  const cs = getComputedStyle(stage);
  return { transform: cs.transform, opacity: Number(cs.opacity) };
};
const END = () => { document.getElementById('stage').getAnimations().forEach((x) => x.finish()); };

async function shot(p) { await p.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))); return p.screenshot({ type: 'png' }); }

/* pixel maths in a scratch page: max channel difference and the share of pixels that differ by more than 2 */
async function diff(lab, a, b) {
  return lab.evaluate(async ({ a, b }) => {
    const img = (s) => new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = 'data:image/png;base64,' + s; });
    const [ia, ib] = [await img(a), await img(b)];
    const px = (i) => { const c = document.createElement('canvas'); c.width = i.width; c.height = i.height; const g = c.getContext('2d'); g.drawImage(i, 0, 0); return g.getImageData(0, 0, i.width, i.height).data; };
    const da = px(ia), db = px(ib);
    let max = 0, over = 0, x0 = 1e9, x1 = -1, y0 = 1e9, y1 = -1;
    for (let k = 0; k < da.length; k += 4) {
      const m = Math.max(Math.abs(da[k] - db[k]), Math.abs(da[k + 1] - db[k + 1]), Math.abs(da[k + 2] - db[k + 2]));
      if (m > max) max = m;
      if (m > 2) { over++; const i = k / 4, x = i % ia.width, y = (i / ia.width) | 0; x0 = Math.min(x0, x); x1 = Math.max(x1, x); y0 = Math.min(y0, y); y1 = Math.max(y1, y); }
    }
    return { max, share: Math.round((over / (da.length / 4)) * 10000) / 10000, box: over ? [x0, y0, x1, y1] : null };
  }, { a: a.toString('base64'), b: b.toString('base64') });
}

/* one PNG strip: a label column and the frames stacked */
async function strip(lab, rows, file, title) {
  const b64 = await lab.evaluate(async ({ rows, title }) => {
    const img = (s) => new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = 'data:image/png;base64,' + s; });
    const ims = [];
    for (const r of rows) ims.push(await img(r.png));
    const LW = 230, G = 6, HEAD = 34, w = LW + ims[0].width, h = HEAD + ims.reduce((s, i) => s + i.height + G, 0);
    const c = document.createElement('canvas'); c.width = w; c.height = h;
    const g = c.getContext('2d');
    g.fillStyle = '#e9eaee'; g.fillRect(0, 0, w, h);
    g.fillStyle = '#16181d'; g.font = '600 16px Segoe UI, Arial, sans-serif'; g.fillText(title, 10, 22);
    let y = HEAD;
    g.font = '13px Segoe UI, Arial, sans-serif';
    ims.forEach((im, k) => {
      g.fillStyle = rows[k].sep ? '#c9ccd4' : '#f6f7f9'; g.fillRect(0, y, LW - 4, im.height);
      g.fillStyle = '#16181d'; g.fillText(rows[k].label, 8, y + im.height / 2 + 5);
      g.drawImage(im, LW, y);
      y += im.height + G;
    });
    return c.toDataURL('image/png').split(',')[1];
  }, { rows: rows.map((r) => ({ label: r.label, png: r.png.toString('base64'), sep: r.sep })), title });
  fs.writeFileSync(file, Buffer.from(b64, 'base64'));
}

const ty = (m) => { const x = /matrix\(([^)]+)\)/.exec(m || ''); return x ? Number(x[1].split(',')[5]) : 0; };

async function frames(browser, lab, scene, dark, reduced) {
  const name = `${dark ? 'dark' : 'light'}-${reduced ? 'reduced' : 'normal'}`;
  const R = { name, frames: {} };
  const p = await openCase(browser, scene, dark, reduced);
  const rows = [];
  const restMax = await shot(p);   // the bar over a maximised window (before, and where the arrive must land)
  rows.push({ label: 'rest, a maximised window under', png: restMax });
  await p.evaluate(FULL, true);
  const rest = await shot(p);      // the window went full screen; main asks the bar to leave
  rows.push({ label: 'rest, it went full screen', png: rest });
  if (dark) check(`${name}: (set-up) the colour under is native at rest`, await p.evaluate(() => document.body.classList.contains('native')));
  // THE LEAVE
  if (dark) await p.evaluate(() => { window.__probe = true; });
  const L0 = await p.evaluate(LEAVE);
  const lv = L0.a;
  R.leaveTransitions = lv;
  if (dark) check(`${name}: a new desktop picture while leaving is held (the bar leaves in the look it had)`, L0.probe && L0.probe.same && L0.probe.held.includes('backdrop') && L0.probe.phase === 'leaving', L0.probe);
  const D_OUT = Math.max(...lv.map((x) => x.d));
  check(`${name}: the leave transitions only transform/opacity, ${reduced ? '120' : '252'} ms`,
    lv.length > 0 && lv.every((x) => (reduced ? x.p === 'opacity' : /^(transform|opacity)$/.test(x.p))) && D_OUT === (reduced ? 120 : 252), lv);
  check(`${name}: leave -> body.away, the motion leaving`, await p.evaluate(() => document.body.classList.contains('away') && window.__topbarMotion.phase() === 'leaving'));
  if (dark) check(`${name}: the handoff -> the page paints the colours itself (no body.native) at the leave's start`, await p.evaluate(() => !document.body.classList.contains('native')));
  const leave = [];
  for (const f of FR) {
    const st = await p.evaluate(AT, f * D_OUT);
    const png = await shot(p);
    leave.push({ f, st, png });
    rows.push({ label: `leave ${Math.round(f * 100)}% (${Math.round(f * D_OUT)} ms) o=${st.opacity.toFixed(2)} y=${ty(st.transform).toFixed(1)}`, png });
  }
  R.leave = leave.map((x) => ({ f: x.f, opacity: x.st.opacity, y: ty(x.st.transform) }));
  const d0 = await diff(lab, rest, leave[0].png);
  R.restVsLeave0 = d0;
  check(`${name}: rest == leave 0 % (no swap at the start: the handover is seamless)`, d0.max <= 3, d0);
  // the screen with no bar at all: what the leave's last frame must be
  await p.evaluate(() => { document.getElementById('stage').style.visibility = 'hidden'; });
  const bare = await shot(p);
  await p.evaluate(() => { document.getElementById('stage').style.visibility = ''; });
  const dEnd = await diff(lab, bare, leave[4].png);
  R.leave100VsBare = dEnd;
  check(`${name}: leave 100 % == the screen with no bar (no band, no strip left behind)`, dEnd.max <= 2, dEnd);
  const ops = R.leave.map((x) => x.opacity), ys = R.leave.map((x) => x.y);
  check(`${name}: the leave fades monotonically to 0`, ops.every((v, i) => i === 0 || v <= ops[i - 1] + 1e-6) && ops[4] === 0 && ops[0] === 1, ops);
  check(`${name}: ${reduced ? 'reduced motion never moves' : 'the leave slides up monotonically to -28 px'}`,
    reduced ? ys.every((v) => v === 0) : ys.every((v, i) => i === 0 || v <= ys[i - 1] + 1e-6) && Math.abs(ys[4] + 28) < 0.01 && ys[0] === 0, ys);
  check(`${name}: the leave's last frame is fully clear (opacity 0)`, ops[4] === 0);
  // away: the leave's end, the phase timer, the held picture applied (invisible)
  await p.evaluate(END);
  await p.waitForFunction(() => window.__topbarMotion.phase() === 'away', null, { timeout: 2000 }).catch(() => {});
  check(`${name}: away after the leave`, await p.evaluate(() => window.__topbarMotion.phase() === 'away' && document.body.classList.contains('away')));
  if (dark) {
    check(`${name}: away -> the held picture is applied (invisibly)`, await p.evaluate(() => /data:image/.test(document.getElementById('bdrop').style.backgroundImage) && window.__topbarMotion.view().held.length === 0 && window.__topbarGlass.png && window.__topbarGlass.png.length < 2000));
    await p.close();   // the arrive starts from a fresh page with the scene's own strip (the magenta one was a probe)
  }
  // THE ARRIVE (fresh page for dark: the strip and the native set-up as at rest, then a real leave to away)
  const q = dark ? await openCase(browser, scene, dark, reduced) : p;
  if (dark) { await q.evaluate(FULL, true); await q.evaluate(LEAVE); await q.evaluate(END); await q.waitForFunction(() => window.__topbarMotion.phase() === 'away', null, { timeout: 2000 }).catch(() => {}); }
  await q.evaluate(FULL, false);   // full screen is over; main shows the window (still in its away frame) and says arrive
  const av = await q.evaluate(ARRIVE, false);
  R.arriveTransitions = av;
  const D_IN = Math.max(...av.map((x) => x.d));
  check(`${name}: the arrive: transform ${reduced ? 'never' : '560 ms on the spring'}, opacity ${reduced ? '120' : '224'} ms`,
    reduced ? av.length === 1 && av[0].p === 'opacity' && av[0].d === 120 : av.some((x) => x.p === 'transform' && x.d === 560) && av.some((x) => x.p === 'opacity' && x.d === 224), av);
  check(`${name}: arrive -> body.away cleared, the motion arriving`, await q.evaluate(() => !document.body.classList.contains('away') && window.__topbarMotion.phase() === 'arriving'));
  const arrive = [];
  for (const f of FR) {
    const st = await q.evaluate(AT, f * D_IN);
    const png = await shot(q);
    arrive.push({ f, st, png });
    rows.push({ label: `arrive ${Math.round(f * 100)}% (${Math.round(f * D_IN)} ms) o=${st.opacity.toFixed(2)} y=${ty(st.transform).toFixed(1)}`, png, sep: f === 0 });
  }
  R.arrive = arrive.map((x) => ({ f: x.f, opacity: x.st.opacity, y: ty(x.st.transform) }));
  // (the leave's bare screen had the window full screen; the arrive's has it back under the bar: its own bare screen)
  await q.evaluate(() => { document.getElementById('stage').style.visibility = 'hidden'; });
  const bare2 = await shot(q);
  await q.evaluate(() => { document.getElementById('stage').style.visibility = ''; });
  const dA0b = await diff(lab, bare2, arrive[0].png);
  R.arrive0VsBare = dA0b;
  check(`${name}: arrive 0 % == the screen with no bar (the first frame shown is the empty one)`, dA0b.max <= 2, dA0b);
  await q.evaluate(END);
  const after = await shot(q);
  const dA = await diff(lab, restMax, arrive[4].png);
  R.restVsArrive100 = dA;
  check(`${name}: arrive 100 % == rest (it lands exactly where it was, no pop at the end)`, dA.max <= 3, dA);
  const dAfter = await diff(lab, arrive[4].png, after);
  check(`${name}: the transition's end == the settled bar a frame later`, dAfter.max <= 3, dAfter);
  const aops = R.arrive.map((x) => x.opacity);
  check(`${name}: the arrive fades in to 1`, aops[0] === 0 && aops[4] === 1 && aops.every((v, i) => i === 0 || v >= aops[i - 1] - 1e-6), aops);
  // THE TURN-ROUND: an arrive at 50 % of the leave, the window never hidden
  await q.waitForTimeout(reduced ? 200 : 700);
  await q.evaluate(LEAVE);   // (over the maximised window: a leave that turns round before it is done)
  const mid = await q.evaluate(AT, 0.5 * D_OUT);
  const midPng = await shot(q);
  rows.push({ label: `turn-round: leave 50% o=${mid.opacity.toFixed(2)} y=${ty(mid.transform).toFixed(1)}`, png: midPng, sep: true });
  const tv = await q.evaluate(ARRIVE, true);
  const D_T = Math.max(...tv.map((x) => x.d));
  R.turnTransitions = tv;
  const turn = [];
  for (const f of FR) {
    const st = await q.evaluate(AT, f * D_T);
    const png = await shot(q);
    turn.push({ f, st, png });
    rows.push({ label: `  back ${Math.round(f * 100)}% (${Math.round(f * D_T)} ms) o=${st.opacity.toFixed(2)} y=${ty(st.transform).toFixed(1)}`, png });
  }
  R.turn = { at: { opacity: mid.opacity, y: ty(mid.transform) }, back: turn.map((x) => ({ f: x.f, opacity: x.st.opacity, y: ty(x.st.transform) })) };
  const dT = await diff(lab, midPng, turn[0].png);
  R.turnJump = dT;
  // (measured: in dark one physical row, the moving bar's anti-aliased bottom edge at a fractional y, can differ by up
  // to ~18 levels between the paused leave frame and the new transition's first: a re-raster of the same pose, the
  // pose itself is identical; any other difference fails)
  const edgeOnly = dT.box && dT.box[1] === dT.box[3] && Math.abs(dT.box[1] - Math.round((28 + ty(mid.transform)) * DPR)) <= 1;
  R.turnEdgeOnly = !!edgeOnly;
  check(`${name}: the turn-round's first frame == the pose it turned at (no jump; at most the bottom edge's AA row)`, (dT.max <= 3 || edgeOnly) && Math.abs(turn[0].st.opacity - mid.opacity) < 0.01 && Math.abs(ty(turn[0].st.transform) - ty(mid.transform)) < 0.05, { dT, mid, t0: turn[0].st });
  check(`${name}: the turn-round lands at rest`, turn[4].st.opacity === 1 && Math.abs(ty(turn[4].st.transform)) < 0.05, turn[4].st);
  check(`${name}: no page errors`, (p._errs || []).concat(q._errs || []).length === 0, (p._errs || []).concat(q._errs || []));
  const file = path.join(OUT, `motion-strip-${name}.png`);
  await strip(lab, rows, file, `Top bar full-screen step-aside: ${name} (1536x48 css px at 1.25; window clips at 28 px, 20 px of screen under)`);
  R.strip = file;
  await q.close();
  if (q !== p && !p.isClosed()) await p.close();
  results.cases[name] = R;
}

/* CPU: real time, CDP, against an idle baseline of the same length */
async function cpu(browser, scene, dark) {
  const name = dark ? 'dark' : 'light';
  const p = await openCase(browser, scene, dark, false);
  if (dark) await p.evaluate(() => { const nat = document.getElementById('mock-native'); if (nat) nat.remove(); window.mantle.emit('topbar.under', Object.assign({}, window.__under, { native: false })); });   // as after the handoff
  const cdp = await p.context().newCDPSession(p);
  await cdp.send('Performance.enable');
  let bcdp = null;
  try { bcdp = await browser.newBrowserCDPSession(); } catch (_) { bcdp = null; }
  const procs = async () => {
    if (!bcdp) return null;
    try { const r = await bcdp.send('SystemInfo.getProcessInfo'); const by = {}; for (const x of r.processInfo) by[x.type] = (by[x.type] || 0) + x.cpuTime; return by; } catch (e) { return { error: e.message }; }
  };
  const metrics = async () => { const r = await cdp.send('Performance.getMetrics'); const m = {}; for (const x of r.metrics) m[x.name] = x.value; return m; };
  const N = 6, CYCLE = 1400;   // leave 252 + away 300 + arrive 560 + rest ~290
  const window_ = async (moving) => {
    await p.evaluate(() => { window.__raf = []; const f = (t) => { window.__raf.push(t); if (window.__rafOn) requestAnimationFrame(f); }; window.__rafOn = true; requestAnimationFrame(f); });
    const p0 = await procs(), m0 = await metrics(), t0 = Date.now();
    for (let i = 0; i < N; i++) {
      if (moving) {
        await p.evaluate(() => { window.mantle.emit('topbar.motion', { phase: 'leave', ms: 260, kind: 'video' }); });
        await p.waitForTimeout(552);
        await p.evaluate(() => { window.mantle.emit('topbar.motion', { phase: 'arrive', why: 'shown' }); });
        await p.waitForTimeout(CYCLE - 552);
      } else await p.waitForTimeout(CYCLE);
    }
    const ms = Date.now() - t0, p1 = await procs(), m1 = await metrics();
    const raf = await p.evaluate(() => { window.__rafOn = false; const r = window.__raf; let gap = 0; for (let i = 1; i < r.length; i++) gap = Math.max(gap, r[i] - r[i - 1]); return { n: r.length, maxGap: Math.round(gap * 10) / 10 }; });
    const dp = {};
    if (p0 && p1 && !p0.error && !p1.error) for (const k of Object.keys(p1)) dp[k] = Math.round(((p1[k] - (p0[k] || 0)) * 1000) * 10) / 10;   // cpuTime is seconds
    const dm = {};
    for (const k of ['TaskDuration', 'ScriptDuration', 'LayoutDuration', 'RecalcStyleDuration']) dm[k] = Math.round(((m1[k] || 0) - (m0[k] || 0)) * 1000 * 10) / 10;
    return { ms, cpuMsByProcess: dp, procError: p1 && p1.error, renderer: dm, raf };
  };
  const idle = await window_(false);
  const moving = await window_(true);
  const per = {};
  for (const k of Object.keys(moving.cpuMsByProcess)) per[k] = Math.round(((moving.cpuMsByProcess[k] - (idle.cpuMsByProcess[k] || 0)) / N) * 10) / 10;
  const R = { name, cycles: N, cycleMs: CYCLE, idle, moving, extraCpuMsPerLeaveArrive: per,
    extraPctOfOneCoreOverTheTransition: Object.fromEntries(Object.entries(per).map(([k, v]) => [k, Math.round((v / (252 + 560)) * 1000) / 10])) };
  results.cpu[name] = R;
  check(`cpu ${name}: measured (${idle.procError || moving.procError ? 'process CPU unavailable: ' + (idle.procError || moving.procError) : 'per process'})`, !idle.procError && !moving.procError && Object.keys(moving.cpuMsByProcess).length > 0, R);
  check(`cpu ${name}: the page drew frames all through the transitions (rAF max gap < 50 ms)`, moving.raf.maxGap < 50, moving.raf);
  await p.close();
}

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const { chromium } = require('../../tools/dev-env.js').playwright();
  const browser = await require('../settings/test-browser.js').launch(chromium, { headless: true, args: ['--allow-file-access-from-files'] });
  try {
    const lab = await browser.newPage();
    const scene = await makeScene(browser);
    for (const dark of [true, false]) for (const reduced of [false, true]) {
      try { await frames(browser, lab, scene, dark, reduced); } catch (e) { failures.push(`${dark ? 'dark' : 'light'}-${reduced ? 'reduced' : 'normal'}: the run threw: ${(e && e.stack || e).toString().split('\n').slice(0, 3).join(' | ')}`); }
    }
    for (const dark of [true, false]) {
      try { await cpu(browser, scene, dark); } catch (e) { failures.push(`cpu ${dark ? 'dark' : 'light'}: the run threw: ${(e && e.message || e).toString().split('\n')[0]}`); }
    }
  } finally { await browser.close(); }
  fs.writeFileSync(path.join(OUT, 'motion-proof.json'), JSON.stringify(results, null, 1));
  for (const [k, c] of Object.entries(results.cpu)) console.log(`cpu ${k}: extra ms of CPU per leave+arrive by process ${JSON.stringify(c.extraCpuMsPerLeaveArrive)} (= % of one core over the 812 ms: ${JSON.stringify(c.extraPctOfOneCoreOverTheTransition)}); idle ${JSON.stringify(c.idle.cpuMsByProcess)} vs moving ${JSON.stringify(c.moving.cpuMsByProcess)} over ${c.moving.ms} ms; renderer moving ${JSON.stringify(c.moving.renderer)}; rAF ${JSON.stringify(c.moving.raf)}`);
  for (const c of Object.values(results.cases)) console.log(`strip ${c.name}: ${c.strip}`);
  for (const f of failures) console.log('  FAIL ' + f);
  console.log(`TOPBAR MOTION PROOF: ${passed} passed, ${failures.length} failed`);
  process.exitCode = failures.length ? 1 : 0;
})();
