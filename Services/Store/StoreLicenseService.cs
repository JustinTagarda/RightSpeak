using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace RightSpeak.Services.Store;

public sealed class StoreLicenseService : IStoreLicenseService
{
    private readonly IStoreContextProvider _storeContextProvider;
    private readonly IPremiumEntitlementCache _premiumEntitlementCache;
    private readonly string _premiumProductDisplayName;
    private readonly string[] _premiumStoreIds;
    private readonly string[] _premiumProductIds;
    private readonly TimeSpan _premiumGraceWindow;

    public StoreLicenseService(
        IStoreContextProvider storeContextProvider,
        IPremiumEntitlementCache premiumEntitlementCache,
        string premiumProductDisplayName,
        IReadOnlyList<string> premiumStoreIds,
        IReadOnlyList<string> premiumProductIds,
        TimeSpan? premiumGraceWindow = null)
    {
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
        _premiumEntitlementCache = premiumEntitlementCache ?? throw new ArgumentNullException(nameof(premiumEntitlementCache));
        _premiumProductDisplayName = string.IsNullOrWhiteSpace(premiumProductDisplayName) ? "Premium" : premiumProductDisplayName;
        _premiumStoreIds = premiumStoreIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _premiumProductIds = premiumProductIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _premiumGraceWindow = premiumGraceWindow ?? TimeSpan.FromDays(7);
    }

    public async Task<PremiumEntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_storeContextProvider.IsStoreSupported)
        {
            return new PremiumEntitlementSnapshot(
                IsPackaged: false,
                HasPremium: false,
                State: PremiumEntitlementState.VerificationFailed,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _premiumProductDisplayName,
                StatusMessage: "Premium entitlement is unavailable outside the Microsoft Store package.");
        }

        try
        {
            var context = _storeContextProvider.TryGetContext();
            if (context is null)
            {
                return BuildVerificationFailedSnapshot(isPackaged: true);
            }

            StoreAppLicense license = await context.GetAppLicenseAsync().AsTask(cancellationToken);
            var userCollectionResult = await QueryAsync(() => context.GetUserCollectionAsync(new[] { "Durable" }).AsTask(cancellationToken));
            var associatedResult = await QueryAsync(() => context.GetAssociatedStoreProductsAsync(new[] { "Durable" }).AsTask(cancellationToken));

            bool hasPremium = HasMatchingActiveLicense(license) || HasMatchingOwnedProduct(userCollectionResult.Products);
            bool hasQueryFailure = userCollectionResult.Error is not null || associatedResult.Error is not null;
            bool isProductAvailable = ResolvePremiumProductAvailability(userCollectionResult.Products, associatedResult.Products);

            if (hasPremium)
            {
                _premiumEntitlementCache.SaveVerifiedPremium(DateTimeOffset.UtcNow);
                return new PremiumEntitlementSnapshot(
                    IsPackaged: true,
                    HasPremium: true,
                    State: PremiumEntitlementState.VerifiedOwned,
                    IsPremiumProductAvailable: isProductAvailable,
                    PremiumProductDisplayName: _premiumProductDisplayName,
                    StatusMessage: $"{_premiumProductDisplayName} is unlocked.",
                    LastVerifiedOwnedUtc: DateTimeOffset.UtcNow);
            }

            _premiumEntitlementCache.Clear();
            return new PremiumEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: false,
                State: hasQueryFailure ? PremiumEntitlementState.VerificationFailed : PremiumEntitlementState.VerifiedNotOwned,
                IsPremiumProductAvailable: isProductAvailable,
                PremiumProductDisplayName: _premiumProductDisplayName,
                StatusMessage: hasQueryFailure
                    ? $"Unable to verify {_premiumProductDisplayName} entitlement right now."
                    : $"{_premiumProductDisplayName} is available in Microsoft Store.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var lastVerified = _premiumEntitlementCache.TryGetLastVerifiedPremiumUtc();
            bool allowGrace = lastVerified is DateTimeOffset cached && DateTimeOffset.UtcNow - cached <= _premiumGraceWindow;
            return new PremiumEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: allowGrace,
                State: PremiumEntitlementState.VerificationFailed,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _premiumProductDisplayName,
                StatusMessage: allowGrace
                    ? $"Using temporary {_premiumProductDisplayName} access while Microsoft Store entitlement is being re-verified."
                    : $"Unable to verify {_premiumProductDisplayName} entitlement right now.",
                LastVerifiedOwnedUtc: lastVerified,
                IsUsingGracePremium: allowGrace);
        }
    }

    private static async Task<QueryProductsResult> QueryAsync(Func<Task<StoreProductQueryResult>> query)
    {
        try
        {
            var result = await query();
            return new QueryProductsResult((result.Products?.Values ?? Array.Empty<StoreProduct>()).ToArray(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new QueryProductsResult(Array.Empty<StoreProduct>(), ex);
        }
    }

    private bool HasMatchingActiveLicense(StoreAppLicense license)
    {
        foreach (var item in license.AddOnLicenses)
        {
            if (!item.Value.IsActive)
            {
                continue;
            }

            if (_premiumStoreIds.Any(storeId => MatchesStoreId(item.Key, storeId) || MatchesStoreId(item.Value.SkuStoreId, storeId)))
            {
                return true;
            }

            if (_premiumProductIds.Any(productId => string.Equals(item.Value.InAppOfferToken, productId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasMatchingOwnedProduct(IReadOnlyList<StoreProduct> products)
    {
        return products.Any(product =>
            product.IsInUserCollection &&
            (_premiumStoreIds.Any(storeId => MatchesStoreId(product.StoreId, storeId)) ||
             _premiumProductIds.Any(productId => string.Equals(product.InAppOfferToken, productId, StringComparison.OrdinalIgnoreCase))));
    }

    private bool ResolvePremiumProductAvailability(IReadOnlyList<StoreProduct> userCollectionProducts, IReadOnlyList<StoreProduct> associatedProducts)
    {
        return userCollectionProducts.Concat(associatedProducts).Any(product =>
            _premiumStoreIds.Any(storeId => MatchesStoreId(product.StoreId, storeId)) ||
            _premiumProductIds.Any(productId => string.Equals(product.InAppOfferToken, productId, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesStoreId(string? storeIdOrSku, string productStoreId)
    {
        if (string.IsNullOrWhiteSpace(storeIdOrSku))
        {
            return false;
        }

        return string.Equals(storeIdOrSku, productStoreId, StringComparison.OrdinalIgnoreCase)
               || storeIdOrSku.StartsWith($"{productStoreId}/", StringComparison.OrdinalIgnoreCase);
    }

    private PremiumEntitlementSnapshot BuildVerificationFailedSnapshot(bool isPackaged)
    {
        return new PremiumEntitlementSnapshot(
            IsPackaged: isPackaged,
            HasPremium: false,
            State: PremiumEntitlementState.VerificationFailed,
            IsPremiumProductAvailable: false,
            PremiumProductDisplayName: _premiumProductDisplayName,
            StatusMessage: $"Unable to verify {_premiumProductDisplayName} entitlement right now.");
    }

    private sealed record QueryProductsResult(IReadOnlyList<StoreProduct> Products, Exception? Error);
}

