'use strict';
/*
 * tools/img-mem-probe.js - what does Chromium keep for each NEW data: image a page shows and drops?
 * (lane mantle-slim-bar, 2026-11-06; companion to tools/bar-mem-probe.js)
 *
 * The bar's renderer kept ~2x the size of every unique data: picture it ever drew (album art, Library
 * covers, backdrop strips), even after forced GC and a critical memory-pressure signal. This runs the same
 * job several ways in Mantle's own Electron, one hidden window each, so the fix is measured, not guessed:
 *   img-data      <img src="data:..."> shown, then removed (what the bar did)
 *   img-same      the SAME pictures every cycle (control: should stay flat)
 *   img-blob      the bytes as a Blob, <img src="blob:...">, URL revoked when removed
 *   css-data      a data: url() as a background-image, then cleared
 *   bitmap        createImageBitmap(blob) drawn into a <canvas>, bitmap.close()
 *   img-data-clear  img-data plus webFrame.clearCache() after each cycle
 *   img-data-gc   img-data with a forced GC after every cycle
 * Each picture is 300x300 and compresses well (a smooth gradient: ~1/20 of its decoded size), so the
 * kept bytes say WHICH copy is kept: ~360 KB per picture = the decoded pixels, a few KB = the URL/bytes.
 *
 *   electron tools/img-mem-probe.js        (IMG_PROBE_N cycles, default 30, of IMG_PROBE_K=20 pictures)
 * Hidden windows only (show:false + lib/test-quiet.js); its own temp profile; never Mantle's.
 */
const path = require('path');
const os = require('os');
const fs = require('fs');
const zlib = require('zlib');
require(path.join(__dirname, '..', 'lib', 'test-quiet.js')).install(['--test'], process.env);
const { app, BrowserWindow } = require('electron');

const N = Math.max(2, Number(process.env.IMG_PROBE_N) || 30);
const K = Math.max(1, Number(process.env.IMG_PROBE_K) || 20);
const SIDE = 300;
const VARIANTS = (process.env.IMG_PROBE || 'img-data,img-same,img-blob,css-data,bitmap,img-data-clear,img-data-gc').split(',');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-img-probe-'));
app.setPath('userData', path.join(dir, 'profile'));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const CRC = (() => { const t = new Int32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c; } return t; })();
function crc32(buf) { let c = -1; for (const b of buf) c = CRC[(c ^ b) & 255] ^ (c >>> 8); return (c ^ -1) >>> 0; }
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
let seed = 7;
function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; }
/* a smooth gradient, PNG filter Sub per row: decodes to SIDE*SIDE*4 bytes, encodes to a few KB */
function png() {
  const a = [rnd() * 255, rnd() * 255, rnd() * 255], b = [rnd() * 255, rnd() * 255, rnd() * 255];
  const w = SIDE, h = SIDE, raw = Buffer.alloc((w * 4 + 1) * h);
  let o = 0;
  for (let y = 0; y < h; y++) {
    raw[o++] = 1;
    let prev = [0, 0, 0, 0];
    for (let x = 0; x < w; x++) {
      const t = y / h;
      const px = [0, 1, 2].map((k) => Math.round(a[k] + (b[k] - a[k]) * t)).concat([255]);
      for (let k = 0; k < 4; k++) raw[o++] = (px[k] - prev[k]) & 255;
      prev = px;
    }
  }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  const out = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
  return 'data:image/png;base64,' + out.toString('base64');
}

const PRELOAD = path.join(dir, 'preload.js');
fs.writeFileSync(PRELOAD, "const { contextBridge, webFrame } = require('electron');\n" +
  "contextBridge.exposeInMainWorld('probe', { clearCache: () => webFrame.clearCache(), usage: () => webFrame.getResourceUsage() });\n");
