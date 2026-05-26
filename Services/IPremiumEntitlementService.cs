using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

public interface IPremiumEntitlementService
{
    Task<PremiumEntitlementState> RefreshEntitlementAsync(
        CancellationToken cancellationToken = default);
}
