'use strict';
/*
 * tools/bar-mem-probe.js - does the bar page keep memory as it is used? (lane mantle-slim-bar, 2026-11-06)
 *
 * WHY. The live log's 30-minute memory line had the bar renderer's private bytes climbing with use and
 * never coming back (one run: 54 MB at start, 126 MB peak on day 4; another day: 36 -> 110 MB in an hour
 * of heavy media-card use), while every other renderer stayed flat.
 *
 * WHAT. The REAL ui/bar/index.html in headless Chromium with the mock bridge (ui/bar/page-harness.js),
 * dark theme, one browser per scenario so the renderer is the scenario's own. Each scenario is driven N
 * times and measured at checkpoints after two forced GCs:
 *   JS heap used, DOM counters (documents, nodes, JS listeners), Performance metrics (resources, layout
 *   objects), the renderer's private bytes (Windows: Get-Process), and, at the end, the same after a
 *   CRITICAL memory-pressure signal (what Chromium can give back by itself = cache, not a leak).
 * Scenarios (MEM_PROBE=a,b to pick):
 *   cards   - the media card opened, Up next shown, the Library tab, closed (the user's 13:47 pattern)
 *   tracks  - the card open with Up next, one track change after another: a new 192 px artwork push and a
 *             queue whose 64 px art is new each time (what Spotify sends as songs go by)
 *   reveals - the bar revealed and hidden with a new backdrop strip each time
 *   popups  - the media card opened and closed, nothing else: the dark pop-up glass on its own
 *
 *   node tools/bar-mem-probe.js            (MEM_PROBE_N=60 cycles, checkpoints every N/3)
 * Output: one JSON line per checkpoint ("bar-mem: {...}") and a summary table; never fails a suite.
 */
const path = require('path');
const zlib = require('zlib');
const { execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
/* MEM_PROBE_ROOT=<dir>: measure the bar page of ANOTHER tree (its ui/, lib/ and preload.js), e.g. a base commit
   unpacked by tools/bar-mem-ab.js, on the same runner and in the same Electron as this one */
const ROOT = process.env.MEM_PROBE_ROOT ? path.resolve(process.env.MEM_PROBE_ROOT) : path.join(__dirname, '..');
/* Under Electron (node node_modules/electron/cli.js tools/bar-mem-probe.js) the page runs in Mantle's own
   Chromium, in a quiet window (lib/test-quiet.js: alpha 0, click-through, never focused), with the GPU in
   the browser process as Mantle runs it, and with webFrame's image cache behind the mock's bridge. Under
   node it runs in headless Chromium through ui/bar/page-harness.js. */
const ELECTRON = !!process.versions.electron;
if (ELECTRON) require(path.join(__dirname, '..', 'lib', 'test-quiet.js')).install(['--test'], process.env);

const N = Math.max(3, Number(process.env.MEM_PROBE_N) || 60);
const STEPS = 3;
const WANT = (process.env.MEM_PROBE || 'cards,tracks,reveals,popups').split(',').map((s) => s.trim()).filter(Boolean);
/* dark, as the user runs it (preload.js stamps <html data-theme>; the mock does not): the dark pop-up glass and
   the bar's backdrop strip run only in dark */
/* MEM_PROBE_THEME=light measures the light page (no backdrop guard, no pop-up glass); MEM_PROBE_CSS=<css> adds a
   style (Electron mode), e.g. '.pop.bd{background-image:none!important}' to tell the glass's picture from the rest */
const THEME = process.env.MEM_PROBE_THEME === 'light' ? 'light' : 'dark';
const DARK = THEME === 'light' ? '' : "(function () { var st = function () { if (document.documentElement) document.documentElement.setAttribute('data-theme', 'dark'); }; st(); document.addEventListener('readystatechange', st); })();";
const W = { w: 1400, h: 877, dsf: 1, settle: 900, init: 'window.__MOCK_CALLS__ = null;' + DARK };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
// before each checkpoint: longer than the bar's own wait before it trims its image cache (bar.js IMG_TRIM_*)
const IDLE_MS = Math.max(0, Number(process.env.MEM_PROBE_IDLE_MS) || 2500);

/* ------------------------------------------------------------ PNGs made here, unique per call */
const CRC = (() => { const t = new Int32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c; } return t; })();
function crc32(buf) { let c = -1; for (const b of buf) c = CRC[(c ^ b) & 255] ^ (c >>> 8); return (c ^ -1) >>> 0; }
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
let seed = 1;
function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; }
/* a photo-ish picture: a gradient in a random pair of colours plus grain, so it compresses like art does */
function png(w, h, grain) {
  const a = [rnd() * 255, rnd() * 255, rnd() * 255], b = [rnd() * 255, rnd() * 255, rnd() * 255];
  const raw = Buffer.alloc((w * 4 + 1) * h);
  let o = 0;
  for (let y = 0; y < h; y++) {
    raw[o++] = 0;
    for (let x = 0; x < w; x++) {
      const t = (x + y) / (w + h);
      for (let k = 0; k < 3; k++) raw[o++] = Math.max(0, Math.min(255, a[k] + (b[k] - a[k]) * t + (rnd() - 0.5) * grain));
      raw[o++] = 255;
    }
  }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  const out = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
  return 'data:image/png;base64,' + out.toString('base64');
}

