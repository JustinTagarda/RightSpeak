using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

internal sealed class StoreAppUpdateService : IAppUpdateService
{
    private static readonly TimeSpan DeferredInstallRetryDelay = TimeSpan.FromHours(6);
    private static readonly TimeSpan DeferredInstallTimeout = TimeSpan.FromMinutes(3);
    private readonly Func<IStoreUpdateClient>? _storeUpdateClientFactory;
    private IStoreUpdateClient? _storeUpdateClient;
    private readonly IAppVersionProvider _versionProvider;
    private readonly IDeferredUpdateStateStore _deferredUpdateStateStore;
    private readonly IDeferredUpdateHistoryStore _deferredUpdateHistoryStore;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    private DeferredUpdateState? _deferredUpdatePendingState;
    private DeferredUpdateState? _deferredUpdateHistoryState;
    private bool _initialized;
    private bool _startRequested;
    private AppUpdateSnapshot _currentSnapshot;

    public StoreAppUpdateService(
        IStoreUpdateClient storeUpdateClient,
        IAppVersionProvider versionProvider,
        IDeferredUpdateStateStore deferredUpdateStateStore,
        IDeferredUpdateHistoryStore deferredUpdateHistoryStore)
        : this(null, storeUpdateClient, versionProvider, deferredUpdateStateStore, deferredUpdateHistoryStore)
    {
    }

    internal StoreAppUpdateService(
        Func<IStoreUpdateClient> storeUpdateClientFactory,
        IAppVersionProvider versionProvider,
        IDeferredUpdateStateStore deferredUpdateStateStore,
        IDeferredUpdateHistoryStore deferredUpdateHistoryStore)
        : this(storeUpdateClientFactory, null, versionProvider, deferredUpdateStateStore, deferredUpdateHistoryStore)
    {
    }

