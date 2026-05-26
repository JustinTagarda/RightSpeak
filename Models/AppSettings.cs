namespace RightSpeak.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = AppThemes.Light;
    public bool AlwaysOnTop { get; set; }
    public int SpeechRate { get; set; } = 0;
    public string? VoiceName { get; set; }
    public string TypedTextDraft { get; set; } = string.Empty;
    public string HotkeyModifierPreset { get; set; } = "AltShift";
    public string ReadSelectedHotkeyKey { get; set; } = "S";
    public string ReadParagraphHotkeyKey { get; set; } = "P";
    public string ReadDocumentHotkeyKey { get; set; } = "D";
    public string StopHotkeyKey { get; set; } = "X";
    public bool PremiumEntitlementVerified { get; set; }
    public string? PremiumEntitlementVerifiedUtc { get; set; }
}
