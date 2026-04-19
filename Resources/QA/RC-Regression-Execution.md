# RightSpeak RC Regression Execution

Execution date: `2026-04-16`
Owner: `Codex + user validation`

Manual execution guide:
- `Resources/QA/RC-Manual-Runbook.md`

Status values:
- `Pass`
- `Fail`
- `Blocked`
- `Not Run`

## Automated Checks
Recommended one-command smoke run:
```powershell
.\Resources\QA\Run-RC-Smoke.ps1
```

Recommended manual execution capture:
```powershell
.\Resources\QA\Run-RC-ManualChecklist.ps1
```

| Check | Command | Status | Notes |
|---|---|---|---|
| Build (verify output path) | `dotnet build .\RightSpeak.csproj -p:OutputPath=bin\Debug\net10.0-windows-verify\` | Pass | 0 warnings, 0 errors |

## Retrieval Matrix
| Target App | Action | Status | Notes |
|---|---|---|---|
| RightSpeak input box | Read typed text | Not Run | Manual validation required |
| RightSpeak input box | Read selected text | Not Run | Manual validation required |
| Windows Notepad | Read selected text | Not Run | Manual validation required |
| Windows Notepad | Read paragraph | Not Run | Manual validation required |
| Windows Notepad | Read document | Not Run | Manual validation required |
| VS Code editor | Read selected text | Not Run | Manual validation required |
| VS Code editor | Read paragraph | Not Run | Manual validation required |
| VS Code editor | Read document | Not Run | Manual validation required |
| Edge/Chrome text field | Read selected text | Not Run | Manual validation required |
| Edge/Chrome text field | Read paragraph | Not Run | Manual validation required |
| Edge/Chrome page selection | Read selected text | Not Run | Manual validation required |

## Hotkey/Tray Robustness
| Scenario | Status | Notes |
|---|---|---|
| Startup hotkey registration | Not Run | Manual validation required |
| Apply new hotkeys in window | Not Run | Confirm status text + functionality |
| Tray labels reflect hotkeys | Not Run | Confirm after apply |
| Hide to tray then restore | Not Run | Confirm command behavior remains intact |
| Single-instance activation | Not Run | Launch second instance and verify first activates |

## Settings Robustness
| Scenario | Status | Notes |
|---|---|---|
| Save voice/rate/hotkeys and restart | Not Run | Manual validation required |
| Missing settings file | Not Run | Should recreate defaults |
| Malformed settings file recovery | Not Run | Should backup `*.corrupt` and continue |

## Open P0/P1 Bugs
- None recorded in this file yet.
- Update this section with issue IDs once discovered.

## Release Gate Summary
- Automated gate: `Pass`
- Manual gate: `Blocked` until matrix above is executed.
