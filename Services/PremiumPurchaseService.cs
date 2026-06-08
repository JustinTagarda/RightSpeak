using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
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

        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher is not null && !app.Dispatcher.CheckAccess())
            {
                var dispatched = await app.Dispatcher.InvokeAsync(
                    () => PurchaseCoreOnUiThreadAsync(cancellationToken)).Task.ConfigureAwait(false);
                return await dispatched.ConfigureAwait(false);
            }

            return await PurchaseCoreOnUiThreadAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<PremiumPurchaseResult> PurchaseCoreOnUiThreadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ownerHandle = ResolveBestOwnerWindowHandle();
        if (ownerHandle != nint.Zero)
        {
            _storeContextProvider.SetOwnerWindowHandle(ownerHandle);
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
            var purchaseResult = await storeContext.RequestPurchaseAsync(_premiumAddOnStoreId).AsTask(cancellationToken).ConfigureAwait(false);
            return await FinalizePurchaseAsync(purchaseResult, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCancellationException(ex))
        {
            AppDiagnostics.Warn(
                "premium_purchase_owner_window_retry",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });

            var retryOwnerHandle = ResolveBestOwnerWindowHandle();
            if (retryOwnerHandle != nint.Zero)
            {
                _storeContextProvider.SetOwnerWindowHandle(retryOwnerHandle);
            }

            storeContext = _storeContextProvider.TryGetDefaultContext();
            if (storeContext is null)
            {
                return new PremiumPurchaseResult(
                    PremiumPurchaseOutcome.NotSupported,
                    "Store services are unavailable right now.");
            }

            var retryPurchaseResult = await storeContext.RequestPurchaseAsync(_premiumAddOnStoreId).AsTask(cancellationToken).ConfigureAwait(false);
            return await FinalizePurchaseAsync(retryPurchaseResult, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PremiumPurchaseResult> FinalizePurchaseAsync(StorePurchaseResult purchaseResult, CancellationToken cancellationToken)
    {
        var mapped = MapPurchaseStatus(purchaseResult.Status);
        if (mapped is PremiumPurchaseOutcome.Succeeded or PremiumPurchaseOutcome.AlreadyOwned)
        {
            await _premiumEntitlementService.RefreshEntitlementAsync(cancellationToken).ConfigureAwait(false);
        }

        return new PremiumPurchaseResult(mapped, BuildMessage(mapped));
    }

    private static nint ResolveBestOwnerWindowHandle()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return nint.Zero;
        }

        var window = app.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(candidate => candidate.IsActive)
            ?? app.Windows.OfType<System.Windows.Window>().FirstOrDefault(candidate => candidate.IsVisible)
            ?? app.MainWindow;

        if (window is null)
        {
            return nint.Zero;
        }

        try
        {
            return new WindowInteropHelper(window).Handle;
        }
        catch
        {
            return nint.Zero;
        }
    }

    private static bool IsCancellationException(Exception ex)
    {
        return ex is OperationCanceledException || ex.InnerException is OperationCanceledException;
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

}
