# RightSpeak

RightSpeak is a Windows desktop utility for reading selected text aloud.

The goal is low-friction text-to-speech for text the user has already selected in another application. The long-term direction is a lightweight Windows-first tool that can expand from selected-text reading into paragraph, control, and document reading without turning into a heavy desktop app.

## Status
This repository is in early development.

Current state:
- WPF app scaffold exists
- project targets `.NET 10` on Windows
- core reading workflows are implemented and being hardened for reliability
- external `Read Document` is enabled with browser-PDF-specific hardening and diagnostics

The current focus is getting the MVP path working reliably:
1. local text-to-speech
2. selected-text retrieval
3. global hotkey trigger
4. basic settings and tray behavior

## Product Goal
RightSpeak is intended to:
- retrieve selected text from other Windows applications
- read that text aloud with minimal steps
- fail clearly when retrieval is unsupported
- stay lightweight and maintainable

Reliability matters more than UI polish.

## Planned Capabilities
Target capabilities include:
- read selected text
- stop or pause reading
- choose voice and speech rate
- read a paragraph when the target app exposes enough structure
- read the current control or document content where possible
- trigger reading from a global hotkey
- expose quick actions from the tray

Context-menu integration may be added where practical, but it is not a universal Windows capability and should not be treated as the primary path.

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
- fragile claims about universal context-menu support
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

## Current Implementation
- local speech engine abstraction with Windows OneCore, `System.Speech`, and Piper support
- manual text input with `Read` and `Stop`
- selected-text reading pipeline with UI Automation, focused-control, and clipboard fallback stages
- paragraph reading (first path via UI Automation paragraph expansion from focused selection/caret)
- document reading (first path via focused-control UI Automation document or value patterns)
- document browser-PDF fallback hardening:
  - multi-cycle clipboard capture and settle-window upgrade
  - UI Automation fallback when browser PDF copy is blocked
  - retrieval candidate scoring to reduce viewer-UI preamble drift
- chunked/continuous speech stream orchestration for longer reads
- chunk-render retry path for transient long-read continuation misses in pinned-engine chunk streams
- Piper startup-clipping mitigation baseline:
  - short and long Piper reads both use continuous stream playback
  - single-chunk Piper reads are routed through the same stream path as multi-chunk reads
  - direct Piper `SoundPlayer` playback is not the normal read path
- speech diagnostics include stream and engine routing events (for example `speech_single_chunk_stream_routed`, `speech_chunk_stream_*`, `piper_continuous_playback_*`)
- global hotkeys:
  - `Ctrl+Shift+R` read selected text
  - `Ctrl+Shift+T` read typed text from the app input box
  - `Ctrl+Shift+X` stop reading
  - keys are configurable in-app (modifier stays `Ctrl+Shift`)
- tray quick actions:
  - read typed text
  - read selected text
  - read paragraph
  - read document
  - stop reading
  - typed/selected/stop items show current hotkey hints
- browser context-menu integration for Chrome/Edge through an unpacked extension plus native messaging bridge

## Browser Context Menu Setup
Browser context-menu support is implemented only where specifically supported. It is currently provided through a Chrome/Edge extension, not as a universal Windows context menu.

Files:
- `Resources/BrowserIntegration/Extension`
- `Resources/BrowserIntegration/Install-BrowserExtensionIntegration.ps1`

Development setup:
1. Build the solution.
2. Load the unpacked extension from `Resources/BrowserIntegration/Extension`.
3. Copy the extension ID from Chrome or Edge.
4. Run:

```powershell
.\Resources\BrowserIntegration\Install-BrowserExtensionIntegration.ps1 -ExtensionId "<extension-id>"
```

If you use both Chrome and Edge unpacked extensions:

```powershell
.\Resources\BrowserIntegration\Install-BrowserExtensionIntegration.ps1 -ExtensionId "<chrome-id>" -AdditionalExtensionIds "<edge-id>" -RunBridgeTest
```

5. Start RightSpeak.
6. In Chrome or Edge, select text, right-click, and choose `Read with RightSpeak`.

Bridge verification:

```powershell
.\Resources\BrowserIntegration\Test-BrowserIntegration.ps1
```

Troubleshooting:
- [Browser Integration Troubleshooting](./Resources/BrowserIntegration/TROUBLESHOOTING.md)

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
