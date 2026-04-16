# RightSpeak Production Readiness Checklist

This checklist defines the remaining phases to reach a stable first production release.
Status values: `Not Started`, `In Progress`, `Blocked`, `Done`.

## Phase 1 - Retrieval Reliability Matrix
Status: `In Progress`

Goal:
- Verify selected/paragraph/document reading behavior against representative target apps.

Exit Criteria:
- Manual matrix completed for:
  - RightSpeak input box
  - Windows Notepad
  - VS Code editor
  - Edge/Chrome text field
  - Edge/Chrome page selection
- For each target/action pair, outcome is recorded as:
  - `Pass`
  - `Fail (with reason)`
  - `Unsupported (explicit)`
- No crashes or hangs during repeated runs.
- Failure messages clearly indicate retrieval strategy path.

## Phase 2 - Clipboard Fallback Hardening
Status: `In Progress`

Goal:
- Make clipboard fallback predictable and safe under lock/contention conditions.

Exit Criteria:
- Clipboard preserve/restore behavior verified in success and timeout scenarios.
- Restore failures are reported clearly.
- Clipboard contention does not crash app.
- Repeated fallback attempts do not degrade app responsiveness.

## Phase 3 - Hotkey and Tray Robustness
Status: `In Progress`

Goal:
- Ensure hotkeys and tray commands are consistent after settings changes and app lifecycle events.

Exit Criteria:
- Hotkeys work after startup, apply, hide-to-tray, and restore cycles.
- Tray command labels match configured hotkeys.
- Registration failures are surfaced as non-blocking status.
- Single-instance activation path remains stable.

## Phase 4 - Settings Robustness and Defaults
Status: `In Progress`

Goal:
- Validate settings load/save behavior across upgrade/reset cases.

Exit Criteria:
- Invalid or stale settings are normalized safely.
- Missing settings file recreates cleanly with defaults.
- Voice/rate/hotkeys persist correctly across app restart.
- No startup crash due to malformed settings file.

## Phase 5 - Diagnostics and Failure Telemetry
Status: `In Progress`

Goal:
- Improve supportability for real-world retrieval failures.

Exit Criteria:
- Retrieval failures include strategy context.
- Speech/hotkey/native-bridge failures include actionable status text.
- Logging remains minimal and non-spammy in normal flow.

## Phase 6 - Installer and First-Run Setup
Status: `In Progress`

Goal:
- Reduce manual setup burden, especially for browser context integration.

Exit Criteria:
- Build output packaging documented and repeatable.
- Native host registration workflow is reliable.
- Browser extension setup steps are clear for Chrome and Edge.
- First-run troubleshooting section exists.

## Phase 7 - Regression and Release Candidate Pass
Status: `In Progress`

Goal:
- Lock behavior for release candidate.

Exit Criteria:
- Full matrix rerun after all fixes.
- No open P0/P1 bugs.
- Known limitations documented explicitly.
- Release notes drafted.

## Notes For Current Iteration
- Phase 1 has started.
- Selected-text retrieval now reports a combined strategy-failure summary when all providers fail.
- Phase 2 has started.
- Clipboard fallback now avoids clobbering clipboard when it changed again before restore, and reports restore-skip/restore-failure explicitly.
- Phase 3 has started.
- Hotkey registration now reports granular status for startup/apply, including partial registration failures per action.
- Phase 4 has started.
- Settings load now backs up malformed settings files and resets to safe defaults.
- Settings save now writes via temp/replace flow to reduce partial-write risk.
- Phase 5 has started.
- Added minimal structured diagnostics log at `%LocalAppData%\\RightSpeak\\logs\\rightspeak.log`.
- Retrieval provider failures/success, clipboard fallback outcomes, hotkey registration outcomes, and settings recovery/save now emit diagnostic events.
- Phase 6 has started.
- Browser integration installer now validates extension IDs/origins, supports multi-ID setup, and can run bridge verification in one command.
- Added dedicated browser integration troubleshooting guide.
- Phase 7 has started.
- Added concrete regression execution sheet: `Resources/QA/RC-Regression-Execution.md`.
- Added release notes draft with known limitations: `Resources/QA/Release-Notes-Draft.md`.
- Added repeatable RC smoke runner: `Resources/QA/Run-RC-Smoke.ps1`.
- Added manual RC execution runbook: `Resources/QA/RC-Manual-Runbook.md`.
- Added guided manual result capture script: `Resources/QA/Run-RC-ManualChecklist.ps1`.
- Reproduced and fixed a repeated typed-text speech clipping issue on a real machine by prepending validated leading PCM silence before playback.
