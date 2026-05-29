using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace RightSpeak.Services;

public sealed class StoreUpdateCoordinator : IStoreUpdateCoordinator
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const string StoreThrottleReasonMinInterval = "min_interval_30m";
    private const string StoreThrottleReasonRolling24h = "rolling_24h_limit";
    private static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromMinutes(30);
    private const int MaxChecksPerRollingDay = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private readonly IStoreContextProvider _storeContextProvider;
    private readonly IAppSettingsService _appSettingsService;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly DispatcherTimer _retryTimer;
    private readonly List<StoreQueueItem> _trackedQueueItems = new();
    private CancellationTokenSource? _checkCancellationTokenSource;
    private IReadOnlyList<StorePackageUpdate>? _cachedUpdates;
    private bool _isStarted;
    private bool _isDisposed;
    private StoreUpdateState _state = new(false, false, false);

    public StoreUpdateCoordinator(
        IStoreContextProvider storeContextProvider,
        IAppSettingsService appSettingsService,
        Dispatcher dispatcher)
    {
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
        _appSettingsService = appSettingsService ?? throw new ArgumentNullException(nameof(appSettingsService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = RetryDelay
        };
        _retryTimer.Tick += OnRetryTimerTick;
    }

    public event EventHandler<StoreUpdateState>? StateChanged;

    public StoreUpdateState CurrentState => _state;

    public void Start()
    {
        if (_isDisposed)
        {
            return;
        }

        _isStarted = true;
        _ = RunCheckAsync();
    }

    public async Task RequestInstallAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!HasPackageIdentity())
        {
            UpdateState(BuildAvailabilityState(false, false, false));
            return;
        }

        var context = _storeContextProvider.TryGetDefaultContext();
        if (context is null)
        {
            UpdateState(BuildAvailabilityState(false, false, false));
            return;
        }

        try
        {
            UpdateState(_state with { IsBusy = true });
            SetProgressState(0, "Preparing", "Preparing Microsoft Store update request...");

            var updates = _cachedUpdates;
            if (updates is null || updates.Count == 0 || IsCachedUpdatesExpired())
            {
                if (TryGetThrottleSkipReason(DateTimeOffset.UtcNow, out var skipReason))
                {
                    AppDiagnostics.Info(
                        "store_update_check_skipped_throttled",
                        BuildCheckDiagnostics(skipReason, 0, null, context: "request_install"));
                    updates = null;
                }
                else
                {
                    RegisterStoreCheckAttempt(DateTimeOffset.UtcNow);
                    updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken).ConfigureAwait(false);
                    _cachedUpdates = updates;
                    _appSettingsService.Current.StoreUpdateLastKnownAvailable = updates.Count > 0;
                    _appSettingsService.Save();
                    AppDiagnostics.Info(
                        "store_update_check_completed",
                        BuildCheckDiagnostics(null, updates.Count, null, context: "request_install"));
                }
            }

            if (updates is null || updates.Count == 0)
            {
                _cachedUpdates = null;
                UpdateState(BuildAvailabilityState(true, false, false));
                SetProgressState(0, "Failed", "No updates are currently available. Try again later.", isTerminal: true);
                ScheduleRetry();
                return;
            }

            var operation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            operation.Progress = (_, status) => HandleInstallProgress(status);
            var result = await operation.AsTask(cancellationToken).ConfigureAwait(false);

            var terminal = BuildTerminalProgress(result.OverallState);
            SetProgressState(terminal.ProgressPercent, terminal.Phase, terminal.ResultMessage, isTerminal: true);
            AppDiagnostics.Info(
                "store_update_install_requested",
                new Dictionary<string, string?>
                {
                    ["overallState"] = result.OverallState.ToString(),
                    ["itemCount"] = updates.Count.ToString(CultureInfo.InvariantCulture),
                    ["context"] = "request_install"
                });

            _cachedUpdates = null;
            await RunCheckAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown or user cancellation.
            SetProgressState(_state.ProgressPercent, "Canceled", "Update request was canceled.", isTerminal: true);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_update_install_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message,
                    ["context"] = "request_install"
                });
            UpdateState(BuildAvailabilityState(true, false, false));
            SetProgressState(_state.ProgressPercent, "Failed", "Update failed. Please retry later.", isTerminal: true);
        }
        finally
        {
            UpdateState(_state with { IsBusy = false });
        }
    }

    public void Stop()
    {
        if (_isDisposed)
        {
            return;
        }

        _isStarted = false;
        _retryTimer.Stop();
        lock (_sync)
        {
            _checkCancellationTokenSource?.Cancel();
            _checkCancellationTokenSource?.Dispose();
            _checkCancellationTokenSource = null;
        }

        ClearQueueSubscriptions();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Stop();
        _retryTimer.Tick -= OnRetryTimerTick;
        _isDisposed = true;
    }

    private async Task RunCheckAsync()
    {
        if (!_isStarted || _isDisposed)
        {
            return;
        }

        if (!HasPackageIdentity())
        {
            _cachedUpdates = null;
            _retryTimer.Stop();
            UpdateState(BuildAvailabilityState(false, false, false));
            return;
        }

        var context = _storeContextProvider.TryGetDefaultContext();
        if (context is null)
        {
            _cachedUpdates = null;
            _retryTimer.Stop();
            UpdateState(BuildAvailabilityState(false, false, false));
            return;
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            _checkCancellationTokenSource?.Cancel();
            _checkCancellationTokenSource?.Dispose();
            _checkCancellationTokenSource = new CancellationTokenSource();
            cts = _checkCancellationTokenSource;
        }

        await RecoverQueueProgressStateAsync(context, cts.Token).ConfigureAwait(false);
        if (_state.IsBusy)
        {
            return;
        }

        if (TryGetThrottleSkipReason(DateTimeOffset.UtcNow, out var skipReason))
        {
            var lastKnownAvailable = _appSettingsService.Current.StoreUpdateLastKnownAvailable;
            AppDiagnostics.Info(
                "store_update_check_skipped_throttled",
                BuildCheckDiagnostics(skipReason, null, null, context: "startup"));
            UpdateState(BuildAvailabilityState(true, lastKnownAvailable, false));
            if (!lastKnownAvailable)
            {
                ScheduleRetry();
            }

            return;
        }

        try
        {
            RegisterStoreCheckAttempt(DateTimeOffset.UtcNow);
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cts.Token).ConfigureAwait(false);
            _cachedUpdates = updates;
            _appSettingsService.Current.StoreUpdateLastKnownAvailable = updates.Count > 0;
            _appSettingsService.Save();
            AppDiagnostics.Info(
                "store_update_check_completed",
                BuildCheckDiagnostics(null, updates.Count, null, context: "startup"));
            if (updates.Count > 0)
            {
                _retryTimer.Stop();
                UpdateState(BuildAvailabilityState(true, true, false));
                return;
            }

            UpdateState(BuildAvailabilityState(true, false, false));
            ScheduleRetry();
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown/restart.
        }
        catch (Exception ex)
        {
            _cachedUpdates = null;
            _retryTimer.Stop();
            AppDiagnostics.Warn(
                "store_update_check_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message,
                    ["context"] = "startup"
                });
            UpdateState(BuildAvailabilityState(true, false, false));
        }
    }

    private async Task RecoverQueueProgressStateAsync(StoreContext context, CancellationToken cancellationToken)
    {
        try
        {
            var queueItems = await context.GetAssociatedStoreQueueItemsAsync().AsTask(cancellationToken).ConfigureAwait(false);
            ClearQueueSubscriptions();

            var appPackageFamilyName = GetCurrentPackageFamilyName();
            var relevant = queueItems
                .Where(item => string.IsNullOrWhiteSpace(appPackageFamilyName) ||
                               string.Equals(item.PackageFamilyName, appPackageFamilyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (relevant.Count == 0)
            {
                if (_state.IsProgressVisible && !_state.IsBusy)
                {
                    UpdateState(_state with { IsProgressVisible = false, ProgressPercent = 0, ProgressPhase = string.Empty, ProgressDetail = null, ProgressResult = null });
                }

                return;
            }

            foreach (var item in relevant)
            {
                item.StatusChanged += OnQueueItemStatusChanged;
                item.Completed += OnQueueItemCompleted;
                _trackedQueueItems.Add(item);
                ApplyQueueItemStatus(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown/restart.
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_update_queue_recovery_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private void OnQueueItemStatusChanged(StoreQueueItem sender, object args)
    {
        _ = args;
        ApplyQueueItemStatus(sender);
    }

    private void OnQueueItemCompleted(StoreQueueItem sender, StoreQueueItemCompletedEventArgs args)
    {
        _ = args;
        ApplyQueueItemStatus(sender);
    }

    private void ApplyQueueItemStatus(StoreQueueItem item)
    {
        try
        {
            var status = item.GetCurrentStatus();
            var percent = TryExtractPercent(status);
            var stateName = TryReadProperty(status, "PackageInstallState") ?? TryReadProperty(status, "InstallState") ?? "Pending";
            var phase = MapStateToPhase(stateName);
            var detail = BuildQueueStatusDetail(item, status);
            var isTerminal = IsTerminalPhase(phase);
            SetProgressState(percent, phase, detail, isTerminal);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_update_queue_status_apply_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private string BuildQueueStatusDetail(StoreQueueItem item, object status)
    {
        var packageName = item.PackageFamilyName;
        var downloaded = TryReadProperty(status, "PackageBytesDownloaded");
        var total = TryReadProperty(status, "PackageDownloadSizeInBytes");
        if (!string.IsNullOrWhiteSpace(downloaded) && !string.IsNullOrWhiteSpace(total))
        {
            return $"{packageName} ({downloaded}/{total} bytes)";
        }

        return packageName;
    }

    private void ClearQueueSubscriptions()
    {
        foreach (var item in _trackedQueueItems)
        {
            try
            {
                item.StatusChanged -= OnQueueItemStatusChanged;
                item.Completed -= OnQueueItemCompleted;
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        _trackedQueueItems.Clear();
    }

    private void HandleInstallProgress(StorePackageUpdateStatus status)
    {
        try
        {
            var percent = Math.Clamp((int)Math.Round(status.TotalDownloadProgress * 100, MidpointRounding.AwayFromZero), 0, 100);
            if (percent == 0)
            {
                percent = Math.Clamp((int)Math.Round(status.PackageDownloadProgress * 100, MidpointRounding.AwayFromZero), 0, 100);
            }

            var phase = MapStateToPhase(status.PackageUpdateState.ToString());
            var detail = $"{status.PackageFamilyName} ({status.PackageBytesDownloaded}/{status.PackageDownloadSizeInBytes} bytes)";
            SetProgressState(percent, phase, detail, isTerminal: IsTerminalPhase(phase));
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_update_progress_handler_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private (int ProgressPercent, string Phase, string ResultMessage) BuildTerminalProgress(StorePackageUpdateState overallState)
    {
        return overallState switch
        {
            StorePackageUpdateState.Completed => (100, "Completed", "Update completed."),
            StorePackageUpdateState.Canceled => (_state.ProgressPercent, "Canceled", "Update canceled. You can retry."),
            StorePackageUpdateState.ErrorLowBattery => (_state.ProgressPercent, "Failed", "Low battery prevented update. Charge and retry."),
            StorePackageUpdateState.ErrorWiFiRecommended => (_state.ProgressPercent, "Failed", "Wi-Fi is recommended before retrying update."),
            StorePackageUpdateState.ErrorWiFiRequired => (_state.ProgressPercent, "Failed", "Wi-Fi is required before retrying update."),
            StorePackageUpdateState.OtherError => (_state.ProgressPercent, "Failed", "Update failed. Retry later."),
            _ => (_state.ProgressPercent, "Failed", $"Update ended with state: {overallState}. Retry later.")
        };
    }

    private void OnRetryTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _retryTimer.Stop();
        _ = RunCheckAsync();
    }

    private void ScheduleRetry()
    {
        if (_isDisposed || !_isStarted)
        {
            return;
        }

        _retryTimer.Stop();
        _retryTimer.Start();
    }

    private void SetProgressState(int progressPercent, string phase, string detail, bool isTerminal = false)
    {
        var next = _state with
        {
            IsProgressVisible = true,
            ProgressPercent = Math.Clamp(progressPercent, 0, 100),
            ProgressPhase = phase,
            ProgressDetail = detail,
            ProgressResult = isTerminal ? detail : null
        };

        UpdateState(next);
    }

    private StoreUpdateState BuildAvailabilityState(bool isSupported, bool isUpdateAvailable, bool isBusy)
    {
        return _state with
        {
            IsSupported = isSupported,
            IsUpdateAvailable = isUpdateAvailable,
            IsBusy = isBusy
        };
    }

    private void UpdateState(StoreUpdateState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private bool IsCachedUpdatesExpired()
    {
        var utcText = _appSettingsService.Current.StoreUpdateLastAttemptUtc;
        if (string.IsNullOrWhiteSpace(utcText))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(utcText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastAttempt))
        {
            return true;
        }

        return DateTimeOffset.UtcNow - lastAttempt > MinimumCheckInterval;
    }

    private bool TryGetThrottleSkipReason(DateTimeOffset nowUtc, out string reason)
    {
        reason = string.Empty;
        var settings = _appSettingsService.Current;
        if (!string.IsNullOrWhiteSpace(settings.StoreUpdateLastAttemptUtc) &&
            DateTimeOffset.TryParse(settings.StoreUpdateLastAttemptUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastAttemptUtc) &&
            nowUtc - lastAttemptUtc < MinimumCheckInterval)
        {
            reason = StoreThrottleReasonMinInterval;
            return true;
        }

        var recentChecks = ParseAndPruneHistory(nowUtc, persistIfChanged: true);
        if (recentChecks.Count >= MaxChecksPerRollingDay)
        {
            reason = StoreThrottleReasonRolling24h;
            return true;
        }

        return false;
    }

    private void RegisterStoreCheckAttempt(DateTimeOffset nowUtc)
    {
        var settings = _appSettingsService.Current;
        var recentChecks = ParseAndPruneHistory(nowUtc, persistIfChanged: false);
        recentChecks.Add(nowUtc);
        settings.StoreUpdateCheckHistoryUtc = recentChecks
            .Select(x => x.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
            .ToList();
        settings.StoreUpdateLastAttemptUtc = nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        _appSettingsService.Save();
    }

    private List<DateTimeOffset> ParseAndPruneHistory(DateTimeOffset nowUtc, bool persistIfChanged)
    {
        var settings = _appSettingsService.Current;
        var parsed = new List<DateTimeOffset>();
        var hasChanges = false;

        foreach (var entry in settings.StoreUpdateCheckHistoryUtc)
        {
            if (!DateTimeOffset.TryParse(entry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEntry))
            {
                hasChanges = true;
                continue;
            }

            if (nowUtc - parsedEntry > TimeSpan.FromHours(24))
            {
                hasChanges = true;
                continue;
            }

            parsed.Add(parsedEntry.ToUniversalTime());
        }

        if (hasChanges && persistIfChanged)
        {
            settings.StoreUpdateCheckHistoryUtc = parsed
                .Select(x => x.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))
                .ToList();
            _appSettingsService.Save();
        }

        return parsed;
    }

    private Dictionary<string, string?> BuildCheckDiagnostics(string? skipReason, int? resultCount, Exception? exception, string context)
    {
        var packageIdentityPresent = HasPackageIdentity();
        var packageFullName = GetPackageFullName();
        var packageVersion = GetPackageVersion();
        var signatureKind = GetPackageSignatureKindText();
        var recentCheckCount = ParseAndPruneHistory(DateTimeOffset.UtcNow, persistIfChanged: false).Count;

        return new Dictionary<string, string?>
        {
            ["context"] = context,
            ["packageIdentityPresent"] = packageIdentityPresent.ToString(),
            ["packageFullName"] = packageFullName,
            ["installedVersion"] = packageVersion,
            ["packageSignatureKind"] = signatureKind,
            ["checkAttemptUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["checkSkippedByThrottle"] = (!string.IsNullOrWhiteSpace(skipReason)).ToString(),
            ["throttleReason"] = skipReason,
            ["checkCountRolling24h"] = recentCheckCount.ToString(CultureInfo.InvariantCulture),
            ["resultCount"] = resultCount?.ToString(CultureInfo.InvariantCulture),
            ["exceptionType"] = exception?.GetType().FullName,
            ["errorCode"] = exception is null ? null : Marshal.GetHRForException(exception).ToString("X8", CultureInfo.InvariantCulture)
        };
    }

    private static int TryExtractPercent(object status)
    {
        var raw = TryReadDouble(status, "TotalDownloadProgress") ?? TryReadDouble(status, "PackageDownloadProgress");
        if (raw is null)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(raw.Value * 100, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static double? TryReadDouble(object instance, string propertyName)
    {
        try
        {
            var prop = instance.GetType().GetProperty(propertyName);
            if (prop?.GetValue(instance) is double value)
            {
                return value;
            }
        }
        catch
        {
            // Ignore reflection failures.
        }

        return null;
    }

    private static string? TryReadProperty(object instance, string propertyName)
    {
        try
        {
            var prop = instance.GetType().GetProperty(propertyName);
            var value = prop?.GetValue(instance);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string MapStateToPhase(string? stateName)
    {
        return stateName switch
        {
            "Pending" => "Preparing",
            "Downloading" => "Downloading",
            "Deploying" => "Installing",
            "Completed" => "Completed",
            "Canceled" => "Canceled",
            "OtherError" => "Failed",
            "ErrorLowBattery" => "Failed",
            "ErrorWiFiRecommended" => "Failed",
            "ErrorWiFiRequired" => "Failed",
            _ => "Preparing"
        };
    }

    private static bool IsTerminalPhase(string phase)
    {
        return string.Equals(phase, "Completed", StringComparison.Ordinal) ||
               string.Equals(phase, "Canceled", StringComparison.Ordinal) ||
               string.Equals(phase, "Failed", StringComparison.Ordinal);
    }

    private static bool HasPackageIdentity()
    {
        var length = 0u;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    private static string? GetCurrentPackageFamilyName()
    {
        try
        {
            return Package.Current.Id.FamilyName;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetPackageFullName()
    {
        try
        {
            var length = 0u;
            var firstResult = GetCurrentPackageFullName(ref length, null);
            if (firstResult == AppModelErrorNoPackage)
            {
                return null;
            }

            if (firstResult != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var builder = new StringBuilder((int)length);
            var secondResult = GetCurrentPackageFullName(ref length, builder);
            if (secondResult != 0)
            {
                return null;
            }

            return builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetPackageVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return null;
        }
    }

    private static string? GetPackageSignatureKindText()
    {
        try
        {
            return Package.Current.SignatureKind.ToString();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
