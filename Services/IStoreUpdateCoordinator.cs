using System;
using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

public interface IStoreUpdateCoordinator : IDisposable
{
    event EventHandler<StoreUpdateState>? StateChanged;
    StoreUpdateState CurrentState { get; }
    void Start();
    Task RequestInstallAsync(CancellationToken cancellationToken = default);
    void Stop();
}
