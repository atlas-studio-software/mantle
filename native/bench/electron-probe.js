'use strict';
/*
 * electron-probe.js - loaded into the Electron Mantle main process by run-bench.ps1 with
 * NODE_OPTIONS=--require=<this file> (on the runner only; Mantle's tools/perf-budget.js uses the same
 * hook). It changes nothing in Mantle.
 *
 * WHY IT HOOKS Module._load. Node runs NODE_OPTIONS --require modules in its pre-execution step, BEFORE
 * Electron's browser init script, which is what turns `require('electron')` into the API. So at load time
 * `require('electron')` here resolves through node_modules to the npm package `electron`, whose index.js
 * exports the PATH of electron.exe (a string). The spike's probe did exactly that, saw no `app`, and
 * silently did nothing: no marks.json, the dump requests never answered. This version waits instead
 * for the first `require('electron')` that returns the API (Mantle's main.js asks for it on its first
 * line) and boots then, before main.js has created any window. probe-loaded-<pid>.json is written at
 * load, unconditionally, so a run can tell "never loaded" from "loaded but never booted".
 *
 * It records, per window: created, did-finish-load, ready-to-show (Chromium's first paint of a page that
 * is not shown yet), first show, the page's own paint timeline (first-paint, first-contentful-paint from
 * performance.getEntriesByType('paint')), the second requestAnimationFrame after load (PERF.md's
 * "painted"), and the second rAF after the first show. On request it writes app.getAppMetrics() with each
 * page's renderer pid, so the bench can say which processes are the bar's and which are the rest of Mantle's.
 *
 * Requests: the bench creates <dir>/dump-<label>.req; the probe answers with <dir>/metrics-<label>.json.
 * fs.watch (ReadDirectoryChangesW): no polling timer in the measured process.
 */
const fs = require('fs');
const path = require('path');
const Module = require('module');

const dir = process.env.SPIKE_PROBE_OUT || process.env.MANTLE_BENCH_PROBE_OUT;
const loadedAt = Date.now();

function writeJson(name, obj) {
  try { fs.writeFileSync(path.join(dir, name), JSON.stringify(obj, null, 1)); } catch (_) { /* best effort */ }
}

if (dir) {
  try { fs.mkdirSync(dir, { recursive: true }); } catch (_) { /* exists */ }
  let resolvesTo = null;
  try { resolvesTo = require.resolve('electron'); } catch (err) { resolvesTo = 'unresolved: ' + String(err && err.message || err); }
  // What the spike's probe got: `require('electron')` at load time (the npm package's index.js returns a string).
  let early = null;
  try {
    const x = require('electron');
    early = { type: typeof x, hasApp: !!(x && x.app), value: typeof x === 'string' ? x : undefined };
  } catch (err) { early = { error: String(err && err.message || err) }; }
  writeJson(`probe-loaded-${process.pid}.json`, {
    pid: process.pid, atUnixMs: loadedAt, processType: process.type || null,
    electron: process.versions.electron || null, node: process.versions.node,
    argv: process.argv.slice(1), electronResolvesTo: resolvesTo, requireElectronAtLoad: early,
  });
}

