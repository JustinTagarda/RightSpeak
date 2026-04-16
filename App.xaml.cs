using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RightSpeak.Interop;
using RightSpeak.Services;
using RightSpeak.ViewModels;
using RightSpeak.Views;
using RightSpeak.WindowsIntegration;
using WpfApplication = System.Windows.Application;

namespace RightSpeak;

public partial class App : WpfApplication
{
    private const string SingleInstanceMutexName = @"Global\RightSpeak.SingleInstance";
    private const string ActivateWindowMessageName = "RightSpeak.Activate.MainWindow";

    private WindowsSpeechService? _speechService;
    private WindowsGlobalHotkeyService? _hotkeyService;
    private WindowsNamedPipeContextReadIngressService? _contextReadIngressService;
    private WindowsTrayService? _trayService;
    private IHotkeySettingsService? _hotkeySettingsService;
    private MainViewModel? _mainViewModel;
    private IReadingService? _readingService;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private uint _activateWindowMessageId;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (TryRunCommandMode(e.Args, out var exitCode))
        {
            Environment.Exit(exitCode);
            return;
        }

        _activateWindowMessageId = WindowMessageInterop.RegisterWindowMessage(ActivateWindowMessageName);
        if (!TryAcquireSingleInstanceLock())
        {
            SignalExistingInstance();
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        _speechService = new WindowsSpeechService();
        var appSettingsService = new JsonAppSettingsService();
        _hotkeySettingsService = new HotkeySettingsService(appSettingsService);
        _hotkeyService = new WindowsGlobalHotkeyService(_hotkeySettingsService);
        var selectedTextRetrievalService = new SelectedTextRetrievalService(
            new List<ISelectedTextProvider>
            {
                new UiAutomationSelectedTextProvider(),
                new FocusedControlSelectedTextProvider(),
                new ClipboardSelectedTextProvider()
            });
        var paragraphTextRetrievalService = new ParagraphTextRetrievalService(
            new List<IParagraphTextProvider>
            {
                new UiAutomationParagraphTextProvider(),
                new FocusedControlParagraphTextProvider(),
                new ClipboardParagraphTextProvider()
            });
        var documentTextRetrievalService = new DocumentTextRetrievalService(
            new List<IDocumentTextProvider>
            {
                new FocusedControlDocumentTextProvider()
            });
        _readingService = new ReadingService(
            _speechService,
            selectedTextRetrievalService,
            paragraphTextRetrievalService,
            documentTextRetrievalService,
            appSettingsService);
        _mainViewModel = new MainViewModel(_readingService, _hotkeySettingsService);
        _mainViewModel.HotkeysApplyRequested += OnHotkeysApplyRequested;

        _contextReadIngressService = new WindowsNamedPipeContextReadIngressService();
        _contextReadIngressService.ReadRequested += OnContextReadRequested;
        _contextReadIngressService.Start();

        _trayService = new WindowsTrayService();
        _trayService.ReadTypedTextRequested += OnTrayReadTypedTextRequested;
        _trayService.ReadSelectedRequested += OnTrayReadSelectedRequested;
        _trayService.ReadParagraphRequested += OnTrayReadParagraphRequested;
        _trayService.ReadDocumentRequested += OnTrayReadDocumentRequested;
        _trayService.StopRequested += OnTrayStopRequested;
        _trayService.ShowRequested += OnTrayShowRequested;
        _trayService.ExitRequested += OnTrayExitRequested;
        _trayService.Initialize();
        ApplyTrayHotkeyHints();

        _mainWindow = new MainWindow(_mainViewModel, _hotkeyService, _activateWindowMessageId, ExecuteTrayFocusSensitiveReadAsync);
        _mainWindow.Closing += OnMainWindowClosing;
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_contextReadIngressService is not null)
        {
            _contextReadIngressService.ReadRequested -= OnContextReadRequested;
            _contextReadIngressService.Dispose();
        }

        _hotkeyService?.Dispose();
        if (_mainViewModel is not null)
        {
            _mainViewModel.HotkeysApplyRequested -= OnHotkeysApplyRequested;
        }
        if (_trayService is not null)
        {
            _trayService.ReadTypedTextRequested -= OnTrayReadTypedTextRequested;
            _trayService.ReadSelectedRequested -= OnTrayReadSelectedRequested;
            _trayService.ReadParagraphRequested -= OnTrayReadParagraphRequested;
            _trayService.ReadDocumentRequested -= OnTrayReadDocumentRequested;
            _trayService.StopRequested -= OnTrayStopRequested;
            _trayService.ShowRequested -= OnTrayShowRequested;
            _trayService.ExitRequested -= OnTrayExitRequested;
            _trayService.Dispose();
        }

