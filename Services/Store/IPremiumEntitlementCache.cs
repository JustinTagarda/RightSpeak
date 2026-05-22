using System;

namespace RightSpeak.Services.Store;

public interface IPremiumEntitlementCache
{
    void SaveVerifiedPremium(DateTimeOffset verifiedUtc);
    void Clear();
    DateTimeOffset? TryGetLastVerifiedPremiumUtc();
}

