# RightSpeak RC Manual Results

Session: 20260416-092539

| Area | Check | Status | Notes |
|---|---|---|---|
| Retrieval Matrix | RightSpeak input box - Read typed text | Pass | Retested after speech-path stabilization; opening words are now read completely. |
| Retrieval Matrix | RightSpeak input box - Read selected text | Pass | fix was validated after adding leading PCM silence. |
| Retrieval Matrix | Windows Notepad - Read selected text | Pass |  |
| Retrieval Matrix | Windows Notepad - Read paragraph | Pass |  |
| Retrieval Matrix | Windows Notepad - Read document | Pass |  |
| Retrieval Matrix | VS Code editor - Read selected text | Pass |  |
| Retrieval Matrix | VS Code editor - Read paragraph | Pass |  |
| Retrieval Matrix | VS Code editor - Read document | Pass |  |
| Retrieval Matrix | Edge/Chrome text field - Read selected text | Pass |  |
| Retrieval Matrix | Edge/Chrome text field - Read paragraph | Pass |  |
| Retrieval Matrix | Edge/Chrome page selection - Read selected text | Pass |  |
| Retrieval Matrix | Edge/Chrome page selection - Context menu read | Pass |  |
| Hotkey/Tray | Startup hotkey registration | Pass |  |
| Hotkey/Tray | Apply new hotkeys in window | Pass |  |
| Hotkey/Tray | Tray labels reflect hotkeys | Pass |  |
| Hotkey/Tray | Hide to tray then restore | Pass |  |
| Hotkey/Tray | Single-instance activation | Pass |  |
| Settings | Save voice/rate/hotkeys and restart | Pass |  |
| Settings | Missing settings file recovery | Pass | App recovered successfully after missing settings file was removed. |
| Settings | Malformed settings file recovery | Pass | App recovered successfully after malformed settings file was introduced. |
| Installer/First-Run | Install browser integration (single ID) | Pass | Browser native host manifest installed successfully for a single extension ID. |
| Installer/First-Run | Install browser integration (Chrome + Edge IDs) | Pass | Browser native host manifest installed successfully for both Chrome and Edge extension IDs. |
| Installer/First-Run | -RunBridgeTest path | Pass | Bridge verification succeeded for `RightSpeak.exe --send-text` and `RightSpeak.NativeHost.exe`. |
| Installer/First-Run | Troubleshooting doc followability | Not Run |  |
| Diagnostics | Success/failure events written to diagnostics log | Not Run |  |

Summary:
- Pass: 20
- Fail: 0
- Blocked: 0
- Not Run: 2
