namespace RightSpeak.Models;

public sealed class AppSettings
{
    public int SpeechRate { get; set; } = 0;
    public string? VoiceName { get; set; }
    public string ReadSelectedHotkeyKey { get; set; } = "R";
    public string ReadTypedTextHotkeyKey { get; set; } = "T";
    public string StopHotkeyKey { get; set; } = "X";
}
