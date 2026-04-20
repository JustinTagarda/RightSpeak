using System.Collections.Generic;

namespace RightSpeak.Services;

internal enum StoreUpdateOperationState
{
    Completed,
    Canceled,
    ErrorLowBattery,
    OtherError,
    Unknown
}

internal sealed class StorePackageUpdateInfo
{
    public required string PackageFamilyName { get; init; }
    public required string Version { get; init; }
    public bool IsMandatory { get; init; }
}

internal sealed class StoreUpdateOperationProgress
{
    public string PackageFamilyName { get; init; } = string.Empty;
    public double Progress { get; init; }
}

internal sealed class StoreUpdateOperationResult
{
    public static StoreUpdateOperationResult Completed()
    {
        return new StoreUpdateOperationResult
        {
            OverallState = StoreUpdateOperationState.Completed
        };
    }

    public StoreUpdateOperationState OverallState { get; init; }
    public IReadOnlyList<string> FailedPackageFamilyNames { get; init; } = [];
}
