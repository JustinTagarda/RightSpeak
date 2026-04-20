using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IVoiceInstallStore
{
    string PiperRootDirectory { get; }
    string VoicesDirectory { get; }
    string DownloadsDirectory { get; }
    string ManifestPath { get; }

    InstalledVoiceManifest LoadManifest();
    void SaveManifest(InstalledVoiceManifest manifest);
    InstalledVoiceRecord? FindInstalledVoice(string voiceId);
    bool IsVoiceInstalled(DownloadableVoice voice);
    void UpsertInstalledVoice(DownloadableVoice voice, string? runtimeVersion);
    void RemoveInstalledVoice(string voiceId);
}
