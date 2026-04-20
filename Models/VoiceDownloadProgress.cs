namespace RightSpeak.Models;

public sealed class VoiceDownloadProgress
{
    public VoiceDownloadProgress(string voiceId, string phase, long bytesReceived, long? totalBytes)
    {
        VoiceId = voiceId;
        Phase = phase;
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }

    public string VoiceId { get; }
    public string Phase { get; }
    public long BytesReceived { get; }
    public long? TotalBytes { get; }
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}
