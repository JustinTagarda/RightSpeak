using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace RightSpeak.Services;

public sealed class StoreUpdateCoordinator : IStoreUpdateCoordinator
{
    private const int AppModelErrorNoPackage = 15700;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    private readonly IStoreContextProvider _storeContextProvider;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly DispatcherTimer _retryTimer;
    private CancellationTokenSource? _checkCancellationTokenSource;
    private IReadOnlyList<StorePackageUpdate>? _cachedUpdates;
    private bool _isStarted;
    private bool _isDisposed;
    private StoreUpdateState _state = new(false, false, false);

    public StoreUpdateCoordinator(IStoreContextProvider storeContextProvider, Dispatcher dispatcher)
    {
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
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

        if (!IsStorePackagedRuntime())
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
            if (updates is null || updates.Count == 0)
            {
                updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken).ConfigureAwait(false);
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
                    ["itemCount"] = updates.Count.ToString()
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
                    ["message"] = ex.Message
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

        if (!IsStorePackagedRuntime())
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
            var updates = await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cts.Token).ConfigureAwait(false);
            _cachedUpdates = updates;
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
                    ["message"] = ex.Message
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

    private static bool IsStorePackagedRuntime()
    {
        if (!HasPackageIdentity())
        {
            return false;
        }

        try
        {
            return Package.Current.SignatureKind == PackageSignatureKind.Store;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasPackageIdentity()
    {
        var length = 0u;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);
}
