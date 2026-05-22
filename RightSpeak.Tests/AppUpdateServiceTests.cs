using RightSpeak.Models;
using RightSpeak.Services;
using Windows.Services.Store;
using Xunit;

namespace RightSpeak.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task Startup_without_store_support_skips_update_flow()
    {
        var pendingStore = new InMemoryDeferredUpdateStateStore();
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var service = new StoreAppUpdateService(
            new UnsupportedStoreUpdateClient(),
            new PackageVersionProvider(isPackaged: false, installedVersion: "0.0.0.0"),
            pendingStore,
            historyStore);

        await service.StartAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.False(service.HasDeferredInstallPending);
        Assert.Null(pendingStore.CurrentState);
        Assert.Null(historyStore.CurrentState);
    }

    [Fact]
    public async Task Store_client_and_state_are_initialized_lazily()
    {
        var pendingStore = new CountingDeferredUpdateStateStore();
        var historyStore = new CountingDeferredUpdateHistoryStore();
        var factoryCalls = 0;
        var service = new StoreAppUpdateService(
            () =>
            {
                factoryCalls++;
                return new FakeStoreUpdateClient
                {
                    CanSilentlyDownloadUpdates = false,
                    Updates = []
                };
            },
            new PackageVersionProvider(isPackaged: true, installedVersion: "1.0.0.0"),
            pendingStore,
            historyStore);

        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, pendingStore.LoadCalls);
        Assert.Equal(0, historyStore.LoadCalls);

        await service.StartAsync();

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, pendingStore.LoadCalls);
        Assert.Equal(1, historyStore.LoadCalls);
        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task Startup_with_silent_download_persists_deferred_install_state()
    {
        var pendingStore = new InMemoryDeferredUpdateStateStore();
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = true,
            Updates =
            [
                new StorePackageUpdateInfo
                {
                    PackageFamilyName = "RightSpeak",
                    Version = "2.0.0.0",
                    IsMandatory = true
                }
            ],
            SilentDownloadResult = StoreUpdateOperationResult.Completed()
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "1.0.0.0"),
            pendingStore,
            historyStore);

        await service.StartAsync();

        Assert.Equal(1, client.SilentDownloadCalls);
        Assert.True(service.HasDeferredInstallPending);
        Assert.NotNull(pendingStore.CurrentState);
        Assert.True(pendingStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(pendingStore.CurrentState.LastCheckUtc);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.Equal(AppUpdateState.Deferred, service.CurrentSnapshot.State);
        Assert.Contains("install when you close the app", service.CurrentSnapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Startup_with_no_updates_records_last_check_state()
    {
        var pendingStore = new InMemoryDeferredUpdateStateStore();
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = true,
            Updates = []
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "1.0.0.0"),
            pendingStore,
            historyStore);

        await service.StartAsync();

        Assert.Null(pendingStore.CurrentState);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal(0, client.RequestInstallCalls);
    }

    [Fact]
    public async Task Manual_check_reports_available_without_queueing_install()
    {
        var pendingStore = new InMemoryDeferredUpdateStateStore();
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = false,
            Updates =
            [
                new StorePackageUpdateInfo
                {
                    PackageFamilyName = "RightSpeak",
                    Version = "2.1.0.0",
                    IsMandatory = false
                }
            ]
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "2.0.0.0"),
            pendingStore,
            historyStore);

        var result = await service.CheckForUpdatesOnDemandAsync();

        Assert.Equal(UserInitiatedUpdateAvailability.Available, result.Availability);
        Assert.Equal(0, client.SilentDownloadCalls);
        Assert.Equal(0, client.RequestInstallCalls);
        Assert.False(service.HasDeferredInstallPending);
        Assert.Null(pendingStore.CurrentState);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task Startup_with_silent_download_cancelled_uses_store_fallback_ui()
    {
        var pendingStore = new InMemoryDeferredUpdateStateStore();
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = true,
            Updates =
            [
                new StorePackageUpdateInfo
                {
                    PackageFamilyName = "RightSpeak",
                    Version = "2.2.0.0",
                    IsMandatory = true
                }
            ],
            SilentDownloadResult = new StoreUpdateOperationResult
            {
                OverallState = StoreUpdateOperationState.Canceled
            },
            RequestInstallResult = StoreUpdateOperationResult.Completed()
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "2.0.0.0"),
            pendingStore,
            historyStore);

        await service.StartAsync();

        Assert.Equal(1, client.SilentDownloadCalls);
        Assert.Equal(1, client.RequestInstallCalls);
        Assert.Null(pendingStore.CurrentState);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.Equal(AppUpdateState.Deferred, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task Startup_with_silent_download_unavailable_uses_store_fallback_ui_without_deferred_state()
    {
        var pendingStore = new InMemoryDeferredUpdateStateStore();
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = false,
            Updates =
            [
                new StorePackageUpdateInfo
                {
                    PackageFamilyName = "RightSpeak",
                    Version = "2.3.0.0",
                    IsMandatory = false
                }
            ],
            RequestInstallResult = StoreUpdateOperationResult.Completed()
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "2.0.0.0"),
            pendingStore,
            historyStore);

        await service.StartAsync();

        Assert.Equal(0, client.SilentDownloadCalls);
        Assert.Equal(1, client.RequestInstallCalls);
        Assert.False(service.HasDeferredInstallPending);
        Assert.Null(pendingStore.CurrentState);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.Equal(AppUpdateState.Completed, service.CurrentSnapshot.State);
        Assert.Contains("next time the app opens", service.CurrentSnapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deferred_exit_install_clears_state_after_success()
    {
        var pendingState = DeferredUpdateState.CreatePending(
            "RightSpeak:2.1.0.0:False",
            "2.0.0.0",
            "2.1.0.0",
            DateTimeOffset.UtcNow);
        var pendingStore = new InMemoryDeferredUpdateStateStore(pendingState);
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            IsSupported = true,
            CanSilentlyDownloadUpdates = true,
            SilentInstallResult = StoreUpdateOperationResult.Completed()
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "2.0.0.0"),
            pendingStore,
            historyStore);

        var result = await service.TryApplyDeferredInstallOnExitAsync();

        Assert.True(result.Attempted);
        Assert.True(result.Succeeded);
        Assert.Equal(1, client.SilentInstallCalls);
        Assert.Null(pendingStore.CurrentState);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.False(historyStore.CurrentState.LastInstallAttemptFailed);
        Assert.False(service.HasDeferredInstallPending);
        Assert.Equal(AppUpdateState.Completed, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task Deferred_exit_install_failure_records_retry_metadata()
    {
        var pendingState = DeferredUpdateState.CreatePending(
            "RightSpeak:2.1.0.0:False",
            "2.0.0.0",
            "2.1.0.0",
            DateTimeOffset.UtcNow);
        var pendingStore = new InMemoryDeferredUpdateStateStore(pendingState);
        var historyStore = new InMemoryDeferredUpdateHistoryStore();
        var client = new FakeStoreUpdateClient
        {
            IsSupported = true,
            CanSilentlyDownloadUpdates = true,
            SilentInstallResult = new StoreUpdateOperationResult
            {
                OverallState = StoreUpdateOperationState.OtherError
            }
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "2.0.0.0"),
            pendingStore,
            historyStore);

        var result = await service.TryApplyDeferredInstallOnExitAsync();

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal(1, client.SilentInstallCalls);
        Assert.Null(pendingStore.CurrentState);
        Assert.NotNull(historyStore.CurrentState);
        Assert.False(historyStore.CurrentState!.HasPendingInstall);
        Assert.True(historyStore.CurrentState.LastInstallAttemptFailed);
        Assert.True(historyStore.CurrentState.RetryNotBeforeUtc is not null);
        Assert.NotNull(historyStore.CurrentState.LastCheckUtc);
        Assert.Equal(AppUpdateState.Failed, service.CurrentSnapshot.State);
    }

    [Fact]
    public void Package_version_provider_formats_display_text_from_installed_package_version()
    {
        var provider = new PackageVersionProvider(isPackaged: true, installedVersion: "3.4.5.6");

        Assert.True(provider.IsPackaged);
        Assert.Equal("3.4.5.6", provider.InstalledVersion);
        Assert.Equal("v3.4.5.6", provider.GetDisplayVersionText());
    }

    private sealed class InMemoryDeferredUpdateStateStore : IDeferredUpdateStateStore
    {
        public InMemoryDeferredUpdateStateStore(DeferredUpdateState? currentState = null)
        {
            CurrentState = currentState;
        }

        public DeferredUpdateState? CurrentState { get; private set; }

        public DeferredUpdateState? TryLoad()
        {
            return CurrentState;
        }

        public Task<bool> SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            CurrentState = state;
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            CurrentState = null;
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryDeferredUpdateHistoryStore : IDeferredUpdateHistoryStore
    {
        public InMemoryDeferredUpdateHistoryStore(DeferredUpdateState? currentState = null)
        {
            CurrentState = currentState;
        }

        public DeferredUpdateState? CurrentState { get; private set; }

        public DeferredUpdateState? TryLoad()
        {
            return CurrentState;
        }

        public Task<bool> SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            CurrentState = state;
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            CurrentState = null;
            return Task.FromResult(true);
        }
    }

    private sealed class CountingDeferredUpdateStateStore : IDeferredUpdateStateStore
    {
        public int LoadCalls { get; private set; }

        public DeferredUpdateState? TryLoad()
        {
            LoadCalls++;
            return null;
        }

        public Task<bool> SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
        {
            _ = state;
            _ = cancellationToken;
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(true);
        }
    }

    private sealed class CountingDeferredUpdateHistoryStore : IDeferredUpdateHistoryStore
    {
        public int LoadCalls { get; private set; }

        public DeferredUpdateState? TryLoad()
        {
            LoadCalls++;
            return null;
        }

        public Task<bool> SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
        {
            _ = state;
            _ = cancellationToken;
            return Task.FromResult(true);
        }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeStoreUpdateClient : IStoreUpdateClient
    {
        public bool IsSupported { get; init; } = true;

        public bool CanSilentlyDownloadUpdates { get; init; }

        public IReadOnlyList<StorePackageUpdateInfo> Updates { get; init; } = [];

        public StoreUpdateOperationResult SilentDownloadResult { get; init; } = StoreUpdateOperationResult.Completed();

        public StoreUpdateOperationResult RequestDownloadResult { get; init; } = StoreUpdateOperationResult.Completed();

        public StoreUpdateOperationResult RequestInstallResult { get; init; } = StoreUpdateOperationResult.Completed();

        public StoreUpdateOperationResult SilentInstallResult { get; init; } = StoreUpdateOperationResult.Completed();

        public int SilentDownloadCalls { get; private set; }

        public int RequestDownloadCalls { get; private set; }

        public int RequestInstallCalls { get; private set; }

        public int SilentInstallCalls { get; private set; }

        public Task<IReadOnlyList<StorePackageUpdateInfo>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(Updates);
        }

        public Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
            Action<StoreUpdateOperationProgress>? onProgress,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            SilentDownloadCalls++;
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 1d
            });
            return Task.FromResult(SilentDownloadResult);
        }

        public Task<StoreUpdateOperationResult> RequestDownloadAsync(
            Action<StoreUpdateOperationProgress>? onProgress,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RequestDownloadCalls++;
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 1d
            });
            return Task.FromResult(RequestDownloadResult);
        }

        public Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
            Action<StoreUpdateOperationProgress>? onProgress,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            SilentInstallCalls++;
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 1d
            });
            return Task.FromResult(SilentInstallResult);
        }

        public Task<StoreUpdateOperationResult> RequestDownloadAndInstallAsync(
            Action<StoreUpdateOperationProgress>? onProgress,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RequestInstallCalls++;
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 1d
            });
            return Task.FromResult(RequestInstallResult);
        }

        public Task<IReadOnlyList<StoreQueueItem>> GetAssociatedStoreQueueItemsAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<StoreQueueItem>>([]);
        }
    }
}
