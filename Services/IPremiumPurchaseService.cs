using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

public interface IPremiumPurchaseService
{
    Task<PremiumPurchaseResult> PurchasePremiumAsync(
        CancellationToken cancellationToken = default);
}
