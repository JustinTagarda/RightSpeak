using System;

namespace RightSpeak.Services;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? ReadSelectedHotkeyPressed;
    event EventHandler? ReadTypedTextHotkeyPressed;
    event EventHandler? StopHotkeyPressed;
    string LastRegistrationStatus { get; }

    bool RegisterHotkeys(nint windowHandle);
    bool RefreshHotkeys();

    void UnregisterHotkeys();
}
