using System;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class NoOpAppUpdateService : IAppUpdateService
{
    public NoOpAppUpdateService(IAppVersionProvider versionProvider)
    {
        if (versionProvider is null)
        {
            throw new ArgumentNullException(nameof(versionProvider));
        }

        CurrentSnapshot = AppUpdateSnapshot.Idle(versionProvider.InstalledVersion);
    }

    public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    public AppUpdateSnapshot CurrentSnapshot { get; }

    public bool HasDeferredInstallPending => false;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SnapshotChanged?.Invoke(this, CurrentSnapshot);
        return Task.CompletedTask;
    }

    public Task<UserInitiatedUpdateResult> CheckForUpdatesOnDemandAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new UserInitiatedUpdateResult(
            UserInitiatedUpdateAvailability.Unavailable,
            "Automatic app updates are not configured in this build."));
    }

    public Task<DeferredInstallAttemptResult> TryApplyDeferredInstallOnExitAsync(
        IProgress<AppUpdateSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DeferredInstallAttemptResult(false, true, false, string.Empty));
    }
}
