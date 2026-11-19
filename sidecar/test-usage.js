// test-usage.js - usage.sample / usage.watch / usage.fg against a PASSIVE sidecar (lane
// mantle-ps-sidecar, 2026-11-18). Read-only: nothing in Windows changes.
//
//   node sidecar/test-usage.js [--exe=<path>]            the checks (about 15 s)
//   node sidecar/test-usage.js --measure [--watch-s=180]  + the costs: usage.sample ms / cpuMs over 20 calls,
//                                                         the watch thread's CPU after --watch-s seconds
//
// A PC without RAPL (most cloud runners) answers energy {ok:false, reason}: that is checked as a worded
// state, not failed. The idle check needs nobody touching the PC for a few seconds; when someone does,
// or an app holds the display on, it says so and checks the worded state instead.
// The ONE thing it does to Windows: a child PowerShell holds a display-on power request for 6 s (what a
// video player holds), released when that process ends.
'use strict';

const T = require('./test-lib');
const { spawn } = require('child_process');

/** A child PowerShell holding PowerSetRequest(PowerRequestDisplayRequired) for `sec` s. */
function holdDisplay(sec) {
  const ps = [
    "Add-Type -Namespace P -Name N -MemberDefinition '" +
      '[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct REASON { public uint Version; public uint Flags; public string Text; }' +
      '[DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr PowerCreateRequest(ref REASON r);' +
      '[DllImport("kernel32.dll", SetLastError = true)] public static extern bool PowerSetRequest(IntPtr h, int type);' + "'",
    '$r = New-Object P.N+REASON; $r.Version = 0; $r.Flags = 1; $r.Text = "Mantle usage test"',
    '$h = [P.N]::PowerCreateRequest([ref]$r)',
    'if ([P.N]::PowerSetRequest($h, 0)) { Write-Output "held" } else { Write-Output "nohold" }',
    `Start-Sleep -Seconds ${sec}`,
  ].join('; ');
  const enc = Buffer.from(ps, 'utf16le').toString('base64');
  const proc = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', enc], { stdio: ['ignore', 'pipe', 'pipe'] });
  let out = '';
  const ready = new Promise((resolve) => {
    const to = setTimeout(() => resolve('timeout'), 20000);
    proc.stdout.on('data', (d) => { out += d; if (/held|nohold/.test(out)) { clearTimeout(to); resolve(/nohold/.test(out) ? 'nohold' : 'held'); } });
    proc.on('exit', () => { clearTimeout(to); resolve(/held/.test(out) && !/nohold/.test(out) ? 'held' : 'exited'); });
  });
  const exited = new Promise((resolve) => proc.on('exit', resolve));
  return { proc, ready, exited };
}

const MEASURE = process.argv.includes('--measure');
const WATCH_S = Number((process.argv.find((a) => a.startsWith('--watch-s=')) || '--watch-s=180').split('=')[1]) || 180;
const PROC_FIELDS = ['pid', 'ppid', 'file', 'created', 'cpu', 'session', 'path', 'desc', 'windows'];
const FG_FIELDS = ['hwnd', 'pid', 'created', 'file', 'path', 'desc'];

/** Every key anywhere in `o` named `title` (or holding a window title by another common name). */
function titleKeys(o, at, out) {
  if (o && typeof o === 'object') {
    for (const k of Object.keys(o)) {
      if (/^(title|caption|windowText|text)$/i.test(k)) out.push(`${at}.${k}`);
      titleKeys(o[k], `${at}.${k}`, out);
    }
  }
  return out;
}

const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
const isStrOrNull = (v) => v === null || typeof v === 'string';
const median = (a) => { const s = a.slice().sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };

