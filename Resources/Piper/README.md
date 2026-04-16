# Piper Integration

RightSpeak will automatically prefer Piper for speech when it finds a usable
local Piper runtime and at least one voice model.

## Auto-discovery paths

RightSpeak checks these locations for `piper.exe`:

- `%LocalAppData%\RightSpeak\Piper\piper.exe`
- `%LocalAppData%\RightSpeak\Piper\piper\piper.exe`
- `Resources\Piper\piper.exe` relative to the app directory
- `Resources\Piper\piper\piper.exe` relative to the app directory
- `Piper\piper.exe` relative to the app directory
- `Piper\piper\piper.exe` relative to the app directory

RightSpeak checks these locations for voice models:

- `%LocalAppData%\RightSpeak\Piper\voices\`
- `%LocalAppData%\RightSpeak\Piper\`
- `Resources\Piper\voices\` relative to the app directory
- `Resources\Piper\` relative to the app directory
- `Piper\voices\` relative to the app directory
- `Piper\` relative to the app directory

## Required voice files

Each voice must include:

- `voice-name.onnx`
- `voice-name.onnx.json`

Example:

```text
%LocalAppData%\RightSpeak\Piper\
  piper.exe
  voices\
    en_US-lessac-medium.onnx
    en_US-lessac-medium.onnx.json
```

Once these files exist, RightSpeak will surface the voice as
`en US-lessac-medium (Piper)` in the voice picker and will prefer Piper for
`System default` playback.
