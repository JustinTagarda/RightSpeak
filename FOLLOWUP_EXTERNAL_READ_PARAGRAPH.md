# External Read Paragraph Follow-Up (On Hold)

## Status
- `ON_HOLD`
- As of `2026-04-18`, external-app `Read Paragraph` remediation is paused.
- Current directive:
  - do not continue browser-PDF caret-targeting behavior changes until explicitly re-opened.
  - keep existing paragraph diagnostics in place for future troubleshooting.

## Scope
- This file tracks only external `Read Paragraph`.
- External `Read Document` follow-up is tracked in [FOLLOWUP_EXTERNAL_READ_DOCUMENT.md](/D:/Projects/RightSpeak/FOLLOWUP_EXTERNAL_READ_DOCUMENT.md) and is currently in resolved-baseline monitoring mode.

## Latest Confirmed Symptom Pattern
- In browser PDF context, caret-based paragraph retrieval remains unreliable.
- Managed UIA path cannot access true caret range (`caret_range_unavailable_managed_api`).
- Geometry-based point ranges can resolve to non-caret paragraphs or be far from cursor and rejected.
- Clipboard paragraph fallback can capture short fragments depending on PDF viewer behavior.

## Relevant Diagnostics
- Focus and provider flow:
  - `focused_read_paragraph_started`
  - `paragraph_retrieval_started`
  - `paragraph_retrieval_provider_result`
  - `paragraph_retrieval_success`
  - `paragraph_retrieval_all_failed`
- Focused browser-PDF geometry diagnostics:
  - `paragraph_provider_focused_pdf_geometry_decision_trace`
  - `paragraph_provider_focused_pdf_cursor_point_success`
  - `paragraph_provider_focused_pdf_deferred_to_clipboard`
  - counters in geometry trace:
    - `caretRangeUnavailableCount`
    - `selectionRejectedEmptyCount`
    - `rangeFromPointRejectedDistanceCount`
- Clipboard paragraph diagnostics:
  - `paragraph_provider_clipboard_copy_sent`
  - `paragraph_provider_clipboard_capture_observed`
  - `paragraph_provider_clipboard_retrying_pdf_copy`
  - `paragraph_provider_clipboard_rejected_short_pdf_fragment`
  - `paragraph_provider_clipboard_timeout`

## Resume Plan (When Re-Opened)
1. Implement low-level caret range retrieval for browser PDF (`TextPattern2.GetCaretRange`) in Windows interop layer.
2. Use caret range geometry as highest-priority candidate when near cursor and text is meaningful.
3. Keep empty-selection rejection and distance gating to avoid stale top-of-page reads.
4. Keep clipboard fallback as last strategy; reject non-paragraph short fragments deterministically.
5. Re-test with the known PDF layout and validate:
   - caret placed in target paragraph reads that paragraph,
   - no forced text selection required,
   - no top-of-page paragraph regressions.

## Notes
- Speech behavior is out of scope for this workstream.
- Preserve AGENTS constraints for focus restore and tray shell-window exclusions.
