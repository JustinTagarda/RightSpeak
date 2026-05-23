using System;
using System.Collections.Generic;
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
    public async Task Upgrade_purchase_blocked_reports_blocked_message()
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

        Assert.Equal("Microsoft Store purchase is unavailable while running as administrator.", viewModel.StatusMessage);
        Assert.Equal("Basic", viewModel.ModeText);
    }

    [Fact]
    public async Task Upgrade_purchase_not_supported_reports_clear_message()
    {
        var premiumService = new FakePremiumEntitlementService(CreateBasicSnapshot());
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(
            StorePurchaseOutcome.NotSupported,
            "Premium purchase is available only in the Microsoft Store version."));
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

        Assert.Equal("Premium purchase is available only in the Microsoft Store version.", viewModel.StatusMessage);
        Assert.Equal(0, premiumService.RefreshCalls);
    }

    [Fact]
    public async Task Upgrade_purchase_succeeded_refreshes_entitlement_and_unlocks()
    {
        var premiumService = new FakePremiumEntitlementService(CreateBasicSnapshot())
        {
            SnapshotAfterRefresh = CreatePremiumSnapshot()
        };
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(
            StorePurchaseOutcome.Succeeded,
            "Premium purchase completed."));
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

        Assert.Equal(1, premiumService.RefreshCalls);
        Assert.Equal("Premium unlocked.", viewModel.StatusMessage);
        Assert.Equal("Premium", viewModel.ModeText);
        Assert.False(viewModel.IsModeClickable);
    }

    [Fact]
    public async Task Upgrade_purchase_already_owned_refreshes_entitlement_and_unlocks()
    {
        var premiumService = new FakePremiumEntitlementService(CreateBasicSnapshot())
        {
            SnapshotAfterRefresh = CreatePremiumSnapshot()
        };
        var purchaseService = new FakeStorePurchaseService(new PremiumPurchaseResult(
            StorePurchaseOutcome.AlreadyOwned,
            "Premium is already owned."));
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

        Assert.Equal(1, premiumService.RefreshCalls);
        Assert.Equal("Premium unlocked.", viewModel.StatusMessage);
        Assert.Equal("Premium", viewModel.ModeText);
    }

    [Fact]
    public async Task Upgrade_purchase_network_and_server_errors_surface_messages()
    {
        var premiumService = new FakePremiumEntitlementService(CreateBasicSnapshot());
        var purchaseService = new FakeStorePurchaseService(
            new PremiumPurchaseResult(
                StorePurchaseOutcome.NetworkError,
                "Premium purchase failed due to a network error. Check your connection and try again."),
            new PremiumPurchaseResult(
                StorePurchaseOutcome.ServerError,
                "Microsoft Store could not complete the purchase right now. Try again later."));
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
        Assert.Equal("Premium purchase failed due to a network error. Check your connection and try again.", viewModel.StatusMessage);

        await ((AsyncCommand)viewModel.UpgradeCommand).ExecuteAsync();
        Assert.Equal("Microsoft Store could not complete the purchase right now. Try again later.", viewModel.StatusMessage);
        Assert.Equal(0, premiumService.RefreshCalls);
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
        private readonly Queue<PremiumPurchaseResult> _results;

        public FakeStorePurchaseService(params PremiumPurchaseResult[] results)
        {
            _results = new Queue<PremiumPurchaseResult>(results);
        }

        public int PurchaseCalls { get; private set; }

        public Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            PurchaseCalls++;
            if (_results.Count == 0)
            {
                return Task.FromResult(new PremiumPurchaseResult(StorePurchaseOutcome.Failed, "No configured purchase result."));
            }

            if (_results.Count == 1)
            {
                return Task.FromResult(_results.Peek());
            }

            return Task.FromResult(_results.Dequeue());
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

        public int OpenAppPageCalls { get; private set; }

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
        public PremiumEntitlementSnapshot? SnapshotAfterRefresh { get; set; }

        public int RefreshCalls { get; private set; }

        public event EventHandler<PremiumEntitlementSnapshot>? SnapshotChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RefreshCalls++;
            if (SnapshotAfterRefresh is not null)
            {
                CurrentSnapshot = SnapshotAfterRefresh;
            }
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