        _speechService?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private async void OnContextReadRequested(object? sender, string text)
    {
        if (_readingService is null || _mainViewModel is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() => _mainViewModel.SetStatusMessage("Context read request received."));
            var result = await _readingService.ReadTextAsync(text).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() => _mainViewModel.SetStatusMessage(result.Message));
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => _mainViewModel.SetStatusMessage($"Context read request failed: {ex.Message}"));
        }
    }

    private static bool TryRunCommandMode(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length == 0)
        {
            if (ShouldRunNativeHostMode())
            {
                exitCode = WindowsNativeMessagingHost.RunAsync().GetAwaiter().GetResult();
                return true;
            }

            return false;
        }

        var command = args[0];
        if (LooksLikeBrowserNativeHostInvocation(args))
        {
            exitCode = WindowsNativeMessagingHost.RunAsync().GetAwaiter().GetResult();
            return true;
        }

        if (string.Equals(command, "--send-text", StringComparison.OrdinalIgnoreCase))
        {
            var text = string.Join(" ", args.Skip(1)).Trim();
            var result = WindowsNamedPipeContextReadClient.SendReadRequestAsync(text).GetAwaiter().GetResult();
            exitCode = result.Success ? 0 : 1;
            return true;
        }

        if (string.Equals(command, "--native-host", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = WindowsNativeMessagingHost.RunAsync().GetAwaiter().GetResult();
            return true;
        }

        return false;
    }

    private static bool LooksLikeBrowserNativeHostInvocation(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        var firstArgument = args[0];
        if (!firstArgument.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return args.Skip(1).Any(argument => argument.StartsWith("--parent-window=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldRunNativeHostMode()
    {
        if (!Console.IsInputRedirected || !Console.IsOutputRedirected)
        {
            return false;
        }

        var parentProcessName = ProcessInterop.GetParentProcessName();
        if (string.IsNullOrWhiteSpace(parentProcessName))
        {
            return false;
        }

        return string.Equals(parentProcessName, "chrome", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(parentProcessName, "msedge", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryAcquireSingleInstanceLock()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var createdNew);
        return createdNew;
    }

    private void SignalExistingInstance()
    {
        if (_activateWindowMessageId == 0)
        {
            return;
        }

        WindowMessageInterop.PostMessage(WindowMessageInterop.HwndBroadcast, _activateWindowMessageId, nint.Zero, nint.Zero);
    }

    private void OnTrayReadTypedTextRequested(object? sender, EventArgs e)
    {
        if (_mainViewModel?.ReadCommand.CanExecute(null) == true)
        {
            _mainViewModel.ReadCommand.Execute(null);
        }
    }

    private void OnTrayReadSelectedRequested(object? sender, EventArgs e)
    {
        _ = ExecuteTrayFocusSensitiveReadAsync(() =>
        {
            if (_mainViewModel?.ReadSelectedTextCommand.CanExecute(null) == true)
            {
                _mainViewModel.ReadSelectedTextCommand.Execute(null);
            }
        });
    }

    private void OnTrayReadParagraphRequested(object? sender, EventArgs e)
    {
        _ = ExecuteTrayFocusSensitiveReadAsync(() =>
        {
            if (_mainViewModel?.ReadParagraphCommand.CanExecute(null) == true)
            {
                _mainViewModel.ReadParagraphCommand.Execute(null);
            }
        });
    }

    private void OnTrayReadDocumentRequested(object? sender, EventArgs e)
    {
        _ = ExecuteTrayFocusSensitiveReadAsync(() =>
        {
            if (_mainViewModel?.ReadDocumentCommand.CanExecute(null) == true)
            {
                _mainViewModel.ReadDocumentCommand.Execute(null);
            }
        });
    }

    private void OnTrayStopRequested(object? sender, EventArgs e)
    {
        if (_mainViewModel?.StopCommand.CanExecute(null) == true)
        {
            _mainViewModel.StopCommand.Execute(null);
        }
    }

    private void OnTrayShowRequested(object? sender, EventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        _isShuttingDown = true;
        Shutdown();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
        _mainViewModel?.SetStatusMessage("RightSpeak is running in the system tray.");
    }

    private void OnHotkeysApplyRequested(object? sender, EventArgs e)
    {
        if (_hotkeyService is not null)
        {
            var refreshed = _hotkeyService.RefreshHotkeys();
            _mainViewModel?.SetStatusMessage(_hotkeyService.LastRegistrationStatus);
            if (!refreshed)
            {
                ApplyTrayHotkeyHints();
                return;
            }
        }

        ApplyTrayHotkeyHints();
    }

    private void ApplyTrayHotkeyHints()
    {
        if (_trayService is null || _hotkeySettingsService is null)
        {
            return;
        }

        _trayService.UpdateHotkeyHints(
            _hotkeySettingsService.ReadSelectedKey,
            _hotkeySettingsService.ReadTypedTextKey,
            _hotkeySettingsService.StopKey);
    }

    private async Task ExecuteTrayFocusSensitiveReadAsync(Action executeCommand)
    {
        if (_trayService is not null)
        {
            var restored = _trayService.TryRestoreLastExternalForegroundWindow();
            if (!restored)
            {
                await Dispatcher.InvokeAsync(() =>
                    _mainViewModel?.SetStatusMessage("Tray read failed: unable to restore the last active app window."));
                AppDiagnostics.Warn("tray_focus_read_aborted_restore_failed");
                return;
            }

            await Task.Delay(220).ConfigureAwait(false);
        }

        await Dispatcher.InvokeAsync(executeCommand);
    }
}
