# AGENTS.md

## Project Purpose
RightSpeak is a Windows desktop utility built with WPF and .NET 10.
Its core job is simple:
- let the user select text in another Windows application
- retrieve that text reliably
- read it aloud with minimal friction

Planned expansion may include:
- paragraph reading
- full control or document reading
- global hotkeys
- tray behavior
- voice and speed settings
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
The current repository is an early single-project WPF app.

Until the codebase clearly needs more separation:
- keep the solution as one project
- organize code so it can split later without a rewrite
- do not introduce multi-project churn prematurely

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
1. basic app shell
2. local text-to-speech from app-provided text
3. selected-text retrieval pipeline
4. global hotkey
5. settings and tray behavior

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
- stop or pause support
- queueing
- future engine replacement

Do not add cloud speech or AI dependencies for MVP TTS.

Validated reliability note:
- The current local speech path includes a leading-silence mitigation in the Windows speech service because some Windows systems clip the beginning of each utterance.
- Do not remove or reduce that mitigation without rerunning manual typed-text playback validation against repeated reads and confirming the opening words remain audible.

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

Validated paragraph-read reliability notes:
- A confirmed regression occurred where `Read Paragraph` in Notepad failed with `UI Automation returned an empty paragraph` when only a caret/insertion point existed.
- The fix depends on paragraph retrieval expanding insertion ranges to enclosing units (`Paragraph`, then `Line`) when direct selection text is empty.
- A confirmed regression also occurred where clicking `Read Paragraph`/`Read Document` from the app window shifted focus to RightSpeak and caused retrieval against the wrong window.
- The fix depends on routing these UI-triggered external reads through the same focus-sensitive restore path used by tray reads.
- Do not simplify paragraph retrieval back to raw `GetSelection().GetText(-1)` only, and do not bypass focus restore for UI paragraph/document triggers without rerunning manual Notepad tests for tray and app-button paths.

### Global Hotkeys
Global hotkeys are a first-class feature.

Rules:
- implement through a dedicated service
- keep Win32 interop out of view models
- support registration and unregistration cleanly
- do not hardcode hotkeys deep in the UI layer

Likely initial defaults:
- read selected text
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
3. context-menu integration where specifically supported

Do not present right-click integration as universal. It is not.

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

## Engineering Rules
1. Do not guess. Inspect the actual codebase first.
2. Preserve working behavior.
3. Keep changes scoped.
4. Do not do broad cosmetic rewrites unless requested.
5. Ship complete runnable code, not pseudo-code.
6. Prefer explicit names and simple flow.
7. Avoid premature abstraction.
8. Add comments only when behavior is non-obvious, Windows-specific, or error-prone.

Code style defaults:
- prefer readability over cleverness
- use small focused methods
- keep nullable reference types enabled
- use `async` and `await` correctly
- do not block the UI thread
- avoid `async void` except real event handlers
- do not swallow exceptions silently

---

## Dependency Rules
Keep the app lean.

Before adding a package, ask:
1. is it truly needed
2. can built-in .NET or Windows functionality do the job
3. does it create long-term maintenance cost

Do not add heavy frameworks for MVVM, DI, messaging, or logging unless there is a clear project need.

---

## Logging and Diagnostics
Use minimal structured logging where it helps diagnose:
- selection retrieval failures
- missing UI Automation patterns
- clipboard fallback usage
- speech engine initialization failures
- hotkey registration failures

Do not spam logs during normal interaction.
If logging is added, prefer a clear abstraction over scattered debug output.

Current stabilization rule:
- Keep the current reduced JSON diagnostics in place while non-production bug fixing is ongoing.
- Do not fully remove diagnostics until all known bugs are fixed and the build is explicitly being prepared for production shipment.

---

## Testing Guidance
Unit test what is practical.

Good candidates:
- speech request composition
- settings validation
- orchestration logic
- fallback decision flow
- queue or history logic if added

Do not over-promise unit coverage for deep Windows integration.
Keep Windows-specific code isolated enough for manual testing and targeted integration testing.

---

## Task Guidance For Agents

### When starting work
1. read the relevant files first
2. understand the current behavior
3. choose the smallest correct change
4. implement the full solution
5. verify side effects

### When fixing a bug
1. identify the real cause
2. avoid speculative fixes
3. explain the actual issue briefly
4. apply the smallest complete correction
5. watch for regressions

### When adding a feature
1. fit it into the current architecture
2. avoid shortcuts that block future growth
3. keep Windows-specific behavior isolated
4. preserve MVP simplicity
5. add extension points only when justified

### When improving UI
- keep the interface lightweight
- prefer utility over visual flourish
- keep keyboard accessibility where practical
- do not redesign the entire application unless asked

---

## What To Avoid
- do not migrate away from WPF without request
- do not hardwire Windows interop into view models
- do not assume right-click integration works everywhere
- do not overwrite the clipboard carelessly
- do not add cloud AI dependencies for core speech
- do not introduce broad architectural churn
- do not rewrite unrelated files
- do not remove code or comments unless you are confident they are obsolete

---

## Definition Of Done
A task is not done unless:
- the code builds
- the change fits the project structure
- there is no obvious dead code left behind
- user-facing behavior is sensible
- failure cases are handled reasonably
- the implementation remains maintainable

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
