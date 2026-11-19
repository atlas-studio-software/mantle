/* _usage-store.js - where "Power use and screen time" keeps its history (lane mantle-ps-node, 2026-11-18).
 * <userData>/usage/ only (every name below is path.join(dir, name)); nothing is ever sent anywhere.
 *   screentime.json            {v:1, apps:{key:{name, file, icon}}, days:{'YYYY-MM-DD':{apps:{key:seconds}, hours:[24]}}}
 *                              written whole, atomically (tmp + rename). key = the exe FILE name; never a
 *                              path, never a window title. A record with nothing in it is deleted, not written.
 *   power-YYYY-MM-DD.jsonl     one line per 5-minute bucket (the local date of its start); appended.
 *                              Per-app rows only in a line recorded with Screen time on (perApp:true);
 *                              with it off a line holds the totals only (General Counsel, 2026-11-18).
 * A damaged screentime.json is never deleted silently: it is kept as screentime.json.bad, the feature
 * starts it fresh, and the note says so in the replies (and once in mantle.log, by file name only).
 * A damaged power line is DELETED (it may name apps, so no .bad copy is kept) and the note says so.
 * Power files older than 3 local days, screen-time days older than 30 and a screentime*.bad copy last
 * written over 30 days ago are pruned at start-up, every 24 h and at each flush (usage.js).
 * Clear history deletes screentime.json and every screentime* copy at once (clearScreentime).
 * Clear power history, and Power use switched off, delete every power* file at once (clearPower).
 * Screen time off: stripPower() rewrites every power file with the totals only and deletes any power
 * .tmp / .bad copy (one of them could still name apps).
 * Migration: a power line written before the per-app rule (no perApp field) loses its app rows on the
 * first read (checkBucket) and the file is written back; migrate() runs that read at start-up.
 * A file written before 2026-11-18 (path keys) is re-keyed by exe name and written back on first read.
 */
'use strict';
const fs = require('fs');
const path = require('path');
const C = require('./_usage-core.js');

const POWER_RE = /^power-(\d{4}-\d{2}-\d{2})\.jsonl(\.bad|\.tmp)?$/;
const POWER_ANY = /^power/i;          // every power file and every copy of one (Clear power history)
const MAX_FILE = 8 * 1024 * 1024;   // a file over this is not one of ours

const words = (err) => (err && err.code ? err.code : err && err.message ? err.message : String(err));

