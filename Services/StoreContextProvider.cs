using System;
using Windows.Services.Store;

namespace RightSpeak.Services;

public sealed class StoreContextProvider : IStoreContextProvider
{
    private StoreContext? _storeContext;
    private nint _windowHandle;

    public StoreContext? TryGetDefaultContext()
    {
        try
        {
            _storeContext ??= StoreContext.GetDefault();
            if (_storeContext is not null && _windowHandle != nint.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(_storeContext, _windowHandle);
            }

            return _storeContext;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_context_unavailable",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return null;
        }
    }

    public void SetOwnerWindowHandle(nint windowHandle)
    {
        _windowHandle = windowHandle;
        if (_storeContext is null || _windowHandle == nint.Zero)
        {
            return;
        }

        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(_storeContext, _windowHandle);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "store_context_owner_window_init_failed",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }
}
