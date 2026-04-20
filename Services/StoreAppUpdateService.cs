using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

internal sealed class StoreAppUpdateService : IAppUpdateService
{
    private static readonly TimeSpan DefaultInstallIdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultInstallRetryDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultDownloadRetryDelay = TimeSpan.FromMinutes(5);

    private readonly IStoreUpdateClient _storeUpdateClient;
    private readonly IAppVersionProvider _versionProvider;
    private readonly Func<bool> _isAppBusy;
    private readonly object _sync = new();
    private readonly TimeSpan _installIdleDelay;
    private readonly TimeSpan _installRetryDelay;
    private readonly TimeSpan _downloadRetryDelay;

    private IReadOnlyList<StorePackageUpdateInfo> _availableUpdates = [];
    private bool _startRequested;
    private bool _downloadRetryScheduled;
    private bool _installRetryScheduled;
    private AppUpdateSnapshot _currentSnapshot;

    public StoreAppUpdateService(
        IStoreUpdateClient storeUpdateClient,
        IAppVersionProvider versionProvider,
        Func<bool> isAppBusy,
        TimeSpan? installIdleDelay = null,
        TimeSpan? installRetryDelay = null,
        TimeSpan? downloadRetryDelay = null)
    {
        _storeUpdateClient = storeUpdateClient ?? throw new ArgumentNullException(nameof(storeUpdateClient));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _isAppBusy = isAppBusy ?? throw new ArgumentNullException(nameof(isAppBusy));
        _installIdleDelay = installIdleDelay ?? DefaultInstallIdleDelay;
        _installRetryDelay = installRetryDelay ?? DefaultInstallRetryDelay;
        _downloadRetryDelay = downloadRetryDelay ?? DefaultDownloadRetryDelay;
        _currentSnapshot = AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion);
    }

    public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    public AppUpdateSnapshot CurrentSnapshot => _currentSnapshot;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_startRequested)
            {
                return;
            }

            _startRequested = true;
        }

        await CheckForUpdatesAsync(isRetry: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckForUpdatesAsync(bool isRetry, CancellationToken cancellationToken)
    {
        Publish(new AppUpdateSnapshot(
            AppUpdateState.Checking,
            "Checking for updates",
            isRetry ? "Retrying Store update check." : "Looking for newer package versions.",
            isMandatoryUpdateAvailable: false,
            isProgressVisible: false,
            progressValue: 0d,
            installedVersion: _versionProvider.InstalledVersion));

        if (!_storeUpdateClient.IsSupported)
        {
            AppDiagnostics.Info("store_update_skipped_unpacked");
            Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
            return;
        }

        IReadOnlyList<StorePackageUpdateInfo> updates;
        try
        {
            updates = await _storeUpdateClient.GetAvailableUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "store_update_check_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            Publish(new AppUpdateSnapshot(
                AppUpdateState.Failed,
                "Update check failed",
                "Could not reach Microsoft Store for update information.",
                isMandatoryUpdateAvailable: false,
                isProgressVisible: false,
                progressValue: 0d,
                installedVersion: _versionProvider.InstalledVersion));
            QueueDownloadRetry(cancellationToken);
            return;
        }

        _availableUpdates = updates;
        if (updates.Count == 0)
        {
            AppDiagnostics.Info("store_update_none_available");
            Publish(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion));
            return;
        }

        var hasMandatoryUpdate = updates.Any(update => update.IsMandatory);
        var availableVersion = GetHighestVersion(updates);
        AppDiagnostics.Info(
            "store_update_available",
            new Dictionary<string, string?>
            {
                ["count"] = updates.Count.ToString(),
                ["mandatory"] = hasMandatoryUpdate.ToString(),
                ["availableVersion"] = availableVersion,
                ["canSilentDownload"] = _storeUpdateClient.CanSilentlyDownloadUpdates.ToString()
            });

        if (!_storeUpdateClient.CanSilentlyDownloadUpdates)
        {
            Publish(BuildDeferredSnapshot(
                hasMandatoryUpdate,
                availableVersion,
                "Store automatic updates are unavailable right now. RightSpeak will retry in the background."));
            QueueDownloadRetry(cancellationToken);
            return;
        }

        var downloadResult = await DownloadUpdatesAsync(hasMandatoryUpdate, availableVersion, cancellationToken).ConfigureAwait(false);
        if (downloadResult.OverallState != StoreUpdateOperationState.Completed)
        {
            Publish(BuildDeferredSnapshot(
                hasMandatoryUpdate,
                availableVersion,
                BuildRetryMessage("download", downloadResult.OverallState, hasMandatoryUpdate)));
            QueueDownloadRetry(cancellationToken);
            return;
        }

        Publish(new AppUpdateSnapshot(
            AppUpdateState.UpdateAvailable,
            "Update downloaded",
            "Downloaded package is ready and will install when RightSpeak is idle.",
            hasMandatoryUpdate,
            isProgressVisible: false,
            progressValue: 1d,
            installedVersion: _versionProvider.InstalledVersion,
            availableVersion: availableVersion));

        QueueInstallRetry(hasMandatoryUpdate, availableVersion, cancellationToken);
    }

    private async Task<StoreUpdateOperationResult> DownloadUpdatesAsync(
        bool hasMandatoryUpdate,
        string? availableVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _storeUpdateClient.TrySilentDownloadAsync(progress =>
            {
                Publish(new AppUpdateSnapshot(
                    AppUpdateState.Downloading,
                    "Downloading update",
                    $"{(int)Math.Round(progress.Progress * 100d)}% complete",
                    hasMandatoryUpdate,
                    isProgressVisible: true,
                    progressValue: progress.Progress,
                    installedVersion: _versionProvider.InstalledVersion,
                    availableVersion: availableVersion));
            }, cancellationToken).ConfigureAwait(false);

            AppDiagnostics.Info(
                "store_update_download_completed",
                new Dictionary<string, string?>
                {
                    ["overallState"] = result.OverallState.ToString(),
                    ["failedPackageCount"] = result.FailedPackageFamilyNames.Count.ToString()
                });
            return result;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "store_update_download_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return new StoreUpdateOperationResult
            {
                OverallState = StoreUpdateOperationState.OtherError
            };
        }
    }

    private void QueueDownloadRetry(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_downloadRetryScheduled)
            {
                return;
            }

            _downloadRetryScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_downloadRetryDelay, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _downloadRetryScheduled = false;
                }

                await CheckForUpdatesAsync(isRetry: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private void QueueInstallRetry(bool hasMandatoryUpdate, string? availableVersion, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_installRetryScheduled)
            {
                return;
            }

            _installRetryScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_isAppBusy())
                    {
                        Publish(BuildDeferredSnapshot(
                            hasMandatoryUpdate,
                            availableVersion,
                            "Update is downloaded and will install after reading finishes."));
                        await Task.Delay(_installRetryDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(_installIdleDelay, cancellationToken).ConfigureAwait(false);
                    if (_isAppBusy())
                    {
                        continue;
                    }

                    var installResult = await InstallUpdatesAsync(hasMandatoryUpdate, availableVersion, cancellationToken).ConfigureAwait(false);
                    if (installResult.OverallState == StoreUpdateOperationState.Completed)
                    {
                        Publish(new AppUpdateSnapshot(
                            AppUpdateState.Completed,
                            "Update installed",
                            "Latest package is installed. Restart RightSpeak to run the new version.",
                            hasMandatoryUpdate,
                            isProgressVisible: false,
                            progressValue: 1d,
                            installedVersion: _versionProvider.InstalledVersion,
                            availableVersion: availableVersion));
                        break;
                    }

                    Publish(BuildDeferredSnapshot(
                        hasMandatoryUpdate,
                        availableVersion,
                        BuildRetryMessage("install", installResult.OverallState, hasMandatoryUpdate)));
                    await Task.Delay(_installRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_sync)
                {
                    _installRetryScheduled = false;
                }
            }
        }, cancellationToken);
    }

    private async Task<StoreUpdateOperationResult> InstallUpdatesAsync(
        bool hasMandatoryUpdate,
        string? availableVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _storeUpdateClient.TrySilentDownloadAndInstallAsync(progress =>
            {
                Publish(new AppUpdateSnapshot(
                    AppUpdateState.Installing,
                    "Installing update",
                    $"{(int)Math.Round(progress.Progress * 100d)}% complete",
                    hasMandatoryUpdate,
                    isProgressVisible: true,
                    progressValue: progress.Progress,
                    installedVersion: _versionProvider.InstalledVersion,
                    availableVersion: availableVersion));
            }, cancellationToken).ConfigureAwait(false);

            AppDiagnostics.Info(
                "store_update_install_completed",
                new Dictionary<string, string?>
                {
                    ["overallState"] = result.OverallState.ToString(),
                    ["failedPackageCount"] = result.FailedPackageFamilyNames.Count.ToString()
                });
            return result;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "store_update_install_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return new StoreUpdateOperationResult
            {
                OverallState = StoreUpdateOperationState.OtherError
            };
        }
    }

    private AppUpdateSnapshot BuildDeferredSnapshot(bool hasMandatoryUpdate, string? availableVersion, string message)
    {
        return new AppUpdateSnapshot(
            AppUpdateState.Deferred,
            hasMandatoryUpdate ? "Mandatory update available" : "Update available",
            message,
            hasMandatoryUpdate,
            isProgressVisible: false,
            progressValue: 0d,
            installedVersion: _versionProvider.InstalledVersion,
            availableVersion: availableVersion);
    }

    private void Publish(AppUpdateSnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private static string BuildRetryMessage(string phase, StoreUpdateOperationState resultState, bool hasMandatoryUpdate)
    {
        var prefix = hasMandatoryUpdate
            ? "Mandatory update is available."
            : "Update is available.";

        return resultState switch
        {
            StoreUpdateOperationState.Canceled => $"{prefix} Background {phase} was canceled and will retry later.",
            StoreUpdateOperationState.ErrorLowBattery => $"{prefix} Background {phase} paused because the device reported low battery.",
            StoreUpdateOperationState.Completed => $"{prefix} Background {phase} finished successfully.",
            _ => $"{prefix} Background {phase} could not finish and will retry later."
        };
    }

    private static string? GetHighestVersion(IReadOnlyList<StorePackageUpdateInfo> updates)
    {
        Version? highest = null;
        string? highestText = null;

        foreach (var update in updates)
        {
            if (!Version.TryParse(update.Version, out var parsed))
            {
                continue;
            }

            if (highest is null || parsed > highest)
            {
                highest = parsed;
                highestText = update.Version;
            }
        }

        return highestText;
    }
}
