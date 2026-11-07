'use strict';
// The golden table for the C# look ports (LookSyncCore, LookSync, LookHost, LookGuard, GlassAdapt), written by
// the JavaScript modules themselves: lib/look-sync.js (vendored from the look-sync project), lib/look-host.js,
// lib/look-guard.js, ui/shared/glass-adapt.js. Mantle PUBLISHES look.json for Helm and Quartz, so the C# port
// must write the same bytes; LookGoldenTests replays every case and compares.
//
//   node native/tests/Mantle.Core.Tests/Look/gen-look-golden.js     (writes ../Fixtures/look/golden.json)
//
// Run it again after any change to those modules and commit the new table. Nothing here touches the
// user's real folders: every directory is a fresh one under the OS temp folder, removed at the end.
const fs = require('fs');
const os = require('os');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..', '..', '..');
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'mantle-look-golden-'));
process.env.LOOK_SYNC_DIR = path.join(TMP, 'never-used');
const LS = require(path.join(ROOT, 'lib', 'look-sync.js'));
const LH = require(path.join(ROOT, 'lib', 'look-host.js'));
const LG = require(path.join(ROOT, 'lib', 'look-guard.js'));
const GA = require(path.join(ROOT, 'ui', 'shared', 'glass-adapt.js'));
const accent = require(path.join(ROOT, 'lib', 'accent.js'));

const NOW = '2026-11-07T12:00:00.000Z';
const PID = 4242;
let n = 0;
const tmpDir = () => { const d = path.join(TMP, 'd' + (++n)); fs.mkdirSync(d, { recursive: true }); return d; };
const LOCAL = { theme: 'light', accent: { hue: 30 }, glass: { dark: { blurPx: 8, dim: 0.6, panel: -0.1 }, light: { blurPx: 12, dim: 0.45, panel: 0 }, render: 'picture' } };

// The texts that vary by machine, made the same on both sides: the folder, the pid, V8's JSON error words.
const norm = (s, d) => (s == null ? s : String(s).split(d).join('<dir>').replace(/not valid JSON \([^)]*\)/g, 'not valid JSON (...)'));
const fileOf = (d) => { try { return fs.readFileSync(path.join(d, 'look.json'), 'utf8').replace(`"pid": ${process.pid}`, `"pid": ${PID}`); } catch (_) { return null; } };
const listOf = (d) => { try { return fs.readdirSync(d).sort(); } catch (_) { return null; } };

const out = { generatedBy: 'native/tests/Mantle.Core.Tests/Look/gen-look-golden.js', node: process.version };

