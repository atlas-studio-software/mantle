// gen-levitate-vectors.js - the hand-computed placeholder vectors of the levitated look's brightness check (lane r10-newlook).
//
// Writes Fixtures/look/v2/film.json from the v2 check's formula, written out here in plain JS the way the prototype
// (quartz-home/app.js, 2026-11-24) states it: the dimmed sample, the WCAG luminance, the brightest-but-four of 40, and the
// a += 0.04 step loop with the same floating-point accumulation. look-sync v2's own vectors replace this file when they land.
// Also prints the motion curves' and the blur's reference values the unit tests copy. Run: node gen-levitate-vectors.js
'use strict';
const fs = require('fs');
const path = require('path');

const FILM = [10, 12, 14];
const lin = v => { const c = v / 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
const L = ([r, g, b]) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
const lumOf = (rgb, a) => L(rgb.map((v, k) => v * (1 - a) + FILM[k] * a));
function film(samples, dim) {
  const s = samples.map(c => c.map(v => v * (1 - dim)));
  const sorted = s.map((c, i) => ({ c, l: L(c), i })).sort((p, q) => q.l - p.l);   // stable (ES2019)
  if (!sorted.length) return 0;
  const bright = sorted[Math.floor(sorted.length * 0.1)].c;
  let a = 0;
  while (a < 0.56 && lumOf(bright, a) > 0.163) a += 0.04;
  return Math.min(0.56, a);
}
const fill = (n, c) => Array.from({ length: n }, () => c.slice());

const cases = [
  { name: 'dark picture: no film', dim: 0.20, samples: fill(40, [30, 40, 45]) },
  { name: 'white at the default dim', dim: 0.20, samples: fill(40, [255, 255, 255]) },
  { name: 'white at dim 0.50: dim lowers the film', dim: 0.50, samples: fill(40, [255, 255, 255]) },
  { name: 'white undimmed: the 0.56 cap', dim: 0.0, samples: fill(40, [255, 255, 255]) },
  { name: 'four white of forty: the top 10% is ignored', dim: 0.20, samples: fill(4, [255, 255, 255]).concat(fill(36, [30, 40, 45])) },
  { name: 'five white of forty: index 4 is white', dim: 0.20, samples: fill(35, [30, 40, 45]).concat(fill(5, [255, 255, 255])) },
];
// not in the file (unit tests): white at dim 0.90 -> 0; pale sky (170,200,225) at dim 0.30 -> 0.2
for (const c of cases) c.film = film(c.samples, c.dim);

const out = {
  source: 'hand-computed, replace with look-sync v2 vectors',
  made: 'native/tests/Mantle.Core.Tests/Look/gen-levitate-vectors.js from the r10-newlook brief formula (prototype quartz-home/app.js 2026-11-24)',
  tolerance: 1e-9,
  cases: cases.map(c => ({ name: c.name, dim: c.dim, film: c.film, samples: c.samples })),
};
const file = path.join(__dirname, '..', 'Fixtures', 'look', 'v2', 'film.json');
fs.mkdirSync(path.dirname(file), { recursive: true });
let json = JSON.stringify(out, null, 2).replace(/\[\s+(\d+),\s+(\d+),\s+(\d+)\s+\]/g, '[$1, $2, $3]');
fs.writeFileSync(file, json.replace(/\n/g, '\r\n') + '\r\n');
for (const c of cases) console.log('film', JSON.stringify(c.name), c.dim, '->', c.film, 'lum(bright,0)=', lumOf(c.samples.map(x => x.map(v => v * (1 - c.dim))).sort((p, q) => L(q) - L(p))[4], 0));

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
