using System;
using System.Collections.Generic;
using System.Linq;
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
            return new PremiumEntitlementState(
                true,
                false,
                false,
                "Development build: Premium gating is disabled.",
                ShouldShowPremiumUi: false);
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
            var licenseEvidence = GetLicenseEvidence(appLicense);
            var ownsFromLicense = PremiumEntitlementResolver.OwnsPremiumFromLicenses(
                licenseEvidence,
                _premiumAddOnStoreId);
            var collectionEvidence = await GetCollectionEvidenceAsync(storeContext, cancellationToken).ConfigureAwait(false);
            var ownsFromCollection = PremiumEntitlementResolver.OwnsPremiumFromCollection(
                collectionEvidence,
                _premiumAddOnStoreId);
            var isOwned = ownsFromLicense || ownsFromCollection;

            AppDiagnostics.Info(
                "premium_entitlement_resolved",
                new Dictionary<string, string?>
                {
                    ["packageFullName"] = TryGetPackageFullName(),
                    ["premiumAddOnStoreId"] = _premiumAddOnStoreId,
                    ["licenseOwned"] = ownsFromLicense.ToString(),
                    ["collectionOwned"] = ownsFromCollection.ToString(),
                    ["licenseSkuStoreIds"] = string.Join(",", licenseEvidence.Select(static item => item.SkuStoreId)),
                    ["collectionStoreIds"] = string.Join(",", collectionEvidence.Select(static item => item.StoreId))
                });

            if (isOwned)
            {
                _premiumEntitlementCache.SaveVerifiedPremiumEntitlement();
                return new PremiumEntitlementState(true, true, false, "Premium is active.");
            }

            _premiumEntitlementCache.ClearVerifiedPremiumEntitlement();
            return new PremiumEntitlementState(
                false,
                true,
                false,
                "Basic mode is active. If you redeemed a Premium promo code, make sure Microsoft Store is signed into the same account, then restore purchases.");
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
                    ["packageFullName"] = TryGetPackageFullName(),
                    ["premiumAddOnStoreId"] = _premiumAddOnStoreId,
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

    private static IReadOnlyList<PremiumAddOnLicenseEvidence> GetLicenseEvidence(StoreAppLicense? appLicense)
    {
        if (appLicense?.AddOnLicenses is null)
        {
            return Array.Empty<PremiumAddOnLicenseEvidence>();
        }

        return appLicense.AddOnLicenses.Values
            .Where(static license => license is not null)
            .Select(static license => new PremiumAddOnLicenseEvidence(
                license.SkuStoreId ?? string.Empty,
                license.IsActive,
                license.InAppOfferToken))
            .ToArray();
    }

    private static async Task<IReadOnlyList<PremiumAddOnCollectionEvidence>> GetCollectionEvidenceAsync(
        StoreContext storeContext,
        CancellationToken cancellationToken)
    {
        var result = await storeContext
            .GetUserCollectionAsync(["Durable"])
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (result.ExtendedError is not null)
        {
            throw result.ExtendedError;
        }

        if (result.Products is null)
        {
            return Array.Empty<PremiumAddOnCollectionEvidence>();
        }

        return result.Products.Values
            .Where(static product => product is not null)
            .Select(static product => new PremiumAddOnCollectionEvidence(
                product.StoreId ?? string.Empty,
                product.InAppOfferToken))
            .ToArray();
    }

    private static string? TryGetPackageFullName()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FullName;
        }
        catch
        {
            return null;
        }
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
