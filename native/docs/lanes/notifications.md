# Lane native-r7-notif: the bar's right cluster (notification centre, tray, chips, More menu)

Branch `native/r7-notif` from native/main 3c8d76d. Rows: bar.md "Notification centre" (BAR-819..862), "Notifications
IPC" (863, 864), "Notification area" (368, 369), "Tray" (352..362), "Tray tone" (363..367), "Mantle tray menu" (370, 371),
"Status chips" (181..191), "More menu" (575..582), "Tooltips" (053..055).

## Part 1: the host half (built)

| File | Electron | What |
|---|---|---|
| `Mantle.Core/Features/Notifications/NotificationCentre.cs` | `lib/notifications.js` (the bell's half) | list read (sidecar `notifications` op list), watch armed on every ready, 10 s poll when it is not, same-rev skip, burst coalescing, `shape()` (unread, detail, seenNote), seen ids in `<data>/notifications-seen.json` through the write gate (tmp + rename, pruned), `knownIds` guard (500 cap, de-dup, integers), remove / clear (one app's, case-insensitive) / open, SAY words + `PlainOr`, failures logged once, the `Notifications` state piece; event `"notifications"` (`NotificationsPayload`, replayed) |
| `Mantle.Core/Features/Notifications/NotificationArea.cs` | `main.js` startTrayPoll / trayForBar / tray.list / tray.click / sys.keys, `bar.js` toneOf / toneKeep / renderTray signature | 5 s tray poll (60 s while unreadable), Mantle's own icon left out, event `"tray"` `{status, reason, items}` (`TrayPayload`, replayed, pushed only on change), click guard (unclickable refused with the sidecar's reason, no tooltip text in the log), sys.keys whitelist; `TrayTone.Of` (alpha-weighted luminance), `TrayTone.Signature`, `TrayToneCache` (48 settled, in-flight kept) |
| `Mantle.Core/Features/Notifications/NotificationWords.cs` | `ui/bar/notifications.js` | ages, groups, badge, bell tooltip, card tooltip, title / meta / letter, the Win+N plan (`WinKey`), the panel note |
| `Mantle.Core/Features/Notifications/NotificationCommands.cs` | `COMMANDS` | `[Command]` notifs.get / seen / remove / clear / open, tray.list, tray.click, sys.keys |
| `Mantle.App/App.xaml.cs` block "notifications wiring" | `main.js` start | makes both, registers the commands |

### A --test run never changes the runner

