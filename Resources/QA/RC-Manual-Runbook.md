# RightSpeak RC Manual Runbook

Use this runbook to execute the GUI/manual checks listed in `RC-Regression-Execution.md`.

Optional result capture helper:
```powershell
.\Resources\QA\Run-RC-ManualChecklist.ps1
```

Execution date: `2026-04-16`

## Setup
1. Start RightSpeak.
2. Ensure no stale test text remains in clipboard-sensitive workflows.
3. Keep `Resources/QA/RC-Regression-Execution.md` open and record each result immediately.

## Retrieval Matrix Steps
### RightSpeak input box
1. Type multi-line text in the input box.
2. Click `Read`.
3. Expected:
   - Speech starts.
   - Window remains responsive.
   - `Stop` interrupts playback.

### Selected text (Notepad / VS Code / browser field / browser page)
1. Select text in target app.
2. Trigger selected-text read via:
   - hotkey `Ctrl+Shift+<selected key>` or
   - tray `Read Selected Text`.
3. Expected:
   - Selected text is spoken.
   - On failure, status message names strategy/fallback context.

### Paragraph read
1. Put caret or selection in a paragraph in supported editor target.
2. Trigger `Read Paragraph` (window or tray).
3. Expected:
   - Paragraph candidate text is spoken when available.
   - Unsupported targets fail clearly without crash.

### Document read
1. Focus a text control exposing full content.
2. Trigger `Read Document` (window or tray).
3. Expected:
   - Full document/control text is spoken when exposed by UIA patterns.
   - Unsupported targets fail clearly without crash.

## Hotkey/Tray Robustness Steps
### Startup hotkey registration
1. Launch app.
2. Expected:
   - Status message either confirms registration or reports specific failed action.

### Apply new hotkeys
1. Change selected/typed/stop keys in UI.
2. Click `Apply`.
3. Expected:
   - New keys work.
   - Tray labels update to new key hints.
   - No crash/hang.

### Hide/restore tray cycle
1. Close window (hide to tray).
2. Restore using tray `Show RightSpeak` or tray icon double-click.
3. Expected:
   - Commands still function.
   - Window activation behaves correctly.

### Single-instance activation
1. Start app once.
2. Start app second time.
3. Expected:
   - Existing instance activates/restores.
   - Second launch does not create duplicate active instance.

## Settings Robustness Steps
### Persistence
1. Change voice/rate/hotkeys.
2. Restart app.
3. Expected:
   - Values persist and are applied.

### Missing settings file
1. Exit app.
2. Delete `%LocalAppData%\RightSpeak\settings.json`.
3. Start app.
4. Expected:
   - App starts with defaults.
   - No crash.

### Malformed settings recovery
1. Exit app.
2. Put invalid JSON into `%LocalAppData%\RightSpeak\settings.json`.
3. Start app.
4. Expected:
   - App starts with defaults.
   - Corrupt backup file created: `settings.json.<timestamp>.corrupt`.

## Diagnostics Verification
1. Trigger at least one successful retrieval and one failed retrieval.
2. Check `%LocalAppData%\RightSpeak\logs\rightspeak.log`.
3. Expected:
   - Structured events exist for success and failure paths.
