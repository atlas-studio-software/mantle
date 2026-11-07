'use strict';
/*
 * electron-probe.js - loaded into the Electron Mantle main process by run-bench.ps1
 * (NODE_OPTIONS=--require, on the runner only; Mantle's own tools/perf-budget.js uses the same hook).
 * It changes nothing in Mantle. It records, per window, when it was created, when its page finished
 * loading, its ready-to-show, and the page's second requestAnimationFrame after load (PERF.md's
 * "painted"). On request it writes app.getAppMetrics() with each window's renderer pid, so the
 * bench can say which processes are the bar's and which are the rest of Mantle's.
 *
 * Requests: the bench creates <dir>/dump-<label>.req; the probe answers with <dir>/metrics-<label>.json.
 * fs.watch (ReadDirectoryChangesW): no polling timer in the measured process.
 */
const fs = require('fs');
const path = require('path');

const dir = process.env.SPIKE_PROBE_OUT;
let electron = null;
try { electron = require('electron'); } catch (_) { electron = null; }

if (dir && electron && electron.app && process.type === 'browser') {
  const { app, BrowserWindow, webContents } = electron;
  fs.mkdirSync(dir, { recursive: true });
  const marks = { probeLoadedUnixMs: Date.now(), uptimeAtLoadS: process.uptime(), windows: [] };
  const save = () => { try { fs.writeFileSync(path.join(dir, 'marks.json'), JSON.stringify(marks, null, 1)); } catch (_) { /* best effort */ } };

  app.on('browser-window-created', (_e, win) => {
    const rec = { id: win.id, createdUnixMs: Date.now() };
    marks.windows.push(rec);
    const wc = win.webContents;
    win.once('ready-to-show', () => { rec.readyToShowUnixMs = Date.now(); save(); });
    win.once('show', () => { rec.firstShowUnixMs = Date.now(); save(); });
    wc.once('did-finish-load', () => {
      rec.url = wc.getURL();
      rec.loadedUnixMs = Date.now();
      wc.executeJavaScript('new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(() => r(Date.now()))))', true)
        .then((t) => { rec.paintedUnixMs = t; save(); }, (err) => { rec.paintError = String(err && err.message || err); save(); });
      save();
    });
  });

  const dump = (label) => {
    let metrics = [];
    try { metrics = app.getAppMetrics(); } catch (err) { metrics = [{ error: String(err) }]; }
    const pages = [];
    try {
      for (const wc of webContents.getAllWebContents()) {
        let win = null;
        try { win = BrowserWindow.fromWebContents(wc); } catch (_) { win = null; }
        pages.push({
          url: wc.getURL(), pid: wc.getOSProcessId(), type: wc.getType(),
          window: win ? { id: win.id, visible: win.isVisible(), bounds: win.getBounds(), title: win.getTitle() } : null,
        });
      }
    } catch (err) { pages.push({ error: String(err) }); }
    const out = { label, atUnixMs: Date.now(), mainPid: process.pid, metrics, pages };
    try { fs.writeFileSync(path.join(dir, `metrics-${label}.json`), JSON.stringify(out, null, 1)); } catch (_) { /* best effort */ }
  };

  app.whenReady().then(() => {
    marks.readyUnixMs = Date.now(); save();
    try {
      fs.watch(dir, (_ev, name) => {
        const m = /^dump-(.+)\.req$/.exec(String(name || ''));
        if (!m) return;
        const req = path.join(dir, name);
        if (!fs.existsSync(req)) return;
        try { fs.unlinkSync(req); } catch (_) { return; }
        dump(m[1]);
      });
    } catch (err) { marks.watchError = String(err); save(); }
  });
}
