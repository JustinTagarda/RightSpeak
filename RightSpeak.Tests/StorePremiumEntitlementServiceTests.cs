using RightSpeak.Services;
using RightSpeak.Services.Store;
using Xunit;

namespace RightSpeak.Tests;

public sealed class StorePremiumEntitlementServiceTests
{
    [Fact]
    public async Task Packaged_refresh_uses_cached_premium_after_store_failure_even_when_cache_is_old()
    {
        var cache = new FakePremiumEntitlementCache(DateTimeOffset.UtcNow.AddDays(-30));
        var service = new ThrowingStorePremiumEntitlementService(
            new PackageVersionProvider(isPackaged: true, installedVersion: "1.0.0.0"),
            cache,
            options: new StorePremiumEntitlementOptions
            {
                TreatUnpackagedBuildsAsPremium = false,
                PremiumProductDisplayName = "RightSpeak Premium",
                PremiumStoreIds = ["9PG6LR8K5M0Z"],
                PremiumProductIds = ["rightspeak_premium_lifetime"]
            });

        await service.RefreshAsync();

        Assert.True(service.CurrentSnapshot.HasPremium);
        Assert.Equal(PremiumEntitlementState.VerificationFailed, service.CurrentSnapshot.State);
        Assert.True(service.CurrentSnapshot.IsUsingGracePremium);
        Assert.Equal(cache.VerifiedUtc, service.CurrentSnapshot.LastVerifiedOwnedUtc);
    }

    private sealed class ThrowingStorePremiumEntitlementService : StorePremiumEntitlementService
    {
        public ThrowingStorePremiumEntitlementService(
            IAppVersionProvider appVersionProvider,
            IPremiumEntitlementCache premiumEntitlementCache,
            StorePremiumEntitlementOptions options)
            : base(appVersionProvider, premiumEntitlementCache, options: options)
        {
        }

        protected override Windows.Services.Store.StoreContext CreateStoreContext()
        {
            throw new InvalidOperationException("Store context unavailable for test.");
        }
    }

    private sealed class FakePremiumEntitlementCache : IPremiumEntitlementCache
    {
        public FakePremiumEntitlementCache(DateTimeOffset? verifiedUtc = null)
        {
            VerifiedUtc = verifiedUtc;
        }

        public DateTimeOffset? VerifiedUtc { get; private set; }

        public void SaveVerifiedPremium(DateTimeOffset verifiedUtc)
        {
            VerifiedUtc = verifiedUtc;
        }

        public void Clear()
        {
            VerifiedUtc = null;
        }

        public DateTimeOffset? TryGetLastVerifiedPremiumUtc()
        {
            return VerifiedUtc;
        }
    }
}
