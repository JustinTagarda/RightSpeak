using System;
using System.Collections.Generic;
using Windows.Services.Store;

namespace RightSpeak.Services.Store;

public sealed class StoreContextProvider : IStoreContextProvider
{
    private readonly IAppVersionProvider _appVersionProvider;
    private readonly Func<IntPtr>? _ownerWindowHandleProvider;

    public StoreContextProvider(
        IAppVersionProvider appVersionProvider,
        Func<IntPtr>? ownerWindowHandleProvider = null)
    {
        _appVersionProvider = appVersionProvider ?? throw new ArgumentNullException(nameof(appVersionProvider));
        _ownerWindowHandleProvider = ownerWindowHandleProvider;
    }

    public bool IsStoreSupported => _appVersionProvider.IsPackaged;

    public StoreContext? TryGetContext()
    {
        if (!IsStoreSupported)
        {
            return null;
        }

        var context = StoreContext.GetDefault();
        var ownerWindowHandle = _ownerWindowHandleProvider?.Invoke() ?? IntPtr.Zero;
        if (ownerWindowHandle == IntPtr.Zero)
        {
            return context;
        }

        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindowHandle);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_context_owner_window_initialize_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }

        return context;
    }
}

