using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace RightSpeak.Services;

public sealed class PremiumEntitlementService : IPremiumEntitlementService
{
    private readonly IStoreContextProvider _storeContextProvider;
    private readonly IPremiumEntitlementCache _premiumEntitlementCache;
    private readonly string _premiumAddOnStoreId;

    public PremiumEntitlementService(
        IStoreContextProvider storeContextProvider,
        IPremiumEntitlementCache premiumEntitlementCache,
        string premiumAddOnStoreId)
    {
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
        _premiumEntitlementCache = premiumEntitlementCache ?? throw new ArgumentNullException(nameof(premiumEntitlementCache));
        _premiumAddOnStoreId = premiumAddOnStoreId ?? throw new ArgumentNullException(nameof(premiumAddOnStoreId));
    }

    public async Task<PremiumEntitlementState> RefreshEntitlementAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPackagedProcess())
        {
            _premiumEntitlementCache.ClearVerifiedPremiumEntitlement();
            return new PremiumEntitlementState(false, false, false, "Basic mode (Store package identity not found).");
        }

        var storeContext = _storeContextProvider.TryGetDefaultContext();
        if (storeContext is null)
        {
            return UseCacheFallback("Store unavailable right now.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appLicense = await storeContext.GetAppLicenseAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var isOwned = IsPremiumOwnedByLicense(appLicense, _premiumAddOnStoreId);
            if (isOwned)
            {
                _premiumEntitlementCache.SaveVerifiedPremiumEntitlement();
                return new PremiumEntitlementState(true, true, false, "Premium is active.");
            }

            _premiumEntitlementCache.ClearVerifiedPremiumEntitlement();
            return new PremiumEntitlementState(false, true, false, "Basic mode is active.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "premium_entitlement_refresh_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return UseCacheFallback("Couldn't reach Store services. Showing last verified mode.");
        }
    }

    private PremiumEntitlementState UseCacheFallback(string message)
    {
        if (_premiumEntitlementCache.HasVerifiedPremiumEntitlement())
        {
            return new PremiumEntitlementState(true, false, true, $"{message} Premium is enabled from verified cache.");
        }

        return new PremiumEntitlementState(false, false, true, $"{message} Basic mode is enabled.");
    }

    private static bool IsPremiumOwnedByLicense(StoreAppLicense? appLicense, string addOnStoreId)
    {
        if (appLicense?.AddOnLicenses is null || string.IsNullOrWhiteSpace(addOnStoreId))
        {
            return false;
        }

        if (!appLicense.AddOnLicenses.TryGetValue(addOnStoreId, out var addOnLicense))
        {
            return false;
        }

        return addOnLicense.IsActive;
    }

    private static bool IsPackagedProcess()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