// process.type is 'browser' in the main process; tolerate it being unset this early (probe-loaded says which).
if (dir && process.versions.electron && (process.type === 'browser' || !process.type)) {
  // Keep the hook out of everything Mantle starts (sidecar, PowerShell, renderers): only this process.
  delete process.env.NODE_OPTIONS;
  delete process.env.SPIKE_PROBE_OUT;
  delete process.env.MANTLE_BENCH_PROBE_OUT;

  const marks = { probeLoadedUnixMs: loadedAt, uptimeAtLoadS: process.uptime(), mainPid: process.pid, windows: [] };
  const save = () => writeJson('marks.json', marks);
  save();

  const PAINT_JS = 'new Promise((resolve) => {' +
    'const read = () => { const e = { timeOriginUnixMs: Math.round(performance.timeOrigin) };' +
    '  for (const p of performance.getEntriesByType("paint")) e[p.name] = Math.round(performance.timeOrigin + p.startTime);' +
    '  return e; };' +
    'let n = 0; const tick = () => { const e = read(); if (e["first-contentful-paint"] || ++n > 120) resolve(e); else setTimeout(tick, 250); };' +
    'tick(); })';
  const RAF2_JS = 'new Promise((r) => { let d = 0; const f = (t) => { if (!d) { d = 1; r(t); } };' +
    'requestAnimationFrame(() => requestAnimationFrame(() => f(Date.now()))); setTimeout(() => f(-1), 30000); })';

  let el = null;
  let bootedBy = null;
  const watchWindow = (win) => {
    if (!win || marks.windows.some((w) => w.id === win.id)) return;
    const rec = { id: win.id, createdUnixMs: Date.now() };
    marks.windows.push(rec);
    const wc = win.webContents;
    const run = (js, ok) => {
      try { wc.executeJavaScript(js, true).then(ok, (err) => { rec.jsError = String(err && err.message || err); save(); }); }
      catch (err) { rec.jsError = String(err && err.message || err); save(); }
    };
    win.once('ready-to-show', () => { rec.readyToShowUnixMs = Date.now(); save(); });
    win.once('show', () => {
      rec.firstShowUnixMs = Date.now(); save();
      run(RAF2_JS, (t) => { if (t > 0) rec.frameAfterShowUnixMs = t; else rec.frameAfterShowTimedOut = true; save(); });
    });
    wc.once('dom-ready', () => { rec.domReadyUnixMs = Date.now(); save(); });
    wc.once('did-finish-load', () => {
      try { rec.url = wc.getURL(); } catch (_) { rec.url = null; }
      rec.loadedUnixMs = Date.now();
      save();
      run(RAF2_JS, (t) => { if (t > 0) rec.paintedUnixMs = t; else rec.paintedTimedOut = true; save(); });
      run(PAINT_JS, (e) => { rec.paintEntries = e; save(); });
    });
  };

  const boot = (electron) => {
    if (el) return;
    el = electron;
    marks.bootUnixMs = Date.now(); marks.bootedBy = bootedBy; save();
    const { app, BrowserWindow, webContents } = el;
    app.on('browser-window-created', (_e, win) => watchWindow(win));
    try { for (const w of BrowserWindow.getAllWindows()) watchWindow(w); } catch (_) { /* none yet */ }

    const dump = (label) => {
      let metrics = [];
      try { metrics = app.getAppMetrics(); } catch (err) { metrics = [{ error: String(err) }]; }
      const pages = [];
      try {
        for (const wc of webContents.getAllWebContents()) {
          let win = null;
          try { win = BrowserWindow.fromWebContents(wc); } catch (_) { win = null; }
          let pid = null;
          try { pid = wc.getOSProcessId(); } catch (_) { pid = null; }
          pages.push({
            url: wc.getURL(), pid, type: wc.getType(),
            window: win ? { id: win.id, visible: win.isVisible(), bounds: win.getBounds(), title: win.getTitle() } : null,
          });
        }
      } catch (err) { pages.push({ error: String(err) }); }
      writeJson(`metrics-${label}.json`, { label, atUnixMs: Date.now(), mainPid: process.pid, metrics, pages });
      save();
    };

    app.whenReady().then(() => {
      marks.readyUnixMs = Date.now(); save();
      try {
        fs.watch(dir, (_ev, name) => {
          const m = /^dump-(.+)\.req$/.exec(String(name || ''));
          if (!m) return;
          const req = path.join(dir, String(name));
          if (!fs.existsSync(req)) return;
          try { fs.unlinkSync(req); } catch (_) { return; }
          dump(m[1]);
        });
      } catch (err) { marks.watchError = String(err); save(); }
    });
  };

  // If this Electron ever runs --require after its own init, the API is already here.
  try {
    const r = require.resolve('electron') === 'electron' ? require('electron') : null;
    if (r && r.app && r.BrowserWindow) { bootedBy = 'the API was already there at load'; boot(r); }
  } catch (_) { /* the normal case: not yet */ }

  if (!el) {
    const origLoad = Module._load;
    Module._load = function (request) {
      const r = origLoad.apply(this, arguments);
      if (!el && request === 'electron' && r && r.app && r.BrowserWindow) {
        bootedBy = 'Module._load hook: the first require("electron") that returned the API (main.js)';
        try { boot(r); } catch (err) { marks.bootError = String(err && err.stack || err); save(); }
      }   // (not unhooked: something may have wrapped Module._load after us; once booted this is one compare)
      return r;
    };
  }
}
