using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
    public Dictionary<string, PiperRuntimeOptions> RuntimeByArchitecture { get; set; } = new();

    public bool TryResolveRuntimeOptions(
        Architecture architecture,
        out string runtimeMoniker,
        out PiperRuntimeOptions? runtimeOptions)
    {
        runtimeMoniker = PiperRuntimeEnvironment.GetRuntimeMoniker(architecture);
        runtimeOptions = null;

        if (!string.IsNullOrWhiteSpace(runtimeMoniker) &&
            RuntimeByArchitecture is not null &&
            RuntimeByArchitecture.Count > 0)
        {
            foreach (var pair in RuntimeByArchitecture)
            {
                if (!string.Equals(pair.Key, runtimeMoniker, StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.IsEmpty())
                {
                    continue;
                }

                runtimeOptions = pair.Value;
                return true;
            }
        }

        if (architecture == Architecture.X64 && !Runtime.IsEmpty())
        {
            runtimeOptions = Runtime;
            return true;
        }

        return false;
    }

    public string? ResolveRuntimeVersion(Architecture architecture)
    {
        return TryResolveRuntimeOptions(architecture, out _, out var runtimeOptions)
            ? runtimeOptions?.Version
            : null;
    }
}

public sealed class PiperRuntimeOptions
{
    public string Version { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    public bool IsEmpty()
    {
        return string.IsNullOrWhiteSpace(Version) &&
               string.IsNullOrWhiteSpace(AssetName) &&
               string.IsNullOrWhiteSpace(DownloadUrl) &&
               SizeBytes <= 0 &&
               string.IsNullOrWhiteSpace(Sha256);
    }
}
