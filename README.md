# RightSpeak

RightSpeak is a Windows desktop utility for reading text aloud.

The goal is low-friction text-to-speech for text the user selects in another application, reads from a document, or pastes directly into the app. The app stays Windows-first and utility-focused without turning into a heavy desktop product.

## Status
This repository is in early development.

Current state:
- WPF app scaffold exists
- project targets `.NET 10` on Windows
- core reading workflows are implemented and being hardened for reliability
- manual text reading, paragraph reading, selected-text reading, document reading, pause/resume, always-on-top window behavior, voice management, tray actions, hotkeys, themes, and background Store updates are implemented
- external `Read Document` is enabled with browser-PDF-specific hardening and diagnostics
- production-facing external commands are:
  - app/tray: `Read Selected Text` and `Read Document`
  - global hotkey: `Read Selected Text`, `Read Paragraph`, `Read Document`, and `Stop`

The current focus is getting the MVP path working reliably:
1. local text-to-speech and playback controls
2. selected-text, paragraph, and document retrieval
3. global hotkey trigger
4. basic settings, tray behavior, and window state

## Product Goal
RightSpeak is intended to:
- retrieve selected text from other Windows applications
- read that text aloud with minimal steps
- fail clearly when retrieval is unsupported
- stay lightweight and maintainable

Reliability matters more than UI polish.

## Planned Capabilities
Target capabilities include:
- read paragraph text
- read selected text
- pause and resume reading
- stop reading
- choose voice and speech rate
- read the current control or document content where possible
- trigger reading from a global hotkey
- expose quick actions from the tray
- keep the app on top when needed

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
dotnet build .\RightSpeak.slnx
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

Create a Store upload package from the command line:
```powershell
$env:DOTNET_ROOT='C:\Program Files\dotnet'
$env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR='C:\Program Files\dotnet'
$env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR='C:\Program Files\dotnet\sdk\10.0.202\Sdks'
$env:DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER='10.0.202'
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' .\RightSpeak.Package\RightSpeak.Package.wapproj /restore /p:Configuration=Release /p:Platform=x64 /p:UapAppxPackageBuildMode=StoreUpload
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' .\RightSpeak.Package\RightSpeak.Package.wapproj /restore /p:Configuration=Release /p:Platform=ARM64 /p:UapAppxPackageBuildMode=StoreUpload
```

Expected output:
- `.msixupload` or `.appxupload` under `RightSpeak.Package\AppPackages\`
- install/test artifacts for local validation in the same output folder
- build `Platform=x64` for an x64-only upload package
- build `Platform=ARM64` for an ARM64-only upload package
- upload the architecture-specific files, not any older `_x64_ARM64_bundle.msixupload` or `_bundle.msixupload` artifacts

Runtime update behavior for packaged installs:
- RightSpeak checks Store updates asynchronously after startup
- silent background download/install is attempted only when Store automatic app updates are allowed
- update progress is shown in the footer center and on the Windows taskbar button
- the footer-right version text shows the installed packaged version when the app has package identity

## Current Implementation
- local speech engine abstraction with Windows OneCore, `System.Speech`, and Piper support
- manual text input with `Read`, `Pause/Resume`, and `Stop`
- paragraph reading pipeline with UI Automation, focused-control, and clipboard fallback stages
- selected-text reading pipeline with UI Automation, focused-control, and clipboard fallback stages
- document reading (first path via focused-control UI Automation document or value patterns)
- document browser-PDF fallback hardening:
  - multi-cycle clipboard capture and settle-window upgrade
  - UI Automation fallback when browser PDF copy is blocked
  - retrieval candidate scoring to reduce viewer-UI preamble drift
  - strict command-scope behavior for document flow (no selected-text downgrade)
  - webpage main-context extraction path for conversation-like pages before generic document fallbacks
- chunked/continuous speech stream orchestration for longer reads
- chunk-render retry path for transient long-read continuation misses in pinned-engine chunk streams
- Piper startup-clipping mitigation baseline:
  - short and long Piper reads both use continuous stream playback
  - single-chunk Piper reads are routed through the same stream path as multi-chunk reads
  - direct Piper `SoundPlayer` playback is not the normal read path
- Piper voice management with downloadable voice models and runtime installation
- voice selection, speech-rate control, and voice preview
- theme switching with light, dark, and Windows settings support
- always-on-top window behavior
- speech diagnostics include stream and engine routing events (for example `speech_single_chunk_stream_routed`, `speech_chunk_stream_*`, `piper_continuous_playback_*`)
- global hotkeys:
  - read selected text
  - read paragraph
  - read document
  - stop reading
  - modifier and keys are configurable in-app (`Alt+Shift`, `Ctrl+Shift`, `Ctrl+Alt`)
- tray quick actions:
  - read selected text
  - read document
  - stop reading
  - show app / exit
  - menu items display current hotkey hints
- background Microsoft Store update checks with footer and taskbar progress
- packaged version display in the footer for Store installs

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
