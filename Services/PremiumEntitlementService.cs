using System;
using System.Threading;
using System.Threading.Tasks;

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

public interface IPremiumEntitlementService
{
    PremiumEntitlementSnapshot CurrentSnapshot { get; }
    event EventHandler<PremiumEntitlementSnapshot>? SnapshotChanged;
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalPremiumEntitlementService : IPremiumEntitlementService
{
    private readonly PremiumEntitlementSnapshot _snapshot;

    public LocalPremiumEntitlementService(IAppVersionProvider appVersionProvider)
    {
        if (appVersionProvider is null)
        {
            throw new ArgumentNullException(nameof(appVersionProvider));
        }

        _snapshot = new PremiumEntitlementSnapshot(
            IsPackaged: appVersionProvider.IsPackaged,
            HasPremium: true,
            State: PremiumEntitlementState.VerifiedOwned,
            IsPremiumProductAvailable: false,
            PremiumProductDisplayName: "RightSpeak Premium",
            StatusMessage: "Premium features are enabled in this build.",
            LastVerifiedOwnedUtc: DateTimeOffset.UtcNow,
            IsUsingGracePremium: false);
    }

    public PremiumEntitlementSnapshot CurrentSnapshot => _snapshot;

    public event EventHandler<PremiumEntitlementSnapshot>? SnapshotChanged;

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        SnapshotChanged?.Invoke(this, _snapshot);
        return Task.CompletedTask;
    }
}