const PAGE = path.join(dir, 'page.html');
fs.writeFileSync(PAGE, '<!doctype html><meta http-equiv="Content-Security-Policy" content="default-src \'none\'; script-src \'unsafe-inline\'; style-src \'unsafe-inline\'; img-src data: blob:">' +
  '<body style="margin:0;background:#222"><div id="box" style="display:flex;flex-wrap:wrap"></div><script>' +
  'function bytes(u){var s=atob(u.slice(u.indexOf(",")+1)),a=new Uint8Array(s.length);for(var i=0;i<s.length;i++)a[i]=s.charCodeAt(i);return new Blob([a],{type:"image/png"});}' +
  'window.urls=[];' +
  'window.show=async function(kind,list){var box=document.getElementById("box");' +
  ' box.querySelectorAll("img").forEach(function(i){if(i.src.indexOf("blob:")===0)URL.revokeObjectURL(i.src);});' +
  ' box.textContent="";var waits=[];' +
  ' for(var j=0;j<list.length;j++){var u=list[j];' +
  '  if(kind==="css-data"){var d=document.createElement("div");d.style.cssText="width:34px;height:34px;background-size:cover;background-image:url(\\""+u+"\\")";box.appendChild(d);}' +
  '  else if(kind==="bitmap"){var c=document.createElement("canvas");c.width=68;c.height=68;c.style.width="34px";c.style.height="34px";box.appendChild(c);' +
  '   waits.push(createImageBitmap(bytes(u),{resizeWidth:68,resizeHeight:68,resizeQuality:"high"}).then((function(c){return function(bm){c.getContext("2d").drawImage(bm,0,0);bm.close();};})(c)));}' +
  '  else{var im=document.createElement("img");im.width=34;im.height=34;im.src=kind==="img-blob"?URL.createObjectURL(bytes(u)):u;box.appendChild(im);waits.push(im.decode().catch(function(){}));}' +
  ' }await Promise.all(waits);await new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r);});});return true;};' +
  '</script>');

async function run(kind) {
  const win = new BrowserWindow({ show: false, width: 800, height: 600, webPreferences: { sandbox: true, contextIsolation: true, preload: PRELOAD, backgroundThrottling: false } });
  await win.loadFile(PAGE);
  const wc = win.webContents;
  wc.debugger.attach('1.3');
  const send = (m, p) => wc.debugger.sendCommand(m, p || {});
  const gc = async () => { for (let i = 0; i < 2; i++) { await send('HeapProfiler.collectGarbage'); await sleep(120); } };
  const priv = () => { const pid = wc.getOSProcessId(); const m = app.getAppMetrics().find((x) => x.pid === pid); return m ? Math.round(m.memory.privateBytes / 1024 * 10) / 10 : null; };
  const same = []; for (let i = 0; i < K; i++) same.push(png());
  const one = (k) => wc.executeJavaScript(`show(${JSON.stringify(k === 'img-same' ? 'img-data' : k.replace(/-(clear|gc)$/, ''))}, ${JSON.stringify(k === 'img-same' ? same : Array.from({ length: K }, png))})`);
  await one(kind); await gc(); await sleep(300);
  const warm = priv();
  const marks = [];
  for (let c = 1; c <= N; c++) {
    await one(kind);
    if (kind === 'img-data-clear') await wc.executeJavaScript('probe.clearCache()');
    if (kind === 'img-data-gc') await gc();
    if (c % Math.ceil(N / 3) === 0 || c === N) { await gc(); await sleep(300); marks.push(c + ':' + priv()); }
  }
  const usage = await wc.executeJavaScript('JSON.stringify(probe.usage().images)').catch(() => null);
  await send('Memory.simulatePressureNotification', { level: 'critical' }); await sleep(1500); await gc(); await sleep(300);
  const after = priv();
  const end = Number(marks[marks.length - 1].split(':')[1]);
  const perPic = Math.round((end - warm) * 1024 / (N * K));
  process.stdout.write(`img-mem ${kind}: renderer private warm ${warm} MB -> ${marks.join(' ')} MB after ${N} cycles x ${K} pictures; ` +
    `after critical pressure ${after} MB; kept ~${perPic} KB per new picture (decoded ${Math.round(SIDE * SIDE * 4 / 1024)} KB, encoded ~${Math.round(same[0].length * 0.75 / 1024)} KB); ` +
    `webFrame images ${usage}\n`);
  win.destroy();
}

app.whenReady().then(async () => {
  process.stdout.write(`img-mem: Electron ${process.versions.electron}, Chromium ${process.versions.chrome}\n`);
  for (const v of VARIANTS) { try { await run(v.trim()); } catch (e) { process.stdout.write(`img-mem ${v}: FAILED ${e && e.stack || e}\n`); } }
  try { fs.rmSync(dir, { recursive: true, force: true }); } catch (_) { /* temp */ }
  app.quit();
});
