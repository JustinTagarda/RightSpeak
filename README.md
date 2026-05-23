# RightSpeak

RightSpeak is a Windows desktop utility for reading text aloud.

The goal is low-friction text-to-speech for text the user selects in another application, reads from a document, or pastes directly into the app. The app stays Windows-first and utility-focused without turning into a heavy desktop product.

## Status
This repository is in early development.

Current state:
- WPF desktop app is implemented
- project targets `.NET 10` on Windows
- core reading workflows are implemented and being hardened for reliability
- manual text reading, external selected-text reading, external document reading, pause/resume, stop, always-on-top behavior, voice management, tray actions, configurable hotkeys, themes, and background Store updates are implemented
- external `Read Document` is enabled with browser-PDF-specific hardening and diagnostics
- paragraph retrieval code still exists internally, but it is not part of the current production-facing app/tray/global-hotkey surface
- production-facing external commands are:
  - app/tray: `Read Selected Text` and `Read Document`
  - global hotkey: `Read Selected Text`, `Read Document`, and `Stop`

The current focus is getting the MVP path working reliably:
1. local text-to-speech and playback controls
2. selected-text and document retrieval
3. global hotkey trigger
4. basic settings, tray behavior, packaged updates, and window state

## Product Goal
RightSpeak is intended to:
- retrieve selected text from other Windows applications
- read that text aloud with minimal steps
- fail clearly when retrieval is unsupported
- stay lightweight and maintainable

Reliability matters more than UI polish.

## Current Features
- Read typed or pasted text directly in the app, with `Read`, `Clear`, `Pause/Resume`, and `Stop`.
- Read selected text from another app through a multi-strategy retrieval pipeline: UI Automation, focused-control selection access, then clipboard fallback.
- Read document text from another app through focused-control document access, clipboard document capture, browser-PDF hardening, and candidate scoring.
- Cancel external reads before speech starts, and pause/resume or stop once speech is active.
- Use `System.Speech` voices or Piper through one speech abstraction with speech-rate control and voice preview.
- Manage downloadable Piper voices with refresh, install, update, remove, language filters, quality filters, cancel, and license confirmation.
- Use tray quick actions for `Read Selected Text`, `Read Document`, `Stop Reading`, `Show RightSpeak`, and `Exit`.
- Configure global hotkeys in-app for `Read Selected Text`, `Read Document`, and `Stop` using `Alt+Shift`, `Ctrl+Shift`, or `Ctrl+Alt`.
- Persist theme, always-on-top, selected voice, speech rate, typed text draft, and hotkey settings.
- Show focused-window context for external reads, a single Premium entitlement source, packaged app version text in the footer, and background Store update handling with deferred install-on-exit and Store fallback UI when packaged.

## Planned Capabilities
Target capabilities still under consideration include:
- a production-facing paragraph-read surface
- reading queue or reading history
- optional AI-assisted features later

## Windows-First Design
RightSpeak is intentionally Windows-specific.

Technical direction:
- WPF UI
- C# on `.NET 10`
- MVVM structure
- Win32 interop for hotkeys and window behavior
- UI Automation for text retrieval
- clipboard fallback when direct retrieval is unavailable
- built-in Windows speech APIs behind a speech abstraction

The app is not being designed around cross-platform constraints at this stage.

## Retrieval Strategy
Text retrieval is the hardest part of the product, and it will not work the same way in every application.

Preferred retrieval order:
1. UI Automation selection or text patterns
2. focused control text retrieval
3. clipboard fallback
4. graceful failure with clear feedback

Clipboard fallback is a last active strategy, not the default. If it is used, the app should preserve and restore the user's clipboard where practical.

## MVP Principles
The MVP should optimize for:
- functional correctness
- reliability across common Windows apps
- low-friction workflow
- maintainability

It should avoid:
- broad architectural churn
- unnecessary dependencies
- hardwiring Windows interop directly into view models

## Getting Started
### Requirements
- Windows 10 or Windows 11
- .NET 10 SDK

### Build
```powershell
dotnet build .\RightSpeak.csproj
```

### Run
```powershell
dotnet run --project .\RightSpeak.csproj
```

## Microsoft Store Packaging
`RightSpeak.Package\RightSpeak.Package.wapproj` is the Windows Application Packaging Project for MSIX and Store submission.

Before creating a real Store upload package:
- verify the `Identity` values in `RightSpeak.Package\Package.appxmanifest` match the identity and publisher from Partner Center association
- keep the WPF app project as the only application payload; the packaging project is deployment-only
- promo codes for the Premium durable add-on are redeemed through the Microsoft Store redeem flow, and RightSpeak picks up the resulting entitlement on startup and after successful in-app Premium purchase checks
- if a Partner Center association export is available for the app, compare it with the manifest and publish profile before submission; this repository does not include the Partner Center export itself

