using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace RightSpeak.Services;

public sealed class PremiumPurchaseService : IPremiumPurchaseService
{
    private readonly IStoreContextProvider _storeContextProvider;
    private readonly IPremiumEntitlementService _premiumEntitlementService;
    private readonly string _premiumAddOnStoreId;

    public PremiumPurchaseService(
        IStoreContextProvider storeContextProvider,
        IPremiumEntitlementService premiumEntitlementService,
        string premiumAddOnStoreId)
    {
        _storeContextProvider = storeContextProvider ?? throw new ArgumentNullException(nameof(storeContextProvider));
        _premiumEntitlementService = premiumEntitlementService ?? throw new ArgumentNullException(nameof(premiumEntitlementService));
        _premiumAddOnStoreId = premiumAddOnStoreId ?? throw new ArgumentNullException(nameof(premiumAddOnStoreId));
    }

    public async Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPackagedProcess())
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseOutcome.NotSupported,
                "Premium purchase is available in the Microsoft Store packaged app.");
        }

        if (IsProcessElevated())
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseOutcome.Blocked,
                "Premium purchase is blocked while running as administrator. Relaunch normally and try again.");
        }

        var storeContext = _storeContextProvider.TryGetDefaultContext();
        if (storeContext is null)
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseOutcome.NotSupported,
                "Store services are unavailable right now.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var purchaseResult = await storeContext.RequestPurchaseAsync(_premiumAddOnStoreId).AsTask(cancellationToken).ConfigureAwait(false);
            var mapped = MapPurchaseStatus(purchaseResult.Status);
            if (mapped is PremiumPurchaseOutcome.Succeeded or PremiumPurchaseOutcome.AlreadyOwned)
            {
                await _premiumEntitlementService.RefreshEntitlementAsync(cancellationToken).ConfigureAwait(false);
            }

            return new PremiumPurchaseResult(mapped, BuildMessage(mapped));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "premium_purchase_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return new PremiumPurchaseResult(
                PremiumPurchaseOutcome.Failed,
                "Premium purchase couldn't be completed. Please try again.");
        }
    }

    private static PremiumPurchaseOutcome MapPurchaseStatus(StorePurchaseStatus status)
    {
        return status switch
        {
            StorePurchaseStatus.Succeeded => PremiumPurchaseOutcome.Succeeded,
            StorePurchaseStatus.AlreadyPurchased => PremiumPurchaseOutcome.AlreadyOwned,
            StorePurchaseStatus.NotPurchased => PremiumPurchaseOutcome.Canceled,
            StorePurchaseStatus.NetworkError => PremiumPurchaseOutcome.NetworkError,
            StorePurchaseStatus.ServerError => PremiumPurchaseOutcome.ServerError,
            _ => PremiumPurchaseOutcome.Failed
        };
    }

    private static string BuildMessage(PremiumPurchaseOutcome outcome)
    {
        return outcome switch
        {
            PremiumPurchaseOutcome.Succeeded => "Premium unlocked successfully.",
            PremiumPurchaseOutcome.AlreadyOwned => "Premium is already owned on this account.",
            PremiumPurchaseOutcome.Canceled => "Purchase was canceled.",
            PremiumPurchaseOutcome.NetworkError => "Network error while contacting Microsoft Store.",
            PremiumPurchaseOutcome.ServerError => "Microsoft Store server error. Please try again.",
            PremiumPurchaseOutcome.Blocked => "Premium purchase is blocked in this execution mode.",
            PremiumPurchaseOutcome.NotSupported => "Premium purchase is not supported in this build.",
            _ => "Premium purchase failed."
        };
    }

    private static bool IsPackagedProcess()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity is null)
            {
                return false;
            }

            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
