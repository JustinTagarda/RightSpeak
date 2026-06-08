using System;
using System.Collections.Generic;
using System.Linq;

namespace RightSpeak.Services;

internal static class PremiumEntitlementResolver
{
    public static bool OwnsPremiumFromLicenses(
        IEnumerable<PremiumAddOnLicenseEvidence> licenses,
        string premiumAddOnStoreId,
        string? premiumOfferToken = null)
    {
        if (string.IsNullOrWhiteSpace(premiumAddOnStoreId))
        {
            return false;
        }

        foreach (var license in licenses ?? Enumerable.Empty<PremiumAddOnLicenseEvidence>())
        {
            if (!license.IsActive)
            {
                continue;
            }

            if (MatchesOfferToken(license.InAppOfferToken, premiumOfferToken) ||
                MatchesSkuPrefix(license.SkuStoreId, premiumAddOnStoreId))
            {
                return true;
            }
        }

        return false;
    }

    public static bool OwnsPremiumFromCollection(
        IEnumerable<PremiumAddOnCollectionEvidence> products,
        string premiumAddOnStoreId,
        string? premiumOfferToken = null)
    {
        if (string.IsNullOrWhiteSpace(premiumAddOnStoreId))
        {
            return false;
        }

        foreach (var product in products ?? Enumerable.Empty<PremiumAddOnCollectionEvidence>())
        {
            if (MatchesOfferToken(product.InAppOfferToken, premiumOfferToken) ||
                string.Equals(product.StoreId, premiumAddOnStoreId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesOfferToken(string? candidateToken, string? premiumOfferToken)
    {
        return !string.IsNullOrWhiteSpace(premiumOfferToken) &&
               string.Equals(candidateToken, premiumOfferToken, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSkuPrefix(string? skuStoreId, string premiumAddOnStoreId)
    {
        if (string.IsNullOrWhiteSpace(skuStoreId))
        {
            return false;
        }

        return skuStoreId.StartsWith(premiumAddOnStoreId, StringComparison.OrdinalIgnoreCase);
    }
}
