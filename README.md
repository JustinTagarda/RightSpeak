# RightSpeak

RightSpeak is a Windows desktop utility for reading text aloud.

The goal is low-friction text-to-speech for text the user selects in another application, reads from a document, or pastes directly into the app. The app stays Windows-first and utility-focused without turning into a heavy desktop product.

## Status
This repository is in early development.

Current state:
- WPF desktop app is implemented
- project targets `.NET 10` on Windows
- core reading workflows are implemented and being hardened for reliability
- manual text reading, external selected-text reading, external document reading, pause/resume, stop, always-on-top behavior, voice management, tray actions, configurable hotkeys, themes, are implemented
- external `Read Document` is enabled with browser-PDF-specific hardening and diagnostics
- paragraph retrieval code still exists internally, but it is not part of the current production-facing app/tray/global-hotkey surface
- production-facing external commands are:
  - app/tray: `Read Selected Text` and `Read Document`
  - global hotkey: `Read Selected Text`, `Read Document`, and `Stop`

The current focus is getting the MVP path working reliably:
1. local text-to-speech and playback controls
2. selected-text and document retrieval
3. global hotkey trigger
4. basic settings, tray behavior, and window state

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
- Show focused-window context for external reads.

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

### Microsoft Store Packaging
- Use `FULL-BUILD` only for Store package/submission workflows.
- Use Visual Studio 2026 MSBuild only: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`.
- Before Store packaging, enforce the pinned SDK from `D:\Projects\global.json` and align resolver root to that SDK.
- Store packaging output is x64-only and must use a new manifest identity version in `Major.Minor.Build.0` format.
- Follow `D:\Projects\4-MSSTORE-PACKAGE-GENERATION.md` end-to-end for submission-ready package generation.

### Premium Add-on Behavior
- Store model: main app remains free in `Basic`; `Premium` is unlocked by owning a durable Microsoft Store add-on.
- Premium purchase path uses in-app Store purchase (`Windows.Services.Store` + `RequestPurchaseAsync`) through a shared purchase service.
- Entitlement is Store-verified first; verified cache is fallback-only when Store services are unavailable.
- Premium purchase and upgrade dialog flows always run main-window accessibility recovery on exit paths (success/cancel/failure/exception): main window is re-enabled and re-focused.
- Premium purchase calls bind Store owner window handle per invocation (active top-level app window fallback chain) and execute on the app UI dispatcher for Store UI safety.
- Premium-gated hotkey customization now routes blocked Basic-mode attempts through the existing in-app upgrade confirmation dialog and shared premium purchase flow (including Store fallback for unsupported purchase environments).
- Footer status uses bottom-right `AppStatusDisplay`:
- startup: `Basic/Premium` text and `Upgrade` button stay hidden until the first entitlement refresh completes
- owned entitlement: show `Premium` text only
- not owned entitlement: show `Basic` text plus compact `Upgrade` button

### Run
```powershell
dotnet run --project .\RightSpeak.csproj
```

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

## Store Packaging Verification Baseline
- Store packaging runs must follow `D:\Projects\4-MSSTORE-PACKAGE-GENERATION.md` end-to-end.
- Use Visual Studio 2026 MSBuild only: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`.
- Before packaging, verify pinned SDK `10.0.204` from `D:\Projects\global.json` is installed and resolver root points to that SDK band.
- Packaging output must be x64-only and the upload artifact must contain exactly one x64 package.
- Increment `RightSpeak.Package\Package.appxmanifest` identity version for each submission using `Major.Minor.Build.0` with revision `0`.
- Clean stale artifacts before packaging so only the newest `.msixupload` remains as the submission candidate.

## Store Pre-Submission Readiness Record (2026-05-28)
- Instruction baseline re-applied from `D:\Projects\1-MSSTORE-PACKAGE-PREPARATION.md`.
- Packaging build toolchain used: `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`.
- Fixed manifest identity validation:
  - `Name=JustinTagardaSoftware.RightSpeak`
  - `Publisher=CN=68EC506E-4B5E-416B-93E8-BA707CA3BE0F`
  - `Version=1.0.19.0` (`Major.Minor.Build.0`)
  - `TargetDeviceFamily Name=Windows.Desktop`
- Packaging architecture validation: x64-only output verified.
- Final Store upload artifact (`.msixupload`):
  - `D:\Projects\RightSpeak\RightSpeak.Package\bin\x64\Release\AppPackages\RightSpeak.Package_1.0.19.0_x64.msixupload`
