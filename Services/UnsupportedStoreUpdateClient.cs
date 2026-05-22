using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace RightSpeak.Services;

internal sealed class UnsupportedStoreUpdateClient : IStoreUpdateClient
{
    public bool IsSupported => false;

    public bool CanSilentlyDownloadUpdates => false;

    public Task<IReadOnlyList<StorePackageUpdateInfo>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<StorePackageUpdateInfo>>([]);
    }

    public Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = onProgress;
        _ = cancellationToken;
        return Task.FromResult(StoreUpdateOperationResult.Completed());
    }

    public Task<StoreUpdateOperationResult> RequestDownloadAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = onProgress;
        _ = cancellationToken;
        return Task.FromResult(StoreUpdateOperationResult.Completed());
    }

    public Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = onProgress;
        _ = cancellationToken;
        return Task.FromResult(StoreUpdateOperationResult.Completed());
    }

    public Task<StoreUpdateOperationResult> RequestDownloadAndInstallAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = onProgress;
        _ = cancellationToken;
        return Task.FromResult(new StoreUpdateOperationResult
        {
            OverallState = StoreUpdateOperationState.OtherError
        });
    }

    public Task<IReadOnlyList<StoreQueueItem>> GetAssociatedStoreQueueItemsAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<StoreQueueItem>>([]);
    }
}
