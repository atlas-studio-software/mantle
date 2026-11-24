# Lane notes: native-r9-clipemoji (stage 10)

Parity rows: launch.md CLIP-001..076 (there is no CLIP-075), EMO-001..050, WEL-001..053. Electron source is the spec
(read only). Branch `native/r9-clipemoji`, from `native/main` at 40414f38.

No .NET SDK was available in this window: everything below is **written, not run**. The rows say
`written-not-run:<test file>`; they become `tested:` after the held cloud run
(`cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit,ui,parity"`).

## What is here

Pure logic and its tests only. No `[Feature]` and no `[Surface]` is registered, no chord is taken and the App does
not call any of it yet, so nothing engages in the app.

| Native file | Electron source | What it is |
|---|---|---|
| `Mantle.Core/Features/Clipboard/ClipModel.cs` | `ui/clipboard/model.js` | filter, preview lines, ago/meta, keys, selection moves, reselect, placement, dark-glass log line |
| `Mantle.Core/Features/Clipboard/ClipHost.cs` | `lib/features/clipboard.js` | which pins file the sidecar may have (`PinsFor`, asks the write gate), `clip.start`'s request, health words, item ids, the list cache |
| `Mantle.Core/Features/Emoji/EmojiModel.cs` | `ui/emoji/model.js` | catalogue, tones, search ranking, recents, grid layout and moves, keys, placement |
| `Mantle.Core/Features/Emoji/EmojiTabs.g.cs` | `ui/emoji/model.js` | the Symbols and Kaomoji tabs and the popularity list (generated) |
| `Mantle.Core/Features/Emoji/EmojiPicks.cs` | `lib/features/emoji.js` | recents, tone, per-app picks in `features\emoji.json` through the feature's `IStore` |
| `Mantle.Core/Features/Welcome/FirstRun.cs` | `lib/welcome.js` | `Judge` (read only), `ReadFlag`, `EvidenceIn`, `WriteFlag` (through `SafeFile`), `WelcomeRecorder` (record, done, closed) |
| `tools/parity/clipemoji/make-golden.cjs` | both `model.js` files | runs Electron's models in node over fixed inputs; writes `EmojiTabs.g.cs` and the golden; `--check` reports drift |

The emoji data itself is **not copied** into native: `Mantle.Core.csproj` embeds Electron's `ui/emoji/emoji-data.js`
unchanged (resource `Mantle.Core.Emoji.emoji-data.js`) and `EmojiCatalog` reads its `var DATA = {...};` line. The
first attempt kept a JSON copy in `EmojiData.g.cs`; the oss-guard pre-commit hook refused it (8 hits: some CLDR
keywords match its family-word list), and one copy of the data is better anyway.

Clipboard history itself (capture, privacy flags, dedupe, the 25 / 4 MB / 64 MB limits, the DPAPI pins file, paste) is
`sidecar/ClipHistory.cs`, linked unchanged into `Mantle.Sidecar` (PLAN.md A4). Its storage rows are proved on that
linked source by `Mantle.Sidecar.Tests/ClipEmoji/ClipPinsTests.cs` (in process, passive, temp pins files).

## Safety

- The sidecar writes the pins file itself, past native's write gate. `ClipHost.PinsFor` therefore hands it
  `<root>\features\clipboard-pins.dat` only when the gate would let native write that file (and its `.tmp` and
  `.unreadable`); otherwise `clip.start` goes without a `pinsFile`: pins work for the run and Electron's file is never
  read or written by the sidecar on native's behalf. The App must use it when clipboard history is wired.
- `FirstRun.Judge` only reads. Flag writes go through `SafeFile.WriteAtomic` (Electron's `first-run.json.<pid>.tmp`
  name and bytes). A flag Mantle cannot read is first set aside as `first-run.json.unreadable` (native's data rule;
  Electron overwrites it); a second damaged flag is not written over while the first aside copy exists.
- `EmojiPicks` writes only through the feature's `IStore` (gate, tmp + rename, unknown keys kept, `.unreadable`).
- Tests use `TestDir` (or a temp folder in the sidecar tests) only; no test reads or writes `%APPDATA%`.
- Nothing logs clipboard text or an emoji; `EmojiPicksTests` checks the store's log lines.