/* ---- parse / checkValue / checkDesktop ---------------------------------------------------------- */
const texts = [
  '{"schema":1,"rev":3,"writer":{"app":"helm","instance":"h","pid":1,"at":"2026-10-10T10:00:00.000Z","extra":true},"look":{"theme":{"v":"dark","rev":2,"by":"helm"},"glass":{"v":{"dark":{"blurPx":6,"dim":0.55,"panel":0.05,"x":1},"light":{"blurPx":12,"dim":0.45,"panel":0},"render":"seeThrough","more":[1,2]},"rev":3,"by":"quartz"},"other":{"v":1,"rev":1}},"future":{"a":1}}',
  '{"schema":1,"rev":9,"look":{"theme":{"v":"light","rev":9,"by":7},"accent":{"v":{"hue":999},"rev":9,"by":"mantle"},"glass":{"v":{"dark":{"blurPx":100,"dim":0.5,"panel":0},"light":{"blurPx":1,"dim":0.5,"panel":0},"render":"picture"},"rev":9,"by":"quartz"}}}',
  '{"schema":1,"rev":1,"look":{},"wallpaperRequest":{"rev":1,"kind":"builtin","id":"x"}}',
  '{"schema":2,"rev":5,"look":{}}', '{"schema":1.5,"rev":1}', '{"rev":1}', '[]', '42', 'null', '{"schema":1}', '{"schema":1,"rev":-1}',
  '{"schema":1,"rev":2.5}', 'not json', '{"schema":1,"rev":3,"look":{"theme":{"v":"li', '\ufeff{"schema":1,"rev":1,"look":{"theme":{"v":"system","rev":1,"by":"mantle"}}}',
  '{"schema":1,"rev":4,"look":{"accent":{"v":{"hue":null},"rev":"4"},"theme":"dark"}}',
  '{"schema":1,"rev":4,"look":{"accent":{"v":{"hue":12.5},"rev":4}},"desktop":{"rev":4,"ok":true,"id":"builtin:snow","stillSize":[2560,1440],"by":"mantle"}}',
  '{"schema":1,"rev":4,"look":{},"desktop":{"rev":4,"ok":"yes"}}', '{"schema":1,"rev":4,"look":{},"desktop":{"rev":4,"ok":false,"stillSize":[0,1]}}',
  '{"schema":1,"rev":4,"look":{},"desktop":{"rev":4,"ok":true,"title":5}}', '{"schema":1,"rev":4,"look":{},"desktop":null}',
  '{"schema":1,"rev":1e21,"look":{"glass":{"v":{"dark":{"blurPx":1e-7,"dim":0.30000000000000004,"panel":-0},"light":{"blurPx":64,"dim":0.9,"panel":0.15},"render":"picture"},"rev":0,"by":"helm"}}}',
  '{"schema":1,"rev":2,"rev":7,"look":{"theme":{"v":"dark","rev":1,"by":"helm \\u2028 \\"q\\" \\u001b é 雪 😀"}}}',
];
out.parse = texts.map((t) => {
  const p = LS.parse(t.charCodeAt(0) === 0xfeff ? t.slice(1) : t);
  return { text: t, state: p.state, reason: norm(p.reason, '\0'), skipped: p.skipped.map((s) => ({ field: s.field, by: s.by === undefined ? null : s.by, reason: s.reason })),
    doc: p.doc ? JSON.stringify(p.doc, null, 2) : null };
});
const values = [
  ['theme', 'system'], ['theme', 'light'], ['theme', 'dark'], ['theme', 'Dark'], ['theme', null], ['theme', 3],
  ['accent', { hue: null }], ['accent', { hue: 0 }], ['accent', { hue: 360 }], ['accent', { hue: 361 }], ['accent', { hue: -1 }], ['accent', { hue: 1.5 }],
  ['accent', {}], ['accent', null], ['accent', [1]], ['accent', { hue: '20' }],
  ['glass', LS.SEED.glass], ['glass', null], ['glass', { dark: {} }], ['glass', { dark: LS.SEED.glass.dark }],
  ['glass', { dark: { blurPx: 64.5, dim: 0.5, panel: 0 }, light: LS.SEED.glass.light, render: 'picture' }],
  ['glass', { dark: { blurPx: 5, dim: 0.19, panel: 0 }, light: LS.SEED.glass.light, render: 'picture' }],
  ['glass', { dark: LS.SEED.glass.dark, light: { blurPx: 5, dim: 0.5, panel: 0.16 }, render: 'picture' }],
  ['glass', { dark: LS.SEED.glass.dark, light: { blurPx: 5, dim: 0.5, panel: -0.31 }, render: 'picture' }],
  ['glass', { dark: LS.SEED.glass.dark, light: LS.SEED.glass.light, render: 'blur' }], ['glass', { dark: LS.SEED.glass.dark, light: LS.SEED.glass.light }],
  ['glass', { dark: { blurPx: '5', dim: 0.5, panel: 0 }, light: LS.SEED.glass.light, render: 'picture' }], ['colour', 'x'],
];
out.checkValue = values.map(([f, v]) => ({ field: f, value: JSON.stringify(v === undefined ? null : v), reason: LS.checkValue(f, v) }));
const desks = [{ rev: 1, ok: true }, { rev: 1.5, ok: true }, { rev: -1, ok: true }, { rev: 1 }, { rev: 1, ok: true, id: null, still: 'x' }, { rev: 1, ok: true, reason: 3 },
  { rev: 1, ok: true, stillSize: [1, 2] }, { rev: 1, ok: true, stillSize: [1, 2, 3] }, { rev: 1, ok: true, stillSize: [1.5, 2] }, { rev: 1, ok: true, stillSize: null }, [], 'x'];
out.checkDesktop = desks.map((d) => ({ value: JSON.stringify(d), reason: LS.checkDesktop(d) }));

