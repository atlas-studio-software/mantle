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
 *   img-blob-load the same, URL revoked on the picture's own load event (what the bar does)
 *   img-blob-keep the same, URL never revoked (control: does the revoke matter?)
 *   css-data      a data: url() as a background-image, then cleared
 *   bitmap        createImageBitmap(blob) drawn into a <canvas>, bitmap.close()
 *   img-data-clear  img-data plus webFrame.clearCache() after each cycle
 *   img-data-gc   img-data with a forced GC after every cycle
 * Each picture is 300x300 and compresses well (a smooth gradient: ~1/20 of its decoded size), so the
 * kept bytes say WHICH copy is kept: ~360 KB per picture = the decoded pixels, a few KB = the URL/bytes.
 *
 *   electron tools/img-mem-probe.js        (IMG_PROBE_N cycles, default 30, of IMG_PROBE_K=20 pictures)
 * First (IMG_PROBE_SYNC=0 skips it) a sync line: is a picture complete the moment its src is set, and on the
 * next two frames, for data:, blob:, and blob: after another element decoded the same URL (revoked or not).
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
const GRAIN = Math.max(0, Number(process.env.IMG_PROBE_GRAIN) || 0);   // 0: ~1 KB each; 1: ~53 KB, about a real cover
const VARIANTS = (process.env.IMG_PROBE || 'img-data,img-same,img-blob,css-data,bitmap,img-data-clear').split(',');
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
      const px = [0, 1, 2].map((k) => Math.max(0, Math.min(255, Math.round(a[k] + (b[k] - a[k]) * t + (GRAIN ? (rnd() - 0.5) * GRAIN : 0))))).concat([255]);
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
  ' if(kind!=="img-blob-keep")box.querySelectorAll("img").forEach(function(i){if(i.src.indexOf("blob:")===0)URL.revokeObjectURL(i.src);});' +
  ' box.textContent="";var waits=[];' +
  ' for(var j=0;j<list.length;j++){var u=list[j];' +
  '  if(kind==="css-data"){var d=document.createElement("div");d.style.cssText="width:34px;height:34px;background-size:cover;background-image:url(\\""+u+"\\")";box.appendChild(d);}' +
  '  else if(kind==="bitmap"){var c=document.createElement("canvas");c.width=68;c.height=68;c.style.width="34px";c.style.height="34px";box.appendChild(c);' +
  '   waits.push(createImageBitmap(bytes(u),{resizeWidth:68,resizeHeight:68,resizeQuality:"high"}).then((function(c){return function(bm){c.getContext("2d").drawImage(bm,0,0);bm.close();};})(c)));}' +
  '  else if(kind==="img-blob-load"){var il=document.createElement("img");il.width=34;il.height=34;var bu=URL.createObjectURL(bytes(u));il.onload=il.onerror=(function(x){return function(){URL.revokeObjectURL(x);};})(bu);il.src=bu;box.appendChild(il);waits.push(il.decode().catch(function(){}));}'+
  '  else{var im=document.createElement("img");im.width=34;im.height=34;im.src=/^img-blob/.test(kind)?URL.createObjectURL(bytes(u)):u;box.appendChild(im);waits.push(im.decode().catch(function(){}));}' +
  ' }await Promise.all(waits);await new Promise(function(r){requestAnimationFrame(function(){requestAnimationFrame(r);});});return true;};'+
  /* is a picture there the moment its src is set, and on the next frame? data:, blob:, and blob: again after another element decoded it */
  'window.syncTest=async function(u){var out={};var f1=function(){return new Promise(function(r){requestAnimationFrame(function(){r();});});};' +
  ' var t=function(name,src){var im=document.createElement("img");im.decoding="sync";im.src=src;document.body.appendChild(im);var o={now:im.complete&&im.naturalWidth>0};return f1().then(function(){o.frame1=im.complete&&im.naturalWidth>0;return f1();}).then(function(){o.frame2=im.complete&&im.naturalWidth>0;out[name]=o;im.remove();});};' +
  ' await t("data",u);var b1=URL.createObjectURL(bytes(u));await t("blob",b1);' +
  ' var b2=URL.createObjectURL(bytes(u));var pre=new Image();pre.src=b2;await pre.decode();await t("blob-after-decode",b2);' +
  ' var b3=URL.createObjectURL(bytes(u));var pre3=new Image();pre3.src=b3;await pre3.decode();URL.revokeObjectURL(b3);await t("blob-after-decode-revoked",b3);return out;};' +
  '</script><script>(' + bakePage.toString() + ')();</script>');

