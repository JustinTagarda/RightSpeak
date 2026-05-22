using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.Services.Store;
using RightSpeak.ViewModels;
using RightSpeak.Views;
using RightSpeak.WindowsIntegration;
using Windows.Services.Store;
using WpfApplication = System.Windows.Application;

namespace RightSpeak;

public partial class App : WpfApplication
{
    private const string SingleInstanceMutexName = @"Global\RightSpeak.SingleInstance";
    private const string ActivateWindowMessageName = "RightSpeak.Activate.MainWindow";
    private const string PremiumAddOnStoreId = "9PG6LR8K5M0Z";
    private const string PremiumAddOnProductId = "rightspeak_premium_lifetime";

    private WindowsSpeechService? _speechService;
    private WindowsGlobalHotkeyService? _hotkeyService;
    private WindowsTrayService? _trayService;
    private JsonAppSettingsService? _appSettingsService;
    private IHotkeySettingsService? _hotkeySettingsService;
    private IVoiceCatalogService? _voiceCatalogService;
    private IVoiceDownloadService? _voiceDownloadService;
    private IAppVersionProvider? _appVersionProvider;
    private IAppUpdateService? _appUpdateService;
    private IPremiumEntitlementService? _premiumEntitlementService;
    private IStoreNavigationService? _storeNavigationService;
    private MainViewModel? _mainViewModel;
    private AppStatusViewModel? _appStatusViewModel;
    private IReadingService? _readingService;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private uint _activateWindowMessageId;
    private bool _isExiting;
    private bool _isDeferredUpdateExitFlowRunning;
    private bool _startupUpdateCheckScheduled;
    private readonly CancellationTokenSource _updateCancellationTokenSource = new();

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

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
        _appVersionProvider = new PackageVersionProvider();
        _appSettingsService = new JsonAppSettingsService();
        if (!ApplyTheme(_appSettingsService.Current.Theme))
        {
            _appSettingsService.Current.Theme = AppThemes.Light;
            ApplyTheme(AppThemes.Light);
        }

        _hotkeySettingsService = new HotkeySettingsService(_appSettingsService);
        var voiceInstallStore = new VoiceInstallStore();
        _voiceCatalogService = new PiperVoiceCatalogService(voiceInstallStore);
        var piperRuntimeInstaller = new PiperRuntimeInstaller(voiceInstallStore);
        _voiceDownloadService = new VoiceDownloadService(voiceInstallStore, piperRuntimeInstaller);
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
        _appUpdateService = new StoreAppUpdateService(
            CreateStoreUpdateClient,
            _appVersionProvider,
            new DeferredUpdateStateStore(),
            new DeferredUpdateHistoryStore());
        var premiumEntitlementCache = new PremiumEntitlementCache();
        _premiumEntitlementService = new StorePremiumEntitlementService(
            _appVersionProvider,
            premiumEntitlementCache,
            () => _mainWindow is null ? nint.Zero : new WindowInteropHelper(_mainWindow).Handle,
            new StorePremiumEntitlementOptions
            {
                TreatUnpackagedBuildsAsPremium = false,
                PremiumProductDisplayName = "RightSpeak Premium",
                PremiumStoreIds = [PremiumAddOnStoreId],
                PremiumProductIds = [PremiumAddOnProductId]
            });

