using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using RightSpeak.Interop;
using RightSpeak.Models;
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
    private WindowsTrayService? _trayService;
    private JsonAppSettingsService? _appSettingsService;
    private IHotkeySettingsService? _hotkeySettingsService;
    private MainViewModel? _mainViewModel;
    private IReadingService? _readingService;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private uint _activateWindowMessageId;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDiagnostics.Info(
            "app_startup_entered",
            new Dictionary<string, string?>
            {
                ["args"] = string.Join(" ", e.Args ?? Array.Empty<string>()),
                ["processId"] = Environment.ProcessId.ToString()
            });

        _activateWindowMessageId = WindowMessageInterop.RegisterWindowMessage(ActivateWindowMessageName);
        if (!TryAcquireSingleInstanceLock())
        {
            AppDiagnostics.Info("app_startup_single_instance_forwarded");
            SignalExistingInstance();
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        _speechService = new WindowsSpeechService();
        _appSettingsService = new JsonAppSettingsService();
        if (!ApplyTheme(_appSettingsService.Current.Theme))
        {
            _appSettingsService.Current.Theme = AppThemes.Light;
            ApplyTheme(AppThemes.Light);
        }

        _hotkeySettingsService = new HotkeySettingsService(_appSettingsService);
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
        var webpageMainContextAnalyzer = new WebpageMainContextAnalyzer();
        var documentTextRetrievalService = new DocumentTextRetrievalService(
            new List<IDocumentTextProvider>
            {
                new WebpageMainContextDocumentTextProvider(webpageMainContextAnalyzer),
                new FocusedControlDocumentTextProvider(),
                new ClipboardDocumentTextProvider()
            });
        _readingService = new ReadingService(
            _speechService,
            selectedTextRetrievalService,
            paragraphTextRetrievalService,
            documentTextRetrievalService,
            _appSettingsService);
        _mainViewModel = new MainViewModel(
            _readingService,
            _hotkeySettingsService,
            ApplyHotkeysAndRefreshTray,
            BuildConfiguration.IsDebugDiagnosticsEnabled,
            _appSettingsService,
            ApplyTheme);

        _trayService = new WindowsTrayService();
        _trayService.ReadSelectedRequested += OnTrayReadSelectedRequested;
        _trayService.ReadDocumentRequested += OnTrayReadDocumentRequested;
        _trayService.StopRequested += OnTrayStopRequested;
        _trayService.ShowRequested += OnTrayShowRequested;
        _trayService.ExitRequested += OnTrayExitRequested;
        _trayService.ForegroundWindowChanged += OnTrayForegroundWindowChanged;
        _trayService.Initialize();
        ApplyTrayHotkeyHints();
        UpdateFocusedWindowText();

        _mainWindow = new MainWindow(
            _mainViewModel,
            _hotkeyService,
            webpageMainContextAnalyzer,
            () => _trayService?.LastExternalForegroundWindow ?? nint.Zero,
            _activateWindowMessageId,
            ExecuteTrayFocusSensitiveReadAsync,
            placeOnStartup: true);
        _mainWindow.Closing += OnMainWindowClosing;
        AppDiagnostics.Info("main_window_created");
        _mainWindow.RevealWindow();
        AppDiagnostics.Info(
            "main_window_revealed",
            new Dictionary<string, string?>
            {
                ["isVisible"] = _mainWindow.IsVisible.ToString(),
                ["windowState"] = _mainWindow.WindowState.ToString()
            });

        _ = Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(350).ConfigureAwait(true);
            if (_mainWindow is null)
            {
                return;
            }

            if (_mainWindow.IsVisible)
            {
                AppDiagnostics.Info("main_window_reveal_post_startup_visible");
                return;
            }

            AppDiagnostics.Warn("main_window_reveal_post_startup_retry");
            _mainWindow.RevealWindow();
        }, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyService?.Dispose();
        if (_trayService is not null)
        {
            _trayService.ReadSelectedRequested -= OnTrayReadSelectedRequested;
            _trayService.ReadDocumentRequested -= OnTrayReadDocumentRequested;
            _trayService.StopRequested -= OnTrayStopRequested;
            _trayService.ShowRequested -= OnTrayShowRequested;
            _trayService.ExitRequested -= OnTrayExitRequested;
            _trayService.ForegroundWindowChanged -= OnTrayForegroundWindowChanged;
            _trayService.Dispose();
        }

        _speechService?.Dispose();
        _appSettingsService?.Save();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
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

    private void OnTrayReadSelectedRequested(object? sender, EventArgs e)
    {
        if (_mainViewModel?.ReadSelectedTextCommand.CanExecute(null) != true)
        {
            return;
        }

        _ = ExecuteTrayFocusSensitiveReadAsync("read_selected_text_external", "tray_menu", () =>
        {
            if (_mainViewModel?.ReadSelectedTextCommand.CanExecute(null) == true)
            {
                return ExecuteCommandAsync(_mainViewModel.ReadSelectedTextCommand);
            }

            return Task.CompletedTask;
        });
    }

    private void OnTrayReadDocumentRequested(object? sender, EventArgs e)
    {
        if (_mainViewModel?.ReadDocumentCommand.CanExecute(null) != true)
        {
            return;
        }

        _ = ExecuteTrayFocusSensitiveReadAsync("read_document_external", "tray_menu", () =>
        {
            if (_mainViewModel?.ReadDocumentCommand.CanExecute(null) == true)
            {
                return ExecuteCommandAsync(_mainViewModel.ReadDocumentCommand);
            }

            return Task.CompletedTask;
        });
    }

    private void OnTrayStopRequested(object? sender, EventArgs e)
    {
        if (_mainViewModel?.StopCommand.CanExecute(null) == true)
        {
            AppDiagnostics.Info(
                "stop_command_dispatch_requested",
                new Dictionary<string, string?>
                {
                    ["trigger"] = "tray_menu",
                    ["source"] = nameof(App)
                });
            _mainViewModel.StopCommand.Execute(null);
        }
    }

    private void OnTrayShowRequested(object? sender, EventArgs e)
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.RevealWindow();
    }

    private void OnTrayExitRequested(object? sender, EventArgs e)
    {
        _isShuttingDown = true;
        Shutdown();
    }

    private void OnTrayForegroundWindowChanged(object? sender, EventArgs e)
    {
        UpdateFocusedWindowText();
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

    private void ApplyTrayHotkeyHints()
    {
        if (_trayService is null || _hotkeySettingsService is null)
        {
            return;
        }

        _trayService.UpdateHotkeyHints(
            GetModifierLabel(_hotkeySettingsService.ModifierPreset),
            _hotkeySettingsService.ReadSelectedKey,
            _hotkeySettingsService.ReadParagraphKey,
            _hotkeySettingsService.ReadDocumentKey,
            _hotkeySettingsService.StopKey);
    }

    private (bool Success, string StatusMessage) ApplyHotkeysAndRefreshTray()
    {
        if (_hotkeyService is null)
        {
            return (false, "Hotkeys aren't available right now.");
        }

        var refreshed = _hotkeyService.RefreshHotkeys();
        ApplyTrayHotkeyHints();
        return (refreshed, _hotkeyService.LastRegistrationStatus);
    }

    private static string GetModifierLabel(RightSpeak.Models.HotkeyModifierPreset preset)
    {
        return preset switch
        {
            Models.HotkeyModifierPreset.CtrlShift => "Ctrl+Shift",
            Models.HotkeyModifierPreset.CtrlAlt => "Ctrl+Alt",
            _ => "Alt+Shift"
        };
    }

    private void UpdateFocusedWindowText()
    {
        if (_mainViewModel is null || _trayService is null)
        {
            return;
        }

        _mainViewModel.SetFocusedWindowContext(
            _trayService.CurrentForegroundWindowTitle,
            _trayService.HasExternalForegroundWindow);
    }

    private async Task ExecuteTrayFocusSensitiveReadAsync(string workflowName, string trigger, Func<Task> executeCommandAsync)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var scope = AppDiagnostics.BeginScope(new Dictionary<string, string?>
        {
            ["operationId"] = operationId,
            ["workflow"] = workflowName,
            ["trigger"] = trigger
        });

        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "focused_read_focus_restore_started",
            BuildFocusRestoreDiagnostics(trigger));

        if (_trayService is not null)
        {
            var restored = _trayService.TryRestoreLastExternalForegroundWindow();
            if (!restored)
            {
                stopwatch.Stop();
                AppDiagnostics.Warn(
                    "focused_read_focus_restore_failed",
                    new Dictionary<string, string?>
                    {
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                        ["trigger"] = trigger,
                        ["workflow"] = workflowName
                    });
                await Dispatcher.InvokeAsync(() =>
                    _mainViewModel?.SetStatusMessage("Couldn't switch back to the other app. Click that app first, then try again."));
                AppDiagnostics.Warn("tray_focus_read_aborted_restore_failed");
                return;
            }
        }

        stopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_focus_restore_succeeded",
            new Dictionary<string, string?>
            {
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["trigger"] = trigger,
                ["workflow"] = workflowName
            });

        var commandStopwatch = Stopwatch.StartNew();
        var commandOperation = Dispatcher.InvokeAsync(executeCommandAsync);
        var commandTask = await commandOperation.Task.ConfigureAwait(false);
        await commandTask.ConfigureAwait(false);
        commandStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_command_dispatched",
            new Dictionary<string, string?>
            {
                ["elapsedMs"] = commandStopwatch.ElapsedMilliseconds.ToString(),
                ["trigger"] = trigger,
                ["workflow"] = workflowName
            });
    }

    private static Task ExecuteCommandAsync(ICommand command)
    {
        if (command is AsyncCommand asyncCommand)
        {
            return asyncCommand.ExecuteAsync();
        }

        command.Execute(null);
        return Task.CompletedTask;
    }

    private Dictionary<string, string?> BuildFocusRestoreDiagnostics(string trigger)
    {
        return new Dictionary<string, string?>
        {
            ["trigger"] = trigger,
            ["hasTrayService"] = (_trayService is not null).ToString(),
            ["hasExternalForegroundWindow"] = _trayService?.HasExternalForegroundWindow.ToString(),
            ["currentForegroundWindowTitle"] = _trayService?.CurrentForegroundWindowTitle
        };
    }

    private bool ApplyTheme(string? requestedTheme)
    {
        var normalizedTheme = AppThemes.Normalize(requestedTheme);
        var resolvedTheme = ResolveTheme(normalizedTheme);
        var themePath = string.Equals(resolvedTheme, AppThemes.Dark, StringComparison.Ordinal)
            ? "Resources/Themes/DarkTheme.xaml"
            : "Resources/Themes/LightTheme.xaml";

        try
        {
            var dictionaries = Resources.MergedDictionaries;
            for (var index = dictionaries.Count - 1; index >= 0; index--)
            {
                var source = dictionaries[index].Source?.OriginalString;
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                if (source.Contains("Resources/Themes/", StringComparison.OrdinalIgnoreCase))
                {
                    dictionaries.RemoveAt(index);
                }
            }

            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(themePath, UriKind.Relative)
            });

            if (_appSettingsService is not null &&
                !string.Equals(_appSettingsService.Current.Theme, normalizedTheme, StringComparison.Ordinal))
            {
                _appSettingsService.Current.Theme = normalizedTheme;
                _appSettingsService.Save();
            }

            AppDiagnostics.Info(
                "theme_applied",
                new Dictionary<string, string?>
                {
                    ["theme"] = normalizedTheme,
                    ["resolvedTheme"] = resolvedTheme
                });
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "theme_apply_failed",
                new Dictionary<string, string?>
                {
                    ["theme"] = normalizedTheme,
                    ["message"] = ex.Message
                });
            return false;
        }
    }

    private static string ResolveTheme(string normalizedTheme)
    {
        if (!string.Equals(normalizedTheme, AppThemes.WindowsSettings, StringComparison.Ordinal))
        {
            return normalizedTheme;
        }

        return IsWindowsLightTheme() ? AppThemes.Light : AppThemes.Dark;
    }

    private static bool IsWindowsLightTheme()
    {
        const string personalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string appsUseLightThemeValueName = "AppsUseLightTheme";

        try
        {
            using var personalizeKey = Registry.CurrentUser.OpenSubKey(personalizeKeyPath, writable: false);
            if (personalizeKey?.GetValue(appsUseLightThemeValueName) is int appsUseLightTheme)
            {
                return appsUseLightTheme != 0;
            }
        }
        catch
        {
            // Fall through to light theme default when registry reads fail.
        }

        return true;
    }
}
