using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

internal interface IDeferredUpdateHistoryStore
{
    DeferredUpdateState? TryLoad();

    Task<bool> SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(CancellationToken cancellationToken = default);
}
