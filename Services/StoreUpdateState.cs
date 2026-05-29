namespace RightSpeak.Services;

public sealed record StoreUpdateState(
    bool IsSupported,
    bool IsUpdateAvailable,
    bool IsBusy,
    bool IsProgressVisible = false,
    int ProgressPercent = 0,
    string ProgressPhase = "",
    string? ProgressDetail = null,
    string? ProgressResult = null);
