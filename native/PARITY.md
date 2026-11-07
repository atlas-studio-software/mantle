# Native Mantle: the parity contract

Baseline: Electron Mantle at origin/master **6ca499f** (2026-11-06). Later Electron changes enter through the
ledger (`docs/ledger.md`), never by silent edits.

This is the definition of "done" for the native (C# + WinUI 3) rebuild. A native surface replaces its Electron
counterpart only when **every row** for that surface is `verified` or carries a status the user approved
(see "Status vocabulary"). A function that is missing from this list is a feature that could be lost without
anyone noticing. If you find one, add a row (next free ID in that file) and log it in the ledger.

How to read it:

- `PARITY.md` (this file) holds the rules, the counts and the gaps that are still open.
- The rows themselves live in **one file per area** under `docs/parity/`. Each port lane owns its area's file,
  so parallel lanes never edit the same file.
- Each row has six columns: `ID | Area | Function | Electron source | Electron test | Native`.
  - **Function** is one user-visible (or user-relied-on) behaviour, written as a testable statement.
  - **Electron source** is where the behaviour lives today.
  - **Electron test** names the existing test that exercises it. `none` means no test was found;
    `live-only` means it can only be checked on a real desktop.
  - **Native** is the port status.

## Counts (6,948 rows)

| File | Rows | Prefixes |
|---|---:|---|
| [bar](docs/parity/bar.md) | 1045 | BAR 1045 (shell, Windows integration, pins and running apps, jump list, tray, pop-up framework, clock, Google Calendar, quick settings / network / Bluetooth / sound / battery, power mode, game mode, notification centre, downloads, media widget, Helm badge, quick links) |
| [settings-mantle](docs/parity/settings-mantle.md) | 208 | SETM 208 (Settings framework, kit controls, Mantle pages, feature switches and options, Personalization, Apps, Gaming, Accounts, Google Calendar controls, **Settings key index**) |
| [settings-windows](docs/parity/settings-windows.md) | 707 | SETW 707 (System 293, Network 86, Bluetooth & devices 98, Time & language 69, Privacy & security 67, Accessibility 62, Windows Update 23) |
| [shell](docs/parity/shell.md) | 1068 | TOP 382, WALL 320, LOOK 131, DESK 127, CORN 108 |
| [launch](docs/parity/launch.md) | 1264 | LAU 277, SNAP 170, SWI 145, TV 134, TST 134, OSD 115, UNF 111, CLIP 75, WEL 53, EMO 50 |
| [files-taskmgr](docs/parity/files-taskmgr.md) | 696 | TM 424, FILES 272 |
| [apps](docs/parity/apps.md) | 1445 | SHOT 254, PLAY 161, AUTO 132, NOTE 114, CLIPS 114, SPOT 113, REC 102, TIMER 81, APW 80, PROJ 62, LKA 59, MAXF 59, ONTOP 50, RAM 41, OCR 23 |
| [core](docs/parity/core.md) | 207 | CORE 40, SAFE 37, WIN 34, KEY 30, FEAT 23, TRAY 22, LOG 21 |
| [ship](docs/parity/ship.md) | 189 | UPD 58, DATA 49, PKG 36, INST 32, BOOT 14 |
| [sidecar](docs/parity/sidecar.md) | 119 | SC 119 (protocol, lanes, deadlines, hang handling, exit restore, hooks, guard and supervisor processes, elevation, build) |
| **Total** | **6948** | |

Test coverage today:

- 1,532 rows (22%) have **no Electron test**: bar 321, shell 282, apps 263, files-taskmgr 210, launch 182,
  settings-windows 146, settings-mantle 82, core 29, ship 14, sidecar 3.
- 46 rows are `live-only`.
- The native port must add a test for every one of these rows. The lack of an Electron test is not a licence
  to skip one natively.

Prefix legend:

| Prefix | Area |
|---|---|
| CLIP | Clipboard history (Win+V) |
| CLIPS | Game and instant clips |
| CORN | Hot corners |
| LKA | Look-away |
| MAXF | Maximise fold |
| TST | Mantle's own toasts and banners |
| UNF | The unfold animation and component |
| AUTO | Auto-pause |
| APW | App power |
| BOOT | Autostart |
| PKG | Packaged mode, README claims and perf budgets |

## Reference docs (inputs to the port, not rows)

| Doc | What it is |
|---|---|
| [docs/electron-architecture.md](docs/electron-architecture.md) | How today's app is built: processes, windows, startup order, flags and exit codes, the preload/IPC surface, main-to-sidecar protocol, status object, logging, crash/exit paths, game rest, test isolation, and which Electron facilities must be replaced |
| [docs/sidecar-map.md](docs/sidecar-map.md) | One row per sidecar `.cs` file (136 files): verbs, events, threads, API families, consumers, reuse class (pure 101, winrt 15, drawing 7, protocol 4, process 9). Also the wire protocol, dispatcher, the processes, hooks, the elevated helpers, build.ps1, and 14 threading hazards for in-process use |
| [docs/data-files.md](docs/data-files.md) | Every file, registry value, scheduled task and localStorage key Mantle reads or writes (88 files, 15 OS-state items, 19 localStorage keys), with a carry-over plan: read as-is, migrate once, or Electron-internal and needing an export |
| [docs/theme-tokens.md](docs/theme-tokens.md) | The one theme as data: every `--lg-*` token with light and dark values (73 light, 79 dark), the material stack, geometry, motion curves, component states, and the look.json mapping |
| [docs/ledger.md](docs/ledger.md) | The parity ledger: every Electron commit after the baseline, and what it changes here |

## Status vocabulary (the Native column)

| Status | Meaning | Who sets it |
|---|---|---|
| `todo` | Not started. Every row starts here | inventory |
| `wip:<lane>` | A lane has claimed it | the lane, at start |
| `ported:<sha>` | Code exists on `native/main` | the lane |
| `tested:<test id>` | A native test exercises exactly this row and passes. The test id is `<project>/<file>#<name>` | the lane |
| `verified:<sha>` | Tested, **and** checked side by side against Electron. The check is a functional run for behaviour rows, plus a screenshot pair (light and dark, 100% and 150% DPI) for visible rows. The coordinator reviewed it | the coordinator only |
| `stale@<master sha>` | Electron changed this behaviour after the row was ported. Re-port, re-test, re-verify | the ledger pass |
| `deviation:<ledger id>` | Native intentionally differs, for example where Electron had a bug the user would not want kept. The reason and the visible effect are in the ledger. If the user would notice it, it needs the user's OK | the coordinator, with the user's OK when visible |
| `webview2:<ledger id>` | Ported as a WebView2-hosted fallback because it cannot be done well natively. **Always flagged to the user**, never silent | the coordinator, with the user's OK |
| `dropped:<ledger id>` | Removed, with the user's explicit OK (date in the ledger) | the user |
| `live-only:<steps>` | Can only be proven on the user's desktop. The exact steps are listed for the user | the lane. The user runs the steps; the coordinator marks it `verified` after |

Rules:

- **One row, one test.** A screenshot is not a test. A behaviour row needs a functional test: a UI Automation
  click-through, a unit test of the ported logic, a platform-command test, or a dry-run write in a test profile.
- **Parity of intent, not of bugs.** Where the inventory notes an Electron defect, the native lane raises a
  `deviation` instead of copying the defect. Examples: the desktop status strip has drifted from
  `lib/state.js#problems()`, and Task Manager's `theme` setting is saved but never applied.
- **Text matches exactly** unless a `deviation` says otherwise. That covers labels, tooltips, empty, loading
  and error states, and plain-words reasons. The user rates Mantle against big-company polish, and wording is
  part of the product.
- **Each lane edits only the rows of its own area file.** The coordinator updates the counts in this file at
  merge time.
- **New rows** take the next free ID in their file and are never renumbered once a lane has referenced them.
  Every new row also gets a ledger entry.

## Definition of done (per surface, and for the switch-over)

A surface is done when:

1. Every row is `verified`, or is `deviation`, `webview2`, `dropped` or `live-only` with the user's OK and the
   steps run.
2. No row is `stale`.
3. Its memory and CPU budget is met (PLAN.md, "Test strategy").

The whole app is ready to switch when every surface is done, `docs/ledger.md` is caught up to the current
origin/master, and the switch-over gate in PLAN.md is passed.

## Inventory pending (known gaps in this baseline)

The inventory was read-only and done by lanes working from the source, so these areas were not fully
inventoried. Each pending row must be expanded into real rows **before the lane that owns its area starts
porting**. The owning lane does that as its first task.

| ID | Area | Function | Electron source | Electron test | Native |
|---|---|---|---|---|---|
| PEND-001 | Shared / Tooltips | inventory pending: Mantle-drawn tooltips. They replace Chromium's native `title` tooltips on every surface, use the island theme, and stay above the bar. This is ~826 lines; every behaviour still needs its own row | ui/shared/tips.js, ui/shared/tips.css | ui/shared/tips.test.js, ui/shared/tips-panel.test.js | todo |
| PEND-002 | Shared / Components | inventory pending: shared components without their own rows: `motion-in.js` (entry motion), `empty-state.css` (empty states), `page-stopped` (the page shown once a renderer has crashed past its restart cap), `clipped-shadow`, `switches`. `hud-backdrop`, `glass-adapt`, `wall-bdrop`, `bar-follow` and `unfold` are only partly covered by the surface rows | ui/shared/* | ui/shared/*.test.js | todo |
| PEND-003 | Sidecar / Verbs | inventory pending: a per-verb contract (arguments, result, errors, lane, deadline) for the ~345 feature verbs and 54 built-in verbs. Surface rows cover the behaviour and `sidecar/test-*.js` covers the verbs, so this is needed only before the in-process platform switch (PLAN.md phase 7) | sidecar/*.cs | sidecar/test-*.js | todo |
| PEND-004 | Settings / Strings | inventory pending: the `helmbadge` feature's label, default and options; the full text of every plain-words reason in Settings; the search keyword strings of the Windows pages; the sidecar's refusal and dry-run strings | ui/settings/**, lib/features/helmbadge.js, sidecar/Sys*.cs | ui/settings/**/*.test.js | todo |
| PEND-005 | Look / Contrast | inventory pending: the top bar's contrast rules (4.5:1 target, 4.7 guard) and the guard-band maths in `look-guard.js`, which must match `glass-adapt` exactly. They belong in docs/theme-tokens.md as data | ui/topbar/under.js, lib/look-guard.js, ui/shared/glass-adapt.js | lib/look-guard.test.js, ui/shared/glass-adapt.test.js | todo |
| PEND-006 | Bar / Quick links | inventory pending: the quick links (Win+X) menu is only partly covered in bar.md and launch.md. Every item, its order, and the keyboard behaviour each still need a row | lib/quick-links.js, lib/features/quicklinks.js | none | todo |
| PEND-007 | Files, Task Manager / Internals | inventory pending: Files sidecar internals (FilesOps, FilesShell, FilesThumbs: what each refuses and why) and the admin helper's data readers | sidecar/Files*.cs, sidecar/elevated/** | sidecar/test-files.js, sidecar/test-taskmgr-admin.js | todo |
| PEND-008 | Google Calendar / Privacy claims | inventory pending: re-check every calendar privacy row against the published privacy page itself, which lives in the site repo and not here. The page's claims bind the code, so the page is updated first if the native token storage changes its wording | bar.md (Google Calendar rows), the site repo's `mantle/privacy.html` | none | todo |
| PEND-009 | Data / Shapes | inventory pending: exact JSON shapes of the supervisor records, `keep-ready.json`, `switcher.json`, `gamemode-restore.json`, `taskmgr-history.json` and `player-library.json` | see docs/data-files.md "Not covered" | none | done: docs/data-files.md "Record shapes and exact write formats (PEND-009)" (native-data-look); F-010 and the inner F-020/F-027 counters still unread |
| PEND-010 | Tooling / Guarantees | inventory pending: the guarantees the test tooling enforces, which native tooling must keep: the no-overlap audit (hover and focus boxes never touch their neighbours), the perf budgets, run-suite and the check script | tools/overlap-audit.js, tools/perf-budget.js, tools/run-suite.js, tools/check.js | tools/perf-budget.test.js | todo |
| PEND-011 | Core / main.js internals | inventory pending: the bar, backdrop and hover internals of main.js, plus its pinned, quick-settings and media regions, were mapped by function name only in core.md. Cross-check them against bar.md before the bar lanes finish | main.js | lib/bar-hover-main.test.js, lib/main-*.test.js | todo |
| PEND-012 | All / Test column | inventory pending: the Electron test column was matched by file and grep, not by assertion. Each porting lane confirms the cited test really covers the row before it uses that test as the spec | all parity files | n/a | todo |
| PEND-013 | Task Manager / Startup apps | inventory pending: the Startup page test's mock disagrees with the real code about all-users startup entries. Decide which one is the contract (Windows' own behaviour wins, per the Task Manager spec) | ui/taskmgr/**, lib/features/_taskmgr-*.js | lib/features/taskmgr.test.js | todo |
| PEND-014 | Data / localStorage | inventory pending: confirm the localStorage key list L-001..L-019 is complete with a full renderer grep, since that data cannot be read natively without an export | ui/**/*.js | none | todo |