/* ---- sync scenarios: real folders, the real module, byte-for-byte look.json after every step ------- */
const scenarios = [
  { name: 'first start seeds, then Mantle publishes each field, the desktop, and the switch',
    ops: [['start'], ['publish', 'theme', 'dark'], ['publish', 'accent', { hue: 200 }],
      ['publish', 'glass', { dark: { blurPx: 6, dim: 0.555, panel: 0.05 }, light: { blurPx: 1e-7, dim: 0.30000000000000004, panel: -0 }, render: 'seeThrough' }],
      ['publish', 'theme', 'dark'], ['publish', 'theme', 'Dark'], ['publish', 'colour', 1],
      ['desktop', { ok: true, reason: null, id: 'builtin:snow-peak', type: 'builtin', title: 'Snow peak — 雪 "q"', file: 'C:\\x\\snow-peak.jpg', still: 'C:\\s\\wall-6.jpg', stillSize: [2560, 1440] }],
      ['desktop', { ok: 'yes' }], ['enable', false], ['publish', 'theme', 'light'], ['desktop', { ok: false, reason: 'no picture', id: null, type: null, title: null, file: null, still: null }],
      ['enable', true], ['enable', true]] },
  { name: 'a Helm file with unknown keys: what Mantle keeps and drops when it publishes',
    file: texts[0], ops: [['start'], ['publish', 'theme', 'light'], ['publish', 'accent', { hue: 0, extra: 'kept' }]] },
  { name: 'bad fields are skipped and named; a publish rewrites only good fields', file: texts[1], ops: [['start'], ['publish', 'accent', { hue: 12 }]] },
  { name: 'a damaged file is moved aside and re-seeded from this app', file: 'not json, and it stays that way', ops: [['start'], ['publish', 'theme', 'dark']] },
  { name: 'a newer file is read-only and never written', file: '{"schema":2,"rev":5,"look":{"theme":{"v":"light","rev":5}},"futureThing":1}', ops: [['start'], ['publish', 'theme', 'dark'], ['desktop', { ok: true }]] },
  { name: 'another app writes; a stale field rev is ignored; a re-seed (rev went down) is adopted',
    file: '{"schema":1,"rev":10,"writer":{"app":"helm","instance":"h"},"look":{"theme":{"v":"dark","rev":10,"by":"helm"},"accent":{"v":{"hue":40},"rev":8,"by":"quartz"}}}',
    ops: [['start'], ['write', '{"schema":1,"rev":11,"writer":{"app":"quartz","instance":"q"},"look":{"theme":{"v":"light","rev":9,"by":"quartz"},"accent":{"v":{"hue":41},"rev":11,"by":"quartz"}}}'], ['refresh'],
      ['refresh'], ['write', '{"schema":1,"rev":1,"writer":{"app":"helm","instance":"h2"},"look":{"theme":{"v":"system","rev":1,"by":"helm"}}}'], ['refresh'], ['publish', 'glass', LS.SEED.glass]] },
  { name: 'missing while running: the next publish re-creates the file from current values', ops: [['start'], ['publish', 'theme', 'dark'], ['delete'], ['refresh'], ['publish', 'accent', { hue: 77 }]] },
  { name: 'own instance writes are never applied', file: '{"schema":1,"rev":3,"writer":{"app":"mantle","instance":"golden-mantle"},"look":{"theme":{"v":"dark","rev":3,"by":"mantle"}}}', ops: [['start']] },
  { name: 'sync off at start: nothing read into the look, the desktop still flows; on adopts',
    enabled: false, file: '{"schema":1,"rev":6,"writer":{"app":"helm","instance":"h"},"look":{"theme":{"v":"system","rev":6,"by":"helm"},"accent":{"v":{"hue":30},"rev":5,"by":"helm"},"glass":{"v":{"dark":{"blurPx":8,"dim":0.6,"panel":-0.1},"light":{"blurPx":20,"dim":0.45,"panel":0},"render":"picture"},"rev":4,"by":"quartz"}},"desktop":{"rev":6,"ok":false,"reason":"gone","by":"mantle"}}',
    ops: [['start'], ['publish', 'theme', 'dark'], ['enable', true], ['enable', false]] },
  { name: 'sync off with no file; turning it on seeds from this app', enabled: false, ops: [['start'], ['enable', true]] },
  { name: 'a damaged file while sync is off is reported, never touched', enabled: false, file: 'not json at all', ops: [['start']] },
  { name: 'Helm seeds; the app name shows in the reason', app: 'helm', ops: [['start'], ['desktop', { ok: true }]] },
];
async function runScenario(sc) {
  const d = tmpDir();
  const FILE = path.join(d, 'look.json');
  if (sc.file != null) fs.writeFileSync(FILE, sc.file);
  const app = sc.app || 'mantle';
  const s = LS.create({ app, instance: 'golden-' + app, dir: d, enabled: sc.enabled !== false, local: () => LOCAL, now: () => new Date(NOW),
    debounceMs: 600000, corruptRetryMs: 1, rewatchDelays: [600000], retryDelays: [1, 1, 1] });
  const events = [];
  s.on('status', (x) => events.push({ kind: 'status', state: x.state, reason: norm(x.reason, d), rev: x.rev, by: x.by, at: x.at, recovered: x.recovered || null, skipped: (x.skipped || []).length }));
  s.on('apply', (e) => events.push({ kind: 'apply', changes: JSON.stringify(e.changes), by: e.by, rev: e.rev, cause: e.cause }));
  s.on('desktop', (e) => events.push({ kind: 'desktop', record: JSON.stringify(e.record), reason: e.reason }));
  const steps = [];
  for (const op of sc.ops) {
    events.length = 0;
    let res = null;
    if (op[0] === 'start') { s.start(); await s.queue; await s.queue; }
    else if (op[0] === 'publish') res = await s.publish(op[1], op[2]);
    else if (op[0] === 'desktop') res = await s.publishDesktop(op[1]);
    else if (op[0] === 'enable') res = await s.setEnabled(op[1]);
    else if (op[0] === 'write') fs.writeFileSync(FILE, op[1]);
    else if (op[0] === 'delete') fs.unlinkSync(FILE);
    else if (op[0] === 'refresh') { s.refresh(); await s.queue; await s.queue; }
    steps.push({ op: JSON.stringify(op), res: res && { ok: res.ok, rev: res.rev == null ? null : res.rev, reason: norm(res.reason == null ? null : res.reason, d) },
      events: events.slice(), file: fileOf(d), list: listOf(d), status: s.status().state, readOnly: norm(s.readOnlyNow(), d) });
  }
  s.close();
  return { name: sc.name, app, enabled: sc.enabled !== false, file: sc.file == null ? null : sc.file, steps };
}

