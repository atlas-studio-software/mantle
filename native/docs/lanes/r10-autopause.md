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

## Lane r10-autopause-2 (continued, 2026-11-24): the feature wiring

Still nothing built or run (no SDK; owner's order). New, all in Features/AutoPause:

- `AutoPauseFeature.cs`: `[Feature("autopause", "Spotify auto-pause", true, Rest = true)]`, no chords, static `Options`
  (`apps`: media/any, Electron's label, choices and help). Wires engine + AutoPauseSpotify (host only) + AutoPauseHost +
  AutoPauseLink as autopause.js install() does: commands `status` (+detail, test) and `switch {on}`, the replayed
  `autopause.status` push (sent when state/words/card/mode/role change), `StatusOf`/`HoldWords` (pure), the tick loop
  (250 ms while wanted, never overlapping, start-up quiet 5 s / 0 in --test, meter `autopause.peaks {everyMs}` 1500 ms
  only when another media session exists or in any mode, warming up to 8 asks), the NOW PLAYING wake-up, the 15 s
  coexistence timer (CheckSafe: a throw stands by), standby enter (leave link, hand back) / leave (adopt, re-read),
  off (leave link, hand back), StopAsync (leave link first, ReleaseAll, wait for the queue; also on a rest).
  `PortsFor(test, env)`: live 8787-8790; --test only MANTLE_AUTOPAUSE_PORT (whole number 1025..65535), else none.
- `AutoPauseAttrib.cs`: _autopause-attrib.js ported (sources, matcherFor, the browser/exe tables, NEVER_BY_NAME, appLabel,
  Spotify/Mantle/sys exclusions; Mantle's AUMID is MediaService.MantleAumid).
- `AutoPauseNet.cs`: the REAL reads, not unit-tested: loopback GET (HttpClient, no proxy, Connection: close, 1 MB cap,
  never throws), the task file, `schtasks.exe /query` (no window, 4 s, killed on timeout), /ping with X-Mantle: coexist.

Tests: `AutoPauseFeatureTests` (16 facts; rig with NO ports, injected check/GET/bind, fake NOW PLAYING) and LinkApp got an
optional `may` (AUTO-098). Safety: SAFETY_stopping_the_feature_resumes_what_Mantle_paused_and_hands_back_every_hold,
SAFETY_a_coexistence_check_that_throws_stands_by_with_words_and_never_touches_Spotify,
SAFETY_Spotify_system_sounds_and_Mantle_itself_never_count; dry runs asserted in the hold and stop tests.

Rows now `written, not run`: AUTO-001..032, 040..052, 080..083, 088..090, 098, 109 (54). Some cite no test (004, 008..010,
025, 027, 028, 031, 083): said in the cell.

AUTO-033..039 (the sidecar meter): native/src/Mantle.Sidecar/Mantle.Sidecar.csproj compiles `..\..\..\sidecar\*.cs` with an
empty exclude list, so sidecar/AutoPause.cs IS linked unchanged. Left `todo`: no native test exercises it and this lane did
not write it.

Deviations (new): 8. The coexistence check does not overlap itself (Electron's setInterval can). 9. Exceptions are logged
as type + message, not a stack. 10. MANTLE_AUTOPAUSE_PORT above 65535 is refused (Electron takes any integer > 1024).
11. The stop log line does not repeat the stop reason (a game's name stays out of the log).

Shared file: `native/src/Mantle.App/App.xaml.cs`, one block after the media wiring, marked `// lane r10-autopause`:
`AutoPauseFeature.ConfigureMedia(new(() => media.Last, fn => media.OnMedia(fn)))`. The feature looks the source up again at
every coexistence check, so the order against FeatureSeam.Start does not matter (worst case: first 15 s without NOW PLAYING).

Not verified (no SDK): that `media.Last` carries `sessions[].aumid/status/app.name/timeline` as Electron's ctx.host.media()
does; that FeatureHost's TrySetEnabled calls OnEnabled synchronously (AUTO-006 relies on it); ManualTime firing 0 ms timers.

Left: AUTO-033..039 native test; a loopback test of AutoPauseServer and AutoPauseNet (port 0); a live interop run against
Quartz; the cloud build/test run above.