/* ------------------------------------------------------------ measuring */
function privateBytes(pids) {
  if (process.platform !== 'win32' || !pids.length) return {};
  try {
    const out = execFileSync('powershell', ['-NoProfile', '-Command',
      `Get-Process -Id ${pids.join(',')} -ErrorAction SilentlyContinue | ForEach-Object { '{0} {1} {2}' -f $_.Id,$_.PrivateMemorySize64,$_.WorkingSet64 }`],
    { encoding: 'utf8', timeout: 20000 });
    const m = {};
    for (const line of out.split(/\r?\n/)) { const [id, p, ws] = line.trim().split(/\s+/); if (id) m[id] = { priv: Number(p), ws: Number(ws) }; }
    return m;
  } catch (e) { return { error: e.message }; }
}
const MB = (b) => Math.round(b / 1048576 * 10) / 10;

async function probe(pg, browser) {
  const cdp = await pg.page.context().newCDPSession(pg.page);
  const bcdp = await browser.newBrowserCDPSession();
  await cdp.send('Performance.enable');
  async function renderers() {
    try {
      const info = await bcdp.send('SystemInfo.getProcessInfo');
      return info.processInfo.filter((p) => p.type === 'renderer').map((p) => p.id);
    } catch (e) { return []; }
  }
  async function gc() { for (let i = 0; i < 2; i++) { await cdp.send('HeapProfiler.collectGarbage'); await sleep(150); } }
  async function measure(label, extra) {
    await gc();
    await sleep(300);
    const heap = await cdp.send('Runtime.getHeapUsage');
    const dom = await cdp.send('Memory.getDOMCounters');
    const perf = {};
    for (const m of (await cdp.send('Performance.getMetrics')).metrics) perf[m.name] = m.value;
    const pids = await renderers();
    const pb = privateBytes(pids);
    // the page's own renderer: the one with the most private bytes (a spare renderer is ~10 MB)
    let mine = null;
    for (const id of Object.keys(pb)) if (pb[id] && pb[id].priv && (!mine || pb[id].priv > mine.priv)) mine = Object.assign({ pid: Number(id) }, pb[id]);
    const row = Object.assign({ label, jsUsedMB: MB(heap.usedSize), jsTotalMB: MB(heap.totalSize), documents: dom.documents, nodes: dom.nodes, listeners: dom.jsEventListeners,
      resources: perf.Resources, layoutObjects: perf.LayoutObjects, rendererPrivMB: mine ? MB(mine.priv) : null, rendererWsMB: mine ? MB(mine.ws) : null, renderers: pids.length }, extra || {});
    process.stdout.write('bar-mem: ' + JSON.stringify(row) + '\n');
    return row;
  }
  async function purge() {
    await cdp.send('Memory.simulatePressureNotification', { level: 'critical' });
    await sleep(1500);
  }
  return { cdp, measure, purge };
}