/* ---- look-host: vars, the local copy's bytes, the view's words ------------------------------------- */
function themeStub(m) { return { mode: () => m, resolved: () => (m === 'dark' ? 'dark' : 'light'), set: (x) => { m = x; return { ok: true }; } }; }
const varCases = [
  {}, { glass: { dark: { blurPx: 8, dim: 0.5, panel: 0 }, light: { blurPx: 12, dim: 0.45, panel: 0 }, render: 'picture' } },
  { glass: { dark: { blurPx: 5, dim: 0.555, panel: 0 }, light: { blurPx: 12, dim: 0.45, panel: 0 }, render: 'picture' } },
  { glass: { dark: { blurPx: 5, dim: 0.545, panel: -0.1 }, light: { blurPx: 12, dim: 0.45, panel: 0 }, render: 'picture' } },
  { glass: { dark: { blurPx: 5.125, dim: 0.5, panel: 0.15 }, light: { blurPx: 20, dim: 0.3, panel: 0.1 }, render: 'picture' } },
  { glass: { dark: { blurPx: 0, dim: 0.9, panel: -0.3 }, light: { blurPx: 0, dim: 0.2, panel: -0.3 }, render: 'seeThrough' } },
  { glass: { dark: { blurPx: 64, dim: 0.2, panel: 0.15 }, light: { blurPx: 12.004, dim: 0.455, panel: 0.15 }, render: 'picture' } },
  { accent: { hue: 212 } }, { accent: { hue: 0 } }, { accent: { hue: 360 } }, { accent: { hue: 137 }, glass: { dark: { blurPx: 9, dim: 0.7, panel: 0.1 }, light: { blurPx: 12, dim: 0.45, panel: 0 }, render: 'picture' } },
  { accent: { hue: 400 } }, { accent: { hue: 1.5 } }, { lookSync: 'no', glass: { dark: {} } },
];
function hostFor(local, mode) {
  const d = tmpDir();
  const file = path.join(d, 'look-sync.json');
  if (local !== undefined) fs.writeFileSync(file, typeof local === 'string' ? local : JSON.stringify(local));
  const lines = [];
  const h = LH.makeLookHost({ LS, accent, file, dir: path.join(d, 'look-sync'), theme: themeStub(mode || 'dark'), log: (l) => lines.push(l) });
  return { h, d, file, lines };
}
out.vars = varCases.map((c) => {
  const { h } = hostFor(c);
  const r = { local: JSON.stringify(c), dark: JSON.stringify(h.vars('dark')), light: JSON.stringify(h.vars('light')), saved: JSON.stringify(h._local()) };
  h.close();
  return r;
});
out.hostConstants = { DARK: LH.DARK, LIGHT: LH.LIGHT, PANEL_CLAMP: LH.PANEL_CLAMP };
async function hostScenario() {
  const { h, d, file, lines } = hostFor(undefined, 'dark');
  const steps = [];
  const snap = (what, r) => {
    const v = h.view();
    steps.push({ what, answer: r === undefined ? null : JSON.stringify(Object.assign({}, r, r && r.ok !== false ? { sync: undefined, desktop: undefined, windows: undefined, lock: undefined, guard: undefined } : {})),
      local: fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null, text: v.sync.text.replace(/ at \d\d:\d\d/, ' at HH:MM'), state: v.sync.state, glass: JSON.stringify(v.glass), glassLook: v.glassLook,
      accent: JSON.stringify(v.accent), shared: (() => { const t = fileOf(path.join(d, 'look-sync')); return t ? JSON.parse(t).look : null; })() });
  };
  snap('made');
  h.start(); await h.sync.queue; await h.sync.queue; snap('started');
  snap('glass dim .6', await h.setGlass({ dim: 0.6 }));
  snap('glass light blur 20', await h.setGlass({ blurPx: 20, look: 'light' }));
  snap('glass bad look', await h.setGlass({ dim: 0.6, look: 'blue' }));
  snap('glass out of range', await h.setGlass({ panel: 0.5 }));
  snap('glass text', await h.setGlass({ dim: '0.5' }));
  snap('glass nothing', await h.setGlass(null));
  snap('accent 200', await h.setAccent(200));
  snap('accent bad', await h.setAccent(361));
  snap('accent own', await h.setAccent(null));
  snap('sync bad', await h.setSync('on'));
  snap('sync off', await h.setSync(false));
  snap('accent while off', await h.setAccent(90));
  snap('sync on', await h.setSync(true));
  h.close();
  return { steps, lines: lines.map((l) => l.replace(/ at \d\d:\d\d/, ' at HH:MM')) };
}
const unreadable = () => { const { h, lines } = hostFor('{"lookSync": fals', 'light'); const r = { text: h.view().sync.text, reason: h.view().sync.reason, lines }; h.close(); return r; };

