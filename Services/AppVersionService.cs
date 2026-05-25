using System;

namespace RightSpeak.Services;

public interface IAppVersionService
{
    string GetVersionText();
}

public sealed class AppVersionService : IAppVersionService
{
    private readonly IAppVersionProvider _appVersionProvider;

    public AppVersionService(IAppVersionProvider appVersionProvider)
    {
        _appVersionProvider = appVersionProvider ?? throw new ArgumentNullException(nameof(appVersionProvider));
    }

    public string GetVersionText()
    {
        var raw = _appVersionProvider.InstalledVersion;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "v0.0.0.0";
        }

        return raw.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? raw : $"v{raw}";
    }
}