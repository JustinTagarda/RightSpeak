namespace RightSpeak.Models;

public enum AppUpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    Installing,
    Completed,
    Deferred,
    Failed
}
