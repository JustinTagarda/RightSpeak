using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RightSpeak.Models;
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
            UpdateState(new StoreUpdateState(false, false, false));
            return;
        }

        var context = _storeContextProvider.TryGetDefaultContext();
        if (context is null)
        {
            UpdateState(new StoreUpdateState(false, false, false));
            return;
        }

        try
        {
            UpdateState(_state with { IsBusy = true });
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
                UpdateState(new StoreUpdateState(true, false, false));
                ScheduleRetry();
                return;
            }

            var result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates).AsTask(cancellationToken).ConfigureAwait(false);
            AppDiagnostics.Info(
                "store_update_install_requested",
                new Dictionary<string, string?>
                {
                    ["overallState"] = result.OverallState.ToString(),
                    ["itemCount"] = updates.Count.ToString(),
                    ["context"] = "request_install"
                });

            _cachedUpdates = null;
            await RunCheckAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // App shutdown or user cancellation.
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
            UpdateState(new StoreUpdateState(true, false, false));
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
            UpdateState(new StoreUpdateState(false, false, false));
            return;
        }

        var context = _storeContextProvider.TryGetDefaultContext();
        if (context is null)
        {
            _cachedUpdates = null;
            _retryTimer.Stop();
            UpdateState(new StoreUpdateState(false, false, false));
            return;
        }

        if (TryGetThrottleSkipReason(DateTimeOffset.UtcNow, out var skipReason))
        {
            var lastKnownAvailable = _appSettingsService.Current.StoreUpdateLastKnownAvailable;
            AppDiagnostics.Info(
                "store_update_check_skipped_throttled",
                BuildCheckDiagnostics(skipReason, null, null, context: "startup"));
            UpdateState(new StoreUpdateState(true, lastKnownAvailable, false));
            if (!lastKnownAvailable)
            {
                ScheduleRetry();
            }

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
                UpdateState(new StoreUpdateState(true, true, false));
                return;
            }

            UpdateState(new StoreUpdateState(true, false, false));
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
            UpdateState(new StoreUpdateState(true, false, false));
        }
    }

    private void OnRetryTimerTick(object? sender, EventArgs e)
    {
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

    private static bool HasPackageIdentity()
    {
        var length = 0u;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
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
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);
}
