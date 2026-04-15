using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ISpeechService _speechService;
    private string _inputText = string.Empty;
    private string _statusMessage = "Enter text, then click Read.";
    private bool _isSpeaking;

    public MainViewModel(ISpeechService speechService)
    {
        _speechService = speechService ?? throw new ArgumentNullException(nameof(speechService));
        ReadCommand = new AsyncCommand(ReadAsync, CanRead);
        StopCommand = new AsyncCommand(StopAsync, CanStop);
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

    public ICommand ReadCommand { get; }

    public ICommand StopCommand { get; }

    private bool CanRead()
    {
        return !_isSpeaking && !string.IsNullOrWhiteSpace(InputText);
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
            var request = new SpeechRequest(text, new SpeechOptions());
            var result = await _speechService.SpeakAsync(request).ConfigureAwait(true);
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
        var result = await _speechService.StopAsync().ConfigureAwait(true);
        StatusMessage = result.Message;
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
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
