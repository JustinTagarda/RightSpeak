using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.Views;

namespace RightSpeak.ViewModels;

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
    private readonly IPremiumEntitlementService? _premiumEntitlementService;
    private readonly IPremiumPurchaseService? _premiumPurchaseService;
    private readonly IStoreNavigationService? _storeNavigationService;
    private readonly Func<CancellationToken, Task>? _requestStoreUpdateInstallAsync;
    private readonly string _displayVersionText;
    private IReadOnlyList<string> _voiceOptions;
    private Dictionary<string, string?> _voiceNameByOptionLabel;
    private Dictionary<string, string> _voiceOptionLabelByName;
    private readonly Func<(bool Success, string StatusMessage)>? _applyHotkeysRegistration;
    private readonly Func<string, bool>? _applyTheme;
    private readonly Func<string, Task<bool>>? _promptPremiumUpgradeAsyncOverride;
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
    private int _speechRate;
    private string _selectedVoiceOption = SystemDefaultVoiceOption;
    private string _selectedTheme = AppThemes.Light;
    private bool _isPremiumOwned;
    private bool _isPremiumBusy;
    private bool _isPremiumUiReady;
    private bool _shouldShowPremiumUi = true;
    private bool _isStoreUpdateAvailable;
    private bool _isStoreUpdateBusy;
    private bool _isStoreUpdateProgressVisible;
    private int _storeUpdateProgressPercent;
    private string _storeUpdateProgressPhase = string.Empty;
    private string _storeUpdateProgressDetail = string.Empty;
    private string _storeUpdateProgressResult = string.Empty;
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
        string? displayVersionText = null,
        IPremiumEntitlementService? premiumEntitlementService = null,
        IPremiumPurchaseService? premiumPurchaseService = null,
        IStoreNavigationService? storeNavigationService = null,
        Func<CancellationToken, Task>? requestStoreUpdateInstallAsync = null,
        Func<string, Task<bool>>? promptPremiumUpgradeAsyncOverride = null)
    {
        _readingService = readingService ?? throw new ArgumentNullException(nameof(readingService));
        _hotkeySettingsService = hotkeySettingsService ?? throw new ArgumentNullException(nameof(hotkeySettingsService));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        _applyHotkeysRegistration = applyHotkeysRegistration;
        _isAnalyzeAvailable = isAnalyzeAvailable;
        _applyTheme = applyTheme;
        _displayVersionText = string.IsNullOrWhiteSpace(displayVersionText) ? "0.0.0.0" : displayVersionText;
        _premiumEntitlementService = premiumEntitlementService;
        _premiumPurchaseService = premiumPurchaseService;
        _storeNavigationService = storeNavigationService;
        _requestStoreUpdateInstallAsync = requestStoreUpdateInstallAsync;
        _promptPremiumUpgradeAsyncOverride = promptPremiumUpgradeAsyncOverride;

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
        UpgradeToPremiumCommand = new AsyncCommand(UpgradeToPremiumAsync, CanUpgradeToPremium);
        UpdateAppCommand = new AsyncCommand(UpdateAppAsync, CanUpdateApp);
        RefreshPremiumStatusCommand = new AsyncCommand(() => RefreshPremiumStatusAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

            if (!_isPremiumOwned && !IsBasicAllowedVoiceOption(value))
            {
                SetStatusMessage("That voice is available in Premium. Use Upgrade to Premium.");
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
    public string DisplayVersionText => _displayVersionText;
    public bool IsPremiumOwned => _isPremiumOwned;
    public bool IsPremiumBusy => _isPremiumBusy;
    public bool IsPremiumUiReady => _isPremiumUiReady;
    public bool IsUpgradeButtonVisible => _isPremiumUiReady && _shouldShowPremiumUi && !_isPremiumOwned && _premiumPurchaseService is not null;
    public bool IsUpgradeAvailable => !_isPremiumOwned && !_isPremiumBusy && _premiumPurchaseService is not null;
    public bool IsUpdateButtonVisible => _isStoreUpdateAvailable;
    public bool IsUpdateAvailable => _isStoreUpdateAvailable && !_isStoreUpdateBusy;
    public bool IsUpdateProgressVisible => _isStoreUpdateProgressVisible;
    public int StoreUpdateProgressPercent => _storeUpdateProgressPercent;
    public string StoreUpdateProgressPhase => _storeUpdateProgressPhase;
    public string StoreUpdateProgressDetail => _storeUpdateProgressDetail;
    public string StoreUpdateProgressResult => _storeUpdateProgressResult;
    public string AppModeText => _isPremiumOwned ? "Premium" : "Basic";
    public string UpgradeTooltipText => _isPremiumOwned
        ? "Premium is active."
        : "Upgrade to Premium to unlock full access.";

    public ICommand ReadCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PreviewVoiceCommand { get; }
    public ICommand ReadSelectedTextCommand { get; }
    public ICommand ReadParagraphCommand { get; }
    public ICommand ReadDocumentCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand ResetHotkeysCommand { get; }
    public ICommand UpgradeToPremiumCommand { get; }
    public ICommand UpdateAppCommand { get; }
    public ICommand RefreshPremiumStatusCommand { get; }

    public void SetStatusMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusMessage = message;
    }

    public async Task InitializePremiumStatusAsync(CancellationToken cancellationToken = default)
    {
        await RefreshPremiumStatusAsync(cancellationToken).ConfigureAwait(true);
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

    private bool CanUpgradeToPremium()
    {
        return IsUpgradeAvailable;
    }

    private bool CanUpdateApp()
    {
        return IsUpdateAvailable && _requestStoreUpdateInstallAsync is not null;
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

    private async Task ResetHotkeysAsync()
    {
        if (!_isPremiumOwned)
        {
            await PromptHotkeyPremiumUpgradeAsync().ConfigureAwait(true);
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
    }

    private async Task UpgradeToPremiumAsync()
    {
        if (_premiumPurchaseService is null || _isPremiumBusy || _isPremiumOwned)
        {
            return;
        }

        var dialog = new ConfirmActionWindow(
            "Premium feature",
            "Upgrade to Premium to unlock full access.",
            confirmText: "Upgrade to Premium",
            cancelText: "Not now");

        if (!ShowOwnedDialog(dialog))
        {
            SetStatusMessage("That action is available in Premium.");
            return;
        }

        SetPremiumBusy(true);
        try
        {
            var result = await ExecutePremiumPurchaseWithMainWindowRecoveryAsync(
                cancellationToken => _premiumPurchaseService.PurchasePremiumAsync(cancellationToken),
                CancellationToken.None).ConfigureAwait(true);
            SetStatusMessage(result.Message);
            if (result.Outcome is PremiumPurchaseOutcome.Succeeded or PremiumPurchaseOutcome.AlreadyOwned)
            {
                await RefreshPremiumStatusAsync().ConfigureAwait(true);
                if (_isPremiumOwned)
                {
                    SetStatusMessage("Premium unlocked successfully.");
                }
            }
            else if (result.Outcome is PremiumPurchaseOutcome.NotSupported && _storeNavigationService is not null)
            {
                _storeNavigationService.OpenPremiumAddOnPage();
            }
        }
        finally
        {
            SetPremiumBusy(false);
        }
    }

    private async Task UpdateAppAsync()
    {
        if (_requestStoreUpdateInstallAsync is null)
        {
            return;
        }

        SetStoreUpdateBusy(true);
        try
        {
            await _requestStoreUpdateInstallAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_update_command_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
        finally
        {
            SetStoreUpdateBusy(false);
        }
    }

    private async Task RefreshPremiumStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_premiumEntitlementService is null)
        {
            SetPremiumUiReady(true);
            return;
        }

        try
        {
            var state = await _premiumEntitlementService.RefreshEntitlementAsync(cancellationToken).ConfigureAwait(true);
            SetShouldShowPremiumUi(state.ShouldShowPremiumUi);
            SetPremiumOwned(state.IsPremiumOwned);
            SetStatusMessage(state.Message);
        }
        finally
        {
            SetPremiumUiReady(_shouldShowPremiumUi);
        }
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

        if (UpgradeToPremiumCommand is AsyncCommand upgradeToPremiumCommand)
        {
            upgradeToPremiumCommand.RaiseCanExecuteChanged();
        }

        if (UpdateAppCommand is AsyncCommand updateAppCommand)
        {
            updateAppCommand.RaiseCanExecuteChanged();
        }
    }

    public void SetStoreUpdateState(StoreUpdateState state)
    {
        SetStoreUpdateAvailable(state.IsSupported && state.IsUpdateAvailable);
        SetStoreUpdateBusy(state.IsBusy);
        SetStoreUpdateProgressVisible(state.IsProgressVisible);
        SetStoreUpdateProgressPercent(state.ProgressPercent);
        SetStoreUpdateProgressPhase(state.ProgressPhase);
        SetStoreUpdateProgressDetail(state.ProgressDetail ?? string.Empty);
        SetStoreUpdateProgressResult(state.ProgressResult ?? string.Empty);
    }

    private void SetPremiumOwned(bool isPremiumOwned)
    {
        if (_isPremiumOwned == isPremiumOwned)
        {
            return;
        }

        _isPremiumOwned = isPremiumOwned;
        OnPropertyChanged(nameof(IsPremiumOwned));
        OnPropertyChanged(nameof(IsUpgradeButtonVisible));
        OnPropertyChanged(nameof(IsUpgradeAvailable));
        OnPropertyChanged(nameof(AppModeText));
        OnPropertyChanged(nameof(UpgradeTooltipText));
        UpdateCommandStates();
    }

    private void SetShouldShowPremiumUi(bool shouldShowPremiumUi)
    {
        if (_shouldShowPremiumUi == shouldShowPremiumUi)
        {
            return;
        }

        _shouldShowPremiumUi = shouldShowPremiumUi;
        OnPropertyChanged(nameof(IsUpgradeButtonVisible));
    }

    private void SetPremiumBusy(bool isPremiumBusy)
    {
        if (_isPremiumBusy == isPremiumBusy)
        {
            return;
        }

        _isPremiumBusy = isPremiumBusy;
        OnPropertyChanged(nameof(IsPremiumBusy));
        OnPropertyChanged(nameof(IsUpgradeAvailable));
        UpdateCommandStates();
    }

    private void SetPremiumUiReady(bool isPremiumUiReady)
    {
        if (_isPremiumUiReady == isPremiumUiReady)
        {
            return;
        }

        _isPremiumUiReady = isPremiumUiReady;
        OnPropertyChanged(nameof(IsPremiumUiReady));
        OnPropertyChanged(nameof(IsUpgradeButtonVisible));
    }

    private void SetStoreUpdateAvailable(bool isStoreUpdateAvailable)
    {
        if (_isStoreUpdateAvailable == isStoreUpdateAvailable)
        {
            return;
        }

        _isStoreUpdateAvailable = isStoreUpdateAvailable;
        OnPropertyChanged(nameof(IsUpdateButtonVisible));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        UpdateCommandStates();
    }

    private void SetStoreUpdateBusy(bool isStoreUpdateBusy)
    {
        if (_isStoreUpdateBusy == isStoreUpdateBusy)
        {
            return;
        }

        _isStoreUpdateBusy = isStoreUpdateBusy;
        OnPropertyChanged(nameof(IsUpdateAvailable));
        UpdateCommandStates();
    }

    private void SetStoreUpdateProgressVisible(bool isVisible)
    {
        if (_isStoreUpdateProgressVisible == isVisible)
        {
            return;
        }

        _isStoreUpdateProgressVisible = isVisible;
        OnPropertyChanged(nameof(IsUpdateProgressVisible));
    }

    private void SetStoreUpdateProgressPercent(int progressPercent)
    {
        var clamped = progressPercent < 0 ? 0 : progressPercent > 100 ? 100 : progressPercent;
        if (_storeUpdateProgressPercent == clamped)
        {
            return;
        }

        _storeUpdateProgressPercent = clamped;
        OnPropertyChanged(nameof(StoreUpdateProgressPercent));
    }

    private void SetStoreUpdateProgressPhase(string phase)
    {
        if (string.Equals(_storeUpdateProgressPhase, phase, StringComparison.Ordinal))
        {
            return;
        }

        _storeUpdateProgressPhase = phase;
        OnPropertyChanged(nameof(StoreUpdateProgressPhase));
    }

    private void SetStoreUpdateProgressDetail(string detail)
    {
        if (string.Equals(_storeUpdateProgressDetail, detail, StringComparison.Ordinal))
        {
            return;
        }

        _storeUpdateProgressDetail = detail;
        OnPropertyChanged(nameof(StoreUpdateProgressDetail));
    }

    private void SetStoreUpdateProgressResult(string result)
    {
        if (string.Equals(_storeUpdateProgressResult, result, StringComparison.Ordinal))
        {
            return;
        }

        _storeUpdateProgressResult = result;
        OnPropertyChanged(nameof(StoreUpdateProgressResult));
    }

    private void TrySetHotkeyKey(string? value, ref string field, string propertyName, string displayPropertyName)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        if (!_isPremiumOwned)
        {
            _ = PromptHotkeyPremiumUpgradeAsync();
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

        if (!_isPremiumOwned)
        {
            _ = PromptHotkeyPremiumUpgradeAsync();
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

    private bool IsBasicAllowedVoiceOption(string optionLabel)
    {
        if (string.Equals(optionLabel, SystemDefaultVoiceOption, StringComparison.Ordinal))
        {
            return true;
        }

        var voiceName = GetVoiceNameForOption(optionLabel);
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return false;
        }

        return voiceName.Contains("ljspeech", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> PromptPremiumUpgradeAsync(string message)
    {
        var dialog = new ConfirmActionWindow(
            "Premium feature",
            $"{message} Upgrade to unlock this feature.",
            confirmText: "Upgrade to Premium",
            cancelText: "Not now");

        if (!ShowOwnedDialog(dialog))
        {
            return false;
        }

        if (_premiumPurchaseService is null)
        {
            _storeNavigationService?.OpenPremiumAddOnPage();
            return false;
        }

        var result = await ExecutePremiumPurchaseWithMainWindowRecoveryAsync(
            cancellationToken => _premiumPurchaseService.PurchasePremiumAsync(cancellationToken),
            CancellationToken.None).ConfigureAwait(true);
        SetStatusMessage(result.Message);
        if (result.Outcome is PremiumPurchaseOutcome.Succeeded or PremiumPurchaseOutcome.AlreadyOwned)
        {
            await RefreshPremiumStatusAsync().ConfigureAwait(true);
            return _isPremiumOwned;
        }

        if (result.Outcome is PremiumPurchaseOutcome.NotSupported)
        {
            _storeNavigationService?.OpenPremiumAddOnPage();
        }

        return false;
    }

    private Task<bool> PromptHotkeyPremiumUpgradeAsync()
    {
        if (_promptPremiumUpgradeAsyncOverride is not null)
        {
            return _promptPremiumUpgradeAsyncOverride("Hotkey customization is available in Premium.");
        }

        return PromptPremiumUpgradeAsync("Hotkey customization is available in Premium.");
    }

    private static async Task<PremiumPurchaseResult> ExecutePremiumPurchaseWithMainWindowRecoveryAsync(
        Func<CancellationToken, Task<PremiumPurchaseResult>> purchaseOperation,
        CancellationToken cancellationToken)
    {
        if (purchaseOperation is null)
        {
            throw new ArgumentNullException(nameof(purchaseOperation));
        }

        try
        {
            return await purchaseOperation(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            RecoverMainWindowAccessibility();
        }
    }

    private static bool ShowOwnedDialog(System.Windows.Window dialog)
    {
        var owner = System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(window => window.IsActive) ??
            System.Windows.Application.Current?.MainWindow;

        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            dialog.Topmost = owner.Topmost;
        }

        return dialog.ShowDialog() == true;
    }

    private static void RecoverMainWindowAccessibility()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
            {
                return;
            }

            var initiatingWindow = app.Windows
                .OfType<System.Windows.Window>()
                .FirstOrDefault(window => window.IsActive);
            RecoverWindowAccessibility(initiatingWindow);
            RecoverWindowAccessibility(app.MainWindow);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "premium_purchase_main_window_recovery_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private static void RecoverWindowAccessibility(System.Windows.Window? window)
    {
        if (window is null || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        window.IsEnabled = true;
        if (window.WindowState == System.Windows.WindowState.Minimized)
        {
            window.WindowState = System.Windows.WindowState.Normal;
        }

        window.Activate();
        _ = window.Focus();
        System.Windows.Input.Keyboard.Focus(window);
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

