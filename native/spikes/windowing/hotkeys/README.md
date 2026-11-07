# hotkeys: primitive 8, global shortcuts in the WinUI process

**Status: works**, with one test caveat: the keys are injected (see below). Runner run
20261107-084007-227bdf (Windows Server 2025, build 26100): `WINDOWING hotkeys: ok (20 of 20 checks)`.

**Technique:** a `WH_KEYBOARD_LL` hook on its own thread with its own `GetMessage` loop. The hook
proc only reads three fields, steps a small key machine (the sidecar's `KeyMachine` rules for a
solo Win tap, Win+Tab and Alt+Space) and writes the gesture into a ring. The loop then posts it to
the UI `DispatcherQueue`. A modifier keyup gets the VK 0xFF partner key first, so Start and the
menu bar stay shut. Raw keyboard input on a message-only window re-arms a hook that Windows dropped
(`HookHealth.cs`'s rule: 4 raw key events and no callback for 1.5 s, at most one re-arm per 15 s).

Copy `KeyHook.cs` (class `KeyHook`: `Start`, `Proc`, `Step`, `OnRaw`, `Install`). Wire it up as
`App.OnLaunched` does.

## Numbers

| claim | result |
|---|---|
| Win tap fires, on the UI thread | yes; with no hook the same injected key opened `Windows.UI.Core.CoreWindow 'Search'` (SearchHost), and with the hook nothing opened |
| Win+R still Windows', no tap | Run dialog opened, 0 spike events |
| Alt+Space fires, no window menu | yes; with no hook the same keys opened notepad's `#32768` window menu |
| Win+Tab fires, Task View and Start stay shut | yes; with no hook it opened `XamlExplorerHostIslandWindow 'Task View'` |
| latency, key sent -> handler on the UI thread, 50 Win taps | median 0.67 ms, p95 0.95 ms, max 1.28 ms (hook -> UI median 0.59 ms) |
| latency, 50 Alt+Space (hook) | median 0.30 ms, p95 0.50 ms, max 0.88 ms |
| latency, 50 Alt+Space (`RegisterHotKey`, `WM_HOTKEY` on the hook thread) | median 0.47 ms, p95 0.62 ms, max 1.15 ms; 50 of 50; no window menu |
| UI thread blocked 2 s, 5 Win taps during it | key -> hook max 0.16 ms (no key waited for the UI thread); all 5 fired once the UI thread was free (key -> handler median 1,044 ms, max 1,755 ms); Start stayed shut; hook still alive afterwards, 0 re-arms |
| contrast: the hook ON the UI thread, same 2 s block | keys waited 718-1,620 ms for the hook, **only 3 of 5 taps fired**; Windows kept the hook |
| dead hook found and re-armed | hook unhooked behind `KeyHook`'s back, then 6 keys: re-armed (4 raw key events, no callback); unhooking the old handle then failed, as for a hook Windows removed; the next Win tap fired |
| injected keys ignored by default | 0 events without `--accept-injected`; Windows opened its own Search, as with no hook |
| cost | 1 process; private 35.7 MB, private working set 22.2 MB; idle CPU 0.3 % of one core over 5 s |

`RegisterHotKey` vs the hook for Alt+Space: both work on the runner and both keep the window menu
shut. The hook is about 0.2 ms faster. `RegisterHotKey` needs no partner key and cannot be dropped
by Windows, but it cannot express the solo Win tap or Win+Tab, and it fails if another app holds
Alt+Space (Electron Mantle registers Alt+Space with `globalShortcut`, which is `RegisterHotKey`).
Either works for Alt+Space; the hook has to exist for the Win gestures anyway.

## Caveats and findings

- **Injected keys.** Like the sidecar, `KeyHook` ignores `LLKHF_INJECTED` / `LLKHF_LOWER_IL_INJECTED`.
  Every key in this check is `SendInput`, so the spike runs with `--accept-injected`, and one run
  without it proves the default. Our own 0xFF partner key carries `dwExtraInfo` = `MANT` and is
  always ignored. Real hardware keys were not tested.
- **Windows 26100 did not drop the hook** in any test:
  - a 3 s sleep inside one callback (run 3: callbacks kept coming, 547 -> 559 over the next 12 key events);
  - a 6 s freeze of the hook thread;
  - the hook on a UI thread blocked for 2 s.

  `LowLevelHooksTimeout` is unset on the runner. Keys simply waited, and some gestures were lost.
  So the re-arm is proved with a simulated drop, not a real one.
- **False alarm after a freeze.** After the 6 s freeze, the backlog of raw input (keys Windows had
  already passed on after the timeout) arrived with no callbacks. The health rule re-armed a hook
  that was still installed: unhooking the old handle SUCCEEDED, and its log line still says
  "Windows dropped the hook". This costs one harmless re-install, the same as the sidecar would do.
  Suggested follow-up, not done because it was not run: word the log from the old handle's unhook
  result (`LastOldUnhook`), as the status already records.
- **Win key on the Server runner.** A solo Win tap opens SearchHost's `Search` window, not
  `StartMenuExperienceHost`. The check treats either as "Start opened". On a client SKU it is
  StartMenuExperienceHost.

## What the runner cannot show

- Real hardware keys (with `LLKHF_INJECTED` clear) and the default ignore-injected path with
  someone typing.
- An elevated window in front (low-level hooks of a normal process are skipped there), a resume
  from sleep, or the lock screen. Win+L is never sent, because it would lock the runner session.
- A real Windows-initiated hook removal (see above).

## Output (out/hotkeys/)

`results.json` / `summary.md` (`baseline`, `latency`, `uiBlocked`, `hookHealthFreeze`,
`hookHealth`, `registerHotKeyLatency`, `contrastHookOnUiThread`), the app's `events.jsonl`,
`status.json` and `log.txt`. There are no screenshots; this lane reads window state, not pixels.
