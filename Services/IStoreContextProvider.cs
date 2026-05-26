using Windows.Services.Store;

namespace RightSpeak.Services;

public interface IStoreContextProvider
{
    StoreContext? TryGetDefaultContext();
    void SetOwnerWindowHandle(nint windowHandle);
}
