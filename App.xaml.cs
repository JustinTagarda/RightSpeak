using System;
using System.Windows;
using RightSpeak.Services;
using RightSpeak.ViewModels;
using RightSpeak.Views;

namespace RightSpeak;

public partial class App : Application
{
    private WindowsSpeechService? _speechService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _speechService = new WindowsSpeechService();
        var mainViewModel = new MainViewModel(_speechService);
        var mainWindow = new MainWindow(mainViewModel);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _speechService?.Dispose();
        base.OnExit(e);
    }
}
