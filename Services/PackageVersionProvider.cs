using System;
using System.Reflection;

namespace RightSpeak.Services;

public sealed class PackageVersionProvider : IAppVersionProvider
{
    private readonly string _installedVersion;

    public PackageVersionProvider()
    {
        IsPackaged = false;
        _installedVersion = GetAssemblyVersion();
    }

    internal PackageVersionProvider(bool isPackaged, string installedVersion)
    {
        IsPackaged = isPackaged;
        _installedVersion = string.IsNullOrWhiteSpace(installedVersion)
            ? "0.0.0.0"
            : installedVersion;
    }

    public bool IsPackaged { get; }

    public string InstalledVersion => _installedVersion;

    public string GetDisplayVersionText()
    {
        return $"v{_installedVersion}";
    }

    private static string GetAssemblyVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null)
        {
            return "0.0.0.0";
        }

        if (version.Revision >= 0)
        {
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{version.Revision}";
        }

        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}