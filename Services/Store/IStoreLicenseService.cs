using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services.Store;

public interface IStoreLicenseService
{
    Task<PremiumEntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}

