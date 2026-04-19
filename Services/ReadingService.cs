using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Interop;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class ReadingService : IReadingService
{
    private const int FirstChunkMinCharacters = 100;
    private const int FirstChunkTargetCharacters = 150;
    private const int FirstChunkMaxCharacters = 200;
    private const int ContinuationChunkMinCharacters = 200;
    private const int ContinuationChunkTargetCharacters = 280;
    private const int ContinuationChunkMaxCharacters = 380;
    private const int TextRetrievalRetryDelayMilliseconds = 220;
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
        var retrievalAttempt = await RetrieveTextWithRetryAsync(
                "selected",
                operationId,
                _selectedTextRetrievalService.RetrieveSelectedTextAsync,
                cancellationToken)
            .ConfigureAwait(false);
        var retrieval = retrievalAttempt.Result;
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
                ["retried"] = retrievalAttempt.Retried.ToString(),
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
                    ["retried"] = retrievalAttempt.Retried.ToString(),
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
        using var scope = AppDiagnostics.BeginScope(new Dictionary<string, string?>
        {
            ["readOperationId"] = operationId,
            ["readWorkflow"] = "paragraph_external",
            ["readVoice"] = _settingsService.Current.VoiceName,
            ["readRate"] = _settingsService.Current.SpeechRate.ToString()
        });
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
        var retrievalAttempt = await RetrieveTextWithRetryAsync(
                "paragraph",
                operationId,
                _paragraphTextRetrievalService.RetrieveParagraphTextAsync,
                cancellationToken)
            .ConfigureAwait(false);
        var retrieval = retrievalAttempt.Result;
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
                ["retried"] = retrievalAttempt.Retried.ToString(),
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
                    ["source"] = retrieval.Source?.ToString(),
                    ["reason"] = retrieval.Message,
                    ["textPreview"] = BuildPreview(retrieval.Text),
                    ["retried"] = retrievalAttempt.Retried.ToString(),
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
        var operationId = Guid.NewGuid().ToString("N");
        using var scope = AppDiagnostics.BeginScope(new Dictionary<string, string?>
        {
            ["readOperationId"] = operationId,
            ["readWorkflow"] = "document_external",
            ["readVoice"] = _settingsService.Current.VoiceName,
            ["readRate"] = _settingsService.Current.SpeechRate.ToString()
        });
        var readStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "focused_read_document_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["voice"] = _settingsService.Current.VoiceName,
                ["rate"] = _settingsService.Current.SpeechRate.ToString()
            });

        var retrievalStopwatch = Stopwatch.StartNew();
        var retrievalAttempt = await RetrieveTextWithRetryAsync(
                "document",
                operationId,
                _documentTextRetrievalService.RetrieveDocumentTextAsync,
                cancellationToken)
            .ConfigureAwait(false);
        var retrieval = retrievalAttempt.Result;
        retrievalStopwatch.Stop();
        AppDiagnostics.Info(
            "focused_read_document_retrieval_result",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["success"] = retrieval.Success.ToString(),
                ["source"] = retrieval.Source?.ToString(),
                ["message"] = retrieval.Message,
                ["textLength"] = retrieval.Text?.Length.ToString(),
                ["textPreview"] = BuildPreview(retrieval.Text),
                ["retried"] = retrievalAttempt.Retried.ToString(),
                ["elapsedMs"] = retrievalStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            readStopwatch.Stop();
            AppDiagnostics.Warn(
                "focused_read_document_failed_before_speech",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["source"] = retrieval.Source?.ToString(),
                    ["reason"] = retrieval.Message,
                    ["textPreview"] = BuildPreview(retrieval.Text),
                    ["retried"] = retrievalAttempt.Retried.ToString(),
                    ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
                });
            return SpeechResult.Failed(BuildDocumentFailureMessage(retrieval));
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
                ["operationId"] = operationId,
                ["success"] = speechResult.Success.ToString(),
                ["cancelled"] = speechResult.WasCancelled.ToString(),
                ["message"] = speechResult.Message,
                ["chunkCount"] = chunkCount.ToString(),
                ["textLength"] = retrieval.Text?.Length.ToString(),
                ["textPreview"] = BuildPreview(retrieval.Text),
                ["speechElapsedMs"] = speechStopwatch.ElapsedMilliseconds.ToString(),
                ["totalElapsedMs"] = readStopwatch.ElapsedMilliseconds.ToString()
            });

        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    private static async Task<(TextRetrievalResult Result, bool Retried)> RetrieveTextWithRetryAsync(
        string workflowName,
        string operationId,
        Func<CancellationToken, Task<TextRetrievalResult>> retrieveAsync,
        CancellationToken cancellationToken)
    {
        var result = await retrieveAsync(cancellationToken).ConfigureAwait(false);
        if (!ShouldRetryTextRetrieval(result))
        {
            return (result, false);
        }

        if (IsExternalReadWorkflow(workflowName) &&
            !IsForegroundExternalToRightSpeak(out var foregroundData))
        {
            foregroundData["operationId"] = operationId;
            foregroundData["workflow"] = workflowName;
            foregroundData["firstAttemptSource"] = result.Source?.ToString();
            foregroundData["firstAttemptMessage"] = result.Message;
            AppDiagnostics.Warn("focused_read_retrieval_retry_skipped_focus_not_external", foregroundData);
            return (result, false);
        }

        AppDiagnostics.Info(
            "focused_read_retrieval_retry_scheduled",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["workflow"] = workflowName,
                ["delayMs"] = TextRetrievalRetryDelayMilliseconds.ToString(),
                ["firstAttemptSource"] = result.Source?.ToString(),
                ["firstAttemptMessage"] = result.Message,
                ["firstAttemptTextLength"] = result.Text?.Length.ToString()
            });

        await Task.Delay(TextRetrievalRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        return (await retrieveAsync(cancellationToken).ConfigureAwait(false), true);
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

    private async Task<SpeechResult> SpeakWithChunkingIfNeededAsync(
        string text,
        CancellationToken cancellationToken,
        double? firstChunkPrimerOverride = null)
    {
        var normalizedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return SpeechResult.Failed("Nothing to read. Enter text first.");
        }

        if (normalizedText.Length <= FirstChunkMaxCharacters)
        {
            return await _speechService
                .SpeakAsync(BuildRequest(normalizedText, firstChunkPrimerOverride), cancellationToken)
                .ConfigureAwait(false);
        }

        var chunks = SplitIntoSpeechChunks(normalizedText);
        if (chunks.Count <= 1)
        {
            return await _speechService
                .SpeakAsync(BuildRequest(normalizedText, firstChunkPrimerOverride), cancellationToken)
                .ConfigureAwait(false);
        }

        var chunkRequests = BuildChunkRequests(chunks, firstChunkPrimerOverride);
        if (_speechService is WindowsSpeechService windowsSpeechService)
        {
            return await windowsSpeechService.SpeakChunkSequenceAsync(chunkRequests, cancellationToken).ConfigureAwait(false);
        }

        foreach (var request in chunkRequests)
        {
            var result = await _speechService.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Success || result.WasCancelled)
            {
                return result;
            }
        }

        return SpeechResult.Completed();
    }

    private IReadOnlyList<SpeechRequest> BuildChunkRequests(IReadOnlyList<string> chunks, double? firstChunkPrimerOverride)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<SpeechRequest>();
        }

        var requests = new List<SpeechRequest>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var isContinuationChunk = index > 0;
            var request = isContinuationChunk
                ? BuildRequest(
                    chunks[index],
                    leadingPrimerSecondsOverride: 0.0,
                    isContinuationChunk: true,
                    allowOutputDeviceWarmup: false,
                    useFullPrimerWarmupCarrier: false)
                : BuildRequest(chunks[index], firstChunkPrimerOverride);
            requests.Add(request);
        }

        return requests;
    }

    private static IReadOnlyList<string> SplitIntoSpeechChunks(string text)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        if (normalized.Length <= FirstChunkMaxCharacters)
        {
            return new[] { normalized };
        }

        var chunks = new List<string>();
        var firstSplitIndex = FindFirstChunkSplitIndex(normalized);
        var firstChunk = normalized[..firstSplitIndex].Trim();
        if (!string.IsNullOrWhiteSpace(firstChunk))
        {
            chunks.Add(firstChunk);
        }

        var cursor = firstSplitIndex;
        while (cursor < normalized.Length && char.IsWhiteSpace(normalized[cursor]))
        {
            cursor++;
        }

        if (cursor < normalized.Length)
        {
            var continuationText = normalized[cursor..].Trim();
            chunks.AddRange(SplitIntoSpeechChunks(
                continuationText,
                ContinuationChunkMinCharacters,
                ContinuationChunkTargetCharacters,
                ContinuationChunkMaxCharacters));
        }

        return chunks;
    }

    private static int FindFirstChunkSplitIndex(string text)
    {
        var minimumEndExclusive = Math.Min(text.Length, FirstChunkMinCharacters);
        var targetEndExclusive = Math.Min(text.Length, FirstChunkTargetCharacters);
        var maximumEndExclusive = Math.Min(text.Length, FirstChunkMaxCharacters);
        var splitIndex = FindChunkSplitIndex(text, 0, minimumEndExclusive, targetEndExclusive, maximumEndExclusive);
        return splitIndex <= 0 ? maximumEndExclusive : splitIndex;
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

        if (normalizedText.Length <= FirstChunkMaxCharacters)
        {
            return 1;
        }

        return SplitIntoSpeechChunks(normalizedText).Count;
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

    private static bool ShouldRetryTextRetrieval(TextRetrievalResult retrieval)
    {
        return (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text)) && retrieval.ShouldRetry;
    }

    private static bool IsExternalReadWorkflow(string workflowName)
    {
        return string.Equals(workflowName, "selected", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(workflowName, "paragraph", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(workflowName, "document", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForegroundExternalToRightSpeak(out Dictionary<string, string?> data)
    {
        var foregroundWindow = WindowFocusInterop.GetForegroundWindow();
        WindowFocusInterop.GetWindowThreadProcessId(foregroundWindow, out var processId);
        var isExternal = foregroundWindow != nint.Zero &&
                         processId != 0 &&
                         processId != Environment.ProcessId;
        data = new Dictionary<string, string?>
        {
            ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
            ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundWindow),
            ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundWindow),
            ["foregroundWindowProcessId"] = processId.ToString(),
            ["isExternalForeground"] = isExternal.ToString()
        };
        return isExternal;
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

    private static string BuildDocumentFailureMessage(TextRetrievalResult retrieval)
    {
        if (retrieval.Source == TextRetrievalSource.ClipboardFallback &&
            !string.IsNullOrWhiteSpace(retrieval.Message) &&
            retrieval.Message.Contains("Browser PDF viewer blocked document copy to clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return "Couldn't copy document text from the browser PDF viewer. Enable PDF accessibility text access and try again, or open the PDF in an external reader.";
        }

        return "Couldn't read the document from that app.";
    }
}