/* ---- the guard maths (PEND-005): glass-adapt's model and look-guard's band ------------------------- */
function img(w, h, f) {
  const data = new Uint8ClampedArray(w * h * 4);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const i = (y * w + x) * 4; const p = f(x, y); data[i] = p[0]; data[i + 1] = p[1]; data[i + 2] = p[2]; data[i + 3] = p[3] === undefined ? 255 : p[3]; }
  return { data, width: w, height: h };
}
const pictures = {
  snow: img(24, 10, (x, y) => [200 + ((x * 7 + y * 3) % 56), 210 + ((x * 3) % 46), 220 + ((y * 5) % 36)]),
  night: img(24, 10, (x, y) => [10 + ((x * 5) % 30), 12 + ((y * 7) % 20), 30 + ((x + y) % 40)]),
  mixed: img(20, 12, (x, y) => (x < 10 ? [240, 240, 235] : [20 + y * 3, 40 + x, 90]).concat(y === 0 ? [128] : [])),
  ties: img(16, 8, (x, y) => ((x + y) % 2 ? [128, 128, 128] : [100, 140, 120])),
};
out.pictures = Object.fromEntries(Object.entries(pictures).map(([k, v]) => [k, { width: v.width, height: v.height, data: Array.from(v.data) }]));
out.lum = [[0, 0, 0], [255, 255, 255], [169, 170, 167], [51, 58, 78], [9, 10, 11], [10.3, 200.7, 3.2]].map(([r, g, b]) => ({ rgb: [r, g, b], lum: GA.lum(r, g, b) }));
out.contrast = [[1, 0], [0.5, 0.2], [GA.lum(169, 170, 167), GA.lum(9, 10, 11)]].map(([a, b]) => ({ a, b, ratio: GA.contrast(a, b) }));
out.parseColor = ['rgba(30, 31, 34, 0.34)', 'rgb(1 2 3 / .5)', 'rgb(1,2,3)', '#a9aaa7', '#fff', 'rgba(1,2,3,50%)', 'blue', '', 'rgba(255,255,255,.45)'].map((s) => ({ s, c: GA.parseColor(s) }));
out.parseFilter = ['blur(12px) saturate(140%) brightness(.86)', 'blur(5px)', 'blur(12px) saturate(150%)', '', 'BLUR(3PX) saturate(1.2)'].map((s) => ({ s, f: GA.parseFilter(s) }));
out.glassPixels = [];
for (const [name, p] of Object.entries(pictures)) for (const f of [{ sigma: 0 }, { sigma: 0.3 }, { sigma: 2, saturate: 1.5 }, { sigma: 5.5, saturate: 1, brightness: 0.86 }]) {
  out.glassPixels.push({ picture: name, filter: f, px: GA.glassPixels(p.data, p.width, p.height, f) });
}
const T = (dim, blur, light) => LG.tokens(dim, blur, light);
out.glassTint = [];
for (const g of out.glassPixels) for (const [ink, tint, need, floor, ceil] of [[LG.RULE.ink, LG.RULE.tint, 4.2, 0.5, 0.88], [LG.RULE_LIGHT.ink, LG.RULE_LIGHT.tint, 4.7, 0.45, 0.88], [{ r: 196, g: 199, b: 206 }, { r: 19, g: 20, b: 21 }, 4.7, 0.83, 0.83], [LG.RULE.ink, LG.RULE.tint, 21, 0.2, 0.9]]) {
  out.glassTint.push({ picture: g.picture, filter: g.filter, ink, tint, need, floor, ceil, r: GA.glassTint({ px: g.px, ink, tint, need, floor, ceil, step: 0.02 }) });
}
out.glassTintEmpty = GA.glassTint({ px: [], ink: LG.RULE.ink, tint: LG.RULE.tint, need: 4.2, floor: 0.5, ceil: 0.88 });
out.guardRect = [];
for (const [name, p] of Object.entries(pictures)) for (const rect of [null, { x: 0, y: 0, w: 8, h: 4 }, { x: 2.5, y: 1.2, w: 30, h: 3.1 }, { x: 50, y: 0, w: 2, h: 2 }]) for (const light of [false, true]) {
  const Tk = T(light ? 0.45 : 0.5, 5, light);
  const pic = GA.glassPicture(p, Tk, 0.5);
  out.guardRect.push({ picture: name, rect, light, r: GA.guardRect(pic, Tk, rect) });
}
out.settle = [[null, 0.5], [{ cur: 0.6, lower: 0 }, 0.7], [{ cur: 0.6, lower: 0 }, 0.45], [{ cur: 0.6, lower: 0 }, 0.58], [{ cur: 0.6, lower: 0 }, 0.55], [{ cur: 0.6, lower: 3 }, 0.55], [{ cur: 0.6, lower: 1 }, 0.55, 2]]
  .map(([g, next, hold]) => ({ g, next, hold: hold === undefined ? null : hold, r: GA.glassSettle(g, next, hold) }));
