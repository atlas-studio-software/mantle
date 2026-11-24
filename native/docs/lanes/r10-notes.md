# Lane native-r10-notes: the quick notes pad (model, store, service)

Round: stage 10 (native r10). Branch `native/r10-notes` from `350d2e7a`. Nothing here was built or run: the laptop has
no .NET SDK, so every row is `written-not-run` or `partial`, never `tested`. Held cloud command:
`cloudtest --recipe mantle-native-public -- "pwsh -NoProfile -File native/tools/ci.ps1 -Stages build,unit"`.

## The shape

All new code is in `native/src/Mantle.Core/Features/Notes/`; no shared file was edited (Win+Alt+N was already in
`Keys/ChordTable.cs`, and the generator registers `[Feature]` classes, so there is no registration block).

- `NotesFormat.cs`: the file format, pure (serialize / parse / escape, BOM, CRLF), the limits (8 MB, 500 notes,
  10 copies / 10 min, 3 restarts, 10 x 100 ms), stamps, tab label and tooltip, plain-words fs reasons.
- `NotesStore.cs`: notes.md, deleted.md, outside-change copies and rescue files. EVERY change goes through
  `Data/SafeFile`, so the write gate is asked first; a refusal saves nothing and answers why.
- `NotesPaths.cs`: live = `%USERPROFILE%\Documents\Mantle Notes` (literally USERPROFILE); --test =
  `MANTLE_NOTES_DIR` or `<data root>\notes`, and a folder under Documents is refused.
- `NotesPad.cs`: the editing model (tabs, rev / saved rev, new, prune, step, delete, undo), `NotesAutosave` (400 ms
  quiet, 3 s cap, 5 s retry; the time is passed in) and `NotesFind` (search, count words).
- `NotesFeature.cs`: `[Feature("notes", "Quick notes (Win+Alt+N)", true, Chords = { "Win+Alt+N" }, Rest = true)]`.
  Commands toggle / open / close / new / state; `notes.state`, `notes.open`, `notes.new` events. The text never goes
  through a command or broadcast: a surface edits `NotesFeature.Current.Pad` in-process (`Edit`, `Blurred`,
  `DeleteNote`, `UndoDelete`, `Activated`).

## Safety decisions (for the coordinator)

1. **The live notes folder is never written today.** It is the folder Electron Mantle writes and it is outside
   Mantle's data folders, so `WriteGate.Refusal` refuses it in every run. Native READS the user's notes (the
   import is reading in place: nothing is copied) and any save answers "could not save ...: notes.md is outside
   Mantle's data folders, so native Mantle never writes it"; the text stays in the pad. Making native save there
   needs a gate decision (admit `Documents\Mantle Notes` under the same --live + only-shell rule) and is not made here.
2. **No gate given = nothing written.** Without `NotesFeature.Configure(new Host(Safe: ...))` the feature uses
   `WriteGate.Never` (fail closed). App wiring (`App.xaml.cs`: Safe = the run's SafeFile, RescueDir = the data root,
   DataRoot, Surface) is left for the surface lane.
3. **No surface = the chord stays Windows'.** Until a surface is attached (`Host.Surface`), the feature reports
   unhealthy ("the notes pad is not drawn in native Mantle yet, so Win+Alt+N stays Windows' own"), so the host hands
   the chord back instead of swallowing a key that would show nothing. Off, unhealthy and resting all hand it back
   (the host's rule). The kill switch is never seen by this feature.
4. **A damaged notes.md is never renamed `.unreadable`**: Electron's rule is kept (not opened, left exactly where it
   is so the user still finds it, copied aside byte for byte before the first save over it). The pad is read-only
   while the file is refused.
5. **Loss bound:** text is on disk 400 ms after typing stops and at most 3 s into a burst; at once on blur, close,
   quit, session end and rest. A crash can lose at most the last 3 s, the same as Electron (whose text sits in the
   renderer until the pull). A rest that cannot save carries the text in module scope to the wake; a quit that
   cannot save writes `notes-rescue <stamp>.md` in RescueDir (when the gate allows it there).

## Rows

written-not-run (44): NOTE-001, 003, 004, 007, 012, 013, 014, 024, 025, 026, 035, 037, 039, 041, 042, 043, 044, 050,
052, 053, 054, 055, 057, 058, 061, 063, 064, 065, 067, 069, 070, 072, 073, 076, 077, 078, 080, 082, 084, 091, 092,
093, 094, 095. Written-not-run (partial) (15; the cell says which half is done): NOTE-011, 028, 029, 031, 036, 040,
047, 056, 066, 074, 079, 083, 097, 098, 101.

SAFETY rows, each its own test: storage NOTE-052, 053, 056, 058, 063, 064, 072 and the gate
(`NotesStoreTests.Safety_*`); loss NOTE-041, 042, 044, 091, 092, 093 (`NotesFeatureTests.Loss_*`).

Left `todo` (surface or window): NOTE-002, 005, 006, 008, 009, 010, 015..023, 027, 030, 032, 033, 034, 038, 045,
046, 048, 049, 051, 059, 060, 062, 068, 071, 075, 081, 085..090, 096, 099, 100, 102. Code without a test: 010
(re-open re-pushes focus), 060 (reload on open), 062 (re-check before each rename try), 068 (tmp removed first),
075 (symlinked deleted.md refused). NOTE-900..906 are Tools hub / Timer rows, not this lane's.

## Deviations

- NOTE-056 / 068: SafeFile opens the tmp with FileMode.Create (not O_EXCL); the tmp is removed first. A rename
  replaces the link itself, so a linked notes.md never has its target overwritten.
- NOTE-062: notes.md is re-checked before every try because the store runs its own retry loop around a
  no-retry `SafeFile.WriteAtomic` (each try rewrites the tmp).
- NOTE-072 / 075: deleted.md is rewritten whole (old bytes + the block) tmp-and-rename instead of appended, so it
  still only grows and a failure leaves it as it was; a symbolic link is refused in Electron's words, a hard link
  is not detected (the rename breaks it, so the other name is never written).
- NOTE-041: no pull (`__notesTake`): the surface edits the model in-process.
- Saves run on the host thread (sync file IO, under 8 MB).

## PEND-012 (Electron tests checked against their rows)

lib/features/notes.test.js covers the cited rows except: NOTE-024 (only `ok`, never the healthy words or the label),
NOTE-011 (only the tray-off case), NOTE-035 (the clamp), NOTE-069 (only "a file is in the way").
