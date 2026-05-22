using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;

namespace RightSpeak.Services.Store;

public sealed class PremiumEntitlementCache : IPremiumEntitlementCache
{
    private readonly string _cacheFilePath;

    public PremiumEntitlementCache(string? cacheFilePath = null)
    {
        _cacheFilePath = cacheFilePath ?? ResolveDefaultPath();
    }

    public void SaveVerifiedPremium(DateTimeOffset verifiedUtc)
    {
        var directoryPath = Path.GetDirectoryName(_cacheFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var payload = Encoding.UTF8.GetBytes(verifiedUtc.ToUniversalTime().ToString("O"));
        var protectedPayload = ProtectedData.Protect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_cacheFilePath, protectedPayload);
    }

    public void Clear()
    {
        if (File.Exists(_cacheFilePath))
        {
            File.Delete(_cacheFilePath);
        }
    }

    public DateTimeOffset? TryGetLastVerifiedPremiumUtc()
    {
        if (!File.Exists(_cacheFilePath))
        {
            return null;
        }

        try
        {
            var protectedPayload = File.ReadAllBytes(_cacheFilePath);
            var payload = ProtectedData.Unprotect(protectedPayload, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(payload);
            return DateTimeOffset.TryParse(text, out var value) ? value.ToUniversalTime() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveDefaultPath()
    {
        try
        {
            var localFolderPath = ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrWhiteSpace(localFolderPath))
            {
                return Path.Combine(localFolderPath, "premium-cache.dat");
            }
        }
        catch
        {
        }

        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataDirectory, "RightSpeak", "premium-cache.dat");
    }
}
