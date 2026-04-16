# Browser Integration Troubleshooting

Use this guide when browser context-menu read is not working.

## Quick checks
1. Ensure RightSpeak app is running.
2. Ensure extension is enabled and loaded from `Resources/BrowserIntegration/Extension`.
3. Ensure native host is installed:
```powershell
.\Resources\BrowserIntegration\Install-BrowserExtensionIntegration.ps1 -ExtensionId "<extension-id>" -RunBridgeTest
```

## Common errors
### "Error when communicating with the native messaging host"
- Cause:
  - Native host manifest missing or invalid.
  - Host path points to old/nonexistent executable.
- Fix:
  1. Rebuild solution.
  2. Rerun install script with the current extension ID.
  3. Reload extension page.

### "Access to the specified native messaging host is forbidden"
- Cause:
  - Extension ID not listed in manifest `allowed_origins`.
- Fix:
  1. Get current extension ID from Chrome/Edge extensions page.
  2. Rerun install script with this ID.
  3. If using both Chrome and Edge extensions, add second ID with `-AdditionalExtensionIds`.

### Context menu click does nothing
- Cause:
  - App not running, or pipe bridge unavailable.
- Fix:
  1. Start RightSpeak manually.
  2. Run:
```powershell
.\Resources\BrowserIntegration\Test-BrowserIntegration.ps1
```
  3. Check app status area and `%LocalAppData%\RightSpeak\logs\rightspeak.log`.

## Verify manifest registration
Expected registry keys:
- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.rightspeak.bridge`
- `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.rightspeak.bridge`

Both should point to:
- `%LocalAppData%\RightSpeak\NativeHost\com.rightspeak.bridge.json`

## Notes
- Browser context menu integration is browser-specific and not a universal Windows context menu.
- Extension IDs may change when reloading unpacked extensions; rerun install when ID changes.
