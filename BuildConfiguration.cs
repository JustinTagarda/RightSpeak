namespace RightSpeak;

internal static class BuildConfiguration
{
#if DEBUG
    public static bool IsDebugDiagnosticsEnabled => true;
#else
    public static bool IsDebugDiagnosticsEnabled => false;
#endif
}
