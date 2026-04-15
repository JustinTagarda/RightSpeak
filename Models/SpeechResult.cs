namespace RightSpeak.Models;

public sealed class SpeechResult
{
    private SpeechResult(bool success, bool wasCancelled, string message)
    {
        Success = success;
        WasCancelled = wasCancelled;
        Message = message;
    }

    public bool Success { get; }

    public bool WasCancelled { get; }

    public string Message { get; }

    public static SpeechResult Completed(string message = "Reading completed.")
    {
        return new SpeechResult(true, false, message);
    }

    public static SpeechResult Stopped(string message = "Reading stopped.")
    {
        return new SpeechResult(true, true, message);
    }

    public static SpeechResult Failed(string message)
    {
        return new SpeechResult(false, false, message);
    }
}
