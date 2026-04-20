using System.Collections.Generic;

namespace RightSpeak.Services;

public sealed class PiperCatalogOptions
{
    public int CatalogVersion { get; set; }
    public string UpstreamVoicesUrl { get; set; } = string.Empty;
    public string VoiceBaseUrl { get; set; } = string.Empty;
    public string HuggingFaceTreeApiBaseUrl { get; set; } = "https://huggingface.co/api/models/rhasspy/piper-voices/tree/";
    public int CacheTtlHours { get; set; } = 24;
    public string VoiceDenylistPath { get; set; } = "Resources/Piper/VoiceDenylist.json";
    public List<string> ExcludedQualities { get; set; } = new();
    public List<string> ApprovedLicenses { get; set; } = new();
    public PiperRuntimeOptions Runtime { get; set; } = new();
}

public sealed class PiperRuntimeOptions
{
    public string Version { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
