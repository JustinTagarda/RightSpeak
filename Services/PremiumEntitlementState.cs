namespace RightSpeak.Services;

public sealed record PremiumEntitlementState(
    bool IsPremiumOwned,
    bool IsVerifiedByStore,
    bool UsedCacheFallback,
    string Message);
