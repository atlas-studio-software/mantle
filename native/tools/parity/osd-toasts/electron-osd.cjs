'use strict';
/* electron-osd.cjs: Electron's volume pop-up page (ui/osd/index.html over its own mock bridge, ui/osd/mock-mantle.js,
 * the way ui/osd/shoot.js shoots it) in headless Chromium, one PNG per state and theme: the pane plus the 18 DIP
 * margin (--m) round it, over ONE solid colour, so run-osd-parity.ps1 can put the native window over the same colour
 * and diff the two. Nothing of the user's is read; nothing is written outside <out>.
 *
 *   node native/tools/parity/osd-toasts/electron-osd.cjs <out-dir> <bg #rrggbb> <name=query> ...
 *   e.g. vol-62=level=62  vol-muted=level=38&muted=1  vol-dead=dead=1  bright-70=kind=brightness&level=70
 * Prints one line per shot: ELECTRON-OSD <name> <theme> <w>x<h> <file>
 */
const path = require('path');
const fs = require('fs');
const REPO = path.resolve(__dirname, '..', '..', '..', '..');
const { chromium } = require(path.join(REPO, 'tools', 'dev-env.js')).playwright();
const PAGE = 'file:///' + path.join(REPO, 'ui', 'osd', 'index.html').replace(/\\/g, '/');
const [out, bg, ...specs] = process.argv.slice(2);
fs.mkdirSync(out, { recursive: true });

(async () => {
  const browser = await require(path.join(REPO, 'ui', 'settings', 'test-browser.js')).launch(chromium, { headless: true });
  try {
    for (const theme of ['light', 'dark']) {
      for (const spec of specs) {
        const eq = spec.indexOf('=');
        const name = spec.slice(0, eq), q = spec.slice(eq + 1);
        const ctx = await browser.newContext({ viewport: { width: 440, height: 560 }, deviceScaleFactor: 1 });
        const page = await ctx.newPage();
        await page.addInitScript((t) => { document.documentElement.setAttribute('data-theme', t); }, theme);
        await page.goto(PAGE + '?' + q);
        await page.waitForSelector('#osd:not([hidden])', { timeout: 5000 });
        await page.evaluate(([c, t]) => { document.documentElement.setAttribute('data-theme', t); document.documentElement.style.background = c; }, [bg, theme]);
        await page.waitForTimeout(300);
        const r = await page.evaluate(() => { const b = document.getElementById('osd').getBoundingClientRect(); return { x: b.x, y: b.y, w: b.width, h: b.height }; });
        const clip = { x: Math.floor(r.x - 18), y: Math.floor(r.y - 18), width: Math.ceil(r.w + 36), height: Math.ceil(r.h + 36) };
        const file = path.join(out, `electron-${name}-${theme}.png`);
        await page.screenshot({ path: file, clip });
        console.log(`ELECTRON-OSD ${name} ${theme} ${clip.width}x${clip.height} ${file}`);
        await ctx.close();
      }
    }
  } finally { await browser.close(); }
})().catch((e) => { console.error('ELECTRON-OSD failed: ' + (e && e.stack || e)); process.exit(1); });
