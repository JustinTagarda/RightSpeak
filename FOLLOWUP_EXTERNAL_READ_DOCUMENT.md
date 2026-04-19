# External Read Document Follow-Up (Resolved Baseline, Monitor)

## Status
- `RESOLVED_BASELINE_MONITOR`
- As of `2026-04-18`, external-app `Read Document` is enabled in UI/tray/hotkey flow.
- Current user-facing behavior:
  - normal read path is active;
  - failures are surfaced with explicit status and diagnostics.

## What Was Fixed
- Re-enabled external document command flow after prior temporary gate.
- Hardened browser-PDF retrieval behavior:
  - multi-cycle clipboard document capture;
  - PDF settle-window upgrade for larger post-copy text;
  - browser-PDF UI Automation fallback when clipboard copy is blocked.
- Hardened document candidate selection:
  - provider candidate scoring/selection to avoid low-quality leading viewer preamble.
- Hardened long-read speech continuity:
  - chunk-render retry path for transient pinned-engine prefetch misses (for example `speech_chunk_render_no_clip`) before terminal failure.

## Current Baseline Guardrails
1. Keep browser-PDF clipboard stabilization/retry path.
2. Keep browser-PDF automation fallback path for blocked copy contexts.
3. Keep document candidate quality scoring.
4. Keep chunk-render retry diagnostics and stream diagnostics in speech path.

## Key Diagnostics To Keep
- Retrieval:
  - `focused_read_document_started`
  - `document_retrieval_started`
  - `document_retrieval_provider_result`
  - `document_retrieval_candidate_scored`
  - `document_retrieval_success`
  - `focused_read_document_retrieval_result`
- Browser-PDF document fallback:
  - `clipboard_document_capture_cycle_*`
  - `clipboard_document_pdf_settle_window_upgrade`
  - `clipboard_document_browser_pdf_copy_blocked`
  - `clipboard_document_browser_pdf_automation_fallback_*`
  - `clipboard_document_capture_succeeded`
- Speech continuity:
  - `speech_chunk_render_attempt`
  - `speech_chunk_render_no_clip`
  - `speech_chunk_render_retry_scheduled`
  - `speech_chunk_stream_*`
  - `speech_chunk_continuous_playback_failed`

## Monitoring Notes
- If a new regression appears, capture a fresh log window around:
  - `focused_read_document_started` to `focused_read_document_speech_result`.
- For browser PDF specifically, include:
  - `copySequenceTransitions`,
  - `captureStrategy`,
  - first document preview lines,
  - chunk index where speech continuity failed (if any).

## Scope Note
- This file tracks only external `Read Document`.
- External paragraph follow-up remains tracked separately in [FOLLOWUP_EXTERNAL_READ_PARAGRAPH.md](/D:/Projects/RightSpeak/FOLLOWUP_EXTERNAL_READ_PARAGRAPH.md).
