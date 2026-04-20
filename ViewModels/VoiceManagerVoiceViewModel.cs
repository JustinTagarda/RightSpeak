using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Models;

namespace RightSpeak.ViewModels;

public sealed class VoiceManagerVoiceViewModel : INotifyPropertyChanged
{
    private readonly Func<VoiceManagerVoiceViewModel, Task> _installOrUpdateAsync;
    private readonly Func<VoiceManagerVoiceViewModel, Task> _removeAsync;
    private VoiceInstallState _status;
    private string _progressText = string.Empty;
    private double _progressPercent;
    private bool _hasProgress;

    public VoiceManagerVoiceViewModel(
        DownloadableVoice voice,
        Func<VoiceManagerVoiceViewModel, Task> installOrUpdateAsync,
        Func<VoiceManagerVoiceViewModel, Task> removeAsync)
    {
        Voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _status = voice.Status;
        _installOrUpdateAsync = installOrUpdateAsync ?? throw new ArgumentNullException(nameof(installOrUpdateAsync));
        _removeAsync = removeAsync ?? throw new ArgumentNullException(nameof(removeAsync));
        InstallOrUpdateCommand = new AsyncCommand(() => _installOrUpdateAsync(this), CanInstallOrUpdate);
        RemoveCommand = new AsyncCommand(() => _removeAsync(this), CanRemove);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadableVoice Voice { get; }
    public string Id => Voice.Id;
    public string DisplayName => Voice.DisplayName;
    public string Metadata => $"{Voice.Locale.Replace('_', '-')} - {FormatQuality(Voice.Quality)} - {FormatSize(Voice.TotalSizeBytes)}";
    public string StatusText => Status switch
    {
        VoiceInstallState.Installed => "Installed",
        VoiceInstallState.UpdateAvailable => "Update available",
        VoiceInstallState.Downloading => "Downloading",
        VoiceInstallState.Failed => "Failed",
        _ => "Not installed"
    };

    public VoiceInstallState Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PrimaryActionText));
            RaiseCommandStates();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (string.Equals(_progressText, value, StringComparison.Ordinal))
            {
                return;
            }

            _progressText = value;
            OnPropertyChanged();
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.01)
            {
                return;
            }

            _progressPercent = value;
            OnPropertyChanged();
        }
    }

    public bool HasProgress
    {
        get => _hasProgress;
        set
        {
            if (_hasProgress == value)
            {
                return;
            }

            _hasProgress = value;
            OnPropertyChanged();
        }
    }

    public string PrimaryActionText => Status == VoiceInstallState.UpdateAvailable
        ? "Update"
        : Status == VoiceInstallState.Installed
            ? "Installed"
            : "Install";

    public ICommand InstallOrUpdateCommand { get; }
    public ICommand RemoveCommand { get; }

    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        RaiseCommandStates();
    }

    private bool CanInstallOrUpdate()
    {
        return Status is VoiceInstallState.NotInstalled or VoiceInstallState.UpdateAvailable or VoiceInstallState.Failed;
    }

    private bool CanRemove()
    {
        return Status is VoiceInstallState.Installed or VoiceInstallState.UpdateAvailable;
    }

    private void RaiseCommandStates()
    {
        if (InstallOrUpdateCommand is AsyncCommand installCommand)
        {
            installCommand.RaiseCanExecuteChanged();
        }

        if (RemoveCommand is AsyncCommand removeCommand)
        {
            removeCommand.RaiseCanExecuteChanged();
        }
    }

    private static string FormatQuality(string quality)
    {
        return quality.Replace('_', ' ') switch
        {
            "x low" => "Extra Low",
            "low" => "Low",
            "medium" => "Medium",
            "high" => "High",
            var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other)
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "Unknown size";
        }

        var megabytes = bytes / 1024d / 1024d;
        return $"{megabytes:0.#} MB";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
