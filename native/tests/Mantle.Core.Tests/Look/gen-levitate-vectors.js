// gen-levitate-vectors.js - reference values the levitated look's unit tests copy (lane r10-newlook): the motion
// curves and the blur. Run: node gen-levitate-vectors.js
//
// The brightness check's vectors are NOT made here any more: Fixtures/look/v2/lift-v2.json is look-sync's own file
// (look-sync 9a53e89 test/lift-v2.json, made by its lift-v2.gen.js from the prototype's code); this script's old
// hand-computed placeholder (film.json) is gone.
'use strict';

// cubic-bezier(x1, y1, x2, y2) at progress x (bisection on t, to 1e-12)
function bez(x1, y1, x2, y2, x) {
  const f = (p1, p2, t) => 3 * p1 * t * (1 - t) * (1 - t) + 3 * p2 * t * t * (1 - t) + t * t * t;
  let lo = 0, hi = 1;
  for (let k = 0; k < 100; k++) { const m = (lo + hi) / 2; if (f(x1, x2, m) < x) lo = m; else hi = m; }
  return f(y1, y2, (lo + hi) / 2);
}
for (const x of [0.1, 0.25, 0.5, 0.75, 0.9]) console.log('ease', x, bez(0.2, 0.8, 0.2, 1, x).toFixed(6), 'lift', bez(0.22, 1.12, 0.36, 1, x).toFixed(6));
let mx = 0, at = 0;
for (let k = 0; k <= 10000; k++) { const y = bez(0.22, 1.12, 0.36, 1, k / 10000); if (y > mx) { mx = y; at = k / 10000; } }
console.log('lift max', mx.toFixed(6), 'at x', at);

// the blur: one row [0,0,0,255,0,0,0] at sigma 1 (radius 3), clamp-padded
const r = 3, w = []; let sum = 0;
for (let k = -r; k <= r; k++) { const v = Math.exp(-(k * k) / 2); w.push(v); sum += v; }
const wn = w.map(v => v / sum);
console.log('sigma1 weights', wn.map(v => v.toFixed(8)).join(' '), 'centre 255*w0 =', (255 * wn[3]).toFixed(4), 'next', (255 * wn[2]).toFixed(4), 'edge (x=0, 3 away)', (255 * wn[0]).toFixed(4));
