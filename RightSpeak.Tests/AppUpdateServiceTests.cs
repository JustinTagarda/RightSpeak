using RightSpeak.Models;
using RightSpeak.Services;
using Xunit;

namespace RightSpeak.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task Unsupported_store_client_keeps_update_snapshot_idle()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var service = new StoreAppUpdateService(
            new UnsupportedStoreUpdateClient(),
            new PackageVersionProvider(isPackaged: false, installedVersion: "0.0.0"),
            isAppBusy: () => false,
            installIdleDelay: TimeSpan.FromMilliseconds(5),
            installRetryDelay: TimeSpan.FromMilliseconds(5),
            downloadRetryDelay: TimeSpan.FromMilliseconds(5));

        await service.StartAsync(cancellationTokenSource.Token);

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.False(service.CurrentSnapshot.IsProgressVisible);
        Assert.False(service.CurrentSnapshot.IsMandatoryUpdateAvailable);
    }

    [Fact]
    public async Task Mandatory_update_without_silent_download_is_deferred_without_blocking()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = false,
            Updates =
            [
                new StorePackageUpdateInfo
                {
                    PackageFamilyName = "RightSpeak",
                    Version = "2.0.0.0",
                    IsMandatory = true
                }
            ]
        };
        var service = new StoreAppUpdateService(
            client,
            new PackageVersionProvider(isPackaged: true, installedVersion: "1.0.0.0"),
            isAppBusy: () => false,
            installIdleDelay: TimeSpan.FromMilliseconds(5),
            installRetryDelay: TimeSpan.FromMilliseconds(5),
            downloadRetryDelay: TimeSpan.FromMilliseconds(50));

        await service.StartAsync(cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();

        Assert.Equal(AppUpdateState.Deferred, service.CurrentSnapshot.State);
        Assert.True(service.CurrentSnapshot.IsMandatoryUpdateAvailable);
        Assert.Contains("retry", service.CurrentSnapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.DownloadCalls);
        Assert.Equal(0, client.InstallCalls);
    }

    [Fact]
    public async Task Downloaded_update_waits_for_idle_before_installing()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var isBusy = true;
        var client = new FakeStoreUpdateClient
        {
            CanSilentlyDownloadUpdates = true,
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
            isAppBusy: () => isBusy,
            installIdleDelay: TimeSpan.FromMilliseconds(25),
            installRetryDelay: TimeSpan.FromMilliseconds(25),
            downloadRetryDelay: TimeSpan.FromMilliseconds(25));

        await service.StartAsync(cancellationTokenSource.Token);
        await Task.Delay(80, cancellationTokenSource.Token);

        Assert.Equal(1, client.DownloadCalls);
        Assert.Equal(0, client.InstallCalls);
        Assert.Equal(AppUpdateState.Deferred, service.CurrentSnapshot.State);

        isBusy = false;
        await WaitForAsync(() => service.CurrentSnapshot.State == AppUpdateState.Completed, cancellationTokenSource.Token);

        Assert.Equal(1, client.InstallCalls);
        Assert.Equal(AppUpdateState.Completed, service.CurrentSnapshot.State);
    }

    [Fact]
    public void Package_version_provider_formats_display_text_from_installed_package_version()
    {
        var provider = new PackageVersionProvider(isPackaged: true, installedVersion: "3.4.5.6");

        Assert.True(provider.IsPackaged);
        Assert.Equal("3.4.5.6", provider.InstalledVersion);
        Assert.Equal("Version: 3.4.5.6", provider.GetDisplayVersionText());
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class FakeStoreUpdateClient : IStoreUpdateClient
    {
        public bool IsSupported { get; init; } = true;

        public bool CanSilentlyDownloadUpdates { get; init; }

        public IReadOnlyList<StorePackageUpdateInfo> Updates { get; init; } = [];

        public StoreUpdateOperationResult DownloadResult { get; init; } = StoreUpdateOperationResult.Completed();

        public StoreUpdateOperationResult InstallResult { get; init; } = StoreUpdateOperationResult.Completed();

        public int DownloadCalls { get; private set; }

        public int InstallCalls { get; private set; }

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
            DownloadCalls++;
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 0.35d
            });
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 1d
            });
            return Task.FromResult(DownloadResult);
        }

        public Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
            Action<StoreUpdateOperationProgress>? onProgress,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            InstallCalls++;
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 0.55d
            });
            onProgress?.Invoke(new StoreUpdateOperationProgress
            {
                PackageFamilyName = "RightSpeak",
                Progress = 1d
            });
            return Task.FromResult(InstallResult);
        }
    }
}
