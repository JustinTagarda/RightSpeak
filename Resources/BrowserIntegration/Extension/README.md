# RightSpeak Browser Extension

This is an unpacked Chrome/Edge extension that adds a `Read with RightSpeak` context menu item for selected text.

## Files

- `manifest.json`
- `service-worker.js`

## Install (development)

1. Build RightSpeak so the executable exists.
   Build the solution so both the WPF app and the native host are produced.
2. Load this extension as unpacked:
   - Chrome: `chrome://extensions`
   - Edge: `edge://extensions`
3. Enable developer mode.
4. Click `Load unpacked` and select this `Extension` folder.
5. Copy the loaded extension ID.
6. Install browser integration from the `Resources\BrowserIntegration` folder:

```powershell
.\Install-BrowserExtensionIntegration.ps1 -ExtensionId "<extension-id>"
```

If you use both Chrome and Edge unpacked extensions:

```powershell
.\Install-BrowserExtensionIntegration.ps1 -ExtensionId "<chrome-id>" -AdditionalExtensionIds "<edge-id>" -RunBridgeTest
```

7. Start RightSpeak.
8. Select text in the browser, right-click, and choose `Read with RightSpeak`.

## Verification

You can verify the native bridge before testing in the browser:

```powershell
.\Test-BrowserIntegration.ps1
```

This checks both:
- `RightSpeak.exe --send-text`
- `RightSpeak.exe --native-host`

## Manual install alternative

If you want to register the native host manually instead of using the helper wrapper:

```powershell
.\Install-NativeHost.ps1 -HostExecutablePath "D:\Projects\RightSpeak\RightSpeak.NativeHost\bin\Debug\net10.0-windows\RightSpeak.NativeHost.exe" -ExtensionOrigin "chrome-extension://<extension-id>/"
```

## Notes

- The same extension package works in Edge because Edge supports the Chrome extension format. The native host install script registers both Chrome and Edge registry keys.
- If the extension is reloaded and gets a different ID, rerun the install script with the new origin.
- Native messaging launches `RightSpeak.NativeHost.exe`, which forwards the text to the running WPF app through the local named pipe.
- Troubleshooting guide: [`..\TROUBLESHOOTING.md`](..\TROUBLESHOOTING.md)
