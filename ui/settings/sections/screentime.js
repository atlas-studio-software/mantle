/* screentime.js — Mantle Settings > Screen time (lane mantle-ps-st, 2026-11-18).
 *
 * How long each app was in front: today (total, by hour, by app) and the last 7 days (a column per day;
 * pick a day for its apps). Only app names, icons and times are shown, never window titles: this file
 * never reads a `title` field from a reply. Local only, kept 30 days, a switch and "Clear history".
 * Main: lib/features/usage.js (usage.screentime.get / .set {on} / .clear, usage.icons {paths}); no main
 * half of its own. Never silent: loading, the feature off, recording off, empty and a failed read are
 * each worded on the page.
 *
 * The pure half (durations, day names, the chart and list models) is `M` below: node requires this file
 * for it (ui/settings/sections/screentime.test.js). With no MantleSettings (node) nothing is registered.
 */
(() => {
'use strict';

const WD = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const num = (v) => (typeof v === 'number' && isFinite(v) && v > 0 ? v : 0);

const M = {
  /** 0 -> "0 min", 40 -> "under a minute", 480 -> "8 min", 4320 -> "1 h 12 min", 7200 -> "2 h". */
  dur(sec) {
    const s = num(sec);
    if (!s) return '0 min';
    if (s < 60) return 'under a minute';
    const m = Math.round(s / 60);
    if (m < 60) return m + ' min';
    const h = Math.floor(m / 60), r = m % 60;
    return r ? `${h} h ${r} min` : `${h} h`;
  },
  /** 'YYYY-MM-DD' -> a local Date at midnight, or null. */
  date(s) {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(typeof s === 'string' ? s : '');
    if (!m) return null;
    const d = new Date(+m[1], +m[2] - 1, +m[3]);
    return d.getMonth() === +m[2] - 1 ? d : null;
  },
  /** Whole days from `date` to `today` (both 'YYYY-MM-DD'), or null. */
  ago(date, today) {
    const a = M.date(date), b = M.date(today);
    return a && b ? Math.round((b - a) / 864e5) : null;
  },
  /** "Today", "Yesterday", "Mon 16 Nov" (the full name of a day). */
  dayName(date, today) {
    const n = M.ago(date, today), d = M.date(date);
    if (n === 0) return 'Today';
    if (n === 1) return 'Yesterday';
    return d ? `${WD[d.getDay()]} ${d.getDate()} ${MON[d.getMonth()]}` : String(date || 'A day');
  },
  /** A column's label: "Today", else the weekday ("Mon"). */
  dayShort(date, today) {
    const d = M.date(date);
    return M.ago(date, today) === 0 ? 'Today' : d ? WD[d.getDay()] : '?';
  },
  /** 0 -> "12 am", 13 -> "1 pm". */
  hourName(h) {
    const x = ((h | 0) % 24 + 24) % 24;
    return `${x % 12 || 12} ${x < 12 ? 'am' : 'pm'}`;
  },
  /** An app list: [{key, name, path, seconds, text, share, pct}], most time first, nothing at 0 s.
      Reads only key, name, path and seconds of each app. */
  appRows(apps, totalSec) {
    const list = (Array.isArray(apps) ? apps : []).filter((a) => a && typeof a === 'object')
      .map((a) => ({ key: String(a.key == null ? '' : a.key), name: typeof a.name === 'string' && a.name.trim() ? a.name.trim() : 'An app',
        path: typeof a.path === 'string' && a.path ? a.path : null, seconds: num(a.seconds) }))
      .filter((a) => a.seconds > 0)
      .sort((a, b) => b.seconds - a.seconds || a.name.localeCompare(b.name));
    const sum = list.reduce((s, a) => s + a.seconds, 0);
    const total = Math.max(num(totalSec), sum);
    return list.map((a) => {
      const share = total ? a.seconds / total : 0;
      return Object.assign(a, { text: M.dur(a.seconds), share, pct: Math.round(share * 100) });
    });
  },
  /** The 7-day chart: oldest left. frac is of the biggest day. */
  week(days, today, selected) {
    const list = (Array.isArray(days) ? days : []).filter((d) => d && typeof d.date === 'string');
    const max = list.reduce((m, d) => Math.max(m, num(d.totalSec)), 0);
    const sel = list.some((d) => d.date === selected) ? selected : (list.some((d) => d.date === today) ? today : (list.length ? list[list.length - 1].date : null));
    const cols = list.map((d) => ({
      date: d.date, label: M.dayShort(d.date, today), full: M.dayName(d.date, today), totalSec: num(d.totalSec),
      text: M.dur(d.totalSec), frac: max ? num(d.totalSec) / max : 0, today: d.date === today, selected: d.date === sel,
    }));
    const aria = cols.length ? 'Screen time, last 7 days: ' + cols.map((c) => `${c.full} ${c.text}`).join('; ') : 'Screen time, last 7 days: nothing recorded';
    return { cols, selected: sel, aria };
  },
  /** Today by hour: 24 columns, frac of the busiest hour. */
  hours(hours) {
    const secs = Array.from({ length: 24 }, (_, i) => num(Array.isArray(hours) ? hours[i] : 0));
    const max = Math.max(0, ...secs);
    const top = max ? secs.indexOf(max) : -1;
    return {
      cols: secs.map((s, i) => ({ h: i, sec: s, frac: max ? s / max : 0, label: i % 6 === 0 ? M.hourName(i) : '' })),
      aria: top < 0 ? 'Today by hour: nothing yet' : `Today by hour: most time ${M.hourName(top)} to ${M.hourName(top + 1)} (${M.dur(max)})`,
    };
  },
  /** The quiet line under the switch: paused (and why) or the app being recorded now. */
  nowLine(r) {
    if (!r || r.ok !== true) return null;
    if (r.on === false || r.paused === 'off') return 'Nothing is being recorded. The history below stays until you clear it.';
    const idle = num(r.idleMin) || 5;
    switch (r.paused) {
      case 'locked': return 'Paused: the PC is locked';
      case 'asleep': return 'Paused: the PC is asleep';
      case 'idle': return `Paused: no input for ${idle} min`;
      default: break;
    }
    const c = r.current && typeof r.current === 'object' && typeof r.current.name === 'string' && r.current.name.trim() ? r.current.name.trim() : null;
    return c ? `Recording now: ${c}` : 'Recording';
  },
  /** The answer of usage.screentime.clear in words. */
  cleared(x) {
    if (!x || x.ok !== true) return null;
    if (x.dryRun) return x.reason || 'Test run: nothing was cleared';
    const n = x.cleared;
    if (typeof n !== 'number' || !isFinite(n)) return 'Cleared your screen time history';
    if (n <= 0) return 'There was no screen time to clear';
    return `Cleared ${n} ${n === 1 ? 'day' : 'days'} of screen time`;
  },
  privacy(idleMin, keptDays) {
    const idle = num(idleMin) || 5, kept = num(keptDays) || 30;
    return 'Recorded only on this PC and never sent anywhere. Only app names and times are kept, never window titles. '
      + `Kept for ${kept} days. Time while the PC is locked, asleep, or idle (no input for ${idle} min) doesn’t count.`;
  },
  /** What the page shows for a get answer: 'loading' | 'feature-off' | 'error' | 'empty' | 'ok'. */
  view(r) {
    if (r === undefined) return { kind: 'loading' };
    if (!r || typeof r !== 'object' || r.ok !== true) {
      const reason = (r && typeof r.reason === 'string' && r.reason) || 'no answer came back';
      return { kind: r && r.off ? 'feature-off' : 'error', reason };
    }
    const t = r.today && typeof r.today === 'object' ? r.today : {};
    const any = num(t.totalSec) > 0 || (Array.isArray(r.days) && r.days.some((d) => d && num(d.totalSec) > 0));
    return { kind: any ? 'ok' : 'empty', recording: r.on !== false && r.paused !== 'off' };
  },
};

if (typeof module === 'object' && module && module.exports) module.exports = M;
if (typeof MantleSettings === 'undefined') return;

const SEARCH = [
  { row: 'screentime.record', label: 'Record screen time', keywords: 'app time usage screen time foreground track record turn off on' },
  { row: 'screentime.today', label: 'Screen time today', keywords: 'app time usage today apps hours foreground' },
  { row: 'screentime.week', label: 'Screen time, last 7 days', keywords: 'app time usage week days history chart' },
  { row: 'screentime.clear', label: 'Clear screen time history', keywords: 'app time usage screen time foreground history delete erase clear' },
];

// app icons, cached for as long as this page is open: path -> { url|null, why|null }
const ICONS = new Map();
const ASKING = new Map();   // path -> the pending usage.icons promise

MantleSettings.section({
  id: 'screentime',
  title: 'Screen time',
  group: 'mantle',
  // an hourglass
  icon: '<path d="M4.5 2.5h7M4.5 13.5h7"/><path d="M5.5 2.5c0 3 5 3 5 5.5s-5 2.5-5 5.5M10.5 2.5c0 3-5 3-5 5.5s5 2.5 5 5.5"/>',
  order: 1007,
  help: 'How long you spend in each app, today and over the last week',
  keywords: 'app time usage screen time foreground history apps hours week',
  search: SEARCH,
  render(el, kit) {
    const h = kit.h;
    const css = (n, s) => { Object.assign(n.style, s); return n; };   // CSSOM: the page's CSP refuses style attributes
    const show = (n, on) => { n.style.display = on ? '' : 'none'; };
    const S = { r: undefined, sel: null, cleared: null, confirming: false, tiles: new Map() };

    /* ---- the switch, the quiet "now" line and the privacy line (always shown) */
    const sw = kit.toggle({
      row: 'screentime.record', label: 'Record screen time', help: 'How long each app is in front, per day', value: false, disabled: true,
      onChange: async (on) => {
        const x = await kit.call('usage.screentime.set', { on });
        setTimeout(update, 0);
        // the read-back decides; a refusal (ok:false) or a dry run springs back with its words
        return x && x.ok === true && !x.dryRun && 'on' in x ? { ok: true, value: !!x.on } : x;
      },
    });
    const privacyBox = h('div', { 'data-row': 'screentime.privacy' });

    /* ---- the state lines: loading, the feature off, a failed read, notes */
    const stateBox = h('div', { class: 'st-state' });
    const retry = kit.button({ row: 'screentime.retry', label: 'Screen time could not be read', text: 'Try again', onClick: () => update() });
    const retryCard = kit.card(retry);

    /* ---- today */
    const todayTotal = kit.status({ row: 'screentime.today', label: 'Today', value: '…' });
    const hourChart = css(h('div', { class: 'st-hours', role: 'img' }), { padding: '4px 16px 12px' });
    const todayApps = h('div', { class: 'klist st-apps', 'data-list': 'today' });
    const todayCard = kit.card(todayTotal, hourChart, todayApps);

    /* ---- the last 7 days */
    const weekChart = css(h('div', { class: 'st-week', role: 'group', 'data-row': 'screentime.week' }),
      { display: 'flex', gap: '6px', padding: '12px 16px 8px', alignItems: 'stretch' });
    const dayHead = kit.status({ row: 'screentime.day', label: 'Today', value: '' });
    const dayApps = h('div', { class: 'klist st-apps', 'data-list': 'day' });
    const weekCard = kit.card(weekChart, dayHead, dayApps);

    const emptyCard = kit.card(h('div', { class: 'row k-empty', 'data-row': 'screentime.empty' },
      h('div', { class: 'row-l' }, h('div', { class: 'row-h' }, 'No screen time yet: it builds up as you use apps'))));

    /* ---- clear history, with its confirm step in the page */
    const clear = kit.button({
      row: 'screentime.clear', label: 'Clear history', help: 'Deletes every day of screen time kept on this PC', text: 'Clear history…', disabled: true,
      onClick: () => { S.confirming = true; paintConfirm(); },
    });
    const yes = h('button', { class: 'btn acc', type: 'button' }, 'Clear history');
    const no = h('button', { class: 'btn quiet', type: 'button' }, 'Keep it');
    const confirm = h('div', { class: 'row k-confirm', 'data-row': 'screentime.clear.confirm', role: 'alertdialog', 'aria-label': 'Clear all screen time?' },
      h('div', { class: 'row-l' }, h('div', { class: 'row-t' }, 'Clear all screen time?'),
        h('div', { class: 'row-h' }, 'Every day kept on this PC is deleted. This can’t be undone.')),
      css(h('div', { class: 'row-c' }, no, yes), { gap: '8px' }));
    no.addEventListener('click', () => { S.confirming = false; paintConfirm(); });
    yes.addEventListener('click', async () => {
      yes.disabled = true; no.disabled = true; yes.textContent = 'Clearing…';
      const x = await kit.call('usage.screentime.clear', {});
      if (kit.disposed) return;
      S.confirming = false; yes.textContent = 'Clear history'; yes.disabled = false; no.disabled = false;
      if (!x || x.ok !== true) { S.cleared = null; clear.set({ reason: 'Nothing was cleared: ' + ((x && x.reason) || 'no answer came back'), info: null }); }
      else { S.cleared = M.cleared(x); clear.set({ reason: null }); }
      paintConfirm();
      await update();
    });
    const clearCard = kit.card(clear);   // the question joins it only while asked (so the card's last row keeps its corners)

    const content = h('div', { class: 'st-content' },
      kit.group('Today'), todayCard, kit.group('Last 7 days'), weekCard);
    el.append(kit.card(sw, privacyBox), stateBox, retryCard, emptyCard, content, kit.group('History'), clearCard);
    show(retryCard, false); show(emptyCard, false); show(content, false);

    function paintConfirm() {
      if (S.confirming) clearCard.append(confirm); else confirm.remove();
      if (S.confirming) { clear.set({ disabled: true, reason: null, info: null }); setTimeout(() => { if (!kit.disposed) no.focus(); }, 0); }
      else paint();
    }

    /* ---- icons: usage.icons {paths}, once per path for as long as the page is open */
    function glyph(tile, path) {
      const c = path && ICONS.get(path);
      if (c && c.url) {
        const img = h('img', { alt: '', draggable: 'false' });
        img.addEventListener('error', () => { ICONS.set(path, { url: null, why: 'the icon could not be shown' }); glyph(tile, path); });
        img.src = c.url;
        tile.replaceChildren(img);
        tile.removeAttribute('title');
      } else {
        tile.replaceChildren(kit.icon('apps'));
        if (c && c.why) tile.setAttribute('title', 'No icon: ' + c.why); else tile.removeAttribute('title');
      }
    }
    function askIcons(paths) {
      // a failed call (the helper starting, say) is asked again the next time a list is drawn; an answer is kept
      const want = [...new Set(paths)].filter((p) => p && !(ICONS.has(p) && !ICONS.get(p).again) && !ASKING.has(p));
      if (want.length) {
        const ask = kit.call('usage.icons', { paths: want }).then((x) => {
          for (const p of want) {
            const url = x && x.ok === true && x.icons && typeof x.icons[p] === 'string' && /^data:image\//.test(x.icons[p]) ? x.icons[p] : null;
            const why = url ? null : (x && x.ok !== true ? x.reason : x && x.why && x.why[p]) || 'Windows gave no icon for this app';
            ICONS.set(p, { url, why, again: !(x && x.ok === true) });
            ASKING.delete(p);
          }
        });
        want.forEach((p) => ASKING.set(p, ask));
      }
      const waits = [...new Set(paths.map((p) => ASKING.get(p)).filter(Boolean))];
      if (waits.length) Promise.all(waits).then(() => { if (!kit.disposed) for (const [tile, p] of S.tiles) if (tile.isConnected) glyph(tile, p); });
    }

    /* ---- an app list: icon, name, a static share bar, the time */
    function drawApps(box, rows, empty, of) {
      const sig = JSON.stringify([rows.map((a) => [a.key, a.name, a.path, a.seconds]), empty, of]);
      if (box.dataset.sig === sig) return;
      box.dataset.sig = sig;
      for (const t of [...S.tiles.keys()]) if (box.contains(t)) S.tiles.delete(t);
      if (!rows.length) {
        box.replaceChildren(h('div', { class: 'row k-empty' }, h('div', { class: 'row-l' }, h('div', { class: 'row-h' }, empty))));
        return;
      }
      box.replaceChildren(...rows.map((a) => {
        const tile = h('div', { class: 'tile' });
        S.tiles.set(tile, a.path);
        glyph(tile, a.path);
        const fill = css(h('div'), { height: '100%', width: Math.max(1, a.share * 100).toFixed(1) + '%', background: 'var(--lg-acc-line)', borderRadius: '2px' });
        const bar = css(h('div', { class: 'st-share', role: 'img', 'aria-label': `${a.pct}% of ${of}` }, fill),
          { height: '4px', marginTop: '6px', borderRadius: '2px', background: 'var(--lg-sunk)', overflow: 'hidden', maxWidth: '360px' });
        return h('div', { class: 'row k-item st-app' }, tile,
          h('div', { class: 'row-l' }, h('div', { class: 'row-t' }, a.name), bar),
          h('div', { class: 'row-c' }, h('span', { class: 'val' }, a.text)));
      }));
      askIcons(rows.map((a) => a.path).filter(Boolean));
    }

    /* ---- the charts (text and heights only; nothing moves) */
    function drawHours(t) {
      const m = M.hours(t.hours);
      hourChart.setAttribute('aria-label', m.aria);
      show(hourChart, m.cols.some((c) => c.sec > 0));   // no by-hour numbers (yet): no flat chart
      const sig = JSON.stringify(m.cols.map((c) => c.sec));
      if (hourChart.dataset.sig === sig) return;
      hourChart.dataset.sig = sig;
      const bars = css(h('div'), { display: 'flex', alignItems: 'flex-end', gap: '2px', height: '40px' });
      const labels = css(h('div'), { display: 'flex', gap: '2px', marginTop: '4px' });
      for (const c of m.cols) {
        bars.append(css(h('div', { 'data-h': c.h }), { flex: '1', height: c.sec ? Math.max(4, c.frac * 100).toFixed(1) + '%' : '2px',
          background: c.sec ? 'var(--lg-acc-line)' : 'var(--lg-sunk)', borderRadius: '2px' }));
        labels.append(css(h('div', null, c.label), { flex: '1', fontSize: '11px', color: 'var(--lg-ink-3)', whiteSpace: 'nowrap', overflow: 'visible' }));
      }
      hourChart.replaceChildren(bars, labels);
    }
    function drawWeek(r) {
      const m = M.week(r.days, r.today && r.today.date, S.sel);
      S.sel = m.selected;
      weekChart.setAttribute('aria-label', m.aria);
      const sig = JSON.stringify(m.cols);
      if (weekChart.dataset.sig !== sig) {
        weekChart.dataset.sig = sig;
        const had = weekChart.contains(document.activeElement) ? document.activeElement.dataset.date : null;
        weekChart.replaceChildren(...m.cols.map((c) => {
          const bar = css(h('div'), { width: '100%', height: c.totalSec ? Math.max(3, c.frac * 100).toFixed(1) + '%' : '0',
            background: c.today || c.selected ? 'var(--lg-acc)' : 'var(--lg-acc-line)', borderRadius: '4px' });
          const well = css(h('div', null, bar), { width: '100%', maxWidth: '34px', height: '80px', display: 'flex', alignItems: 'flex-end', background: 'var(--lg-sunk)', borderRadius: '4px' });
          const b = css(h('button', { type: 'button', class: 'st-day', 'data-date': c.date, 'aria-pressed': c.selected ? 'true' : 'false',
            'aria-label': `${c.full}: ${c.text}`, tabindex: c.selected ? '0' : '-1' },
          well,
          css(h('div', { class: 'st-day-l' }, c.label), { marginTop: '6px', fontSize: '12px', fontWeight: c.selected ? '600' : '400', color: c.selected ? 'var(--lg-ink)' : 'var(--lg-ink-3)',
            borderBottom: '2px solid ' + (c.selected ? 'var(--lg-acc)' : 'transparent'), paddingBottom: '1px' }),
          css(h('div', { class: 'st-day-v' }, c.text), { fontSize: '11px', color: 'var(--lg-ink-3)', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' })),
          { flex: '1', minWidth: '0', display: 'flex', flexDirection: 'column', alignItems: 'center', padding: '4px 2px', border: '0',
            background: 'transparent', color: 'inherit', font: 'inherit', cursor: 'pointer', borderRadius: 'var(--lg-r-ctl)' });
          b.addEventListener('click', () => pick(c.date, false));
          return b;
        }));
        if (had) { const f = weekChart.querySelector(`[data-date="${had}"]`); if (f) f.focus(); }
      }
      const day = (Array.isArray(r.days) ? r.days : []).find((d) => d && d.date === S.sel) || null;
      const isToday = !!(day && r.today && day.date === r.today.date);
      dayHead.set({ label: day ? M.dayName(day.date, r.today && r.today.date) : 'No day picked', value: day ? M.dur(day.totalSec) : '' });
      drawApps(dayApps, day ? M.appRows(isToday ? r.today.apps : day.apps, day.totalSec) : [], day ? 'Nothing recorded that day' : 'Pick a day above', isToday ? 'today' : 'that day');
    }
    function pick(date, focus) {
      S.sel = date;
      if (S.r && S.r.ok === true) drawWeek(S.r);
      if (focus) { const f = weekChart.querySelector(`[data-date="${date}"]`); if (f) f.focus(); }
    }
    weekChart.addEventListener('keydown', (e) => {
      const dates = [...weekChart.querySelectorAll('[data-date]')].map((b) => b.dataset.date);
      let i = dates.indexOf(S.sel);
      if (e.key === 'ArrowLeft') i = Math.max(0, i - 1);
      else if (e.key === 'ArrowRight') i = Math.min(dates.length - 1, i + 1);
      else if (e.key === 'Home') i = 0;
      else if (e.key === 'End') i = dates.length - 1;
      else return;
      e.preventDefault();
      if (dates[i]) pick(dates[i], true);
    });

    /* ---- one paint of the last answer */
    function paint() {
      const r = S.r, v = M.view(r);
      const pv = M.privacy(r && r.idleMin, r && r.keptDays);
      if (privacyBox.dataset.text !== pv) { privacyBox.dataset.text = pv; privacyBox.replaceChildren(kit.reason(pv, { info: true })); }
      const lines = [];
      if (v.kind === 'loading') lines.push(kit.reason('Reading your screen time…', { info: true }));
      if (v.kind === 'feature-off') {
        lines.push(kit.reason(v.reason));
        lines.push(kit.reason('Its switch is in Mantle’s tray menu > Features (Power use and screen time).', { info: true }));
      }
      if (r && r.ok === true && Array.isArray(r.notes)) r.notes.filter((n) => typeof n === 'string' && n).forEach((n) => lines.push(kit.reason(n, { info: true })));
      stateBox.replaceChildren(...(lines.length ? [kit.card(...lines)] : []));
      show(retryCard, v.kind === 'error');
      if (v.kind === 'error') retry.set({ reason: v.reason });
      show(emptyCard, v.kind === 'empty');
      show(content, v.kind === 'ok');
      const live = v.kind === 'ok' || v.kind === 'empty';
      sw.set(live ? { value: !!(r.on !== false), disabled: false, info: M.nowLine(r) } : { disabled: true, info: null });
      if (!S.confirming) clear.set({ disabled: v.kind !== 'ok', info: S.cleared || (v.kind === 'empty' ? 'Nothing is kept yet' : null) });
      if (v.kind !== 'ok') return;
      const t = r.today && typeof r.today === 'object' ? r.today : {};
      todayTotal.set({ value: M.dur(t.totalSec) });
      drawHours(t);
      drawApps(todayApps, M.appRows(t.apps, t.totalSec), 'Nothing recorded yet today', 'today');
      drawWeek(r);
    }

    let reading = null;
    function update() {
      if (reading) return reading;
      reading = kit.call('usage.screentime.get', {}).then((r) => {
        reading = null;
        if (kit.disposed) return;
        S.r = r;
        paint();
      });
      return reading;
    }
    paint();
    update();
    kit.every(60000, update);   // the kit skips it while the window is hidden and stops it when the page is left
    return { update, dispose() { S.tiles.clear(); } };
  },
});
})();
