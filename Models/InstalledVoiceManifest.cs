using System;
using System.Collections.Generic;

namespace RightSpeak.Models;

public sealed class InstalledVoiceManifest
{
    public string ManifestVersion { get; set; } = "1";
    public string? PiperRuntimeVersion { get; set; }
    public List<InstalledVoiceRecord> Voices { get; set; } = new();
}

public sealed class InstalledVoiceRecord
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ModelFileName { get; set; } = string.Empty;
    public string ConfigFileName { get; set; } = string.Empty;
    public string ModelSha256 { get; set; } = string.Empty;
    public string ConfigSha256 { get; set; } = string.Empty;
    public string ModelSourceUrl { get; set; } = string.Empty;
    public string ConfigSourceUrl { get; set; } = string.Empty;
    public DateTime InstalledAtUtc { get; set; }
}
