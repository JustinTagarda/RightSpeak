using System;

namespace RightSpeak.Services;

public sealed class PremiumEntitlementCache : IPremiumEntitlementCache
{
    private readonly IAppSettingsService _appSettingsService;

    public PremiumEntitlementCache(IAppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
    }

    public bool HasVerifiedPremiumEntitlement()
    {
        return _appSettingsService.Current.PremiumEntitlementVerified;
    }

    public void SaveVerifiedPremiumEntitlement()
    {
        _appSettingsService.Current.PremiumEntitlementVerified = true;
        _appSettingsService.Current.PremiumEntitlementVerifiedUtc = DateTime.UtcNow.ToString("O");
        _appSettingsService.Save();
    }

    public void ClearVerifiedPremiumEntitlement()
    {
        _appSettingsService.Current.PremiumEntitlementVerified = false;
        _appSettingsService.Current.PremiumEntitlementVerifiedUtc = null;
        _appSettingsService.Save();
    }
}