function createStore(dir, o) {
  const log = (o && o.log) || (() => {});
  const notes = [];               // plain words for the replies, one per problem, kept for this run
  const note = (text) => { if (!notes.includes(text)) { notes.push(text); log(text); } };
  const unnote = (prefix) => { for (let i = notes.length - 1; i >= 0; i--) if (notes[i].startsWith(prefix)) notes.splice(i, 1); };   // a later pass did it
  let cache = null;               // the last 3 days of buckets, oldest first (loaded on first use)
  let blocked = null;             // screentime.json could not be read OR moved aside: never write over it

  const file = (name) => path.join(dir, name);
  const ensure = () => fs.mkdirSync(dir, { recursive: true });

  /** Keep a damaged file as <name>.bad (over an older .bad). -> words for the note. */
  function keepBad(name) {
    try { fs.renameSync(file(name), file(name + '.bad')); return `it was kept as ${name}.bad`; } catch (e) { return `it could not be moved aside (${words(e)})`; }
  }

  function writeAtomic(name, text) {
    ensure();
    const tmp = file(name + '.tmp');
    fs.writeFileSync(tmp, text);
    fs.renameSync(tmp, file(name));
  }

  /** -> the screen-time record ({v, apps, days}); a fresh one when missing or damaged. */
  function loadScreentime() {
    let text;
    try {
      const st = fs.statSync(file('screentime.json'));
      if (st.size > MAX_FILE) throw Object.assign(new Error('too big'), { tooBig: st.size });
      text = fs.readFileSync(file('screentime.json'), 'utf8');
    } catch (e) {
      if (e && e.code === 'ENOENT') return C.freshScreentime();
      const why = e && e.tooBig ? `is ${(e.tooBig / 1048576).toFixed(1)} MB, far more than screen time ever needs` : `could not be read (${words(e)})`;
      const kept = keepBad('screentime.json');
      if (!/^it was kept/.test(kept)) blocked = `screentime.json could not be read or moved aside, so nothing is written over it this run`;
      note(`Screen time history started fresh: screentime.json ${why}; ${kept}`);
      return C.freshScreentime();
    }
    let parsed, problem = null;
    try { parsed = JSON.parse(text); } catch (_) { problem = 'is damaged (not valid JSON)'; }
    if (!problem) {
      const r = C.parseScreentime(parsed);
      if (r.problem) problem = `is not usable (${r.problem})`;
      else {
        // a path or a title dropped on the way in: it leaves the disk now, not at the next save
        if (r.mended) { try { writeAtomic('screentime.json', JSON.stringify(r.data)); } catch (e) { note(`Screen time history could not be rewritten without program paths (${words(e)}); it is tried again at the next save`); } }
        return r.data;
      }
    }
    const kept = keepBad('screentime.json');
    if (!/^it was kept/.test(kept)) blocked = `screentime.json could not be moved aside, so nothing is written over it this run`;
    note(`Screen time history started fresh: screentime.json ${problem}; ${kept}`);
    return C.freshScreentime();
  }

  /** -> null, or the words of why it could not be saved (also a note). */
  function saveScreentime(data) {
    if (blocked) { const why = `Screen time is not saved: ${blocked}`; note(why); return why; }
    const empty = !data || (!Object.keys(data.days || {}).length && !Object.keys(data.apps || {}).length);
    try {
      if (empty) { for (const n of ['screentime.json', 'screentime.json.tmp']) fs.rmSync(file(n), { force: true }); return null; }
      writeAtomic('screentime.json', JSON.stringify(data));
      return null;
    } catch (e) {
      const why = `Screen time could not be saved (${words(e)}); it keeps counting and tries again in 5 minutes`;
      note(why);
      return why;
    }
  }

  /** Read one power file. Damaged lines are left out; the original is kept as .bad and the good lines written back. */
  function readPowerFile(name) {
    let text;
    try {
      const st = fs.statSync(file(name));
      if (st.size > MAX_FILE) { note(`Power history for ${name.slice(6, 16)} was not used: ${name} is too big; ${dropFile(name)}`); return []; }
      text = fs.readFileSync(file(name), 'utf8');
    } catch (e) {
      if (e && e.code !== 'ENOENT') note(`Power history for ${name.slice(6, 16)} could not be read (${words(e)})`);
      return [];
    }
    const good = [];
    let bad = 0;
    for (const line of text.split(/\r?\n/)) {
      if (!line.trim()) continue;
      let b = null;
      try { b = C.checkBucket(JSON.parse(line)); } catch (_) { b = null; }
      if (b) good.push(b); else bad++;
    }
    if (bad || good.some((b) => b.mended)) {
      // damaged lines go (no .bad copy: it could name apps); lines from before the per-app rule lose
      // their app rows; path keys become exe names: the file is written back so none of it stays on disk
      const why = rewritePower(name, good);
      if (bad) note(`Power history for ${name.slice(6, 16)}: ${bad} damaged line${bad === 1 ? ' was' : 's were'} deleted${why ? ` (${why})` : ''}`);
      else if (why) note(`Power history for ${name.slice(6, 16)} could not be rewritten without app names (${why}); it is tried again at the next start`);
    }
    return good;
  }

  /** Write a power file back from its buckets (an empty list deletes it). -> null or the words of why not. */
  function rewritePower(name, list) {
    try {
      if (!list.length) fs.rmSync(file(name), { force: true });
      else writeAtomic(name, list.map((b) => JSON.stringify(b) + '\n').join(''));
      return null;
    } catch (e) { try { fs.rmSync(file(name + '.tmp'), { force: true }); } catch (_) { /* tried at the next pass */ } return words(e); }
  }

  /** Delete a power file that is not usable (it could name apps, so it is not kept). -> words for the note. */
  function dropFile(name) {
    try { fs.rmSync(file(name), { force: true }); return 'it was deleted'; } catch (e) { return `it could not be deleted (${words(e)})`; }
  }

  /** The names of every power file and copy in the folder (empty when the folder is missing). */
  function powerNames() {
    try { return fs.readdirSync(dir).filter((n) => POWER_ANY.test(n)); } catch (e) { if (e && e.code === 'ENOENT') return []; throw e; }
  }

  /**
   * Screen time off (and the start-up migration): every power file keeps its totals only. Each
   * power-<date>.jsonl is rewritten without app rows, every power .tmp / .bad copy is deleted, and the
   * in-memory buckets are stripped too. -> null, or the words of what still names apps.
   */
  function stripPower() {
    let names;
    try { names = powerNames(); } catch (e) { const why = `Power history still names apps: its folder could not be read (${words(e)})`; note(why); return why; }
    const left = [];
    for (const n of names) {
      const m = POWER_RE.exec(n);
      if (!m || m[2]) { try { fs.rmSync(file(n), { recursive: true, force: true }); } catch (e) { left.push(`${n} (${words(e)})`); } continue; }
      const list = readPowerFile(n);           // lines from before the rule are stripped (and written) here
      if (!list.some((b) => b.perApp === true)) continue;
      for (const b of list) C.stripApps(b);
      const why = rewritePower(n, list);
      if (why) left.push(`${n} (${why})`);
    }
    if (cache) for (const b of cache) C.stripApps(b);
    if (left.length) { const why = `Power history still names apps: ${left.join(', ')}; it is tried again at the next change and every 24 hours`; note(why); return why; }
    unnote('Power history still names apps');
    return null;
  }

  /** Start-up: read every kept power file once, so a file from before the per-app rule is migrated now
   *  (its app rows dropped, its totals kept), and delete power .tmp / .bad copies left by an older run. */
  function migrate(now) {
    try { for (const n of powerNames()) if (/\.(bad|tmp)$/i.test(n)) fs.rmSync(file(n), { recursive: true, force: true }); } catch (e) { note(`Power history: old copies could not be deleted (${words(e)})`); }
    cache = loadBuckets(now);
    return cache.length;
  }

  /**
   * Clear power history / Power use switched off: every power* file (each day, its .tmp, any .bad or
   * other copy) is DELETED at once (fs.rm, never moved aside), and the in-memory buckets go.
   * -> null, or the words of what is still there.
   */
  function clearPower() {
    cache = [];
    let names;
    try { names = powerNames(); } catch (e) { const why = `Power history could not be cleared: its folder could not be read (${words(e)})`; note(why); return why; }
    const left = [];
    for (const n of names) { try { fs.rmSync(file(n), { recursive: true, force: true }); } catch (e) { left.push(`${n} (${words(e)})`); } }
    if (left.length) { const why = `Power history was not fully deleted: ${left.join(', ')} could not be removed; try again`; note(why); return why; }
    unnote('Power history could not be cleared'); unnote('Power history was not fully deleted');
    return null;
  }

  /** Is any power file (or copy) on disk? */
  function hasPower() { try { return powerNames().length > 0; } catch (_) { return true; } }

  function loadBuckets(now) {
    const out = [];
    for (let i = C.POWER_KEEP_DAYS - 1; i >= 0; i--) out.push(...readPowerFile(`power-${C.dateBefore(now, i)}.jsonl`));
    out.sort((a, b) => a.t - b.t);
    return out;
  }

  /** The stored buckets of the last 3 local days, oldest first. */
  function buckets(now) {
    if (!cache) cache = loadBuckets(now);
    return cache;
  }

  /** Add one bucket (to disk and to the cache). -> null or the words of why not. */
  function appendBucket(b, now) {
    if (!cache) cache = loadBuckets(now);
    cache.push(b);
    try {
      ensure();
      fs.appendFileSync(file(`power-${C.dateKey(b.t)}.jsonl`), JSON.stringify(b) + '\n');
      return null;
    } catch (e) {
      const why = `Power history could not be saved (${words(e)}); this session's readings are kept in memory only`;
      note(why);
      return why;
    }
  }

  /** Drop power files older than 3 local days (their .bad copies too), the cache's old buckets, and a
   *  kept damaged screen-time copy last written more than 30 days ago (it is history too). */
  function prune(now) {
    const keepFrom = C.dateBefore(now, C.POWER_KEEP_DAYS - 1);
    let gone = 0;
    let names = [];
    try { names = fs.readdirSync(dir); } catch (_) { names = []; }
    for (const n of names) {
      const m = POWER_RE.exec(n);
      if (m && m[1] < keepFrom) { try { fs.unlinkSync(file(n)); gone++; } catch (_) { /* tried again at the next pass */ } }
      if (/^screentime.*\.bad$/i.test(n)) {
        try { if (fs.statSync(file(n)).mtimeMs < now - C.KEEP_DAYS * 86400000) { fs.rmSync(file(n), { recursive: true, force: true }); gone++; } } catch (_) { /* the next pass */ }
      }
    }
    if (cache) { const from = C.dayStart(new Date(`${keepFrom}T12:00:00`).getTime()); cache = cache.filter((b) => b.t + b.dur * 1000 > from); }
    return gone;
  }

  /**
   * Clear history: screentime.json and every screentime* copy in the folder (its .tmp, a kept .bad, any
   * backup) are DELETED at once (fs.rm, never moved aside). -> null, or the words of what is still there.
   */
  function clearScreentime() {
    let names;
    try { names = fs.readdirSync(dir); } catch (e) {
      if (e && e.code === 'ENOENT') { blocked = null; return null; }
      const why = `Screen time history could not be cleared: its folder could not be read (${words(e)})`;
      log(why);
      return why;
    }
    const left = [];
    for (const n of names) {
      if (!/^screentime/i.test(n)) continue;
      try { fs.rmSync(file(n), { recursive: true, force: true }); } catch (e) { left.push(`${n} (${words(e)})`); }
    }
    if (left.length) {
      const why = `Screen time history was not fully deleted: ${left.join(', ')} could not be removed; try Clear history again`;
      log(why);
      return why;
    }
    blocked = null;          // the file that could not be read is gone with the rest
    return null;
  }

  /** Forget the in-memory buckets (tests; a file changed under us). */
  function reset() { cache = null; }

  return { dir, notes, loadScreentime, saveScreentime, clearScreentime, buckets, appendBucket, prune, reset, stripPower, clearPower, migrate, hasPower };
}

module.exports = { createStore, POWER_RE, POWER_ANY };
