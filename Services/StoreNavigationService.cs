using System;
using System.Diagnostics;

namespace RightSpeak.Services;

public sealed class StoreNavigationService : IStoreNavigationService
{
    private readonly string _appStoreId;
    private readonly string _premiumAddOnStoreId;

    public StoreNavigationService(string appStoreId, string premiumAddOnStoreId)
    {
        _appStoreId = appStoreId ?? throw new ArgumentNullException(nameof(appStoreId));
        _premiumAddOnStoreId = premiumAddOnStoreId ?? throw new ArgumentNullException(nameof(premiumAddOnStoreId));
    }

    public bool OpenMainStorePage()
    {
        return OpenStoreProductPage(_appStoreId);
    }

    public bool OpenPremiumAddOnPage()
    {
        return OpenStoreProductPage(_premiumAddOnStoreId);
    }

    private static bool OpenStoreProductPage(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"ms-windows-store://pdp/?productid={productId}",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_navigation_failed",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return false;
        }
    }
}
