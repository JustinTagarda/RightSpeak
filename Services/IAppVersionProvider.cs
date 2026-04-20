namespace RightSpeak.Services;

public interface IAppVersionProvider
{
    bool IsPackaged { get; }
    string InstalledVersion { get; }
    string GetDisplayVersionText();
}