        var storeContextProvider = new StoreContextProvider(
            _appVersionProvider,
            () => _mainWindow is null ? nint.Zero : new WindowInteropHelper(_mainWindow).Handle);
        var appStatusVersionService = new AppVersionService(_appVersionProvider);
        var storePurchaseService = new StorePurchaseService(storeContextProvider, PremiumAddOnStoreId);
        _storeNavigationService = new StoreNavigationService(PremiumAddOnStoreId);
        _appStatusViewModel = new AppStatusViewModel(
            storePurchaseService,
            _premiumEntitlementService,
            _appUpdateService,
            _storeNavigationService,
            appStatusVersionService);
        _mainViewModel = new MainViewModel(
            _readingService,
            _hotkeySettingsService,
            ApplyHotkeysAndRefreshTray,
            BuildConfiguration.IsDebugDiagnosticsEnabled,
            _appSettingsService,
            ApplyTheme,
            premiumEntitlementSnapshot: _premiumEntitlementService.CurrentSnapshot);
        _appUpdateService.SnapshotChanged += OnAppUpdateSnapshotChanged;
        _premiumEntitlementService.SnapshotChanged += OnPremiumEntitlementSnapshotChanged;
        _mainViewModel.ApplyUpdateSnapshot(_appUpdateService.CurrentSnapshot);
        _mainViewModel.ApplyPremiumEntitlementSnapshot(_premiumEntitlementService.CurrentSnapshot);
        _appStatusViewModel.ApplyPremiumSnapshot(_premiumEntitlementService.CurrentSnapshot);
        _appStatusViewModel.StatusMessageChanged += OnAppStatusMessageChanged;

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
                _appStatusViewModel,
                _storeNavigationService,
                _appSettingsService,
                ExecuteTrayFocusSensitiveReadAsync,
                CreateVoiceManagerViewModel,
                placeOnStartup: true);
        _mainWindow.ContentRendered += OnMainWindowContentRendered;
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

        var revealOperation = Dispatcher.BeginInvoke(async () =>
        {
            try
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
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error(
                    "main_window_reveal_post_startup_failed",
                    new Dictionary<string, string?>
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message
                    });
            }
        }, DispatcherPriority.ApplicationIdle);
        ObserveBackgroundTask(revealOperation.Task, "main_window_reveal_post_startup");

        var entitlementOperation = Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (_premiumEntitlementService is not null)
                {
                    await _premiumEntitlementService.RefreshAsync(_updateCancellationTokenSource.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, DispatcherPriority.Background);
        ObserveBackgroundTask(entitlementOperation.Task, "premium_entitlement_startup");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppDiagnostics.Info(
            "app_exit_started",
            new Dictionary<string, string?>
            {
                ["processId"] = Environment.ProcessId.ToString(),
                ["exitCode"] = e.ApplicationExitCode.ToString()
            });
        _isExiting = true;
        TryRunCleanup("app_exit_cancel_updates", () => _updateCancellationTokenSource.Cancel());
        TryRunCleanup("app_exit_unsubscribe_dispatcher_unhandled", () => DispatcherUnhandledException -= OnDispatcherUnhandledException);
        TryRunCleanup("app_exit_unsubscribe_domain_unhandled", () => AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException);
        TryRunCleanup("app_exit_unsubscribe_process_exit", () => AppDomain.CurrentDomain.ProcessExit -= OnProcessExit);
        TryRunCleanup("app_exit_unsubscribe_task_unobserved", () => TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException);
        TryRunCleanup("app_exit_dispose_hotkeys", () => _hotkeyService?.Dispose());
        TryRunCleanup("app_exit_dispose_tray", () =>
        {
            if (_trayService is null)
            {
                return;
            }

            _trayService.ReadSelectedRequested -= OnTrayReadSelectedRequested;
            _trayService.ReadDocumentRequested -= OnTrayReadDocumentRequested;
            _trayService.StopRequested -= OnTrayStopRequested;
            _trayService.ShowRequested -= OnTrayShowRequested;
            _trayService.ExitRequested -= OnTrayExitRequested;
            _trayService.ForegroundWindowChanged -= OnTrayForegroundWindowChanged;
            _trayService.Dispose();
        });
        TryRunCleanup("app_exit_unsubscribe_update_snapshot", () =>
        {
            if (_appUpdateService is not null)
            {
                _appUpdateService.SnapshotChanged -= OnAppUpdateSnapshotChanged;
            }
            if (_premiumEntitlementService is not null)
            {
                _premiumEntitlementService.SnapshotChanged -= OnPremiumEntitlementSnapshotChanged;
            }
            if (_appStatusViewModel is not null)
            {
                _appStatusViewModel.StatusMessageChanged -= OnAppStatusMessageChanged;
            }
            if (_mainWindow is not null)
            {
                _mainWindow.ContentRendered -= OnMainWindowContentRendered;
                _mainWindow.Closing -= OnMainWindowClosing;
            }
        });
        TryRunCleanup("app_exit_dispose_speech", () => _speechService?.Dispose());
        TryRunCleanup("app_exit_save_settings", () => _appSettingsService?.Save());
        TryRunCleanup("app_exit_dispose_single_instance_mutex", () => _singleInstanceMutex?.Dispose());
        AppDiagnostics.Info(
            "app_exit_completed",
            new Dictionary<string, string?>
            {
                ["processId"] = Environment.ProcessId.ToString(),
                ["exitCode"] = e.ApplicationExitCode.ToString()
            });
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

        ObserveBackgroundTask(
            ExecuteTrayFocusSensitiveReadAsync("read_selected_text_external", "tray_menu", () =>
            {
                if (_mainViewModel?.ReadSelectedTextCommand.CanExecute(null) == true)
                {
                    return ExecuteCommandAsync(_mainViewModel.ReadSelectedTextCommand);
                }

                return Task.CompletedTask;
            }),
            "tray_read_selected_text");
    }

    private void OnTrayReadDocumentRequested(object? sender, EventArgs e)
    {
        if (_mainViewModel?.ReadDocumentCommand.CanExecute(null) != true)
        {
            return;
        }

        ObserveBackgroundTask(
            ExecuteTrayFocusSensitiveReadAsync("read_document_external", "tray_menu", () =>
            {
                if (_mainViewModel?.ReadDocumentCommand.CanExecute(null) == true)
                {
                    return ExecuteCommandAsync(_mainViewModel.ReadDocumentCommand);
                }

                return Task.CompletedTask;
            }),
            "tray_read_document");
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
        if (_isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Shutdown();
    }

    private void OnTrayForegroundWindowChanged(object? sender, EventArgs e)
    {
        UpdateFocusedWindowText();
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

    private IStoreUpdateClient CreateStoreUpdateClient()
    {
        if (_appVersionProvider?.IsPackaged != true)
        {
            return new UnsupportedStoreUpdateClient();
        }

        return new StoreContextUpdateClient(
            StoreContext.GetDefault(),
            isSupported: true,
            ownerWindowHandleProvider: () => _mainWindow is null ? nint.Zero : new WindowInteropHelper(_mainWindow).Handle);
    }

    private void OnAppUpdateSnapshotChanged(object? sender, AppUpdateSnapshot snapshot)
    {
        ObserveBackgroundTask(
            Dispatcher.InvokeAsync(() => _mainViewModel?.ApplyUpdateSnapshot(snapshot)).Task,
            "app_update_snapshot_changed");
    }

    private void OnMainWindowContentRendered(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        if (_startupUpdateCheckScheduled || _isExiting || _appUpdateService is null)
        {
            return;
        }

        _startupUpdateCheckScheduled = true;
        var startupUpdateOperation = Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await _appUpdateService.StartAsync(_updateCancellationTokenSource.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error(
                    "app_update_startup_failed",
                    new Dictionary<string, string?>
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message
                    });
            }
        }, DispatcherPriority.Background);

        ObserveBackgroundTask(startupUpdateOperation.Task, "app_update_startup");
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;

        if (_isExiting || _isDeferredUpdateExitFlowRunning)
        {
            return;
        }

        if (_appUpdateService?.HasDeferredInstallPending != true)
        {
            return;
        }

        e.Cancel = true;
        TryRunCleanup("app_deferred_update_exit_cancel_background", () => _updateCancellationTokenSource.Cancel());
        _isDeferredUpdateExitFlowRunning = true;
        ObserveBackgroundTask(RunDeferredUpdateExitFlowAsync(), "app_deferred_update_exit_flow");
    }

    private async Task RunDeferredUpdateExitFlowAsync()
    {
        DeferredUpdateProgressWindow? progressWindow = null;
        try
        {
            if (_appUpdateService is null || _mainWindow is null)
            {
                return;
            }

            progressWindow = new DeferredUpdateProgressWindow
            {
                Owner = _mainWindow
            };
            progressWindow.SetStatus("Applying update...");
            progressWindow.SetProgress(null);

            var progressReporter = new Progress<AppUpdateSnapshot>(snapshot => progressWindow.SetSnapshot(snapshot));
            var installTask = _appUpdateService.TryApplyDeferredInstallOnExitAsync(
                progressReporter,
                CancellationToken.None);

            _ = installTask.ContinueWith(
                _ =>
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (progressWindow is not null && progressWindow.IsVisible)
                            {
                                progressWindow.Close();
                            }
                        });
                    }
                    catch
                    {
                        // Closing the progress window is best-effort during shutdown.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            progressWindow.ShowDialog();
            var result = await installTask.ConfigureAwait(true);

            if (result.Attempted && !result.Succeeded)
            {
                ShowDeferredUpdateFailureMessageSafe(result.Message, result.TimedOut);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "app_deferred_update_exit_flow_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
        finally
        {
            TryRunCleanup("app_deferred_update_exit_flow_close_window", () =>
            {
                if (progressWindow is not null && progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            });

            _isDeferredUpdateExitFlowRunning = false;
            _isExiting = true;
            Shutdown();
        }
    }

    private void ShowDeferredUpdateFailureMessageSafe(string message, bool timedOut)
    {
        try
        {
            System.Windows.MessageBox.Show(
                string.IsNullOrWhiteSpace(message)
                    ? (timedOut
                        ? "The update installation timed out. The app will close normally."
                        : "The update could not be installed. The app will close normally.")
                    : message,
                "Update installation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "app_deferred_update_failure_message_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private void OnPremiumEntitlementSnapshotChanged(object? sender, PremiumEntitlementSnapshot snapshot)
    {
        ObserveBackgroundTask(
            Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel?.ApplyPremiumEntitlementSnapshot(snapshot);
                _appStatusViewModel?.ApplyPremiumSnapshot(snapshot);
            }).Task,
            "premium_entitlement_snapshot_changed");
    }

    private void OnAppStatusMessageChanged(object? sender, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _mainViewModel?.SetStatusMessage(message);
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

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await Dispatcher.InvokeAsync(() => _mainViewModel?.SetExternalReadFocusStatus());
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
                    {
                        _mainViewModel?.SetStatusMessage("Couldn't switch back to the other app. Click that app first, then try again.");
                        _mainViewModel?.ClearExternalReadFocusStatus();
                    });
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
        catch (OperationCanceledException)
        {
            AppDiagnostics.Info(
                "focused_read_command_canceled",
                new Dictionary<string, string?>
                {
                    ["trigger"] = trigger,
                    ["workflow"] = workflowName
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "focused_read_command_failed",
                new Dictionary<string, string?>
                {
                    ["trigger"] = trigger,
                    ["workflow"] = workflowName,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            await Dispatcher.InvokeAsync(() =>
                _mainViewModel?.SetStatusMessage("Couldn't read from the other app due to an internal error. Please try again."));
        }
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

    private VoiceManagerViewModel CreateVoiceManagerViewModel()
    {
        if (_voiceCatalogService is null ||
            _voiceDownloadService is null ||
            _readingService is null ||
            _mainViewModel is null ||
            _premiumEntitlementService is null)
        {
            throw new InvalidOperationException("Voice manager services are not available.");
        }

        return new VoiceManagerViewModel(
            _voiceCatalogService,
            _voiceDownloadService,
            _readingService,
            _mainViewModel.RefreshVoiceOptions,
            _premiumEntitlementService,
            () => _storeNavigationService?.OpenPremiumPage());
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

    private void ObserveBackgroundTask(Task task, string operationName)
    {
        if (task.IsCompletedSuccessfully || task.IsCanceled)
        {
            return;
        }

        _ = task.ContinueWith(
            continuation =>
            {
                if (continuation.IsCanceled)
                {
                    return;
                }

                var exception = continuation.Exception?.GetBaseException();
                if (exception is null)
                {
                    return;
                }

                AppDiagnostics.Error(
                    "background_operation_failed",
                    new Dictionary<string, string?>
                    {
                        ["operationName"] = operationName,
                        ["exceptionType"] = exception.GetType().FullName,
                        ["message"] = exception.Message
                    });

                if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                try
                {
                    _ = Dispatcher.InvokeAsync(() =>
                        _mainViewModel?.SetStatusMessage("An internal operation failed. Please try again."));
                }
                catch
                {
                    // Ignore UI update failures while reporting background exceptions.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static void TryRunCleanup(string operationName, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "app_exit_cleanup_failed",
                new Dictionary<string, string?>
                {
                    ["operationName"] = operationName,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppDiagnostics.Error(
            "dispatcher_unhandled_exception",
            new Dictionary<string, string?>
            {
                ["exceptionType"] = e.Exception.GetType().FullName,
                ["message"] = e.Exception.Message,
                ["isCritical"] = IsCriticalException(e.Exception).ToString()
            });

        if (IsCriticalException(e.Exception))
        {
            return;
        }

        e.Handled = true;
        if (_isExiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _mainViewModel?.SetStatusMessage("RightSpeak recovered from an internal error. Please try again.");
        }
        catch
        {
            // Ignore status update failures in unhandled exception path.
        }
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppDiagnostics.Error(
            "appdomain_unhandled_exception",
            new Dictionary<string, string?>
            {
                ["exceptionType"] = exception?.GetType().FullName ?? e.ExceptionObject?.GetType().FullName,
                ["message"] = exception?.Message ?? "Unknown unhandled exception",
                ["stackTrace"] = exception?.StackTrace,
                ["isTerminating"] = e.IsTerminating.ToString()
            });
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        AppDiagnostics.Info(
            "process_exit_event",
            new Dictionary<string, string?>
            {
                ["processId"] = Environment.ProcessId.ToString()
            });
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var exception = e.Exception.GetBaseException();
        AppDiagnostics.Error(
            "task_unobserved_exception",
            new Dictionary<string, string?>
            {
                ["exceptionType"] = exception.GetType().FullName,
                ["message"] = exception.Message
            });
        e.SetObserved();
    }

    private static bool IsCriticalException(Exception exception)
    {
        return exception is OutOfMemoryException or AccessViolationException or AppDomainUnloadedException or BadImageFormatException or CannotUnloadAppDomainException;
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
            string themeModeText;
#pragma warning disable WPF0001
            ThemeMode = string.Equals(normalizedTheme, AppThemes.WindowsSettings, StringComparison.Ordinal)
                ? ThemeMode.System
                : string.Equals(normalizedTheme, AppThemes.Dark, StringComparison.Ordinal)
                    ? ThemeMode.Dark
                    : ThemeMode.Light;
            themeModeText = ThemeMode.ToString();
#pragma warning restore WPF0001
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
                    ["resolvedTheme"] = resolvedTheme,
                    ["themeMode"] = themeModeText
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