/* ------------------------------------------------------------ scenarios */
const PILL = '?mode=pill&media=playing&spq=long';
let trackNo = 0;
function newTrack() {
  trackNo++;
  return { title: 'Probe song ' + trackNo, artist: 'Probe artist ' + (trackNo % 7), art: png(192, 192, 10) };
}

/* page-side: a 'media' push for the mock's Spotify session with a new title and artwork */
async function pushTrack(pg, t) {
  await pg.eval(async ([title, artist, art]) => {
    const p = await window.mantle.call('media.get', {});   // the mock's own payload, a fresh object each time
    if (!p) return false;
    const pick = (s) => { if (s && /spotify/i.test(String(s.aumid || '') + String(s.app && s.app.name || ''))) { s.title = title; s.artist = artist; s.artwork = art; } };
    if (p.shown) pick(p.shown);
    (p.sessions || []).forEach(pick);
    p.rev = (Number(p.rev) || 0) + 1;
    window.__mockMain.emit('media', p);
    return true;
  }, [t.title, t.artist, t.art]);
}

/* the queue reply with new art per row each time it is asked (the mock's tiles are the same strings) */
const QUEUE_REPLY = `(function () {
  var n = 0;
  return function (ch, p) {
    if (ch === 'spotify.queue' && window.__probeQueue) return window.__probeQueue;
    if (ch === 'spotify.library' && window.__probeLib) return window.__probeLib;
    if (ch === 'bar.popover' && p && p.open && p.backdrop === true && window.__probeBd) {
      var key = p.key;
      setTimeout(function () { window.__mockMain.emit('bar.popbackdrop', { key: key, png: window.__probeBd, at: { x: 0, y: 0, w: 1, h: 1 }, win: { w: innerWidth, h: innerHeight }, ms: 40 }); }, 40);
      return { ok: true, reason: '', mock: true, max: 640, height: Math.min(640, Number(p.height) || 0), capped: Number(p.height) > 640 };
    }
    return undefined;
  };
})()`;

function queueReply(cur) {
  const row = (i, where) => ({ uri: 'spotify:track:PROBE' + String(trackNo).padStart(6, '0') + String(i).padStart(10, '0'), uid: where + i, name: 'Row ' + i + ' of ' + trackNo,
    artists: 'Someone', kind: 'track', where, provider: where === 'queued' ? 'queue' : 'context', image: png(64, 64, 10) });
  const queued = []; for (let i = 0; i < 12; i++) queued.push(row(i, 'queued'));
  const nextUp = []; for (let i = 0; i < 20; i++) nextUp.push(row(100 + i, 'next'));
  return { ok: true, state: 'connected', at: Date.now(), caps: { skipTo: true, next: true, remove: true, move: true, search: true },
    current: { uri: 'spotify:track:PROBENOW' + String(trackNo).padStart(14, '0'), uid: 'now', name: cur.title, artists: cur.artist, kind: 'track', where: 'current', image: png(64, 64, 10) },
    queued, nextUp, player: { paused: false, shuffle: true, repeat: 0, canShuffle: true, canRepeat: true, canRepeatTrack: true, hasItem: true } };
}
function libReply() {
  const items = [];
  for (let i = 0; i < 40; i++) items.push({ uri: 'spotify:playlist:PROBE' + String(trackNo).padStart(6, '0') + String(i).padStart(16, '0'), kind: 'playlist', name: 'List ' + i, owner: 'Someone', sub: 'Someone', image: png(64, 64, 10) });
  return { ok: true, liked: { uri: 'spotify:collection:tracks', name: 'Liked Songs' }, skipped: 0, total: items.length, at: Date.now(), items };
}
async function setReplies(pg, t, opts) {
  const q = queueReply(t);
  const l = opts && opts.lib ? libReply() : null;
  await pg.eval(([qq, ll]) => { window.__probeQueue = qq; if (ll) window.__probeLib = ll; }, [q, l]);
}

async function popKey(pg) { return pg.eval(() => { const p = document.getElementById('pop'); return p && !p.hidden ? p.className : ''; }); }

