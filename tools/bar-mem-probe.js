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
const H = require('../ui/bar/page-harness.js');

const N = Math.max(3, Number(process.env.MEM_PROBE_N) || 60);
const STEPS = 3;
const WANT = (process.env.MEM_PROBE || 'cards,tracks,reveals,popups').split(',').map((s) => s.trim()).filter(Boolean);
const W = { w: 1400, h: 877, dsf: 1, settle: 900, init: 'window.__MOCK_CALLS__ = null;' };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

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
  return { title: 'Probe song ' + trackNo, artist: 'Probe artist ' + (trackNo % 7), art: png(192, 192, 40) };
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
    artists: 'Someone', kind: 'track', where, provider: where === 'queued' ? 'queue' : 'context', image: png(64, 64, 30) });
  const queued = []; for (let i = 0; i < 12; i++) queued.push(row(i, 'queued'));
  const nextUp = []; for (let i = 0; i < 20; i++) nextUp.push(row(100 + i, 'next'));
  return { ok: true, state: 'connected', at: Date.now(), caps: { skipTo: true, next: true, remove: true, move: true, search: true },
    current: { uri: 'spotify:track:PROBENOW' + String(trackNo).padStart(14, '0'), uid: 'now', name: cur.title, artists: cur.artist, kind: 'track', where: 'current', image: png(64, 64, 30) },
    queued, nextUp, player: { paused: false, shuffle: true, repeat: 0, canShuffle: true, canRepeat: true, canRepeatTrack: true, hasItem: true } };
}
function libReply() {
  const items = [];
  for (let i = 0; i < 40; i++) items.push({ uri: 'spotify:playlist:PROBE' + String(trackNo).padStart(6, '0') + String(i).padStart(16, '0'), kind: 'playlist', name: 'List ' + i, owner: 'Someone', sub: 'Someone', image: png(160, 160, 40) });
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

(async () => {
  const results = {};
  for (const name of WANT) {
    if (!SCEN[name]) { process.stdout.write(`bar-mem: no scenario "${name}"\n`); continue; }
    await H.withBrowser(async (open) => {
      const pg = await open(PILL, Object.assign({}, W, { reply: QUEUE_REPLY }));
      const browser = pg.page.context().browser();
      const m = await probe(pg, browser);
      // a first pop-up picture so every scenario starts with the dark glass path warmed
      await pg.eval((b) => { window.__probeBd = b; }, png(350, 219, 18));
      const rows = [await m.measure(name + ' start', { cycle: 0 })];
      // one warm cycle: first-use allocations (fonts, the card's code paths) are not growth
      await SCEN[name](pg, 0);
      rows.push(await m.measure(name + ' warm', { cycle: 1 }));
      for (let i = 1; i <= N; i++) {
        await SCEN[name](pg, i);
        if (i % Math.ceil(N / STEPS) === 0 || i === N) rows.push(await m.measure(name + ' ' + i, { cycle: i + 1 }));
      }
      await m.purge();
      rows.push(await m.measure(name + ' after critical pressure', { cycle: N + 1 }));
      rows.errors = pg.errors.slice(0, 5);
      results[name] = rows;
    });
  }
  process.stdout.write('\nbar-mem SUMMARY (N=' + N + ', after forced GC; priv = the renderer\'s private bytes)\n');
  for (const [name, rows] of Object.entries(results)) {
    const w = rows[1], e = rows[rows.length - 2], p = rows[rows.length - 1];
    const d = (k) => (e[k] != null && w[k] != null ? Math.round((e[k] - w[k]) * 10) / 10 : 'n/a');
    process.stdout.write(`bar-mem ${name}: warm -> ${N}: priv ${w.rendererPrivMB} -> ${e.rendererPrivMB} MB (${d('rendererPrivMB')}), after pressure ${p.rendererPrivMB} MB; ` +
      `js ${w.jsUsedMB} -> ${e.jsUsedMB} MB (${d('jsUsedMB')}); nodes ${w.nodes} -> ${e.nodes} (${d('nodes')}); listeners ${w.listeners} -> ${e.listeners} (${d('listeners')}); ` +
      `resources ${w.resources} -> ${e.resources} (${d('resources')}); page errors ${rows.errors.length}${rows.errors.length ? ': ' + rows.errors.join(' | ') : ''}\n`);
  }
})().catch((e) => { process.stdout.write('bar-mem: FAILED ' + (e && e.stack || e) + '\n'); process.exit(1); });
