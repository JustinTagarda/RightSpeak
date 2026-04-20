namespace RightSpeak.Models;

public sealed class DownloadableVoice
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Locale { get; init; }
    public required string Quality { get; init; }
    public string? LicenseId { get; init; }
    public string? ExclusionReason { get; init; }
    public string Engine { get; init; } = "Piper";
    public VoiceInstallState Status { get; init; }
    public string? InstalledVersion { get; init; }
    public required string AvailableVersion { get; init; }
    public long ModelSizeBytes { get; init; }
    public long ConfigSizeBytes { get; init; }
    public long TotalSizeBytes => ModelSizeBytes + ConfigSizeBytes;
    public required string ModelSha256 { get; init; }
    public required string ConfigSha256 { get; init; }
    public required string ModelUrl { get; init; }
    public required string ConfigUrl { get; init; }
    public required string ModelFileName { get; init; }
    public required string ConfigFileName { get; init; }
}