out.bandGeometry = [[2560, 1440, 1536, 864, 5], [1920, 1080, 1920, 1080, 12], [3000, 1000, 1280, 1024, 20], [800, 1200, 1920, 1080, 0], [10, 10, 1920, 50, 64], [0, 10, 10, 10, 5], [1920, 1080, 1920, 1080, -3]]
  .map(([iw, ih, mw, mh, b]) => ({ args: [iw, ih, mw, mh, b], g: LG.bandGeometry(iw, ih, mw, mh, b, LG.BAND_DIP) }));
out.tokens = [[0.5, 5, false], [0.45, 12, true]].map(([d, b, l]) => ({ args: [d, b, l], t: T(d, b, l) }));
out.fromPixels = [];
for (const [name, p] of Object.entries(pictures)) for (const [dim, blurPx, light, ws, bandRows] of [[0.5, 5, false, 1, 6], [0.45, 12, true, 0.6667, 6], [0.5, 0, false, 1, 100], [0.45, 3, true, 1, 2], [0.2, 2, false, 0.5, 10]]) {
  out.fromPixels.push({ picture: name, o: { dim, blurPx, light, ws, bandRows }, r: LG.fromPixels(p, { dim, blurPx, light, ws, bandRows }) });
}
const recs = [null, { rev: 3, ok: true }, { rev: 3, ok: true, guardReason: 'no still' }, { rev: 3, ok: false, reason: 'gone' },
  { rev: 3, ok: true, guard: { dark: { alpha: 1.2, dim: 0.5, blurPx: 5 } } }, { rev: 3, ok: true, guard: { dark: { alpha: 0.6 } } },
  { rev: 3, ok: true, guard: { dark: { alpha: 0.62, contrast: 4.25, dim: 0.5, blurPx: 5, monitor: [1536, 864], region: { topDip: 0, heightDip: 100 } }, light: { alpha: 0.5, dim: 0.45, blurPx: 12 } } },
  { rev: 3.5, ok: true, guard: { dark: { alpha: 0.62, dim: 0.504, blurPx: 5.001, monitor: [1536, 864], region: { heightDip: 144 } } } }, { ok: true, guard: { dark: { alpha: 0.7, dim: 0.6, blurPx: 8 } } }];
