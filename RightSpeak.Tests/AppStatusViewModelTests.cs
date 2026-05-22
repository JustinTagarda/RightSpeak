using System;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.Services.Store;
using RightSpeak.ViewModels;
using Xunit;

namespace RightSpeak.Tests;

public sealed class AppStatusViewModelTests
{
    [Fact]
    public async Task Check_for_update_available_opens_app_store_page()
    {
        var premiumService = new FakePremiumEntitlementService(CreateBasicSnapshot());
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(StorePurchaseOutcome.Canceled, "Premium purchase canceled."));
        var updateService = new FakeAppUpdateService(new UserInitiatedUpdateResult(UserInitiatedUpdateAvailability.Available, "Update available."));
        var navigationService = new FakeStoreNavigationService(openAppPageResult: true);
        var viewModel = new AppStatusViewModel(
            purchaseService,
            premiumService,
            updateService,
            navigationService,
            new FakeAppVersionService("v1.2.3.4"));

        viewModel.ApplyPremiumSnapshot(premiumService.CurrentSnapshot);

        await ((AsyncCommand)viewModel.CheckForUpdateCommand).ExecuteAsync();

        Assert.Equal(1, navigationService.OpenAppPageCalls);
        Assert.Equal("Update available. Opening Microsoft Store.", viewModel.StatusMessage);
        Assert.False(viewModel.IsNoUpdateToastVisible);
    }

    [Fact]
    public async Task Restore_purchase_refreshes_entitlement_and_reports_result()
    {
        var premiumSnapshot = CreatePremiumSnapshot();
        var premiumService = new FakePremiumEntitlementService(premiumSnapshot);
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(StorePurchaseOutcome.Canceled, "Premium purchase canceled."));
        var updateService = new FakeAppUpdateService(new UserInitiatedUpdateResult(UserInitiatedUpdateAvailability.NotAvailable, "No update available."));
        var navigationService = new FakeStoreNavigationService(openAppPageResult: true);
        var viewModel = new AppStatusViewModel(
            purchaseService,
            premiumService,
            updateService,
            navigationService,
            new FakeAppVersionService("1.2.3.4"));

        viewModel.ApplyPremiumSnapshot(CreateBasicSnapshot());

        await ((AsyncCommand)viewModel.RestorePurchaseCommand).ExecuteAsync();

        Assert.Equal(1, premiumService.RefreshCalls);
        Assert.Equal("Premium", viewModel.ModeText);
        Assert.Equal("Premium restored", viewModel.StatusMessage);
        Assert.Equal("Premium mode active.", viewModel.ModeTooltip);
    }

    [Fact]
    public async Task Restore_purchase_with_cached_premium_reports_verification_failure()
    {
        var cachedPremiumSnapshot = new PremiumEntitlementSnapshot(
            IsPackaged: true,
            HasPremium: true,
            State: PremiumEntitlementState.VerificationFailed,
            IsPremiumProductAvailable: false,
            PremiumProductDisplayName: "RightSpeak Premium",
            StatusMessage: "Using cached RightSpeak Premium access while Microsoft Store entitlement is being re-verified.",
            LastVerifiedOwnedUtc: DateTimeOffset.UtcNow,
            IsUsingGracePremium: true);
        var premiumService = new FakePremiumEntitlementService(cachedPremiumSnapshot);
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(StorePurchaseOutcome.Canceled, "Premium purchase canceled."));
        var updateService = new FakeAppUpdateService(new UserInitiatedUpdateResult(UserInitiatedUpdateAvailability.NotAvailable, "No update available."));
        var navigationService = new FakeStoreNavigationService(openAppPageResult: true);
        var viewModel = new AppStatusViewModel(
            purchaseService,
            premiumService,
            updateService,
            navigationService,
            new FakeAppVersionService("1.2.3.4"));

        viewModel.ApplyPremiumSnapshot(CreateBasicSnapshot());

        await ((AsyncCommand)viewModel.RestorePurchaseCommand).ExecuteAsync();

        Assert.Equal("Premium", viewModel.ModeText);
        Assert.Equal("Unable to verify purchase right now", viewModel.StatusMessage);
        Assert.False(viewModel.IsModeClickable);
    }

    [Fact]
    public async Task Upgrade_purchase_blocked_does_not_open_premium_page()
    {
        var premiumService = new FakePremiumEntitlementService(CreateBasicSnapshot());
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(StorePurchaseOutcome.Blocked, "Microsoft Store purchase is unavailable while running as administrator."));
        var updateService = new FakeAppUpdateService(new UserInitiatedUpdateResult(UserInitiatedUpdateAvailability.NotAvailable, "No update available."));
        var navigationService = new FakeStoreNavigationService(openAppPageResult: true);
        var viewModel = new AppStatusViewModel(
            purchaseService,
            premiumService,
            updateService,
            navigationService,
            new FakeAppVersionService("1.2.3.4"));

        viewModel.ApplyPremiumSnapshot(CreateBasicSnapshot());

        await ((AsyncCommand)viewModel.UpgradeCommand).ExecuteAsync();

        Assert.Equal(0, navigationService.OpenPremiumPageCalls);
        Assert.Equal("Microsoft Store purchase is unavailable while running as administrator.", viewModel.StatusMessage);
        Assert.Equal("Basic", viewModel.ModeText);
    }

    private static PremiumEntitlementSnapshot CreateBasicSnapshot()
    {
        return new PremiumEntitlementSnapshot(
            IsPackaged: true,
            HasPremium: false,
            State: PremiumEntitlementState.VerifiedNotOwned,
            IsPremiumProductAvailable: true,
            PremiumProductDisplayName: "RightSpeak Premium",
            StatusMessage: "RightSpeak Premium is available in Microsoft Store.");
    }

    private static PremiumEntitlementSnapshot CreatePremiumSnapshot()
    {
        return new PremiumEntitlementSnapshot(
            IsPackaged: true,
            HasPremium: true,
            State: PremiumEntitlementState.VerifiedOwned,
            IsPremiumProductAvailable: true,
            PremiumProductDisplayName: "RightSpeak Premium",
            StatusMessage: "RightSpeak Premium is unlocked.",
            LastVerifiedOwnedUtc: DateTimeOffset.UtcNow);
    }

    private sealed class FakeStorePurchaseService : IStorePurchaseService
    {
        private readonly PremiumPurchaseResult _result;

        public FakeStorePurchaseService(PremiumPurchaseResult result)
        {
            _result = result;
        }

        public Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeAppUpdateService : IAppUpdateService
    {
        private readonly UserInitiatedUpdateResult _result;

        public FakeAppUpdateService(UserInitiatedUpdateResult result)
        {
            _result = result;
        }

        public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

        public AppUpdateSnapshot CurrentSnapshot => AppUpdateSnapshot.Idle("1.2.3.4");

        public bool HasDeferredInstallPending => false;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<UserInitiatedUpdateResult> CheckForUpdatesOnDemandAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(_result);
        }

        public Task<DeferredInstallAttemptResult> TryApplyDeferredInstallOnExitAsync(
            IProgress<AppUpdateSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _ = progress;
            _ = cancellationToken;
            return Task.FromResult(new DeferredInstallAttemptResult(false, false, false, "Not used."));
        }
    }

    private sealed class FakeStoreNavigationService : IStoreNavigationService
    {
        private readonly bool _openAppPageResult;

        public FakeStoreNavigationService(bool openAppPageResult)
        {
            _openAppPageResult = openAppPageResult;
        }

        public int OpenPremiumPageCalls { get; private set; }
        public int OpenAppPageCalls { get; private set; }

        public bool OpenPremiumPage()
        {
            OpenPremiumPageCalls++;
            return true;
        }

        public bool OpenAppPage()
        {
            OpenAppPageCalls++;
            return _openAppPageResult;
        }
    }

    private sealed class FakePremiumEntitlementService : IPremiumEntitlementService
    {
        public FakePremiumEntitlementService(PremiumEntitlementSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public PremiumEntitlementSnapshot CurrentSnapshot { get; private set; }

        public int RefreshCalls { get; private set; }

        public event EventHandler<PremiumEntitlementSnapshot>? SnapshotChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RefreshCalls++;
            SnapshotChanged?.Invoke(this, CurrentSnapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppVersionService : IAppVersionService
    {
        private readonly string _versionText;

        public FakeAppVersionService(string versionText)
        {
            _versionText = string.IsNullOrWhiteSpace(versionText) ? "v0.0.0.0" : versionText;
        }

        public string GetVersionText()
        {
            return _versionText.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? _versionText
                : $"v{_versionText}";
        }
    }
}
