# Parity ledger

This file records every Electron Mantle commit after the parity baseline, and what each one changes in the
parity contract (`../PARITY.md`, rows in `parity/*.md`). It also holds every intentional difference from
Electron: deviations, WebView2 fallbacks and dropped functions.

- Baseline: **6ca499f** (origin/master, 2026-11-06).
- **Last processed master commit: 6ca499f.**

## Why

Other sessions keep shipping Electron Mantle during the port. A behaviour that changes in Electron after
native has ported it would otherwise be lost silently. The ledger makes every such change either a row edit
or an explicit "no parity effect".

## Procedure (the coordinator runs it at the start of every round, before lanes launch)

1. List the new commits:

   ```
   git fetch origin
   git log --reverse --format='%h %ad %s' --date=short --name-only <last processed>..origin/master
   ```

2. Classify each commit. Use the path map below to find the parity file it touches.

   | Effect | Meaning | What to do |
   |---|---|---|
   | `none` | Docs, tests only, CI, or no behaviour change | — |
   | `rows+` | New behaviour | Add rows with the next free IDs, status `todo` |
   | `rows~` | Changed behaviour | Edit the row text. A row already past `todo` becomes `stale@<sha>` |
   | `rows-` | Removed behaviour | Mark the row `dropped:<ledger id>`, with "removed in Electron" as the reason |
   | `sidecar` | The sidecar sources changed | Native shares them through the merge. Re-run the platform tests next round |
   | `data` | A file or key was added or changed | Update `data-files.md` and the DATA rows |

3. Merge origin/master into `native/main`. This is conflict-free by construction, because native lanes never
   edit files outside `native/`.
4. Add one ledger row per commit below, then move "Last processed" to the newest commit.

Path map, Electron path to parity file:

| Electron path | Parity file |
|---|---|
| `ui/bar/**` (except `corners.*`) | `parity/bar.md` |
| `ui/bar/corners.*` | `parity/shell.md` (CORN) |
| `ui/topbar/**`, `ui/desktop/**`, `ui/wallpapers/**` | `parity/shell.md` |
| `ui/shared/**` | `parity/shell.md` (LOOK), or PARITY.md PEND-001/002 until those are expanded |
| `ui/{launcher,switcher,taskview,snap,layouts,unfold,osd,toasts,clipboard,emoji,welcome}/**` | `parity/launch.md` |
| `ui/{files,taskmgr}/**` | `parity/files-taskmgr.md` |
| `ui/{screenshot,screenrec,clips,notes,timer,player,project,lookaway,ontop}/**` | `parity/apps.md` |
| `ui/settings/**`: framework, Personalization, Apps, App power, RAM, Gaming, Accounts | `parity/settings-mantle.md` |
| `ui/settings/sections/{system,internet,devices,time,privacy,accessibility,update}.js` | `parity/settings-windows.md` |
| `lib/features/<name>.js` and `_<name>-*.js` | the file that holds that surface's rows. Grep the parity files for the path |
| `main.js`, `preload.js`, the core `lib/*.js` | `parity/core.md`. For a surface-specific region of main.js, use that surface's file |
| `lib/updater.js`, `update-watch.js`, `packaged*.js`, `installer/**`, `.github/workflows/**`, `README.md` claims, `PERF.md` | `parity/ship.md` |
| `sidecar/**` | `parity/sidecar.md` for platform behaviour. Also the surface file whose behaviour changed |

## Ledger

| Ledger ID | Master SHA | Date | Subject | Effect | Rows | Native action |
|---|---|---|---|---|---|---|
| LED-000 | 6ca499f | 2026-11-06 | Baseline (Welcome: card 1 says what Mantle sends over the network on its own) | baseline | all 6,948 | inventory |

## Deviations, WebView2 fallbacks, drops

Every `deviation:`, `webview2:` and `dropped:` status in a parity row points to an entry here. If the user
would notice the difference, the entry needs the user's OK and its date.

| Ledger ID | Rows | Kind | What differs and why | User-visible effect | User OK (date) |
|---|---|---|---|---|---|
| (none yet) | | | | | |