function checkFgShape(t, name, r) {
  t.check(`${name}: kind is fg or shell`, r && (r.kind === 'fg' || r.kind === 'shell'), JSON.stringify(r).slice(0, 200));
  if (r && r.kind === 'fg') {
    const missing = FG_FIELDS.filter((f) => !(f in r));
    t.check(`${name}: fg has ${FG_FIELDS.join(', ')}`, missing.length === 0, 'missing ' + missing.join(','));
    t.check(`${name}: hwnd is a decimal string`, typeof r.hwnd === 'string' && /^\d+$/.test(r.hwnd), String(r.hwnd));
    t.check(`${name}: pid is a number > 0`, isNum(r.pid) && r.pid > 0, String(r.pid));
    t.check(`${name}: created is a decimal string or null`, r.created === null || /^\d+$/.test(r.created), String(r.created));
    t.check(`${name}: file is a string`, typeof r.file === 'string' && r.file.length > 0, String(r.file));
    t.check(`${name}: path and desc are string or null`, isStrOrNull(r.path) && isStrOrNull(r.desc));
  }
}

(async () => {
  const t = T.checker('USAGE');
  const s = await T.startPassive();
  const replies = [];
  const call = async (cmd, extra, ms) => { const r = await s.call(cmd, extra, ms); replies.push(r); return r; };
  try {
    // ---------------------------------------------------------------- usage.sample
    const a = await call('usage.sample', {}, 15000);
    const now = Date.now();
    t.check('sample: t is UTC ms since 1970 (within 60 s of the test clock)', isNum(a.t) && Math.abs(a.t - now) < 60000, String(a.t));
    t.check('sample: cpus > 0', isNum(a.cpus) && a.cpus > 0, String(a.cpus));
    t.check('sample: idle (Idle-process CPU) is a number > 0', isNum(a.idle) && a.idle > 0, String(a.idle));
    t.check('sample: procs is a non-empty array', Array.isArray(a.procs) && a.procs.length > 10, String(a.procs && a.procs.length));
    const bad = (a.procs || []).filter((p) => PROC_FIELDS.some((f) => !(f in p)) || !isNum(p.pid) || !isNum(p.cpu) ||
      typeof p.created !== 'string' || typeof p.file !== 'string' || !isNum(p.windows) || !isStrOrNull(p.path) || !isStrOrNull(p.desc));
    t.check('sample: every proc has pid, ppid, file, created (string), cpu, session, path, desc, windows', bad.length === 0,
      bad.length + ' bad, first ' + JSON.stringify(bad[0] || null).slice(0, 200));
    t.check('sample: the Idle process is not a row', !(a.procs || []).some((p) => p.pid === 0));
    t.check('sample: selfPid is the sidecar', a.selfPid === s.ready.pid, `${a.selfPid} vs ${s.ready.pid}`);
    t.check('sample: parentPid is this test', a.parentPid === process.pid, `${a.parentPid} vs ${process.pid}`);
    t.check('sample: some row has a path and a desc', (a.procs || []).some((p) => p.path && p.desc));
    t.check('sample: some row has windows > 0', (a.procs || []).some((p) => p.windows > 0));
    t.check('sample: ms and cpuMs are numbers', isNum(a.ms) && isNum(a.cpuMs) && a.cpuMs >= 0, `${a.ms} ${a.cpuMs}`);
    t.check('sample: battery is Battery.Read() (ok + present)', a.battery && typeof a.battery.ok === 'boolean' && 'present' in a.battery);
    for (const k of ['energy', 'meter', 'gpu']) {
      const v = a[k];
      t.check(`sample: ${k} is {ok, reason}`, v && typeof v.ok === 'boolean' && 'reason' in v, JSON.stringify(v).slice(0, 200));
      if (v && !v.ok) t.check(`sample: ${k} ok:false carries a reason in words`, typeof v.reason === 'string' && v.reason.length > 10, String(v.reason));
    }
    if (a.meter && a.meter.ok) t.check('sample: meter.mw is a number >= 0', isNum(a.meter.mw) && a.meter.mw >= 0, String(a.meter.mw));
    if (a.gpu && a.gpu.ok) {
      const ks = Object.keys(a.gpu.byPid || {});
      t.check('sample: gpu.byPid keys are pids, values numbers >= 0',
        ks.every((k) => /^\d+$/.test(k) && isNum(a.gpu.byPid[k]) && a.gpu.byPid[k] >= 0), ks.slice(0, 5).join(','));
    }

    await T.sleep(2000);
    const b = await call('usage.sample', {}, 15000);
    t.check('sample 2: Idle-process CPU grew', b.idle > a.idle, `${a.idle} -> ${b.idle}`);
    if (a.energy && a.energy.ok && b.energy && b.energy.ok) {
      const dE = b.energy.pkg - a.energy.pkg, dMs = b.energy.time - a.energy.time;
      const w = dMs > 0 ? dE * 3.6e-9 / (dMs / 1000) : NaN;
      t.check('energy: pkg increased between two calls', dE > 0, `${a.energy.pkg} -> ${b.energy.pkg}`);
      t.check('energy: time (ms) increased by about the wait', dMs > 1000 && dMs < 10000, String(dMs));
      t.check('energy: pWh -> W is sane (1-60 W)', w >= 1 && w <= 60, `${w.toFixed(2)} W`);
      t.check('energy: cores and gfx are numbers (or null) no greater than pkg',
        ['cores', 'gfx'].every((k) => b.energy[k] === null || (isNum(b.energy[k]) && b.energy[k] <= b.energy.pkg)));
      console.log(`  energy: package ${w.toFixed(2)} W over ${dMs} ms`);
    } else {
      console.log(`  energy not readable here: ${a.energy && a.energy.reason}`);
    }
    if (a.gpu && a.gpu.ok && b.gpu && b.gpu.ok) {
      const common = Object.keys(a.gpu.byPid).filter((k) => k in b.gpu.byPid);
      const back = common.filter((k) => b.gpu.byPid[k] < a.gpu.byPid[k]);
      t.check('gpu: running time never goes back for a pid', common.length > 0 && back.length === 0, `${common.length} common, ${back.length} back`);
    } else {
      console.log(`  gpu not readable here: ${(a.gpu && a.gpu.reason) || (b.gpu && b.gpu.reason)}`);
    }

    const c = await call('usage.sample', { procs: false }, 15000);
    t.check('sample procs:false: procs null, idle still a number', c.procs === null && isNum(c.idle), `${c.procs} ${c.idle}`);
    t.check('sample procs:false: gpu not collected, worded', c.gpu && c.gpu.ok === false && /procs:false/.test(c.gpu.reason || ''));
    t.check('sample procs:false: energy still present', c.energy && typeof c.energy.ok === 'boolean');

    // ---------------------------------------------------------------- usage.fg
    const f = await call('usage.fg', {}, 5000);
    checkFgShape(t, 'usage.fg', f);
    t.check('usage.fg: idleMs is a number >= 0', isNum(f.idleMs) && f.idleMs >= 0, String(f.idleMs));
    t.check('usage.fg: displayHeld is a bool (or null with a reason)', typeof f.displayHeld === 'boolean' || (f.displayHeld === null && f.reason));

    // ---------------------------------------------------------------- usage.watch
    const st0 = await call('usage.watch', { op: 'status' }, 5000);
    t.check('watch status before on: not watching', st0.watching === false, JSON.stringify(st0));
    let r = await s.send('usage.watch', {}, 5000);
    t.check('watch {}: refused in words', r.ok === false && /on:true/.test(r.error || ''), JSON.stringify(r));
    r = await s.send('usage.watch', { on: true, idleMs: 10 }, 5000);
    t.check('watch idleMs:10: refused in words', r.ok === false && /idleMs/.test(r.error || ''), JSON.stringify(r));

    const evBefore = s.collect('usage').length;
    const on1 = await call('usage.watch', { on: true, idleMs: 600000 }, 6000);
    t.check('watch on: ok, watching', on1.ok === true && on1.watching === true, JSON.stringify(on1));
    const first = await s.waitEvent('usage', 3000, (e) => s.events.indexOf(e) >= 0 && (e.kind === 'fg' || e.kind === 'shell') &&
      s.collect('usage').indexOf(e) >= evBefore).catch(() => null);
    t.check('watch on: the first act is one fg/shell event', !!first, String(first && first.kind));
    if (first) {
      checkFgShape(t, 'first fg event', first);
      t.check('first fg event: t is UTC ms', isNum(first.t) && Math.abs(first.t - Date.now()) < 60000, String(first.t));
    }
    const st1 = await call('usage.watch', { op: 'status' }, 5000);
    t.check('watch status: watching, events >= 1, since, cpuMs, displayHeldReadable',
      st1.watching === true && st1.events >= 1 && isNum(st1.since) && isNum(st1.cpuMs) && typeof st1.displayHeldReadable === 'boolean',
      JSON.stringify(st1));
    t.check('watch status: idleMs is the one asked for', st1.idleMs === 600000, String(st1.idleMs));

    const on2 = await call('usage.watch', { on: true, idleMs: 300000 }, 8000);
    t.check('watch on again: restarts, still watching', on2.ok === true && on2.watching === true, JSON.stringify(on2));
    const st2 = await call('usage.watch', { op: 'status' }, 5000);
    t.check('watch on again: the new idleMs, a new since', st2.idleMs === 300000 && st2.since >= st1.since, JSON.stringify(st2));

    const off = await call('usage.watch', { on: false }, 6000);
    t.check('watch off: ok, not watching', off.ok === true && off.watching === false, JSON.stringify(off));
    const st3 = await call('usage.watch', { op: 'status' }, 5000);
    t.check('watch status after off: not watching', st3.watching === false, JSON.stringify(st3));
    const off2 = await call('usage.watch', { on: false }, 6000);
    t.check('watch off twice: still ok', off2.ok === true && off2.watching === false, JSON.stringify(off2));
    const n0 = s.collect('usage').length;
    await T.sleep(600);
    t.check('watch off: no more usage events', s.collect('usage').length === n0);

    // The idle threshold, with 1 s: needs nobody at the PC for ~2 s.
    const on3 = await call('usage.watch', { on: true, idleMs: 1000 }, 6000);
    t.check('watch on (idleMs 1000): watching', on3.ok === true && on3.watching === true, JSON.stringify(on3));
    const idleEv = await s.waitEvent('usage', 8000, (e) => e.kind === 'idle' && s.collect('usage').indexOf(e) >= n0).catch(() => null);
    const fNow = await call('usage.fg', {}, 5000);
    if (idleEv) {
      t.check('idle event: idleMs >= the threshold', isNum(idleEv.idleMs) && idleEv.idleMs >= 1000, String(idleEv.idleMs));
      t.check('idle event: t is UTC ms', isNum(idleEv.t) && Math.abs(idleEv.t - Date.now()) < 60000);
      const stI = await call('usage.watch', { op: 'status' }, 5000);
      t.check('watch status: idle true after the idle event (unless input came since)', stI.idle === true || fNow.idleMs < 8000, JSON.stringify(stI));
    } else {
      // No idle event: fine only if there was input or an app holds the display on.
      const explained = fNow.idleMs < 3000 || fNow.displayHeld === true;
      t.check('no idle event within 8 s is explained by input or a held display', explained,
        `idleMs ${fNow.idleMs}, displayHeld ${fNow.displayHeld}`);
      console.log(`  idle not seen: idleMs ${fNow.idleMs}, displayHeld ${fNow.displayHeld} (someone at the PC, or a video)`);
    }

    // ---------------------------------------------------------------- a held display (a video)
    await call('usage.watch', { on: false }, 6000);
    const hold = holdDisplay(6);
    const how = await hold.ready;
    t.check('display hold: the child PowerShell holds a display request', how === 'held', how);
    if (how === 'held') {
      const fh = await call('usage.fg', {}, 5000);
      t.check('usage.fg: displayHeld is true while another process holds the display on', fh.displayHeld === true, String(fh.displayHeld));
      const mark = s.collect('usage').length;
      const onH = await call('usage.watch', { on: true, idleMs: 1500 }, 6000);
      t.check('watch on (idleMs 1500) during the hold: watching', onH.ok === true && onH.watching === true, JSON.stringify(onH));
      await hold.exited;
      const during = s.collect('usage').slice(mark).filter((e) => e.kind === 'idle');
      const atRelease = await call('usage.fg', {}, 5000);
      const after = await s.waitEvent('usage', 7000, (e) => e.kind === 'idle' && s.collect('usage').indexOf(e) >= mark).catch(() => null);
      if (atRelease.idleMs > 8000) {
        t.check('display held: no idle event while an app holds the display on', during.length === 0, JSON.stringify(during[0] || null));
        t.check('display released: an idle event follows within 7 s', !!after);
        if (after) {
          console.log(`  display released: idle came with idleMs ${after.idleMs} (input idle at release ${atRelease.idleMs} ms)`);
          t.check('display released: idleMs counts from the release, not from the last input',
            after.idleMs >= 1500 && after.idleMs < 5000 && atRelease.idleMs > after.idleMs + 2000,
            `idleMs ${after.idleMs}, input idle at release ${atRelease.idleMs}`);
        }
      } else {
        console.log(`  display-hold idle checks skipped: someone used the PC (input idle ${atRelease.idleMs} ms at release)`);
      }
    } else {
      try { hold.proc.kill(); } catch (_) { /* gone */ }
    }

    // ---------------------------------------------------------------- the costs
    if (MEASURE) {
      const ms = [], cpu = [];
      for (let i = 0; i < 20; i++) {
        const x = await call('usage.sample', {}, 15000);
        ms.push(x.ms); cpu.push(x.cpuMs);
        await T.sleep(250);
      }
      const sum = (v) => v.reduce((p, q) => p + q, 0);
      console.log(`  MEASURE usage.sample x20: ms min ${Math.min(...ms).toFixed(1)} / median ${median(ms).toFixed(1)} / max ${Math.max(...ms).toFixed(1)}; ` +
        `cpuMs min ${Math.min(...cpu).toFixed(1)} / median ${median(cpu).toFixed(1)} / max ${Math.max(...cpu).toFixed(1)} / mean ${(sum(cpu) / 20).toFixed(2)}`);
      const pf = await call('usage.sample', { procs: false }, 15000);
      console.log(`  MEASURE usage.sample procs:false: ms ${pf.ms.toFixed(1)} cpuMs ${pf.cpuMs.toFixed(1)}`);
      await call('usage.watch', { on: true, idleMs: 300000 }, 6000);
      const w0 = await call('usage.watch', { op: 'status' }, 5000);
      console.log(`  MEASURE watch: running ${WATCH_S} s ...`);
      await T.sleep(WATCH_S * 1000);
      const w1 = await call('usage.watch', { op: 'status' }, 5000);
      const fg = s.collect('usage').filter((e) => e.t >= w0.since).length;
      console.log(`  MEASURE watch after ${((w1.since ? Date.now() - w1.since : 0) / 1000).toFixed(0)} s: cpuMs ${w1.cpuMs} events ${w1.events} ` +
        `(seen ${fg}) displayHeldReadable ${w1.displayHeldReadable} reason ${w1.reason}`);
      t.check('measure: the watch is still running', w1.watching === true);
    }

    await call('usage.watch', { on: false }, 6000);
    const titles = titleKeys({ replies, events: s.collect('usage') }, '', []);
    t.check('no reply or usage event carries a window title', titles.length === 0, titles.slice(0, 5).join(', '));
  } catch (e) {
    t.fail('the run', e && e.stack ? e.stack.split('\n').slice(0, 3).join(' | ') : String(e));
  } finally {
    await s.stop();
  }
  process.exit(t.summary());
})();
