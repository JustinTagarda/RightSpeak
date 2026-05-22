using System;
using System.Diagnostics;
using Windows.ApplicationModel;

namespace RightSpeak.Services.Store;

public sealed class StoreNavigationService : IStoreNavigationService
{
    private readonly string _premiumStoreId;

    public StoreNavigationService(string premiumStoreId)
    {
        _premiumStoreId = premiumStoreId ?? throw new ArgumentNullException(nameof(premiumStoreId));
    }

    public bool OpenPremiumPage()
    {
        return OpenUri($"ms-windows-store://pdp/?productid={_premiumStoreId}");
    }

    public bool OpenAppPage()
    {
        var packageFamilyName = TryGetPackageFamilyName();
        if (!string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return OpenUri($"ms-windows-store://pdp/?PFN={packageFamilyName}");
        }

        return false;
    }

    private static bool OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetPackageFamilyName()
    {
        try
        {
            return Package.Current.Id.FamilyName;
        }
        catch
        {
            return null;
        }
    }
}

