namespace RightSpeak.Services;

public interface IPremiumEntitlementCache
{
    bool HasVerifiedPremiumEntitlement();
    void SaveVerifiedPremiumEntitlement();
    void ClearVerifiedPremiumEntitlement();
}
