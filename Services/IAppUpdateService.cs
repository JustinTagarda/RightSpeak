using System;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IAppUpdateService
{
    event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    AppUpdateSnapshot CurrentSnapshot { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}
