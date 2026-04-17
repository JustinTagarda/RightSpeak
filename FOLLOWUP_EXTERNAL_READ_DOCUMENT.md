# External Read Document Follow-Up (Pending Final Fix)

## Status
- `OPEN`
- As of `2026-04-17`, external-app `Read Document` is **temporarily disabled** in UI/hotkey/tray command flow.
- Current user-facing behavior:  
  `Read Document (external app) is temporarily disabled pending final fix.`

## Why It Was Disabled
- The external document pipeline was unstable and still read wrong preamble content in browser/PDF scenarios (especially Chrome PDF viewer), even after:
  - focused-control browser bypass attempts,
  - clipboard document fallback,
  - leading viewer-UI sanitization.
- To avoid unreliable output, command is intentionally hard-failed pending final remediation.

## Temporary Gate Location
- [MainViewModel.cs](/D:/Projects/RightSpeak/ViewModels/MainViewModel.cs)
  - `ReadDocumentAsync()` now logs:
    - `focused_read_document_temporarily_disabled_pending_fix`
  - and returns immediate status failure message.

## Repro Scenario (Previously Failing)
1. Open PDF in Chrome:
   - `file:///C:/Users/Justiniano/Downloads/ACA-22-67%20DESCRIPTIVE%20STATISTICS.pdf`
2. Focus PDF tab content.
3. Trigger `Read Document` from RightSpeak.
4. Observed before gate:
   - often starts with browser/PDF viewer accessibility or UI text instead of pure document body.

## Key Diagnostics Already Added
- Focus/document orchestration:
  - `focused_read_document_started`
  - `document_retrieval_started`
  - `document_retrieval_provider_result`
  - `document_retrieval_success`
  - `focused_read_document_retrieval_result`
- Browser/PDF and fallback diagnostics:
  - `document_retrieval_browser_prefers_clipboard_fallback`
  - `clipboard_document_capture_started`
  - `clipboard_document_capture_succeeded`
  - `clipboard_document_capture_timeout`
- Speech diagnostics (separate known issue):
  - `piper_speech_playback_failed`
  - `speech_fallback_engaged`

## Latest Confirmed Symptom Pattern Before Gate
- Retrieval frequently succeeded with large text (`textLength` around `28300`) but content still began with wrong/non-document preamble.
- Fallback and sanitization improved behavior but did not fully stabilize first-content correctness.

## Next-Agent Follow-Up Plan
1. Re-enable `ReadDocumentAsync()` behind a guarded feature flag or branch-only toggle.
2. Capture and compare first `N` lines from:
   - focused-control document text,
   - clipboard fallback text.
3. Choose source by content-quality heuristic:
   - reject known viewer/accessibility/navigation preamble,
   - prefer body text density and paragraph continuity.
4. Add deterministic acceptance criteria for Chrome PDF:
   - first spoken chunk must start from actual document content, not viewer UI/help text.
5. Re-run manual matrix:
   - Chrome PDF (local file URL)
   - Notepad
   - VS Code
   - browser text pages/inputs
6. Remove temporary gate only after repeated pass.

## Notes
- Keep AGENTS reliability constraints intact:
  - preserve focused-read restore behavior,
  - preserve tray shell-window exclusions,
  - do not regress selected/paragraph flows while fixing document mode.
