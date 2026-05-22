using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.Views;

namespace RightSpeak.ViewModels;

public sealed class VoiceManagerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IVoiceCatalogService _voiceCatalogService;
    private readonly IVoiceDownloadService _voiceDownloadService;
    private readonly IReadingService _readingService;
    private readonly Action _refreshMainVoiceOptions;
    private readonly Action _goToPremiumAction;
    private readonly IPremiumEntitlementService _premiumEntitlementService;
    private PremiumEntitlementSnapshot _entitlementSnapshot;
    private readonly List<DownloadableVoice> _allVoices = [];
    private CancellationTokenSource? _downloadCancellationTokenSource;
    private CancellationTokenSource? _catalogLoadCancellationTokenSource;
    private string _statusMessage = "Choose a voice to install.";
    private bool _isBusy;
    private bool _isLoading;
    private LanguageFilterOption? _selectedLanguageFilter;
    private string _selectedQualityFilter = "All qualities";
    private bool _hasPremiumAccess;

    public VoiceManagerViewModel(
        IVoiceCatalogService voiceCatalogService,
        IVoiceDownloadService voiceDownloadService,
        IReadingService readingService,
        Action refreshMainVoiceOptions,
        IPremiumEntitlementService premiumEntitlementService,
        Action goToPremiumAction)
    {
        _voiceCatalogService = voiceCatalogService ?? throw new ArgumentNullException(nameof(voiceCatalogService));
        _voiceDownloadService = voiceDownloadService ?? throw new ArgumentNullException(nameof(voiceDownloadService));
        _readingService = readingService ?? throw new ArgumentNullException(nameof(readingService));
        _refreshMainVoiceOptions = refreshMainVoiceOptions ?? throw new ArgumentNullException(nameof(refreshMainVoiceOptions));
        _goToPremiumAction = goToPremiumAction ?? throw new ArgumentNullException(nameof(goToPremiumAction));
        _premiumEntitlementService = premiumEntitlementService ?? throw new ArgumentNullException(nameof(premiumEntitlementService));
        _entitlementSnapshot = _premiumEntitlementService.CurrentSnapshot;
        _hasPremiumAccess = _entitlementSnapshot.HasPremium;
        _premiumEntitlementService.SnapshotChanged += OnPremiumEntitlementSnapshotChanged;

        RefreshCommand = new AsyncCommand(RefreshFromScratchAsync, CanRefresh);
        CancelCommand = new AsyncCommand(CancelAsync, CanCancel);
        GoToPremiumCommand = new AsyncCommand(GoToPremiumAsync, CanGoToPremium);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<VoiceManagerVoiceViewModel> Voices { get; } = new();
    public ObservableCollection<LanguageFilterOption> LanguageFilters { get; } = new();
    public ObservableCollection<string> QualityFilters { get; } = new();

    public LanguageFilterOption? SelectedLanguageFilter
    {
        get => _selectedLanguageFilter;
        set
        {
            if (ReferenceEquals(_selectedLanguageFilter, value))
            {
                return;
            }

            _selectedLanguageFilter = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public string SelectedQualityFilter
    {
        get => _selectedQualityFilter;
        set
        {
            if (string.Equals(_selectedQualityFilter, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedQualityFilter = value;
            OnPropertyChanged();
            ApplyFilters();
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
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOperationIdle));
            RefreshCommandStates();
            RefreshVoiceCommands();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOperationIdle));
            RefreshCommandStates();
        }
    }

    public bool IsOperationIdle => !IsBusy && !IsLoading;
    public bool HasPremiumAccess
    {
        get => _hasPremiumAccess;
        private set
        {
            if (_hasPremiumAccess == value)
            {
                return;
            }

            _hasPremiumAccess = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPremiumUpsellVisible));
        }
    }

    public bool IsPremiumUpsellVisible =>
        !HasPremiumAccess && _entitlementSnapshot.State == PremiumEntitlementState.VerifiedNotOwned;
    public string PremiumUpsellMessage =>
        _entitlementSnapshot.State switch
        {
            PremiumEntitlementState.Checking => "Checking license. Install and remove actions will be available after verification.",
            PremiumEntitlementState.VerificationFailed => "Unable to verify license right now. Please ensure Microsoft Store is signed in and try again.",
            _ => "Get Premium to unlock install, update, and remove voice options."
        };

    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand GoToPremiumCommand { get; }

    public async Task LoadAsync()
    {
        await LoadCoreAsync(forceRefresh: false, resetUiBeforeFetch: false).ConfigureAwait(true);
    }

    private async Task RefreshFromScratchAsync()
    {
        await LoadCoreAsync(forceRefresh: true, resetUiBeforeFetch: true).ConfigureAwait(true);
    }

    private async Task LoadCoreAsync(bool forceRefresh, bool resetUiBeforeFetch)
    {
        if (IsLoading)
        {
            return;
        }

        _catalogLoadCancellationTokenSource?.Dispose();
        _catalogLoadCancellationTokenSource = new CancellationTokenSource();
        IsLoading = true;
        StatusMessage = forceRefresh ? "Refreshing voice catalog..." : "Loading voice catalog...";
        if (resetUiBeforeFetch)
        {
            ClearCatalogUiState();
        }

        try
        {
            var voices = await _voiceCatalogService
                .GetDownloadableVoicesAsync(
                    forceRefresh: forceRefresh,
                    cancellationToken: _catalogLoadCancellationTokenSource.Token)
                .ConfigureAwait(true);
            _allVoices.Clear();
            _allVoices.AddRange(voices);
            BuildFilterOptions(_allVoices);
            ApplyFilters();

            StatusMessage = Voices.Count == 0
                ? "No downloadable voices are available right now."
                : !PiperRuntimeEnvironment.IsRuntimeSupportedOnCurrentArchitecture(out var installBlockedReason)
                    ? installBlockedReason ?? "Piper installs are unavailable on this build."
                : HasPremiumAccess
                    ? "Choose a voice to install."
                    : _entitlementSnapshot.State switch
                    {
                        PremiumEntitlementState.Checking => "Checking license. Install and remove actions will be available after verification.",
                        PremiumEntitlementState.VerificationFailed => "Unable to verify license right now. Please ensure Microsoft Store is signed in and try again.",
                        _ => "Browse available Premium voice models."
                    };
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Voice catalog refresh cancelled.";
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "voice_catalog_load_failed",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            StatusMessage = "Couldn't load the voice catalog.";
        }
        finally
        {
            _catalogLoadCancellationTokenSource?.Dispose();
            _catalogLoadCancellationTokenSource = null;
            IsLoading = false;
        }
    }

    private void ClearCatalogUiState()
    {
        _allVoices.Clear();
        Voices.Clear();
        LanguageFilters.Clear();
        QualityFilters.Clear();
        _selectedLanguageFilter = null;
        _selectedQualityFilter = "All qualities";
        OnPropertyChanged(nameof(SelectedLanguageFilter));
        OnPropertyChanged(nameof(SelectedQualityFilter));
    }

    private async Task InstallOrUpdateAsync(VoiceManagerVoiceViewModel item)
    {
        if (!item.Voice.IsInstallSupported)
        {
            StatusMessage = item.Voice.InstallBlockedReason ?? "Piper installs are unavailable on this build.";
            return;
        }

        if (!ConfirmLicenseTerms())
        {
            StatusMessage = "Voice download cancelled. You must accept license terms before install/update.";
            return;
        }

        if (_readingService.IsReading)
        {
            StatusMessage = "Stop reading before installing voices.";
            return;
        }

        IsBusy = true;
        item.Status = VoiceInstallState.Downloading;
        item.HasProgress = true;
        item.ProgressPercent = 0;
        item.ProgressText = "Starting download...";
        _downloadCancellationTokenSource = new CancellationTokenSource();
        RefreshCommandStates();
        var progress = new Progress<VoiceDownloadProgress>(downloadProgress =>
        {
            if (!string.Equals(downloadProgress.VoiceId, item.Id, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(downloadProgress.VoiceId, "piper-runtime", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            item.HasProgress = true;
            item.ProgressPercent = downloadProgress.Percent ?? 0;
            item.ProgressText = downloadProgress.Percent is double percent
                ? $"{downloadProgress.Phase} ({percent:0}%)"
                : downloadProgress.Phase;
            StatusMessage = item.ProgressText;
        });

        try
        {
            var result = await _voiceDownloadService
                .InstallOrUpdateAsync(item.Voice, progress, _downloadCancellationTokenSource.Token)
                .ConfigureAwait(true);
            StatusMessage = result.Message;
            if (result.Success)
            {
                _readingService.RefreshAvailableVoices();
                _refreshMainVoiceOptions();
                await LoadAsync().ConfigureAwait(true);
                return;
            }

            item.Status = result.WasCancelled ? item.Voice.Status : VoiceInstallState.Failed;
            item.RefreshCommands();
        }
        catch (OperationCanceledException)
        {
            item.Status = item.Voice.Status;
            item.RefreshCommands();
            StatusMessage = "Voice download cancelled.";
        }
        finally
        {
            _downloadCancellationTokenSource?.Dispose();
            _downloadCancellationTokenSource = null;
            item.HasProgress = false;
            IsBusy = false;
        }
    }

    private async Task RemoveAsync(VoiceManagerVoiceViewModel item)
    {
        if (!ConfirmRemoveVoice(item.DisplayName))
        {
            StatusMessage = "Voice removal cancelled.";
            return;
        }

        if (_readingService.IsReading)
        {
            StatusMessage = "Stop reading before removing voices.";
            return;
        }

        IsBusy = true;
        try
        {
            var removedSelectedVoice = string.Equals(
                _readingService.SelectedVoiceName,
                $"piper:{item.Id}",
                StringComparison.OrdinalIgnoreCase);
            var result = await _voiceDownloadService.RemoveAsync(item.Voice).ConfigureAwait(true);
            StatusMessage = result.Message;
            if (result.Success)
            {
                if (removedSelectedVoice)
                {
                    _readingService.SelectedVoiceName = null;
                }

                _readingService.RefreshAvailableVoices();
                _refreshMainVoiceOptions();
                await LoadAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task CancelAsync()
    {
        var canceled = false;
        if (_catalogLoadCancellationTokenSource is not null)
        {
            _catalogLoadCancellationTokenSource.Cancel();
            canceled = true;
        }

        if (_downloadCancellationTokenSource is not null)
        {
            _downloadCancellationTokenSource.Cancel();
            canceled = true;
        }

        StatusMessage = canceled
            ? "Cancelling current operation..."
            : "No active operation to cancel.";
        return Task.CompletedTask;
    }

    private bool CanRefresh()
    {
        return !IsBusy && !IsLoading;
    }

    private bool CanCancel()
    {
        return _catalogLoadCancellationTokenSource is not null || _downloadCancellationTokenSource is not null;
    }

    public void CancelActiveOperations()
    {
        _catalogLoadCancellationTokenSource?.Cancel();
        _downloadCancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        _premiumEntitlementService.SnapshotChanged -= OnPremiumEntitlementSnapshotChanged;
    }

    private Task GoToPremiumAsync()
    {
        _goToPremiumAction();
        return Task.CompletedTask;
    }

    private bool CanGoToPremium()
    {
        return !HasPremiumAccess && _entitlementSnapshot.State == PremiumEntitlementState.VerifiedNotOwned;
    }

    private void OnPremiumEntitlementSnapshotChanged(object? sender, PremiumEntitlementSnapshot snapshot)
    {
        _ = sender;
        var app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            _ = app.Dispatcher.InvokeAsync(() => ApplyPremiumEntitlementSnapshot(snapshot));
            return;
        }

        ApplyPremiumEntitlementSnapshot(snapshot);
    }

    public void ApplyPremiumEntitlementSnapshot(PremiumEntitlementSnapshot snapshot)
    {
        _entitlementSnapshot = snapshot;
        HasPremiumAccess = snapshot.HasPremium;
        OnPropertyChanged(nameof(PremiumUpsellMessage));
        OnPropertyChanged(nameof(IsPremiumUpsellVisible));
        RefreshCommandStates();
        if (GoToPremiumCommand is AsyncCommand goToPremiumCommand)
        {
            goToPremiumCommand.RaiseCanExecuteChanged();
        }

        ApplyFilters();
    }

    private void RefreshCommandStates()
    {
        if (RefreshCommand is AsyncCommand refreshCommand)
        {
            refreshCommand.RaiseCanExecuteChanged();
        }

        if (CancelCommand is AsyncCommand cancelCommand)
        {
            cancelCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshVoiceCommands()
    {
        foreach (var voice in Voices.ToArray())
        {
            voice.RefreshCommands();
        }
    }

    private bool ConfirmLicenseTerms()
    {
        var dialog = new LicenseTermsWindow();
        return ShowOwnedDialog(dialog);
    }

    private bool ConfirmRemoveVoice(string displayName)
    {
        var dialog = new ConfirmActionWindow(
            "Confirm Remove Voice",
            $"Remove '{displayName}' from local installed voices?",
            confirmText: "Remove",
            cancelText: "Cancel");
        return ShowOwnedDialog(dialog);
    }

    private static bool ShowOwnedDialog(Window dialog)
    {
        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive) ??
            System.Windows.Application.Current?.MainWindow;

        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Topmost = owner.Topmost;
        }

        return dialog.ShowDialog() == true;
    }

    private void BuildFilterOptions(IEnumerable<DownloadableVoice> voices)
    {
        var currentLanguageCode = SelectedLanguageFilter?.Code;
        var currentQuality = SelectedQualityFilter;

        LanguageFilters.Clear();
        LanguageFilters.Add(new LanguageFilterOption(string.Empty, "All languages"));
        foreach (var language in voices
                     .Select(voice => voice.Locale)
                     .Where(locale => !string.IsNullOrWhiteSpace(locale))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(locale => locale, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = language.Replace('_', '-');
            var displayName = BuildLanguageDisplayName(normalized);
            LanguageFilters.Add(new LanguageFilterOption(normalized, displayName));
        }

        QualityFilters.Clear();
        QualityFilters.Add("All qualities");
        foreach (var quality in voices
                     .Select(voice => voice.Quality)
                     .Where(quality => !string.IsNullOrWhiteSpace(quality))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(quality => quality, StringComparer.OrdinalIgnoreCase))
        {
            QualityFilters.Add(quality);
        }

        SelectedLanguageFilter = LanguageFilters.FirstOrDefault(item =>
                                     string.Equals(item.Code, currentLanguageCode, StringComparison.OrdinalIgnoreCase)) ??
                                 LanguageFilters[0];

        SelectedQualityFilter = QualityFilters.Contains(currentQuality)
            ? currentQuality
            : QualityFilters[0];
    }

    private void ApplyFilters()
    {
        var filtered = _allVoices
            .Where(voice =>
                (SelectedLanguageFilter is null ||
                 string.IsNullOrEmpty(SelectedLanguageFilter.Code) ||
                 string.Equals(voice.Locale.Replace('_', '-'), SelectedLanguageFilter.Code, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(SelectedQualityFilter, "All qualities", StringComparison.Ordinal) ||
                 string.Equals(voice.Quality, SelectedQualityFilter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(voice => IsInstalledLikeStatus(voice.Status) ? 0 : 1)
            .ThenBy(voice => voice.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase);

        Voices.Clear();
        foreach (var voice in filtered)
        {
            Voices.Add(new VoiceManagerVoiceViewModel(voice, InstallOrUpdateAsync, RemoveAsync, HasPremiumAccess));
        }

        RefreshVoiceCommands();
    }

    private static bool IsInstalledLikeStatus(VoiceInstallState status)
    {
        return status is VoiceInstallState.Installed or VoiceInstallState.UpdateAvailable or VoiceInstallState.Downloading;
    }

    private static string BuildLanguageDisplayName(string languageTag)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageTag);
            return culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return languageTag;
        }
    }

    public sealed class LanguageFilterOption
    {
        public LanguageFilterOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public string Code { get; }
        public string DisplayName { get; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
