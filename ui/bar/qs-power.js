/*
 * ui/bar/qs-power.js - "Power use" in the quick-settings panel (lane mantle-ps-qs, 2026-11-18).
 *
 * WHAT (the user, 2026-11-18): one row in quick settings, "Power use >", that opens a sub-view in the
 * same panel (the Wi-Fi / Bluetooth pattern of ui/bar/qs-more.js: a back arrow, the main view hidden
 * by .pop.qsx-sub). The view is THE SAME plugged in and on battery: Now | Today | Last 24 h.
 *   Now           the draw right now (the battery's total on battery; the processor's own counters
 *                 always, where Windows has them), then the apps by share, then the fixed rows
 *                 (Windows, the rest of the processor, the screen and the rest of the PC). Re-read
 *                 every 2 s, ONLY while Now is the tab on screen and the view is open.
 *   Today / 24 h  one read per tab open: a static hourly column chart, the totals, the apps.
 * Per-app numbers are an ESTIMATE (an app's share of processor and graphics time times the measured
 * power), share first; the reply's caption says so at the bottom of every tab, always.
 *
 * WHAT IT TALKS TO (lib/features/usage.js; the shapes: the power-pull design's addendum, "Command
 * replies"):
 *   usage.state               {ok, on, power, screentime, idleMin, reason}: the row shows when on && power
 *   usage.power.now           {ok, at, source, battery, total, chip, shareOf, rows, caption, notes}
 *   usage.power.history       {range:'today'|'24h'} -> {ok, hours, rows, totals, empty, emptyWhy, caption, notes}
 *   usage.icons {paths}       {ok, icons:{path: dataUrl|null}, why:{path: reason}}: cached for the page's life
 * Every failure is {ok:false, reason}; the feature switched off in the tray is {ok:false, off:true, reason}.
 * Every one of them is drawn in words, never as an empty view.
 *
 * NO MOTION: the share bars and the chart are static; a refresh changes widths and text, nothing
 * animates (the panel sits under backdrop-filter, where any motion repaints the blur).
 *
 * Loaded as a plain <script> by the bar (window.MantleQsPower) and by require() in node:
 * ui/bar/qs-power.test.js tests the pure half (nowView, historyView and the formatters).
 * bar.js calls attach(pop, ctx, before) each time it builds the panel and closed() when it closes.
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.MantleQsPower = factory();
}(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var POLL_MS = 2000;
  /* The sub-view's own height, qs-more's: the popover adds 24 and main's room request 16, so this
     asks for 400 of main's 640 cap. */
  var VIEW_H = 360;
  var TABS = [
    { id: 'now', label: 'Now' },
    { id: 'today', label: 'Today' },
    { id: '24h', label: 'Last 24 h' }
  ];
  /* Shown until the first reply brings node's own caption: the numbers are labelled an estimate
     from the very first frame. */
  var CAPTION = 'Per-app numbers are estimates: each app\'s share of processor and graphics time.';
  var SHARE_OF = {
    pc: 'Share of the whole PC\'s draw',
    chip: 'Share of the processor\'s power',
    cpu: 'Share of processor time (power not measurable here)'
  };
  var AC_TOTAL = 'Windows only reports total draw on battery';
  var NO_HISTORY = 'No history yet: Mantle records every 5 minutes';

  /* ------------------------------------------------------------------ the pure half */

  function num(x) { return typeof x === 'number' && isFinite(x); }
  function str(s) { return typeof s === 'string' && s.trim() ? s.trim() : ''; }
  function dec(v) { var r = Math.round(v * 10) / 10; return r >= 10 ? String(Math.round(v)) : r.toFixed(1); }

  /* 7800 -> "7.8 W", 18000 -> "18 W", 40 -> "<0.1 W" */
  function watts(mw) {
    if (!num(mw)) return '';
    var w = mw / 1000;
    if (w <= 0) return '0 W';
    if (w < 0.05) return '<0.1 W';
    return dec(w) + ' W';
  }
  /* an estimate: "~2.6 W" */
  function approxWatts(mw) { var s = watts(mw); return s ? '~' + s : ''; }
  /* 0.8 -> "0.8 Wh" (measured: no tilde); approxWh for the estimates */
  function wh(v) {
    if (!num(v)) return '';
    if (v <= 0) return '0 Wh';
    if (v < 0.05) return '<0.1 Wh';
    return dec(v) + ' Wh';
  }
  function approxWh(v) { var s = wh(v); return s ? '~' + s : ''; }
  /* 0.34 -> "34 %"; a sliver -> "<1 %" */
  function pct(share) {
    if (!num(share)) return '';
    if (share <= 0) return '0 %';
    if (share < 0.005) return '<1 %';
    return Math.round(Math.min(1, share) * 100) + ' %';
  }
  /* 13200 -> "about 3 h 40 min left" */
  function timeLeft(sec) {
    if (!num(sec) || sec <= 0) return '';
    var m = Math.max(1, Math.round(sec / 60));
    if (m < 60) return 'about ' + m + ' min left';
    var hr = Math.floor(m / 60), mm = m % 60;
    return 'about ' + hr + ' h' + (mm ? ' ' + mm + ' min' : '') + ' left';
  }
  function hourLabel(t) {
    var d = new Date(t);
    if (isNaN(d.getTime())) return '';
    var hh = d.getHours();
    return (hh < 10 ? '0' : '') + hh + ':00';
  }
  function share01(s) { return num(s) ? Math.max(0, Math.min(1, s)) : null; }

  /* A failed reply in words. off: the user switched the feature off in the tray. */
  function failed(r, what) {
    if (r && r.off) return { kind: 'off', title: 'Power use is switched off', why: str(r.reason) || 'Turn on "Power use and screen time" in Mantle\'s tray menu to see it.' };
    return { kind: 'error', title: 'Could not read ' + what, why: str(r && r.reason) || 'Mantle gave no reason.' };
  }

  /* The processor line: "Processor 4.1 W · Graphics 0.2 W", or the reason it cannot be measured. */
  function chipLine(chip) {
    chip = chip || {};
    if (str(chip.why)) return { text: str(chip.why), why: true };
    var parts = [];
    if (num(chip.pkgMw)) parts.push('Processor ' + watts(chip.pkgMw));
    if (num(chip.gfxMw)) parts.push('Graphics ' + watts(chip.gfxMw));
    if (!parts.length) return { text: 'The processor\'s power was not measured this time', why: true };
    return { text: parts.join(' · '), why: false };
  }

  function rowView(r, wattsOf) {
    var s = share01(r.share);
    return {
      key: String(r.key || r.name || ''),
      kind: r.kind || 'app',
      name: str(r.name) || (r.kind === 'app' ? 'An app' : 'Other'),
      path: str(r.path) || null,
      share: s,
      pct: s === null ? '' : pct(s),
      amount: s === null ? '' : wattsOf(r),
      why: s === null ? (str(r.why) || 'Not measured') : ''
    };
  }

  /* usage.power.now's reply -> what to draw. */
  function nowView(r) {
    if (!r || r.ok === false) return failed(r, 'power use');
    var src = r.source, bat = r.battery || {}, total = r.total || {};
    var v = { kind: 'ok', big: '', line: '', sub: '', chip: chipLine(r.chip), shareOf: SHARE_OF[r.shareOf] || '' };
    if (num(total.mw)) v.big = watts(total.mw);
    if (src === 'battery') {
      var left = timeLeft(bat.secondsLeft);
      v.line = 'On battery' + (left ? ' · ' + left : '');
      if (!v.big) v.sub = 'Total draw not available' + (str(total.why) ? ': ' + str(total.why) : '');
    } else if (src === 'ac') {
      if (bat.charging) v.line = num(bat.chargeMw) && bat.chargeMw > 0 ? 'Charging at ' + watts(bat.chargeMw) : 'Plugged in, charging';
      else v.line = 'Plugged in, not charging';
      if (!v.big) v.sub = AC_TOTAL;
    } else if (src === 'nobattery') {
      v.line = 'This PC has no battery';
      if (!v.big) v.sub = str(total.why);
    } else {
      v.line = 'Power source not known';
      v.sub = str(bat.why) || str(total.why);
    }
    v.rows = (Array.isArray(r.rows) ? r.rows : []).map(function (x) {
      return rowView(x, function (y) { return approxWatts(y.mw); });
    });
    v.caption = str(r.caption) || CAPTION;
    v.notes = (Array.isArray(r.notes) ? r.notes : []).map(str).filter(Boolean);
    return v;
  }

  /* usage.power.history's reply -> what to draw. */
  function historyView(r) {
    if (!r || r.ok === false) return failed(r, 'power history');
    var v = { kind: 'ok', caption: str(r.caption) || CAPTION, notes: (Array.isArray(r.notes) ? r.notes : []).map(str).filter(Boolean) };
    var hours = Array.isArray(r.hours) ? r.hours : [];
    if (r.empty || !hours.length) {
      v.kind = 'empty';
      v.title = 'No power history';
      v.why = str(r.emptyWhy) || NO_HISTORY;
      return v;
    }
    var max = 0, peak = null, onBat = 0, partial = 0;
    v.bars = hours.map(function (x) {
      var chipWh = num(x.chipWh) ? Math.max(0, x.chipWh) : 0;
      var totalWh = num(x.totalWh) && x.power !== 'ac' ? Math.max(0, x.totalWh) : null;
      var cov = num(x.covered) ? Math.max(0, Math.min(1, x.covered)) : 0;
      if (totalWh !== null) onBat++;
      if (cov < 1) partial++;
      if (chipWh > max) max = chipWh;
      if (totalWh !== null && totalWh > max) max = totalWh;
      if (!peak || chipWh > peak.chipWh) peak = { chipWh: chipWh, t: x.t };
      return { t: x.t, label: hourLabel(x.t), chipWh: chipWh, totalWh: totalWh, covered: cov, part: cov < 1, none: cov <= 0 };
    });
    v.max = max;
    v.bars.forEach(function (b) {
      b.chipH = max > 0 ? b.chipWh / max : 0;
      b.totalH = max > 0 && b.totalWh !== null ? b.totalWh / max : 0;
      b.tip = b.label + ': processor ' + wh(b.chipWh) + (b.totalWh !== null ? ', whole PC ' + wh(b.totalWh) : '') +
        (b.none ? ' (not recorded)' : b.part ? ' (partly recorded)' : '');
    });
    v.anyTotal = onBat > 0;
    v.anyPart = partial > 0;
    var first = v.bars[0], last = v.bars[v.bars.length - 1];
    var a = 'Power by hour, ' + first.label + ' to ' + last.label + '. ';
    a += peak && peak.chipWh > 0 ? 'The processor used the most at ' + hourLabel(peak.t) + ', ' + wh(peak.chipWh) + '.' : 'The processor used almost nothing.';
    if (onBat) a += ' Whole-PC draw shown for ' + onBat + (onBat === 1 ? ' hour' : ' hours') + ' on battery.';
    if (partial) a += ' ' + partial + (partial === 1 ? ' hour was' : ' hours were') + ' only partly recorded.';
    v.aria = a;
    var t = r.totals || {};
    v.totals = 'Processor ' + (wh(t.chipWh) || 'not measured') + (num(t.totalWh) ? ' · Whole PC ' + wh(t.totalWh) : '');
    v.totalsWhy = num(t.totalWh) ? '' : (str(t.totalWhy) || AC_TOTAL);
    v.shareOf = SHARE_OF[r.shareOf] || '';
    v.rows = (Array.isArray(r.rows) ? r.rows : []).map(function (x) {
      return rowView(x, function (y) { return approxWh(y.wh); });
    });
    return v;
  }

  /* ------------------------------------------------------------------ glyphs */

  var GAUGE = '<path d="M2.6 11.4a5.6 5.6 0 1 1 10.8 0" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/>' +
              '<path d="M8 9.6l2.6-3.4" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>' +
              '<circle cx="8" cy="9.8" r="1.1" fill="currentColor"/>';
  var CHEV_R = '<path d="M6.2 3.6L10.6 8l-4.4 4.4" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>';
  var CHEV_L = '<path d="M9.8 3.6L5.4 8l4.4 4.4" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/>';
  var APP = '<rect x="2.5" y="3" width="11" height="10" rx="2" fill="none" stroke="currentColor" stroke-width="1.2"/>' +
            '<path d="M2.5 6h11" fill="none" stroke="currentColor" stroke-width="1.2"/>';
  var WIN = '<rect x="2.5" y="2.5" width="5" height="5" rx=".8" fill="currentColor"/><rect x="8.5" y="2.5" width="5" height="5" rx=".8" fill="currentColor"/>' +
            '<rect x="2.5" y="8.5" width="5" height="5" rx=".8" fill="currentColor"/><rect x="8.5" y="8.5" width="5" height="5" rx=".8" fill="currentColor"/>';
  var CHIP = '<rect x="4" y="4" width="8" height="8" rx="1.2" fill="none" stroke="currentColor" stroke-width="1.2"/>' +
             '<path d="M6 2v2M10 2v2M6 12v2M10 12v2M2 6h2M2 10h2M12 6h2M12 10h2" fill="none" stroke="currentColor" stroke-width="1.1" stroke-linecap="round"/>';
  var SCREEN = '<rect x="2" y="3" width="12" height="8" rx="1.2" fill="none" stroke="currentColor" stroke-width="1.2"/>' +
               '<path d="M6 13.5h4M8 11v2.5" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>';
  var KIND_GLYPH = { windows: WIN, 'chip-rest': CHIP, 'pc-rest': SCREEN };

  /* ------------------------------------------------------------------ the DOM half */

  var C = null;                 // the panel's ctx from bar.js: call, on, svg, resize
  var S = null;                 // the open panel's state; null while it is closed
  var gen = 0;                  // bumps on every view or tab change: a late reply never paints a gone view
  var icons = {};               // path -> {url} | {why}: the page's life, never on disk
  var iconAsked = {};           // path -> true while a usage.icons read is out
  var lastCaption = '';

  function h(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null && text !== '') e.textContent = text;
    return e;
  }
  function button(cls, text, title) {
    var b = h('button', cls, text);
    b.type = 'button';
    if (title) { b.title = title; b.setAttribute('aria-label', title); }
    return b;
  }
  function svg(body, size) {
    if (C && typeof C.svg === 'function') return C.svg(body, size);
    var s = size || 16;
    return '<svg viewBox="0 0 16 16" width="' + s + '" height="' + s + '" aria-hidden="true" focusable="false">' + body + '</svg>';
  }
  function msg(e) { return (e && (e.message || e.reason)) || String(e || '') || 'no reason given'; }
  /* bar.js's call() always returns a promise; ok:false comes back as a value (its `off` matters here) */
  function ask(channel, payload) {
    try {
      var p = C.call(channel, payload);
      return (p && typeof p.then === 'function') ? p : Promise.resolve(p);
    } catch (e) { return Promise.reject(e); }
  }
  function resize() { if (C && typeof C.resize === 'function') { try { C.resize(); } catch (e) { /* bar.js says if the panel is clipped */ } } }

  function attach(pop, ctx, before) {
    closed();
    C = ctx || {};
    if (!C.call) return null;
    gen++;
    var me = S = { pop: pop, view: null, el: null, tab: 'now', timer: 0, body: null, notesEl: null, capEl: null, rowEls: {}, nowKind: '', notes: {}, rowOn: false };

    var row = button('qspw-open', null, null);
    row.hidden = true;                                   // until usage.state says on && power
    var ico = h('span', 'qspw-oico'); ico.innerHTML = svg(GAUGE);
    var txt = h('span', 'qspw-otext');
    txt.appendChild(h('span', 'qspw-olbl', 'Power use'));
    var sub = h('span', 'qspw-osub', 'What is using power');
    txt.appendChild(sub);
    var chev = h('span', 'qspw-ochev'); chev.innerHTML = svg(CHEV_R);
    row.appendChild(ico); row.appendChild(txt); row.appendChild(chev);
    row.addEventListener('click', function () { enter(); });
    if (before && before.parentNode === pop) pop.insertBefore(row, before); else pop.appendChild(row);
    me.row = row; me.sub = sub;

    ask('usage.state').then(function (st) {
      if (S !== me) return;
      if (st && st.ok !== false) {
        var show = !!(st.on && st.power);              // the user's switch: off = no row, by choice
        row.hidden = !show;
        row.classList.remove('bad');
        sub.textContent = 'What is using power';
      } else if (st && st.off) {
        row.hidden = true;                             // switched off in the tray: the user's choice too
      } else {
        showRowFail(st && st.reason);
      }
      resize();
    }, function (e) {
      if (S !== me) return;
      showRowFail(msg(e));
      resize();
    });
    function showRowFail(reason) {
      row.hidden = false;
      row.classList.add('bad');
      sub.textContent = 'Could not check if it is on: ' + (str(reason) || 'Mantle gave no reason');
      row.title = sub.textContent;
    }
    return { row: row };
  }

  function closed() {
    if (!S) return;
    gen++;
    stopPoll();
    var pop = S.pop;
    if (S.el && S.el.parentNode) S.el.parentNode.removeChild(S.el);
    if (S.view && pop) { pop.classList.remove('qsx-sub'); pop.style.width = ''; }
    S = null;
  }

  function enter() {
    if (!S || S.view) return;
    var pop = S.pop;
    if (pop.classList.contains('qsx-sub')) return;      // another sub-view (qs-more's) is open
    gen++;
    pop.style.width = getComputedStyle(pop).width;      // long names ellipsize; nothing jumps sideways
    pop.classList.add('qsx-sub');
    var el = h('div', 'qsx-view qspw-view');
    el.style.height = VIEW_H + 'px';
    pop.appendChild(el);
    S.view = true;
    S.el = el;

    var head = h('div', 'qsx-head');
    var b = button('qsx-icon qsx-back', null, 'Back to quick settings');
    b.innerHTML = svg(CHEV_L);
    b.addEventListener('click', back);
    head.appendChild(b);
    head.appendChild(h('span', 'qsx-title', 'Power use'));
    el.appendChild(head);

    var tabs = h('div', 'qspw-tabs');
    tabs.setAttribute('role', 'tablist');
    tabs.setAttribute('aria-label', 'Power use: when');
    S.tabEls = {};
    TABS.forEach(function (t) {
      var tb = button('qspw-tab', t.label);
      tb.setAttribute('role', 'tab');
      tb.dataset.tab = t.id;
      tb.id = 'qspw-tab-' + t.id;
      tb.setAttribute('aria-controls', 'qspw-panel');
      tb.addEventListener('click', function () { select(t.id); });
      tabs.appendChild(tb);
      S.tabEls[t.id] = tb;
    });
    /* arrows move the focus, Enter / Space (the button's own click) select */
    tabs.addEventListener('keydown', function (ev) {
      var ids = TABS.map(function (t) { return t.id; });
      var at = ids.indexOf(document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.tab : '');
      if (at < 0) return;
      var to = -1;
      if (ev.key === 'ArrowRight' || ev.key === 'ArrowDown') to = (at + 1) % ids.length;
      else if (ev.key === 'ArrowLeft' || ev.key === 'ArrowUp') to = (at + ids.length - 1) % ids.length;
      else if (ev.key === 'Home') to = 0;
      else if (ev.key === 'End') to = ids.length - 1;
      if (to < 0) return;
      ev.preventDefault(); ev.stopPropagation();
      ids.forEach(function (id, i) { S.tabEls[id].tabIndex = i === to ? 0 : -1; });
      S.tabEls[ids[to]].focus();
    });
    el.appendChild(tabs);

    var body = h('div', 'qsx-scroll qspw-body');
    body.id = 'qspw-panel';
    body.setAttribute('role', 'tabpanel');
    el.appendChild(body);
    S.body = body;
    var notes = h('div', 'qsx-notes qspw-notes');
    notes.hidden = true;
    el.appendChild(notes);
    S.notesEl = notes;
    S.capEl = h('div', 'qspw-cap', lastCaption || CAPTION);
    el.appendChild(S.capEl);

    select('now');
    resize();
  }

  function back() {
    if (!S || !S.view) return;
    gen++;
    stopPoll();
    if (S.el && S.el.parentNode) S.el.parentNode.removeChild(S.el);
    S.el = null; S.view = null; S.body = null; S.rowEls = {}; S.nowKind = '';
    S.pop.classList.remove('qsx-sub');
    S.pop.style.width = '';
    resize();
    if (S.row) S.row.focus();
  }

  function select(id) {
    if (!S || !S.view) return;
    gen++;
    stopPoll();
    S.tab = id;
    TABS.forEach(function (t) {
      var tb = S.tabEls[t.id], on = t.id === id;
      tb.classList.toggle('on', on);
      tb.setAttribute('aria-selected', on ? 'true' : 'false');
      tb.tabIndex = on ? 0 : -1;
    });
    S.body.setAttribute('aria-labelledby', 'qspw-tab-' + id);
    S.notes = {};
    S.rowEls = {};
    S.nowKind = '';
    stateBlock('Reading…', null, false, null, 'loading');
    paintNotes([]);
    if (id === 'now') readNow();
    else readHistory(id);
  }

  function alive(g) { return !!S && !!S.view && g === gen; }
  function stopPoll() { if (S && S.timer) { clearTimeout(S.timer); S.timer = 0; } }

  /* ---- Now: one read, then one more 2 s after each reply (never setInterval) */
  function readNow() {
    var g = gen;
    return ask('usage.power.now').then(function (r) {
      if (!alive(g)) return;
      paintNow(nowView(r), g);
    }, function (e) {
      if (!alive(g)) return;
      paintNow(failed({ reason: msg(e) }, 'power use'), g);
    });
  }
  function paintNow(v, g) {
    if (v.kind === 'ok') {
      delete S.notes.refresh;
      drawNow(v);
      setCaption(v.caption);
      paintNotes(v.notes);
    } else if (S.nowKind === 'ok' && v.kind === 'error') {
      /* a refresh failed under numbers already on screen: keep them, say so, keep trying */
      S.notes.refresh = 'The last refresh failed: ' + v.why;
      paintNotes(S.lastNotes || []);
    } else {
      S.rowEls = {};
      S.nowKind = v.kind;
      stateBlock(v.title, v.why, v.kind === 'error', v.kind === 'error' ? function () { select('now'); } : null, v.kind);
      return;                                            // an error or "off" stops the chain; Try again restarts it
    }
    S.timer = setTimeout(function () {
      if (!alive(g)) return;
      S.timer = 0;
      readNow();
    }, POLL_MS);
  }

  function drawNow(v) {
    var body = S.body;
    if (S.nowKind !== 'ok') {
      body.textContent = '';
      S.rowEls = {};
      var sum = h('div', 'qspw-sum');
      S.n = {
        big: sum.appendChild(h('div', 'qspw-big')),
        line: sum.appendChild(h('div', 'qspw-line')),
        sub: sum.appendChild(h('div', 'qspw-subline')),
        chip: sum.appendChild(h('div', 'qspw-chip'))
      };
      body.appendChild(sum);
      S.n.of = body.appendChild(h('div', 'qspw-of'));
      S.n.list = body.appendChild(h('div', 'qspw-list'));
      S.n.list.setAttribute('role', 'list');
      S.nowKind = 'ok';
    }
    var n = S.n;
    setText(n.big, v.big);
    setText(n.line, v.line);
    setText(n.sub, v.sub);
    setText(n.chip, v.chip.text);
    n.chip.classList.toggle('why', !!v.chip.why);
    setText(n.of, v.shareOf);
    drawRows(n.list, v.rows, S.rowEls);
  }

  function setText(el, t) { if (el.textContent !== (t || '')) el.textContent = t || ''; el.hidden = !t; }

  /* Rows keyed by `key`: a refresh moves and re-words the same elements (text-only updates). */
  function drawRows(list, rows, els) {
    var seen = {}, firstFixed = true;
    rows.forEach(function (r) {
      seen[r.key] = true;
      var e = els[r.key];
      if (!e) {
        e = els[r.key] = { el: h('div', 'qspw-row') };
        e.el.setAttribute('role', 'listitem');
        e.ico = e.el.appendChild(h('span', 'qspw-ico'));
        var main = e.el.appendChild(h('div', 'qspw-main'));
        var top = main.appendChild(h('div', 'qspw-top'));
        e.name = top.appendChild(h('span', 'qspw-name'));
        e.pct = top.appendChild(h('span', 'qspw-pct'));
        e.amt = top.appendChild(h('span', 'qspw-amt'));
        e.bar = main.appendChild(h('div', 'qspw-bar'));
        e.fill = e.bar.appendChild(h('div', 'qspw-fill'));
        e.why = main.appendChild(h('div', 'qspw-why'));
        e.iconFor = undefined;
      }
      e.el.dataset.kind = r.kind;
      e.el.dataset.key = r.key;
      e.path = r.path;
      var fixed = r.kind !== 'app';
      e.el.classList.toggle('fixed1', fixed && firstFixed);
      if (fixed) firstFixed = false;
      setText(e.name, r.name);
      e.name.title = r.name;
      setText(e.pct, r.pct);
      setText(e.amt, r.amount);
      setText(e.why, r.why);
      e.bar.hidden = r.share === null;
      e.fill.style.width = r.share === null ? '0' : (Math.round(r.share * 1000) / 10) + '%';
      paintIcon(e, r);
      list.appendChild(e.el);                          // appendChild moves: the order is the reply's
    });
    Object.keys(els).forEach(function (k) {
      if (!seen[k]) { if (els[k].el.parentNode) els[k].el.parentNode.removeChild(els[k].el); delete els[k]; }
    });
    wantIcons(rows);
  }

  function paintIcon(e, r) {
    var known = r.kind === 'app' && r.path ? icons[r.path] : null;
    var want = known && known.url ? known.url : null;
    var why = known && known.why ? known.why : '';
    var tag = want || 'glyph:' + r.kind + ':' + why;
    if (e.iconFor === tag) return;
    e.iconFor = tag;
    e.ico.textContent = '';
    e.ico.title = '';
    if (want) {
      var img = document.createElement('img');
      img.alt = '';
      img.onerror = function () { e.ico.textContent = ''; e.ico.innerHTML = svg(APP); e.ico.title = 'The icon did not load'; };
      if (window.MantlePic && typeof window.MantlePic.set === 'function') window.MantlePic.set(img, want);
      else img.src = want;
      e.ico.appendChild(img);
      return;
    }
    e.ico.innerHTML = svg(KIND_GLYPH[r.kind] || APP);
    if (why) e.ico.title = 'No icon: ' + why;
  }

  /* One usage.icons read for the paths not yet known; each path asked once per page. */
  function wantIcons(rows) {
    var paths = [];
    rows.forEach(function (r) {
      if (r.kind === 'app' && r.path && !icons[r.path] && !iconAsked[r.path] && paths.indexOf(r.path) < 0) paths.push(r.path);
    });
    if (!paths.length) return;
    paths.forEach(function (p) { iconAsked[p] = true; });
    ask('usage.icons', { paths: paths }).then(function (r) {
      var got = (r && r.icons) || {}, why = (r && r.why) || {};
      paths.forEach(function (p) {
        delete iconAsked[p];
        if (r && r.ok !== false && typeof got[p] === 'string' && got[p]) icons[p] = { url: got[p] };
        else icons[p] = { why: str(why[p]) || str(r && r.reason) || 'Windows gave no icon' };
      });
      repaintIcons();
    }, function (e) {
      paths.forEach(function (p) { delete iconAsked[p]; icons[p] = { why: msg(e) }; });
      repaintIcons();
    });
  }
  function repaintIcons() {
    if (!S || !S.view) return;
    Object.keys(S.rowEls).forEach(function (k) {
      var e = S.rowEls[k];
      paintIcon(e, { kind: e.el.dataset.kind, path: e.path });
    });
  }

  /* ---- Today / Last 24 h: one read per tab open */
  function readHistory(range) {
    var g = gen;
    ask('usage.power.history', { range: range }).then(function (r) {
      if (!alive(g)) return;
      drawHistory(historyView(r), range);
    }, function (e) {
      if (!alive(g)) return;
      drawHistory(failed({ reason: msg(e) }, 'power history'), range);
    });
  }

  function drawHistory(v, range) {
    if (v.kind === 'error' || v.kind === 'off') {
      stateBlock(v.title, v.why, v.kind === 'error', v.kind === 'error' ? function () { select(range); } : null, v.kind);
      return;
    }
    setCaption(v.caption);
    paintNotes(v.notes);
    if (v.kind === 'empty') { stateBlock(v.title, v.why, false, null, 'empty'); return; }
    var body = S.body;
    body.textContent = '';
    S.rowEls = {};
    var chart = h('div', 'qspw-chart');
    chart.setAttribute('role', 'img');
    chart.setAttribute('aria-label', v.aria);
    var cols = chart.appendChild(h('div', 'qspw-cols'));
    var axis = h('div', 'qspw-axis');
    v.bars.forEach(function (b, i) {
      var col = cols.appendChild(h('div', 'qspw-col' + (b.part ? ' part' : '') + (b.none ? ' none' : '')));
      col.title = b.tip;
      if (b.totalWh !== null) { var tb = col.appendChild(h('div', 'qspw-tot')); tb.style.height = pctH(b.totalH); }
      var cb = col.appendChild(h('div', 'qspw-chipbar')); cb.style.height = pctH(b.chipH);
      var lab = axis.appendChild(h('span', 'qspw-tick'));
      var d = new Date(b.t);
      if (i === 0 || i === v.bars.length - 1 || (!isNaN(d.getTime()) && d.getHours() % 6 === 0)) lab.textContent = b.label;
    });
    chart.appendChild(axis);
    body.appendChild(chart);
    var key = h('div', 'qspw-key');
    key.appendChild(h('span', 'qspw-sw chip')); key.appendChild(h('span', null, 'Processor'));
    if (v.anyTotal) { key.appendChild(h('span', 'qspw-sw tot')); key.appendChild(h('span', null, 'Whole PC (on battery)')); }
    if (v.anyPart) key.appendChild(h('span', 'qspw-keypart', 'Lighter: partly recorded'));
    body.appendChild(key);
    body.appendChild(h('div', 'qspw-totals', v.totals));
    if (v.totalsWhy) body.appendChild(h('div', 'qspw-subline', v.totalsWhy));
    if (v.shareOf) body.appendChild(h('div', 'qspw-of', v.shareOf));
    var list = body.appendChild(h('div', 'qspw-list'));
    list.setAttribute('role', 'list');
    drawRows(list, v.rows, S.rowEls);
  }
  function pctH(f) { return f > 0 ? Math.max(2, Math.round(f * 100)) + '%' : '0'; }

  /* a whole-tab state: loading, off, empty, or an error with Try again */
  function stateBlock(title, why, bad, retry, kind) {
    var body = S.body;
    body.textContent = '';
    var box = h('div', 'qsx-state qspw-state');
    box.dataset.state = kind || '';
    box.appendChild(h('div', 'qsx-statetitle', title));
    if (why) box.appendChild(h('div', 'qsx-statewhy' + (bad ? ' bad' : ''), why));
    if (retry) {
      var row = h('div', 'qsx-btns');
      var b = button('qsx-btn qspw-retry', 'Try again');
      b.addEventListener('click', function (ev) { ev.stopPropagation(); retry(); });
      row.appendChild(b);
      box.appendChild(row);
    }
    body.appendChild(box);
  }

  function setCaption(t) {
    lastCaption = t || lastCaption;
    if (S && S.capEl) S.capEl.textContent = lastCaption || CAPTION;
  }
  function paintNotes(list) {
    if (!S || !S.notesEl) return;
    S.lastNotes = list;
    var all = (list || []).slice();
    Object.keys(S.notes).forEach(function (k) { all.push(S.notes[k]); });
    S.notesEl.textContent = '';
    all.forEach(function (t) { S.notesEl.appendChild(h('div', 'note', t)); });
    S.notesEl.hidden = !all.length;
  }

  return {
    nowView: nowView, historyView: historyView,
    watts: watts, approxWatts: approxWatts, wh: wh, approxWh: approxWh, pct: pct, timeLeft: timeLeft, hourLabel: hourLabel,
    SHARE_OF: SHARE_OF, CAPTION: CAPTION, AC_TOTAL: AC_TOTAL, NO_HISTORY: NO_HISTORY, POLL_MS: POLL_MS,
    attach: attach, closed: closed,
    _debug: function () { return S ? { view: !!S.view, tab: S.tab, polling: !!S.timer } : null; }
  };
}));
