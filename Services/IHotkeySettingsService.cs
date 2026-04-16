using System.Collections.Generic;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IHotkeySettingsService
{
    IReadOnlyList<string> AvailableKeyOptions { get; }
    HotkeyModifierPreset ModifierPreset { get; set; }
    string ReadSelectedKey { get; set; }
    string ReadParagraphKey { get; set; }
    string ReadDocumentKey { get; set; }
    string StopKey { get; set; }

    bool Save();
    HotkeyConfiguration BuildConfiguration();
}
