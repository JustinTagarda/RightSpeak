using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class ReadingService : IReadingService
{
    private sealed class PendingChunkPrefetch
    {
        private int _faultObserved;

        public PendingChunkPrefetch(
            int chunkIndex,
            SpeechRequest request,
            string? preferredEngineName,
            CancellationTokenSource cancellationTokenSource,
            Task<IPrefetchedSpeechClip?> task)
        {
            ChunkIndex = chunkIndex;
            Request = request;
            PreferredEngineName = preferredEngineName;
            CancellationTokenSource = cancellationTokenSource;
            Task = task;
        }

        public int ChunkIndex { get; }
        public SpeechRequest Request { get; }
        public string? PreferredEngineName { get; }
        public CancellationTokenSource CancellationTokenSource { get; }
        public Task<IPrefetchedSpeechClip?> Task { get; }

        public bool TryMarkFaultObserved()
        {
            return Interlocked.Exchange(ref _faultObserved, 1) == 0;
        }
    }

    private const int ChunkMinCharacters = 160;
    private const int ChunkTargetCharacters = 220;
    private const int ChunkMaxCharacters = 300;
    private const double ContinuationPrimerWindowsOneCoreSeconds = 0.0;
    private const double ContinuationPrimerSystemSpeechSeconds = 0.0;
    private const double ContinuationPrimerPiperSeconds = 1.0;
    private const double ContinuationPrimerUnknownEngineSeconds = 1.0;
    private const string PiperEngineName = "Piper";
    private const string PreferredPiperLjspeechVoiceName = "piper:en_US-ljspeech-high";

    private readonly ISpeechService _speechService;
    private readonly ISelectedTextRetrievalService _selectedTextRetrievalService;
    private readonly IParagraphTextRetrievalService _paragraphTextRetrievalService;
    private readonly IDocumentTextRetrievalService _documentTextRetrievalService;
    private readonly IAppSettingsService _settingsService;
    private readonly IReadOnlyList<SpeechVoice> _availableVoices;

    public ReadingService(
        ISpeechService speechService,
        ISelectedTextRetrievalService selectedTextRetrievalService,
        IParagraphTextRetrievalService paragraphTextRetrievalService,
        IDocumentTextRetrievalService documentTextRetrievalService,
        IAppSettingsService settingsService)
    {
        _speechService = speechService;
        _selectedTextRetrievalService = selectedTextRetrievalService;
        _paragraphTextRetrievalService = paragraphTextRetrievalService;
        _documentTextRetrievalService = documentTextRetrievalService;
        _settingsService = settingsService;
        _availableVoices = _speechService.GetInstalledVoices();
        NormalizeSavedVoiceSetting();
        EnsureDefaultVoiceSelectionForEmptyStorage();
    }

    public bool IsReading => _speechService.IsSpeaking;
    public IReadOnlyList<SpeechVoice> AvailableVoices => _availableVoices;
    public int SpeechRate
    {
        get => _settingsService.Current.SpeechRate;
        set
        {
            var clamped = value < -10 ? -10 : value > 10 ? 10 : value;
            if (_settingsService.Current.SpeechRate == clamped)
            {
                return;
            }

            _settingsService.Current.SpeechRate = clamped;
            _settingsService.Save();
        }
    }
    public string? SelectedVoiceName
    {
        get => NormalizeVoiceName(_settingsService.Current.VoiceName);
        set
        {
            var normalized = NormalizeVoiceName(value);
            if (string.Equals(_settingsService.Current.VoiceName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Current.VoiceName = normalized;
            _settingsService.Save();
        }
    }

    public string TypedTextDraft
    {
        get => _settingsService.Current.TypedTextDraft ?? string.Empty;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_settingsService.Current.TypedTextDraft, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Current.TypedTextDraft = normalized;
        }
    }

    public Task<SpeechResult> ReadTextAsync(string text, CancellationToken cancellationToken = default)
    {
        return SpeakWithChunkingIfNeededAsync(text, cancellationToken);
    }

    public async Task<SpeechResult> ReadSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var readStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "focused_read_selected_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["voice"] = _settingsService.Current.VoiceName,
                ["rate"] = _settingsService.Current.SpeechRate.ToString()
            });

        var retrievalStopwatch = Stopwatch.StartNew();
        var retrieval = await _selectedTextRetrievalService.RetrieveSelectedTextAsync(cancellationToken).ConfigureAwait(false);
        retrievalStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_selected_retrieval_result",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["success"] = retrieval.Success.ToString(),
                ["source"] = retrieval.Source?.ToString(),
                ["message"] = retrieval.Message,
                ["textLength"] = retrieval.Text?.Length.ToString(),
                ["textPreview"] = BuildPreview(retrieval.Text),
                ["elapsedMs"] = retrievalStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            readStopwatch.Stop();
            AppDiagnostics.Warn(
                "focused_read_selected_failed_before_speech",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["reason"] = retrieval.Message,
                    ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
                });
            return SpeechResult.Failed(BuildSelectedTextFailureMessage());
        }

        var chunkCount = EstimateSpeechChunkCount(retrieval.Text);
        var speechStopwatch = Stopwatch.StartNew();
        var speechResult = await SpeakWithChunkingIfNeededAsync(retrieval.Text, cancellationToken).ConfigureAwait(false);
        speechStopwatch.Stop();
        readStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_selected_speech_result",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["success"] = speechResult.Success.ToString(),
                ["cancelled"] = speechResult.WasCancelled.ToString(),
                ["message"] = speechResult.Message,
                ["chunkCount"] = chunkCount.ToString(),
                ["speechElapsedMs"] = speechStopwatch.ElapsedMilliseconds.ToString(),
                ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    public Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
    {
        return _speechService.StopAsync(cancellationToken);
    }

    public async Task<SpeechResult> ReadParagraphAsync(CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var readStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "focused_read_paragraph_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["voice"] = _settingsService.Current.VoiceName,
                ["rate"] = _settingsService.Current.SpeechRate.ToString()
            });

        var retrievalStopwatch = Stopwatch.StartNew();
        var retrieval = await _paragraphTextRetrievalService.RetrieveParagraphTextAsync(cancellationToken).ConfigureAwait(false);
        var retried = false;
        if ((!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text)) && ShouldRetryParagraphRetrieval(retrieval))
        {
            retried = true;
            AppDiagnostics.Info(
                "focused_read_paragraph_retry_scheduled",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["firstAttemptSource"] = retrieval.Source?.ToString(),
                    ["firstAttemptMessage"] = retrieval.Message
                });
            await Task.Delay(220, cancellationToken).ConfigureAwait(false);
            retrieval = await _paragraphTextRetrievalService.RetrieveParagraphTextAsync(cancellationToken).ConfigureAwait(false);
        }
        retrievalStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_paragraph_retrieval_result",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["success"] = retrieval.Success.ToString(),
                ["source"] = retrieval.Source?.ToString(),
                ["message"] = retrieval.Message,
                ["textLength"] = retrieval.Text?.Length.ToString(),
                ["textPreview"] = BuildPreview(retrieval.Text),
                ["retried"] = retried.ToString(),
                ["elapsedMs"] = retrievalStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            readStopwatch.Stop();
            AppDiagnostics.Warn(
                "focused_read_paragraph_failed_before_speech",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["reason"] = retrieval.Message,
                    ["retried"] = retried.ToString(),
                    ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
                });
            return SpeechResult.Failed(BuildParagraphFailureMessage());
        }

        var chunkCount = EstimateSpeechChunkCount(retrieval.Text);
        var speechStopwatch = Stopwatch.StartNew();
        var speechResult = await SpeakWithChunkingIfNeededAsync(retrieval.Text, cancellationToken).ConfigureAwait(false);
        speechStopwatch.Stop();
        readStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_paragraph_speech_result",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["success"] = speechResult.Success.ToString(),
                ["cancelled"] = speechResult.WasCancelled.ToString(),
                ["message"] = speechResult.Message,
                ["chunkCount"] = chunkCount.ToString(),
                ["speechElapsedMs"] = speechStopwatch.ElapsedMilliseconds.ToString(),
                ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    public async Task<SpeechResult> ReadDocumentAsync(CancellationToken cancellationToken = default)
    {
        var readStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "focused_read_document_started",
            new Dictionary<string, string?>
            {
                ["voice"] = _settingsService.Current.VoiceName,
                ["rate"] = _settingsService.Current.SpeechRate.ToString()
            });

        var retrievalStopwatch = Stopwatch.StartNew();
        var retrieval = await _documentTextRetrievalService.RetrieveDocumentTextAsync(cancellationToken).ConfigureAwait(false);
        retrievalStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_document_retrieval_result",
            new Dictionary<string, string?>
            {
                ["success"] = retrieval.Success.ToString(),
                ["source"] = retrieval.Source?.ToString(),
                ["message"] = retrieval.Message,
                ["textLength"] = retrieval.Text?.Length.ToString(),
                ["elapsedMs"] = retrievalStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            readStopwatch.Stop();
            AppDiagnostics.Warn(
                "focused_read_document_failed_before_speech",
                new Dictionary<string, string?>
                {
                    ["reason"] = retrieval.Message,
                    ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
                });
            return SpeechResult.Failed(BuildDocumentFailureMessage());
        }

        var chunkCount = EstimateSpeechChunkCount(retrieval.Text);
        var speechStopwatch = Stopwatch.StartNew();
        var speechResult = await SpeakWithChunkingIfNeededAsync(retrieval.Text, cancellationToken).ConfigureAwait(false);
        speechStopwatch.Stop();
        readStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_document_speech_result",
            new Dictionary<string, string?>
            {
                ["success"] = speechResult.Success.ToString(),
                ["cancelled"] = speechResult.WasCancelled.ToString(),
                ["message"] = speechResult.Message,
                ["chunkCount"] = chunkCount.ToString(),
                ["speechElapsedMs"] = speechStopwatch.ElapsedMilliseconds.ToString(),
                ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    private SpeechRequest BuildRequest(string text)
    {
        return BuildRequest(text, leadingPrimerSecondsOverride: null);
    }

    private SpeechRequest BuildRequest(string text, double? leadingPrimerSecondsOverride)
    {
        return BuildRequest(
            text,
            leadingPrimerSecondsOverride,
            isContinuationChunk: false,
            allowOutputDeviceWarmup: true,
            useFullPrimerWarmupCarrier: true);
    }

    private SpeechRequest BuildRequest(
        string text,
        double? leadingPrimerSecondsOverride,
        bool isContinuationChunk,
        bool allowOutputDeviceWarmup,
        bool useFullPrimerWarmupCarrier)
    {
        var options = new SpeechOptions
        {
            Rate = _settingsService.Current.SpeechRate,
            VoiceName = _settingsService.Current.VoiceName,
            LeadingPrimerSecondsOverride = leadingPrimerSecondsOverride,
            IsContinuationChunk = isContinuationChunk,
            AllowOutputDeviceWarmup = allowOutputDeviceWarmup,
            UseFullPrimerWarmupCarrier = useFullPrimerWarmupCarrier
        };

        return new SpeechRequest(text, options);
    }

    private async Task<SpeechResult> SpeakWithChunkingIfNeededAsync(string text, CancellationToken cancellationToken)
    {
        var normalizedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return SpeechResult.Failed("Nothing to read. Enter text first.");
        }

        if (normalizedText.Length <= ChunkMaxCharacters)
        {
            return await _speechService.SpeakAsync(BuildRequest(normalizedText), cancellationToken).ConfigureAwait(false);
        }

        var chunks = SplitIntoSpeechChunks(normalizedText, ChunkMinCharacters, ChunkTargetCharacters, ChunkMaxCharacters);
        if (chunks.Count <= 1)
        {
            return await _speechService.SpeakAsync(BuildRequest(normalizedText), cancellationToken).ConfigureAwait(false);
        }

        var prefetchSpeechService = _speechService as IPrefetchSpeechService;
        var windowsSpeechService = _speechService as WindowsSpeechService;
        PendingChunkPrefetch? pendingPrefetch = null;
        string? pinnedChunkEngineName = null;

        try
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = chunks[index];
                var isContinuationChunk = index > 0;
                double? primerOverride = isContinuationChunk
                    ? ResolveContinuationPrimerSeconds(pinnedChunkEngineName)
                    : null;
                var request = BuildRequest(
                    chunk,
                    primerOverride,
                    isContinuationChunk: isContinuationChunk,
                    allowOutputDeviceWarmup: !isContinuationChunk,
                    useFullPrimerWarmupCarrier: !isContinuationChunk);

                IPrefetchedSpeechClip? prefetchedClip = null;
                var usingPrefetchedClip = false;

                if (pendingPrefetch is not null && pendingPrefetch.ChunkIndex == index)
                {
                    var prefetchGraceWindowMilliseconds = GetPrefetchGraceWindowMilliseconds(
                        pendingPrefetch.Request,
                        pendingPrefetch.PreferredEngineName);
                    prefetchedClip = await TryTakeReadyPrefetchedClipAsync(
                            pendingPrefetch,
                            prefetchGraceWindowMilliseconds,
                            cancellationToken)
                        .ConfigureAwait(false);
                    pendingPrefetch = null;

                    if (prefetchedClip is not null &&
                        windowsSpeechService is not null &&
                        !string.IsNullOrWhiteSpace(pinnedChunkEngineName))
                    {
                        var prefetchedClipEngineName = windowsSpeechService.GetPrefetchedClipEngineName(prefetchedClip);
                        if (!string.IsNullOrWhiteSpace(prefetchedClipEngineName) &&
                            !string.Equals(prefetchedClipEngineName, pinnedChunkEngineName, StringComparison.OrdinalIgnoreCase))
                        {
                            AppDiagnostics.Info(
                                "speech_chunk_prefetch_discarded_for_engine_continuity",
                                new Dictionary<string, string?>
                                {
                                    ["chunkIndex"] = index.ToString(),
                                    ["pinnedEngine"] = pinnedChunkEngineName,
                                    ["prefetchedEngine"] = prefetchedClipEngineName,
                                    ["voice"] = request.Options.VoiceName
                                });
                            prefetchedClip.Dispose();
                            prefetchedClip = null;
                        }
                    }

                    usingPrefetchedClip = prefetchedClip is not null;
                }

                if (prefetchSpeechService is not null && index + 1 < chunks.Count)
                {
                    var nextChunk = chunks[index + 1];
                    var nextRequest = BuildRequest(
                        nextChunk,
                        ResolveContinuationPrimerSeconds(pinnedChunkEngineName),
                        isContinuationChunk: true,
                        allowOutputDeviceWarmup: false,
                        useFullPrimerWarmupCarrier: false);

                    pendingPrefetch = TryStartChunkPrefetch(
                        prefetchSpeechService,
                        windowsSpeechService,
                        index + 1,
                        nextRequest,
                        pinnedChunkEngineName,
                        cancellationToken);
                }

                AppDiagnostics.Info(
                    "speech_chunk_dispatch",
                    new Dictionary<string, string?>
                    {
                        ["chunkIndex"] = index.ToString(),
                        ["chunkCount"] = chunks.Count.ToString(),
                        ["chunkLength"] = chunk.Length.ToString(),
                        ["isContinuationChunk"] = isContinuationChunk.ToString(),
                        ["primerOverrideSeconds"] = primerOverride?.ToString("0.00") ?? "default",
                        ["usingPrefetchedClip"] = usingPrefetchedClip.ToString(),
                        ["pinnedEngine"] = pinnedChunkEngineName
                    });

                SpeechResult result;
                string? resolvedChunkEngineName = pinnedChunkEngineName;

                if (windowsSpeechService is not null)
                {
                    var chunkResult = await windowsSpeechService
                        .SpeakChunkAsync(request, prefetchedClip, pinnedChunkEngineName, cancellationToken)
                        .ConfigureAwait(false);
                    result = chunkResult.SpeechResult;
                    if (result.Success && !result.WasCancelled && !string.IsNullOrWhiteSpace(chunkResult.EngineName))
                    {
                        resolvedChunkEngineName = chunkResult.EngineName;
                    }
                }
                else if (usingPrefetchedClip)
                {
                    result = await prefetchSpeechService!
                        .SpeakPrefetchedAsync(prefetchedClip!, request, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    result = await _speechService
                        .SpeakAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!result.Success || result.WasCancelled)
                {
                    return result;
                }

                pinnedChunkEngineName = resolvedChunkEngineName;

                if (pendingPrefetch is not null &&
                    prefetchSpeechService is not null &&
                    ShouldRealignPendingPrefetch(pendingPrefetch, pinnedChunkEngineName))
                {
                    var replacementChunkIndex = pendingPrefetch.ChunkIndex;
                    var replacementRequest = pendingPrefetch.Request;
                    CancelPendingPrefetch(pendingPrefetch, "engine_realigned");
                    pendingPrefetch = TryStartChunkPrefetch(
                        prefetchSpeechService,
                        windowsSpeechService,
                        replacementChunkIndex,
                        replacementRequest,
                        pinnedChunkEngineName,
                        cancellationToken);
                }
            }
        }
        finally
        {
            CancelPendingPrefetch(pendingPrefetch, "read_cleanup");
        }

        return SpeechResult.Completed();
    }

    private PendingChunkPrefetch? TryStartChunkPrefetch(
        IPrefetchSpeechService prefetchSpeechService,
        WindowsSpeechService? windowsSpeechService,
        int chunkIndex,
        SpeechRequest request,
        string? preferredEngineName,
        CancellationToken cancellationToken)
    {
        var supportsPrefetch = windowsSpeechService is not null
            ? windowsSpeechService.SupportsPrefetch(request, preferredEngineName)
            : prefetchSpeechService.SupportsPrefetch(request);
        if (!supportsPrefetch)
        {
            return null;
        }

        return StartChunkPrefetch(
            prefetchSpeechService,
            windowsSpeechService,
            chunkIndex,
            request,
            preferredEngineName,
            cancellationToken);
    }

    private static PendingChunkPrefetch StartChunkPrefetch(
        IPrefetchSpeechService prefetchSpeechService,
        WindowsSpeechService? windowsSpeechService,
        int chunkIndex,
        SpeechRequest request,
        string? preferredEngineName,
        CancellationToken cancellationToken)
    {
        var prefetchCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var prefetchTask = windowsSpeechService is not null
            ? windowsSpeechService.PrefetchAsync(request, preferredEngineName, prefetchCancellationTokenSource.Token)
            : prefetchSpeechService.PrefetchAsync(request, prefetchCancellationTokenSource.Token);
        var pendingPrefetch = new PendingChunkPrefetch(
            chunkIndex,
            request,
            preferredEngineName,
            prefetchCancellationTokenSource,
            prefetchTask);
        _ = prefetchTask.ContinueWith(
            static (_, state) => ObserveFaultedPrefetch((PendingChunkPrefetch)state!, "background"),
            pendingPrefetch,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return pendingPrefetch;
    }

    private static async Task<IPrefetchedSpeechClip?> TryTakeReadyPrefetchedClipAsync(
        PendingChunkPrefetch pendingPrefetch,
        int prefetchGraceWindowMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            if (pendingPrefetch.Task.IsCompleted)
            {
                if (pendingPrefetch.Task.IsCompletedSuccessfully)
                {
                    var readyClip = pendingPrefetch.Task.Result;
                    pendingPrefetch.CancellationTokenSource.Dispose();
                    return readyClip;
                }

                ObserveFaultedPrefetch(pendingPrefetch, "ready_check");
                CancelPendingPrefetch(pendingPrefetch, "ready_check");
                return null;
            }

            var completedTask = await Task.WhenAny(
                pendingPrefetch.Task,
                    Task.Delay(prefetchGraceWindowMilliseconds, cancellationToken))
                .ConfigureAwait(false);

            if (completedTask == pendingPrefetch.Task)
            {
                if (pendingPrefetch.Task.IsCompletedSuccessfully)
                {
                    var prefetchedClip = pendingPrefetch.Task.Result;
                    pendingPrefetch.CancellationTokenSource.Dispose();
                    return prefetchedClip;
                }

                ObserveFaultedPrefetch(pendingPrefetch, "grace_window");
                CancelPendingPrefetch(pendingPrefetch, "grace_window");
                return null;
            }

            CancelPendingPrefetch(pendingPrefetch, "grace_window_expired");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ObserveFaultedPrefetch(pendingPrefetch, "harness_exception");
            CancelPendingPrefetch(pendingPrefetch, "harness_exception");
            return null;
        }
    }

    private int GetPrefetchGraceWindowMilliseconds(SpeechRequest request, string? preferredEngineName)
    {
        if (_speechService is WindowsSpeechService windowsSpeechService)
        {
            return windowsSpeechService.GetChunkPrefetchGraceWindowMilliseconds(request, preferredEngineName);
        }

        return 30;
    }

    private static bool ShouldRealignPendingPrefetch(PendingChunkPrefetch pendingPrefetch, string? pinnedChunkEngineName)
    {
        if (string.IsNullOrWhiteSpace(pinnedChunkEngineName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pendingPrefetch.Request.Options.VoiceName))
        {
            return false;
        }

        return !string.Equals(
            pendingPrefetch.PreferredEngineName,
            pinnedChunkEngineName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveContinuationPrimerSeconds(string? engineName)
    {
        return engineName switch
        {
            "windows_onecore" => ContinuationPrimerWindowsOneCoreSeconds,
            "system_speech" => ContinuationPrimerSystemSpeechSeconds,
            "piper" => ContinuationPrimerPiperSeconds,
            _ => ContinuationPrimerUnknownEngineSeconds
        };
    }

    private static void ObserveFaultedPrefetch(PendingChunkPrefetch pendingPrefetch, string stage)
    {
        if (!pendingPrefetch.Task.IsFaulted || !pendingPrefetch.TryMarkFaultObserved())
        {
            return;
        }

        var exception = pendingPrefetch.Task.Exception?.Flatten().InnerExceptions.FirstOrDefault() ??
                        pendingPrefetch.Task.Exception;
        AppDiagnostics.Warn(
            "speech_chunk_prefetch_faulted",
            new Dictionary<string, string?>
            {
                ["chunkIndex"] = pendingPrefetch.ChunkIndex.ToString(),
                ["preferredEngine"] = pendingPrefetch.PreferredEngineName,
                ["voice"] = pendingPrefetch.Request.Options.VoiceName,
                ["stage"] = stage,
                ["message"] = exception?.Message,
                ["textLength"] = pendingPrefetch.Request.Text?.Length.ToString()
            });
    }

    private static void CancelPendingPrefetch(PendingChunkPrefetch? pendingPrefetch, string stage)
    {
        if (pendingPrefetch is null)
        {
            return;
        }

        ObserveFaultedPrefetch(pendingPrefetch, stage);

        try
        {
            pendingPrefetch.CancellationTokenSource.Cancel();
        }
        catch
        {
            // Best effort cancellation only.
        }

        try
        {
            if (pendingPrefetch.Task.IsCompletedSuccessfully)
            {
                pendingPrefetch.Task.Result?.Dispose();
            }
        }
        catch
        {
            // Ignore cleanup failures when discarding unused prefetched audio.
        }
        finally
        {
            pendingPrefetch.CancellationTokenSource.Dispose();
        }
    }

    private static IReadOnlyList<string> SplitIntoSpeechChunks(string text, int minChunkCharacters, int targetChunkCharacters, int maxChunkCharacters)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        if (normalized.Length <= maxChunkCharacters)
        {
            return new[] { normalized };
        }

        var chunks = new List<string>();
        var cursor = 0;
        while (cursor < normalized.Length)
        {
            var remaining = normalized.Length - cursor;
            if (remaining <= maxChunkCharacters)
            {
                var tailChunk = normalized[cursor..].Trim();
                if (!string.IsNullOrWhiteSpace(tailChunk))
                {
                    chunks.Add(tailChunk);
                }

                break;
            }

            var minimumEndExclusive = Math.Min(normalized.Length, cursor + minChunkCharacters);
            var targetEndExclusive = Math.Min(normalized.Length, cursor + targetChunkCharacters);
            var maximumEndExclusive = Math.Min(normalized.Length, cursor + maxChunkCharacters);
            var splitIndex = FindChunkSplitIndex(normalized, cursor, minimumEndExclusive, targetEndExclusive, maximumEndExclusive);
            if (splitIndex <= cursor)
            {
                splitIndex = maximumEndExclusive;
            }

            var chunk = normalized[cursor..splitIndex].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            cursor = splitIndex;
            while (cursor < normalized.Length && char.IsWhiteSpace(normalized[cursor]))
            {
                cursor++;
            }
        }

        return chunks;
    }

    private static int FindChunkSplitIndex(
        string text,
        int startInclusive,
        int minimumEndExclusive,
        int targetEndExclusive,
        int maximumEndExclusive)
    {
        var sentenceBeforeTarget = FindBoundaryNearTarget(text, minimumEndExclusive, targetEndExclusive, targetEndExclusive, SentenceBoundaryScore);
        if (sentenceBeforeTarget > startInclusive)
        {
            return sentenceBeforeTarget;
        }

        var sentenceAfterTarget = FindBoundaryNearTarget(text, targetEndExclusive, maximumEndExclusive, targetEndExclusive, SentenceBoundaryScore);
        if (sentenceAfterTarget > startInclusive)
        {
            return sentenceAfterTarget;
        }

        var clauseBoundary = FindBoundaryNearTarget(text, minimumEndExclusive, maximumEndExclusive, targetEndExclusive, ClauseBoundaryScore);
        if (clauseBoundary > startInclusive)
        {
            return clauseBoundary;
        }

        var whitespaceBoundary = FindBoundaryNearTarget(text, minimumEndExclusive, maximumEndExclusive, targetEndExclusive, WhitespaceBoundaryScore);
        if (whitespaceBoundary > startInclusive)
        {
            return whitespaceBoundary;
        }

        return maximumEndExclusive;
    }

    private static int FindBoundaryNearTarget(
        string text,
        int searchStartInclusive,
        int searchEndExclusive,
        int targetEndExclusive,
        Func<string, int, int> boundaryScore)
    {
        var bestIndex = -1;
        var bestScore = int.MaxValue;
        for (var boundaryIndex = searchStartInclusive; boundaryIndex <= searchEndExclusive; boundaryIndex++)
        {
            var score = boundaryScore(text, boundaryIndex);
            if (score == int.MaxValue)
            {
                continue;
            }

            var distancePenalty = Math.Abs(boundaryIndex - targetEndExclusive) * 10;
            var totalScore = score + distancePenalty;
            if (totalScore < bestScore)
            {
                bestScore = totalScore;
                bestIndex = boundaryIndex;
            }
        }

        return bestIndex;
    }

    private static int SentenceBoundaryScore(string text, int boundaryIndex)
    {
        if (boundaryIndex <= 0 || boundaryIndex > text.Length)
        {
            return int.MaxValue;
        }

        var previousIndex = boundaryIndex - 1;
        while (previousIndex >= 0 && (text[previousIndex] == '"' || text[previousIndex] == '\'' || text[previousIndex] == ')' || text[previousIndex] == ']' || text[previousIndex] == '}'))
        {
            previousIndex--;
        }

        if (previousIndex < 0)
        {
            return int.MaxValue;
        }

        var punctuation = text[previousIndex];
        if (punctuation != '.' && punctuation != '!' && punctuation != '?')
        {
            return int.MaxValue;
        }

        if (boundaryIndex == text.Length || char.IsWhiteSpace(text[boundaryIndex]))
        {
            return 0;
        }

        return int.MaxValue;
    }

    private static int ClauseBoundaryScore(string text, int boundaryIndex)
    {
        if (boundaryIndex <= 0 || boundaryIndex > text.Length)
        {
            return int.MaxValue;
        }

        var punctuation = text[boundaryIndex - 1];
        if (punctuation == ';' || punctuation == ':' || punctuation == ',')
        {
            return 300;
        }

        if (punctuation == '-')
        {
            return 450;
        }

        return int.MaxValue;
    }

    private static int WhitespaceBoundaryScore(string text, int boundaryIndex)
    {
        if (boundaryIndex <= 0 || boundaryIndex >= text.Length)
        {
            return int.MaxValue;
        }

        return char.IsWhiteSpace(text[boundaryIndex]) ? 900 : int.MaxValue;
    }

    private static int EstimateSpeechChunkCount(string text)
    {
        var normalizedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return 0;
        }

        if (normalizedText.Length <= ChunkMaxCharacters)
        {
            return 1;
        }

        return SplitIntoSpeechChunks(normalizedText, ChunkMinCharacters, ChunkTargetCharacters, ChunkMaxCharacters).Count;
    }

    private void NormalizeSavedVoiceSetting()
    {
        var normalized = NormalizeVoiceName(_settingsService.Current.VoiceName);
        if (string.Equals(_settingsService.Current.VoiceName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Current.VoiceName = normalized;
        _settingsService.Save();
    }

    private void EnsureDefaultVoiceSelectionForEmptyStorage()
    {
        if (!string.IsNullOrWhiteSpace(_settingsService.Current.VoiceName))
        {
            return;
        }

        try
        {
            var selectedDefaultVoiceName = SelectDefaultVoiceForEmptyStorage();
            if (string.IsNullOrWhiteSpace(selectedDefaultVoiceName))
            {
                return;
            }

            _settingsService.Current.VoiceName = selectedDefaultVoiceName;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "voice_default_selection_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            _settingsService.Current.VoiceName = null;
            _settingsService.Save();
        }
    }

    private string? SelectDefaultVoiceForEmptyStorage()
    {
        var hasAnyPiperVoice = _availableVoices.Any(voice =>
            string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal));
        if (hasAnyPiperVoice)
        {
            var piperLjspeechVoice = _availableVoices.FirstOrDefault(voice =>
                string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal) &&
                string.Equals(voice.Name, PreferredPiperLjspeechVoiceName, StringComparison.OrdinalIgnoreCase));
            if (piperLjspeechVoice is not null)
            {
                return piperLjspeechVoice.Name;
            }

            var piperLjspeechByFragment = _availableVoices.FirstOrDefault(voice =>
                string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal) &&
                voice.Name.Contains("ljspeech", StringComparison.OrdinalIgnoreCase));
            if (piperLjspeechByFragment is not null)
            {
                return piperLjspeechByFragment.Name;
            }

            return null;
        }

        var microsoftDavidVoice = _availableVoices
            .Where(voice =>
                !string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal) &&
                (voice.Name.Contains("Microsoft David", StringComparison.OrdinalIgnoreCase) ||
                 voice.DisplayName.Contains("Microsoft David", StringComparison.OrdinalIgnoreCase) ||
                 voice.Name.Contains("David", StringComparison.OrdinalIgnoreCase) ||
                 voice.DisplayName.Contains("David", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(voice => voice.Engine, StringComparer.Ordinal)
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return microsoftDavidVoice?.Name;
    }

    private string? NormalizeVoiceName(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return null;
        }

        foreach (var availableVoice in _availableVoices)
        {
            if (string.Equals(availableVoice.Name, voiceName, StringComparison.OrdinalIgnoreCase))
            {
                return availableVoice.Name;
            }
        }

        return null;
    }

    private static bool ShouldRetryParagraphRetrieval(TextRetrievalResult retrieval)
    {
        return retrieval.ShouldRetry;
    }

    private static string BuildSelectedTextFailureMessage()
    {
        return "Couldn't read the selected text. Select the text in the other app, then try again.";
    }

    private static string? BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string BuildParagraphFailureMessage()
    {
        return "Couldn't read the current paragraph. Click in the paragraph you want, then try again.";
    }

    private static string BuildDocumentFailureMessage()
    {
        return "Couldn't read the document from that app.";
    }
}