- Generated architecture package (`.msix`):
  - `D:\Projects\RightSpeak\RightSpeak.Package\bin\x64\Release\AppPackages\RightSpeak.Package_1.0.19.0_x64_Test\RightSpeak.Package_1.0.19.0_x64.msix`
  - `D:\Projects\RightSpeak\RightSpeak.Package\bin\x64\Release\Upload\RightSpeak.Package_1.0.19.0_x64\RightSpeak.Package_1.0.19.0_x64.msix`

## Microsoft Store Updater Baseline (2026-05-29)
- Instruction baseline re-applied end-to-end from `D:\Projects\3-MSSTORE-UPDATER.md`.
- Updater runs only for packaged runtime with package identity gate first; `Package.Current.SignatureKind` is treated as a diagnostic/supporting signal and not the sole eligibility gate.
- Startup decision flow runs after first main-window render and remains async/non-blocking.
- Startup now attempts Store queue recovery before fresh availability checks so in-progress update operations can be surfaced after restart.
- Store check throttling is enforced across sessions:
  - no more than one `GetAppAndOptionalStorePackageUpdatesAsync()` attempt every 30 minutes
  - no more than ten check attempts in a rolling 24-hour window
  - when throttled, updater uses last-known status and defers fresh Store API calls
- Update availability is surfaced through the compact footer `Update` button beside the version/status controls.
- Update click path reconfirms eligible runtime and requests install through `StoreContext.RequestDownloadAndInstallStorePackageUpdatesAsync(...)`.
- Update progress is shown in a separate modal progress window (not the footer) with phase/state text, determinate progress percent, package detail, and terminal result guidance.
- The `Update` button is shown only after a positive Store availability result (`updates.Count > 0`) or while an active app-update queue item is in progress; throttled/skipped checks do not keep stale visibility.
- No-update path schedules one retry after one hour while the app remains open.
- App shutdown cancels in-flight checks and retry timers through coordinator disposal.
- Updater diagnostics now include package identity/full name/version, signature kind, attempt UTC, throttle skip reason, rolling 24-hour check count, Store result count, and Store API exception/error details.

## Store Package Generation Record (2026-05-29)
- Instruction baseline applied end-to-end from `D:\Projects\4-MSSTORE-PACKAGE-GENERATION.md`.
- Submission packaging mode: FULL-BUILD via Visual Studio 2026 MSBuild only.
- Pinned SDK verification source: `D:\Projects\global.json` -> `10.0.204` (`rollForward=disable`).
- Expected upload artifact shape: single x64 `.msixupload` (non-bundle).
- Manifest submission version advanced to `1.0.21.0` (`Major.Minor.Build.0`, revision `0`).

## Store Package Generation Record (2026-05-29, rerun)
- Instruction baseline re-applied end-to-end from `D:\Projects\4-MSSTORE-PACKAGE-GENERATION.md`.
- Submission packaging mode: FULL-BUILD via Visual Studio 2026 MSBuild only.
- Pinned SDK verification source: `D:\Projects\global.json` -> `10.0.204` (`rollForward=disable`).
- Expected upload artifact shape: single x64 `.msixupload` (non-bundle).
- Manifest submission version advanced to `1.0.22.0` (`Major.Minor.Build.0`, revision `0`).




## Premium Add-ons Re-Application Record (2026-05-29)
- Instruction baseline re-applied end-to-end from `D:\Projects\2-MSSTORE-PREMIUM-ADDONS.md`.
- Premium footer `Upgrade` CTA now always shows the existing in-app confirmation dialog before any purchase call.
- Footer `Upgrade` button default styling is explicit compact blue (`#0078D4` with border `#106EBE`) with white text and no icon.
- Premium CTAs continue to use shared in-app purchase service path (`Windows.Services.Store` + `RequestPurchaseAsync`) with entitlement refresh on `Succeeded`/`AlreadyOwned`.


## Store Package Generation Record (2026-05-29, 4-MSSTORE end-to-end)
- Instruction baseline applied end-to-end from `D:\Projects\4-MSSTORE-PACKAGE-GENERATION.md`.
- Submission packaging mode: FULL-BUILD via Visual Studio 2026 MSBuild only.
- Pinned SDK verification source: `D:\Projects\global.json` -> `10.0.204` (`rollForward=disable`).
- Manifest `RightSpeak.Package\Package.appxmanifest` identity version advanced to `1.0.23.0` (`Major.Minor.Build.0`, revision `0`).
- Packaging target remains x64-only with single-architecture upload artifact verification required.
