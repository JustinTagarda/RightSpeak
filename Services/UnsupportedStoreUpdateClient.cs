using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

    public Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = onProgress;
        _ = cancellationToken;
        return Task.FromResult(StoreUpdateOperationResult.Completed());
    }
}
