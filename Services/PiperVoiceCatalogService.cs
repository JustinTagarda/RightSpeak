using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class PiperVoiceCatalogService : IVoiceCatalogService
{
    private const string CatalogResourcePath = "Resources/Piper/CuratedVoices.json";
    private readonly IVoiceInstallStore _installStore;
    private readonly HttpClient _httpClient;
    private readonly Func<PiperCatalogOptions> _optionsResolver;

    public PiperVoiceCatalogService(IVoiceInstallStore installStore)
        : this(installStore, new HttpClient())
    {
    }

    public PiperVoiceCatalogService(IVoiceInstallStore installStore, HttpClient httpClient)
        : this(installStore, httpClient, LoadCatalogOptions)
    {
    }

    public PiperVoiceCatalogService(
        IVoiceInstallStore installStore,
        HttpClient httpClient,
        Func<PiperCatalogOptions> optionsResolver)
    {
        _installStore = installStore ?? throw new ArgumentNullException(nameof(installStore));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _optionsResolver = optionsResolver ?? throw new ArgumentNullException(nameof(optionsResolver));
    }

    public async Task<IReadOnlyList<DownloadableVoice>> GetDownloadableVoicesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "voice_catalog_load_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId
            });

        var options = _optionsResolver();
        var availableVersion = ExtractVersionFromVoiceBaseUrl(options.VoiceBaseUrl);
        var excludedQualities = new HashSet<string>(options.ExcludedQualities ?? new(), StringComparer.OrdinalIgnoreCase);
        var approvedLicenses = new HashSet<string>(options.ApprovedLicenses ?? new(), StringComparer.OrdinalIgnoreCase);
        var denylist = LoadDenylist(options.VoiceDenylistPath);

        if (forceRefresh)
        {
            InvalidateCache();
        }

        if (!forceRefresh && TryLoadCache(options, availableVersion, out var cachedVoices))
        {
            var cachedResult = ApplyInstalledState(cachedVoices, availableVersion);
            stopwatch.Stop();
            AppDiagnostics.Info(
                "voice_catalog_load_completed_cache_hit",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["resolvedVoices"] = cachedResult.Count.ToString(),
                    ["availableVersion"] = availableVersion
                });
            return cachedResult;
        }

        var upstreamJson = await _httpClient.GetStringAsync(options.UpstreamVoicesUrl, cancellationToken).ConfigureAwait(false);
        var upstream = JsonNode.Parse(upstreamJson)?.AsObject()
            ?? throw new InvalidOperationException("Upstream Piper voices catalog could not be parsed.");
        AppDiagnostics.Info(
            "voice_catalog_upstream_loaded",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["upstreamVoicesUrl"] = options.UpstreamVoicesUrl,
                ["upstreamEntryCount"] = upstream.Count.ToString(),
                ["upstreamPayloadLength"] = upstreamJson.Length.ToString()
            });

        var cacheEntries = new List<CachedVoiceEntry>();
        var exclusionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in upstream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pair.Value is not JsonObject voiceObject)
            {
                Increment(exclusionCounts, "invalid_shape");
                continue;
            }

            var voiceId = pair.Key;
            var quality = voiceObject["quality"]?.GetValue<string>() ?? string.Empty;
            if (excludedQualities.Contains(quality))
            {
                Increment(exclusionCounts, "quality");
                LogExcludedVoice(operationId, voiceId, "quality");
                continue;
            }

            if (denylist.TryGetValue(voiceId, out var denyReason))
            {
                Increment(exclusionCounts, "denylist");
                LogExcludedVoice(operationId, voiceId, $"denylist:{denyReason}");
                continue;
            }

            if (!TryResolveFilePaths(voiceObject, out var modelPath, out var configPath, out var modelCardPath))
            {
                Increment(exclusionCounts, "missing_metadata");
                LogExcludedVoice(operationId, voiceId, "missing_metadata");
                continue;
            }

            var locale = voiceObject["language"]?["code"]?.GetValue<string>() ?? string.Empty;
            var modelSizeBytes = (voiceObject["files"]?[modelPath]?["size_bytes"]?.GetValue<long>()).GetValueOrDefault();
            var configSizeBytes = (voiceObject["files"]?[configPath]?["size_bytes"]?.GetValue<long>()).GetValueOrDefault();
            var displayName = BuildDisplayName(voiceObject, voiceId);
            var modelUrl = BuildDownloadUrl(options.VoiceBaseUrl, modelPath);
            var configUrl = BuildDownloadUrl(options.VoiceBaseUrl, configPath);
            var modelCardUrl = BuildDownloadUrl(options.VoiceBaseUrl, modelCardPath);

            if (!TryBuildHuggingFaceTreeUrl(options, modelPath, out var treeUrl))
            {
                Increment(exclusionCounts, "missing_metadata");
                LogExcludedVoice(operationId, voiceId, "missing_metadata");
                continue;
            }

            string modelSha256;
            string configSha256;
            string licenseId;
            try
            {
                modelSha256 = await ResolveModelSha256Async(treeUrl, modelPath, cancellationToken).ConfigureAwait(false);
                configSha256 = await ResolveConfigSha256Async(configUrl, cancellationToken).ConfigureAwait(false);
                var modelCard = await _httpClient.GetStringAsync(modelCardUrl, cancellationToken).ConfigureAwait(false);
                licenseId = ParseLicenseId(modelCard);
            }
            catch (Exception ex)
            {
                Increment(exclusionCounts, "missing_metadata");
                LogExcludedVoice(operationId, voiceId, $"missing_metadata:{ex.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(licenseId) || !approvedLicenses.Contains(licenseId))
            {
                Increment(exclusionCounts, "license");
                LogExcludedVoice(operationId, voiceId, $"license:{licenseId}");
                continue;
            }

            cacheEntries.Add(new CachedVoiceEntry
            {
                Id = voiceId,
                DisplayName = displayName,
                Locale = locale,
                Quality = quality,
                LicenseId = licenseId,
                AvailableVersion = availableVersion,
                ModelSizeBytes = modelSizeBytes,
                ConfigSizeBytes = configSizeBytes,
                ModelSha256 = modelSha256,
                ConfigSha256 = configSha256,
                ModelUrl = modelUrl,
                ConfigUrl = configUrl,
                ModelFileName = Path.GetFileName(modelPath),
                ConfigFileName = Path.GetFileName(configPath)
            });
        }

        SaveCache(options, availableVersion, cacheEntries);
        var result = ApplyInstalledState(cacheEntries, availableVersion);
        stopwatch.Stop();
        AppDiagnostics.Info(
            "voice_catalog_load_completed",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["resolvedVoices"] = result.Count.ToString(),
                ["excludedQualityCount"] = GetCount(exclusionCounts, "quality").ToString(),
                ["excludedLicenseCount"] = GetCount(exclusionCounts, "license").ToString(),
                ["excludedDenylistCount"] = GetCount(exclusionCounts, "denylist").ToString(),
                ["excludedMissingMetadataCount"] = GetCount(exclusionCounts, "missing_metadata").ToString()
            });
        return result;
    }

    private void InvalidateCache()
    {
        try
        {
            var cachePath = BuildCachePath();
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
            // Best-effort cache invalidation; continue with fresh fetch attempt.
        }
    }

    internal static PiperCatalogOptions LoadCatalogOptions()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, CatalogResourcePath);
        if (!File.Exists(catalogPath))
        {
            catalogPath = Path.Combine(Directory.GetCurrentDirectory(), CatalogResourcePath);
        }

        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("Curated Piper voice catalog was not found.", catalogPath);
        }

        var json = File.ReadAllText(catalogPath);
        return JsonSerializer.Deserialize<PiperCatalogOptions>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Curated Piper voice catalog could not be parsed.");
    }

    private IReadOnlyList<DownloadableVoice> ApplyInstalledState(IReadOnlyList<CachedVoiceEntry> entries, string availableVersion)
    {
        var manifest = _installStore.LoadManifest();
        var installedById = manifest.Voices.ToDictionary(voice => voice.Id, StringComparer.OrdinalIgnoreCase);
        var installSupported = PiperRuntimeEnvironment.IsRuntimeSupportedOnCurrentArchitecture(out var installBlockedReason);
        var catalogVoices = entries
            .Select(entry =>
            {
                installedById.TryGetValue(entry.Id, out var installed);
                var isBundledVoice = string.Equals(entry.Id, PiperRuntimeEnvironment.PreinstalledVoiceId, StringComparison.OrdinalIgnoreCase) &&
                                     HasVoiceFiles(entry.ModelFileName, entry.ConfigFileName);
                var status = installed is null && !isBundledVoice
                    ? VoiceInstallState.NotInstalled
                    : installed is not null &&
                      !string.IsNullOrWhiteSpace(installed.Version) &&
                      !string.Equals(installed.Version, availableVersion, StringComparison.OrdinalIgnoreCase)
                        ? VoiceInstallState.UpdateAvailable
                        : VoiceInstallState.Installed;

                return new DownloadableVoice
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName,
                    Locale = entry.Locale,
                    Quality = entry.Quality,
                    IsBundled = isBundledVoice,
                    IsInstallSupported = installSupported,
                    InstallBlockedReason = installBlockedReason,
                    LicenseId = entry.LicenseId,
                    Status = status,
                    InstalledVersion = installed?.Version ?? (isBundledVoice ? availableVersion : null),
                    AvailableVersion = entry.AvailableVersion,
                    ModelSizeBytes = entry.ModelSizeBytes,
                    ConfigSizeBytes = entry.ConfigSizeBytes,
                    ModelSha256 = entry.ModelSha256,
                    ConfigSha256 = entry.ConfigSha256,
                    ModelUrl = entry.ModelUrl,
                    ConfigUrl = entry.ConfigUrl,
                    ModelFileName = entry.ModelFileName,
                    ConfigFileName = entry.ConfigFileName
                };
            })
            .ToList();

        AppDiagnostics.Info(
            "voice_catalog_local_installed_overlay",
            new Dictionary<string, string?>
            {
                ["catalogCount"] = entries.Count.ToString(),
                ["finalCount"] = catalogVoices.Count.ToString(),
                ["preinstalledVoiceId"] = PiperRuntimeEnvironment.PreinstalledVoiceId,
                ["installSupported"] = installSupported.ToString()
            });

        return catalogVoices
            .OrderBy(voice => voice.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool HasVoiceFiles(string modelFileName, string configFileName)
    {
        if (string.IsNullOrWhiteSpace(modelFileName) || string.IsNullOrWhiteSpace(configFileName))
        {
            return false;
        }

        foreach (var directory in PiperRuntimeEnvironment.EnumerateVoiceDirectoryCandidates(_installStore.PiperRootDirectory, PiperRuntimeEnvironment.GetBaseDirectory()))
        {
            var modelPath = Path.Combine(directory, modelFileName);
            var configPath = Path.Combine(directory, configFileName);
            if (File.Exists(modelPath) && File.Exists(configPath))
            {
                return true;
            }
        }

        return false;
    }

    private static (string Locale, string Quality) ParseVoiceIdParts(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return (string.Empty, string.Empty);
        }

        var tokens = voiceId.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return (string.Empty, string.Empty);
        }

        return (tokens[0].Replace('_', '-'), tokens[^1]);
    }

    private bool TryLoadCache(PiperCatalogOptions options, string availableVersion, out IReadOnlyList<CachedVoiceEntry> entries)
    {
        entries = Array.Empty<CachedVoiceEntry>();
        var cachePath = BuildCachePath();
        if (!File.Exists(cachePath))
        {
            return false;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<VoiceCatalogCache>(File.ReadAllText(cachePath));
            if (cache is null)
            {
                return false;
            }

            if (cache.CatalogVersion != options.CatalogVersion ||
                !string.Equals(cache.AvailableVersion, availableVersion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expiresAt = cache.GeneratedAtUtc.AddHours(options.CacheTtlHours <= 0 ? 24 : options.CacheTtlHours);
            if (DateTime.UtcNow > expiresAt)
            {
                return false;
            }

            entries = cache.Voices;
            return entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void SaveCache(PiperCatalogOptions options, string availableVersion, IReadOnlyList<CachedVoiceEntry> entries)
    {
        var cachePath = BuildCachePath();
        var cache = new VoiceCatalogCache
        {
            CatalogVersion = options.CatalogVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            AvailableVersion = availableVersion,
            Voices = entries.ToList()
        };

        Directory.CreateDirectory(_installStore.PiperRootDirectory);
        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(cachePath, json);
    }

    private string BuildCachePath()
    {
        return Path.Combine(_installStore.PiperRootDirectory, "catalog-cache.json");
    }

    private static Dictionary<string, string> LoadDenylist(string denylistPath)
    {
        var resolvedPath = Path.Combine(AppContext.BaseDirectory, denylistPath);
        if (!File.Exists(resolvedPath))
        {
            resolvedPath = Path.Combine(Directory.GetCurrentDirectory(), denylistPath);
        }

        if (!File.Exists(resolvedPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(resolvedPath);
            var options = JsonSerializer.Deserialize<VoiceDenylistOptions>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return options?.DeniedVoices ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool TryResolveFilePaths(
        JsonObject voiceObject,
        out string modelPath,
        out string configPath,
        out string modelCardPath)
    {
        modelPath = string.Empty;
        configPath = string.Empty;
        modelCardPath = string.Empty;
        if (voiceObject["files"] is not JsonObject filesObject)
        {
            return false;
        }

        modelPath = filesObject
            .Select(file => file.Key)
            .FirstOrDefault(path => path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        configPath = filesObject
            .Select(file => file.Key)
            .FirstOrDefault(path => path.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        modelCardPath = filesObject
            .Select(file => file.Key)
            .FirstOrDefault(path => path.EndsWith("MODEL_CARD", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(modelPath) &&
               !string.IsNullOrWhiteSpace(configPath) &&
               !string.IsNullOrWhiteSpace(modelCardPath);
    }

    private static string BuildDisplayName(JsonObject voiceObject, string voiceId)
    {
        var languageName = voiceObject["language"]?["name_english"]?.GetValue<string>();
        var voiceName = voiceObject["name"]?.GetValue<string>();
        var quality = voiceObject["quality"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(languageName) &&
            !string.IsNullOrWhiteSpace(voiceName) &&
            !string.IsNullOrWhiteSpace(quality))
        {
            return $"{voiceName} ({languageName}, {quality})";
        }

        return voiceId;
    }

    private static bool TryBuildHuggingFaceTreeUrl(PiperCatalogOptions options, string modelPath, out string treeUrl)
    {
        treeUrl = string.Empty;
        var version = ExtractVersionFromVoiceBaseUrl(options.VoiceBaseUrl);
        var directoryPath = Path.GetDirectoryName(modelPath)?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        treeUrl = string.Concat(options.HuggingFaceTreeApiBaseUrl.TrimEnd('/'), "/", version, "/", directoryPath);
        return true;
    }

    private async Task<string> ResolveModelSha256Async(string treeUrl, string modelPath, CancellationToken cancellationToken)
    {
        var treeJson = await _httpClient.GetStringAsync(treeUrl, cancellationToken).ConfigureAwait(false);
        var tree = JsonNode.Parse(treeJson)?.AsArray();
        if (tree is null)
        {
            throw new InvalidOperationException("Tree metadata could not be parsed.");
        }

        foreach (var node in tree)
        {
            if (node is not JsonObject fileNode)
            {
                continue;
            }

            var path = fileNode["path"]?.GetValue<string>();
            if (!string.Equals(path, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sha = fileNode["lfs"]?["oid"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(sha))
            {
                return sha;
            }
        }

        throw new InvalidOperationException("Model SHA-256 was not found in HuggingFace metadata.");
    }

    private async Task<string> ResolveConfigSha256Async(string configUrl, CancellationToken cancellationToken)
    {
        var configText = await _httpClient.GetStringAsync(configUrl, cancellationToken).ConfigureAwait(false);
        var tempPath = Path.Combine(_installStore.DownloadsDirectory, $"config-hash-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(_installStore.DownloadsDirectory);
        await File.WriteAllTextAsync(tempPath, configText, cancellationToken).ConfigureAwait(false);
        try
        {
            return await FileHashHelper.ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static string ParseLicenseId(string modelCardText)
    {
        if (string.IsNullOrWhiteSpace(modelCardText))
        {
            return string.Empty;
        }

        var patterns = new[]
        {
            @"license\s*[:=]\s*([^\r\n]+)",
            @"^\s*-\s*license\s*:\s*([^\r\n]+)",
            @"^\s*licenses?\s*[:=]\s*([^\r\n]+)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(modelCardText, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success)
            {
                continue;
            }

            var candidate = match.Groups.Count >= 2 ? match.Groups[1].Value : match.Value;
            var normalized = NormalizeLicense(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        var lineMatches = Regex.Matches(modelCardText, @"(?im)^\s*-\s*([^\r\n]+)$");
        foreach (Match lineMatch in lineMatches)
        {
            var normalized = NormalizeLicense(lineMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        foreach (var token in Regex.Split(modelCardText, @"[\s,\;\|\(\)\[\]\{\}""'<>]+"))
        {
            var normalized = NormalizeLicense(token);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static string NormalizeLicense(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim().Trim('"', '\'', '`');
        if (value.StartsWith('[') && value.EndsWith(']') && value.Length > 2)
        {
            value = value[1..^1].Trim().Trim('"', '\'');
        }

        var upper = value.ToUpperInvariant();
        var compact = Regex.Replace(upper, @"[^A-Z0-9]+", "");
        if (compact == "MITLICENSE" || compact == "MIT")
        {
            return "MIT";
        }

        if (compact.Contains("APACHE20") ||
            compact.Contains("APACHELICENSE20") ||
            compact.Contains("SPDXLICENSEIDENTIFIERAPACHE20"))
        {
            return "Apache-2.0";
        }

        if (compact.Contains("BSD2CLAUSE") || compact.Contains("SIMPLIFIEDBSD"))
        {
            return "BSD-2-Clause";
        }

        if (compact.Contains("BSD3CLAUSE") || compact.Contains("NEWBSD"))
        {
            return "BSD-3-Clause";
        }

        if (compact.Contains("MPL20") || compact.Contains("MOZILLAPUBLICLICENSE20"))
        {
            return "MPL-2.0";
        }

        if (compact.Contains("CC01") ||
            compact.Contains("CC0UNIVERSAL10") ||
            upper.Contains("CREATIVECOMMONSZERO"))
        {
            return "CC0-1.0";
        }

        if (compact.Contains("CCBY40") ||
            compact.Contains("CREATIVECOMMONSATTRIBUTION40") ||
            upper.Contains("CREATIVECOMMONSATTRIBUTION4.0"))
        {
            return "CC-BY-4.0";
        }

        if (upper.Contains("CREATIVECOMMONS.ORG/LICENSES/BY/4.0"))
        {
            return "CC-BY-4.0";
        }

        if (upper.Contains("CREATIVECOMMONS.ORG/PUBLICDOMAIN/ZERO/1.0"))
        {
            return "CC0-1.0";
        }

        return upper switch
        {
            "MIT" => "MIT",
            "APACHE-2.0" => "Apache-2.0",
            "APACHE2.0" => "Apache-2.0",
            "BSD-2-CLAUSE" => "BSD-2-Clause",
            "BSD-3-CLAUSE" => "BSD-3-Clause",
            "MPL-2.0" => "MPL-2.0",
            "CC0-1.0" => "CC0-1.0",
            "CC-BY-4.0" => "CC-BY-4.0",
            _ => string.Empty
        };
    }

    private static void Increment(IDictionary<string, int> map, string key)
    {
        map[key] = map.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static int GetCount(IReadOnlyDictionary<string, int> map, string key)
    {
        return map.TryGetValue(key, out var count) ? count : 0;
    }

    private static void LogExcludedVoice(string operationId, string voiceId, string reason)
    {
        AppDiagnostics.Info(
            "voice_catalog_voice_excluded",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["voiceId"] = voiceId,
                ["reason"] = reason
            });
    }

    private static string BuildDownloadUrl(string baseUrl, string relativePath)
    {
        return string.Concat(baseUrl.TrimEnd('/'), "/", relativePath.Replace('\\', '/'));
    }

    private static string ExtractVersionFromVoiceBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var index = trimmed.LastIndexOf("/", StringComparison.Ordinal);
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }
}
