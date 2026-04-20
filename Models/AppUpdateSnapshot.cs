namespace RightSpeak.Models;

public sealed class AppUpdateSnapshot
{
    public static AppUpdateSnapshot Idle(string installedVersion, string? availableVersion = null)
    {
        return new AppUpdateSnapshot(
            AppUpdateState.Idle,
            string.Empty,
            string.Empty,
            false,
            false,
            0d,
            installedVersion,
            availableVersion);
    }

    public AppUpdateSnapshot(
        AppUpdateState state,
        string stageText,
        string statusMessage,
        bool isMandatoryUpdateAvailable,
        bool isProgressVisible,
        double progressValue,
        string installedVersion,
        string? availableVersion = null)
    {
        State = state;
        StageText = stageText ?? string.Empty;
        StatusMessage = statusMessage ?? string.Empty;
        IsMandatoryUpdateAvailable = isMandatoryUpdateAvailable;
        IsProgressVisible = isProgressVisible;
        ProgressValue = progressValue < 0d ? 0d : progressValue > 1d ? 1d : progressValue;
        InstalledVersion = installedVersion ?? string.Empty;
        AvailableVersion = availableVersion;
    }

    public AppUpdateState State { get; }
    public string StageText { get; }
    public string StatusMessage { get; }
    public bool IsMandatoryUpdateAvailable { get; }
    public bool IsProgressVisible { get; }
    public double ProgressValue { get; }
    public string InstalledVersion { get; }
    public string? AvailableVersion { get; }
}