/* The dark pop-up's glass, step by step (what ui/bar/bar.js does on every open), to find which step keeps
   memory: decode-only (blob: Image, URL let go), pixels (+ read back through one shared willReadFrequently
   canvas, as glass-adapt.js pixelsOf), bake (+ the two-canvas edge clamp and blur, toDataURL),
   bake-nofilter (the same without the blur filter), bake-css-data / bake-css-blob (+ shown as the box's
   background through a data: or a blob: url(), the previous blob: let go). */
function bakePage() {
  let scratch = null, cssUrl = null;
  const frames = () => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  window.bakeRun = async function (kind, list) {
    for (const u of list) {
      const url = URL.createObjectURL(bytes(u));   // eslint-disable-line no-undef
      const im = new Image(); im.src = url; await im.decode(); URL.revokeObjectURL(url);
      if (kind === 'decode-only') continue;
      const w = im.naturalWidth, h = im.naturalHeight;
      if (!scratch) scratch = document.createElement('canvas');
      scratch.width = w; scratch.height = h;
      const sg = scratch.getContext('2d', { willReadFrequently: true });
      sg.clearRect(0, 0, w, h); sg.drawImage(im, 0, 0, w, h); sg.getImageData(0, 0, w, h);
      if (kind === 'pixels') continue;
      const P = 23;
      const a = document.createElement('canvas'); a.width = w + 2 * P; a.height = h + 2 * P;
      const ga = a.getContext('2d'); ga.imageSmoothingEnabled = false;
      ga.drawImage(im, P, P); ga.drawImage(im, 0, 0, 1, h, 0, P, P, h); ga.drawImage(im, w - 1, 0, 1, h, P + w, P, P, h);
      const b = document.createElement('canvas'); b.width = w; b.height = h;
      const gb = b.getContext('2d');
      gb.filter = kind === 'bake-nofilter' ? 'none' : 'blur(7.5px)';
      gb.drawImage(a, -P, -P);
      const d = b.toDataURL('image/png');
      if (kind === 'bake' || kind === 'bake-nofilter') continue;
      const v = kind === 'bake-css-blob' ? URL.createObjectURL(bytes(d)) : d;   // eslint-disable-line no-undef
      document.getElementById('box').style.cssText = 'width:' + w + 'px;height:' + h + 'px;background-image:url("' + v + '")';
      await frames();
      if (cssUrl) URL.revokeObjectURL(cssUrl);
      cssUrl = kind === 'bake-css-blob' ? v : null;
    }
    await frames();
    return true;
  };
}