- `MANTLE_TEST_NOTIFS`: a JSON list of toasts or a payload `{toasts, ok, reason, detail, via, fail:{seen, remove, open}}`;
  the toasts' own `unread` flags seed the seen set. Without it the list is empty, or the access check's failure (the same
  words as the bar's "notifications off" chip). Windows' list is never read.
- `MANTLE_TEST_TRAY`: a JSON list of icons or `{status, reason, items}`. Without it the strip is empty and the log says why.
- Dismiss / clear / open: `notifs: dry run (--test): <what>: would dismiss N notification(s) (<ids>) in Windows; nothing
  was sent, Windows was not changed` / `notifs: dry run (--test): would open notification <id> (its app|its link) and
  dismiss it; nothing was opened or sent`; the fixture list loses those ids. Tray: `tray: dry run (--test): would
  <button>-click the notification-area icon <id>; nothing was sent`. Keys: `sys.keys: dry run (--test): would press
  <combo>; nothing was pressed`.

### Tests

`Mantle.Core.Tests/Features/Notifications/NotificationCentreTests.cs` (9) and `NotificationAreaTests.cs` (8).
BAR-370 / 371 (Mantle's own tray menu batch) were already ported by feature-host-2: `TrayBatchTests`
(`AnAskNeverRebuildsInsideTheCall_...`, `SameKeyIsSkipped_ForceRebuildsAtOnce_...`).

## Part 2: the visible half (built)

| File | Electron | What |
|---|---|---|
| `Surfaces/Bar/Notifications/BarWindow.Notifications.cs` | `notifications.js` init / renderBell / health / open, `bar.js on('bar.winkey')` | the bell: the count BESIDE the glyph (the button widens only then), quiet ink-3 + "off" when the list cannot be read, tooltip / UIA name = `BellTip`, ItemStatus `none` / `some` / `new N` / `off`; click toggles the centre, right-click only opens; `OpenNotifications()` for the calendar's footer link (BAR-855); health(): the "notifs" chip follows the list; the lane's SurfaceLink also carries the shell state to the chips and listens for `bar.winkey` |
| `Surfaces/Bar/Notifications/NotifPanel.cs` | `notifications.js` build / paint / act, `notifications.css` | the centre, 340 wide, code-built with theme tokens: head + Clear all, groups (icon or letter, name, count, dismiss-all), cards (logo, title, body clamped to 4 lines, meta, dismiss, hero, the unread dot of what was new at the open), loading / empty / unavailable, the note, the plain-source detail, the Win+N link; seen on open, busy dimming, a refusal as the note, re-measured (PopupHost.Resize) on every change; a 30 s age ticker while open |
| `Surfaces/Bar/Notifications/BarLook.cs` | - | tokens in code, the flat button face, glyphs, data: pictures (decoded once), straight-alpha pixels for the tone |
| `Surfaces/Bar/Tray/BarWindow.Tray.cs` | `bar.js` renderTray / trayButton / toneOf, `bar.css .tray .ti` | the strip first in the right cluster (before the separator): 26 px buttons 2 apart, 20 px tiles, letter tiles, unclickable dimmed (.42) with the reason in the tooltip and the "can't click" chip on a click (nothing sent), promoted:false kept for the More menu, rebuilt only on a new signature; tones measured once (TrayTone / TrayToneCache), the plate per theme; a theme change redraws the strip |
| `Surfaces/Bar/Chips/BarWindow.More.cs` | `bar.js` buildMorePop | the More menu (only when MANTLE_TEST_POPUPS is unset): MANTLE, the test copy line, every chip in words, "Tray icons not shown", the hidden tray icons + their why line + taskbar settings, Settings (Win+I), Restore, Restart, Quit (armed for 4 s) |
| `Surfaces/Bar/Frame/Chips.cs` (2 lines) | `bar.js` chipData | "Desktop not attached" not while the desktop has not started; "Tray icons not updating" suppressed while the strip says the tray is unavailable (`TrayUnavailable`) |
| hooks | | `BarWindow.xaml` names `RightCluster` and `NotifsBtn`; `BarWindow.xaml.cs` calls `StartNotifications()`, `RenderPip` passes the shell state, the startup access check leaves the "notifs" chip to the centre once it has a list; `App.xaml.cs` block "notifications wiring": the lane's SurfaceLink (its own StateFeed on the hub) and `@<file>` fixtures |

### --test

- `MANTLE_TEST_NOTIFS` / `MANTLE_TEST_TRAY` may be `@<path>`: the list is read from that file (the parity run's lists carry
  pictures and outgrow an environment variable).
- The More menu's items are dry runs: `more: dry run (--test): would open Settings (ms-settings:); nothing was opened`,
  `... would restore Windows' taskbar; nothing was changed`, `... would quit Mantle; nothing was closed`.

### Tests and the pair

- `tests/Mantle.UiTests/Notifications/NotificationsTests.cs` (4): the bell's count and widening, the centre's groups /
  cards / unread / dead / meta, dismiss / dismiss-all / Clear all / Win+N as dry runs, the empty state; an unreadable list
  in dark (quiet bell, warn pip, right-click opens, "Notifications unavailable"); the strip's tones, dimming, left / right
  clicks as dry runs, the refused click's chip, the More menu (who line, chips, hidden icon and its why, Quit armed,
  Settings dry run); the tones in dark.
- `tools/parity/notifications/run-notifs-parity.ps1` + `electron-notifs.cjs`: Electron `?mock=1&mode=pill&tray=tones&notifs=some`;
  its `notifs.get` / `tray.list` answers (times moved on) become the native fixtures; the centre and the strip, light and
  dark. ci.ps1 stage `notifs` (last in the list), floors `notifs tray` / `notifs centre`.

## Deviations

- The centre reads the list itself; the toasts feature still has its own `NotificationFeed` (two readers in a live run
  until the toasts feature takes `NotificationCentre.Published`, a request to the toasts owner).
- `shell.restart` does not exist natively yet: the More menu's Restart says "Could not restart: restarting is not part of
  this build yet".
- Nothing publishes `bar.winkey` natively yet (the switcher lane's hook); the bar listens for it.
- Cards open on a pointer tap; keyboard focus / Enter (BAR-844) is not drawn (the bar's pop-ups never take the keyboard).
- The edge bar's chip strip is not drawn (the island's pip stands for it, as before).

## Left

- BAR-844 (keyboard on cards), BAR-856 (no panel fallback), BAR-191 (a push channel that cannot be subscribed).
- The calendar lane calls `OpenNotifications()` for BAR-855.

## State (part 2)

- Run 43014208915 (4b148f0, -Stages build,unit,ui,notifs): `NATIVE-CI: ok (4 ok, 0 skipped)`; unit 639/639, ui 50/50;
  notifs centre light 0.7078 / dark 0.6998 (floor 0.66), tray light 0.4400 / dark 0.7564 (floor 0.30, held low: see below).
- The native tray crop is the backdrop colour, not the bar (even through ParityNative.CleanGrab) although UI Automation
  puts the strip there and clicks at that spot reach it; the tray scores measure that, not the drawing. To look at next.
- bar.md: 30 rows `tested:` (centre 819-823, 826, 831-835, 838, 839, 843, 848, 850, 853, 863; tray 354, 358-360, 363;
  chips 181, 182; More 575-577, 579, 582). The fixture's hidden-icon reason came back as Mantle's plain fallback
  ("Mantle cannot click Tailscale, and Windows did not say why"): PlainOr refused it; why is not known yet.
