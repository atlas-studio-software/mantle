#!/usr/bin/env node
// make-golden.cjs - the spec for the native clipboard panel and emoji picker models is Electron's own output
// (lane native-r9-clipemoji). Runs ui/clipboard/model.js and ui/emoji/model.js (+ emoji-data.js) in plain node
// over fixed inputs and writes:
//
//   native/src/Mantle.Core/Features/Emoji/EmojiTabs.g.cs          model.js SYMBOLS, KAOMOJI, POPULAR (the emoji data itself is
//                                                                 ui/emoji/emoji-data.js, embedded by Mantle.Core.csproj, never copied)
//   native/tests/Mantle.Core.Tests/ClipEmoji/ClipEmojiGolden.g.cs   inputs + Electron's answers, one JSON string
//
//   node native/tools/parity/clipemoji/make-golden.cjs            write both files
//   node native/tools/parity/clipemoji/make-golden.cjs --check    exit 1 with "CLIPEMOJI GOLDEN: DRIFT" when Electron moved on
//
// Pure: no window, no clipboard, no user data. Only fixture strings of this file go into the outputs.
'use strict';
const fs = require('fs');
const path = require('path');

const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const C = require(path.join(REPO, 'ui', 'clipboard', 'model.js'));
const E = require(path.join(REPO, 'ui', 'emoji', 'model.js'));
const DATA = require(path.join(REPO, 'ui', 'emoji', 'emoji-data.js'));
const OUT_DATA = path.join(REPO, 'native', 'src', 'Mantle.Core', 'Features', 'Emoji', 'EmojiTabs.g.cs');
const OUT_GOLD = path.join(REPO, 'native', 'tests', 'Mantle.Core.Tests', 'ClipEmoji', 'ClipEmojiGolden.g.cs');

/* ---------------- clipboard ---------------- */
const NOW = 1763000000000;
const items = [
  { id: 'a', kind: 'text', text: 'Hello World', chars: 11, lines: 1, ts: NOW - 5000 },
  { id: 'b', kind: 'text', text: 'line one\nline two\nline three\nline four', chars: 37, lines: 4, rich: true, ts: NOW - 300000 },
  { id: 'c', kind: 'image', w: 800, h: 600, ts: NOW - 7200000 },
  { id: 'd', kind: 'files', files: [{ name: 'Report.docx', path: 'D:\\Work\\Report.docx' }, { name: 'Photos', path: 'D:\\Pics\\Photos', dir: true }], fileCount: 2, ts: NOW - 90000000 },
  { id: 'e', kind: 'text', text: 'world peace', chars: 11, lines: 1, pinned: true, ts: NOW - 3 * 86400000 },
  { id: 'f', kind: 'image', w: 0, h: 0, fileCount: 1, ts: NOW - 40000 },
  { id: 'g', kind: 'text', text: '   \n\t ', chars: 6, lines: 2, ts: NOW - 60000 },
  { id: 'h', kind: 'files', files: [{ name: 'a.txt', path: 'C:\\x\\a.txt' }], fileCount: 1, ts: NOW - 2 * 86400000 },
  { id: 'i', kind: 'text', text: 'x', chars: 1, lines: 1, ts: NOW - 1000 },
];
const queries = ['', 'world', 'WORLD', 'hello world', 'world hello', 'image', 'picture', 'screenshot', 'file', 'files', 'pinned',
  'report', 'work\\report', 'docx', 'nothing-here', '  line   two ', 'photos', 'd:\\pics', 'line four', 'x', 'peace pinned'];
const filter = queries.map((q) => ({ q, ids: C.filter(items, q).map((i) => i.id) }));

const texts = ['', 'one', '\n\n\nfirst\nsecond', 'a\r\nb\rc\nd', '\tTab\there', 'trail   \nend', '   \n \n', 'x'.repeat(300),
  'a\nb\nc\nd', 'a\nb\nc\n\n  \n', 'a\nb\nc\n\nz', '\u00e9\u00e8 caf\u00e9', 'only\n'];
const preview = [];
for (const t of texts) for (const max of [undefined, 1, 3, 5]) preview.push({ t, max: max == null ? 0 : max, r: C.previewLines(t, max) });

