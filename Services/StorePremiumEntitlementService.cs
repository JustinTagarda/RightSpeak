using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace RightSpeak.Services;

public enum PremiumEntitlementState
{
    Checking,
    VerifiedOwned,
    VerifiedNotOwned,
    VerificationFailed
}

public sealed record PremiumEntitlementSnapshot(
    bool IsPackaged,
    bool HasPremium,
    PremiumEntitlementState State,
    bool IsPremiumProductAvailable,
    string PremiumProductDisplayName,
    string StatusMessage,
    DateTimeOffset? LastVerifiedOwnedUtc = null,
    bool IsUsingGracePremium = false);

public sealed class StorePremiumEntitlementOptions
{
    public bool TreatUnpackagedBuildsAsPremium { get; init; } = true;
    public string PremiumProductDisplayName { get; init; } = "RightSpeak Premium";
    public IReadOnlyList<string> PremiumStoreIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PremiumProductIds { get; init; } = Array.Empty<string>();
    public int RefreshRetryCount { get; init; } = 3;
    public TimeSpan[] RefreshRetryDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7)];
    public TimeSpan PremiumGraceWindow { get; init; } = TimeSpan.FromDays(7);
}

public interface IPremiumEntitlementService
{
    PremiumEntitlementSnapshot CurrentSnapshot { get; }
    event EventHandler<PremiumEntitlementSnapshot>? SnapshotChanged;
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class StorePremiumEntitlementService : IPremiumEntitlementService
{
    private readonly IAppVersionProvider _appVersionProvider;
    private readonly Func<IntPtr>? _ownerWindowHandleProvider;
    private readonly StorePremiumEntitlementOptions _options;
    private readonly object _sync = new();
    private PremiumEntitlementSnapshot _currentSnapshot;
    private DateTimeOffset? _lastVerifiedOwnedUtc;

    public StorePremiumEntitlementService(
        IAppVersionProvider appVersionProvider,
        Func<IntPtr>? ownerWindowHandleProvider = null,
        StorePremiumEntitlementOptions? options = null)
    {
        _appVersionProvider = appVersionProvider ?? throw new ArgumentNullException(nameof(appVersionProvider));
        _ownerWindowHandleProvider = ownerWindowHandleProvider;
        _options = options ?? new StorePremiumEntitlementOptions();

        if (_appVersionProvider.IsPackaged && !GetConfiguredStoreIds().Any())
        {
            throw new InvalidOperationException(
                "At least one Premium Store add-on ID is required for packaged builds. Configure PremiumStoreIds.");
        }

        _currentSnapshot = _appVersionProvider.IsPackaged || !_options.TreatUnpackagedBuildsAsPremium
            ? new PremiumEntitlementSnapshot(
                IsPackaged: _appVersionProvider.IsPackaged,
                HasPremium: false,
                State: PremiumEntitlementState.Checking,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _options.PremiumProductDisplayName,
                StatusMessage: "Checking Microsoft Store entitlement...")
            : new PremiumEntitlementSnapshot(
                IsPackaged: false,
                HasPremium: true,
                State: PremiumEntitlementState.VerifiedOwned,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _options.PremiumProductDisplayName,
                StatusMessage: "Development build: Premium features unlocked.");
    }

    public event EventHandler<PremiumEntitlementSnapshot>? SnapshotChanged;

    public PremiumEntitlementSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_appVersionProvider.IsPackaged)
        {
            Publish(_options.TreatUnpackagedBuildsAsPremium
                ? new PremiumEntitlementSnapshot(
                    IsPackaged: false,
                    HasPremium: true,
                    State: PremiumEntitlementState.VerifiedOwned,
                    IsPremiumProductAvailable: false,
                    PremiumProductDisplayName: _options.PremiumProductDisplayName,
                    StatusMessage: "Development build: Premium features unlocked.")
                : new PremiumEntitlementSnapshot(
                    IsPackaged: false,
                    HasPremium: false,
                    State: PremiumEntitlementState.VerificationFailed,
                    IsPremiumProductAvailable: false,
                    PremiumProductDisplayName: _options.PremiumProductDisplayName,
                    StatusMessage: "Premium entitlement is unavailable outside the Microsoft Store package."));
            return Task.CompletedTask;
        }

