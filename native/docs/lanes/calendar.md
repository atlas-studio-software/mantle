# Lane native-r7-calendar: the clock's calendar and Google Calendar

Branch `native/r7-calendar`. The clock's flyout (month grid, day dots, the day's list, keys, wheel) and the `gcal`
feature (lib/features/gcal.js and its `_gcal-*` modules), ported. Rows: docs/parity/bar.md BAR-415 and BAR-418..529.

## What is built

- `Mantle.Core/Features/Calendar/`
  - `GcalCore.cs`: PKCE S256, the consent URL (ONE scope, calendar.readonly), the client file, event rows, error words,
    backoff, "ago" (`_gcal-core.js`).
  - `GcalDays.cs`: the day index, dots, agenda words, the status line (`ui/bar/gcal-days.js`).
  - `GcalStore.cs`: `<userData>\gcal\` token.bin and client.bin encrypted, cache.json; temp + rename through
    SafeFile; a delete also removes `<name>.<pid>.tmp` (`_gcal-store.js`). Format: Electron's own (Chromium "v10"
    AES-256-GCM with the key from "Local State") whenever that key is there, else plain DPAPI (current user); both read.
  - `GcalGoogle.cs`: the only requests, through one send: https to oauth2.googleapis.com (token, revoke) and
    www.googleapis.com/calendar/v3 (GET only); any other host, scheme, port or method refused before a byte is sent;
    redirects never followed; 20 s timeout; the token only in the Authorization header.
  - `GcalAuth.cs`: sign-in on a 127.0.0.1 listener on an OS-picked port, state checked, PKCE verifier exchanged.
  - `GcalEngine.cs`: sync (4 s after start, then every 10 min), the cache, the month asks, backoff, offline polling,
    the sign-out generation counter (a late answer after Sign out writes nothing).
  - `GcalFeature.cs`: `[Feature("gcal")]`, rest: true, commands `gcal.status / signIn / signOut / pickClient /
    setCalendar / opened / want / open`, pushes `gcal.status` and `gcal.events` (replayed). `--test`: every Google call
    refuses ("test run: nothing is sent to Google"); signIn / signOut / pickClient / open answer dry runs.
  - `GcalSelfTest.cs` (MANTLE_GCAL_SELFTEST=1 in --test), `GcalColour.cs` (calendar colours are data; the title ink
    is the colour mixed 60% with ink in OKLab).
- `Mantle.App/Surfaces/Bar/Calendar/`
  - `CalPanel.cs`: the flyout. `BarWindow.Calendar.cs`: left or right click on the clock toggles pop-up key "cal";
    the clock's tooltip (long date + "Click: the calendar").
- Hooks: one line in BarWindow.xaml.cs (StartCalendar) and two "calendar wiring" blocks in App.xaml.cs.

## Automation ids and test switches

`cal.panel`, `cal.time`, `cal.date`, `cal.title` (its name is the shown month), `cal.today`, `cal.prev`, `cal.next`,
`cal.dow.<n>`, `cal.grid`, `cal.day.<yyyy-mm-dd>` (ItemStatus: selected / today / other / dot; HelpText: the dot's
colour or "many"), `cal.agenda`, `cal.agenda.head`, `cal.agenda.list`, `cal.agenda.empty`, `cal.row.<n>` (name = the
title), `cal.agenda.note`, `cal.connect`, `cal.notifications`.

- `MANTLE_TEST_GCAL` (--test only): `{status, events}` drawn in place of the engine's; links stay in the feature.
- `MANTLE_TEST_CAL_NOW` (--test only, epoch ms): the flyout's clock frozen (the screenshot pair, the UI tests).
- `MANTLE_TEST_CAL_KEYS=1` (with MANTLE_TEST_VISIBLE=1, a CI runner only): the test calendar may take the keyboard.
- `MANTLE_TEST_POPUPS=1`: the clock keeps the framework's stand-in "cal", so PopupTests keep passing.

## The six privacy claims and their tests

All in `tests/Mantle.Core.Tests/Calendar/GcalPrivacyTests.cs`; made-up data, a scratch folder, a fake wire.

| Claim | Test |
| --- | --- |
| 1. Only calendar.readonly | `Claim1_only_the_calendar_readonly_scope_is_asked_for` |
| 2. Data only for the dots and the day list | `Claim2_the_data_is_used_only_for_the_dots_and_the_day_list` (+ GcalFeatureTests: only gcal.status / gcal.events go out, no link reaches the bar) |
| 3. Token only on this PC, DPAPI current user | `Claim3_the_token_is_stored_only_on_this_PC_encrypted_with_DPAPI_for_the_current_user` |
| 4. Event cache only on this PC | `Claim4_the_event_cache_is_one_file_on_this_PC_and_no_request_carries_an_event` |
| 5. No own servers or analytics; only Google | `Claim5_it_talks_only_to_Google_reads_only_and_never_follows_a_redirect` |
| 6. Sign out deletes token AND cache AND revokes | `Claim6_sign_out_deletes_the_token_and_the_cache_and_every_tmp_and_revokes_at_Google` |
| Logs carry no title, token or secret | `No_log_line_carries_an_event_title_a_token_or_the_client_secret` |

Other tests: `GcalCoreTests`, `GcalEngineTests` (store, both formats, sync, refresh, backoff, partial, the generation
counter), `GcalAuthTests` (the loopback listener against a fake exchange, no browser), `GcalFeatureTests` (the Rig),
and `tests/Mantle.UiTests/Calendar/CalendarTests.cs` (opens from the clock, 42 days, dots, the day list, keys, light
and dark, signed out).

## The screenshot pair

`tools/parity/calendar/run-cal-parity.ps1` + `electron-cal.cjs`: Electron's `?gcal=events` mock page with its Date
frozen by a preload, the native app with the mock's replays as MANTLE_TEST_GCAL and the same instant as
MANTLE_TEST_CAL_NOW (2026-11-11 12:00 UTC). ci.ps1 stage `cal` gates on 2 of 2 scored and floors light 0.57 / dark
0.55. First scores (run 43001365502): light 0.5947, dark 0.5793. By eye the two are the same flyout (same month, ring,
dots, rows, colours); the score is low because the native pop-up is 4 px wider and 12-13 px taller (268x520 vs
264x507 DIP), so the bottom-aligned crops drift row by row. Closing that gap is the next step for this stage.

## Deviations (each on purpose, each to revisit)

- The keyboard focus ring on a day is a 2 px accent stroke (WinUI), not Chromium's focus-visible outline.
- "Notifications" presses win+n (Windows' own centre, through the sidecar's `keys`) until Mantle's own notification
  centre is native; a --test run logs a dry run and presses nothing (BAR-424).
- `gcal.pickClient` refuses live runs ("choosing the Google client file is not in the native app yet"): there is no
  native file dialog yet (BAR-434).
- Resume from sleep is not watched (BAR-463): the 10-minute timer and the open-time refresh (older than 2 min) catch up.
- Token and client files are read AND written in Electron's v10 format when Electron's "Local State" key exists (PLAN
  3.7(a), both apps share the files in the fallback period) instead of PLAN 3.7(c). Tested only against a made-up key
  in a scratch folder: it MUST be checked against a real Electron-encrypted token.bin at switch-over (BAR-529).

## Left

- The pair's size gap (above); BAR-463 resume; the native file dialog for pickClient; the perf budget (BAR-516) is not
  measured natively yet.
