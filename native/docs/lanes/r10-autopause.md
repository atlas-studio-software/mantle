# Lane native-r10-autopause: Spotify auto-pause, the protocol-2 core

Branch `native/r10-autopause` from native/main 40414f38 (stage 9). Rows AUTO-001..132 in docs/parity/apps.md.
Nothing here was built or run: there is no .NET SDK on the laptop (owner's order for this window). Every row this lane
touched is `written, not run`; none is `tested`.

## Spec

docs/autopause-protocol.md (protocol v2, it wins), lib/features/_autopause-core.js, _autopause-compat.js, _autopause-link.js,
_autopause-coexist.js, autopause.js, and the tests autopause-link.test.js / autopause-compat.test.js. Read only: the shared
files are byte-identical with Quartz's and were not edited.

## What is built (Mantle.Core/Features/AutoPause/, pure unless said)

- `AutoPauseGate.cs`: the request gate (Origin only extension schemes; no Origin needs Sec-Fetch-Site absent or "none";
  Host 127.0.0.1/localhost[:port]), srcKey, the /pause ttl clamp (5 s .. 10 min, 0 = 180 s), a URLSearchParams-style query.
- `AutoPauseEngine.cs`: _autopause-core.js ported (tracks and verdicts, holds with TTL, one serial Spotify queue, only resume
  what we paused, re-pause with the 2-per-episode back-off, adopt, inherit, handOff, releaseAll). Clock injected.
- `AutoPauseCoexist.cs`: the helper check (task XML -> enabled/disabled/absent/unknown, schtasks CSV, the old-bridge /ping
  test: app spotify-autopause and NO server field), CheckSafe (a throw = in charge, with words). Reads are injected; it has
  no member that can start, stop, enable or edit anything.
- `AutoPauseHost.cs`: _autopause-compat.js ported socket-free: status JSON and sig, every route, /watch bookkeeping (answer
  on change / 240 s, a closed watch drops its client's hold unless another watch of that src is open), successorWatching
  (live can=host hold + a watch open or answered within 2 s), start on the first free port, stop without answering watches.
- `AutoPauseLink.cs`: `AutoPauseElection` (pure rules: who is a host, lowest port wins, bind/none/client, the yield-down
  rule, protocol >= 2 watches) and `AutoPauseLink` (_autopause-link.js ported: election, client hold + 60 s refresh,
  /watch loop with the 40 ms guard, re-elect 150 ms after the host goes, the old bridge's 3 s /ping poll, none-role 10 s
  re-look while holding, retire). Timers through HostClock, GETs through an injected `LinkGet`.
- `AutoPauseSpotify.cs`: the engine's hands (control()/spState()): `media {op:"control", action, aumid, dryRun}` 3000 ms,
  the 2 s pending state, the --test simulation. Refuses (logged, with words) unless this app is the HOST.
- `AutoPauseServer.cs`: `AutoPauseHttp` (pure head parser and answer writer) and `AutoPauseServer`, the thin socket part:
  TcpListener on IPAddress.Loopback, ExclusiveAddressUse (binding is the lock), one request per connection (the Electron
  host answers `Connection: close` to everything), 16 KB head cap, Content-Length over 4 KB never read, a socket read
  watches for the client closing while a /watch waits. Requests run on the host thread via HostClock.Post. NOT unit-tested.

No `[Feature("autopause")]` class yet (see Left): nothing in the app starts any of this, so this branch binds no port.

## Tests (tests/Mantle.Core.Tests/Features/AutoPause/, socket-free, never 8787-8790)

`AutoPauseKit.cs` (FakeSpotify shared by every app, FakeNet carrying each GET to the AutoPauseHost bound on made-up ports
48787-48790, LinkApp = engine + host + link as the Electron test's makeApp), `AutoPauseLinkTests` (17), `AutoPauseHostTests`
(15), `AutoPauseEngineTests` (16), `AutoPauseSafetyTests` (10): 58 facts. HostLoop + ManualTime from Features/HostTestKit.cs.

Safety, one test each: only the host touches Spotify (SAFETY_only_the_host_touches_Spotify), the helper is never touched
(SAFETY_the_helper_and_its_task_are_never_touched), a coexistence check that throws stands by
(SAFETY_a_coexistence_check_that_throws_counts_as_in_charge_with_words), --test sends dry runs only
(SAFETY_test_mode_sends_every_command_as_a_dry_run_and_keeps_the_simulated_state), a passive sidecar's refusal comes back in
words (SAFETY_a_passive_sidecar_refusal_comes_back_in_words_and_is_logged), refusals are logged
(The_gate_answers_403_before_any_route_and_the_refusal_is_logged_in_words).

## Deviations from Electron (none from protocol v2 that I know of)

1. A request with two Host, Origin or Sec-Fetch-Site headers is refused 403 (Node keeps the first Host and joins the rest).
2. A chunked body or a Content-Length that is not one plain number is refused 413 without reading (Node decodes chunked).
3. Gate refusals, 413 and 400 are logged in words (first of each kind, then once a minute with a count; never the site).
   Electron logs none of them.
4. The 204 preflight also says `Connection: close`; a request target with whitespace or starting `//` is 400.
5. AutoPauseSpotify refuses a command when not host (Electron relies on the engine seeing "none" only).
6. Times are HostClock's monotonic ms, not Date.now (watchedAt included). JSON is written with non-ASCII unescaped, as
   JSON.stringify does.
7. With no ports (a test run) the host logs "the bridge-compatible API is not served (no port to use)".

## PEND-012 (does the cited Electron test cover the row?)

Checked against autopause-link.test.js and autopause-compat.test.js only; autopause.test.js citations were NOT checked.
- AUTO-072 cites autopause-link.test.js: it shows no resume-then-pause across a takeover, not the serial queue itself.
- AUTO-096 cites autopause-compat.test.js: it tests "first port taken falls to the next", not all four taken.
- AUTO-100 cites autopause-link.test.js: nothing there waits 60 s; the refresh is untested in Electron.
- AUTO-102: the 40 ms hot-loop guard is untested in Electron.
- AUTO-093: the link test covers the probe and the client; "only while on, not stopped, not in standby" is the feature's.
- AUTO-115: the link test exercises /watch but never asserts "answers at once when since differs".
Native covers 071, 075, 079, 091, 092, 101, 106, 108, 119, 132 that cite `none` in Electron.

## Left

- AUTO-001..052: the feature itself (`[Feature("autopause", "Spotify auto-pause", true, Rest = true)]`, the `apps` option,
  switch/status commands, the status words, ticking and start-up quiet, the meter read and the attribution of
  _autopause-attrib.js). AUTO-033..039 are sidecar/AutoPause.cs rows: check whether the native app's sidecar build links it.
- AUTO-080..083, 088..090, 109: feature wiring (off/stop/rest hand-back, the 15 s coexistence timer, standby enter/leave,
  MANTLE_AUTOPAUSE_PORT in --test). The real coexistence reads (task file, schtasks query, the 700 ms ping with
  X-Mantle: coexist) are delegates the feature must supply.
- AUTO-098 (a host whose may-host turns false retires): written in AutoPauseLink.ElectOnce, no test yet.
- A loopback integration test of AutoPauseServer (port 0, a slow-test category) and a live interop run against Quartz.

## Held for the cloud (not run)

`cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit"`

## Shared files

None. This lane touched only its own folders, this note and the Native column of AUTO rows.
