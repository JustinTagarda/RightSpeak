using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class PiperRuntimeInstaller : IPiperRuntimeInstaller
{
    private static readonly string[] RequiredRuntimeItems =
    {
        "piper.exe",
        "onnxruntime.dll",
        "espeak-ng.dll",
        "piper_phonemize.dll",
        "espeak-ng-data"
    };

    private readonly IVoiceInstallStore _installStore;
    private readonly HttpClient _httpClient;

    public PiperRuntimeInstaller(IVoiceInstallStore installStore)
        : this(installStore, new HttpClient())
    {
    }

    internal PiperRuntimeInstaller(IVoiceInstallStore installStore, HttpClient httpClient)
    {
        _installStore = installStore ?? throw new ArgumentNullException(nameof(installStore));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public bool IsRuntimeInstalled()
    {
        return RequiredRuntimeItems.All(item =>
        {
            var path = Path.Combine(_installStore.PiperRootDirectory, item);
            return File.Exists(path) || Directory.Exists(path);
        });
    }

    public async Task<VoiceInstallResult> EnsureRuntimeInstalledAsync(
        IProgress<VoiceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "piper_runtime_install_check_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["runtimeRoot"] = _installStore.PiperRootDirectory
            });

        if (IsRuntimeInstalled())
        {
            stopwatch.Stop();
            AppDiagnostics.Info(
                "piper_runtime_install_skipped_already_installed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Completed("Piper runtime is already installed.");
        }

        var options = PiperVoiceCatalogService.LoadCatalogOptions().Runtime;
        if (string.IsNullOrWhiteSpace(options.Sha256))
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "piper_runtime_install_blocked_missing_sha256",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["version"] = options.Version
                });
            return VoiceInstallResult.Failed("Piper runtime install is blocked because SHA-256 metadata is missing.");
        }

        var zipPath = Path.Combine(_installStore.DownloadsDirectory, options.AssetName);
        var extractDirectory = Path.Combine(_installStore.DownloadsDirectory, $"runtime-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(_installStore.DownloadsDirectory);
            progress?.Report(new VoiceDownloadProgress("piper-runtime", "Downloading Piper runtime", 0, options.SizeBytes));
            AppDiagnostics.Info(
                "piper_runtime_download_started",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["version"] = options.Version,
                    ["downloadUrl"] = options.DownloadUrl,
                    ["expectedBytes"] = options.SizeBytes.ToString(),
                    ["zipPath"] = zipPath
                });
            await DownloadFileAsync(options.DownloadUrl, zipPath, options.SizeBytes, "piper-runtime", "Downloading Piper runtime", progress, cancellationToken)
                .ConfigureAwait(false);
            AppDiagnostics.Info(
                "piper_runtime_download_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["zipPath"] = zipPath,
                    ["actualBytes"] = new FileInfo(zipPath).Length.ToString()
                });

            var actualHash = await FileHashHelper.ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
            if (!FileHashHelper.Sha256Matches(actualHash, options.Sha256))
            {
                stopwatch.Stop();
                AppDiagnostics.Warn(
                    "piper_runtime_checksum_mismatch",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["expectedSha256"] = options.Sha256,
                        ["actualSha256"] = actualHash,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                    });
                return VoiceInstallResult.Failed("Piper runtime checksum did not match. Install was blocked.");
            }
            AppDiagnostics.Info(
                "piper_runtime_checksum_verified",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["sha256"] = actualHash
                });

            if (Directory.Exists(extractDirectory))
            {
                Directory.Delete(extractDirectory, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDirectory);
            AppDiagnostics.Info(
                "piper_runtime_extract_completed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["extractDirectory"] = extractDirectory
                });
            var runtimeSourceDirectory = FindExtractedRuntimeDirectory(extractDirectory);
            if (runtimeSourceDirectory is null)
            {
                stopwatch.Stop();
                AppDiagnostics.Warn(
                    "piper_runtime_install_failed_missing_executable",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["extractDirectory"] = extractDirectory,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                    });
                return VoiceInstallResult.Failed("Piper runtime archive did not contain piper.exe.");
            }

            CopyDirectory(runtimeSourceDirectory, _installStore.PiperRootDirectory);
            var manifest = _installStore.LoadManifest();
            manifest.PiperRuntimeVersion = options.Version;
            _installStore.SaveManifest(manifest);
            stopwatch.Stop();

            AppDiagnostics.Info(
                "piper_runtime_installed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["version"] = options.Version,
                    ["root"] = _installStore.PiperRootDirectory,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Completed("Piper runtime installed.");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "piper_runtime_install_cancelled",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Cancelled();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "piper_runtime_install_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["message"] = ex.Message
                });
            return VoiceInstallResult.Failed("Couldn't install Piper runtime.");
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractDirectory);
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string targetPath,
        long expectedBytes,
        string voiceId,
        string phase,
        IProgress<VoiceDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? (expectedBytes > 0 ? expectedBytes : (long?)null);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long bytesReadTotal = 0;
        var lastLoggedPercent = -10;
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
                        "piper_runtime_download_progress",
                        new Dictionary<string, string?>
                        {
                            ["phase"] = phase,
                            ["percent"] = percent.ToString(),
                            ["bytesReceived"] = bytesReadTotal.ToString(),
                            ["totalBytes"] = totalBytes.Value.ToString()
                        });
                }
            }
        }
    }

    private static string? FindExtractedRuntimeDirectory(string extractDirectory)
    {
        var piperExecutable = Directory
            .EnumerateFiles(extractDirectory, "piper.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(piperExecutable)
            ? null
            : Path.GetDirectoryName(piperExecutable);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
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
            AppDiagnostics.Warn("piper_runtime_temp_delete_failed");
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
            AppDiagnostics.Warn("piper_runtime_extract_delete_failed");
        }
    }
}
