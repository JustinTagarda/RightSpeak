using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Services;
using RightSpeak.Services.Store;

namespace RightSpeak.ViewModels;

public sealed class AppStatusViewModel : INotifyPropertyChanged
{
    private readonly IStorePurchaseService _storePurchaseService;
    private readonly IStoreLicenseService _storeLicenseService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IStoreNavigationService _storeNavigationService;
    private bool _isBusy;
    private bool _isNoUpdateToastVisible;
    private string _modeText = "Basic";
    private string _modeTooltip = "Click to upgrade to Premium.";
    private string _versionText = "v0.0.0.0";
    private string _statusMessage = string.Empty;
    private bool _hasPremium;

    public AppStatusViewModel(
        IStorePurchaseService storePurchaseService,
        IStoreLicenseService storeLicenseService,
        IAppUpdateService appUpdateService,
        IStoreNavigationService storeNavigationService,
        IAppVersionService appVersionService)
    {
        _storePurchaseService = storePurchaseService ?? throw new ArgumentNullException(nameof(storePurchaseService));
        _storeLicenseService = storeLicenseService ?? throw new ArgumentNullException(nameof(storeLicenseService));
        _appUpdateService = appUpdateService ?? throw new ArgumentNullException(nameof(appUpdateService));
        _storeNavigationService = storeNavigationService ?? throw new ArgumentNullException(nameof(storeNavigationService));
        _versionText = appVersionService?.GetVersionText() ?? "v0.0.0.0";

        UpgradeCommand = new AsyncCommand(UpgradeAsync, () => !_isBusy && !_hasPremium);
        CheckForUpdateCommand = new AsyncCommand(CheckForUpdateAsync, () => !_isBusy);
        RestorePurchaseCommand = new AsyncCommand(RestoreAsync, () => !_isBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? StatusMessageChanged;

    public ICommand UpgradeCommand { get; }
    public ICommand CheckForUpdateCommand { get; }
    public ICommand RestorePurchaseCommand { get; }

    public string ModeText
    {
        get => _modeText;
        private set
        {
            if (string.Equals(_modeText, value, StringComparison.Ordinal))
            {
                return;
            }

            _modeText = value;
            OnPropertyChanged();
        }
    }

    public bool IsModeClickable => !_hasPremium;

    public string ModeTooltip
    {
        get => _modeTooltip;
        private set
        {
            if (string.Equals(_modeTooltip, value, StringComparison.Ordinal))
            {
                return;
            }

            _modeTooltip = value;
            OnPropertyChanged();
        }
    }

    public string VersionText => _versionText;

    public bool IsNoUpdateToastVisible
    {
        get => _isNoUpdateToastVisible;
        private set
        {
            if (_isNoUpdateToastVisible == value)
            {
                return;
            }

            _isNoUpdateToastVisible = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (string.Equals(_statusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
            StatusMessageChanged?.Invoke(this, value);
        }
    }

    public void ApplyPremiumSnapshot(PremiumEntitlementSnapshot snapshot)
    {
        _hasPremium = snapshot.HasPremium;
        ModeText = snapshot.HasPremium ? "Premium" : "Basic";
        ModeTooltip = snapshot.HasPremium ? "Premium mode active." : "Click to upgrade to Premium.";
        OnPropertyChanged(nameof(IsModeClickable));
        RaiseCommandStateChanged();
    }

    private async Task UpgradeAsync()
    {
        if (_hasPremium)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var purchaseResult = await _storePurchaseService.PurchasePremiumAsync();
            StatusMessage = purchaseResult.Message;
            if (purchaseResult.Outcome == StorePurchaseOutcome.NotSupported)
            {
                _storeNavigationService.OpenPremiumPage();
                return;
            }

            if (purchaseResult.Outcome is StorePurchaseOutcome.Succeeded or StorePurchaseOutcome.AlreadyOwned)
            {
                var snapshot = await _storeLicenseService.RefreshAsync();
                ApplyPremiumSnapshot(snapshot);
                if (snapshot.HasPremium)
                {
                    StatusMessage = "Premium unlocked.";
                }
            }
        });
    }

    private async Task RestoreAsync()
    {
        await RunBusyAsync(async () =>
        {
            var snapshot = await _storeLicenseService.RefreshAsync();
            ApplyPremiumSnapshot(snapshot);
            StatusMessage = snapshot.HasPremium
                ? "Premium restored"
                : snapshot.State == PremiumEntitlementState.VerifiedNotOwned
                    ? "No Premium purchase found"
                    : "Unable to verify purchase right now";
        });
    }

    private async Task CheckForUpdateAsync()
    {
        await RunBusyAsync(async () =>
        {
            IsNoUpdateToastVisible = false;
            var result = await _appUpdateService.CheckForUpdatesOnDemandAsync();
            StatusMessage = result.Message;

            if (result.Availability == UserInitiatedUpdateAvailability.NotAvailable)
            {
                IsNoUpdateToastVisible = true;
                _ = HideNoUpdateToastAsync();
                return;
            }
        });
    }

    private async Task HideNoUpdateToastAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);
        IsNoUpdateToastVisible = false;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        RaiseCommandStateChanged();
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            RaiseCommandStateChanged();
        }
    }

    private void RaiseCommandStateChanged()
    {
        if (UpgradeCommand is AsyncCommand upgradeCommand)
        {
            upgradeCommand.RaiseCanExecuteChanged();
        }

        if (CheckForUpdateCommand is AsyncCommand checkForUpdateCommand)
        {
            checkForUpdateCommand.RaiseCanExecuteChanged();
        }

        if (RestorePurchaseCommand is AsyncCommand restorePurchaseCommand)
        {
            restorePurchaseCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
