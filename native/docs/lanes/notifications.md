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

## Deviations

- The centre reads the list itself; the toasts feature still has its own `NotificationFeed` (two readers in a live run
  until the toasts feature takes `NotificationCentre.Published`, a request to the toasts owner).
- `shell.restart` does not exist natively yet: the More menu's Restart will say "Could not restart: restarting is not
  part of this build yet".
- Nothing publishes `bar.winkey` natively yet (the switcher lane's hook); the bar will listen for it.

## Left (part 2, the UI)

- `Surfaces/Bar/Notifications/`: the bell (badge, `.new` widening, `.off`, tooltip, the `notifs` chip), the panel
  (`NotifPanel`, 340 wide, groups, cards, hero, note, foot, Win+N link), right-click opens, Win+N via `bar.winkey`,
  `OpenNotifications()` for the calendar's footer link (BAR-855, the calendar lane calls it).
- `Surfaces/Bar/Tray/`: the strip (inserted before the separator), 26 px buttons, letter tiles, dimmed unclickable,
  tones per theme (`Root.ActualThemeChanged`), clicks through `tray.click`, the "can't click" chip.
- `Surfaces/Bar/Chips/`: the shell state into `Chips` (BarWindow.RenderPip passes null today), the tray-unavailable
  suppression of "Tray icons not updating", the More menu (replaces the MANTLE_TEST_POPUPS stand-in only when that
  variable is unset).
- UI tests, `tools/parity/notifications/` pair script (Electron `?mock=1&mode=pill&tray=tones&notifs=some`; the mock's
  `tray.list` / `notifs.get` answers become MANTLE_TEST_TRAY / MANTLE_TEST_NOTIFS), the ci.ps1 `notifs` stage, bar.md rows.

## State at the part-1 handoff (context ceiling)

- Run 42996911243 (dc88b24): build ok, unit 4 of 639 failed (an int-backed JsonValue is not TryGetValue<long>; seen ids
  read only in Start). Run 42997472325 (f3c72f0): AOT publish failed on IL2026 (generic JsonArray.Add<T>). Both fixed.
- Run 42998120400 (7470f00, -Stages build,unit,ui): `NATIVE-CI: ok (3 ok, 0 skipped)`; unit 639 of 639, ui 46 of 46.
- bar.md: BAR-370, BAR-371 `tested:` (TrayBatchTests). Every other row of the lane is still `todo`: the host half is
  built and unit-tested, but no row is marked until the bar draws it (part 2). No full run, no screenshot pair yet.
