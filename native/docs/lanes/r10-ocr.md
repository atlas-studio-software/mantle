# Lane notes: Copy text, the OCR half (native/r10-ocr)

Brief: OCR-001..023 (native/docs/parity/apps.md). Electron: lib/features/_ocr-text.js (pure text shaping),
lib/features/screenshot.js (#checkOcr, #ocrForPage, #ocrRecheck, #commitText, PLAIN, #plainError, #noPaths,
#missingCommand), sidecar/Ocr.cs and sidecar/Screenshot.cs (shot.ocr; shared source, not edited here).

State: written, not built and not run. This laptop has no .NET SDK and the window's rule was no dotnet, no cloudtest.
Every signature called was re-read from the tree.

## What is where

Folder `src/Mantle.Core/Features/Ocr/`, namespace `Mantle.Core.Features.Ocr`.

- `OcrText.cs`: a line-by-line port of _ocr-text.js: Tidy, Median, ParagraphBreaks, Shape (CRLF, blank paragraph
  line not counted), Preview (40 chars, word cut past 60%, ellipsis), Headline, LogLine (counts and timings only),
  Similarity (tests). Records OcrLine (text + box in crop px, nulls when no box) and OcrShaped.
- `OcrWords.cs`: screenshot.js's whole PLAIN table in the same order (first match wins), PlainError with its
  fallback, NoPaths, MissingCommand, and the fixed reasons (NotChecked "has not been checked yet", OlderSidecar,
  NoReasonGiven, CouldNotAsk, CouldNotRead).
- `OcrService.cs`: no [Feature]: Electron has none for OCR (it lives inside screenshot.js), so this is the service the
  screenshot overlay (SHOT rows) will call. CheckAsync (ocr.status, 8 s, one in flight), FromStatus (pure),
  ShouldRecheck (30 s, never for "built without WinRT"), RecheckAtCapture (not awaited), ForPage (the button's
  state: disabled always carries a plain reason), Changed (availability flipped), CopyTextAsync (shot.ocr over the
  held grab, 20 s backstop, frame released after whatever happened, plain text to the injected IOcrClipboard only when
  something was read, one log line, an OcrCard back). --test is a dry run (OcrDryRun: counts and a similarity score).

Sidecar transport: a `Sender(verb, JsonObject, TimeSpan)` delegate returning PlatformReply, the SnapCore pattern; the
overlay lane wires it to IPlatform.

## Safety

Local and offline only: the only verbs are ocr.status and shot.ocr to the local sidecar (Windows.Media.Ocr). No
pixel ever passes through Mantle.Core (the crop is cut inside the sidecar from the frozen frame). The recognised text
goes to IOcrClipboard and the card only; every log line is counts, timings, sizes, the language and path-free reasons.
Tests: OcrServiceTests.Privacy_TheRecognisedTextIsNeverInTheLog_OnlyCountsAndTimings,
TestMode_ADryRun_NeverTheRealClipboard_OnlyTheScore, OcrTextTests.Log_CountsAndTimings_NeverTheText; no silent
failure: BeforeTheFirstAnswer_CopyTextIsDisabledWithItsReason, Status_UnavailableWithoutAReason_StillCarriesOne.

## Deviations

- No DIP to pixel mapping here: Electron's overlay hands commitText a rect already in physical px (G.MIN_SIDE check
  in the overlay), so OcrRect is physical and the mapping stays with the overlay (SHOT).
- Size limits are the sidecar's (MinSide 8, MaxImageDimension); the service passes the rect through and words the
  refusal, as Electron does.
- PlainError/NoPaths are ported whole here because Copy text needs them; when the SHOT lane ports screenshot.js it
  should reuse OcrWords (or move it) rather than keep a second copy.
- A clipboard writer that throws while copying is caught and worded; Electron's equivalent was a missing writeText.

## PEND-012 (cited Electron tests that do not cover their row)

- OCR-003: "wiring: the card gets plain words" checks copyError/saveError wiring, not the disabled button's reason.
- OCR-005: the ocrRecheck test has no "built without WinRT" case (never re-asked is untested in Electron).
- OCR-006: none cited.
- OCR-021: "#1: the card came up, copied..." is the success card, not the failure note.

## Left

- OCR-002 (engine language), OCR-008 (the sidecar's own retry), OCR-012 (accuracy bars): sidecar/Ocr.cs reused as
  source, need a real Windows OCR engine: sidecar/test-ocr.js territory.
- The overlay's T key, drag, 5 s card and the push to open overlays: SHOT rows.
