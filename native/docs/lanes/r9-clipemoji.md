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