const agoSec = [0, 1, 44, 45, 89, 90, 600, 3569, 3600, 5400, 82800, 86399, 86400, 129599, 129600, 518400, 561599];
const ago = agoSec.map((s) => ({ s, r: C.ago(NOW - s * 1000, NOW) }));
const meta = items.map((it) => ({ id: it.id, r: C.meta(it, NOW) }))
  .concat([{ id: 'one-char', it: { kind: 'text', chars: 1, lines: 1, ts: NOW }, r: C.meta({ kind: 'text', chars: 1, lines: 1, ts: NOW }, NOW) },
    { id: 'three-lines', it: { kind: 'text', chars: 20, lines: 3, ts: NOW }, r: C.meta({ kind: 'text', chars: 20, lines: 3, ts: NOW }, NOW) },
    { id: 'one-file', it: { kind: 'files', files: [{ name: 'x' }], ts: NOW }, r: C.meta({ kind: 'files', files: [{ name: 'x' }], ts: NOW }, NOW) },
    { id: 'rich-image', it: { kind: 'image', rich: true, w: 2, h: 3, ts: NOW }, r: C.meta({ kind: 'image', rich: true, w: 2, h: 3, ts: NOW }, NOW) }]);

const keys = ['Escape', 'ArrowDown', 'ArrowUp', 'PageDown', 'PageUp', 'Enter', 'Delete', 'p', 'P', '/', 'Home', 'End', 'a', 'Tab', 'Backspace'];
const mods = [{}, { ctrlKey: true }, { altKey: true }, { metaKey: true }, { ctrlKey: true, altKey: true }];
const states = [{}, { query: 'ab' }, { filterArmed: true }, { query: 'ab', selStart: 2, selEnd: 2 }, { query: 'ab', selStart: 0, selEnd: 2 },
  { query: 'ab', selStart: 1, selEnd: 1 }, { query: '', selStart: 0, selEnd: 0 }, { query: 'p', filterArmed: true }];
const clipKeys = [];
for (const key of keys) for (const m of mods) for (const st of states) clipKeys.push({ e: Object.assign({ key }, m), st, r: C.keyAction(Object.assign({ key }, m), st) });

const moves = [];
for (const a of ['down', 'up', 'pagedown', 'pageup', 'home', 'end', 'none']) for (const n of [0, 1, 3, 10]) for (const sel of [-1, 0, 2, 9]) for (const page of [0, 4, 6])
  moves.push({ a, n, sel, page, r: C.moveSel(sel, a, n, page || undefined) });

const lists = [[], [{ id: 'a' }], [{ id: 'a' }, { id: 'b' }, { id: 'c' }]];
const reselect = [];
for (const l of lists) for (const id of ['a', 'c', 'zz']) for (const idx of [0, 1, 5, -1]) reselect.push({ id, idx, n: l.map((i) => i.id), r: C.reselect(id, idx, l) });

const works = [{ x: 0, y: 0, width: 1920, height: 1040 }, { x: 1920, y: -200, width: 1280, height: 984 }, { x: 0, y: 0, width: 1280, height: 400 }, { x: -1600, y: 0, width: 1600, height: 860 }];
const anchors = [{ x: 100, y: 100, h: 18 }, { x: 1900, y: 100 }, { x: 5, y: 5 }, { x: 600, y: 1000, h: 20 }, { x: 600, y: 520, h: 16 }, { x: 2500, y: 700, h: 20 },
  { x: 640, y: 230, h: 20 }, { x: -800, y: 850, h: 14 }, { x: 960.6, y: 300.4, h: 17.5 }];
const sizes = [null, { w: 200, h: 100, m: 0 }];
const clipPlace = [];
for (const w of works) for (const a of anchors) for (const s of sizes) clipPlace.push({ a, w, s, r: C.place(a, w, s || undefined) });

const glass = [[{ ok: true, ms: 40.4, capture: 12, bake: 20 }, 3, true], [{ ok: true, ms: 40, capture: 12, bake: 20 }, 3, false], [{ ok: true, ms: 400, capture: null, bake: 20 }, 3, false],
  [{ ok: false, reason: 'no picture' }, 3, false], [{ ok: false }, -1, true], [null, null, false], [{ ok: true, ms: -3, capture: 'x', bake: Infinity }, 5, false]]
  .map(([a, since, first]) => { const g = C.glassOf(a, since); return { a, since, first, g, line: C.glassLine(g, first) }; });

/* ---------------- emoji ---------------- */
const cat = E.catalogue(DATA);
const sections = (secs) => secs.map((s) => ({ id: s.id, name: s.name, glyph: s.glyph || '', n: s.items.length,
  first: s.items.slice(0, 3).map((i) => i.ch), words: s.items.slice(0, 2).map((i) => i.words), nameWords: s.items.slice(0, 2).map((i) => i.nameWords) }));
