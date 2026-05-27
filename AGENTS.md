# AGENTS.md

## Inheritance Rule

- Always read and follow [D:\Projects\AGENTS.md](D:\Projects\AGENTS.md) first.
- Treat the global file as mandatory unless this file explicitly overrides it for RightSpeak-specific behavior.
- If any instruction conflicts, the more specific RightSpeak rule wins, but the global file still applies everywhere else.

## Mandatory Requirements Pointer

- `D:\Projects\DEBUG-LOGGING.md` is mandatory implementation guidance for logging behavior.
- Any logging-related code change in this repository must comply with `D:\Projects\DEBUG-LOGGING.md`.
- Do not merge or ship logging changes that violate those requirements.

## Project Purpose
RightSpeak is a Windows desktop utility built with WPF and .NET 10.
Its core job is simple:
- let the user select text in another Windows application
- retrieve that text reliably
- read it aloud with minimal friction

Planned expansion may include:
- reading queue or history
- optional AI-assisted features later

RightSpeak is Windows-first. Do not trade away Windows reliability for cross-platform design goals.

---

## Primary Product Goal
Optimize for:
1. functional correctness
2. reliability across common Windows applications
3. low-friction reading flow
4. maintainability
5. performance
6. UI polish

This is a utility app first, not a showcase UI.

---

## Current Implementation Baseline
- Keep the solution as one project until the codebase clearly needs more separation.
- Organize code so it can split later without a rewrite.
- Do not introduce multi-project churn prematurely.

## Build Mode Metadata

- `FAST_BUILD_PROJECT`: `RightSpeak.csproj`
- `DEBUG_EXE_PATH`: `bin\Debug\net10.0-windows10.0.19041.0\RightSpeak.exe`

## Microsoft Store Packaging Baseline

