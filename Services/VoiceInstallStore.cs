using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class VoiceInstallStore : IVoiceInstallStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public VoiceInstallStore()
        : this(null)
    {
    }

    public VoiceInstallStore(string? piperRootDirectory)
    {
        PiperRootDirectory = string.IsNullOrWhiteSpace(piperRootDirectory)
            ? PiperRuntimeEnvironment.GetDefaultPiperRootDirectory()
            : piperRootDirectory;
        VoicesDirectory = PiperRuntimeEnvironment.GetVoicesDirectory(PiperRootDirectory);
        DownloadsDirectory = PiperRuntimeEnvironment.GetDownloadsDirectory(PiperRootDirectory);
        ManifestPath = PiperRuntimeEnvironment.GetManifestPath(PiperRootDirectory);

        Directory.CreateDirectory(PiperRootDirectory);
        Directory.CreateDirectory(VoicesDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
    }

    public string PiperRootDirectory { get; }
    public string VoicesDirectory { get; }
    public string DownloadsDirectory { get; }
    public string ManifestPath { get; }

    public InstalledVoiceManifest LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return new InstalledVoiceManifest();
            }

            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize<InstalledVoiceManifest>(json) ?? new InstalledVoiceManifest();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "voice_manifest_load_failed",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["path"] = ManifestPath,
                    ["message"] = ex.Message
                });
            return new InstalledVoiceManifest();
        }
    }

    public void SaveManifest(InstalledVoiceManifest manifest)
    {
        Directory.CreateDirectory(PiperRootDirectory);
        var tempPath = $"{ManifestPath}.tmp";
        var json = JsonSerializer.Serialize(manifest, SerializerOptions);
        File.WriteAllText(tempPath, json);
        if (File.Exists(ManifestPath))
        {
            File.Replace(tempPath, ManifestPath, null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempPath, ManifestPath);
    }

    public InstalledVoiceRecord? FindInstalledVoice(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return null;
        }

        return LoadManifest().Voices.FirstOrDefault(voice =>
            string.Equals(voice.Id, voiceId, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsVoiceInstalled(DownloadableVoice voice)
    {
        var record = FindInstalledVoice(voice.Id);
        if (record is null)
        {
            return false;
        }

        var modelPath = Path.Combine(VoicesDirectory, record.ModelFileName);
        var configPath = Path.Combine(VoicesDirectory, record.ConfigFileName);
        return File.Exists(modelPath) && File.Exists(configPath);
    }

    public void UpsertInstalledVoice(DownloadableVoice voice, string? runtimeVersion)
    {
        var manifest = LoadManifest();
        manifest.PiperRuntimeVersion = runtimeVersion ?? manifest.PiperRuntimeVersion;
        manifest.Voices.RemoveAll(existing =>
            string.Equals(existing.Id, voice.Id, StringComparison.OrdinalIgnoreCase));
        manifest.Voices.Add(new InstalledVoiceRecord
        {
            Id = voice.Id,
            DisplayName = voice.DisplayName,
            Version = voice.AvailableVersion,
            ModelFileName = voice.ModelFileName,
            ConfigFileName = voice.ConfigFileName,
            ModelSha256 = voice.ModelSha256,
            ConfigSha256 = voice.ConfigSha256,
            ModelSourceUrl = voice.ModelUrl,
            ConfigSourceUrl = voice.ConfigUrl,
            InstalledAtUtc = DateTime.UtcNow
        });

        SaveManifest(manifest);
    }

    public void RemoveInstalledVoice(string voiceId)
    {
        var manifest = LoadManifest();
        manifest.Voices.RemoveAll(existing =>
            string.Equals(existing.Id, voiceId, StringComparison.OrdinalIgnoreCase));
        SaveManifest(manifest);
    }
}
