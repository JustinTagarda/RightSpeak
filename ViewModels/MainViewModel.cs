using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Services;

namespace RightSpeak.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string SystemDefaultVoiceOption = "System default";
    private const string VoicePreviewText = "This is a preview of the current voice and speaking rate.";

    private readonly IReadingService _readingService;
    private readonly IHotkeySettingsService _hotkeySettingsService;
    private readonly IReadOnlyList<string> _voiceOptions;
    private string _inputText = string.Empty;
    private string _statusMessage = "Enter text, then click Read.";
    private bool _isSpeaking;
    private int _speechRate;
    private string _selectedVoiceOption = SystemDefaultVoiceOption;
    private string _readSelectedHotkeyKey = "R";
    private string _readTypedTextHotkeyKey = "T";
    private string _stopHotkeyKey = "X";

    public MainViewModel(IReadingService readingService, IHotkeySettingsService hotkeySettingsService)
    {
        _readingService = readingService ?? throw new ArgumentNullException(nameof(readingService));
        _hotkeySettingsService = hotkeySettingsService ?? throw new ArgumentNullException(nameof(hotkeySettingsService));
        _speechRate = _readingService.SpeechRate;
        _voiceOptions = BuildVoiceOptions(_readingService.AvailableVoices);
        _selectedVoiceOption = _readingService.SelectedVoiceName ?? SystemDefaultVoiceOption;
        _readSelectedHotkeyKey = _hotkeySettingsService.ReadSelectedKey;
        _readTypedTextHotkeyKey = _hotkeySettingsService.ReadTypedTextKey;
        _stopHotkeyKey = _hotkeySettingsService.StopKey;
        ReadCommand = new AsyncCommand(ReadAsync, CanRead);
        PreviewVoiceCommand = new AsyncCommand(PreviewVoiceAsync, CanReadSelectedText);
        ReadSelectedTextCommand = new AsyncCommand(ReadSelectedTextAsync, CanReadSelectedText);
        ReadParagraphCommand = new AsyncCommand(ReadParagraphAsync, CanReadSelectedText);
        ReadDocumentCommand = new AsyncCommand(ReadDocumentAsync, CanReadSelectedText);
        StopCommand = new AsyncCommand(StopAsync, CanStop);
        ApplyHotkeysCommand = new AsyncCommand(ApplyHotkeysAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? HotkeysApplyRequested;

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
            _readingService.SelectedVoiceName = string.Equals(value, SystemDefaultVoiceOption, StringComparison.Ordinal)
                ? null
                : value;
            _selectedVoiceOption = _readingService.SelectedVoiceName ?? SystemDefaultVoiceOption;
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
        set
        {
            if (string.Equals(value, _readSelectedHotkeyKey, StringComparison.Ordinal))
            {
                return;
            }

            _readSelectedHotkeyKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReadSelectedHotkeyDisplay));
        }
    }

    public string ReadTypedTextHotkeyKey
    {
        get => _readTypedTextHotkeyKey;
        set
        {
            if (string.Equals(value, _readTypedTextHotkeyKey, StringComparison.Ordinal))
            {
                return;
            }

            _readTypedTextHotkeyKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReadTypedTextHotkeyDisplay));
        }
    }

    public string StopHotkeyKey
    {
        get => _stopHotkeyKey;
        set
        {
            if (string.Equals(value, _stopHotkeyKey, StringComparison.Ordinal))
            {
                return;
            }

            _stopHotkeyKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StopHotkeyDisplay));
        }
    }

    public string ReadSelectedHotkeyDisplay => $"Read Selected: Ctrl+Shift+{_readSelectedHotkeyKey}";
    public string ReadTypedTextHotkeyDisplay => $"Read Typed: Ctrl+Shift+{_readTypedTextHotkeyKey}";
    public string StopHotkeyDisplay => $"Stop: Ctrl+Shift+{_stopHotkeyKey}";

    public ICommand ReadCommand { get; }

    public ICommand PreviewVoiceCommand { get; }

    public ICommand ReadSelectedTextCommand { get; }
    public ICommand ReadParagraphCommand { get; }
    public ICommand ReadDocumentCommand { get; }

    public ICommand StopCommand { get; }
    public ICommand ApplyHotkeysCommand { get; }

    public void SetStatusMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusMessage = message;
    }

    private bool CanRead()
    {
        return !_isSpeaking && !string.IsNullOrWhiteSpace(InputText);
    }

    private bool CanReadSelectedText()
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

        _isSpeaking = true;
        UpdateCommandStates();
        StatusMessage = "Reading text...";

        try
        {
            var result = await _readingService.ReadTextAsync(text).ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            _isSpeaking = false;
            UpdateCommandStates();
        }
    }

    private async Task StopAsync()
    {
        StatusMessage = "Stopping...";
        var result = await _readingService.StopAsync().ConfigureAwait(true);
        StatusMessage = result.Message;
    }

    private async Task ReadSelectedTextAsync()
    {
        _isSpeaking = true;
        UpdateCommandStates();
        StatusMessage = "Retrieving selected text...";

        try
        {
            var result = await _readingService.ReadSelectedTextAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            _isSpeaking = false;
            UpdateCommandStates();
        }
    }

    private async Task ReadParagraphAsync()
    {
        _isSpeaking = true;
        UpdateCommandStates();
        StatusMessage = "Retrieving paragraph...";

        try
        {
            var result = await _readingService.ReadParagraphAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            _isSpeaking = false;
            UpdateCommandStates();
        }
    }

    private async Task ReadDocumentAsync()
    {
        _isSpeaking = true;
        UpdateCommandStates();
        StatusMessage = "Retrieving document text...";

        try
        {
            var result = await _readingService.ReadDocumentAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            _isSpeaking = false;
            UpdateCommandStates();
        }
    }

    private async Task PreviewVoiceAsync()
    {
        _isSpeaking = true;
        UpdateCommandStates();
        StatusMessage = "Previewing voice...";

        try
        {
            var result = await _readingService.ReadTextAsync(VoicePreviewText).ConfigureAwait(true);
            StatusMessage = result.Message;
        }
        finally
        {
            _isSpeaking = false;
            UpdateCommandStates();
        }
    }

    private Task ApplyHotkeysAsync()
    {
        _hotkeySettingsService.ReadSelectedKey = _readSelectedHotkeyKey;
        _hotkeySettingsService.ReadTypedTextKey = _readTypedTextHotkeyKey;
        _hotkeySettingsService.StopKey = _stopHotkeyKey;

        if (!_hotkeySettingsService.Save())
        {
            SetStatusMessage("Hotkey update failed: each action must use a different key.");
            return Task.CompletedTask;
        }

        _readSelectedHotkeyKey = _hotkeySettingsService.ReadSelectedKey;
        _readTypedTextHotkeyKey = _hotkeySettingsService.ReadTypedTextKey;
        _stopHotkeyKey = _hotkeySettingsService.StopKey;
        OnPropertyChanged(nameof(ReadSelectedHotkeyKey));
        OnPropertyChanged(nameof(ReadTypedTextHotkeyKey));
        OnPropertyChanged(nameof(StopHotkeyKey));
        OnPropertyChanged(nameof(ReadSelectedHotkeyDisplay));
        OnPropertyChanged(nameof(ReadTypedTextHotkeyDisplay));
        OnPropertyChanged(nameof(StopHotkeyDisplay));

        HotkeysApplyRequested?.Invoke(this, EventArgs.Empty);
        SetStatusMessage("Hotkeys updated.");
        return Task.CompletedTask;
    }

    private void UpdateCommandStates()
    {
        if (ReadCommand is AsyncCommand readCommand)
        {
            readCommand.RaiseCanExecuteChanged();
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
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static IReadOnlyList<string> BuildVoiceOptions(IReadOnlyList<string> installedVoices)
    {
        return new[] { SystemDefaultVoiceOption }.Concat(installedVoices).ToArray();
    }
}