- For Store package/submission requests, apply `D:\Projects\10-MSSTORE-PACKAGE-GENERATION.md` end-to-end.
- Use `FULL-BUILD` only for Store packaging workflows.
- Use Visual Studio 2026 MSBuild only: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`.
- Enforce x64-only Store packaging output.
- Before packaging, increment `RightSpeak.Package\Package.appxmanifest` identity version using `Major.Minor.Build.0` with revision `0`.

Preferred in-project folders:
- `Views/`
- `ViewModels/`
- `Models/`
- `Services/`
- `WindowsIntegration/`
- `Interop/`
- `Settings/`
- `Resources/`

Current implementation target:
1. manual text reading, selected-text reading, and document reading are production-facing; paragraph retrieval code exists but is not currently exposed in the production UI/tray/global-hotkey surface
2. local text-to-speech with Windows OneCore, `System.Speech`, and Piper support
3. voice selection, speed control, voice preview, and Piper voice downloads/updates/removal
4. pause/resume playback, cancel-before-speech for external reads, global hotkeys, and tray quick actions
5. theme switching, always-on-top window behavior, version display, and background Store update handling
6. browser-specific retrieval hardening, especially PDF fallback paths

---

## Technical Direction
Use these defaults unless explicitly overridden:
- UI: WPF
- Framework: .NET 10 on Windows
- Language: C#
- Pattern: MVVM
- Windows integration: Win32 interop plus UI Automation
- Speech: built-in Windows speech APIs behind an abstraction

Do not migrate the app to WinUI 3, MAUI, Avalonia, Flutter, or Electron unless explicitly requested.

---

## First Technical Decisions

### Speech
For the first local TTS implementation, prefer `System.Speech.Synthesis.SpeechSynthesizer` behind an interface.

Target design:
- `ISpeechService`
- `SpeechRequest`
- `SpeechOptions`
- `SpeechResult`

The abstraction must leave room for:
- voice selection
- rate adjustment
- stop support
- queueing
- future engine replacement

Do not add cloud speech or AI dependencies for MVP TTS.

Validated reliability note:
- The current local speech path includes a leading-silence mitigation in the Windows speech service because some Windows systems clip the beginning of each utterance.
- Do not remove or reduce that mitigation without rerunning manual typed-text playback validation against repeated reads and confirming the opening words remain audible.

Validated Piper manual-read start stability note:
- A confirmed issue caused manual reads with Piper to start inconsistently and often skip the first word.
- The fix depends on keeping the current Piper start-stability path (including warm-session handling and the leading-token guard workaround for susceptible short manual utterances).
- Treat this behavior as fixed baseline and do not revise or simplify the underlying code path unless explicitly instructed.

Validated Piper playback-path reliability note (fixed baseline):
- A confirmed issue pattern showed startup clipping on short reads when Piper used a direct playback path, while continuous-stream reads were stable.
- The fixed baseline is: Piper reads must use the continuous stream playback path for both short and long inputs, including manual read and external-app reads.
- Single-chunk Piper requests are intentionally routed through the same chunk stream path (continuous waveOut) used by multi-chunk reads.
- Do not reintroduce or prefer direct Piper `SoundPlayer` playback for normal read flows unless explicitly instructed and revalidated.
- Keep continuous-stream diagnostics in place (for example `speech_single_chunk_stream_routed`, `speech_chunk_stream_*`, `piper_continuous_playback_*`) so startup regressions can be diagnosed quickly.

Validated voice-default semantics rule:
- In the voice selector, the `System default` option must always mean the Windows OS/engine default voice (for example OneCore/System.Speech defaults), not an app-preferred voice and not Piper.
- Do not route `System default` to Piper when Windows engine defaults are available.

### Text Retrieval
Treat text retrieval as a multi-strategy pipeline, not a single API call.

Preferred acquisition order:
1. UI Automation selection or text patterns
2. focused control text retrieval
3. clipboard fallback
4. graceful failure with clear user feedback

Preferred interfaces:
- `ISelectedTextProvider`
- `IParagraphTextProvider`
- `IDocumentTextProvider`

Potential concrete classes:
- `UiAutomationSelectedTextProvider`
- `ClipboardSelectedTextProvider`
- `FocusedControlDocumentProvider`

Validated tray-focus reliability note:
- A confirmed regression occurred where tray-triggered "Read Selected Text" restored focus to Explorer tray overflow windows instead of the previously focused app, causing selected-text retrieval and clipboard fallback timeouts.
- The fix depends on treating tray shell windows as non-target foreground windows, including:
  - `Shell_TrayWnd`
  - `TrayNotifyWnd`
  - `NotifyIconOverflowWindow`
  - `TopLevelWindowForOverflowXamlIsland`
- Do not remove these exclusions or relax tray focus-restore verification without rerunning manual Notepad selected-text tests for both hotkey and tray flows.

Validated selected-text scope reliability notes:
- A confirmed regression occurred in VS Code where selected-text read spoke unrelated editor content (for example text with many slashes) because focused-control retrieval accepted full control value/document text instead of true selection.
- The fix depends on keeping `FocusedControlSelectedTextProvider` selection-only for selected-text flow (read from `TextPattern.GetSelection()` ranges only).
- For selected-text commands, do not use full control/document value as a success path.
- If selection is unavailable through focused-control UI Automation patterns, the provider must fail so later fallback strategies (including clipboard) can run.
- Do not restore document-wide fallback inside selected-text providers without rerunning VS Code selected-text tests for both hotkey and tray paths.

Validated selected-text cross-app reliability note (fixed baseline):
- A confirmed issue occurred in browser PDF selected-text reads where long highlighted selections were spoken as only the first line/fragment.
- The fixed behavior depends on the following combined path:
  - browser-PDF UI Automation/focused-control selected-text providers must defer to clipboard fallback because browser PDF text-pattern selection is frequently partial;
  - clipboard selected-text capture for browser-PDF context must keep the PDF-specific stabilization/retry path (multi-cycle copy attempts plus settle-window upgrade);
  - Piper selected-text input must be normalized to a single-line payload before synthesis so multiline clipboard text is not truncated by line-based stdin handling.
- Treat this selected-text behavior as fixed baseline for "Read Selected Text from other apps."
- Do not revise, simplify, or bypass this path (provider deferral rules, clipboard PDF stabilization, or Piper single-line normalization) unless explicitly instructed.

Validated document-read cross-app reliability note (fixed baseline):
- External `Read Document` is enabled again for app button, tray command, and global hotkey flows.
- Browser-PDF document retrieval baseline depends on:
  - multi-cycle clipboard capture with PDF-specific stabilization/retry and settle-window upgrade;
  - browser-PDF UI Automation fallback when clipboard copy is blocked (`copySequenceTransitions=0`);
  - document candidate scoring/selection in retrieval orchestration to avoid low-quality preamble candidates.
- Long document speech continuity baseline depends on:
  - Piper continuous chunk stream path (no direct playback path for normal reads);
  - chunk render retry handling for transient `speech_chunk_render_no_clip` events before failing the read.
- Keep document diagnostics in place (for example `clipboard_document_*`, `document_retrieval_*`, `speech_chunk_render_retry_scheduled`, `speech_chunk_continuous_playback_failed`) so regressions are diagnosable.
- Do not re-disable external `Read Document`, remove browser-PDF fallback paths, or remove chunk-render retry/diagnostics unless explicitly instructed and revalidated.

### Global Hotkeys
Global hotkeys are a first-class feature.

Rules:
- implement through a dedicated service
- keep Win32 interop out of view models
- support registration and unregistration cleanly
- do not hardcode hotkeys deep in the UI layer

Likely initial defaults:
- read selected text
- read document
- stop reading

---

## MVP Workflow
Primary intended workflow:
1. user selects text anywhere selectable in Windows
2. user triggers reading
3. app retrieves the text
4. app reads it aloud
5. app reports failure clearly if retrieval is unsupported

Preferred trigger order:
1. global hotkey
2. tray or window command

---

## MVP UX Rules
The app should be:
- lightweight
- fast to open
- utility-focused
- understandable when something fails

For the MVP:
- prefer a small main window over a complex dashboard
- keep code-behind minimal
- bind commands and state through view models
- avoid blocking the UI thread during retrieval or speech setup

Failure UX for early versions should be explicit and simple:
- show a visible status message in the main window
- avoid modal error dialogs for routine retrieval failures
- reserve blocking dialogs for setup or configuration problems
- indicate which fallback path was used when useful

Provide fallback actions instead of dead ends.

---

## Clipboard Fallback Rules
Clipboard fallback is allowed, but it is the last active retrieval strategy before failure.

Rules:
- try non-destructive retrieval first
- preserve the existing clipboard contents where practical
- restore the prior clipboard contents after reading the copied selection
- if restore fails, log it and avoid pretending nothing happened
- do not overwrite the clipboard carelessly
- if clipboard access is unavailable or unsafe, fail gracefully

Agents must remember that clipboard fallback can affect user state and should be treated as sensitive behavior.

---

## Initial Manual Test Matrix
Before calling selected-text retrieval "working", verify the MVP against common targets such as:
- the app's own text input control
- Windows Notepad
- VS Code editor
- a browser text field in Edge or Chrome
- selected text on a browser page in Edge or Chrome

If an app does not expose selection through UI Automation, verify clipboard fallback behavior explicitly.

Do not claim broad compatibility without testing representative targets.

---

## Architecture Rules
The UI layer should orchestrate services, not implement platform details directly.

Use these boundaries:
- UI layer: windows, view models, commands, presentation state
- service layer: reading orchestration, speech abstractions, settings, app logic
- Windows integration layer: UI Automation, Win32 hotkeys, clipboard fallback, focused control inspection

As the codebase grows, it should be easy to split into:
- `RightSpeak`
- `RightSpeak.Core`
- `RightSpeak.Windows`
- `RightSpeak.Tests`

Do not split into separate projects until the boundaries are real and useful.

---

## Final Principle
RightSpeak should remain a reliable Windows reading utility with low-friction behavior.

Optimize for:
- reliability
- simplicity
- maintainability
- clean Windows integration
- clear fallback behavior

Everything else is secondary.
