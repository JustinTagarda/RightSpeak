using System;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IPiperRuntimeInstaller
{
    bool IsRuntimeInstalled();

    Task<VoiceInstallResult> EnsureRuntimeInstalledAsync(
        IProgress<VoiceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
