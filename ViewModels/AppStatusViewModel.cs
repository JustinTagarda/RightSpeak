using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RightSpeak.Services;

namespace RightSpeak.ViewModels;

public sealed class AppStatusViewModel : INotifyPropertyChanged
{
    private readonly IAppVersionService _appVersionService;
    private string _modeText = "Premium";
    private string _modeTooltip = "Premium features are enabled in this build.";
    private string _versionText = "v0.0.0.0";
    private string _statusMessage = string.Empty;

    public AppStatusViewModel(IAppVersionService appVersionService)
    {
        _appVersionService = appVersionService ?? throw new ArgumentNullException(nameof(appVersionService));
        _versionText = _appVersionService.GetVersionText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? StatusMessageChanged;

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

    public bool IsModeClickable => false;

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

    public bool IsNoUpdateToastVisible => false;

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
        if (snapshot is null)
        {
            return;
        }

        ModeText = snapshot.HasPremium ? "Premium" : "Basic";
        ModeTooltip = snapshot.StatusMessage;
        StatusMessage = snapshot.StatusMessage;
        OnPropertyChanged(nameof(IsModeClickable));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
