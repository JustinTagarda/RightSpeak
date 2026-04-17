namespace RightSpeak.Models;

public sealed class SpeechOptions
{
    public string? VoiceName { get; init; }
    public int Rate { get; init; } = 0;
    public double? LeadingPrimerSecondsOverride { get; init; }
    public bool IsContinuationChunk { get; init; }
    public bool UseFullPrimerWarmupCarrier { get; init; } = true;
    public bool AllowOutputDeviceWarmup { get; init; } = true;
}
