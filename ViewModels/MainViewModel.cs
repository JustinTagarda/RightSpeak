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
    private readonly IReadOnlyList<string> _voiceOptions;
    private readonly Dictionary<string, string?> _voiceNameByOptionLabel;
    private readonly Dictionary<string, string> _voiceOptionLabelByName;
    private readonly Func<(bool Success, string StatusMessage)>? _applyHotkeysRegistration;
    private CancellationTokenSource? _hotkeyModifierWarningCts;

    private string _inputText = string.Empty;
    private string _statusMessage = "Select text in another app, then choose a read action.";
    private bool _isSpeaking;
    private bool _isExternalReadActive;
    private bool _isManualReadSpeaking;
    private bool _hasExternalFocusedWindow;
    private bool _suppressHotkeyAutoApply;
    private string _focusedWindowText = "Current app";
    private string _hotkeyModifierWarningMessage = string.Empty;
    private int _speechRate;
    private string _selectedVoiceOption = SystemDefaultVoiceOption;
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

    public MainViewModel(
        IReadingService readingService,
        IHotkeySettingsService hotkeySettingsService,
        Func<(bool Success, string StatusMessage)>? applyHotkeysRegistration = null)
    {
        _readingService = readingService ?? throw new ArgumentNullException(nameof(readingService));
        _hotkeySettingsService = hotkeySettingsService ?? throw new ArgumentNullException(nameof(hotkeySettingsService));
        _applyHotkeysRegistration = applyHotkeysRegistration;

        _speechRate = _readingService.SpeechRate;
        _inputText = _readingService.TypedTextDraft ?? string.Empty;
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
        ResetHotkeysCommand = new AsyncCommand(ResetHotkeysAsync, CanResetHotkeys);
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

    public bool IsManualReadSpeaking => _isManualReadSpeaking;
    public bool IsManualReadEnabled => !_isExternalReadActive;
    public bool IsExternalReadsEnabled => !_isManualReadSpeaking && !_isSpeaking && _hasExternalFocusedWindow;

    public ICommand ReadCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PreviewVoiceCommand { get; }
    public ICommand ReadSelectedTextCommand { get; }
    public ICommand ReadParagraphCommand { get; }
    public ICommand ReadDocumentCommand { get; }
    public ICommand StopCommand { get; }
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
        return _isSpeaking;
    }

    private async Task ReadAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "Nothing to read. Enter text first.";
            return;
        }

        SetManualReadSpeaking(true);
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
            SetSpeakingState(false);
            SetManualReadSpeaking(false);
            UpdateCommandStates();
        }
    }

    private Task ClearAsync()
    {
        InputText = string.Empty;
        StatusMessage = "Manual text cleared.";
        return Task.CompletedTask;
    }

    private async Task StopAsync()
    {
        StatusMessage = "Stopping...";
        var result = await _readingService.StopAsync().ConfigureAwait(true);
        StatusMessage = result.Message;
    }

    private async Task ReadSelectedTextAsync()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "selected_workflow_command_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["trigger"] = "read_selected_text_command",
                ["hasExternalFocusedWindow"] = _hasExternalFocusedWindow.ToString(),
                ["focusedWindowText"] = _focusedWindowText,
                ["isManualReadSpeaking"] = _isManualReadSpeaking.ToString(),
                ["isSpeaking"] = _isSpeaking.ToString()
            });

        SetExternalReadActive(true);
        SetSpeakingState(true);
        UpdateCommandStates();
        StatusMessage = "Retrieving selected text...";

        try
        {
            var result = await _readingService.ReadSelectedTextAsync().ConfigureAwait(true);
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
            AppDiagnostics.Warn(
                "selected_workflow_command_cancelled",
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
            SetSpeakingState(false);
            SetExternalReadActive(false);
            UpdateCommandStates();
        }
    }

    private async Task ReadParagraphAsync()
    {
        SetExternalReadActive(true);
        SetSpeakingState(true);
        UpdateCommandStates();
        StatusMessage = "Retrieving paragraph...";

        try
        {
            var result = await _readingService.ReadParagraphAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            SetSpeakingState(false);
            SetExternalReadActive(false);
            UpdateCommandStates();
        }
    }

    private Task ReadDocumentAsync()
    {
        AppDiagnostics.Warn(
            "focused_read_document_temporarily_disabled_pending_fix",
            new Dictionary<string, string?>
            {
                ["focusedWindowText"] = _focusedWindowText,
                ["reason"] = "external_read_document_unstable_pending_final_fix"
            });
        StatusMessage = "Read Document (external app) is temporarily disabled pending final fix.";
        return Task.CompletedTask;
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
            SetSpeakingState(false);
            UpdateCommandStates();
        }
    }

    private Task ResetHotkeysAsync()
    {
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

    private void SetManualReadSpeaking(bool isSpeaking)
    {
        if (_isManualReadSpeaking == isSpeaking)
        {
            return;
        }

        _isManualReadSpeaking = isSpeaking;
        OnPropertyChanged(nameof(IsManualReadSpeaking));
        OnPropertyChanged(nameof(IsExternalReadsEnabled));
    }

    private void SetSpeakingState(bool isSpeaking)
    {
        if (_isSpeaking == isSpeaking)
        {
            return;
        }

        _isSpeaking = isSpeaking;
        OnPropertyChanged(nameof(IsExternalReadsEnabled));
    }

    private void SetExternalReadActive(bool isActive)
    {
        if (_isExternalReadActive == isActive)
        {
            return;
        }

        _isExternalReadActive = isActive;
        OnPropertyChanged(nameof(IsManualReadEnabled));
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
