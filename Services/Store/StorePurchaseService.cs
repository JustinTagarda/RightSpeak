using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace RightSpeak.Services.Store;

public sealed class StorePurchaseService : IStorePurchaseService
{
    private readonly IStoreContextProvider _storeContextProvider;
    private readonly string _premiumStoreId;

    public StorePurchaseService(IStoreContextProvider storeContextProvider, string premiumStoreId)
    {
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
        _premiumStoreId = premiumStoreId ?? throw new ArgumentNullException(nameof(premiumStoreId));
    }

    public async Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default)
    {
        if (!_storeContextProvider.IsStoreSupported)
        {
            return new PremiumPurchaseResult(StorePurchaseOutcome.NotSupported, "Premium purchase unavailable in this build.");
        }

        if (IsRunningElevated())
        {
            return new PremiumPurchaseResult(StorePurchaseOutcome.Blocked, "Microsoft Store purchase is unavailable while running as administrator.");
        }

        var context = _storeContextProvider.TryGetContext();
        if (context is null)
        {
            return new PremiumPurchaseResult(StorePurchaseOutcome.NotSupported, "Premium purchase unavailable in this build.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await context.RequestPurchaseAsync(_premiumStoreId).AsTask(cancellationToken);
            return result.Status switch
            {
                StorePurchaseStatus.Succeeded => new PremiumPurchaseResult(StorePurchaseOutcome.Succeeded, "Premium purchase completed."),
                StorePurchaseStatus.AlreadyPurchased => new PremiumPurchaseResult(StorePurchaseOutcome.AlreadyOwned, "Premium is already owned."),
                StorePurchaseStatus.NotPurchased => new PremiumPurchaseResult(StorePurchaseOutcome.Canceled, "Premium purchase canceled."),
                StorePurchaseStatus.NetworkError => new PremiumPurchaseResult(StorePurchaseOutcome.Failed, "Premium purchase failed due to a network error."),
                StorePurchaseStatus.ServerError => new PremiumPurchaseResult(StorePurchaseOutcome.Failed, "Premium purchase failed due to a Store service error."),
                _ => new PremiumPurchaseResult(StorePurchaseOutcome.Failed, "Premium purchase failed.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new PremiumPurchaseResult(StorePurchaseOutcome.Failed, "Premium purchase failed.");
        }
    }

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
