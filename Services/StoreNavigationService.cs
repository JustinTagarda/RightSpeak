using System;
using System.Diagnostics;

namespace RightSpeak.Services;

public sealed class StoreNavigationService : IStoreNavigationService
{
    private readonly string _appStoreId;

    public StoreNavigationService(string appStoreId)
    {
        _appStoreId = appStoreId ?? throw new ArgumentNullException(nameof(appStoreId));
    }

    public bool OpenMainStorePage()
    {
        if (string.IsNullOrWhiteSpace(_appStoreId))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"ms-windows-store://pdp/?productid={_appStoreId}",
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
