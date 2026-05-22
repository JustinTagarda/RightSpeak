namespace RightSpeak.Services;

public enum UserInitiatedUpdateAvailability
{
    Available,
    InstalledOrQueued,
    NotAvailable,
    Unavailable,
    Canceled,
    Failed
}

public sealed record UserInitiatedUpdateResult(UserInitiatedUpdateAvailability Availability, string Message);

public sealed record DeferredInstallAttemptResult(
    bool Attempted,
    bool Succeeded,
    bool TimedOut,
    string Message);