const emojiQ = ['joy', 'heart', 'thumbs', 'lol', 'cat heart', 'cat', 'hea', 'rocket', 'smile', 'SMILE', '  red   heart ', 'face', 'flag', 'pirate', 'hand',
  'xyzzy', 'a', 'e', 'fire', 'ok', 'tears', 'sad', 'love', 'dog', 'party', 'clap', 'eyes', 'pray', 'sun', 'moon', 'star', 'water', 'music', 'car', 'red',
  'blue', 'green', 'happy', 'cry', 'laugh', 'kiss', 'wink', 'thinking', 'skull', 'ghost', 'poo', '100', '1', 'check', 'x', 'woman', 'man', 'family', 'couple',
  'health worker', 'victory', 'point', 'up', 'down', 'left', 'right', 'arrow', 'euro', 'copyright', 'shrug', 'table', 'pi', 'greek', 'money', 'math'];
for (const c of 'bcdfghijklmnopqrstuvwyz') emojiQ.push(c);
for (const w of ['sm', 'smi', 'smil', 'he', 'th', 'thu', 'ca', 'fa', 'ha', 'ro', 'roc']) emojiQ.push(w);
const tabs = { emoji: cat.emoji, symbols: cat.symbols, kaomoji: cat.kaomoji };
const search = [];
for (const q of emojiQ) for (const tab of Object.keys(tabs)) {
  const all = E.search(tabs[tab], q);
  search.push({ q, tab, n: all.length, top: all.slice(0, 24).map((i) => i.ch) });
}
search.push({ q: 'a', tab: 'emoji', limit: 5, n: E.search(cat.emoji, 'a', 5).length, top: E.search(cat.emoji, 'a', 5).map((i) => i.ch) });

const toned = [];
for (const s of cat.emoji) for (const it of s.items) if (it.plan) toned.push({ ch: it.ch, plan: it.plan, d: [1, 2, 3, 4, 5].map((t) => E.display(it, t)) });
const strip = [];
for (const t of toned) for (const d of t.d) strip.push({ s: d, r: E.stripTone(d) });
const clamp = [0, 1, 5, 6, -1, 2.5, '3', 'x', null, undefined, NaN].map((v) => ({ v: v === undefined ? 'undefined' : (typeof v === 'number' && isNaN(v) ? 'NaN' : v), r: E.clampTone(v) }));
const plans = [1, '0,4', '0,3,5', '', 0, null, 2].map((raw) => ({ raw, r: E.planOf(raw) }));

const rememberCases = [[[], '😀'], [['😀', '😂'], '😂'], [Array.from({ length: 40 }, (_, i) => String.fromCodePoint(0x1F600 + i)), '❤️'], [null, 'x'], [['', 5, null, '😀'], '😂']]
  .map(([l, ch]) => ({ l, ch, r: E.remember(l, ch) }));
const recentCases = [['😂', '👍🏽', '✌🏿', 'zz', '', 7, 'x'.repeat(70), '👍']].map((l) => ({ l, r: E.recentItems(l, cat).map((i) => ({ ch: i.ch, name: i.name })) }));

const layouts = [[[3, 10, 1], 8], [[0, 8, 9], 8], [[5], 2], [[], 8], [[1, 1, 1], 8]];
const emoMoves = [];
for (const [counts, cols] of layouts) {
  const rows = E.layout(counts, cols);
  const total = counts.reduce((a, b) => a + b, 0);
  for (const key of ['ArrowRight', 'ArrowLeft', 'ArrowDown', 'ArrowUp', 'PageDown', 'PageUp', 'Home', 'End', 'a'])
    for (const sel of [-1, 0, 2, 5, total - 1, total, total + 3]) for (const page of [0, 1, 5])
      emoMoves.push({ counts, cols, key, sel, page, r: E.move(rows, sel, key, page || undefined) });
}
const layoutGold = layouts.map(([counts, cols]) => ({ counts, cols, rows: E.layout(counts, cols) }));