## Findings

- PEND-012: CLIP-042 cites no Electron test, but sidecar/test-clipboard.js does cover it ("an unreadable pins file:
  said in plain words", "pinning after that sets the damaged file aside"). CLIP-005, EMO-038, EMO-039 have no
  Electron test at all (the native tests are the first). CLIP-031's cited case covers only "pins file is not
  plaintext", not "no log carries content" (that is CLIP-030's case).
- The sidecar's own `.unreadable` rule deletes an older `clipboard-pins.dat.unreadable` before setting a new one
  aside (ClipHistory.cs SavePinsLocked): a second damaged pins file loses the first copy. Native data's rule keeps the
  first. Left as Electron has it (the file is shared source); worth a sidecar fix.
- `Data/DataFileSpecs.cs` lists `first-run.json` with `PidTmp: false`, but lib/welcome.js writes
  `first-run.json.<pid>.tmp`. Only the tmp name differs (the bytes are the same); `FirstRun.WriteFlag` uses the pid
  name. Not changed here (shared data file).
- `ClipModel.Ago` past a week prints `MMM d` in the current culture and local time zone, like
  `toLocaleDateString`; the golden's one date case was made in PST and is the same day in UTC.

## Left

UI surfaces (the clipboard panel, the emoji window and its key watch, the welcome window and its three cards), the
App wiring (feature registration, the Win+V / Win+. / Win+; chords, `welcome.*` commands, the start-up call to
`FirstRun.Judge`), and every row that needs them. See the report for the ID ranges.

## Lane r9-clipemoji-2 (stage 10, branch `native/r10-clipemoji-2` from bc0277b7)

Still **written, not run** (no .NET SDK in this window either; nothing was built or run).

| Native file | Electron source | What it is |
|---|---|---|
| `Mantle.Core/Features/Clipboard/ClipboardFeature.cs` | `lib/features/clipboard.js` | `[Feature("clipboard", "Clipboard history (Win+V)", true, Chords = { "Win+V" }, Rest = true)]`: clip.start with `ClipHost.PinsFor`'s choice, health (`ClipHost.Health` of the panel's and the sidecar's trouble), the Win+V toggle (arm first, list behind it or the cache; a press while opening or Esc cancels, nothing shows), warm-up, relist after copies, paste (dry run in --test) with its notices, pin / delete / clear, switch-off sends clip.stop, a game rest only hides |
| `Mantle.Core.Tests/ClipEmoji/ClipboardFeatureTests.cs` | | the feature on the host rig (fake sidecar, manual clock); no file, no clipboard |
| `Mantle.Sidecar.Tests/ClipEmoji/ClipCaptureTests.cs` | `sidecar/test-clipboard.js` | capture, privacy flags, limits, dedupe, delete / paste of a gone item, the paste dry run, the passive refusal, on the LINKED `sidecar/ClipHistory.cs` through its test-only `clip.capture` / `clip.seed` / `clip.limits` (passive; nothing reads or writes the real clipboard) |

The panel surface talks to the feature by events `clipboard.show {list, arm, from, error?}`, `clipboard.hide {why}`,
`clipboard.changed {list}`, `clipboard.warm {list}`, `clipboard.notice {title, text, ms}`, `clipboard.state {panel, ok,
on, test, why?}` (replayed), and commands `clipboard.ready {ok?, why?}`, `.paste {item}`, `.pin {item, pinned}`,
`.delete {item}`, `.clear`, `.close`, `.list`. The App must call `ClipboardFeature.Configure(new(() =>
ClipHost.PinsFor(profile, gate)))` before the host loads features; without it pins are kept for the run only.

Deviations:
- **Win+V stays Windows' own until the panel is up.** The feature is not healthy (`ClipboardFeature.NoPanel`) until the
  panel calls `clipboard.ready`, so the host hands Win+V back to Windows while no panel exists, and `clip.start` waits
  for the panel too: with no panel nothing the user copies is held. Electron starts the history on sidecar ready
  (its feature makes its own window). A panel that sees `clipboard.state` with `panel:false` (a new install after a rest
  or a restart) calls `clipboard.ready` again.
- No `clip.stop` from `StopAsync`: the host marks a feature stopped before its stop runs, so the scoped platform refuses
  the call. Switching off (OnEnabled false) does send it; quitting ends the sidecar and the memory-only history with it.
- No surface gate port: the toggle is a sequence number in the feature (a press or Esc while opening bumps it and the
  open is dropped). The "drawn" wait (CLIP-006), the keyboard check (CLIP-013..015) and placement on screen are the
  surface's.

Safety (each its own test):
- Storage: unchanged from the first lane (`ClipPinsTests`: CLIP-031, CLIP-040, CLIP-042); `ClipCaptureTests` points
  clip.start at a pins file under `%TEMP%\mantle-native-tests` that does not exist, and `ClipboardFeatureTests` hands
  the feature a made-up pins choice: no test reads or writes a profile.
- Clearing (CLIP-047): `ClipPinsTests.Clear_all_and_switching_off_leave_only_the_pins_in_memory_and_on_disk` (sidecar,
  memory and disk) and `ClipboardFeatureTests.Clear_all_delete_and_pin_reach_the_sidecar_and_answer_with_a_fresh_list`
  (the command reaches clip.clear and answers with the list read again).
- Privacy: `A_private_skip_records_no_format_names_and_no_text_anywhere` (sidecar) and
  `A_busy_skip_is_logged_and_no_log_line_carries_copied_text` (feature logs).
- The kill switch: the feature claims only Win+V and observes nothing (asserted).

PEND-012 (this lane): the cited sidecar/test-clipboard.js cases for CLIP-021..037, 041, 044, 047, 053, 057 exist
(checked by name). CLIP-037's "unreadableImage" half has no Electron case (native's is the first). CLIP-007, CLIP-019,
CLIP-054 and CLIP-056 cite no Electron test (`none`); the selftest does run the warm-up (CLIP-007).

