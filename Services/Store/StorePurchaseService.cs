using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Services;
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
        var isStoreSupported = _storeContextProvider.IsStoreSupported;
        var isElevated = IsRunningElevated();
        AppDiagnostics.Info(
            "premium_purchase_attempt_started",
            new Dictionary<string, string?>
            {
                ["isStoreSupported"] = isStoreSupported.ToString(),
                ["isElevated"] = isElevated.ToString()
            });

        if (!isStoreSupported)
        {
            AppDiagnostics.Warn("premium_purchase_not_supported");
            return new PremiumPurchaseResult(
                StorePurchaseOutcome.NotSupported,
                "Premium purchase is available only in the Microsoft Store version.");
        }

        if (isElevated)
        {
            AppDiagnostics.Warn("premium_purchase_blocked_elevated");
            return new PremiumPurchaseResult(StorePurchaseOutcome.Blocked, "Microsoft Store purchase is unavailable while running as administrator.");
        }

        var context = _storeContextProvider.TryGetContext();
        if (context is null)
        {
            AppDiagnostics.Warn("premium_purchase_context_unavailable");
            return new PremiumPurchaseResult(
                StorePurchaseOutcome.NotSupported,
                "Premium purchase is available only in the Microsoft Store version.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await context.RequestPurchaseAsync(_premiumStoreId).AsTask(cancellationToken);
            var purchaseResult = result.Status switch
            {
                StorePurchaseStatus.Succeeded => new PremiumPurchaseResult(StorePurchaseOutcome.Succeeded, "Premium purchase completed."),
                StorePurchaseStatus.AlreadyPurchased => new PremiumPurchaseResult(StorePurchaseOutcome.AlreadyOwned, "Premium is already owned."),
                StorePurchaseStatus.NotPurchased => new PremiumPurchaseResult(StorePurchaseOutcome.Canceled, "Premium purchase canceled."),
                StorePurchaseStatus.NetworkError => new PremiumPurchaseResult(
                    StorePurchaseOutcome.NetworkError,
                    "Premium purchase failed due to a network error. Check your connection and try again."),
                StorePurchaseStatus.ServerError => new PremiumPurchaseResult(
                    StorePurchaseOutcome.ServerError,
                    "Microsoft Store could not complete the purchase right now. Try again later."),
                _ => new PremiumPurchaseResult(StorePurchaseOutcome.Failed, "Premium purchase failed.")
            };
            AppDiagnostics.Info(
                "premium_purchase_attempt_completed",
                new Dictionary<string, string?>
                {
                    ["storeStatus"] = result.Status.ToString(),
                    ["outcome"] = purchaseResult.Outcome.ToString()
                });
            return purchaseResult;
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Warn("premium_purchase_attempt_canceled");
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "premium_purchase_attempt_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
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