        return RefreshPackagedWithRetryAsync(cancellationToken);
    }

    private async Task RefreshPackagedWithRetryAsync(CancellationToken cancellationToken)
    {
        Publish(CurrentSnapshot with
        {
            State = PremiumEntitlementState.Checking,
            StatusMessage = "Checking Microsoft Store entitlement..."
        });

        int attempt = 0;
        int retryCount = Math.Max(1, _options.RefreshRetryCount);
        while (attempt < retryCount)
        {
            attempt++;
            try
            {
                StoreContext context = CreateStoreContext();
                StoreAppLicense license = await context.GetAppLicenseAsync().AsTask(cancellationToken);

                bool hasActiveLicenseMatch = HasMatchingActiveLicense(license, out string licenseMatchReason);
                QueryProductsResult userCollectionResult = await QueryStoreProductsAsync(
                    "user_collection",
                    () => context.GetUserCollectionAsync(new[] { "Durable" }).AsTask(cancellationToken));
                QueryProductsResult associatedProductsResult = await QueryStoreProductsAsync(
                    "associated_products",
                    () => context.GetAssociatedStoreProductsAsync(new[] { "Durable" }).AsTask(cancellationToken));

                bool userCollectionOwned = HasMatchingOwnedProduct(userCollectionResult.Products, out string userCollectionMatchReason);
                bool hasPremium = hasActiveLicenseMatch || userCollectionOwned;
                if (hasPremium)
                {
                    _lastVerifiedOwnedUtc = DateTimeOffset.UtcNow;
                }

                bool hasQueryFailure = userCollectionResult.Error is not null || associatedProductsResult.Error is not null;
                bool isProductAvailable = ResolvePremiumProductAvailability(userCollectionResult.Products, associatedProductsResult.Products);
                PremiumEntitlementState state;
                string statusMessage;
                if (!hasPremium && hasQueryFailure)
                {
                    state = PremiumEntitlementState.VerificationFailed;
                    statusMessage = $"Unable to verify {_options.PremiumProductDisplayName} entitlement right now.";
                }
                else if (hasPremium)
                {
                    state = PremiumEntitlementState.VerifiedOwned;
                    statusMessage = $"{_options.PremiumProductDisplayName} is unlocked.";
                }
                else
                {
                    state = PremiumEntitlementState.VerifiedNotOwned;
                    statusMessage = $"{_options.PremiumProductDisplayName} is available in Microsoft Store.";
                }

                Publish(new PremiumEntitlementSnapshot(
                    IsPackaged: true,
                    HasPremium: hasPremium,
                    State: state,
                    IsPremiumProductAvailable: isProductAvailable,
                    PremiumProductDisplayName: _options.PremiumProductDisplayName,
                    StatusMessage: statusMessage,
                    LastVerifiedOwnedUtc: _lastVerifiedOwnedUtc,
                    IsUsingGracePremium: false));

                LogRefresh(
                    attempt,
                    state,
                    hasPremium,
                    license,
                    userCollectionResult,
                    associatedProductsResult,
                    hasActiveLicenseMatch ? licenseMatchReason : userCollectionOwned ? userCollectionMatchReason : "not_owned");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error(
                    "premium_entitlement_refresh_failed",
                    new Dictionary<string, string?>
                    {
                        ["attempt"] = attempt.ToString(),
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message
                    });

                if (attempt < retryCount)
                {
                    TimeSpan delay = ResolveRetryDelay(attempt - 1);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }

                    continue;
                }

                bool allowGracePremium =
                    _lastVerifiedOwnedUtc is DateTimeOffset lastVerified &&
                    DateTimeOffset.UtcNow - lastVerified <= _options.PremiumGraceWindow;
                Publish(new PremiumEntitlementSnapshot(
                    IsPackaged: true,
                    HasPremium: allowGracePremium,
                    State: PremiumEntitlementState.VerificationFailed,
                    IsPremiumProductAvailable: false,
                    PremiumProductDisplayName: _options.PremiumProductDisplayName,
                    StatusMessage: allowGracePremium
                        ? $"Using temporary {_options.PremiumProductDisplayName} access while Microsoft Store entitlement is being re-verified."
                        : $"Unable to verify {_options.PremiumProductDisplayName} entitlement right now.",
                    LastVerifiedOwnedUtc: _lastVerifiedOwnedUtc,
                    IsUsingGracePremium: allowGracePremium));
                return;
            }
        }
    }

    private void LogRefresh(
        int attempt,
        PremiumEntitlementState state,
        bool hasPremium,
        StoreAppLicense license,
        QueryProductsResult userCollectionResult,
        QueryProductsResult associatedProductsResult,
        string matchReason)
    {
        AppDiagnostics.Info(
            "premium_entitlement_refreshed",
            new Dictionary<string, string?>
            {
                ["attempt"] = attempt.ToString(),
                ["state"] = state.ToString(),
                ["hasPremium"] = hasPremium.ToString(),
                ["configuredStoreIds"] = string.Join(",", GetConfiguredStoreIds()),
                ["configuredProductIds"] = string.Join(",", GetConfiguredProductIds()),
                ["licenseKeys"] = string.Join(",", license.AddOnLicenses.Keys),
                ["licenseDetails"] = FormatLicenseDetails(license),
                ["userCollectionStoreIds"] = string.Join(",", userCollectionResult.Products.Select(product => product.StoreId)),
                ["associatedStoreIds"] = string.Join(",", associatedProductsResult.Products.Select(product => product.StoreId)),
                ["userCollectionError"] = userCollectionResult.Error?.Message,
                ["associatedError"] = associatedProductsResult.Error?.Message,
                ["matchReason"] = matchReason
            });
    }

    private bool HasMatchingActiveLicense(StoreAppLicense license, out string matchReason)
    {
        string[] configuredStoreIds = GetConfiguredStoreIds();
        string[] configuredProductIds = GetConfiguredProductIds();

        foreach (KeyValuePair<string, StoreLicense> item in license.AddOnLicenses)
        {
            StoreLicense addOnLicense = item.Value;
            if (!addOnLicense.IsActive)
            {
                continue;
            }

            foreach (string configuredStoreId in configuredStoreIds)
            {
                if (MatchesProductStoreIdOrSku(item.Key, configuredStoreId) ||
                    MatchesProductStoreIdOrSku(addOnLicense.SkuStoreId, configuredStoreId))
                {
                    matchReason = $"active_license_store_id:{configuredStoreId}";
                    return true;
                }
            }

            foreach (string configuredProductId in configuredProductIds)
            {
                if (string.Equals(addOnLicense.InAppOfferToken, configuredProductId, StringComparison.OrdinalIgnoreCase))
                {
                    matchReason = $"active_license_offer_token:{configuredProductId}";
                    return true;
                }
            }
        }

        matchReason = "no_active_license_match";
        return false;
    }

    private bool HasMatchingOwnedProduct(IReadOnlyList<StoreProduct> products, out string matchReason)
    {
        string[] configuredStoreIds = GetConfiguredStoreIds();
        string[] configuredProductIds = GetConfiguredProductIds();

        foreach (StoreProduct product in products)
        {
            bool matchesStoreId = configuredStoreIds.Any(configuredStoreId =>
                string.Equals(product.StoreId, configuredStoreId, StringComparison.OrdinalIgnoreCase) ||
                product.StoreId.StartsWith($"{configuredStoreId}/", StringComparison.OrdinalIgnoreCase));
            bool matchesProductId = configuredProductIds.Any(configuredProductId =>
                string.Equals(product.InAppOfferToken, configuredProductId, StringComparison.OrdinalIgnoreCase));
            bool matches = matchesStoreId || matchesProductId;
            if (matches && product.IsInUserCollection)
            {
                matchReason = matchesStoreId
                    ? $"user_collection_store_id:{product.StoreId}"
                    : $"user_collection_offer_token:{product.InAppOfferToken}";
                return true;
            }
        }

        matchReason = "no_user_collection_match";
        return false;
    }

    private bool ResolvePremiumProductAvailability(
        IReadOnlyList<StoreProduct> userCollectionProducts,
        IReadOnlyList<StoreProduct> associatedProducts)
    {
        string[] configuredStoreIds = GetConfiguredStoreIds();
        string[] configuredProductIds = GetConfiguredProductIds();
        return userCollectionProducts.Concat(associatedProducts).Any(product =>
            configuredStoreIds.Any(configuredStoreId =>
                string.Equals(product.StoreId, configuredStoreId, StringComparison.OrdinalIgnoreCase) ||
                product.StoreId.StartsWith($"{configuredStoreId}/", StringComparison.OrdinalIgnoreCase))
            || configuredProductIds.Any(configuredProductId =>
                string.Equals(product.InAppOfferToken, configuredProductId, StringComparison.OrdinalIgnoreCase)));
    }

    private static string FormatLicenseDetails(StoreAppLicense license)
    {
        return string.Join(
            ",",
            license.AddOnLicenses.Select(item =>
                $"key={item.Key}|sku={item.Value.SkuStoreId}|offer={item.Value.InAppOfferToken}|active={item.Value.IsActive}"));
    }

    private async Task<QueryProductsResult> QueryStoreProductsAsync(
        string queryName,
        Func<Task<StoreProductQueryResult>> query)
    {
        try
        {
            StoreProductQueryResult result = await query();
            return new QueryProductsResult(
                (result.Products?.Values ?? Array.Empty<StoreProduct>()).ToArray(),
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppDiagnostics.Warn(
                $"premium_{queryName}_query_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return new QueryProductsResult(Array.Empty<StoreProduct>(), ex);
        }
    }

    private StoreContext CreateStoreContext()
    {
        StoreContext context = StoreContext.GetDefault();
        IntPtr ownerWindowHandle = _ownerWindowHandleProvider?.Invoke() ?? IntPtr.Zero;
        if (ownerWindowHandle != IntPtr.Zero)
        {
            try
            {
                WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindowHandle);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Warn(
                    "premium_store_context_window_initialize_failed",
                    new Dictionary<string, string?>
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message
                    });
            }
        }

        return context;
    }

    private static bool MatchesProductStoreIdOrSku(string? storeIdOrSkuStoreId, string productStoreId)
    {
        if (string.IsNullOrWhiteSpace(storeIdOrSkuStoreId))
        {
            return false;
        }

        return string.Equals(storeIdOrSkuStoreId, productStoreId, StringComparison.OrdinalIgnoreCase)
               || storeIdOrSkuStoreId.StartsWith($"{productStoreId}/", StringComparison.OrdinalIgnoreCase);
    }

    private string[] GetConfiguredStoreIds()
    {
        return _options.PremiumStoreIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string[] GetConfiguredProductIds()
    {
        return _options.PremiumProductIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private TimeSpan ResolveRetryDelay(int attemptIndex)
    {
        if (_options.RefreshRetryDelays.Length == 0 || attemptIndex < 0)
        {
            return TimeSpan.Zero;
        }

        int index = Math.Min(attemptIndex, _options.RefreshRetryDelays.Length - 1);
        return _options.RefreshRetryDelays[index];
    }

    private void Publish(PremiumEntitlementSnapshot snapshot)
    {
        EventHandler<PremiumEntitlementSnapshot>? handler;
        lock (_sync)
        {
            _currentSnapshot = snapshot;
            handler = SnapshotChanged;
        }

        handler?.Invoke(this, snapshot);
    }

    private sealed record QueryProductsResult(IReadOnlyList<StoreProduct> Products, Exception? Error);
}