out.use = [];
for (const rec of recs) for (const [dark, syncOn, glass, working] of [[true, true, { dim: 0.5, blurPx: 5 }, false], [true, true, { dim: 0.5, blurPx: 5 }, true], [true, true, { dim: 0.6, blurPx: 8 }, false], [true, true, { dim: 0.6, blurPx: 8 }, true], [false, true, null, false], [true, false, null, false], [true, true, null, false]]) {
  out.use.push({ c: { dark, syncOn, record: rec, glass, working }, r: LG.use({ dark, syncOn, record: rec, glass, working }) });
}
out.checkGuard = recs.map((r) => ({ g: r && r.guard ? r.guard : null, dark: LG.checkGuard(r && r.guard), light: LG.checkGuardLight(r && r.guard), key: LG.keyOfGuard(r && r.guard) }));
out.guardKey = [[0.5, 5, [1536, 864], { dim: 0.45, blurPx: 12 }], [0.555, 5.004, [1920, 1080], null], [0.5, 5, null, null], ['0.5', 5, [1, 1], null], [0.5, 5, [1, 1], { dim: 0.45 }]]
  .map(([d, b, m, l]) => ({ args: [d, b, m, l], key: LG.guardKey(d, b, m, l) }));
out.guardRules = { RULE: LG.RULE, RULE_LIGHT: LG.RULE_LIGHT, BAND_DIP: LG.BAND_DIP, MAX_SIGMA_PX: LG.MAX_SIGMA_PX, GUARD: GA.GUARD };

(async () => {
  out.sync = [];
  for (const sc of scenarios) out.sync.push(await runScenario(sc));
  out.host = await hostScenario();
  out.hostUnreadable = unreadable();
  const dest = path.join(__dirname, '..', 'Fixtures', 'look', 'golden.json');
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.writeFileSync(dest, JSON.stringify(out, null, 1) + '\n');
  fs.rmSync(TMP, { recursive: true, force: true });
  console.log('wrote', path.relative(ROOT, dest), (fs.statSync(dest).size / 1024).toFixed(0) + ' KB:', out.sync.length, 'sync scenarios,', out.parse.length, 'parse cases,', out.vars.length, 'vars cases');
})().catch((e) => { console.error(e); process.exit(1); });
