using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace RightSpeak.Services;

internal sealed class StoreContextUpdateClient : IStoreUpdateClient
{
    private readonly StoreContext _context;
    private IReadOnlyList<StorePackageUpdate> _cachedUpdates = [];

    public StoreContextUpdateClient(StoreContext context, bool isSupported)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        IsSupported = isSupported;
    }

    public bool IsSupported { get; }

    public bool CanSilentlyDownloadUpdates => IsSupported && _context.CanSilentlyDownloadStorePackageUpdates;

    public async Task<IReadOnlyList<StorePackageUpdateInfo>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!IsSupported)
        {
            _cachedUpdates = [];
            return [];
        }

        var updates = await _context.GetAppAndOptionalStorePackageUpdatesAsync();
        _cachedUpdates = updates.ToList();
        return _cachedUpdates
            .Select(update => new StorePackageUpdateInfo
            {
                PackageFamilyName = update.Package.Id.FamilyName,
                Version = FormatVersion(update.Package.Id.Version),
                IsMandatory = update.Mandatory
            })
            .ToList();
    }

    public async Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (_cachedUpdates.Count == 0)
        {
            return StoreUpdateOperationResult.Completed();
        }

        var operation = _context.TrySilentDownloadStorePackageUpdatesAsync(_cachedUpdates);
        operation.Progress = (_, progress) => onProgress?.Invoke(new StoreUpdateOperationProgress
        {
            PackageFamilyName = progress.PackageFamilyName,
            Progress = ClampProgress(progress.PackageDownloadProgress)
        });

        var result = await operation;
        return MapResult(result);
    }

    public async Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
        Action<StoreUpdateOperationProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (_cachedUpdates.Count == 0)
        {
            return StoreUpdateOperationResult.Completed();
        }

        var operation = _context.TrySilentDownloadAndInstallStorePackageUpdatesAsync(_cachedUpdates);
        operation.Progress = (_, progress) => onProgress?.Invoke(new StoreUpdateOperationProgress
        {
            PackageFamilyName = progress.PackageFamilyName,
            Progress = ClampProgress(progress.PackageDownloadProgress)
        });

        var result = await operation;
        return MapResult(result);
    }

    private static StoreUpdateOperationResult MapResult(StorePackageUpdateResult result)
    {
        return new StoreUpdateOperationResult
        {
            OverallState = result.OverallState switch
            {
                StorePackageUpdateState.Completed => StoreUpdateOperationState.Completed,
                StorePackageUpdateState.Canceled => StoreUpdateOperationState.Canceled,
                StorePackageUpdateState.ErrorLowBattery => StoreUpdateOperationState.ErrorLowBattery,
                StorePackageUpdateState.OtherError => StoreUpdateOperationState.OtherError,
                _ => StoreUpdateOperationState.Unknown
            },
            FailedPackageFamilyNames = result.StorePackageUpdateStatuses
                .Where(status => status.PackageUpdateState != StorePackageUpdateState.Completed)
                .Select(status => status.PackageFamilyName)
                .ToList()
        };
    }

    private static double ClampProgress(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return value < 0d ? 0d : value > 1d ? 1d : value;
    }

    private static string FormatVersion(PackageVersion version)
    {
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }
}