    private StoreAppUpdateService(
        Func<IStoreUpdateClient>? storeUpdateClientFactory,
        IStoreUpdateClient? storeUpdateClient,
        IAppVersionProvider versionProvider,
        IDeferredUpdateStateStore deferredUpdateStateStore,
        IDeferredUpdateHistoryStore deferredUpdateHistoryStore)
    {
        _storeUpdateClientFactory = storeUpdateClientFactory;
        _storeUpdateClient = storeUpdateClient;
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _deferredUpdateStateStore = deferredUpdateStateStore ?? throw new ArgumentNullException(nameof(deferredUpdateStateStore));
        _deferredUpdateHistoryStore = deferredUpdateHistoryStore ?? throw new ArgumentNullException(nameof(deferredUpdateHistoryStore));
        _currentSnapshot = AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion);
    }

    public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    public AppUpdateSnapshot CurrentSnapshot => _currentSnapshot;

    public bool HasDeferredInstallPending => _deferredUpdatePendingState?.HasPendingInstall == true;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            if (_startRequested)
            {
                return;
            }

            _startRequested = true;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunUpdateFlowAsync(
                publishSnapshots: false,
                allowFallbackUi: true,
                userInitiated: false,
                progress: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<UserInitiatedUpdateResult> CheckForUpdatesOnDemandAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUpdateFlowAsync(
                publishSnapshots: true,
                allowFallbackUi: true,
                userInitiated: true,
                progress: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DeferredInstallAttemptResult> TryApplyDeferredInstallOnExitAsync(
        IProgress<AppUpdateSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (!await _operationGate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
        {
            return new DeferredInstallAttemptResult(
                Attempted: false,
                Succeeded: false,
                TimedOut: false,
                Message: "An update operation is already running.");
        }

        try
        {
            var deferredState = _deferredUpdatePendingState;
            if (deferredState is null || !deferredState.HasPendingInstall)
            {
                return new DeferredInstallAttemptResult(
                    Attempted: false,
                    Succeeded: false,
                    TimedOut: false,
                    Message: "No deferred update is pending.");
            }

            var now = DateTimeOffset.UtcNow;
            if (!deferredState.CanAttemptInstall(now))
            {
                AppDiagnostics.Info(
                    "deferred_update_install_throttled",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot,
                        ["retryNotBeforeUtc"] = deferredState.RetryNotBeforeUtc?.ToString("O"),
                        ["lastFailureUtc"] = deferredState.LastFailureUtc?.ToString("O")
                    });

                return new DeferredInstallAttemptResult(
                    Attempted: false,
                    Succeeded: false,
                    TimedOut: false,
                    Message: "The deferred update install is temporarily throttled.");
            }

            if (!GetStoreUpdateClient().IsSupported)
            {
                AppDiagnostics.Info(
                    "deferred_update_install_skipped_unpacked",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot
                    });
                return new DeferredInstallAttemptResult(
                    Attempted: false,
                    Succeeded: false,
                    TimedOut: false,
                    Message: "Update installation is unavailable outside the Microsoft Store package.");
            }

            AppDiagnostics.Info(
                "deferred_update_install_started",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot,
                    ["availableVersion"] = deferredState.AvailableVersion
                });

            PublishSnapshot(
                new AppUpdateSnapshot(
                    AppUpdateState.Installing,
                    "Applying update",
                    "Installing the downloaded Microsoft Store update.",
                    isMandatoryUpdateAvailable: false,
                    isProgressVisible: true,
                    progressValue: 0d,
                    installedVersion: _versionProvider.InstalledVersion,
                    availableVersion: deferredState.AvailableVersion),
                publishSnapshots: true);

            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(DeferredInstallTimeout);

            try
            {
                var result = await GetStoreUpdateClient().TrySilentDownloadAndInstallAsync(
                    progressItem =>
                    {
                        var snapshot = BuildProgressSnapshot(
                            AppUpdateState.Installing,
                            "Applying update",
                            progressItem.Progress,
                            "Installing the downloaded Microsoft Store update.",
                            isMandatoryUpdateAvailable: false,
                            deferredState.AvailableVersion);
                        PublishSnapshot(snapshot, publishSnapshots: true);
                        progress?.Report(snapshot);
                    },
                    timeoutCancellationTokenSource.Token).ConfigureAwait(false);

                if (result.OverallState == StoreUpdateOperationState.Completed)
                {
                    await FinalizeDeferredInstallSuccessAsync(cancellationToken).ConfigureAwait(false);
                    PublishSnapshot(
                        new AppUpdateSnapshot(
                            AppUpdateState.Completed,
                            "Update applied",
                            "The Microsoft Store update will take effect the next time the app opens.",
                            isMandatoryUpdateAvailable: false,
                            isProgressVisible: false,
                            progressValue: 1d,
                            installedVersion: _versionProvider.InstalledVersion,
                            availableVersion: deferredState.AvailableVersion),
                        publishSnapshots: true);

                    AppDiagnostics.Info(
                        "deferred_update_install_completed",
                        new Dictionary<string, string?>
                        {
                            ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot,
                            ["availableVersion"] = deferredState.AvailableVersion
                        });

                    return new DeferredInstallAttemptResult(
                        Attempted: true,
                        Succeeded: true,
                        TimedOut: false,
                        Message: "Update installation completed.");
                }

                var failureMessage = BuildDeferredInstallFailureMessage(result.OverallState);
                await RecordDeferredInstallFailureAsync(deferredState, failureMessage, cancellationToken).ConfigureAwait(false);
                await ClearPendingDeferredInstallStateAsync(cancellationToken).ConfigureAwait(false);
                PublishSnapshot(
                    new AppUpdateSnapshot(
                        AppUpdateState.Failed,
                        "Update installation failed",
                        failureMessage,
                        isMandatoryUpdateAvailable: false,
                        isProgressVisible: false,
                        progressValue: 0d,
                        installedVersion: _versionProvider.InstalledVersion,
                        availableVersion: deferredState.AvailableVersion),
                    publishSnapshots: true);

                AppDiagnostics.Warn(
                    "deferred_update_install_failed",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot,
                        ["overallState"] = result.OverallState.ToString(),
                        ["failedPackageCount"] = result.FailedPackageFamilyNames.Count.ToString()
                    });

                return new DeferredInstallAttemptResult(
                    Attempted: true,
                    Succeeded: false,
                    TimedOut: false,
                    Message: failureMessage);
            }
            catch (OperationCanceledException) when (timeoutCancellationTokenSource.IsCancellationRequested)
            {
                var failureMessage = "The deferred update install timed out.";
                await RecordDeferredInstallFailureAsync(deferredState, failureMessage, cancellationToken).ConfigureAwait(false);
                await ClearPendingDeferredInstallStateAsync(cancellationToken).ConfigureAwait(false);
                AppDiagnostics.Warn(
                    "deferred_update_install_timed_out",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot,
                        ["timeoutMinutes"] = DeferredInstallTimeout.TotalMinutes.ToString()
                    });

                PublishSnapshot(
                    new AppUpdateSnapshot(
                        AppUpdateState.Failed,
                        "Update installation timed out",
                        failureMessage,
                        isMandatoryUpdateAvailable: false,
                        isProgressVisible: false,
                        progressValue: 0d,
                        installedVersion: _versionProvider.InstalledVersion,
                        availableVersion: deferredState.AvailableVersion),
                    publishSnapshots: true);

                return new DeferredInstallAttemptResult(
                    Attempted: true,
                    Succeeded: false,
                    TimedOut: true,
                    Message: failureMessage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var failureMessage = "The deferred update install was canceled.";
                await RecordDeferredInstallFailureAsync(deferredState, failureMessage, cancellationToken).ConfigureAwait(false);
                await ClearPendingDeferredInstallStateAsync(cancellationToken).ConfigureAwait(false);
                AppDiagnostics.Info(
                    "deferred_update_install_canceled",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot
                    });

                return new DeferredInstallAttemptResult(
                    Attempted: true,
                    Succeeded: false,
                    TimedOut: false,
                    Message: failureMessage);
            }
            catch (Exception ex)
            {
                var failureMessage = "The deferred update install failed.";
                await RecordDeferredInstallFailureAsync(deferredState, $"{failureMessage} {ex.Message}", cancellationToken).ConfigureAwait(false);
                await ClearPendingDeferredInstallStateAsync(cancellationToken).ConfigureAwait(false);
                AppDiagnostics.Error(
                    "deferred_update_install_exception",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = deferredState.PackageIdentitySnapshot,
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message
                    });

                PublishSnapshot(
                    new AppUpdateSnapshot(
                        AppUpdateState.Failed,
                        "Update installation failed",
                        failureMessage,
                        isMandatoryUpdateAvailable: false,
                        isProgressVisible: false,
                        progressValue: 0d,
                        installedVersion: _versionProvider.InstalledVersion,
                        availableVersion: deferredState.AvailableVersion),
                    publishSnapshots: true);

                return new DeferredInstallAttemptResult(
                    Attempted: true,
                    Succeeded: false,
                    TimedOut: false,
                    Message: failureMessage);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _storeUpdateClient ??= _storeUpdateClientFactory?.Invoke()
                ?? throw new InvalidOperationException("A Store update client is required.");
            var pendingState = _deferredUpdateStateStore.TryLoad();
            var historyState = _deferredUpdateHistoryStore.TryLoad();

            if (pendingState is not null && !pendingState.HasPendingInstall)
            {
                historyState = MergeHistoryState(historyState, pendingState);
                _ = await _deferredUpdateHistoryStore.SaveAsync(historyState, cancellationToken).ConfigureAwait(false);
                _ = await _deferredUpdateStateStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                pendingState = null;
            }

            _deferredUpdatePendingState ??= pendingState;
            _deferredUpdateHistoryState ??= historyState;
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<UserInitiatedUpdateResult> RunUpdateFlowAsync(
        bool publishSnapshots,
        bool allowFallbackUi,
        bool userInitiated,
        IProgress<AppUpdateSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (!GetStoreUpdateClient().IsSupported)
        {
            AppDiagnostics.Info(
                "store_update_skipped_unpacked",
                new Dictionary<string, string?>
                {
                    ["flow"] = userInitiated ? "user" : "startup"
                });

            PublishSnapshot(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion), publishSnapshots);
            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Unavailable,
                "Update check unavailable outside the Microsoft Store package.");
        }

        AppDiagnostics.Info(
            "store_update_check_started",
            new Dictionary<string, string?>
            {
                ["flow"] = userInitiated ? "user" : "startup"
            });

        PublishSnapshot(
            new AppUpdateSnapshot(
                AppUpdateState.Checking,
                "Checking for updates",
                userInitiated ? "Looking for Microsoft Store updates." : "Checking Microsoft Store updates.",
                isMandatoryUpdateAvailable: false,
                isProgressVisible: false,
                progressValue: 0d,
                installedVersion: _versionProvider.InstalledVersion),
            publishSnapshots);

        IReadOnlyList<StorePackageUpdateInfo> updates;
        try
        {
            updates = await GetStoreUpdateClient().GetAvailableUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Info(
                "store_update_check_canceled",
                new Dictionary<string, string?>
                {
                    ["flow"] = userInitiated ? "user" : "startup"
                });

            await RecordLastCheckAsync(null, null, CancellationToken.None).ConfigureAwait(false);

            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Canceled,
                "Update check was canceled.");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "store_update_check_failed",
                new Dictionary<string, string?>
                {
                    ["flow"] = userInitiated ? "user" : "startup",
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });

            await RecordLastCheckAsync(null, null, CancellationToken.None).ConfigureAwait(false);

            PublishSnapshot(
                new AppUpdateSnapshot(
                    AppUpdateState.Failed,
                    "Update check failed",
                    "Could not reach Microsoft Store for update information.",
                    isMandatoryUpdateAvailable: false,
                    isProgressVisible: false,
                    progressValue: 0d,
                    installedVersion: _versionProvider.InstalledVersion),
                publishSnapshots);

            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Unavailable,
                "Update check unavailable.");
        }

        await RecordLastCheckAsync(
            updates.Count == 0 ? null : BuildPackageIdentitySnapshot(updates),
            updates.Count == 0 ? null : GetHighestVersion(updates),
            CancellationToken.None).ConfigureAwait(false);

        if (updates.Count == 0)
        {
            AppDiagnostics.Info(
                "store_update_none_available",
                new Dictionary<string, string?>
                {
                    ["flow"] = userInitiated ? "user" : "startup"
                });

            PublishSnapshot(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion), publishSnapshots);
            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.NotAvailable,
                "No update available.");
        }

        var hasMandatoryUpdate = updates.Any(update => update.IsMandatory);
        var availableVersion = GetHighestVersion(updates);
        var packageIdentitySnapshot = BuildPackageIdentitySnapshot(updates);

        AppDiagnostics.Info(
            "store_update_available",
            new Dictionary<string, string?>
            {
                ["flow"] = userInitiated ? "user" : "startup",
                ["count"] = updates.Count.ToString(),
                ["mandatory"] = hasMandatoryUpdate.ToString(),
                ["availableVersion"] = availableVersion,
                ["canSilentDownload"] = GetStoreUpdateClient().CanSilentlyDownloadUpdates.ToString(),
                ["packageIdentitySnapshot"] = packageIdentitySnapshot
            });

        if (userInitiated)
        {
            PublishSnapshot(AppUpdateSnapshot.Idle(_versionProvider.InstalledVersion), publishSnapshots);
            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Available,
                "Update available.");
        }

        if (GetStoreUpdateClient().CanSilentlyDownloadUpdates)
        {
            var silentDownloadResult = await TrySilentDownloadAsync(
                hasMandatoryUpdate,
                availableVersion,
                packageIdentitySnapshot,
                publishSnapshots,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (silentDownloadResult.OverallState == StoreUpdateOperationState.Completed)
            {
                var deferredSaved = await TryPersistDeferredInstallAsync(
                    packageIdentitySnapshot,
                    availableVersion,
                    cancellationToken).ConfigureAwait(false);

                var message = deferredSaved
                    ? "Update downloaded. It will install the next time you close the app."
                    : "Update downloaded, but deferred install could not be saved.";

                PublishSnapshot(
                    BuildDeferredSnapshot(
                        hasMandatoryUpdate,
                        availableVersion,
                        _versionProvider.InstalledVersion,
                        deferredSaved ? "Update downloaded. It will install when you close the app." : "Update downloaded."),
                    publishSnapshots);

                return new UserInitiatedUpdateResult(
                    UserInitiatedUpdateAvailability.InstalledOrQueued,
                    message);
            }

            if (silentDownloadResult.OverallState == StoreUpdateOperationState.Canceled)
            {
                if (userInitiated)
                {
                    PublishSnapshot(
                        new AppUpdateSnapshot(
                            AppUpdateState.Failed,
                            "Update download canceled",
                            "The Microsoft Store download was canceled.",
                            hasMandatoryUpdate,
                            isProgressVisible: false,
                            progressValue: 0d,
                            installedVersion: _versionProvider.InstalledVersion,
                            availableVersion: availableVersion),
                        publishSnapshots);

                    return new UserInitiatedUpdateResult(
                        UserInitiatedUpdateAvailability.Canceled,
                        "Update download was canceled.");
                }

                AppDiagnostics.Info(
                    "store_update_silent_download_fell_back_after_cancel",
                    new Dictionary<string, string?>
                    {
                        ["flow"] = "startup",
                        ["packageIdentitySnapshot"] = packageIdentitySnapshot
                    });
            }

            if (!(silentDownloadResult.OverallState == StoreUpdateOperationState.Canceled && !userInitiated))
            {
                AppDiagnostics.Warn(
                    "store_update_silent_download_failed",
                    new Dictionary<string, string?>
                    {
                        ["flow"] = userInitiated ? "user" : "startup",
                        ["overallState"] = silentDownloadResult.OverallState.ToString(),
                        ["packageIdentitySnapshot"] = packageIdentitySnapshot
                    });
            }
        }

        if (!allowFallbackUi)
        {
            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Unavailable,
                "Microsoft Store update UI is unavailable.");
        }

        var fallbackResult = await RunFallbackInstallAsync(
            hasMandatoryUpdate,
            availableVersion,
            packageIdentitySnapshot,
            publishSnapshots,
            progress,
            cancellationToken).ConfigureAwait(false);

        return fallbackResult;
    }

    private async Task<UserInitiatedUpdateResult> RunFallbackInstallAsync(
        bool hasMandatoryUpdate,
        string? availableVersion,
        string packageIdentitySnapshot,
        bool publishSnapshots,
        IProgress<AppUpdateSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        AppDiagnostics.Info(
            "store_update_fallback_started",
            new Dictionary<string, string?>
            {
                ["packageIdentitySnapshot"] = packageIdentitySnapshot
            });

        PublishSnapshot(
            new AppUpdateSnapshot(
                AppUpdateState.Installing,
                "Opening Microsoft Store",
                "Microsoft Store is handling the download and install flow.",
                hasMandatoryUpdate,
                isProgressVisible: true,
                progressValue: 0d,
                installedVersion: _versionProvider.InstalledVersion,
                availableVersion: availableVersion),
            publishSnapshots);

        try
        {
            var operationResult = await GetStoreUpdateClient().RequestDownloadAndInstallAsync(
                progressItem =>
                {
                    var snapshot = BuildProgressSnapshot(
                        AppUpdateState.Installing,
                        "Installing update",
                        progressItem.Progress,
                        "Microsoft Store is handling the update.",
                        hasMandatoryUpdate,
                        availableVersion);
                    PublishSnapshot(snapshot, publishSnapshots);
                    progress?.Report(snapshot);
                },
                cancellationToken).ConfigureAwait(false);

            if (operationResult.OverallState == StoreUpdateOperationState.Completed)
            {
                PublishSnapshot(
                    BuildCompletedSnapshot(
                        hasMandatoryUpdate,
                        availableVersion,
                        _versionProvider.InstalledVersion,
                        "The Microsoft Store update will take effect the next time the app opens."),
                    publishSnapshots);

                AppDiagnostics.Info(
                    "store_update_fallback_completed",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                        ["overallState"] = operationResult.OverallState.ToString()
                    });

                return new UserInitiatedUpdateResult(
                    UserInitiatedUpdateAvailability.InstalledOrQueued,
                    "Microsoft Store accepted the update request.");
            }

            var message = BuildFallbackFailureMessage(operationResult.OverallState);
            PublishSnapshot(
                new AppUpdateSnapshot(
                    AppUpdateState.Failed,
                    "Update install failed",
                    message,
                    hasMandatoryUpdate,
                    isProgressVisible: false,
                    progressValue: 0d,
                    installedVersion: _versionProvider.InstalledVersion,
                    availableVersion: availableVersion),
                publishSnapshots);

            AppDiagnostics.Warn(
                "store_update_fallback_failed",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["overallState"] = operationResult.OverallState.ToString(),
                    ["failedPackageCount"] = operationResult.FailedPackageFamilyNames.Count.ToString()
                });

            return new UserInitiatedUpdateResult(
                operationResult.OverallState == StoreUpdateOperationState.Canceled
                    ? UserInitiatedUpdateAvailability.Canceled
                    : UserInitiatedUpdateAvailability.Failed,
                message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var message = "Microsoft Store update request was canceled.";
            PublishSnapshot(
                new AppUpdateSnapshot(
                    AppUpdateState.Failed,
                    "Update install canceled",
                    message,
                    hasMandatoryUpdate,
                    isProgressVisible: false,
                    progressValue: 0d,
                    installedVersion: _versionProvider.InstalledVersion,
                    availableVersion: availableVersion),
                publishSnapshots);

            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Canceled,
                message);
        }
        catch (Exception ex)
        {
            var message = "Microsoft Store update request failed.";
            AppDiagnostics.Error(
                "store_update_fallback_exception",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });

            PublishSnapshot(
                new AppUpdateSnapshot(
                    AppUpdateState.Failed,
                    "Update install failed",
                    message,
                    hasMandatoryUpdate,
                    isProgressVisible: false,
                    progressValue: 0d,
                    installedVersion: _versionProvider.InstalledVersion,
                    availableVersion: availableVersion),
                publishSnapshots);

            return new UserInitiatedUpdateResult(
                UserInitiatedUpdateAvailability.Failed,
                message);
        }
    }

    private async Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
        bool hasMandatoryUpdate,
        string? availableVersion,
        string packageIdentitySnapshot,
        bool publishSnapshots,
        IProgress<AppUpdateSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            AppDiagnostics.Info(
                "store_update_silent_download_started",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot
                });

            var result = await GetStoreUpdateClient().TrySilentDownloadAsync(
                progressItem =>
                {
                    var snapshot = BuildProgressSnapshot(
                        AppUpdateState.Downloading,
                        "Downloading update",
                        progressItem.Progress,
                        $"{(int)Math.Round(progressItem.Progress * 100d)}% complete",
                        hasMandatoryUpdate,
                        availableVersion);
                    PublishSnapshot(snapshot, publishSnapshots);
                    progress?.Report(snapshot);
                },
                cancellationToken).ConfigureAwait(false);

            AppDiagnostics.Info(
                "store_update_silent_download_finished",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["overallState"] = result.OverallState.ToString(),
                    ["failedPackageCount"] = result.FailedPackageFamilyNames.Count.ToString()
                });

            if (result.OverallState == StoreUpdateOperationState.Completed)
            {
                PublishSnapshot(
                    BuildDeferredSnapshot(
                        hasMandatoryUpdate,
                        availableVersion,
                        _versionProvider.InstalledVersion,
                        "Update downloaded and waiting for exit-time install."),
                    publishSnapshots);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Info(
                "store_update_download_canceled",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot
                });

            return new StoreUpdateOperationResult
            {
                OverallState = StoreUpdateOperationState.Canceled
            };
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "store_update_download_failed",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });

            return new StoreUpdateOperationResult
            {
                OverallState = StoreUpdateOperationState.OtherError
            };
        }
    }

    private async Task<bool> TryPersistDeferredInstallAsync(
        string packageIdentitySnapshot,
        string? availableVersion,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = _deferredUpdateHistoryState;
        if (existing is not null &&
            existing.PackageIdentitySnapshot == packageIdentitySnapshot &&
            !existing.HasPendingInstall &&
            existing.LastInstallAttemptFailed &&
            existing.RetryNotBeforeUtc is { } retryNotBefore &&
            now < retryNotBefore)
        {
            AppDiagnostics.Info(
                "deferred_update_install_save_skipped_retry_window",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["retryNotBeforeUtc"] = retryNotBefore.ToString("O")
                });

            return false;
        }

        var pendingState = DeferredUpdateState.CreatePending(
            packageIdentitySnapshot,
            _versionProvider.InstalledVersion,
            availableVersion,
            now);

        _deferredUpdatePendingState = pendingState;
        var saved = await _deferredUpdateStateStore.SaveAsync(pendingState, cancellationToken).ConfigureAwait(false);
        if (saved)
        {
            AppDiagnostics.Info(
                "deferred_update_install_saved",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["availableVersion"] = availableVersion
                });
        }
        else
        {
            AppDiagnostics.Warn(
                "deferred_update_install_save_not_persisted",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["availableVersion"] = availableVersion
                });
        }

        var historyState = BuildCheckedHistoryState(existing, packageIdentitySnapshot, availableVersion, now);
        _deferredUpdateHistoryState = historyState;
        var historySaved = await _deferredUpdateHistoryStore.SaveAsync(historyState, cancellationToken).ConfigureAwait(false);
        if (!historySaved)
        {
            AppDiagnostics.Warn(
                "deferred_update_history_save_not_persisted",
                new Dictionary<string, string?>
                {
                    ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                    ["availableVersion"] = availableVersion
                });
        }

        return saved;
    }

    private async Task RecordDeferredInstallFailureAsync(
        DeferredUpdateState state,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var baseState = _deferredUpdateHistoryState is not null &&
            _deferredUpdateHistoryState.PackageIdentitySnapshot == state.PackageIdentitySnapshot
            ? _deferredUpdateHistoryState
            : state;
        var failedState = baseState
            .MarkInstallFailure(DateTimeOffset.UtcNow, failureMessage, DeferredInstallRetryDelay);
        _deferredUpdateHistoryState = failedState;
        _ = await _deferredUpdateHistoryStore.SaveAsync(failedState, cancellationToken).ConfigureAwait(false);

        AppDiagnostics.Info(
            "deferred_update_install_failure_recorded",
            new Dictionary<string, string?>
            {
                ["packageIdentitySnapshot"] = state.PackageIdentitySnapshot,
                ["retryNotBeforeUtc"] = failedState.RetryNotBeforeUtc?.ToString("O"),
                ["failureCount"] = failedState.FailureCount.ToString()
            });
    }

    private async Task FinalizeDeferredInstallSuccessAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var historyState = _deferredUpdateHistoryState ?? _deferredUpdatePendingState;
        if (historyState is not null)
        {
            var clearedHistoryState = historyState.MarkInstallSuccess(now);
            _deferredUpdateHistoryState = clearedHistoryState;
            var saved = await _deferredUpdateHistoryStore.SaveAsync(clearedHistoryState, cancellationToken).ConfigureAwait(false);
            if (saved)
            {
                AppDiagnostics.Info(
                    "deferred_update_history_cleared",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = clearedHistoryState.PackageIdentitySnapshot,
                        ["lastCheckUtc"] = clearedHistoryState.LastCheckUtc?.ToString("O")
                    });
            }
            else
            {
                AppDiagnostics.Warn(
                    "deferred_update_history_clear_failed",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = historyState.PackageIdentitySnapshot
                    });
            }
        }

        await ClearPendingDeferredInstallStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearPendingDeferredInstallStateAsync(CancellationToken cancellationToken)
    {
        _deferredUpdatePendingState = null;
        var cleared = await _deferredUpdateStateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        if (cleared)
        {
            AppDiagnostics.Info("deferred_update_state_cleared");
        }
        else
        {
            AppDiagnostics.Warn("deferred_update_state_clear_failed");
        }
    }

    private DeferredUpdateState BuildCheckedHistoryState(
        DeferredUpdateState? existingHistoryState,
        string packageIdentitySnapshot,
        string? availableVersion,
        DateTimeOffset checkedUtc)
    {
        var state = existingHistoryState is null || existingHistoryState.HasPendingInstall
            ? DeferredUpdateState.CreateChecked(_versionProvider.InstalledVersion, checkedUtc)
            : existingHistoryState.MarkChecked(checkedUtc);

        return state with
        {
            PackageIdentitySnapshot = packageIdentitySnapshot,
            AvailableVersion = availableVersion,
            InstalledVersion = _versionProvider.InstalledVersion
        };
    }

    private static DeferredUpdateState MergeHistoryState(
        DeferredUpdateState? existingHistoryState,
        DeferredUpdateState legacyState)
    {
        if (existingHistoryState is null)
        {
            return legacyState;
        }

        return GetStateTimestamp(legacyState) >= GetStateTimestamp(existingHistoryState)
            ? legacyState
            : existingHistoryState;
    }

    private static DateTimeOffset GetStateTimestamp(DeferredUpdateState state)
    {
        return state.LastAttemptUtc
            ?? state.LastCheckUtc
            ?? state.LastObservedUtc
            ?? state.LastFailureUtc
            ?? state.CreatedUtc;
    }

    private async Task RecordLastCheckAsync(
        string? packageIdentitySnapshot,
        string? availableVersion,
        CancellationToken cancellationToken)
    {
        var checkedUtc = DateTimeOffset.UtcNow;
        var existingState = _deferredUpdateHistoryState;
        var state = existingState is null || existingState.HasPendingInstall
            ? DeferredUpdateState.CreateChecked(_versionProvider.InstalledVersion, checkedUtc)
            : existingState.MarkChecked(checkedUtc);

        if (!string.IsNullOrWhiteSpace(packageIdentitySnapshot))
        {
            state = state with
            {
                PackageIdentitySnapshot = packageIdentitySnapshot,
                AvailableVersion = availableVersion,
                InstalledVersion = _versionProvider.InstalledVersion
            };
        }

        _deferredUpdateHistoryState = state;
        var saved = await _deferredUpdateHistoryStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        AppDiagnostics.Info(
            "store_update_check_completed",
            new Dictionary<string, string?>
            {
                ["packageIdentitySnapshot"] = packageIdentitySnapshot,
                ["availableVersion"] = availableVersion,
                ["saved"] = saved.ToString()
            });
    }

    private static AppUpdateSnapshot BuildDeferredSnapshot(
        bool hasMandatoryUpdate,
        string? availableVersion,
        string installedVersion,
        string message)
    {
        return new AppUpdateSnapshot(
            AppUpdateState.Deferred,
            hasMandatoryUpdate ? "Mandatory update available" : "Update available",
            message,
            hasMandatoryUpdate,
            isProgressVisible: false,
            progressValue: 1d,
            installedVersion: installedVersion,
            availableVersion: availableVersion);
    }

    private static AppUpdateSnapshot BuildCompletedSnapshot(
        bool hasMandatoryUpdate,
        string? availableVersion,
        string installedVersion,
        string message)
    {
        return new AppUpdateSnapshot(
            AppUpdateState.Completed,
            hasMandatoryUpdate ? "Mandatory update available" : "Update available",
            message,
            hasMandatoryUpdate,
            isProgressVisible: false,
            progressValue: 1d,
            installedVersion: installedVersion,
            availableVersion: availableVersion);
    }

    private AppUpdateSnapshot BuildProgressSnapshot(
        AppUpdateState state,
        string stageText,
        double progress,
        string statusMessage,
        bool isMandatoryUpdateAvailable,
        string? availableVersion)
    {
        return new AppUpdateSnapshot(
            state,
            stageText,
            statusMessage,
            isMandatoryUpdateAvailable,
            isProgressVisible: true,
            progressValue: progress,
            installedVersion: _versionProvider.InstalledVersion,
            availableVersion: availableVersion);
    }

    private void PublishSnapshot(AppUpdateSnapshot snapshot, bool publishSnapshots)
    {
        if (publishSnapshots)
        {
            _currentSnapshot = snapshot;
            try
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Error(
                    "store_update_snapshot_handler_failed",
                    new Dictionary<string, string?>
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message,
                        ["state"] = snapshot.State.ToString()
                    });
            }
        }
        else
        {
            _currentSnapshot = snapshot;
        }
    }

    private static string BuildFallbackFailureMessage(StoreUpdateOperationState state)
    {
        return state switch
        {
            StoreUpdateOperationState.Canceled => "The Microsoft Store update request was canceled.",
            StoreUpdateOperationState.ErrorLowBattery => "The Microsoft Store update paused because the device reported low battery.",
            StoreUpdateOperationState.Completed => "The Microsoft Store update request completed.",
            _ => "The Microsoft Store update request failed."
        };
    }

    private static string BuildDeferredInstallFailureMessage(StoreUpdateOperationState state)
    {
        return state switch
        {
            StoreUpdateOperationState.Canceled => "The deferred update install was canceled.",
            StoreUpdateOperationState.ErrorLowBattery => "The deferred update install paused because the device reported low battery.",
            StoreUpdateOperationState.Completed => "The deferred update install completed.",
            _ => "The deferred update install failed."
        };
    }

    private static string BuildPackageIdentitySnapshot(IReadOnlyList<StorePackageUpdateInfo> updates)
    {
        return string.Join(
            ";",
            updates
                .OrderBy(update => update.PackageFamilyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(update => update.Version, StringComparer.OrdinalIgnoreCase)
                .Select(update => $"{update.PackageFamilyName}:{update.Version}:{update.IsMandatory}"));
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

    private IStoreUpdateClient GetStoreUpdateClient()
    {
        return _storeUpdateClient ?? throw new InvalidOperationException("Store update client is not initialized.");
    }
}
