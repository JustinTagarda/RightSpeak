namespace RightSpeak.Services;

public sealed record StoreUpdateState(
    bool IsSupported,
    bool IsUpdateAvailable,
    bool IsBusy);
