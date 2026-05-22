using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class VoiceDownloadService : IVoiceDownloadService
{
    private readonly IVoiceInstallStore _installStore;
    private readonly IPiperRuntimeInstaller _runtimeInstaller;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public VoiceDownloadService(IVoiceInstallStore installStore, IPiperRuntimeInstaller runtimeInstaller)
        : this(installStore, runtimeInstaller, new HttpClient())
    {
    }

    public VoiceDownloadService(IVoiceInstallStore installStore, IPiperRuntimeInstaller runtimeInstaller, HttpClient httpClient)
    {
        _installStore = installStore ?? throw new ArgumentNullException(nameof(installStore));
        _runtimeInstaller = runtimeInstaller ?? throw new ArgumentNullException(nameof(runtimeInstaller));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public bool IsBusy => _downloadGate.CurrentCount == 0;

    public async Task<VoiceInstallResult> InstallOrUpdateAsync(
        DownloadableVoice voice,
        IProgress<VoiceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (voice is null)
        {
            throw new ArgumentNullException(nameof(voice));
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "voice_install_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["voiceId"] = voice.Id,
                ["displayName"] = voice.DisplayName,
                ["requestedVersion"] = voice.AvailableVersion,
                ["modelUrl"] = voice.ModelUrl,
                ["configUrl"] = voice.ConfigUrl
            });

        if (!PiperRuntimeEnvironment.IsRuntimeSupportedOnCurrentArchitecture(out var installBlockedReason))
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_install_blocked_unsupported_architecture",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["message"] = installBlockedReason,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed(installBlockedReason ?? "Piper installs are unavailable on this build.");
        }

        if (string.IsNullOrWhiteSpace(voice.ModelSha256) || string.IsNullOrWhiteSpace(voice.ConfigSha256))
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_install_blocked_missing_sha256",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed("Voice install is blocked because SHA-256 metadata is missing.");
        }

        if (!await _downloadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_install_blocked_download_in_progress",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed("Another voice download is already running.");
        }

        var stagingDirectory = Path.Combine(_installStore.DownloadsDirectory, $"voice-{voice.Id}-{Guid.NewGuid():N}");
        try
        {
            AppDiagnostics.Info(
                "voice_install_runtime_ensure_started",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id
                });
            var runtimeResult = await _runtimeInstaller.EnsureRuntimeInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
            if (!runtimeResult.Success)
            {
                stopwatch.Stop();
                AppDiagnostics.Warn(
                    "voice_install_runtime_ensure_failed",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["voiceId"] = voice.Id,
                        ["runtimeMessage"] = runtimeResult.Message,
                        ["wasCancelled"] = runtimeResult.WasCancelled.ToString(),
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                    });
                return runtimeResult;
            }
            AppDiagnostics.Info(
                "voice_install_runtime_ensure_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id
                });

            Directory.CreateDirectory(stagingDirectory);
            var modelTempPath = Path.Combine(stagingDirectory, voice.ModelFileName);
            var configTempPath = Path.Combine(stagingDirectory, voice.ConfigFileName);
            AppDiagnostics.Info(
                "voice_install_staging_prepared",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["stagingDirectory"] = stagingDirectory
                });

            await DownloadAndVerifyAsync(
                    voice.Id,
                    "Downloading voice model",
                    voice.ModelUrl,
                    modelTempPath,
                    voice.ModelSizeBytes,
                    voice.ModelSha256,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            AppDiagnostics.Info(
                "voice_install_model_verified",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["modelTempPath"] = modelTempPath,
                    ["expectedSha256"] = voice.ModelSha256
                });
            await DownloadAndVerifyAsync(
                    voice.Id,
                    "Downloading voice config",
                    voice.ConfigUrl,
                    configTempPath,
                    voice.ConfigSizeBytes,
                    voice.ConfigSha256,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            AppDiagnostics.Info(
                "voice_install_config_verified",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["configTempPath"] = configTempPath,
                    ["expectedSha256"] = voice.ConfigSha256
                });

            var finalModelPath = Path.Combine(_installStore.VoicesDirectory, voice.ModelFileName);
            var finalConfigPath = Path.Combine(_installStore.VoicesDirectory, voice.ConfigFileName);
            Directory.CreateDirectory(_installStore.VoicesDirectory);
            File.Copy(modelTempPath, finalModelPath, overwrite: true);
            File.Copy(configTempPath, finalConfigPath, overwrite: true);
            AppDiagnostics.Info(
                "voice_install_files_activated",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["finalModelPath"] = finalModelPath,
                    ["finalConfigPath"] = finalConfigPath
                });

            var runtimeVersion = PiperVoiceCatalogService.LoadCatalogOptions().ResolveRuntimeVersion(PiperRuntimeEnvironment.GetCurrentProcessArchitecture())
                                 ?? _installStore.LoadManifest().PiperRuntimeVersion
                                 ?? string.Empty;
            _installStore.UpsertInstalledVoice(voice, runtimeVersion);
            stopwatch.Stop();
            AppDiagnostics.Info(
                "voice_installed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["version"] = voice.AvailableVersion,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Completed($"{voice.DisplayName} installed.");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_install_cancelled",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Cancelled();
        }
        catch (InvalidDataException ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_install_verification_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_install_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed("Couldn't install that voice.");
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            _downloadGate.Release();
        }
    }

    public Task<VoiceInstallResult> RemoveAsync(DownloadableVoice voice, CancellationToken cancellationToken = default)
    {
        if (voice is null)
        {
            throw new ArgumentNullException(nameof(voice));
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "voice_remove_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["voiceId"] = voice.Id
            });

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var installed = _installStore.FindInstalledVoice(voice.Id);
            if (installed is not null)
            {
                TryDeleteFile(Path.Combine(_installStore.VoicesDirectory, installed.ModelFileName));
                TryDeleteFile(Path.Combine(_installStore.VoicesDirectory, installed.ConfigFileName));
            }
            else
            {
                TryDeleteFile(Path.Combine(_installStore.VoicesDirectory, voice.ModelFileName));
                TryDeleteFile(Path.Combine(_installStore.VoicesDirectory, voice.ConfigFileName));
            }

            _installStore.RemoveInstalledVoice(voice.Id);
            stopwatch.Stop();
            AppDiagnostics.Info(
                "voice_remove_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return Task.FromResult(VoiceInstallResult.Completed($"{voice.DisplayName} removed."));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "voice_remove_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["voiceId"] = voice.Id,
                    ["message"] = ex.Message,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return Task.FromResult(VoiceInstallResult.Failed("Couldn't remove that voice."));
        }
    }

    private async Task DownloadAndVerifyAsync(
        string voiceId,
        string phase,
        string url,
        string targetPath,
        long expectedBytes,
        string expectedSha256,
        IProgress<VoiceDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new VoiceDownloadProgress(voiceId, phase, 0, expectedBytes));
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? (expectedBytes > 0 ? expectedBytes : (long?)null);
        long bytesReadTotal;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            bytesReadTotal = 0;
            var lastLoggedPercent = -10;
            AppDiagnostics.Info(
                "voice_download_started",
                new Dictionary<string, string?>
                {
                    ["voiceId"] = voiceId,
                    ["phase"] = phase,
                    ["url"] = url,
                    ["targetPath"] = targetPath,
                    ["expectedBytes"] = expectedBytes.ToString()
                });
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                bytesReadTotal += bytesRead;
                progress?.Report(new VoiceDownloadProgress(voiceId, phase, bytesReadTotal, totalBytes));
                if (totalBytes is > 0)
                {
                    var percent = (int)Math.Floor(bytesReadTotal * 100d / totalBytes.Value);
                    if (percent >= lastLoggedPercent + 10 || percent == 100)
                    {
                        lastLoggedPercent = percent;
                        AppDiagnostics.Info(
                            "voice_download_progress",
                            new Dictionary<string, string?>
                            {
                                ["voiceId"] = voiceId,
                                ["phase"] = phase,
                                ["percent"] = percent.ToString(),
                                ["bytesReceived"] = bytesReadTotal.ToString(),
                                ["totalBytes"] = totalBytes.Value.ToString()
                            });
                    }
                }
            }
        }

        var actualSha256 = await FileHashHelper.ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
        if (!FileHashHelper.Sha256Matches(actualSha256, expectedSha256))
        {
            AppDiagnostics.Warn(
                "voice_download_checksum_mismatch",
                new Dictionary<string, string?>
                {
                    ["voiceId"] = voiceId,
                    ["phase"] = phase,
                    ["expectedSha256"] = expectedSha256,
                    ["actualSha256"] = actualSha256
                });
            throw new InvalidDataException("Voice checksum did not match. Install was blocked.");
        }
        AppDiagnostics.Info(
            "voice_download_checksum_verified",
            new Dictionary<string, string?>
            {
                ["voiceId"] = voiceId,
                ["phase"] = phase,
                ["sha256"] = actualSha256,
                ["bytes"] = bytesReadTotal.ToString()
            });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            AppDiagnostics.Warn("voice_file_delete_failed");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            AppDiagnostics.Warn("voice_staging_delete_failed");
        }
    }
}
