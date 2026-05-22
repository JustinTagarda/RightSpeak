using System;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IAppUpdateService
{
    event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    AppUpdateSnapshot CurrentSnapshot { get; }

    bool HasDeferredInstallPending { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<UserInitiatedUpdateResult> CheckForUpdatesOnDemandAsync(CancellationToken cancellationToken = default);

    Task<DeferredInstallAttemptResult> TryApplyDeferredInstallOnExitAsync(
        IProgress<AppUpdateSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}