const SCEN = {
  async cards(pg) {
    const t = newTrack(); await setReplies(pg, t, { lib: true }); await pushTrack(pg, t);
    if (!/mcard/.test(await popKey(pg))) await pg.click('#mchip', { wait: 350 });
    const head = await pg.eval(() => { const h = document.querySelector('#pop .spq-head'); return h ? h.getAttribute('aria-expanded') : null; });
    if (head === 'false') await pg.click('#pop .spq-head', { wait: 350 });
    if (await pg.count('#pop .spq-tab[data-k="tab:lib"]')) {
      await pg.eval(() => { window.__probeLib = null; });
      await setReplies(pg, t, { lib: true });
      await pg.click('#pop .spq-tab[data-k="tab:lib"]', { wait: 350 });
      await pg.click('#pop .spq-tab[data-k="tab:queue"]', { wait: 300 });
    }
    if (/mcard/.test(await popKey(pg))) await pg.click('#mchip', { wait: 300 });
  },
  async tracks(pg, i) {
    if (i === 0) {
      const t0 = newTrack(); await setReplies(pg, t0); await pushTrack(pg, t0);
      await pg.click('#mchip', { wait: 350 });
      const head = await pg.eval(() => { const h = document.querySelector('#pop .spq-head'); return h ? h.getAttribute('aria-expanded') : null; });
      if (head === 'false') await pg.click('#pop .spq-head', { wait: 350 });
    }
    const t = newTrack(); await setReplies(pg, t); await pushTrack(pg, t);
    await pg.wait(1300);   // the card's TRACK_SETTLE_MS re-read of the queue after a change
  },
  async reveals(pg) {
    const strip = png(1400, 62, 18);
    await pg.emit('bar.reveal', { why: 'probe', mode: 'reveal', ms: 1 });
    await pg.emit('bar.backdrop', { png: strip, at: { x: 0, y: 0, w: 1400, h: 62 }, win: { w: 1400, h: 877 } });
    await pg.wait(250);
    await pg.emit('bar.hide', { why: 'probe', ms: 1 });
    await pg.wait(250);
  },
  async popups(pg) {
    await pg.eval(() => { window.__probeBd = null; });
    const bd = png(350, 219, 18);   // main's real pop-up picture: the window, 4x down
    await pg.eval((b) => { window.__probeBd = b; }, bd);
    await pg.click('#mchip', { wait: 350 });
    await pg.click('#mchip', { wait: 300 });
  },
};

/* ------------------------------------------------------------ the page, in Electron */
/* The image-cache primitive exactly as preload.js ships it (the text between its markers), so what is
   measured is the shipped code; a preload.js without it (an older commit) is measured without it. */
