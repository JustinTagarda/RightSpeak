using RightSpeak.Services;
using Xunit;

namespace RightSpeak.Tests;

public sealed class PremiumEntitlementResolverTests
{
    private const string PremiumAddOnStoreId = "9PG6LR8K5M0Z";

    [Fact]
    public void LicenseSkuPrefix_MatchesPremiumProductStoreId()
    {
        PremiumAddOnLicenseEvidence[] licenses =
        [
            new PremiumAddOnLicenseEvidence("9PG6LR8K5M0Z/0010", true, null)
        ];

        var owned = PremiumEntitlementResolver.OwnsPremiumFromLicenses(licenses, PremiumAddOnStoreId);

        Assert.True(owned);
    }

    [Fact]
    public void InactiveLicense_DoesNotUnlockPremium()
    {
        PremiumAddOnLicenseEvidence[] licenses =
        [
            new PremiumAddOnLicenseEvidence("9PG6LR8K5M0Z/0010", false, null)
        ];

        var owned = PremiumEntitlementResolver.OwnsPremiumFromLicenses(licenses, PremiumAddOnStoreId);

        Assert.False(owned);
    }

    [Fact]
    public void WrongDurableLicense_DoesNotUnlockPremium()
    {
        PremiumAddOnLicenseEvidence[] licenses =
        [
            new PremiumAddOnLicenseEvidence("9ABCDEF12345/0010", true, null)
        ];

        var owned = PremiumEntitlementResolver.OwnsPremiumFromLicenses(licenses, PremiumAddOnStoreId);

        Assert.False(owned);
    }

    [Fact]
    public void CollectionStoreId_MatchesPremiumProductStoreId()
    {
        PremiumAddOnCollectionEvidence[] collection =
        [
            new PremiumAddOnCollectionEvidence("9PG6LR8K5M0Z", null)
        ];

        var owned = PremiumEntitlementResolver.OwnsPremiumFromCollection(collection, PremiumAddOnStoreId);

        Assert.True(owned);
    }

    [Fact]
    public void ParentAppStoreId_DoesNotUnlockPremium()
    {
        PremiumAddOnCollectionEvidence[] collection =
        [
            new PremiumAddOnCollectionEvidence("9MWX1Z4TKFL9", null)
        ];

        var owned = PremiumEntitlementResolver.OwnsPremiumFromCollection(collection, PremiumAddOnStoreId);

        Assert.False(owned);
    }
}
