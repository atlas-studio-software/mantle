/* _usage-store.js - where "Power use and screen time" keeps its history (lane mantle-ps-node, 2026-11-18).
 * <userData>/usage/ only (every name below is path.join(dir, name)); nothing is ever sent anywhere.
 *   screentime.json            {v:1, apps:{key:{name, file, icon}}, days:{'YYYY-MM-DD':{apps:{key:seconds}, hours:[24]}}}
 *                              written whole, atomically (tmp + rename). key = the exe FILE name; never a
 *                              path, never a window title. A record with nothing in it is deleted, not written.
 *   power-YYYY-MM-DD.jsonl     one line per 5-minute bucket (the local date of its start); appended.
 * A file that cannot be read is never deleted silently: it is kept as <name>.bad, the feature starts
 * that file fresh, and the note says so in the replies (and once in mantle.log, by file name only).
 * Power files older than 3 local days, screen-time days older than 30 and a screentime*.bad copy last
 * written over 30 days ago are pruned at start-up, every 24 h and at each flush (usage.js).
 * Clear history deletes screentime.json and every screentime* copy at once (clearScreentime).
 * A file written before 2026-11-18 (path keys) is re-keyed by exe name and written back on first read.
 */
'use strict';
const fs = require('fs');
const path = require('path');
const C = require('./_usage-core.js');

const POWER_RE = /^power-(\d{4}-\d{2}-\d{2})\.jsonl(\.bad)?$/;
const MAX_FILE = 8 * 1024 * 1024;   // a file over this is not one of ours

const words = (err) => (err && err.code ? err.code : err && err.message ? err.message : String(err));

function createStore(dir, o) {
  const log = (o && o.log) || (() => {});
  const notes = [];               // plain words for the replies, one per problem, kept for this run
  const note = (text) => { if (!notes.includes(text)) { notes.push(text); log(text); } };
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
      if (st.size > MAX_FILE) { note(`Power history for ${name.slice(6, 16)} was not used: ${name} is too big; ${keepBad(name)}`); return []; }
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
    if (bad) {
      const kept = keepBad(name);
      try { writeAtomic(name, good.map((b) => JSON.stringify(b) + '\n').join('')); } catch (_) { /* the next append starts it again */ }
      note(`Power history for ${name.slice(6, 16)}: ${bad} damaged line${bad === 1 ? ' was' : 's were'} left out; ${kept}`);
    } else if (good.some((b) => b.mended)) {
      // lines keyed by program path (before 2026-11-18): rewritten keyed by exe name, so no path stays on disk
      try { writeAtomic(name, good.map((b) => JSON.stringify(b) + '\n').join('')); } catch (e) { note(`Power history for ${name.slice(6, 16)} could not be rewritten without program paths (${words(e)})`); }
    }
    return good;
  }

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

  return { dir, notes, loadScreentime, saveScreentime, clearScreentime, buckets, appendBucket, prune, reset };
}

module.exports = { createStore, POWER_RE };
