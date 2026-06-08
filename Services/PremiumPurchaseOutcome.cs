namespace RightSpeak.Services;

public enum PremiumPurchaseOutcome
{
    Succeeded,
    AlreadyOwned,
    Canceled,
    Failed,
    NetworkError,
    ServerError,
    NotSupported
}
