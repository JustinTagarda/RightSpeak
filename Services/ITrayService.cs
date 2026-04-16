using System;

namespace RightSpeak.Services;

public interface ITrayService : IDisposable
{
    event EventHandler? ReadTypedTextRequested;
    event EventHandler? ReadSelectedRequested;
    event EventHandler? ReadParagraphRequested;
    event EventHandler? ReadDocumentRequested;
    event EventHandler? StopRequested;
    event EventHandler? ShowRequested;
    event EventHandler? ExitRequested;

    void Initialize();
    void UpdateHotkeyHints(string readSelectedKey, string readTypedTextKey, string stopKey);
    bool TryRestoreLastExternalForegroundWindow();
}