const emoKeyIn = [];
for (const key of ['Escape', 'Enter', 'Backspace', 'Delete', 'Tab', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', 'F5', '', null])
  for (const shift of [false, true]) emoKeyIn.push({ key, shift });
for (const text of ['a', 'Z', ' ', '\u0007', 'ab', '', '\u00e9', '~']) emoKeyIn.push({ key: 'x', text });
const emoKeys = [];
for (const k of emoKeyIn) for (const closeAfter of [false, true]) emoKeys.push({ k, closeAfter, r: E.keyAction(k, closeAfter) });

const emoPlace = [];
for (const w of works) for (const a of anchors.concat([null, { x: NaN, y: 3 }])) for (const s of [null, { w: 100, h: 50 }])
  emoPlace.push({ a: a && !isFinite(a.x) ? { x: 'NaN', y: a.y } : a, w, s, r: E.place(a, w, s || undefined) });

const gold = {
  clip: { now: NOW, items, filter, preview, ago, meta, keys: clipKeys, moves, reselect, place: clipPlace, glass,
    consts: { PANEL_W: C.PANEL_W, PANEL_H: C.PANEL_H, MARGIN: C.MARGIN, GLASS_SLOW_MS: C.GLASS_SLOW_MS } },
  emoji: { sections: { emoji: sections(cat.emoji), symbols: sections(cat.symbols), kaomoji: sections(cat.kaomoji) },
    search, toned, strip, clamp, plans, remember: rememberCases, recents: recentCases, layouts: layoutGold, moves: emoMoves, keys: emoKeys, place: emoPlace,
    consts: { PANEL_W: E.PANEL_W, PANEL_H: E.PANEL_H, MARGIN: E.MARGIN, COLS: E.COLS, RECENT_MAX: E.RECENT_MAX, TONES: E.TONES, TONE_NAMES: E.TONE_NAMES,
      GROUP_LABEL: E.GROUP_LABEL, GROUP_GLYPH: E.GROUP_GLYPH } },
};

function cs(ns, cls, comment, json) {
  if (json.includes('"""""')) throw new Error('the JSON holds five quotes in a row; pick a longer raw-string delimiter');
  return `// <auto-generated> ${comment}\n// Regenerate: node native/tools/parity/clipemoji/make-golden.cjs   (check: --check)\n` +
    `namespace ${ns};\n\ninternal static class ${cls}\n{\n    public const string Json = """""\n${json}\n""""";\n}\n`;
}
// model.js keeps SYMBOLS, KAOMOJI and POPULAR private: the tabs come back through catalogue() (kw as typed, joined by |),
// POPULAR is read from the source text itself (the only string literal in its Array.from call).
const modelSrc = fs.readFileSync(path.join(REPO, 'ui', 'emoji', 'model.js'), 'utf8');
const popMatch = /var POPULAR = Array\.from\('([^']*)'\)/.exec(modelSrc);
if (!popMatch) throw new Error('ui/emoji/model.js: POPULAR not found; update make-golden.cjs');
const tabOut = (secs) => secs.map((s) => ({ id: s.id, name: s.name, items: s.items.map((i) => [i.ch, i.name, i.kw.join('|')]) }));
const dataCs = cs('Mantle.Core.Features.Emoji', 'EmojiTabs',
  'ui/emoji/model.js SYMBOLS, KAOMOJI and POPULAR, as catalogue() gives them. The emoji groups are ui/emoji/emoji-data.js itself (an embedded resource).',
  JSON.stringify({ symbols: tabOut(cat.symbols), kaomoji: tabOut(cat.kaomoji), popular: popMatch[1] }));
const goldCs = cs('Mantle.Core.Tests.ClipEmoji', 'ClipEmojiGolden',
  "Electron's ui/clipboard/model.js and ui/emoji/model.js answers over fixed inputs.", JSON.stringify(gold));

const norm = (s) => s.replace(/\r\n/g, '\n');
if (process.argv.includes('--check')) {
  const drift = [];
  for (const [file, want] of [[OUT_DATA, dataCs], [OUT_GOLD, goldCs]]) {
    let have = '';
    try { have = fs.readFileSync(file, 'utf8'); } catch (_) { /* missing */ }
    if (norm(have) !== norm(want)) drift.push(path.relative(REPO, file).replace(/\\/g, '/'));
  }
  console.log(drift.length ? `CLIPEMOJI GOLDEN: DRIFT (${drift.join(', ')}): Electron changed; regenerate and re-port` :
    `CLIPEMOJI GOLDEN: ok (${search.length} searches, ${clipKeys.length + emoKeys.length} key cases, ${clipPlace.length + emoPlace.length} placements)`);
  process.exit(drift.length ? 1 : 0);
}
fs.mkdirSync(path.dirname(OUT_DATA), { recursive: true });
fs.mkdirSync(path.dirname(OUT_GOLD), { recursive: true });
fs.writeFileSync(OUT_DATA, dataCs);
fs.writeFileSync(OUT_GOLD, goldCs);
console.log(`wrote ${path.relative(REPO, OUT_DATA)} (${dataCs.length} chars) and ${path.relative(REPO, OUT_GOLD)} (${goldCs.length} chars)`);