Create a Store upload package from the command line:
```powershell
.\tools\msstore-generate-package.ps1
```

Optional overrides:
```powershell
.\tools\msstore-generate-package.ps1 -SdkVersion 10.0.202
.\tools\msstore-generate-package.ps1 -ManifestVersion 1.0.13.0
```

Expected output:
- `.msixupload` or `.appxupload` under `RightSpeak.Package\AppPackages\`
- install/test artifacts for local validation in the same output folder
- build `Platform=x64` for the only supported upload package
- only x64 Store packaging is configured
- upload the x64 file, not any older bundle artifacts

Runtime update behavior for packaged installs:
- RightSpeak checks Store updates asynchronously after the main window has rendered
- the check stays hidden and does not block startup
- silent background download is attempted first when Store support allows it
- if silent download is unavailable, blocked, canceled, or fails during the automatic startup flow, the app falls back to the Microsoft Store / OS update UI
- clicking the footer version text checks for updates, shows a small "No update available" toast when appropriate, and opens the Microsoft Store app page when an update exists
- the footer status control presents Basic/Premium mode and the clickable version text in one reusable control
- deferred install-on-exit is used when a silent download succeeds
- successful Store fallback UI updates are treated as completed/queued and do not create an app-owned deferred install state
- if `StoreContext` creation is unavailable in a packaged environment, the app degrades update checking safely instead of failing startup
- the footer version text uses the same Store update service for user-initiated checks, while the startup check stays hidden and non-blocking
- exit-time install shows an app-owned modal progress window
- deferred update pending state is cleared after completion, while last-check and retry history are persisted separately
- the app does not auto-restart or force-close the current session to apply a Store update
- the footer-right version text shows the installed packaged version when the app has package identity

## Current Implementation
- local speech engine abstraction with `System.Speech` and Piper support
- manual text input with `Read`, `Pause/Resume`, and `Stop`
- selected-text reading pipeline with UI Automation, focused-control selection-only access, and clipboard fallback stages
- document reading via focused-control UI Automation document/value patterns plus orchestrated document fallback providers
- document browser-PDF fallback hardening:
  - multi-cycle clipboard capture and settle-window upgrade
  - UI Automation fallback when browser PDF copy is blocked
  - retrieval candidate scoring to reduce viewer-UI preamble drift
  - strict command-scope behavior for document flow (no selected-text downgrade)
  - webpage main-context extraction path for conversation-like pages before generic document fallbacks
- external read lifecycle state with explicit `focusing`, `retrieving`, `preparing speech`, and `speaking` phases
- external read cancel-before-speech behavior, with `Cancel` during retrieval/preparation and `Stop` once speech has started
- chunked/continuous speech stream orchestration for longer reads
- chunk-render retry path for transient long-read continuation misses in pinned-engine chunk streams
- Piper startup-clipping mitigation baseline:
  - short and long Piper reads both use continuous stream playback
  - single-chunk Piper reads are routed through the same stream path as multi-chunk reads
  - direct Piper `SoundPlayer` playback is not the normal read path
- Piper voice management with downloadable voice models and runtime installation
- voice selection, speech-rate control, voice preview, and persisted typed-text draft
- theme switching with light, dark, and Windows settings support
- always-on-top window behavior
- speech diagnostics include stream and engine routing events (for example `speech_single_chunk_stream_routed`, `speech_chunk_stream_*`, `piper_continuous_playback_*`)
- global hotkeys:
  - read selected text
  - read document
  - stop reading
  - modifier and keys are configurable in-app (`Alt+Shift`, `Ctrl+Shift`, `Ctrl+Alt`)
- tray quick actions:
  - read selected text
  - read document
  - stop reading
  - show app / exit
  - menu items display current hotkey hints
- background Microsoft Store update checks with deferred install-on-exit and Store fallback UI
- deferred update pending state and update history are persisted separately
- packaged version display in the footer for Store installs
- voice manager filtering by language and quality, with refresh/cancel/install/update/remove actions

## Current Project Shape
The repository currently starts as a single WPF project. As features are added, code should stay organized so it can later split cleanly into dedicated layers for UI, core logic, and Windows-specific integration.

Preferred in-project organization:
- `Views/`
- `ViewModels/`
- `Models/`
- `Services/`
- `WindowsIntegration/`
- `Interop/`
- `Settings/`
- `Resources/`

## Early Test Targets
When selected-text retrieval is implemented, early validation should cover representative Windows apps such as:
- the app's own text input
- Windows Notepad
- VS Code
- browser text fields in Edge or Chrome
- selected page text in Edge or Chrome

Compatibility claims should stay conservative until those paths are working and verified.

## Contribution Notes
Contributors and coding agents should read [`AGENTS.md`](./AGENTS.md) before making changes. That file contains the implementation constraints for architecture, scope control, clipboard handling, and Windows integration.
