using System.Net;
using System.Net.Http;
using System.Text;
using RightSpeak.Models;
using RightSpeak.Services;
using Xunit;

namespace RightSpeak.Tests;

public sealed class VoiceDownloadFeatureTests
{
    [Theory]
    [InlineData("license: Apache License 2.0")]
    [InlineData("license: BSD 3-Clause")]
    [InlineData("license: Mozilla Public License 2.0")]
    [InlineData("license: https://creativecommons.org/licenses/by/4.0/")]
    [InlineData("license: https://creativecommons.org/publicdomain/zero/1.0/")]
    [InlineData("licenses: [\"cc-by-4.0\"]")]
    [InlineData("random text\n- Apache-2.0\nother text")]
    public async Task Catalog_accepts_common_model_card_license_formats(string licenseForTom)
    {
        var root = CreateTempDirectory();
        try
        {
            var options = BuildOptions(root);
            var responses = BuildCatalogResponses(licenseForTom: licenseForTom);
            using var httpClient = new HttpClient(new RoutingHttpHandler(responses));
            var store = new VoiceInstallStore(root);
            var service = new PiperVoiceCatalogService(store, httpClient, () => options);

            var voices = await service.GetDownloadableVoicesAsync();

            Assert.Contains(voices, voice => voice.Id == "fr_FR-tom-medium");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Catalog_hydrates_multilingual_and_excludes_low_quality()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = BuildOptions(root);
            var responses = BuildCatalogResponses();
            using var httpClient = new HttpClient(new RoutingHttpHandler(responses));
            var store = new VoiceInstallStore(root);
            var service = new PiperVoiceCatalogService(store, httpClient, () => options);

            var voices = await service.GetDownloadableVoicesAsync();

            Assert.Equal(2, voices.Count);
            Assert.Contains(voices, voice => voice.Id == "en_US-amy-medium");
            Assert.Contains(voices, voice => voice.Id == "fr_FR-tom-medium");
            Assert.DoesNotContain(voices, voice => voice.Id == "en_US-bad-low");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Catalog_excludes_denied_and_unapproved_license_voices()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = BuildOptions(root);
            File.WriteAllText(
                options.VoiceDenylistPath,
                """
                {
                  "deniedVoices": {
                    "fr_FR-tom-medium": "known_audio_artifacts"
                  }
                }
                """);
            var responses = BuildCatalogResponses(licenseForTom: "Proprietary");
            using var httpClient = new HttpClient(new RoutingHttpHandler(responses));
            var store = new VoiceInstallStore(root);
            var service = new PiperVoiceCatalogService(store, httpClient, () => options);

            var voices = await service.GetDownloadableVoicesAsync();

            var onlyVoice = Assert.Single(voices);
            Assert.Equal("en_US-amy-medium", onlyVoice.Id);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Catalog_cache_hit_returns_without_refetching()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = BuildOptions(root);
            var responses = BuildCatalogResponses();
            var handler = new RoutingHttpHandler(responses);
            using var httpClient = new HttpClient(handler);
            var store = new VoiceInstallStore(root);
            var service = new PiperVoiceCatalogService(store, httpClient, () => options);

            var first = await service.GetDownloadableVoicesAsync();
            Assert.Equal(2, first.Count);
            var firstCalls = handler.CallCount;

            var second = await service.GetDownloadableVoicesAsync();
            Assert.Equal(2, second.Count);
            Assert.Equal(firstCalls, handler.CallCount);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Install_blocks_when_sha256_metadata_is_missing()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new VoiceInstallStore(root);
            using var httpClient = new HttpClient(new RoutingHttpHandler(new Dictionary<string, string>()));
            var service = new VoiceDownloadService(store, new SuccessfulRuntimeInstaller(), httpClient);
            var voice = BuildDownloadableVoice("en_US-amy-medium", withMissingHashes: true);

            var result = await service.InstallOrUpdateAsync(voice);

            Assert.False(result.Success);
            Assert.Contains("SHA-256", result.Message);
            Assert.False(File.Exists(Path.Combine(store.VoicesDirectory, voice.ModelFileName)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static PiperCatalogOptions BuildOptions(string root)
    {
        var denylistPath = Path.Combine(root, "VoiceDenylist.json");
        File.WriteAllText(denylistPath, """{ "deniedVoices": {} }""");
        return new PiperCatalogOptions
        {
            CatalogVersion = 2,
            UpstreamVoicesUrl = "https://example.test/voices.json",
            VoiceBaseUrl = "https://example.test/resolve/v1.0.0/",
            HuggingFaceTreeApiBaseUrl = "https://example.test/tree/",
            CacheTtlHours = 24,
            VoiceDenylistPath = denylistPath,
            ExcludedQualities = new List<string> { "low", "x_low" },
            ApprovedLicenses = new List<string> { "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "MPL-2.0", "CC0-1.0", "CC-BY-4.0" },
            Runtime = new PiperRuntimeOptions
            {
                Version = "2023.11.14-2",
                AssetName = "piper_windows_amd64.zip",
                DownloadUrl = "https://example.test/piper.zip",
                SizeBytes = 123,
                Sha256 = new string('a', 64)
            }
        };
    }

    private static Dictionary<string, string> BuildCatalogResponses(string licenseForTom = "MIT")
    {
        var upstream = """
        {
          "en_US-amy-medium": {
            "name": "Amy",
            "language": { "code": "en_US", "name_english": "English" },
            "quality": "medium",
            "files": {
              "en/en_US/amy/medium/en_US-amy-medium.onnx": { "size_bytes": 1000 },
              "en/en_US/amy/medium/en_US-amy-medium.onnx.json": { "size_bytes": 50 },
              "en/en_US/amy/medium/MODEL_CARD": { "size_bytes": 12 }
            }
          },
          "fr_FR-tom-medium": {
            "name": "Tom",
            "language": { "code": "fr_FR", "name_english": "French" },
            "quality": "medium",
            "files": {
              "fr/fr_FR/tom/medium/fr_FR-tom-medium.onnx": { "size_bytes": 1100 },
              "fr/fr_FR/tom/medium/fr_FR-tom-medium.onnx.json": { "size_bytes": 60 },
              "fr/fr_FR/tom/medium/MODEL_CARD": { "size_bytes": 12 }
            }
          },
          "en_US-bad-low": {
            "name": "Bad",
            "language": { "code": "en_US", "name_english": "English" },
            "quality": "low",
            "files": {
              "en/en_US/bad/low/en_US-bad-low.onnx": { "size_bytes": 500 },
              "en/en_US/bad/low/en_US-bad-low.onnx.json": { "size_bytes": 40 },
              "en/en_US/bad/low/MODEL_CARD": { "size_bytes": 12 }
            }
          }
        }
        """;

        var amyTree = """
        [
          {
            "path": "en/en_US/amy/medium/en_US-amy-medium.onnx",
            "lfs": { "oid": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
          }
        ]
        """;
        var tomTree = """
        [
          {
            "path": "fr/fr_FR/tom/medium/fr_FR-tom-medium.onnx",
            "lfs": { "oid": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
          }
        ]
        """;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.test/voices.json"] = upstream,
            ["https://example.test/tree/v1.0.0/en/en_US/amy/medium"] = amyTree,
            ["https://example.test/tree/v1.0.0/fr/fr_FR/tom/medium"] = tomTree,
            ["https://example.test/resolve/v1.0.0/en/en_US/amy/medium/en_US-amy-medium.onnx.json"] = """{ "voice": "amy" }""",
            ["https://example.test/resolve/v1.0.0/fr/fr_FR/tom/medium/fr_FR-tom-medium.onnx.json"] = """{ "voice": "tom" }""",
            ["https://example.test/resolve/v1.0.0/en/en_US/amy/medium/MODEL_CARD"] = "license: MIT",
            ["https://example.test/resolve/v1.0.0/fr/fr_FR/tom/medium/MODEL_CARD"] = $"license: {licenseForTom}"
        };
    }

    private static DownloadableVoice BuildDownloadableVoice(string id, bool withMissingHashes = false)
    {
        return new DownloadableVoice
        {
            Id = id,
            DisplayName = id,
            Locale = "en_US",
            Quality = "medium",
            Status = VoiceInstallState.NotInstalled,
            AvailableVersion = "v1.0.0",
            ModelSizeBytes = 10,
            ConfigSizeBytes = 10,
            ModelSha256 = withMissingHashes ? string.Empty : new string('a', 64),
            ConfigSha256 = withMissingHashes ? string.Empty : new string('b', 64),
            ModelUrl = "https://example.test/model.onnx",
            ConfigUrl = "https://example.test/model.onnx.json",
            ModelFileName = $"{id}.onnx",
            ConfigFileName = $"{id}.onnx.json"
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rightspeak-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SuccessfulRuntimeInstaller : IPiperRuntimeInstaller
    {
        public bool IsRuntimeInstalled()
        {
            return true;
        }

        public Task<VoiceInstallResult> EnsureRuntimeInstalledAsync(
            IProgress<VoiceDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(VoiceInstallResult.Completed("Runtime ready."));
        }
    }

    private sealed class RoutingHttpHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public RoutingHttpHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (_responses.TryGetValue(url, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No mocked response for {url}")
            });
        }
    }
}
