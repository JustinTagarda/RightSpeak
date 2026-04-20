namespace RightSpeak.Models;

public sealed class VoiceInstallResult
{
    private VoiceInstallResult(bool success, bool wasCancelled, string message)
    {
        Success = success;
        WasCancelled = wasCancelled;
        Message = message;
    }

    public bool Success { get; }
    public bool WasCancelled { get; }
    public string Message { get; }

    public static VoiceInstallResult Completed(string message)
    {
        return new VoiceInstallResult(true, false, message);
    }

    public static VoiceInstallResult Failed(string message)
    {
        return new VoiceInstallResult(false, false, message);
    }

    public static VoiceInstallResult Cancelled()
    {
        return new VoiceInstallResult(false, true, "Download cancelled.");
    }
}
