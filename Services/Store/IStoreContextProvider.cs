using Windows.Services.Store;

namespace RightSpeak.Services.Store;

public interface IStoreContextProvider
{
    bool IsStoreSupported { get; }
    StoreContext? TryGetContext();
}

