namespace RightSpeak.Services;

public sealed record PremiumAddOnLicenseEvidence(
    string SkuStoreId,
    bool IsActive,
    string? InAppOfferToken);
