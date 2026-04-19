using System;
using System.Collections.Generic;
using System.Globalization;
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

    private sealed class RenderedChunkClip : IDisposable
    {
        private bool _disposed;

        public RenderedChunkClip(IPrefetchedSpeechClip prefetchedClip, byte[] waveBytes, string engineName)
        {
            PrefetchedClip = prefetchedClip;
            WaveBytes = waveBytes;
            EngineName = engineName;
        }

        public string EngineName { get; }
        public byte[] WaveBytes { get; }
        private IPrefetchedSpeechClip PrefetchedClip { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            PrefetchedClip.Dispose();
            _disposed = true;
        }
    }

    private const int PiperPrefetchGraceWindowMilliseconds = 40;
    private const int WindowsOneCorePrefetchGraceWindowMilliseconds = 15;
    private const int SystemSpeechPrefetchGraceWindowMilliseconds = 15;
    private const int DefaultPrefetchGraceWindowMilliseconds = 15;
    private const int ShortChunkThresholdCharacters = 120;
    private const int ChunkRenderMaxRetryAttemptsPinnedEngine = 3;
    private const int ChunkRenderMaxRetryAttemptsFallbackOrder = 2;
    private const int ChunkRenderRetryDelayMilliseconds = 45;

    private readonly SemaphoreSlim _gate;
    private readonly PiperSpeechService _piperSpeechService;
    private readonly WindowsNeuralSpeechService _preferredSpeechService;
    private readonly SystemSpeechService _fallbackSpeechService;
    private readonly SpeechVoice[] _installedVoices;
    private CancellationTokenSource? _continuousChunkPlaybackCancellationTokenSource;
    private ContinuousWaveOutPlayer? _continuousChunkPlaybackPlayer;
    private bool _isContinuousChunkPlaybackActive;
    private bool _disposed;

    public WindowsSpeechService()
    {
        _gate = new SemaphoreSlim(1, 1);
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
        _isContinuousChunkPlaybackActive ||
        _piperSpeechService.IsSpeaking ||
        _preferredSpeechService.IsSpeaking ||
        _fallbackSpeechService.IsSpeaking;

    public IReadOnlyList<SpeechVoice> GetInstalledVoices() => _installedVoices;

    public async Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopActiveContinuousChunkPlaybackAsync("speak_start", cancellationToken).ConfigureAwait(false);

        var explicitVoiceName = request.Options.VoiceName;
        var engineOrder = BuildEngineOrder(explicitVoiceName);
        if (!string.IsNullOrWhiteSpace(explicitVoiceName) && engineOrder.Count == 0)
        {
            return SpeechResult.Failed($"Selected voice '{explicitVoiceName}' is not installed.");
        }

        if (engineOrder.Count > 0 && ReferenceEquals(engineOrder[0], _piperSpeechService))
        {
            AppDiagnostics.Info(
                "speech_single_chunk_stream_routed",
                new Dictionary<string, string?>
                {
                    ["engine"] = GetEngineName(_piperSpeechService),
                    ["voice"] = request.Options.VoiceName,
                    ["textLength"] = GetTextLength(request.Text).ToString(CultureInfo.InvariantCulture),
                    ["reason"] = "piper_uses_continuous_stream"
                });
            return await SpeakChunkSequenceAsync(new[] { request }, cancellationToken).ConfigureAwait(false);
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

    internal async Task<SpeechResult> SpeakChunkSequenceAsync(
        IReadOnlyList<SpeechRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        if (requests.Count == 0)
        {
            return SpeechResult.Failed("Nothing to read. Enter text first.");
        }

        await StopInternalAsync("chunk_sequence_start", cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();

        var streamId = Guid.NewGuid().ToString("N");
        var playbackCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var playbackPlayer = new ContinuousWaveOutPlayer(streamId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _continuousChunkPlaybackCancellationTokenSource = playbackCancellationTokenSource;
            _continuousChunkPlaybackPlayer = playbackPlayer;
            _isContinuousChunkPlaybackActive = true;
        }
        finally
        {
            _gate.Release();
        }

        string? pinnedChunkEngineName = null;
        var lastDispatchedChunkIndex = -1;
        var nextRenderChunkIndex = requests.Count > 1 ? 1 : -1;

        try
        {
            AppDiagnostics.Info(
                "speech_chunk_stream_initializing",
                new Dictionary<string, string?>
                {
                    ["streamId"] = streamId,
                    ["chunkCount"] = requests.Count.ToString(CultureInfo.InvariantCulture),
                    ["firstChunkLength"] = GetTextLength(requests[0].Text).ToString(CultureInfo.InvariantCulture),
                    ["voice"] = requests[0].Options.VoiceName,
                    ["rate"] = requests[0].Options.Rate.ToString(CultureInfo.InvariantCulture)
                });

            using var firstChunk = await RenderChunkAsync(
                    requests[0],
                    0,
                    streamId,
                    preferredEngineName: null,
                    allowEngineFallback: true,
                    playbackCancellationTokenSource.Token)
                .ConfigureAwait(false);
            pinnedChunkEngineName = firstChunk.EngineName;

            AppDiagnostics.Info(
                "speech_chunk_stream_started",
                new Dictionary<string, string?>
                {
                    ["streamId"] = streamId,
                    ["chunkCount"] = requests.Count.ToString(CultureInfo.InvariantCulture),
                    ["engine"] = pinnedChunkEngineName,
                    ["voice"] = requests[0].Options.VoiceName,
                    ["rate"] = requests[0].Options.Rate.ToString(CultureInfo.InvariantCulture),
                    ["firstChunkLength"] = GetTextLength(requests[0].Text).ToString(CultureInfo.InvariantCulture),
                    ["firstChunkWaveBytes"] = firstChunk.WaveBytes.Length.ToString(CultureInfo.InvariantCulture),
                    ["pinned"] = (pinnedChunkEngineName is not null).ToString()
                });

            if (string.Equals(pinnedChunkEngineName, GetEngineName(_piperSpeechService), StringComparison.OrdinalIgnoreCase))
            {
                var warmupWaveBytes = await _piperSpeechService
                    .CreateExternalOutputDeviceWarmupWaveAsync(
                        firstChunk.WaveBytes,
                        requests[0].Options,
                        streamId,
                        playbackCancellationTokenSource.Token)
                    .ConfigureAwait(false);
                if (warmupWaveBytes.Length > 0)
                {
                    await playbackPlayer.EnqueueWaveAsync(warmupWaveBytes, playbackCancellationTokenSource.Token).ConfigureAwait(false);
                }
            }

            Task<RenderedChunkClip>? nextChunkRenderTask = null;
            if (requests.Count > 1)
            {
                nextChunkRenderTask = StartPinnedChunkRenderTask(
                    requests[1],
                    1,
                    streamId,
                    pinnedChunkEngineName,
                    playbackCancellationTokenSource.Token);
            }

            await EnqueueRenderedChunkAsync(
                    playbackPlayer,
                    firstChunk,
                    requests[0],
                    0,
                    requests.Count,
                    streamId,
                    playbackCancellationTokenSource.Token)
                .ConfigureAwait(false);
            lastDispatchedChunkIndex = 0;

            for (var index = 1; index < requests.Count; index++)
            {
                if (nextChunkRenderTask is null)
                {
                    return SpeechResult.Failed("Couldn't continue reading.");
                }

                using var renderedChunk = await nextChunkRenderTask.ConfigureAwait(false);
                if (index + 1 < requests.Count)
                {
                    nextRenderChunkIndex = index + 1;
                    nextChunkRenderTask = StartPinnedChunkRenderTask(
                        requests[index + 1],
                        index + 1,
                        streamId,
                        pinnedChunkEngineName,
                        playbackCancellationTokenSource.Token);
                }
                else
                {
                    nextChunkRenderTask = null;
                }

                await EnqueueRenderedChunkAsync(
                        playbackPlayer,
                        renderedChunk,
                        requests[index],
                        index,
                        requests.Count,
                        streamId,
                        playbackCancellationTokenSource.Token)
                    .ConfigureAwait(false);
                lastDispatchedChunkIndex = index;
            }

            await playbackPlayer.DrainAsync(playbackCancellationTokenSource.Token).ConfigureAwait(false);

            if (string.Equals(pinnedChunkEngineName, GetEngineName(_piperSpeechService), StringComparison.OrdinalIgnoreCase))
            {
                await _piperSpeechService.NotifyExternalPlaybackCompletedAsync(CancellationToken.None).ConfigureAwait(false);
            }

            AppDiagnostics.Info(
                "speech_chunk_stream_completed",
                new Dictionary<string, string?>
                {
                    ["streamId"] = streamId,
                    ["chunkCount"] = requests.Count.ToString(CultureInfo.InvariantCulture),
                    ["engine"] = pinnedChunkEngineName,
                    ["voice"] = requests[0].Options.VoiceName,
                    ["pendingPlayback"] = "0"
                });

            return SpeechResult.Completed();
        }
        catch (OperationCanceledException) when (playbackCancellationTokenSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "speech_chunk_continuous_playback_failed",
                new Dictionary<string, string?>
                {
                    ["streamId"] = streamId,
                    ["message"] = ex.Message,
                    ["engine"] = pinnedChunkEngineName,
                    ["chunkCount"] = requests.Count.ToString(CultureInfo.InvariantCulture),
                    ["voice"] = requests[0].Options.VoiceName,
                    ["lastDispatchedChunkIndex"] = lastDispatchedChunkIndex.ToString(CultureInfo.InvariantCulture),
                    ["nextRenderChunkIndex"] = nextRenderChunkIndex.ToString(CultureInfo.InvariantCulture),
                    ["lastKnownChunkIndex"] = Math.Max(0, requests.Count - 1).ToString(CultureInfo.InvariantCulture)
                });
            playbackPlayer.Stop();
            return SpeechResult.Failed("Couldn't continue reading.");
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_continuousChunkPlaybackCancellationTokenSource, playbackCancellationTokenSource))
                {
                    _continuousChunkPlaybackCancellationTokenSource = null;
                }

                if (ReferenceEquals(_continuousChunkPlaybackPlayer, playbackPlayer))
                {
                    _continuousChunkPlaybackPlayer = null;
                }

                _isContinuousChunkPlaybackActive = false;
            }
            finally
            {
                _gate.Release();
            }

            playbackPlayer.Dispose();
            playbackCancellationTokenSource.Dispose();
        }
    }

    public Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
    {
        return StopInternalAsync("explicit_stop", cancellationToken);
    }

    private async Task<SpeechResult> StopInternalAsync(string reason, CancellationToken cancellationToken)
    {
        AppDiagnostics.Info(
            "windows_speech_stop_requested",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["continuousChunkPlaybackActive"] = _isContinuousChunkPlaybackActive.ToString(),
                ["piperSpeaking"] = _piperSpeechService.IsSpeaking.ToString(),
                ["windowsNeuralSpeaking"] = _preferredSpeechService.IsSpeaking.ToString(),
                ["systemSpeechSpeaking"] = _fallbackSpeechService.IsSpeaking.ToString()
            });

        try
        {
            ThrowIfDisposed();
            var stoppedContinuousChunkPlayback = await StopActiveContinuousChunkPlaybackAsync(reason, cancellationToken).ConfigureAwait(false);
            await _piperSpeechService.CancelPrefetchAsync(cancellationToken).ConfigureAwait(false);
            await _preferredSpeechService.CancelPrefetchAsync(cancellationToken).ConfigureAwait(false);
            await _fallbackSpeechService.CancelPrefetchAsync(cancellationToken).ConfigureAwait(false);

            if (_piperSpeechService.IsSpeaking)
            {
                var piperResult = await _piperSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
                AppDiagnostics.Info(
                    "windows_speech_stop_completed",
                    new Dictionary<string, string?>
                    {
                        ["reason"] = reason,
                        ["stopPath"] = "piper_engine",
                        ["success"] = piperResult.Success.ToString(),
                        ["cancelled"] = piperResult.WasCancelled.ToString(),
                        ["message"] = piperResult.Message
                    });
                return piperResult;
            }

            if (_preferredSpeechService.IsSpeaking)
            {
                var windowsNeuralResult = await _preferredSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
                AppDiagnostics.Info(
                    "windows_speech_stop_completed",
                    new Dictionary<string, string?>
                    {
                        ["reason"] = reason,
                        ["stopPath"] = "windows_neural_engine",
                        ["success"] = windowsNeuralResult.Success.ToString(),
                        ["cancelled"] = windowsNeuralResult.WasCancelled.ToString(),
                        ["message"] = windowsNeuralResult.Message
                    });
                return windowsNeuralResult;
            }

            if (_fallbackSpeechService.IsSpeaking)
            {
                var systemResult = await _fallbackSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
                AppDiagnostics.Info(
                    "windows_speech_stop_completed",
                    new Dictionary<string, string?>
                    {
                        ["reason"] = reason,
                        ["stopPath"] = "system_engine",
                        ["success"] = systemResult.Success.ToString(),
                        ["cancelled"] = systemResult.WasCancelled.ToString(),
                        ["message"] = systemResult.Message
                    });
                return systemResult;
            }

            if (stoppedContinuousChunkPlayback)
            {
                AppDiagnostics.Info(
                    "windows_speech_stop_completed",
                    new Dictionary<string, string?>
                    {
                        ["reason"] = reason,
                        ["stopPath"] = "continuous_chunk_playback",
                        ["success"] = bool.TrueString,
                        ["cancelled"] = bool.TrueString,
                        ["message"] = "Reading stopped."
                    });
                return SpeechResult.Stopped();
            }

            AppDiagnostics.Info(
                "windows_speech_stop_completed",
                new Dictionary<string, string?>
                {
                    ["reason"] = reason,
                    ["stopPath"] = "already_stopped",
                    ["success"] = bool.TrueString,
                    ["cancelled"] = bool.FalseString,
                    ["message"] = "Speech is already stopped."
                });
            return SpeechResult.Completed("Speech is already stopped.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppDiagnostics.Warn(
                "windows_speech_stop_cancelled",
                new Dictionary<string, string?>
                {
                    ["reason"] = reason
                });
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "windows_speech_stop_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return SpeechResult.Failed("Couldn't stop reading. Please try again.");
        }
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
        return SpeakPrefetchedInternalAsync(prefetchedClip, request, cancellationToken);
    }

    private async Task<SpeechResult> SpeakPrefetchedInternalAsync(
        IPrefetchedSpeechClip prefetchedClip,
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        await StopActiveContinuousChunkPlaybackAsync("speak_prefetched_start", cancellationToken).ConfigureAwait(false);

        if (prefetchedClip is PiperSpeechService.PiperPrefetchedSpeechClip piperClip)
        {
            return await _piperSpeechService.SpeakPrefetchedAsync(piperClip, request, cancellationToken).ConfigureAwait(false);
        }

        if (prefetchedClip is WindowsNeuralSpeechService.WindowsNeuralPrefetchedSpeechClip neuralClip)
        {
            return await _preferredSpeechService.SpeakPrefetchedAsync(neuralClip, request, cancellationToken).ConfigureAwait(false);
        }

        if (prefetchedClip is SystemSpeechService.SystemPrefetchedSpeechClip systemClip)
        {
            return await _fallbackSpeechService.SpeakPrefetchedAsync(systemClip, request, cancellationToken).ConfigureAwait(false);
        }

        return SpeechResult.Failed("Prefetched speech clip is not compatible with the active speech engine.");
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
        await StopActiveContinuousChunkPlaybackAsync("speak_chunk_start", cancellationToken).ConfigureAwait(false);

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

        CancellationTokenSource? continuousChunkPlaybackCancellationTokenSource;
        ContinuousWaveOutPlayer? continuousChunkPlaybackPlayer;
        _gate.Wait();
        try
        {
            continuousChunkPlaybackCancellationTokenSource = _continuousChunkPlaybackCancellationTokenSource;
            continuousChunkPlaybackPlayer = _continuousChunkPlaybackPlayer;
            _continuousChunkPlaybackCancellationTokenSource = null;
            _continuousChunkPlaybackPlayer = null;
            _isContinuousChunkPlaybackActive = false;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            continuousChunkPlaybackCancellationTokenSource?.Cancel();
        }
        catch
        {
            // Best effort only.
        }

        continuousChunkPlaybackPlayer?.Stop();
        continuousChunkPlaybackPlayer?.Dispose();
        continuousChunkPlaybackCancellationTokenSource?.Dispose();

        _piperSpeechService.Dispose();
        _preferredSpeechService.Dispose();
        _fallbackSpeechService.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    private async Task<bool> StopActiveContinuousChunkPlaybackAsync(string reason, CancellationToken cancellationToken)
    {
        CancellationTokenSource? continuousChunkPlaybackCancellationTokenSource;
        ContinuousWaveOutPlayer? continuousChunkPlaybackPlayer;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            continuousChunkPlaybackCancellationTokenSource = _continuousChunkPlaybackCancellationTokenSource;
            continuousChunkPlaybackPlayer = _continuousChunkPlaybackPlayer;
            _continuousChunkPlaybackCancellationTokenSource = null;
            _continuousChunkPlaybackPlayer = null;
            _isContinuousChunkPlaybackActive = false;
        }
        finally
        {
            _gate.Release();
        }

        if (continuousChunkPlaybackCancellationTokenSource is null &&
            continuousChunkPlaybackPlayer is null)
        {
            return false;
        }

        AppDiagnostics.Info(
            "speech_chunk_stream_stop_requested",
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["hadCancellationTokenSource"] = (continuousChunkPlaybackCancellationTokenSource is not null).ToString(),
                ["hadPlaybackPlayer"] = (continuousChunkPlaybackPlayer is not null).ToString()
            });

        try
        {
            continuousChunkPlaybackCancellationTokenSource?.Cancel();
        }
        catch
        {
            // Best effort only.
        }

        continuousChunkPlaybackPlayer?.Stop();
        return true;
    }

    private async Task<RenderedChunkClip> RenderChunkAsync(
        SpeechRequest request,
        int chunkIndex,
        string streamId,
        string? preferredEngineName,
        bool allowEngineFallback,
        CancellationToken cancellationToken)
    {
        var textLength = GetTextLength(request.Text);
        var engineOrder = allowEngineFallback
            ? BuildEngineOrder(request.Options.VoiceName, preferredEngineName)
            : BuildPinnedEngineOrder(request.Options.VoiceName, preferredEngineName);
        if (!string.IsNullOrWhiteSpace(request.Options.VoiceName) && engineOrder.Count == 0)
        {
            throw new InvalidOperationException($"Selected voice '{request.Options.VoiceName}' is not installed.");
        }

        if (engineOrder.Count == 0)
        {
            throw new InvalidOperationException("Couldn't determine a speech engine for chunked playback.");
        }

        Exception? firstFailure = null;
        string? successfulFallbackEngineName = null;
        foreach (var engine in engineOrder)
        {
            var engineName = GetEngineName(engine);
            if (!SupportsPrefetch(engine, request))
            {
                AppDiagnostics.Info(
                    "speech_chunk_render_skipped",
                    new Dictionary<string, string?>
                    {
                        ["streamId"] = streamId,
                        ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                        ["engine"] = engineName,
                        ["voice"] = request.Options.VoiceName,
                        ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                        ["reason"] = "prefetch_not_supported",
                        ["allowFallback"] = allowEngineFallback.ToString()
                    });
                continue;
            }

            var maxRetryAttempts = allowEngineFallback
                ? ChunkRenderMaxRetryAttemptsFallbackOrder
                : ChunkRenderMaxRetryAttemptsPinnedEngine;
            for (var attempt = 1; attempt <= maxRetryAttempts; attempt++)
            {
                try
                {
                    AppDiagnostics.Info(
                        "speech_chunk_render_attempt",
                        new Dictionary<string, string?>
                        {
                            ["streamId"] = streamId,
                            ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                            ["engine"] = engineName,
                            ["voice"] = request.Options.VoiceName,
                            ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                            ["preferredEngine"] = preferredEngineName,
                            ["allowFallback"] = allowEngineFallback.ToString(),
                            ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                            ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                        });

                    var prefetchedClip = await PrefetchAsync(engine, request, cancellationToken).ConfigureAwait(false);
                    if (prefetchedClip is null)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            AppDiagnostics.Info(
                                "speech_chunk_render_cancelled_before_clip",
                                new Dictionary<string, string?>
                                {
                                    ["streamId"] = streamId,
                                    ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                    ["engine"] = engineName,
                                    ["voice"] = request.Options.VoiceName,
                                    ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                                    ["preferredEngine"] = preferredEngineName,
                                    ["allowFallback"] = allowEngineFallback.ToString(),
                                    ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                                    ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                                });
                            throw new OperationCanceledException(cancellationToken);
                        }

                        AppDiagnostics.Warn(
                            "speech_chunk_render_no_clip",
                            new Dictionary<string, string?>
                            {
                                ["streamId"] = streamId,
                                ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                ["engine"] = engineName,
                                ["voice"] = request.Options.VoiceName,
                                ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                                ["preferredEngine"] = preferredEngineName,
                                ["allowFallback"] = allowEngineFallback.ToString(),
                                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                                ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                            });

                        if (attempt < maxRetryAttempts)
                        {
                            AppDiagnostics.Warn(
                                "speech_chunk_render_retry_scheduled",
                                new Dictionary<string, string?>
                                {
                                    ["streamId"] = streamId,
                                    ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                    ["engine"] = engineName,
                                    ["voice"] = request.Options.VoiceName,
                                    ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                                    ["nextAttempt"] = (attempt + 1).ToString(CultureInfo.InvariantCulture),
                                    ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture),
                                    ["reason"] = "prefetch_returned_no_clip",
                                    ["retryDelayMs"] = ChunkRenderRetryDelayMilliseconds.ToString(CultureInfo.InvariantCulture)
                                });
                            await Task.Delay(ChunkRenderRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        continue;
                    }

                    if (TryCreateRenderedChunkClip(prefetchedClip, engine, out var renderedChunk))
                    {
                        if (!string.IsNullOrWhiteSpace(successfulFallbackEngineName))
                        {
                            AppDiagnostics.Info(
                                "speech_chunk_render_fallback_engaged",
                                new Dictionary<string, string?>
                                {
                                    ["streamId"] = streamId,
                                    ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                    ["fromEngine"] = successfulFallbackEngineName,
                                    ["toEngine"] = renderedChunk.EngineName,
                                    ["voice"] = request.Options.VoiceName
                                });
                        }

                        AppDiagnostics.Info(
                            "speech_chunk_render_succeeded",
                            new Dictionary<string, string?>
                            {
                                ["streamId"] = streamId,
                                ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                ["engine"] = renderedChunk.EngineName,
                                ["voice"] = request.Options.VoiceName,
                                ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                                ["waveBytes"] = renderedChunk.WaveBytes.Length.ToString(CultureInfo.InvariantCulture),
                                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                                ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                            });

                        return renderedChunk;
                    }

                    AppDiagnostics.Warn(
                        "speech_chunk_render_clip_unrecognized",
                        new Dictionary<string, string?>
                        {
                            ["streamId"] = streamId,
                            ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                            ["engine"] = engineName,
                            ["voice"] = request.Options.VoiceName,
                            ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                            ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                            ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                        });

                    prefetchedClip.Dispose();
                    if (attempt < maxRetryAttempts)
                    {
                        AppDiagnostics.Warn(
                            "speech_chunk_render_retry_scheduled",
                            new Dictionary<string, string?>
                            {
                                ["streamId"] = streamId,
                                ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                ["engine"] = engineName,
                                ["voice"] = request.Options.VoiceName,
                                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                                ["nextAttempt"] = (attempt + 1).ToString(CultureInfo.InvariantCulture),
                                ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture),
                                ["reason"] = "clip_unrecognized",
                                ["retryDelayMs"] = ChunkRenderRetryDelayMilliseconds.ToString(CultureInfo.InvariantCulture)
                            });
                        await Task.Delay(ChunkRenderRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    AppDiagnostics.Info(
                        "speech_chunk_render_cancelled",
                        new Dictionary<string, string?>
                        {
                            ["streamId"] = streamId,
                            ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                            ["engine"] = engineName,
                            ["voice"] = request.Options.VoiceName,
                            ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                            ["preferredEngine"] = preferredEngineName,
                            ["allowFallback"] = allowEngineFallback.ToString(),
                            ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                            ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                        });
                    throw;
                }
                catch (Exception ex)
                {
                    firstFailure ??= ex;
                    successfulFallbackEngineName ??= engineName;
                    AppDiagnostics.Warn(
                        "speech_chunk_render_failed",
                        new Dictionary<string, string?>
                        {
                            ["streamId"] = streamId,
                            ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                            ["engine"] = engineName,
                            ["voice"] = request.Options.VoiceName,
                            ["reason"] = ex.Message,
                            ["textLength"] = textLength.ToString(CultureInfo.InvariantCulture),
                            ["preferredEngine"] = preferredEngineName,
                            ["allowFallback"] = allowEngineFallback.ToString(),
                            ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                            ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture)
                        });

                    if (attempt < maxRetryAttempts)
                    {
                        AppDiagnostics.Warn(
                            "speech_chunk_render_retry_scheduled",
                            new Dictionary<string, string?>
                            {
                                ["streamId"] = streamId,
                                ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                                ["engine"] = engineName,
                                ["voice"] = request.Options.VoiceName,
                                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture),
                                ["nextAttempt"] = (attempt + 1).ToString(CultureInfo.InvariantCulture),
                                ["maxAttempts"] = maxRetryAttempts.ToString(CultureInfo.InvariantCulture),
                                ["reason"] = "prefetch_exception",
                                ["retryDelayMs"] = ChunkRenderRetryDelayMilliseconds.ToString(CultureInfo.InvariantCulture),
                                ["error"] = ex.Message
                            });
                        await Task.Delay(ChunkRenderRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                break;
            }
        }

        if (firstFailure is not null)
        {
            throw new InvalidOperationException(firstFailure.Message, firstFailure);
        }

        throw new InvalidOperationException("Couldn't render speech for chunked playback.");
    }

    private Task<RenderedChunkClip> StartPinnedChunkRenderTask(
        SpeechRequest request,
        int chunkIndex,
        string streamId,
        string? pinnedChunkEngineName,
        CancellationToken cancellationToken)
    {
        return RenderChunkAsync(
            request,
            chunkIndex,
            streamId,
            pinnedChunkEngineName,
            allowEngineFallback: false,
            cancellationToken);
    }

    private static bool TryCreateRenderedChunkClip(
        IPrefetchedSpeechClip prefetchedClip,
        ISpeechService owningEngine,
        out RenderedChunkClip renderedChunk)
    {
        switch (prefetchedClip)
        {
            case PiperSpeechService.PiperPrefetchedSpeechClip piperClip:
                renderedChunk = new RenderedChunkClip(prefetchedClip, piperClip.WaveBytes, GetEngineName(owningEngine));
                return true;
            case WindowsNeuralSpeechService.WindowsNeuralPrefetchedSpeechClip neuralClip:
                renderedChunk = new RenderedChunkClip(prefetchedClip, neuralClip.WaveBytes, GetEngineName(owningEngine));
                return true;
            case SystemSpeechService.SystemPrefetchedSpeechClip systemClip:
                renderedChunk = new RenderedChunkClip(prefetchedClip, systemClip.WaveBytes, GetEngineName(owningEngine));
                return true;
            default:
                renderedChunk = null!;
                return false;
        }
    }

    private static async Task EnqueueRenderedChunkAsync(
        ContinuousWaveOutPlayer playbackPlayer,
        RenderedChunkClip renderedChunk,
        SpeechRequest request,
        int chunkIndex,
        int chunkCount,
        string streamId,
        CancellationToken cancellationToken)
    {
        AppDiagnostics.Info(
            "speech_chunk_dispatch",
            new Dictionary<string, string?>
            {
                ["streamId"] = streamId,
                ["chunkIndex"] = chunkIndex.ToString(CultureInfo.InvariantCulture),
                ["chunkCount"] = chunkCount.ToString(CultureInfo.InvariantCulture),
                ["chunkTextLength"] = GetTextLength(request.Text).ToString(CultureInfo.InvariantCulture),
                ["voice"] = request.Options.VoiceName,
                ["primerOverrideSeconds"] = request.Options.LeadingPrimerSecondsOverride?.ToString("0.00", CultureInfo.InvariantCulture) ?? "default",
                ["mode"] = "continuous_stream",
                ["engine"] = renderedChunk.EngineName,
                ["isContinuationChunk"] = request.Options.IsContinuationChunk.ToString()
            });

        await playbackPlayer.EnqueueWaveAsync(renderedChunk.WaveBytes, cancellationToken).ConfigureAwait(false);
    }

    private static int GetTextLength(string? text)
    {
        return text?.Length ?? 0;
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

    private IReadOnlyList<ISpeechService> BuildPinnedEngineOrder(string? voiceName, string? preferredEngineName)
    {
        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            return BuildEngineOrder(voiceName, preferredEngineName);
        }

        return TryResolveEngine(preferredEngineName, out var pinnedEngine)
            ? new[] { pinnedEngine }
            : Array.Empty<ISpeechService>();
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
