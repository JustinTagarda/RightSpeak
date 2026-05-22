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
        if (TryGetInstalledRuntimeDirectory(out _, out _))
        {
            return true;
        }

        return false;
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
                ["runtimeRoot"] = _installStore.PiperRootDirectory,
                ["activeRuntimeDirectory"] = PiperRuntimeEnvironment.GetActiveRuntimeDirectory(_installStore.PiperRootDirectory)
            });

        if (!PiperRuntimeEnvironment.IsRuntimeSupportedOnCurrentArchitecture(out var unsupportedArchitectureReason))
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "piper_runtime_install_blocked_unsupported_architecture",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["message"] = unsupportedArchitectureReason,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed(unsupportedArchitectureReason ?? "Piper installs are unavailable on this build.");
        }

        if (TryGetInstalledRuntimeDirectory(out var installedRuntimeDirectory, out var installedRuntimeVersion))
        {
            stopwatch.Stop();
            AppDiagnostics.Info(
                "piper_runtime_install_skipped_already_installed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["runtimeDirectory"] = installedRuntimeDirectory,
                    ["runtimeVersion"] = installedRuntimeVersion,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Completed("Piper runtime is already installed.");
        }

        var architecture = PiperRuntimeEnvironment.GetCurrentProcessArchitecture();
        var runtimeMoniker = PiperRuntimeEnvironment.GetRuntimeMoniker(architecture);
        PiperRuntimeOptions? runtimeOptions = null;
        try
        {
            var catalogOptions = PiperVoiceCatalogService.LoadCatalogOptions();
            catalogOptions.TryResolveRuntimeOptions(architecture, out _, out runtimeOptions);
        }
        catch
        {
            runtimeOptions = null;
        }
        foreach (var packagedRuntimeDirectory in PiperRuntimeEnvironment.EnumeratePackagedRuntimeDirectories(
                     PiperRuntimeEnvironment.GetBaseDirectory(),
                     architecture))
        {
            if (!ValidateRuntimeDirectory(packagedRuntimeDirectory, out _))
            {
                continue;
            }

            ActivateRuntimeDirectory(packagedRuntimeDirectory);
            var manifest = _installStore.LoadManifest();
            manifest.PiperRuntimeVersion = runtimeOptions?.Version ?? $"{runtimeMoniker}-packaged";
            _installStore.SaveManifest(manifest);
            stopwatch.Stop();
            AppDiagnostics.Info(
                "piper_runtime_installed_from_packaged_assets",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["version"] = manifest.PiperRuntimeVersion,
                    ["packagedRuntimeDirectory"] = packagedRuntimeDirectory,
                    ["activeRuntimeDirectory"] = PiperRuntimeEnvironment.GetActiveRuntimeDirectory(_installStore.PiperRootDirectory),
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Completed("Piper runtime installed from packaged assets.");
        }

        if (runtimeOptions is null)
        {
            stopwatch.Stop();
            AppDiagnostics.Warn(
                "piper_runtime_install_blocked_missing_runtime_configuration",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["architecture"] = architecture.ToString(),
                    ["runtimeMoniker"] = runtimeMoniker,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                });
            return VoiceInstallResult.Failed(
                string.IsNullOrWhiteSpace(runtimeMoniker)
                    ? $"Piper installs are currently unavailable on {architecture} builds."
                    : $"Piper installs are currently unavailable on {architecture} builds because no bundled or downloadable {runtimeMoniker} runtime is configured.");
        }

        var options = runtimeOptions;
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

            if (!ValidateRuntimeDirectory(runtimeSourceDirectory, out var sourceValidationFailure))
            {
                stopwatch.Stop();
                AppDiagnostics.Warn(
                    "piper_runtime_install_failed_invalid_archive_layout",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["runtimeSourceDirectory"] = runtimeSourceDirectory,
                        ["message"] = sourceValidationFailure,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                    });
                return VoiceInstallResult.Failed(sourceValidationFailure ?? "Piper runtime archive was incomplete.");
            }

            var versionDirectory = PiperRuntimeEnvironment.GetVersionedRuntimeDirectory(_installStore.PiperRootDirectory, options.Version);
            StageDirectory(runtimeSourceDirectory, versionDirectory);
            if (!ValidateRuntimeDirectory(versionDirectory, out var versionValidationFailure))
            {
                stopwatch.Stop();
                AppDiagnostics.Warn(
                    "piper_runtime_install_failed_invalid_staged_runtime",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["versionDirectory"] = versionDirectory,
                        ["message"] = versionValidationFailure,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString()
                    });
                return VoiceInstallResult.Failed(versionValidationFailure ?? "Piper runtime staging failed.");
            }

            ActivateRuntimeDirectory(versionDirectory);
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
                    ["activeRuntimeDirectory"] = PiperRuntimeEnvironment.GetActiveRuntimeDirectory(_installStore.PiperRootDirectory),
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

    private bool TryGetInstalledRuntimeDirectory(out string? runtimeDirectory, out string? runtimeVersion)
    {
        runtimeDirectory = null;
        runtimeVersion = null;

        var activeRuntimeDirectory = PiperRuntimeEnvironment.GetActiveRuntimeDirectory(_installStore.PiperRootDirectory);
        if (ValidateRuntimeDirectory(activeRuntimeDirectory, out _))
        {
            runtimeDirectory = activeRuntimeDirectory;
            runtimeVersion = _installStore.LoadManifest().PiperRuntimeVersion;
            return true;
        }

        foreach (var legacyDirectory in new[]
                 {
                     _installStore.PiperRootDirectory,
                     Path.Combine(_installStore.PiperRootDirectory, "piper")
                 })
        {
            if (!ValidateRuntimeDirectory(legacyDirectory, out _))
            {
                continue;
            }

            runtimeDirectory = legacyDirectory;
            runtimeVersion = _installStore.LoadManifest().PiperRuntimeVersion;
            return true;
        }

        return false;
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

    private static bool ValidateRuntimeDirectory(string runtimeDirectory, out string? failureReason)
    {
        failureReason = null;
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            failureReason = "Piper runtime directory was not found.";
            return false;
        }

        if (PiperRuntimeEnvironment.HasRequiredRuntimeItems(runtimeDirectory, out var missingRuntimeItems))
        {
            return true;
        }

        failureReason = $"Piper runtime is incomplete: {string.Join(", ", missingRuntimeItems)}.";
        return false;
    }

    private static void StageDirectory(string sourceDirectory, string targetDirectory)
    {
        var tempDirectory = $"{targetDirectory}.tmp-{Guid.NewGuid():N}";
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }

        CopyDirectory(sourceDirectory, tempDirectory);
        ReplaceDirectory(tempDirectory, targetDirectory);
    }

    private void ActivateRuntimeDirectory(string sourceDirectory)
    {
        var activeRuntimeDirectory = PiperRuntimeEnvironment.GetActiveRuntimeDirectory(_installStore.PiperRootDirectory);
        StageDirectory(sourceDirectory, activeRuntimeDirectory);
    }

    private static void ReplaceDirectory(string sourceDirectory, string targetDirectory)
    {
        var backupDirectory = $"{targetDirectory}.bak-{Guid.NewGuid():N}";
        try
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
            }

            Directory.Move(sourceDirectory, targetDirectory);
            TryDeleteDirectory(backupDirectory);
        }
        catch
        {
            TryDeleteDirectory(sourceDirectory);
            if (Directory.Exists(backupDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
            }

            throw;
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
