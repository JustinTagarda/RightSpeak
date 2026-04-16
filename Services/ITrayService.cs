using System;

namespace RightSpeak.Services;

public interface ITrayService : IDisposable
{
    event EventHandler? ReadSelectedRequested;
    event EventHandler? ReadParagraphRequested;
    event EventHandler? ReadDocumentRequested;
    event EventHandler? StopRequested;
    event EventHandler? ShowRequested;
    event EventHandler? ExitRequested;
    event EventHandler? ForegroundWindowChanged;

    void Initialize();
    void UpdateHotkeyHints(string modifierLabel, string readSelectedKey, string readParagraphKey, string readDocumentKey, string stopKey);
    bool TryRestoreLastExternalForegroundWindow();
    string CurrentForegroundWindowTitle { get; }
}
