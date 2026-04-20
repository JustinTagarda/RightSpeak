using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

internal interface IStoreUpdateClient
{
    bool IsSupported { get; }
    bool CanSilentlyDownloadUpdates { get; }

    Task<IReadOnlyList<StorePackageUpdateInfo>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default);

    Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default);

    Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default);
}
