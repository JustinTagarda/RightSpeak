using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services.Store;

public enum StorePurchaseOutcome
{
    Succeeded,
    AlreadyOwned,
    Canceled,
    NetworkError,
    ServerError,
    NotSupported,
    Blocked,
    Failed
}

public sealed record PremiumPurchaseResult(StorePurchaseOutcome Outcome, string Message);

public interface IStorePurchaseService
{
    Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default);
}
