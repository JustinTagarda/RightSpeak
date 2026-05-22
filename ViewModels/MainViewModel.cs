using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Shell;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.ViewModels;

public sealed record PremiumUpsellRequest(
    string FeatureName,
    string Message,
    bool CanShowPurchase);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string SystemDefaultVoiceOption = "System default";
    private const string VoicePreviewText = "This is a preview of the current voice and speaking rate.";
    private const string DefaultReadSelectedHotkeyKey = "S";
    private const string DefaultReadParagraphHotkeyKey = "P";
    private const string DefaultReadDocumentHotkeyKey = "D";
    private const string DefaultStopHotkeyKey = "X";

    private readonly IReadingService _readingService;
    private readonly IHotkeySettingsService _hotkeySettingsService;
    private readonly IAppSettingsService _appSettingsService;
    private IReadOnlyList<string> _voiceOptions;
    private Dictionary<string, string?> _voiceNameByOptionLabel;
    private Dictionary<string, string> _voiceOptionLabelByName;
    private readonly Func<(bool Success, string StatusMessage)>? _applyHotkeysRegistration;
    private readonly Func<string, bool>? _applyTheme;
    private PremiumEntitlementSnapshot _premiumEntitlementSnapshot;
    private CancellationTokenSource? _hotkeyModifierWarningCts;
    private CancellationTokenSource? _activeExternalReadCancellationTokenSource;
    private Stopwatch? _activeExternalReadStopwatch;

    private string _inputText = string.Empty;
    private string _statusMessage = "Select text in another app, then choose a read action.";
    private bool _isSpeaking;
    private bool _isPaused;
    private bool _isExternalReadActive;
    private ReadingProgressStage _externalReadStage = ReadingProgressStage.Idle;
    private bool _isInputReadSpeaking;
    private bool _hasExternalFocusedWindow;
    private bool _suppressHotkeyAutoApply;
    private string _focusedWindowText = "Current app";
    private string _hotkeyModifierWarningMessage = string.Empty;
    private string _updateStageText = string.Empty;
    private string _updateStatusMessage = string.Empty;
    private AppUpdateState _updateState = AppUpdateState.Idle;
    private bool _isUpdateVisible;
    private bool _isUpdateProgressVisible;
    private bool _isMandatoryUpdateAvailable;
    private double _updateProgressValue;
    private int _speechRate;
    private string _selectedVoiceOption = SystemDefaultVoiceOption;
    private string _selectedTheme = AppThemes.Light;
    private HotkeyModifierPreset _hotkeyModifierPreset = HotkeyModifierPreset.AltShift;
    private HotkeyModifierPreset _appliedHotkeyModifierPreset = HotkeyModifierPreset.AltShift;
    private string _readSelectedHotkeyKey = DefaultReadSelectedHotkeyKey;
    private string _readParagraphHotkeyKey = DefaultReadParagraphHotkeyKey;
    private string _readDocumentHotkeyKey = DefaultReadDocumentHotkeyKey;
    private string _stopHotkeyKey = DefaultStopHotkeyKey;
    private string _appliedReadSelectedHotkeyKey = DefaultReadSelectedHotkeyKey;
    private string _appliedReadParagraphHotkeyKey = DefaultReadParagraphHotkeyKey;
    private string _appliedReadDocumentHotkeyKey = DefaultReadDocumentHotkeyKey;
    private string _appliedStopHotkeyKey = DefaultStopHotkeyKey;
    private readonly bool _isAnalyzeAvailable;

    public MainViewModel(
        IReadingService readingService,
        IHotkeySettingsService hotkeySettingsService,
        Func<(bool Success, string StatusMessage)>? applyHotkeysRegistration = null,
        bool isAnalyzeAvailable = false,
        IAppSettingsService? appSettingsService = null,
        Func<string, bool>? applyTheme = null,
        PremiumEntitlementSnapshot? premiumEntitlementSnapshot = null)
    {
        _readingService = readingService ?? throw new ArgumentNullException(nameof(readingService));
        _hotkeySettingsService = hotkeySettingsService ?? throw new ArgumentNullException(nameof(hotkeySettingsService));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        _applyHotkeysRegistration = applyHotkeysRegistration;
        _isAnalyzeAvailable = isAnalyzeAvailable;
        _applyTheme = applyTheme;
        _premiumEntitlementSnapshot = premiumEntitlementSnapshot ?? new PremiumEntitlementSnapshot(
            IsPackaged: false,
            HasPremium: false,
            State: PremiumEntitlementState.VerificationFailed,
            IsPremiumProductAvailable: false,
            PremiumProductDisplayName: "RightSpeak Premium",
            StatusMessage: "Premium entitlement is unavailable outside the Microsoft Store package.");

        _speechRate = _readingService.SpeechRate;
        _inputText = _readingService.TypedTextDraft ?? string.Empty;
        _selectedTheme = AppThemes.Normalize(_appSettingsService.Current.Theme);
        (_voiceOptions, _voiceNameByOptionLabel, _voiceOptionLabelByName) = BuildVoiceOptions(_readingService.AvailableVoices);
        _selectedVoiceOption = GetVoiceOptionLabel(_readingService.SelectedVoiceName);
        _hotkeyModifierPreset = _hotkeySettingsService.ModifierPreset;
        _appliedHotkeyModifierPreset = _hotkeyModifierPreset;
        _readSelectedHotkeyKey = _hotkeySettingsService.ReadSelectedKey;
        _readParagraphHotkeyKey = _hotkeySettingsService.ReadParagraphKey;
        _readDocumentHotkeyKey = _hotkeySettingsService.ReadDocumentKey;
        _stopHotkeyKey = _hotkeySettingsService.StopKey;
        _appliedReadSelectedHotkeyKey = _readSelectedHotkeyKey;
        _appliedReadParagraphHotkeyKey = _readParagraphHotkeyKey;
        _appliedReadDocumentHotkeyKey = _readDocumentHotkeyKey;
        _appliedStopHotkeyKey = _stopHotkeyKey;
        EnsureBasicHotkeysIfRequired();
        UpdateHotkeyModifierWarningMessage();

        ReadCommand = new AsyncCommand(ReadAsync, CanRead);
        ClearCommand = new AsyncCommand(ClearAsync, CanClear);
        PreviewVoiceCommand = new AsyncCommand(PreviewVoiceAsync, CanPreviewVoice);
        ReadSelectedTextCommand = new AsyncCommand(ReadSelectedTextAsync, CanReadSelectedText);
        ReadParagraphCommand = new AsyncCommand(ReadParagraphAsync, CanReadSelectedText);
        ReadDocumentCommand = new AsyncCommand(ReadDocumentAsync, CanReadSelectedText);
        StopCommand = new AsyncCommand(StopAsync, CanStop);
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync, CanPauseOrResume);
        ResetHotkeysCommand = new AsyncCommand(ResetHotkeysAsync, CanResetHotkeys);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PremiumUpsellRequest>? PremiumUpsellRequested;
    public PremiumEntitlementState PremiumEntitlementState => _premiumEntitlementSnapshot.State;

    public string InputText
    {
        get => _inputText;
        set
        {
            if (value == _inputText)
            {
                return;
            }

            _inputText = value;
            OnPropertyChanged();
            _readingService.TypedTextDraft = _inputText;
            UpdateCommandStates();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (value == _statusMessage)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public int SpeechRate
    {
        get => _speechRate;
        set
        {
            var clamped = value < -10 ? -10 : value > 10 ? 10 : value;
            if (clamped == _speechRate)
            {
                return;
            }

            _speechRate = clamped;
            _readingService.SpeechRate = clamped;
            OnPropertyChanged();
            SetStatusMessage($"Speech rate set to {_speechRate}.");
        }
    }

    public bool IsUpdateVisible
    {
        get => _isUpdateVisible;
        private set
        {
            if (_isUpdateVisible == value)
            {
                return;
            }

            _isUpdateVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskbarProgressState));
            OnPropertyChanged(nameof(IsFooterUpdateCardVisible));
        }
    }

    public string UpdateStageText
    {
        get => _updateStageText;
        private set
        {
            if (string.Equals(_updateStageText, value, StringComparison.Ordinal))
            {
                return;
            }

            _updateStageText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskbarProgressState));
        }
    }

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set
        {
            if (string.Equals(_updateStatusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _updateStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsUpdateProgressVisible
    {
        get => _isUpdateProgressVisible;
        private set
        {
            if (_isUpdateProgressVisible == value)
            {
                return;
            }

            _isUpdateProgressVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskbarProgressState));
        }
    }

    public bool IsMandatoryUpdateAvailable
    {
        get => _isMandatoryUpdateAvailable;
        private set
        {
            if (_isMandatoryUpdateAvailable == value)
            {
                return;
            }

            _isMandatoryUpdateAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskbarProgressState));
        }
    }

    public double UpdateProgressPercent
    {
        get => _updateProgressValue * 100d;
        private set
        {
            var normalized = value < 0d ? 0d : value > 100d ? 100d : value;
            var progressValue = normalized / 100d;
            if (Math.Abs(_updateProgressValue - progressValue) < 0.0001d)
            {
                return;
            }

            _updateProgressValue = progressValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskbarProgressValue));
        }
    }

    public double TaskbarProgressValue => _updateProgressValue;

    public bool IsFooterUpdateBannerVisible =>
        _updateState is AppUpdateState.Downloading or AppUpdateState.Installing or AppUpdateState.Completed;

    public bool IsFooterDefaultVisible => !IsFooterUpdateBannerVisible;

    public bool IsUpdateInProgress => _updateState is AppUpdateState.Downloading or AppUpdateState.Installing;

    public bool IsFooterStaticInfoVisible => !IsUpdateVisible;

    public bool IsFooterUpdateCardVisible => IsUpdateVisible;

    public bool IsFooterUpdateProgressVisible =>
        _updateState is AppUpdateState.Downloading or AppUpdateState.Installing && _isUpdateProgressVisible;

    public string ApplicationAccessTierText => !_premiumEntitlementSnapshot.IsPackaged
        ? "Development"
        : _premiumEntitlementSnapshot.State == PremiumEntitlementState.Checking
            ? string.Empty
            : _premiumEntitlementSnapshot.HasPremium
                ? "Premium"
                : "Basic";

    public string FooterUpdateBannerText
    {
        get
        {
            if (_updateState == AppUpdateState.Completed)
            {
                return "Update will take effect the next time you open RightSpeak.";
            }

            if (string.IsNullOrWhiteSpace(_updateStatusMessage))
            {
                return _updateStageText;
            }

            if (string.IsNullOrWhiteSpace(_updateStageText))
            {
                return _updateStatusMessage;
            }

            return $"{_updateStageText}: {_updateStatusMessage}";
        }
    }

    public TaskbarItemProgressState TaskbarProgressState
    {
        get
        {
            if (_isUpdateProgressVisible)
            {
                return TaskbarItemProgressState.Normal;
            }

            if (_isUpdateVisible || _isMandatoryUpdateAvailable)
            {
                return TaskbarItemProgressState.Paused;
            }

            return TaskbarItemProgressState.None;
        }
    }

    public IReadOnlyList<string> VoiceOptions => _voiceOptions;
    public IReadOnlyList<string> ThemeOptions => AppThemes.Options;

    public string SelectedVoiceOption
    {
        get => _selectedVoiceOption;
        set
        {
            if (string.Equals(value, _selectedVoiceOption, StringComparison.Ordinal))
            {
                return;
            }

            _selectedVoiceOption = value;
            _readingService.SelectedVoiceName = GetVoiceNameForOption(value);
            _selectedVoiceOption = GetVoiceOptionLabel(_readingService.SelectedVoiceName);
            OnPropertyChanged();
            SetStatusMessage(string.Equals(_selectedVoiceOption, SystemDefaultVoiceOption, StringComparison.Ordinal)
                ? "Voice set to system default."
                : $"Voice set to {_selectedVoiceOption}.");
        }
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            var normalized = AppThemes.Normalize(value);
            if (string.Equals(_selectedTheme, normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (_applyTheme is not null && !_applyTheme(normalized))
            {
                SetStatusMessage("Couldn't apply that theme right now.");
                return;
            }

            _selectedTheme = normalized;
            _appSettingsService.Current.Theme = normalized;
            _appSettingsService.Save();
            OnPropertyChanged();
            SetStatusMessage($"Theme set to {normalized}.");
        }
    }

    public IReadOnlyList<string> HotkeyKeyOptions => _hotkeySettingsService.AvailableKeyOptions;

    public string ReadSelectedHotkeyKey
    {
        get => _readSelectedHotkeyKey;
        set => TrySetHotkeyKey(
            value,
            ref _readSelectedHotkeyKey,
            nameof(ReadSelectedHotkeyKey),
            nameof(ReadSelectedHotkeyDisplay));
    }

    public string ReadParagraphHotkeyKey
    {
        get => _readParagraphHotkeyKey;
        set => TrySetHotkeyKey(
            value,
            ref _readParagraphHotkeyKey,
            nameof(ReadParagraphHotkeyKey),
            nameof(ReadParagraphHotkeyDisplay));
    }

    public string ReadDocumentHotkeyKey
    {
        get => _readDocumentHotkeyKey;
        set => TrySetHotkeyKey(
            value,
            ref _readDocumentHotkeyKey,
            nameof(ReadDocumentHotkeyKey),
            nameof(ReadDocumentHotkeyDisplay));
    }

    public string StopHotkeyKey
    {
        get => _stopHotkeyKey;
        set => TrySetHotkeyKey(
            value,
            ref _stopHotkeyKey,
            nameof(StopHotkeyKey),
            nameof(StopHotkeyDisplay));
    }

    public bool IsAltShiftModifierSelected
    {
        get => _hotkeyModifierPreset == HotkeyModifierPreset.AltShift;
        set => TrySetModifierPreset(HotkeyModifierPreset.AltShift, value);
    }

    public bool IsCtrlShiftModifierSelected
    {
        get => _hotkeyModifierPreset == HotkeyModifierPreset.CtrlShift;
        set => TrySetModifierPreset(HotkeyModifierPreset.CtrlShift, value);
    }

    public bool IsCtrlAltModifierSelected
    {
        get => _hotkeyModifierPreset == HotkeyModifierPreset.CtrlAlt;
        set => TrySetModifierPreset(HotkeyModifierPreset.CtrlAlt, value);
    }

    public string HotkeyModifierLabel => GetModifierLabel(_hotkeyModifierPreset);
    public string HotkeyModifierWarningMessage => _hotkeyModifierWarningMessage;
    public string ReadSelectedHotkeyDisplay => $"Read Selected: {HotkeyModifierLabel}+{_readSelectedHotkeyKey}";
    public string ReadParagraphHotkeyDisplay => $"Read Paragraph: {HotkeyModifierLabel}+{_readParagraphHotkeyKey}";
    public string ReadDocumentHotkeyDisplay => $"Read Document: {HotkeyModifierLabel}+{_readDocumentHotkeyKey}";
    public string StopHotkeyDisplay => $"Stop: {HotkeyModifierLabel}+{_stopHotkeyKey}";

    public string FocusedWindowText
    {
        get => _focusedWindowText;
        private set
        {
            if (string.Equals(value, _focusedWindowText, StringComparison.Ordinal))
            {
                return;
            }

            _focusedWindowText = value;
            OnPropertyChanged();
        }
    }

    public bool IsInputReadSpeaking => _isInputReadSpeaking;
    public bool IsPaused => _isPaused;
    public bool IsInputReadEnabled => !_isExternalReadActive;
    public bool IsExternalReadSectionEnabled => !_isInputReadSpeaking;
    public bool IsExternalReadsEnabled => !_isInputReadSpeaking && !_isSpeaking && _hasExternalFocusedWindow;
    public bool IsVoiceModelButtonsEnabled => !_isInputReadSpeaking && !_isExternalReadActive;
    public bool IsStopEnabled => _isSpeaking || _isInputReadSpeaking || _isExternalReadActive;
    public bool IsPauseEnabled => _isInputReadSpeaking || _externalReadStage == ReadingProgressStage.Speaking;
    public string PauseButtonText => _isPaused ? "Resume" : "Pause";
    public string ExternalReadActionText => IsExternalReadCancellationStage ? "Cancel" : "Stop";
    public bool IsAnalyzeAvailable => _isAnalyzeAvailable;

    public ICommand ReadCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PreviewVoiceCommand { get; }
    public ICommand ReadSelectedTextCommand { get; }
    public ICommand ReadParagraphCommand { get; }
    public ICommand ReadDocumentCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand ResetHotkeysCommand { get; }

    public void SetStatusMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusMessage = message;
    }

    public void SetFocusedWindowText(string? text)
    {
        SetFocusedWindowContext(text, hasExternalFocusedWindow: !string.IsNullOrWhiteSpace(text));
    }

    public void SetFocusedWindowContext(string? text, bool hasExternalFocusedWindow)
    {
        var formatted = string.IsNullOrWhiteSpace(text)
            ? "Current app"
            : FormatFocusedWindowTitle(text.Trim());
        FocusedWindowText = formatted;

        if (_hasExternalFocusedWindow == hasExternalFocusedWindow)
        {
            return;
        }

        _hasExternalFocusedWindow = hasExternalFocusedWindow;
        OnPropertyChanged(nameof(IsExternalReadsEnabled));
        UpdateCommandStates();
    }

    public void SetExternalReadFocusStatus()
    {
        if (_isExternalReadActive)
        {
            return;
        }

        SetExternalReadStage(ReadingProgressStage.Focusing);
        StatusMessage = "Switching to target app...";
    }

    public void ClearExternalReadFocusStatus()
    {
        if (!_isExternalReadActive && _externalReadStage == ReadingProgressStage.Focusing)
        {
            SetExternalReadStage(ReadingProgressStage.Idle);
        }
    }

    public void RefreshVoiceOptions()
    {
        _readingService.RefreshAvailableVoices();
        (_voiceOptions, _voiceNameByOptionLabel, _voiceOptionLabelByName) = BuildVoiceOptions(_readingService.AvailableVoices);
        _selectedVoiceOption = GetVoiceOptionLabel(_readingService.SelectedVoiceName);
        OnPropertyChanged(nameof(VoiceOptions));
        OnPropertyChanged(nameof(SelectedVoiceOption));
        SetStatusMessage("Voice list refreshed.");
    }

    public void ApplyUpdateSnapshot(AppUpdateSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        _updateState = snapshot.State;
        UpdateStageText = snapshot.StageText;
        UpdateStatusMessage = snapshot.StatusMessage;
        IsMandatoryUpdateAvailable = snapshot.IsMandatoryUpdateAvailable;
        IsUpdateProgressVisible = snapshot.IsProgressVisible &&
                                  snapshot.State is AppUpdateState.Downloading or AppUpdateState.Installing;
        UpdateProgressPercent = snapshot.ProgressValue * 100d;
        IsUpdateVisible = IsFooterUpdateUiVisible(snapshot.State);
        OnPropertyChanged(nameof(IsFooterUpdateBannerVisible));
        OnPropertyChanged(nameof(IsFooterDefaultVisible));
        OnPropertyChanged(nameof(IsFooterUpdateProgressVisible));
        OnPropertyChanged(nameof(FooterUpdateBannerText));
        OnPropertyChanged(nameof(IsUpdateInProgress));
        OnPropertyChanged(nameof(IsFooterStaticInfoVisible));
        OnPropertyChanged(nameof(IsFooterUpdateCardVisible));
    }

    private static bool IsFooterUpdateUiVisible(AppUpdateState state)
    {
        return state is not AppUpdateState.Idle and not AppUpdateState.Checking;
    }

    private static string FormatFocusedWindowTitle(string title)
    {
        var parts = title.Split(" - ", StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return title;
        }

        var appName = parts[^1];
        if (string.IsNullOrWhiteSpace(appName))
        {
            return title;
        }

        var windowTitle = string.Join(" - ", parts[..^1]).Trim();
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return appName;
        }

        return $"{appName} - {windowTitle}";
    }

    private bool CanResetHotkeys()
    {
        if (!HasPremiumHotkeyCustomization)
        {
            return false;
        }

        return _hotkeyModifierPreset != HotkeyModifierPreset.AltShift ||
               !string.Equals(_readSelectedHotkeyKey, DefaultReadSelectedHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(_readParagraphHotkeyKey, DefaultReadParagraphHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(_readDocumentHotkeyKey, DefaultReadDocumentHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(_stopHotkeyKey, DefaultStopHotkeyKey, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanRead()
    {
        return !_isSpeaking && !string.IsNullOrWhiteSpace(InputText);
    }

    private bool CanClear()
    {
        return !_isSpeaking && !string.IsNullOrWhiteSpace(InputText);
    }

    private bool CanReadSelectedText()
    {
        return IsExternalReadsEnabled;
    }

    private bool CanPreviewVoice()
    {
        return !_isSpeaking;
    }

    private bool CanStop()
    {
        return IsStopEnabled;
    }

    private bool CanPauseOrResume()
    {
        return IsPauseEnabled;
    }

    private async Task ReadAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "Nothing to read. Enter text first.";
            return;
        }

        SetInputReadSpeaking(true);
        SetSpeakingState(true);
        UpdateCommandStates();
        StatusMessage = "Reading text...";

        try
        {
            var result = await _readingService.ReadTextAsync(text).ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            SetPausedState(false);
            SetSpeakingState(false);
            SetInputReadSpeaking(false);
            UpdateCommandStates();
        }
    }

    private Task ClearAsync()
    {
        InputText = string.Empty;
        StatusMessage = "Input text cleared.";
        return Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var cancelStage = _externalReadStage;
        var stopStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "stop_command_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["isSpeaking"] = _isSpeaking.ToString(),
                ["isInputReadSpeaking"] = _isInputReadSpeaking.ToString(),
                ["isExternalReadActive"] = _isExternalReadActive.ToString(),
                ["externalReadStage"] = _externalReadStage.ToString(),
                ["hasExternalFocusedWindow"] = _hasExternalFocusedWindow.ToString(),
                ["focusedWindowText"] = _focusedWindowText
            });

        if (_activeExternalReadCancellationTokenSource is not null && IsExternalReadCancellationStage)
        {
            StatusMessage = "Canceling...";
            var stageAtCancelRequest = _externalReadStage;
            _activeExternalReadCancellationTokenSource.Cancel();
            SetPausedState(false);
            AppDiagnostics.Info(
                "external_read_cancel_requested",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["cancel_stage"] = GetCancelStageTag(cancelStage),
                    ["cancel_requested_stage"] = stageAtCancelRequest.ToString(),
                    ["tokenCanBeCanceled"] = _activeExternalReadCancellationTokenSource.Token.CanBeCanceled.ToString(),
                    ["tokenIsCancellationRequested"] = _activeExternalReadCancellationTokenSource.IsCancellationRequested.ToString(),
                    ["externalReadElapsedMs"] = _activeExternalReadStopwatch?.ElapsedMilliseconds.ToString(),
                    ["elapsedMs"] = stopStopwatch.ElapsedMilliseconds.ToString()
                });
            return;
        }

        _activeExternalReadCancellationTokenSource?.Cancel();
        if (_isExternalReadActive)
        {
            AppDiagnostics.Info(
                "external_read_stop_requested",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["cancel_stage"] = GetCancelStageTag(cancelStage),
                    ["tokenCanBeCanceled"] = _activeExternalReadCancellationTokenSource?.Token.CanBeCanceled.ToString(),
                    ["tokenIsCancellationRequested"] = _activeExternalReadCancellationTokenSource?.IsCancellationRequested.ToString(),
                    ["externalReadElapsedMs"] = _activeExternalReadStopwatch?.ElapsedMilliseconds.ToString()
                });
        }

        StatusMessage = "Stopping...";
        try
        {
            var result = await _readingService.StopAsync().ConfigureAwait(true);
            SetPausedState(false);
            StatusMessage = result.Message;
            AppDiagnostics.Info(
                "stop_command_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["success"] = result.Success.ToString(),
                    ["cancelled"] = result.WasCancelled.ToString(),
                    ["cancel_stage"] = GetCancelStageTag(cancelStage),
                    ["message"] = result.Message,
                    ["elapsedMs"] = stopStopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "stop_command_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopStopwatch.ElapsedMilliseconds.ToString()
                });
            StatusMessage = "Couldn't stop reading. Please try again.";
        }
    }

    private async Task TogglePauseAsync()
    {
        if (_isPaused)
        {
            var resumeResult = await _readingService.ResumeAsync().ConfigureAwait(true);
            if (resumeResult.Success)
            {
                SetPausedState(false);
            }

            StatusMessage = resumeResult.Message;
            return;
        }

        var pauseResult = await _readingService.PauseAsync().ConfigureAwait(true);
        if (pauseResult.Success)
        {
            SetPausedState(true);
        }

        StatusMessage = pauseResult.Message;
    }

    private async Task ReadSelectedTextAsync()
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var scope = AppDiagnostics.BeginScope(new Dictionary<string, string?>
        {
            ["externalCommandOperationId"] = operationId,
            ["externalCommand"] = "read_selected_text"
        });
        var stopwatch = Stopwatch.StartNew();
        var cancellationTokenSource = BeginExternalReadOperation(
            ReadingProgressStage.Retrieving,
            "Capturing selected text...");
        var progress = new Progress<ReadingProgressUpdate>(ApplyExternalReadProgress);
        AppDiagnostics.Info(
            "selected_workflow_command_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["trigger"] = "read_selected_text_command",
                ["hasExternalFocusedWindow"] = _hasExternalFocusedWindow.ToString(),
                ["focusedWindowText"] = _focusedWindowText,
                ["isInputReadSpeaking"] = _isInputReadSpeaking.ToString(),
                ["isSpeaking"] = _isSpeaking.ToString()
            });

        try
        {
            var result = await _readingService
                .ReadSelectedTextAsync(cancellationTokenSource.Token, progress)
                .ConfigureAwait(true);
            StatusMessage = result.Message;
            stopwatch.Stop();
            AppDiagnostics.Info(
                "selected_workflow_command_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["success"] = result.Success.ToString(),
                    ["cancelled"] = result.WasCancelled.ToString(),
                    ["message"] = result.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var cancelStage = _externalReadStage;
            StatusMessage = HasExternalReadReachedSpeech(cancelStage)
                ? "Reading stopped."
                : "Canceled before reading started.";
            AppDiagnostics.Warn(
                "selected_workflow_command_cancelled",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["cancel_stage"] = GetCancelStageTag(cancelStage),
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Error(
                "selected_workflow_command_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            throw;
        }
        finally
        {
            EndExternalReadOperation(cancellationTokenSource);
            SetPausedState(false);
            SetSpeakingState(false);
            SetExternalReadActive(false);
            UpdateCommandStates();
        }
    }

    private async Task ReadParagraphAsync()
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var scope = AppDiagnostics.BeginScope(new Dictionary<string, string?>
        {
            ["paragraphCommandOperationId"] = operationId,
            ["paragraphCommandTrigger"] = "read_paragraph_command",
            ["paragraphFocusedWindowText"] = _focusedWindowText
        });
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "paragraph_workflow_command_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["trigger"] = "read_paragraph_command",
                ["hasExternalFocusedWindow"] = _hasExternalFocusedWindow.ToString(),
                ["focusedWindowText"] = _focusedWindowText,
                ["isInputReadSpeaking"] = _isInputReadSpeaking.ToString(),
                ["isSpeaking"] = _isSpeaking.ToString()
            });

        SetExternalReadActive(true);
        SetSpeakingState(true);
        UpdateCommandStates();
        StatusMessage = "Retrieving paragraph...";

        try
        {
            var result = await _readingService.ReadParagraphAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
            stopwatch.Stop();
            AppDiagnostics.Info(
                "paragraph_workflow_command_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["success"] = result.Success.ToString(),
                    ["cancelled"] = result.WasCancelled.ToString(),
                    ["message"] = result.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "paragraph_workflow_command_cancelled",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Error(
                "paragraph_workflow_command_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            throw;
        }
        finally
        {
            SetPausedState(false);
            SetSpeakingState(false);
            SetExternalReadActive(false);
            UpdateCommandStates();
        }
    }

    private async Task ReadDocumentAsync()
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var scope = AppDiagnostics.BeginScope(new Dictionary<string, string?>
        {
            ["externalCommandOperationId"] = operationId,
            ["externalCommand"] = "read_document"
        });
        var stopwatch = Stopwatch.StartNew();
        var cancellationTokenSource = BeginExternalReadOperation(
            ReadingProgressStage.Retrieving,
            "Capturing document text...");
        var progress = new Progress<ReadingProgressUpdate>(ApplyExternalReadProgress);
        AppDiagnostics.Info(
            "document_workflow_command_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["trigger"] = "read_document_command",
                ["hasExternalFocusedWindow"] = _hasExternalFocusedWindow.ToString(),
                ["focusedWindowText"] = _focusedWindowText,
                ["isInputReadSpeaking"] = _isInputReadSpeaking.ToString(),
                ["isSpeaking"] = _isSpeaking.ToString()
            });

        try
        {
            var result = await _readingService
                .ReadDocumentAsync(cancellationTokenSource.Token, progress)
                .ConfigureAwait(true);
            StatusMessage = result.Message;
            stopwatch.Stop();
            AppDiagnostics.Info(
                "document_workflow_command_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["success"] = result.Success.ToString(),
                    ["cancelled"] = result.WasCancelled.ToString(),
                    ["message"] = result.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var cancelStage = _externalReadStage;
            StatusMessage = HasExternalReadReachedSpeech(cancelStage)
                ? "Reading stopped."
                : "Canceled before reading started.";
            AppDiagnostics.Warn(
                "document_workflow_command_cancelled",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["cancel_stage"] = GetCancelStageTag(cancelStage),
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Error(
                "document_workflow_command_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            throw;
        }
        finally
        {
            EndExternalReadOperation(cancellationTokenSource);
            SetPausedState(false);
            SetSpeakingState(false);
            SetExternalReadActive(false);
            UpdateCommandStates();
        }
    }

    private async Task PreviewVoiceAsync()
    {
        SetSpeakingState(true);
        UpdateCommandStates();
        StatusMessage = "Previewing voice...";

        try
        {
            var result = await _readingService.ReadTextAsync(VoicePreviewText).ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            SetPausedState(false);
            SetSpeakingState(false);
            UpdateCommandStates();
        }
    }

    private Task ResetHotkeysAsync()
    {
        if (!EnsurePremiumHotkeyCustomization())
        {
            return Task.CompletedTask;
        }

        _suppressHotkeyAutoApply = true;
        try
        {
            _hotkeyModifierPreset = HotkeyModifierPreset.AltShift;
            _readSelectedHotkeyKey = DefaultReadSelectedHotkeyKey;
            _readParagraphHotkeyKey = DefaultReadParagraphHotkeyKey;
            _readDocumentHotkeyKey = DefaultReadDocumentHotkeyKey;
            _stopHotkeyKey = DefaultStopHotkeyKey;
            UpdateHotkeyModifierWarningMessage();
            NotifyHotkeyPropertiesChanged();
        }
        finally
        {
            _suppressHotkeyAutoApply = false;
        }

        if (TryApplyCurrentHotkeys())
        {
            SetStatusMessage("Hotkeys reset to defaults.");
        }

        UpdateCommandStates();
        return Task.CompletedTask;
    }

    private void UpdateCommandStates()
    {
        if (ReadCommand is AsyncCommand readCommand)
        {
            readCommand.RaiseCanExecuteChanged();
        }

        if (ClearCommand is AsyncCommand clearCommand)
        {
            clearCommand.RaiseCanExecuteChanged();
        }

        if (StopCommand is AsyncCommand stopCommand)
        {
            stopCommand.RaiseCanExecuteChanged();
        }

        if (TogglePauseCommand is AsyncCommand togglePauseCommand)
        {
            togglePauseCommand.RaiseCanExecuteChanged();
        }

        if (ReadSelectedTextCommand is AsyncCommand readSelectedCommand)
        {
            readSelectedCommand.RaiseCanExecuteChanged();
        }

        if (PreviewVoiceCommand is AsyncCommand previewVoiceCommand)
        {
            previewVoiceCommand.RaiseCanExecuteChanged();
        }

        if (ReadParagraphCommand is AsyncCommand readParagraphCommand)
        {
            readParagraphCommand.RaiseCanExecuteChanged();
        }

        if (ReadDocumentCommand is AsyncCommand readDocumentCommand)
        {
            readDocumentCommand.RaiseCanExecuteChanged();
        }

        if (ResetHotkeysCommand is AsyncCommand resetHotkeysCommand)
        {
            resetHotkeysCommand.RaiseCanExecuteChanged();
        }
    }

    private void TrySetHotkeyKey(string? value, ref string field, string propertyName, string displayPropertyName)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        if (!EnsurePremiumHotkeyCustomization())
        {
            OnPropertyChanged(propertyName);
            OnPropertyChanged(displayPropertyName);
            return;
        }

        var previous = field;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(displayPropertyName);

        if (_suppressHotkeyAutoApply)
        {
            UpdateCommandStates();
            return;
        }

        if (!TryApplyCurrentHotkeys())
        {
            _suppressHotkeyAutoApply = true;
            try
            {
                field = previous;
                OnPropertyChanged(propertyName);
                OnPropertyChanged(displayPropertyName);
            }
            finally
            {
                _suppressHotkeyAutoApply = false;
            }

            ReapplyLastKnownGoodHotkeys();
        }

        UpdateCommandStates();
    }

    private void TrySetModifierPreset(HotkeyModifierPreset preset, bool selected)
    {
        if (!selected || _hotkeyModifierPreset == preset)
        {
            return;
        }

        if (!EnsurePremiumHotkeyCustomization())
        {
            NotifyHotkeyPropertiesChanged();
            return;
        }

        var previous = _hotkeyModifierPreset;
        _hotkeyModifierPreset = preset;
        UpdateHotkeyModifierWarningMessage();
        NotifyHotkeyPropertiesChanged();

        if (_suppressHotkeyAutoApply)
        {
            UpdateCommandStates();
            return;
        }

        if (!TryApplyCurrentHotkeys())
        {
            _suppressHotkeyAutoApply = true;
            try
            {
                _hotkeyModifierPreset = previous;
                UpdateHotkeyModifierWarningMessage();
                NotifyHotkeyPropertiesChanged();
            }
            finally
            {
                _suppressHotkeyAutoApply = false;
            }

            ReapplyLastKnownGoodHotkeys();
        }

        UpdateCommandStates();
    }

    private bool TryApplyCurrentHotkeys()
    {
        if (HasDuplicateHotkeyKeys())
        {
            SetStatusMessage("That shortcut is already in use. Choose a different letter.");
            return false;
        }

        _hotkeySettingsService.ModifierPreset = _hotkeyModifierPreset;
        _hotkeySettingsService.ReadSelectedKey = _readSelectedHotkeyKey;
        _hotkeySettingsService.ReadParagraphKey = _readParagraphHotkeyKey;
        _hotkeySettingsService.ReadDocumentKey = _readDocumentHotkeyKey;
        _hotkeySettingsService.StopKey = _stopHotkeyKey;

        if (!_hotkeySettingsService.Save())
        {
            SetStatusMessage("That shortcut is already in use. Choose a different letter.");
            return false;
        }

        if (_applyHotkeysRegistration is not null)
        {
            var result = _applyHotkeysRegistration.Invoke();
            if (!result.Success)
            {
                SetStatusMessage(result.StatusMessage);
                return false;
            }

            SetStatusMessage(result.StatusMessage);
        }
        else
        {
            SetStatusMessage($"Hotkeys updated: {HotkeyModifierLabel}+{_readSelectedHotkeyKey}, {HotkeyModifierLabel}+{_readParagraphHotkeyKey}, {HotkeyModifierLabel}+{_readDocumentHotkeyKey}, {HotkeyModifierLabel}+{_stopHotkeyKey}.");
        }

        _appliedHotkeyModifierPreset = _hotkeyModifierPreset;
        _appliedReadSelectedHotkeyKey = _readSelectedHotkeyKey;
        _appliedReadParagraphHotkeyKey = _readParagraphHotkeyKey;
        _appliedReadDocumentHotkeyKey = _readDocumentHotkeyKey;
        _appliedStopHotkeyKey = _stopHotkeyKey;

        return true;
    }

    private void ReapplyLastKnownGoodHotkeys()
    {
        _suppressHotkeyAutoApply = true;
        try
        {
            _hotkeyModifierPreset = _appliedHotkeyModifierPreset;
            _readSelectedHotkeyKey = _appliedReadSelectedHotkeyKey;
            _readParagraphHotkeyKey = _appliedReadParagraphHotkeyKey;
            _readDocumentHotkeyKey = _appliedReadDocumentHotkeyKey;
            _stopHotkeyKey = _appliedStopHotkeyKey;
            UpdateHotkeyModifierWarningMessage();
            NotifyHotkeyPropertiesChanged();
        }
        finally
        {
            _suppressHotkeyAutoApply = false;
        }

        _hotkeySettingsService.ModifierPreset = _hotkeyModifierPreset;
        _hotkeySettingsService.ReadSelectedKey = _readSelectedHotkeyKey;
        _hotkeySettingsService.ReadParagraphKey = _readParagraphHotkeyKey;
        _hotkeySettingsService.ReadDocumentKey = _readDocumentHotkeyKey;
        _hotkeySettingsService.StopKey = _stopHotkeyKey;
        _hotkeySettingsService.Save();
        _applyHotkeysRegistration?.Invoke();
    }

    private bool HasDuplicateHotkeyKeys()
    {
        return string.Equals(_readSelectedHotkeyKey, _readParagraphHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(_readSelectedHotkeyKey, _readDocumentHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(_readSelectedHotkeyKey, _stopHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(_readParagraphHotkeyKey, _readDocumentHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(_readParagraphHotkeyKey, _stopHotkeyKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(_readDocumentHotkeyKey, _stopHotkeyKey, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyHotkeyPropertiesChanged()
    {
        OnPropertyChanged(nameof(ReadSelectedHotkeyKey));
        OnPropertyChanged(nameof(ReadParagraphHotkeyKey));
        OnPropertyChanged(nameof(ReadDocumentHotkeyKey));
        OnPropertyChanged(nameof(StopHotkeyKey));
        OnPropertyChanged(nameof(IsAltShiftModifierSelected));
        OnPropertyChanged(nameof(IsCtrlShiftModifierSelected));
        OnPropertyChanged(nameof(IsCtrlAltModifierSelected));
        OnPropertyChanged(nameof(HotkeyModifierLabel));
        OnPropertyChanged(nameof(ReadSelectedHotkeyDisplay));
        OnPropertyChanged(nameof(ReadParagraphHotkeyDisplay));
        OnPropertyChanged(nameof(ReadDocumentHotkeyDisplay));
        OnPropertyChanged(nameof(StopHotkeyDisplay));
    }

    private void UpdateHotkeyModifierWarningMessage()
    {
        if (_hotkeyModifierPreset == HotkeyModifierPreset.CtrlAlt)
        {
            ShowTransientHotkeyModifierWarning("Warning: Ctrl+Alt may conflict with AltGr on some keyboard layouts.");
            return;
        }

        ClearHotkeyModifierWarning();
    }

    private void ShowTransientHotkeyModifierWarning(string message)
    {
        _hotkeyModifierWarningCts?.Cancel();
        _hotkeyModifierWarningMessage = message;
        OnPropertyChanged(nameof(HotkeyModifierWarningMessage));

        _hotkeyModifierWarningCts = new CancellationTokenSource();
        _ = ClearHotkeyModifierWarningAfterDelayAsync(_hotkeyModifierWarningCts.Token);
    }

    private async Task ClearHotkeyModifierWarningAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "hotkey_modifier_warning_clear_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return;
        }

        if (_hotkeyModifierWarningCts is null || _hotkeyModifierWarningCts.Token != cancellationToken)
        {
            return;
        }

        ClearHotkeyModifierWarning();
    }

    private void ClearHotkeyModifierWarning()
    {
        _hotkeyModifierWarningCts?.Cancel();
        _hotkeyModifierWarningCts = null;

        if (string.IsNullOrEmpty(_hotkeyModifierWarningMessage))
        {
            return;
        }

        _hotkeyModifierWarningMessage = string.Empty;
        OnPropertyChanged(nameof(HotkeyModifierWarningMessage));
    }

    private static string GetModifierLabel(HotkeyModifierPreset preset)
    {
        return preset switch
        {
            HotkeyModifierPreset.CtrlShift => "Ctrl+Shift",
            HotkeyModifierPreset.CtrlAlt => "Ctrl+Alt",
            _ => "Alt+Shift"
        };
    }

    private bool EnsurePremiumHotkeyCustomization()
    {
        if (HasPremiumHotkeyCustomization)
        {
            return true;
        }

        RaisePremiumHotkeysUpsell();
        SetStatusMessage(_premiumEntitlementSnapshot.State switch
        {
            PremiumEntitlementState.Checking => "Checking license...",
            PremiumEntitlementState.VerificationFailed => "Unable to verify license right now.",
            _ => "Custom hotkeys are available with Premium."
        });
        return false;
    }

    private void EnsureBasicHotkeysIfRequired()
    {
        if (HasPremiumHotkeyCustomization)
        {
            return;
        }

        _suppressHotkeyAutoApply = true;
        try
        {
            _hotkeyModifierPreset = HotkeyModifierPreset.AltShift;
            _readSelectedHotkeyKey = DefaultReadSelectedHotkeyKey;
            _readParagraphHotkeyKey = DefaultReadParagraphHotkeyKey;
            _readDocumentHotkeyKey = DefaultReadDocumentHotkeyKey;
            _stopHotkeyKey = DefaultStopHotkeyKey;
            NotifyHotkeyPropertiesChanged();
        }
        finally
        {
            _suppressHotkeyAutoApply = false;
        }

        _hotkeySettingsService.ModifierPreset = _hotkeyModifierPreset;
        _hotkeySettingsService.ReadSelectedKey = _readSelectedHotkeyKey;
        _hotkeySettingsService.ReadParagraphKey = _readParagraphHotkeyKey;
        _hotkeySettingsService.ReadDocumentKey = _readDocumentHotkeyKey;
        _hotkeySettingsService.StopKey = _stopHotkeyKey;
        _hotkeySettingsService.Save();
    }

    private void RaisePremiumHotkeysUpsell()
    {
        bool canShowPurchase = _premiumEntitlementSnapshot.State == PremiumEntitlementState.VerifiedNotOwned;
        string message = _premiumEntitlementSnapshot.State switch
        {
            PremiumEntitlementState.Checking =>
                "RightSpeak is still checking your Microsoft Store entitlement. Try again in a moment.",
            PremiumEntitlementState.VerificationFailed =>
                "RightSpeak could not verify your Microsoft Store entitlement right now. Please ensure Microsoft Store is signed in and try again.",
            _ =>
                "Custom hotkeys are a Premium feature. Upgrade to RightSpeak Premium to unlock full hotkey customization."
        };
        PremiumUpsellRequested?.Invoke(
            this,
            new PremiumUpsellRequest(
                "Custom hotkeys",
                message,
                canShowPurchase));
    }

    public void ApplyPremiumEntitlementSnapshot(PremiumEntitlementSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        bool previousHasPremium = HasPremiumHotkeyCustomization;
        _premiumEntitlementSnapshot = snapshot;

        if (previousHasPremium && !HasPremiumHotkeyCustomization)
        {
            EnsureBasicHotkeysIfRequired();
            SetStatusMessage("Custom hotkeys are now locked to Basic defaults.");
        }

        OnPropertyChanged(nameof(ApplicationAccessTierText));
        UpdateCommandStates();
    }

    private bool HasPremiumHotkeyCustomization => _premiumEntitlementSnapshot.HasPremium;

    private bool IsExternalReadCancellationStage =>
        _isExternalReadActive &&
        _externalReadStage is ReadingProgressStage.Focusing or ReadingProgressStage.Retrieving or ReadingProgressStage.PreparingAudio;

    private CancellationTokenSource BeginExternalReadOperation(ReadingProgressStage initialStage, string statusMessage)
    {
        _activeExternalReadCancellationTokenSource?.Dispose();
        var cancellationTokenSource = new CancellationTokenSource();
        _activeExternalReadCancellationTokenSource = cancellationTokenSource;
        _activeExternalReadStopwatch = Stopwatch.StartNew();

        SetExternalReadActive(true);
        SetSpeakingState(true);
        SetExternalReadStage(initialStage);
        StatusMessage = statusMessage;
        UpdateCommandStates();
        return cancellationTokenSource;
    }

    private void EndExternalReadOperation(CancellationTokenSource cancellationTokenSource)
    {
        if (ReferenceEquals(_activeExternalReadCancellationTokenSource, cancellationTokenSource))
        {
            _activeExternalReadCancellationTokenSource = null;
        }

        _activeExternalReadStopwatch?.Stop();
        _activeExternalReadStopwatch = null;
        cancellationTokenSource.Dispose();
        SetExternalReadStage(ReadingProgressStage.Idle);
    }

    private void ApplyExternalReadProgress(ReadingProgressUpdate update)
    {
        if (_activeExternalReadCancellationTokenSource is null)
        {
            return;
        }

        var previousStage = _externalReadStage;
        var stageChanged = previousStage != update.Stage;
        if (stageChanged || !string.IsNullOrWhiteSpace(update.Message))
        {
            AppDiagnostics.Info(
                "external_read_progress_updated",
                new Dictionary<string, string?>
                {
                    ["previousStage"] = previousStage.ToString(),
                    ["currentStage"] = update.Stage.ToString(),
                    ["stageChanged"] = stageChanged.ToString(),
                    ["message"] = update.Message,
                    ["elapsedMs"] = _activeExternalReadStopwatch?.ElapsedMilliseconds.ToString()
                });
        }

        SetExternalReadStage(update.Stage);
        if (!string.IsNullOrWhiteSpace(update.Message))
        {
            StatusMessage = update.Message;
        }

        UpdateCommandStates();
    }

    private void SetExternalReadStage(ReadingProgressStage stage)
    {
        if (_externalReadStage == stage)
        {
            return;
        }

        _externalReadStage = stage;
        OnPropertyChanged(nameof(IsPauseEnabled));
        OnPropertyChanged(nameof(ExternalReadActionText));
    }

    private static bool HasExternalReadReachedSpeech(ReadingProgressStage stage)
    {
        return stage == ReadingProgressStage.Speaking;
    }

    private static string GetCancelStageTag(ReadingProgressStage stage)
    {
        return stage switch
        {
            ReadingProgressStage.Focusing => "focusing",
            ReadingProgressStage.Retrieving => "retrieval",
            ReadingProgressStage.PreparingAudio => "preparing_audio",
            ReadingProgressStage.Speaking => "speech",
            _ => "idle"
        };
    }

    private void SetInputReadSpeaking(bool isSpeaking)
    {
        if (_isInputReadSpeaking == isSpeaking)
        {
            return;
        }

        _isInputReadSpeaking = isSpeaking;
        OnPropertyChanged(nameof(IsInputReadSpeaking));
        OnPropertyChanged(nameof(IsExternalReadSectionEnabled));
        OnPropertyChanged(nameof(IsExternalReadsEnabled));
        OnPropertyChanged(nameof(IsVoiceModelButtonsEnabled));
        OnPropertyChanged(nameof(IsStopEnabled));
        OnPropertyChanged(nameof(IsPauseEnabled));
        OnPropertyChanged(nameof(ExternalReadActionText));
    }

    private void SetSpeakingState(bool isSpeaking)
    {
        if (_isSpeaking == isSpeaking)
        {
            return;
        }

        _isSpeaking = isSpeaking;
        OnPropertyChanged(nameof(IsExternalReadsEnabled));
        OnPropertyChanged(nameof(IsStopEnabled));
        OnPropertyChanged(nameof(IsPauseEnabled));
        OnPropertyChanged(nameof(ExternalReadActionText));
    }

    private void SetPausedState(bool isPaused)
    {
        if (_isPaused == isPaused)
        {
            return;
        }

        _isPaused = isPaused;
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseButtonText));
    }

    private void SetExternalReadActive(bool isActive)
    {
        if (_isExternalReadActive == isActive)
        {
            return;
        }

        _isExternalReadActive = isActive;
        OnPropertyChanged(nameof(IsInputReadEnabled));
        OnPropertyChanged(nameof(IsVoiceModelButtonsEnabled));
        OnPropertyChanged(nameof(IsStopEnabled));
        OnPropertyChanged(nameof(IsPauseEnabled));
        OnPropertyChanged(nameof(ExternalReadActionText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private string GetVoiceOptionLabel(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return SystemDefaultVoiceOption;
        }

        return _voiceOptionLabelByName.TryGetValue(voiceName, out var label)
            ? label
            : SystemDefaultVoiceOption;
    }

    private string? GetVoiceNameForOption(string optionLabel)
    {
        return _voiceNameByOptionLabel.TryGetValue(optionLabel, out var voiceName)
            ? voiceName
            : null;
    }

    private static (IReadOnlyList<string> Options, Dictionary<string, string?> NameByOptionLabel, Dictionary<string, string> OptionLabelByName) BuildVoiceOptions(IReadOnlyList<SpeechVoice> installedVoices)
    {
        var options = new List<string> { SystemDefaultVoiceOption };
        var nameByOptionLabel = new Dictionary<string, string?>(StringComparer.Ordinal);
        var optionLabelByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        nameByOptionLabel[SystemDefaultVoiceOption] = null;

        foreach (var voice in installedVoices)
        {
            var label = $"{voice.DisplayName} ({voice.Engine})";
            options.Add(label);
            nameByOptionLabel[label] = voice.Name;
            optionLabelByName[voice.Name] = label;
        }

        return (options, nameByOptionLabel, optionLabelByName);
    }
}