async function run(kind) {
  const win = new BrowserWindow({ show: false, width: 800, height: 600, webPreferences: { sandbox: true, contextIsolation: true, preload: PRELOAD, backgroundThrottling: false } });
  const page = path.join(dir, 'page-' + kind + '.html');
  fs.copyFileSync(PAGE, page);
  for (let i = 0; ; i++) { try { await win.loadFile(page); break; } catch (e) { if (i >= 4) throw e; await sleep(1000); } }
  const wc = win.webContents;
  wc.debugger.attach('1.3');
  const send = (m, p) => wc.debugger.sendCommand(m, p || {});
  const gc = async () => { for (let i = 0; i < 2; i++) { await send('HeapProfiler.collectGarbage'); await sleep(120); } };
  const priv = () => { const pid = wc.getOSProcessId(); const m = app.getAppMetrics().find((x) => x.pid === pid); return m ? Math.round(m.memory.privateBytes / 1024 * 10) / 10 : null; };
  const ws = () => { const pid = wc.getOSProcessId(); const m = app.getAppMetrics().find((x) => x.pid === pid); return m ? Math.round(m.memory.workingSetSize / 1024 * 10) / 10 : null; };
  const imgs = async () => { const u = JSON.parse(await wc.executeJavaScript('JSON.stringify(probe.usage().images)')); return u.count + ' images ' + Math.round(u.size / 1048576) + ' MB (live ' + Math.round(u.liveSize / 1048576) + ')'; };
  const browserPriv = () => { const m = app.getAppMetrics().find((x) => x.type === 'Browser'); return m ? Math.round(m.memory.privateBytes / 1024) : null; };
  const same = []; for (let i = 0; i < K; i++) same.push(png());
  const BAKE = /^(decode-only|pixels|bake)/.test(kind);
  const one = (k) => BAKE ? wc.executeJavaScript(`bakeRun(${JSON.stringify(k)}, ${JSON.stringify(Array.from({ length: K }, png))})`) : wc.executeJavaScript(`show(${JSON.stringify(k === 'img-same' ? 'img-data' : k.replace(/-(clear|gc)$/, ''))}, ${JSON.stringify(k === 'img-same' ? same : Array.from({ length: K }, png))})`);
  await one(kind); await gc(); await sleep(300);
  const warm = priv(), warmWs = ws(), warmBrowser = browserPriv();
  const marks = [];
  for (let c = 1; c <= N; c++) {
    await one(kind);
    if (kind === 'img-data-clear') await wc.executeJavaScript('probe.clearCache()');
    if (kind === 'img-data-gc') await gc();
    if (c % Math.ceil(N / 3) === 0 || c === N) { await gc(); await sleep(300); marks.push(c + ':' + priv()); }
  }
  const usage = await imgs().catch((e) => 'n/a: ' + e.message);
  const endWs = ws(), endBrowser = browserPriv();
  await send('Memory.simulatePressureNotification', { level: 'critical' }); await sleep(1500); await gc(); await sleep(300);
  const after = priv(), afterUsage = await imgs().catch(() => 'n/a');
  await wc.executeJavaScript('probe.clearCache()'); await gc(); await sleep(500);
  const cleared = priv(), clearedWs = ws(), clearedUsage = await imgs().catch(() => 'n/a');
  const end = Number(marks[marks.length - 1].split(':')[1]);
  const perPic = Math.round((end - warm) * 1024 / (N * K));
  process.stdout.write(`img-mem ${kind}: renderer private warm ${warm} MB -> ${marks.join(' ')} MB after ${N} cycles x ${K} pictures; ` +
    `after critical pressure ${after} MB; kept ~${perPic} KB per new picture (decoded ${Math.round(SIDE * SIDE * 4 / 1024)} KB, encoded ~${Math.round(same[0].length * 0.75 / 1024)} KB); ` +
    `webFrame cache: ${usage}; after pressure ${afterUsage}; ws ${warmWs} -> ${endWs} MB; browser process private ${warmBrowser} -> ${endBrowser} MB; ` +
    `then ONE webFrame.clearCache(): private ${cleared} MB, ws ${clearedWs} MB, cache ${clearedUsage}\n`);
  win.destroy();
}

// one window per variant: without this, closing the first one quits the app (Electron's default)
app.on('window-all-closed', () => {});
app.whenReady().then(async () => {
  process.stdout.write(`img-mem: Electron ${process.versions.electron}, Chromium ${process.versions.chrome}\n`);
  if (process.env.IMG_PROBE_SYNC !== '0') {
    try {
      const win = new BrowserWindow({ show: true, width: 400, height: 300, webPreferences: { sandbox: true, contextIsolation: true, preload: PRELOAD, backgroundThrottling: false } });
      const page = path.join(dir, 'page-sync.html'); fs.copyFileSync(PAGE, page); await win.loadFile(page);
      const r = await win.webContents.executeJavaScript('syncTest(' + JSON.stringify(png()) + ').then(JSON.stringify)');
      process.stdout.write('img-mem sync: ' + r + '\n');
      win.destroy();
    } catch (e) { process.stdout.write('img-mem sync: FAILED ' + (e && e.message || e) + '\n'); }
  }
  for (const v of VARIANTS) { try { await run(v.trim()); } catch (e) { process.stdout.write(`img-mem ${v}: FAILED ${e && e.stack || e}\n`); } }
  try { fs.rmSync(dir, { recursive: true, force: true }); } catch (_) { /* temp */ }
  app.quit();
});