Left todo on purpose (partly covered): CLIP-004 (the sidecar's arm itself is live-only; the feature's arm-first order is
tested), CLIP-052 (the second click in flight
is the page's), CLIP-039 (the pin button and tooltip are the page's), CLIP-020/027/028 (live-only).

### The emoji service (`Mantle.Core/Features/Emoji/EmojiFeature.cs`, tests `ClipEmoji/EmojiFeatureTests.cs`)

`[Feature("emoji", "Emoji picker (Win+.)", true, Chords = { "Win+.", "Win+;" }, Rest = true)]` with the
`closeAfterInsert` option. Health: the `emoji.verdict` probe (Electron's words) behind the same panel gate as the
clipboard (`EmojiFeature.NoPanel` until `emoji.ready {panel}`), so with no picker window both chords stay Windows' own.
A chord toggles: `emoji.arm` (150 ms; a --test run never reads the foreground, the panel's own hwnd stands in); over an
administrator window `emoji.windows` and a notice, no open; else `emoji.show`, then the key watch `emoji.watch {on, hwnd,
panel, leaseMs: 6000}` renewed every 2 s while open (it lapses by itself, so a refused call at stop is safe), and
`emoji.key` events to the panel; a `lost` watch closes. `emoji.insert {ch, close?, via?}` types (`emoji.type`, dry run in
--test; `emoji.typeback` when the panel took the keyboard) and remembers the pick through `EmojiPicks`; `emoji.tone`.
The watch never takes a key with Ctrl, Alt or Win held (sidecar `Emoji.Captured`), so the kill switch passes.

Deviation: Core has no clipboard, so when there is no window to type into (or typing failed) the answer says
`copy: true` and the PANEL puts the pick on the clipboard (never in a test). Electron's feature wrote it itself.
Not yet: the 2-minute idle destroy (EMO-003, the surface's window), the UIA caret ask (`emoji.caret`, EMO-008), a
`testkey` command (tests raise the fake platform's `emoji` events instead).