function preloadImageCacheSource() {
  const src = fs.readFileSync(path.join(ROOT, 'preload.js'), 'utf8');
  const m = /\/\/ <image-cache>([\s\S]*?)\/\/ <\/image-cache>/.exec(src);
  return m ? m[1] : null;
}
async function electronPage(query) {
  const { app, BrowserWindow, nativeTheme } = require('electron');
  if (!electronPage.ready) {
    app.commandLine.appendSwitch('in-process-gpu');      // as main.js runs Mantle
    app.on('window-all-closed', () => {});               // one window per scenario: closing one must not quit
    electronPage.dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-bar-mem-'));
    app.setPath('userData', path.join(electronPage.dir, 'profile'));
    await app.whenReady();
    nativeTheme.themeSource = THEME;
    electronPage.ready = true;
  }
  const ic = preloadImageCacheSource();
  const pre = path.join(electronPage.dir, 'pre-' + Date.now() + '.js');
  fs.writeFileSync(pre, "const { webFrame } = require('electron');\n" +
    'window.__MOCK_CALLS__ = null;\nwindow.__MOCK_REPLY__ = ' + QUEUE_REPLY + ';\n' + DARK + '\n' +
    (ic ? ic + '\nwindow.__mantleImageCache = imageCache;\n' : '// this commit\'s preload.js has no image-cache primitive\n'));
  const win = new BrowserWindow({ width: W.w, height: W.h, show: true, webPreferences: { preload: pre, contextIsolation: false, sandbox: true, backgroundThrottling: false } });
  const wc = win.webContents;
  const errors = [];
  wc.on('console-message', (...a) => {
    const d = a[1] && typeof a[1] === 'object' && 'message' in a[1] ? a[1] : { level: a[1], message: a[2] };
    if (d.level === 'error' || d.level === 3) errors.push(String(d.message));
    if (/\[bar\] image cache/.test(String(d.message))) process.stdout.write('bar-mem: page said: ' + String(d.message).slice(0, 300) + '\n');
  });
  await win.loadFile(path.join(ROOT, 'ui', 'bar', 'index.html'), { search: query.replace(/^\?/, '') });
  if (process.env.MEM_PROBE_CSS) await wc.insertCSS(process.env.MEM_PROBE_CSS);
  await sleep(W.settle);
  wc.debugger.attach('1.3');
  const send = (m, x) => wc.debugger.sendCommand(m, x || {});
  const js = (f, a) => wc.executeJavaScript('(' + f.toString() + ')(' + (a === undefined ? '' : JSON.stringify(a)) + ')', true);
  const pg = {
    errors,
    eval: js,
    wait: sleep,
    emit: (ch, x) => js(([c, y]) => window.__mockMain.emit(c, y), [ch, x]),
    count: (sel) => js((q) => document.querySelectorAll(q).length, sel),
    click: async (sel, o) => {
      const r = await js((q) => { const e = document.querySelector(q); if (!e) return null; const b = e.getBoundingClientRect(); return { x: b.left + b.width / 2, y: b.top + b.height / 2 }; }, sel);
      if (!r) throw new Error('no element ' + sel);
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: r.x, y: r.y });
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: r.x, y: r.y, button: 'left', clickCount: 1 });
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: r.x, y: r.y, button: 'left', clickCount: 1 });
      await sleep((o && o.wait) || 250);
    },
  };
  await send('Performance.enable');
  const gc = async () => { for (let i = 0; i < 2; i++) { await send('HeapProfiler.collectGarbage'); await sleep(150); } };
  async function measure(label, extra) {
    await gc(); await sleep(300);
    const heap = await send('Runtime.getHeapUsage');
    const dom = await send('Memory.getDOMCounters');
    const perf = {}; for (const x of (await send('Performance.getMetrics')).metrics) perf[x.name] = x.value;
    const all = app.getAppMetrics(), pid = wc.getOSProcessId();
    const r = all.find((x) => x.pid === pid), b = all.find((x) => x.type === 'Browser');
    const cache = await js(() => (window.__mantleImageCache ? window.__mantleImageCache({}).before : null));
    const theme = await js(() => document.documentElement.getAttribute('data-theme'));
    const trim = await js(() => (window.__barImgTrim ? { runs: window.__barImgTrim.runs, cleared: window.__barImgTrim.cleared } : null));
    const pics = await js(() => (window.MantlePic ? window.MantlePic.stats() : null));
    const row = Object.assign({ label, jsUsedMB: MB(heap.usedSize), documents: dom.documents, nodes: dom.nodes, listeners: dom.jsEventListeners, resources: perf.Resources,
      rendererPrivMB: r ? MB(r.memory.privateBytes * 1024) : null, rendererWsMB: r ? MB(r.memory.workingSetSize * 1024) : null,
      browserPrivMB: b ? MB(b.memory.privateBytes * 1024) : null, imageCache: cache ? cache.count + ' / ' + MB(cache.bytes) + ' MB' : 'n/a', trim, pics, theme }, extra || {});
    process.stdout.write('bar-mem: ' + JSON.stringify(row) + '\n');
    return row;
  }
  const purge = async () => { await send('Memory.simulatePressureNotification', { level: 'critical' }); await sleep(1500); };
  return { pg, m: { measure, purge }, close: async () => { try { wc.debugger.detach(); } catch (_) { /* gone */ } win.destroy(); await sleep(500); } };
}

