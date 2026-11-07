'use strict';
/*
 * tools/png-same.js - are two folders of screenshots the same, pixel for pixel? (lane mantle-bar-leak)
 *
 *   node tools/png-same.js <before dir> <after dir> [tolerance 0-255, default 0]
 *
 * Every <name>.png in <before dir> is decoded next to the same name in <after dir> (8-bit gray, RGB,
 * RGBA, gray+alpha; not interlaced: what Chromium writes) and compared channel by channel. One line a
 * file, then:   png-same TOTAL: 24 files: 24 same, 0 differ, 0 missing
 * Exit 1 when any file differs or is missing. No dependencies: zlib only.
 */
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

function decode(file) {
  const b = fs.readFileSync(file);
  if (b.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let o = 8, w = 0, h = 0, depth = 0, type = 0, inter = 0;
  const idat = [];
  while (o < b.length) {
    const len = b.readUInt32BE(o), t = b.toString('ascii', o + 4, o + 8), d = b.subarray(o + 8, o + 8 + len);
    if (t === 'IHDR') { w = d.readUInt32BE(0); h = d.readUInt32BE(4); depth = d[8]; type = d[9]; inter = d[12]; }
    else if (t === 'IDAT') idat.push(d);
    else if (t === 'IEND') break;
    o += 12 + len;
  }
  const ch = { 0: 1, 2: 3, 4: 2, 6: 4 }[type];
  if (depth !== 8 || !ch || inter) throw new Error('unsupported PNG (depth ' + depth + ', type ' + type + ', interlace ' + inter + ')');
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = w * ch, out = Buffer.alloc(stride * h);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= ch ? out[dst + x - ch] : 0, up = y ? out[dst - stride + x] : 0, c = x >= ch && y ? out[dst - stride + x - ch] : 0;
      let v = raw[src + x];
      if (f === 1) v += a; else if (f === 2) v += up; else if (f === 3) v += (a + up) >> 1;
      else if (f === 4) { const p = a + up - c, pa = Math.abs(p - a), pb = Math.abs(p - up), pc = Math.abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? up : c; }
      out[dst + x] = v & 255;
    }
  }
  return { w, h, ch, px: out };
}

if (require.main === module) {
  const [A, B, tolArg] = process.argv.slice(2);
  if (!A || !B) { process.stdout.write('usage: node tools/png-same.js <before dir> <after dir> [tolerance]\n'); process.exit(2); }
  const tol = Number(tolArg) || 0;
  let same = 0, differ = 0, missing = 0;
  const names = fs.readdirSync(A).filter((n) => /\.png$/i.test(n)).sort();
  for (const n of names) {
    const fb = path.join(B, n);
    if (!fs.existsSync(fb)) { missing++; process.stdout.write('png-same ' + n + ': MISSING in ' + B + '\n'); continue; }
    try {
      const a = decode(path.join(A, n)), b = decode(fb);
      if (a.w !== b.w || a.h !== b.h || a.ch !== b.ch) { differ++; process.stdout.write('png-same ' + n + ': DIFFERS in size ' + a.w + 'x' + a.h + 'x' + a.ch + ' vs ' + b.w + 'x' + b.h + 'x' + b.ch + '\n'); continue; }
      let bad = 0, worst = 0, x0 = -1, y0 = -1;
      for (let i = 0, p = 0; i < a.px.length; i += a.ch, p++) {
        let m = 0;
        for (let k = 0; k < a.ch; k++) m = Math.max(m, Math.abs(a.px[i + k] - b.px[i + k]));
        if (m > tol) { bad++; if (x0 < 0) { x0 = p % a.w; y0 = Math.floor(p / a.w); } }
        if (m > worst) worst = m;
      }
      if (bad) { differ++; process.stdout.write('png-same ' + n + ': DIFFERS: ' + bad + ' of ' + a.w * a.h + ' pixels, worst channel ' + worst + ', first at ' + x0 + ',' + y0 + '\n'); }
      else { same++; process.stdout.write('png-same ' + n + ': same (' + a.w + 'x' + a.h + (worst ? ', worst channel ' + worst + ' within ' + tol : '') + ')\n'); }
    } catch (e) { differ++; process.stdout.write('png-same ' + n + ': could not compare: ' + e.message + '\n'); }
  }
  process.stdout.write('png-same TOTAL: ' + names.length + ' files: ' + same + ' same, ' + differ + ' differ, ' + missing + ' missing\n');
  process.exit(differ || missing || !names.length ? 1 : 0);
}
module.exports = { decode };
