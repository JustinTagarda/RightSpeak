namespace RightSpeak.Services;

public sealed record PremiumPurchaseResult(
    PremiumPurchaseOutcome Outcome,
    string Message);
