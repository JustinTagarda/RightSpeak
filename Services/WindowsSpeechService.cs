using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class WindowsSpeechService : ISpeechService, IPrefetchSpeechService, IDisposable
{
    public sealed class ChunkSpeechResult
    {
        public ChunkSpeechResult(SpeechResult speechResult, string? engineName)
        {
            SpeechResult = speechResult;
            EngineName = engineName;
        }

        public SpeechResult SpeechResult { get; }
        public string? EngineName { get; }
    }

    private const int PiperPrefetchGraceWindowMilliseconds = 40;
    private const int WindowsOneCorePrefetchGraceWindowMilliseconds = 15;
    private const int SystemSpeechPrefetchGraceWindowMilliseconds = 15;
    private const int DefaultPrefetchGraceWindowMilliseconds = 15;
    private const int ShortChunkThresholdCharacters = 120;

    private readonly PiperSpeechService _piperSpeechService;
    private readonly WindowsNeuralSpeechService _preferredSpeechService;
    private readonly SystemSpeechService _fallbackSpeechService;
    private readonly SpeechVoice[] _installedVoices;
    private bool _disposed;

    public WindowsSpeechService()
    {
        _piperSpeechService = new PiperSpeechService();
        _preferredSpeechService = new WindowsNeuralSpeechService();
        _fallbackSpeechService = new SystemSpeechService();
        _installedVoices = _piperSpeechService
            .GetInstalledVoices()
            .Concat(_preferredSpeechService.GetInstalledVoices())
            .Concat(_fallbackSpeechService.GetInstalledVoices())
            .GroupBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(voice => GetEnginePriority(voice.Engine))
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsSpeaking =>
        _piperSpeechService.IsSpeaking ||
        _preferredSpeechService.IsSpeaking ||
        _fallbackSpeechService.IsSpeaking;

    public IReadOnlyList<SpeechVoice> GetInstalledVoices() => _installedVoices;

    public async Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var explicitVoiceName = request.Options.VoiceName;
        var engineOrder = BuildEngineOrder(explicitVoiceName);
        if (!string.IsNullOrWhiteSpace(explicitVoiceName) && engineOrder.Count == 0)
        {
            return SpeechResult.Failed($"Selected voice '{explicitVoiceName}' is not installed.");
        }

        SpeechResult? firstFailure = null;
        foreach (var engine in engineOrder)
        {
            var result = await engine.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Success || result.WasCancelled)
            {
                return result;
            }

            firstFailure ??= result;
            AppDiagnostics.Warn(
                "speech_fallback_engaged",
                new Dictionary<string, string?>
                {
                    ["engine"] = GetEngineName(engine),
                    ["voice"] = request.Options.VoiceName,
                    ["reason"] = result.Message
                });
        }

        return firstFailure ?? SpeechResult.Failed("Couldn't start reading.");
    }

    public async Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _piperSpeechService.CancelPrefetchAsync(cancellationToken).ConfigureAwait(false);
        await _preferredSpeechService.CancelPrefetchAsync(cancellationToken).ConfigureAwait(false);
        await _fallbackSpeechService.CancelPrefetchAsync(cancellationToken).ConfigureAwait(false);

        if (_piperSpeechService.IsSpeaking)
        {
            return await _piperSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_preferredSpeechService.IsSpeaking)
        {
            return await _preferredSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_fallbackSpeechService.IsSpeaking)
        {
            return await _fallbackSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        return SpeechResult.Completed("Speech is already stopped.");
    }

    public bool SupportsPrefetch(SpeechRequest request)
    {
        return SupportsPrefetch(request, preferredEngineName: null);
    }

    public bool SupportsPrefetch(SpeechRequest request, string? preferredEngineName)
    {
        ThrowIfDisposed();
        if (request is null)
        {
            return false;
        }

        var engineOrder = BuildEngineOrder(request.Options.VoiceName, preferredEngineName);
        if (engineOrder.Count == 0)
        {
            return false;
        }

        return SupportsPrefetch(engineOrder[0], request);
    }

    public Task<IPrefetchedSpeechClip?> PrefetchAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        return PrefetchAsync(request, preferredEngineName: null, cancellationToken);
    }

    public Task<IPrefetchedSpeechClip?> PrefetchAsync(
        SpeechRequest request,
        string? preferredEngineName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var engineOrder = BuildEngineOrder(request.Options.VoiceName, preferredEngineName);
        if (engineOrder.Count == 0)
        {
            return Task.FromResult<IPrefetchedSpeechClip?>(null);
        }

        return PrefetchAsync(engineOrder[0], request, cancellationToken);
    }

    public Task<SpeechResult> SpeakPrefetchedAsync(
        IPrefetchedSpeechClip prefetchedClip,
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (prefetchedClip is PiperSpeechService.PiperPrefetchedSpeechClip piperClip)
        {
            return _piperSpeechService.SpeakPrefetchedAsync(piperClip, request, cancellationToken);
        }

        if (prefetchedClip is WindowsNeuralSpeechService.WindowsNeuralPrefetchedSpeechClip neuralClip)
        {
            return _preferredSpeechService.SpeakPrefetchedAsync(neuralClip, request, cancellationToken);
        }

        if (prefetchedClip is SystemSpeechService.SystemPrefetchedSpeechClip systemClip)
        {
            return _fallbackSpeechService.SpeakPrefetchedAsync(systemClip, request, cancellationToken);
        }

        return Task.FromResult(SpeechResult.Failed("Prefetched speech clip is not compatible with the active speech engine."));
    }

    public async Task<SpeechResult> SpeakChunkAsync(
        SpeechRequest request,
        IPrefetchedSpeechClip? prefetchedClip,
        CancellationToken cancellationToken = default)
    {
        var chunkResult = await SpeakChunkAsync(
                request,
                prefetchedClip,
                preferredEngineName: null,
                cancellationToken)
            .ConfigureAwait(false);
        return chunkResult.SpeechResult;
    }

    public async Task<ChunkSpeechResult> SpeakChunkAsync(
        SpeechRequest request,
        IPrefetchedSpeechClip? prefetchedClip,
        string? preferredEngineName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (prefetchedClip is null)
        {
            return await SpeakWithFallbackAsync(request, preferredEngineName, cancellationToken).ConfigureAwait(false);
        }

        if (!TryResolveOwningEngine(
                prefetchedClip,
                out var owningEngine,
                out var speakPrefetchedOnOwner))
        {
            return await SpeakWithFallbackAsync(request, preferredEngineName, cancellationToken).ConfigureAwait(false);
        }

        SpeechResult? firstFailure = null;
        var owningEngineName = GetEngineName(owningEngine);

        var prefetchedResult = await speakPrefetchedOnOwner(request, cancellationToken).ConfigureAwait(false);
        if (prefetchedResult.Success || prefetchedResult.WasCancelled)
        {
            return new ChunkSpeechResult(prefetchedResult, owningEngineName);
        }

        firstFailure = prefetchedResult;
        AppDiagnostics.Warn(
            "speech_chunk_prefetch_playback_failed",
            new Dictionary<string, string?>
            {
                ["engine"] = owningEngineName,
                ["voice"] = request.Options.VoiceName,
                ["reason"] = prefetchedResult.Message
            });

        var sameEngineResult = await owningEngine.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
        if (sameEngineResult.Success || sameEngineResult.WasCancelled)
        {
            AppDiagnostics.Info(
                "speech_chunk_same_engine_fallback_engaged",
                new Dictionary<string, string?>
                {
                    ["engine"] = owningEngineName,
                    ["voice"] = request.Options.VoiceName
                });
            return new ChunkSpeechResult(sameEngineResult, owningEngineName);
        }

        AppDiagnostics.Warn(
            "speech_chunk_same_engine_fallback_failed",
            new Dictionary<string, string?>
            {
                ["engine"] = owningEngineName,
                ["voice"] = request.Options.VoiceName,
                ["reason"] = sameEngineResult.Message
            });

        return await SpeakWithFallbackAsync(
                request,
                owningEngineName,
                cancellationToken,
                excludedEngine: owningEngine,
                firstFailure: firstFailure ?? sameEngineResult,
                crossEngineFallbackFromEngineName: owningEngineName)
            .ConfigureAwait(false);
    }

    public int GetChunkPrefetchGraceWindowMilliseconds(SpeechRequest request)
    {
        return GetChunkPrefetchGraceWindowMilliseconds(request, preferredEngineName: null);
    }

    public int GetChunkPrefetchGraceWindowMilliseconds(SpeechRequest request, string? preferredEngineName)
    {
        ThrowIfDisposed();

        if (request is null)
        {
            return DefaultPrefetchGraceWindowMilliseconds;
        }

        var engineOrder = BuildEngineOrder(request.Options.VoiceName, preferredEngineName);
        if (engineOrder.Count == 0)
        {
            return DefaultPrefetchGraceWindowMilliseconds;
        }

        var baseGraceWindowMilliseconds = engineOrder[0] switch
        {
            PiperSpeechService => PiperPrefetchGraceWindowMilliseconds,
            WindowsNeuralSpeechService => WindowsOneCorePrefetchGraceWindowMilliseconds,
            SystemSpeechService => SystemSpeechPrefetchGraceWindowMilliseconds,
            _ => DefaultPrefetchGraceWindowMilliseconds
        };

        var chunkTextLength = request.Text?.Trim().Length ?? 0;
        if (chunkTextLength > 0 && chunkTextLength < ShortChunkThresholdCharacters)
        {
            return baseGraceWindowMilliseconds / 2;
        }

        return baseGraceWindowMilliseconds;
    }

    internal string? GetPrefetchedClipEngineName(IPrefetchedSpeechClip? prefetchedClip)
    {
        if (prefetchedClip is null)
        {
            return null;
        }

        return TryResolveOwningEngine(prefetchedClip, out var owningEngine, out _) ? GetEngineName(owningEngine) : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _piperSpeechService.Dispose();
        _preferredSpeechService.Dispose();
        _fallbackSpeechService.Dispose();
        _disposed = true;
    }

    private IReadOnlyList<ISpeechService> BuildEngineOrder(string? voiceName)
    {
        return BuildEngineOrder(voiceName, preferredEngineName: null);
    }

    private IReadOnlyList<ISpeechService> BuildEngineOrder(string? voiceName, string? preferredEngineName)
    {
        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            if (_piperSpeechService.SupportsVoice(voiceName))
            {
                return new[] { (ISpeechService)_piperSpeechService };
            }

            if (_preferredSpeechService.SupportsVoice(voiceName))
            {
                return new[] { (ISpeechService)_preferredSpeechService };
            }

            if (_fallbackSpeechService.SupportsVoice(voiceName))
            {
                return new[] { (ISpeechService)_fallbackSpeechService };
            }

            return Array.Empty<ISpeechService>();
        }

        var engines = new List<ISpeechService>(3);
        if (TryResolveEngine(preferredEngineName, out var preferredEngine))
        {
            AddEngine(engines, preferredEngine);
        }

        AddEngine(engines, _preferredSpeechService);
        AddEngine(engines, _fallbackSpeechService);
        if (_piperSpeechService.HasUsableInstallation &&
            (engines.Count == 0 || string.Equals(preferredEngineName, GetEngineName(_piperSpeechService), StringComparison.OrdinalIgnoreCase)))
        {
            // Last-resort fallback only when Windows engines are unavailable.
            AddEngine(engines, _piperSpeechService);
        }

        return engines;
    }

    private async Task<ChunkSpeechResult> SpeakWithFallbackAsync(
        SpeechRequest request,
        string? preferredEngineName,
        CancellationToken cancellationToken,
        ISpeechService? excludedEngine = null,
        SpeechResult? firstFailure = null,
        string? crossEngineFallbackFromEngineName = null)
    {
        var explicitVoiceName = request.Options.VoiceName;
        var engineOrder = BuildEngineOrder(explicitVoiceName, preferredEngineName);
        if (!string.IsNullOrWhiteSpace(explicitVoiceName) && engineOrder.Count == 0)
        {
            return new ChunkSpeechResult(
                SpeechResult.Failed($"Selected voice '{explicitVoiceName}' is not installed."),
                null);
        }

        foreach (var engine in engineOrder)
        {
            if (excludedEngine is not null && ReferenceEquals(engine, excludedEngine))
            {
                continue;
            }

            var result = await engine.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Success || result.WasCancelled)
            {
                var engineName = GetEngineName(engine);
                if (!string.IsNullOrWhiteSpace(crossEngineFallbackFromEngineName))
                {
                    AppDiagnostics.Info(
                        "speech_chunk_cross_engine_fallback_engaged",
                        new Dictionary<string, string?>
                        {
                            ["fromEngine"] = crossEngineFallbackFromEngineName,
                            ["toEngine"] = engineName,
                            ["voice"] = request.Options.VoiceName
                        });
                }

                return new ChunkSpeechResult(result, engineName);
            }

            firstFailure ??= result;
            AppDiagnostics.Warn(
                "speech_fallback_engaged",
                new Dictionary<string, string?>
                {
                    ["engine"] = GetEngineName(engine),
                    ["voice"] = request.Options.VoiceName,
                    ["reason"] = result.Message
                });
        }

        return new ChunkSpeechResult(firstFailure ?? SpeechResult.Failed("Couldn't start reading."), preferredEngineName);
    }

    private Task<IPrefetchedSpeechClip?> PrefetchAsync(
        ISpeechService engine,
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        return engine switch
        {
            PiperSpeechService piper => piper.PrefetchAsync(request, cancellationToken),
            WindowsNeuralSpeechService neural => neural.PrefetchAsync(request, cancellationToken),
            SystemSpeechService system => system.PrefetchAsync(request, cancellationToken),
            _ => Task.FromResult<IPrefetchedSpeechClip?>(null)
        };
    }

    private static bool SupportsPrefetch(ISpeechService engine, SpeechRequest request)
    {
        return engine switch
        {
            PiperSpeechService piper => piper.SupportsPrefetch(request),
            WindowsNeuralSpeechService neural => neural.SupportsPrefetch(request),
            SystemSpeechService system => system.SupportsPrefetch(request),
            _ => false
        };
    }

    private bool TryResolveEngine(string? engineName, out ISpeechService engine)
    {
        if (string.Equals(engineName, GetEngineName(_piperSpeechService), StringComparison.OrdinalIgnoreCase) &&
            _piperSpeechService.HasUsableInstallation)
        {
            engine = _piperSpeechService;
            return true;
        }

        if (string.Equals(engineName, GetEngineName(_preferredSpeechService), StringComparison.OrdinalIgnoreCase))
        {
            engine = _preferredSpeechService;
            return true;
        }

        if (string.Equals(engineName, GetEngineName(_fallbackSpeechService), StringComparison.OrdinalIgnoreCase))
        {
            engine = _fallbackSpeechService;
            return true;
        }

        engine = _preferredSpeechService;
        return false;
    }

    private static void AddEngine(ICollection<ISpeechService> engines, ISpeechService engine)
    {
        if (!engines.Contains(engine))
        {
            engines.Add(engine);
        }
    }

    private static string GetEngineName(ISpeechService engine)
    {
        return engine switch
        {
            PiperSpeechService => "piper",
            WindowsNeuralSpeechService => "windows_onecore",
            SystemSpeechService => "system_speech",
            _ => engine.GetType().Name
        };
    }

    private static int GetEnginePriority(string engine)
    {
        return engine switch
        {
            "Piper" => 0,
            "Windows OneCore" => 1,
            "System.Speech" => 2,
            _ => 9
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }

    private bool TryResolveOwningEngine(
        IPrefetchedSpeechClip prefetchedClip,
        out ISpeechService owningEngine,
        out Func<SpeechRequest, CancellationToken, Task<SpeechResult>> speakPrefetchedOnOwner)
    {
        if (prefetchedClip is PiperSpeechService.PiperPrefetchedSpeechClip piperClip)
        {
            owningEngine = _piperSpeechService;
            speakPrefetchedOnOwner = (request, cancellationToken) =>
                _piperSpeechService.SpeakPrefetchedAsync(piperClip, request, cancellationToken);
            return true;
        }

        if (prefetchedClip is WindowsNeuralSpeechService.WindowsNeuralPrefetchedSpeechClip neuralClip)
        {
            owningEngine = _preferredSpeechService;
            speakPrefetchedOnOwner = (request, cancellationToken) =>
                _preferredSpeechService.SpeakPrefetchedAsync(neuralClip, request, cancellationToken);
            return true;
        }

        if (prefetchedClip is SystemSpeechService.SystemPrefetchedSpeechClip systemClip)
        {
            owningEngine = _fallbackSpeechService;
            speakPrefetchedOnOwner = (request, cancellationToken) =>
                _fallbackSpeechService.SpeakPrefetchedAsync(systemClip, request, cancellationToken);
            return true;
        }

        owningEngine = _preferredSpeechService;
        speakPrefetchedOnOwner = static (_, _) => Task.FromResult(SpeechResult.Failed("Prefetched speech clip is not compatible with the active speech engine."));
        return false;
    }
}
