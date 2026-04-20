using System;
using System.Collections.Generic;

namespace RightSpeak.Services;

internal sealed class VoiceCatalogCache
{
    public int CatalogVersion { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public string AvailableVersion { get; set; } = string.Empty;
    public List<CachedVoiceEntry> Voices { get; set; } = new();
}

internal sealed class CachedVoiceEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Locale { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string LicenseId { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
    public long ModelSizeBytes { get; set; }
    public long ConfigSizeBytes { get; set; }
    public string ModelSha256 { get; set; } = string.Empty;
    public string ConfigSha256 { get; set; } = string.Empty;
    public string ModelUrl { get; set; } = string.Empty;
    public string ConfigUrl { get; set; } = string.Empty;
    public string ModelFileName { get; set; } = string.Empty;
    public string ConfigFileName { get; set; } = string.Empty;
}

internal sealed class VoiceDenylistOptions
{
    public Dictionary<string, string> DeniedVoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
