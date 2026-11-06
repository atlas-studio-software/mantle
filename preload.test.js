/*
 * preload.test.js - preload.js's mantle.call when the call cannot even reach main (lane pagewords,
 * 2026-10-14). The page must get a plain sentence it can put on screen, and Electron's own words
 * ("Error invoking remote method ...") must still go somewhere a human can read them: the page's
 * console, which main copies into mantle.log (main.js console-message).
 *
 * The REAL preload.js runs in a vm with a fake 'electron' whose ipcRenderer.invoke fails the way
 * Electron fails: a channel with no handler (the literal Electron message), and arguments that cannot
 * be cloned (node's own structuredClone throws the real DataCloneError on a function).
 *
 *   node preload.test.js   -> "PRELOAD TEST: n passed, m failed"
 */
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

let passed = 0;
const failures = [];
const ok = (cond, name, detail) => { if (cond) passed++; else failures.push(detail !== undefined ? `${name} (${String(detail).slice(0, 300)})` : name); };

function load(invoke, webFrame) {
  const logged = [];
  let api = null;
  const el = { getAttribute: () => null, setAttribute() {}, style: { removeProperty() {}, setProperty() {}, getPropertyValue: () => '' } };
  const electron = {
    contextBridge: { exposeInMainWorld: (name, v) => { if (name === 'mantle') api = v; } },
    ipcRenderer: { sendSync: () => ({ theme: 'light' }), on() {}, invoke },
    webUtils: { getPathForFile: () => '' },
    webFrame,
  };
  const fakeConsole = { error: (...a) => logged.push(a.map(String).join(' ')), warn: (...a) => logged.push(a.map(String).join(' ')), log() {} };
  const sandbox = {
    require: (m) => { if (m === 'electron') return electron; throw new Error(`preload may not require ${m}`); },
    console: fakeConsole,
    document: { documentElement: el, addEventListener() {}, readyState: 'complete' },
    window: { addEventListener() {}, dispatchEvent() {} },
    CustomEvent: function CustomEvent() {},
    setTimeout, clearTimeout, Promise, structuredClone,
  };
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(path.join(__dirname, 'preload.js'), 'utf8'), sandbox, { filename: 'preload.js' });
  return { api, logged };
}

const CODE = /0x[0-9a-f]{4,}|Error invoking|remote method|mantle:call|IPC|DataCloneError|could not be cloned|\[object/i;

(async () => {
  // 1. the channel is gone (main's handler not registered, or main is shutting down)
  {
    const { api, logged } = load(() => Promise.reject(new Error("Error invoking remote method 'mantle:call': Error: No handler registered for 'mantle:call'")));
    ok(api && typeof api.call === 'function', 'preload exposes window.mantle.call');
    let msg = null;
    try { await api.call('files.list', { path: 'C:\\' }); } catch (e) { msg = e.message; }
    ok(msg === 'Mantle did not answer. Try again; if it keeps happening, restart Mantle.', 'no handler: a plain sentence on screen', msg);
    ok(msg && !CODE.test(msg) && !/files\.list/.test(msg), 'no handler: no Electron words, channel or command name on screen', msg);
    const line = logged.find((l) => /mantle\.call\("files\.list"\) could not reach Mantle: Error invoking remote method 'mantle:call': Error: No handler registered/.test(l));
    ok(!!line, 'no handler: Electron\'s own words are logged (the console line main copies into mantle.log)', logged.join(' | '));
  }
  // 2. the page passed something that cannot cross (the real clone failure)
  {
    const { api, logged } = load((ch, name, args) => { structuredClone(args); return Promise.resolve({ ok: true, result: 1 }); });
    let msg = null;
    try { await api.call('files.op', { op: 'copy', cb: () => 1 }); } catch (e) { msg = e.message; }
    ok(/^This page asked Mantle for something in a form it cannot send, so nothing was done\. The details are in Mantle's log\.$/.test(msg || ''), 'not cloneable: a true plain sentence', msg);
    ok(msg && !CODE.test(msg), 'not cloneable: no engine words on screen', msg);
    ok(logged.some((l) => /mantle\.call\("files\.op"\) could not reach Mantle: .*could not be cloned/.test(l)), 'not cloneable: the engine\'s reason is logged', logged.join(' | '));
  }
  // 3. the normal paths are unchanged
  {
    const { api, logged } = load((ch, name) => Promise.resolve(name === 'good' ? { ok: true, result: 42 } : { ok: false, error: 'access was denied' }));
    ok(await api.call('good') === 42, 'a good reply still resolves');
    let msg = null;
    try { await api.call('bad'); } catch (e) { msg = e.message; }
    ok(msg === 'access was denied', 'a refused reply still rejects with main\'s (already plain) words', msg);
    ok(!logged.some((l) => /could not reach/.test(l)), 'nothing logged for a normal reply', logged.join(' | '));
  }
  {
    // the picture cache (lane mantle-slim-bar): read always, cleared ONLY over the line the page gives
    let images = { count: 140, size: 21 * 1048576, liveSize: 20 * 1048576 };
    let clears = 0;
    const wf = { getResourceUsage: () => ({ images }), clearCache: () => { clears++; images = { count: 0, size: 0, liveSize: 0 }; } };
    const { api } = load(() => Promise.resolve({ ok: true, result: null }), wf);
    ok(api && typeof api.imageCache === 'function', 'preload exposes window.mantle.imageCache');
    let r = api.imageCache({});
    ok(!r.cleared && r.before.count === 140 && r.before.bytes === 21 * 1048576 && clears === 0, 'imageCache({}) only reads', JSON.stringify(r));
    r = api.imageCache({ clearOver: 32 * 1048576 });
    ok(!r.cleared && clears === 0, 'under the line: not cleared', JSON.stringify(r));
    r = api.imageCache({ clearOver: 8 * 1048576 });
    ok(r.cleared && clears === 1 && r.before.count === 140 && r.after.count === 0 && r.after.bytes === 0, 'over the line: cleared once, with the numbers before and after', JSON.stringify(r));
    r = api.imageCache({ clearOver: 'x' });
    ok(!r.cleared && clears === 1, 'a line that is not a number never clears', JSON.stringify(r));
    let threw = '';
    try { load(() => Promise.resolve({ ok: true }), undefined).api.imageCache({ clearOver: 0 }); } catch (e) { threw = e.message; }
    ok(/no webFrame/.test(threw), 'no webFrame: it throws with the reason (the bar says it once)', threw);
  }
  for (const f of failures) console.log('FAIL ' + f);
  console.log(`PRELOAD TEST: ${passed} passed, ${failures.length} failed`);
  process.exit(failures.length ? 1 : 0);
})();