/* ------------------------------------------------------------ the page, in headless Chromium */
function headlessPage(query, body) {
  const H = require('../ui/bar/page-harness.js');
  return H.withBrowser(async (open) => {
    const pg = await open(query, Object.assign({}, W, { reply: QUEUE_REPLY }));
    const m = await probe(pg, pg.page.context().browser());
    await body({ pg, m });
  });
}

async function scenario(name, results) {
  const body = async ({ pg, m }) => {
    // a first pop-up picture so every scenario starts with the dark glass path warmed
    await pg.eval((b) => { window.__probeBd = b; }, png(350, 219, 18));
    const rows = [await m.measure(name + ' start', { cycle: 0 })];
    // one warm cycle: first-use allocations (fonts, the card's code paths) are not growth
    await SCEN[name](pg, 0);
    await sleep(IDLE_MS);
    rows.push(await m.measure(name + ' warm', { cycle: 1 }));
    for (let i = 1; i <= N; i++) {
      await SCEN[name](pg, i);
      if (i % Math.ceil(N / STEPS) === 0 || i === N) { await sleep(IDLE_MS); rows.push(await m.measure(name + ' ' + i, { cycle: i + 1 })); }
    }
    await m.purge();
    rows.push(await m.measure(name + ' after critical pressure', { cycle: N + 1 }));
    rows.errors = pg.errors.slice(0, 5);
    results[name] = rows;
  };
  if (!ELECTRON) return headlessPage(PILL, body);
  const p = await electronPage(PILL);
  try { await body(p); } finally { await p.close(); }
}

(async () => {
  const results = {};
  process.stdout.write('bar-mem: page from ' + ROOT + '\n');
  process.stdout.write('bar-mem: ' + (ELECTRON ? 'Electron ' + process.versions.electron + ', Chromium ' + process.versions.chrome : 'headless Chromium (page-harness)') +
    '; image-cache primitive in preload.js: ' + (preloadImageCacheSource() ? 'yes' : 'no') + '\n');
  for (const name of WANT) {
    if (!SCEN[name]) { process.stdout.write('bar-mem: no scenario "' + name + '"\n'); continue; }
    await scenario(name, results);
  }
  process.stdout.write('\nbar-mem SUMMARY (N=' + N + ', after forced GC; priv / ws = the bar renderer\'s private bytes / working set)\n');
  for (const [name, rows] of Object.entries(results)) {
    const w = rows[1], e = rows[rows.length - 2], p = rows[rows.length - 1];
    const d = (k) => (e[k] != null && w[k] != null ? Math.round((e[k] - w[k]) * 10) / 10 : 'n/a');
    process.stdout.write('bar-mem ' + name + ': warm -> ' + N + ': priv ' + w.rendererPrivMB + ' -> ' + e.rendererPrivMB + ' MB (' + d('rendererPrivMB') + '), ws ' +
      w.rendererWsMB + ' -> ' + e.rendererWsMB + ' MB (' + d('rendererWsMB') + '), after pressure priv ' + p.rendererPrivMB + ' / ws ' + p.rendererWsMB + ' MB; ' +
      (ELECTRON ? 'browser process priv ' + w.browserPrivMB + ' -> ' + e.browserPrivMB + ' MB; image cache ' + w.imageCache + ' -> ' + e.imageCache + '; trims ' + JSON.stringify(e.trim) + '; ' : '') +
      'js ' + w.jsUsedMB + ' -> ' + e.jsUsedMB + ' MB; nodes ' + w.nodes + ' -> ' + e.nodes + '; listeners ' + w.listeners + ' -> ' + e.listeners +
      (e.pics ? '; pictures ' + JSON.stringify(e.pics) : '') + '; page errors ' + rows.errors.length + (rows.errors.length ? ': ' + rows.errors.join(' | ') : '') + '\n');
  }
  if (ELECTRON) { try { fs.rmSync(electronPage.dir, { recursive: true, force: true }); } catch (_) { /* temp */ } require('electron').app.exit(0); }
})().catch((e) => {
  process.stdout.write('bar-mem: FAILED ' + (e && e.stack || e) + '\n');
  if (ELECTRON) require('electron').app.exit(1); else process.exit(1);
});
