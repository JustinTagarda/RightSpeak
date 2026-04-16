using System.Collections.Generic;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IHotkeySettingsService
{
    IReadOnlyList<string> AvailableKeyOptions { get; }
    string ReadSelectedKey { get; set; }
    string ReadTypedTextKey { get; set; }
    string StopKey { get; set; }

    bool Save();
    HotkeyConfiguration BuildConfiguration();
}
