using System;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IVoiceDownloadService
{
    bool IsBusy { get; }

    Task<VoiceInstallResult> InstallOrUpdateAsync(
        DownloadableVoice voice,
        IProgress<VoiceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<VoiceInstallResult> RemoveAsync(DownloadableVoice voice, CancellationToken cancellationToken = default);
}
