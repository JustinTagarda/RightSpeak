# RightSpeak Release Notes (Draft)

Version: `0.1.0-rc1`  
Date: `2026-04-16`

## Highlights
- Added local Windows text-to-speech with voice and rate controls.
- Added selected-text reading pipeline with multi-strategy retrieval:
  - UI Automation selection
  - focused control retrieval
  - clipboard fallback
- Added global hotkeys:
  - read selected text
  - read typed text
  - stop reading
- Added tray quick actions for typed/selected/paragraph/document read and stop.
- Added browser context-menu integration for Chrome/Edge via extension + native messaging host.
- Added single-instance behavior and activation handoff.
- Added diagnostics log for key failure/success paths.

## Reliability and Setup Improvements
- Clipboard fallback restoration behavior hardened to avoid clobbering newer clipboard contents.
- Settings save flow hardened with temp/replace write strategy.
- Corrupt settings recovery now backs up malformed files and restores safe defaults.
- Browser integration installer now validates IDs/origins and supports multi-ID install in one command.
- Fixed a local speech playback clipping issue observed on a real Windows machine by adding validated leading silence before buffered playback.

## Known Limitations
- Selected/paragraph/document retrieval depends on what target apps expose through UI Automation; compatibility is not universal.
- Browser context-menu read requires extension installation and native host registration per user profile.
- Windows context-menu integration is browser-specific and not a universal OS-wide right-click action.
- Paragraph/document retrieval currently provides a first foundation path and may not map perfectly in all editors.

## Upgrade Notes
- Existing users should rerun browser integration install if extension ID changed.
- Existing settings files remain compatible; malformed files are auto-backed up and reset.

## Diagnostics
- App diagnostics log path:
  - `%LocalAppData%\RightSpeak\logs\rightspeak.log`

## Validation Status
- Automated build checks passed.
- Full manual regression matrix pending completion in:
  - `Resources/QA/RC-Regression-Execution.md`
