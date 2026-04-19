using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class DocumentTextRetrievalService : IDocumentTextRetrievalService
{
    private const int MaxLeadingLinesForQualityScan = 12;
    private const int DocumentScopeShortTextThreshold = 140;
    private const int DocumentScopeClipboardShortTextThreshold = 420;
    private const int DocumentScopeShortTextMaxLines = 2;
    private static readonly string[] ViewerUiMarkers =
    {
        "toolbar",
        "pdf viewer",
        "accessibility",
        "screen reader",
        "fit to page",
        "zoom",
        "rotate",
        "print",
        "download",
        "open in",
        "show thumbnails",
        "find in file",
        "page "
    };

    private readonly IReadOnlyList<IDocumentTextProvider> _providers;

    public DocumentTextRetrievalService(IReadOnlyList<IDocumentTextProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<TextRetrievalResult> RetrieveDocumentTextAsync(CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
        {
            return TextRetrievalResult.Failed("No document-text providers are configured.");
        }

        var overallStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "document_retrieval_started",
            new Dictionary<string, string?>
            {
                ["providerCount"] = _providers.Count.ToString(),
                ["providerOrder"] = string.Join(" -> ", _providers.Select(provider => provider.GetType().Name))
            });

        TextRetrievalResult? lastFailure = null;
        TextRetrievalResult? bestSuccess = null;
        string? bestProviderName = null;
        string? bestSelectionReason = null;
        var bestScore = int.MinValue;
        var failureDetails = new List<string>();
        var shouldRetry = false;
        var successfulCandidateCount = 0;

        for (var providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = _providers[providerIndex];

            var providerStopwatch = Stopwatch.StartNew();
            var result = await provider.TryGetDocumentTextAsync(cancellationToken).ConfigureAwait(false);
            providerStopwatch.Stop();
            AppDiagnostics.Info(
                "document_retrieval_provider_result",
                new Dictionary<string, string?>
                {
                    ["providerIndex"] = providerIndex.ToString(),
                    ["provider"] = provider.GetType().Name,
                    ["success"] = result.Success.ToString(),
                    ["source"] = result.Source?.ToString(),
                    ["message"] = result.Message,
                    ["textLength"] = result.Text?.Length.ToString(),
                    ["textPreview"] = BuildPreview(result.Text),
                    ["shouldRetry"] = result.ShouldRetry.ToString(),
                    ["elapsedMs"] = providerStopwatch.ElapsedMilliseconds.ToString()
                });

            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                successfulCandidateCount++;
                var providerName = provider.GetType().Name;
                var candidate = AnalyzeCandidate(result.Text);
                AppDiagnostics.Info(
                    "document_scope_validation_started",
                    new Dictionary<string, string?>
                    {
                        ["providerIndex"] = providerIndex.ToString(),
                        ["provider"] = providerName,
                        ["source"] = result.Source?.ToString(),
                        ["textLength"] = result.Text?.Length.ToString(),
                        ["nonEmptyLineCount"] = candidate.NonEmptyLineCount.ToString(),
                        ["leadingUiLikeLineCount"] = candidate.LeadingUiLikeLineCount.ToString(),
                        ["leadingBodyLikeLineCount"] = candidate.LeadingBodyLikeLineCount.ToString(),
                        ["leadingMarkerHitCount"] = candidate.LeadingMarkerHitCount.ToString(),
                        ["suspiciousLeadingPreamble"] = candidate.SuspiciousLeadingPreamble.ToString(),
                        ["candidateSourceMessage"] = result.Message
                    });
                AppDiagnostics.Info(
                    "document_retrieval_candidate_scored",
                    new Dictionary<string, string?>
                    {
                        ["providerIndex"] = providerIndex.ToString(),
                        ["provider"] = providerName,
                        ["source"] = result.Source?.ToString(),
                        ["score"] = candidate.Score.ToString(),
                        ["suspiciousLeadingPreamble"] = candidate.SuspiciousLeadingPreamble.ToString(),
                        ["textLength"] = result.Text?.Length.ToString(),
                        ["leadingNonEmptyLineCount"] = candidate.LeadingNonEmptyLineCount.ToString(),
                        ["leadingUiLikeLineCount"] = candidate.LeadingUiLikeLineCount.ToString(),
                        ["leadingBodyLikeLineCount"] = candidate.LeadingBodyLikeLineCount.ToString(),
                        ["leadingMarkerHitCount"] = candidate.LeadingMarkerHitCount.ToString(),
                        ["firstNonEmptyLinePreview"] = candidate.FirstNonEmptyLinePreview,
                        ["leadingLinesPreview"] = candidate.LeadingLinesPreview,
                        ["textPreview"] = BuildPreview(result.Text),
                        ["candidateSourceMessage"] = result.Message
                    });

                if (TryGetDocumentScopeRejectionReason(result, candidate, out var scopeRejectionReason))
                {
                    shouldRetry |= true;
                    var rejectionMessage = BuildScopeRejectionMessage(scopeRejectionReason);
                    failureDetails.Add($"{result.Source?.ToString() ?? providerName}: {rejectionMessage}");
                    lastFailure = TextRetrievalResult.Failed(
                        rejectionMessage,
                        result.Source,
                        shouldRetry: true);
                    AppDiagnostics.Warn(
                        "document_scope_validation_rejected",
                        new Dictionary<string, string?>
                        {
                            ["providerIndex"] = providerIndex.ToString(),
                            ["provider"] = providerName,
                            ["source"] = result.Source?.ToString(),
                            ["rejectionReason"] = scopeRejectionReason,
                            ["textLength"] = result.Text?.Length.ToString(),
                            ["nonEmptyLineCount"] = candidate.NonEmptyLineCount.ToString(),
                            ["leadingUiLikeLineCount"] = candidate.LeadingUiLikeLineCount.ToString(),
                            ["leadingBodyLikeLineCount"] = candidate.LeadingBodyLikeLineCount.ToString(),
                            ["leadingMarkerHitCount"] = candidate.LeadingMarkerHitCount.ToString(),
                            ["textPreview"] = BuildPreview(result.Text),
                            ["candidateSourceMessage"] = result.Message
                        });
                    continue;
                }

                if (bestSuccess is null || candidate.Score > bestScore)
                {
                    bestSuccess = result;
                    bestProviderName = providerName;
                    bestScore = candidate.Score;
                    bestSelectionReason = "highest_score_so_far";
                    AppDiagnostics.Info(
                        "document_scope_validation_passed",
                        new Dictionary<string, string?>
                        {
                            ["providerIndex"] = providerIndex.ToString(),
                            ["provider"] = providerName,
                            ["source"] = result.Source?.ToString(),
                            ["selectionReason"] = bestSelectionReason,
                            ["textLength"] = result.Text?.Length.ToString(),
                            ["nonEmptyLineCount"] = candidate.NonEmptyLineCount.ToString(),
                            ["leadingUiLikeLineCount"] = candidate.LeadingUiLikeLineCount.ToString(),
                            ["leadingBodyLikeLineCount"] = candidate.LeadingBodyLikeLineCount.ToString(),
                            ["leadingMarkerHitCount"] = candidate.LeadingMarkerHitCount.ToString(),
                            ["textPreview"] = BuildPreview(result.Text)
                        });
                }

                var hasMoreProviders = providerIndex < _providers.Count - 1;
                if (!candidate.SuspiciousLeadingPreamble || !hasMoreProviders)
                {
                    bestSelectionReason = !candidate.SuspiciousLeadingPreamble
                        ? "first_non_suspicious_candidate"
                        : "last_provider_candidate";
                    break;
                }

                AppDiagnostics.Warn(
                    "document_retrieval_candidate_deferred_for_better_source",
                    new Dictionary<string, string?>
                    {
                        ["providerIndex"] = providerIndex.ToString(),
                        ["provider"] = providerName,
                        ["score"] = candidate.Score.ToString(),
                        ["leadingUiLikeLineCount"] = candidate.LeadingUiLikeLineCount.ToString(),
                        ["leadingBodyLikeLineCount"] = candidate.LeadingBodyLikeLineCount.ToString(),
                        ["leadingMarkerHitCount"] = candidate.LeadingMarkerHitCount.ToString(),
                        ["firstNonEmptyLinePreview"] = candidate.FirstNonEmptyLinePreview,
                        ["reason"] = "suspicious_leading_preamble_detected"
                    });
                continue;
            }

            lastFailure = result;
            shouldRetry |= result.ShouldRetry || IsRetryableDocumentFailure(result);
            var source = result.Source?.ToString() ?? provider.GetType().Name;
            var message = string.IsNullOrWhiteSpace(result.Message) ? "No details." : result.Message;
            failureDetails.Add($"{source}: {message}");
            AppDiagnostics.Warn(
                "document_retrieval_provider_failed",
                new Dictionary<string, string?>
                {
                    ["providerIndex"] = providerIndex.ToString(),
                    ["provider"] = provider.GetType().Name,
                    ["source"] = result.Source?.ToString(),
                    ["message"] = message,
                    ["shouldRetry"] = result.ShouldRetry.ToString(),
                    ["retryableByHeuristic"] = IsRetryableDocumentFailure(result).ToString()
                });
        }

        if (bestSuccess is not null)
        {
            overallStopwatch.Stop();
            AppDiagnostics.Info(
                "document_retrieval_success",
                new Dictionary<string, string?>
                {
                    ["provider"] = bestProviderName,
                    ["source"] = bestSuccess.Source?.ToString(),
                    ["score"] = bestScore.ToString(),
                    ["selectionReason"] = bestSelectionReason,
                    ["successfulCandidateCount"] = successfulCandidateCount.ToString(),
                    ["textLength"] = bestSuccess.Text?.Length.ToString(),
                    ["textPreview"] = BuildPreview(bestSuccess.Text),
                    ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
                });
            AppDiagnostics.Info(
                "document_scope_validation_passed",
                new Dictionary<string, string?>
                {
                    ["provider"] = bestProviderName,
                    ["source"] = bestSuccess.Source?.ToString(),
                    ["selectionReason"] = bestSelectionReason,
                    ["successfulCandidateCount"] = successfulCandidateCount.ToString(),
                    ["textLength"] = bestSuccess.Text?.Length.ToString(),
                    ["textPreview"] = BuildPreview(bestSuccess.Text)
                });
            return bestSuccess;
        }

        overallStopwatch.Stop();
        AppDiagnostics.Warn(
            "document_retrieval_all_failed",
            new Dictionary<string, string?>
            {
                ["providerCount"] = _providers.Count.ToString(),
                ["providerOrder"] = string.Join(" -> ", _providers.Select(provider => provider.GetType().Name)),
                ["summary"] = string.Join(" | ", failureDetails.Where(detail => !string.IsNullOrWhiteSpace(detail))),
                ["shouldRetry"] = shouldRetry.ToString(),
                ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
            });
        return (lastFailure ?? TextRetrievalResult.Failed("Document-text retrieval is unavailable."))
            .WithRetrySuggested(shouldRetry);
    }

    private static string? BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        const int maxLength = 220;
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static CandidateAnalysis AnalyzeCandidate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CandidateAnalysis(
                int.MinValue,
                0,
                0,
                SuspiciousLeadingPreamble: false,
                LeadingNonEmptyLineCount: 0,
                LeadingUiLikeLineCount: 0,
                LeadingBodyLikeLineCount: 0,
                LeadingMarkerHitCount: 0,
                FirstNonEmptyLinePreview: null,
                LeadingLinesPreview: null);
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            return new CandidateAnalysis(
                int.MinValue,
                0,
                0,
                SuspiciousLeadingPreamble: false,
                LeadingNonEmptyLineCount: 0,
                LeadingUiLikeLineCount: 0,
                LeadingBodyLikeLineCount: 0,
                LeadingMarkerHitCount: 0,
                FirstNonEmptyLinePreview: null,
                LeadingLinesPreview: null);
        }

        var allNonEmptyLines = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        var lines = allNonEmptyLines
            .Take(MaxLeadingLinesForQualityScan)
            .ToArray();

        var score = 0;
        score += Math.Min(normalized.Length / 40, 180);
        var markerHitCount = 0;

        if (lines.Length == 0)
        {
            return new CandidateAnalysis(
                score - 90,
                normalized.Length,
                0,
                SuspiciousLeadingPreamble: false,
                LeadingNonEmptyLineCount: 0,
                LeadingUiLikeLineCount: 0,
                LeadingBodyLikeLineCount: 0,
                LeadingMarkerHitCount: 0,
                FirstNonEmptyLinePreview: null,
                LeadingLinesPreview: null);
        }

        var uiLineCount = 0;
        var bodyLikeLineCount = 0;
        foreach (var line in lines)
        {
            markerHitCount += CountViewerUiMarkerHits(line);
            if (LooksLikeViewerUiLine(line))
            {
                uiLineCount++;
            }

            if (LooksLikeBodyContentLine(line))
            {
                bodyLikeLineCount++;
            }
        }

        score -= uiLineCount * 55;
        score -= markerHitCount * 12;
        score += bodyLikeLineCount * 20;

        if (uiLineCount >= 3 && bodyLikeLineCount == 0)
        {
            score -= 120;
        }

        var suspiciousLeadingPreamble =
            (uiLineCount >= 3 && bodyLikeLineCount <= 1) ||
            (markerHitCount >= 5 && bodyLikeLineCount <= 2);
        return new CandidateAnalysis(
            score,
            normalized.Length,
            allNonEmptyLines.Length,
            suspiciousLeadingPreamble,
            lines.Length,
            uiLineCount,
            bodyLikeLineCount,
            markerHitCount,
            BuildLinePreview(lines[0]),
            BuildLinesPreview(lines));
    }

    private static bool LooksLikeViewerUiLine(string line)
    {
        var lowered = line.ToLowerInvariant();
        if (lowered.Length <= 3)
        {
            return true;
        }

        if (ViewerUiMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        if (lowered.StartsWith("page ", StringComparison.Ordinal) &&
            lowered.Contains(" of ", StringComparison.Ordinal))
        {
            return true;
        }

        var letterCount = line.Count(char.IsLetter);
        var symbolCount = line.Count(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch));
        return letterCount == 0 && symbolCount > 0;
    }

    private static int CountViewerUiMarkerHits(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return 0;
        }

        var lowered = line.ToLowerInvariant();
        return ViewerUiMarkers.Count(marker => lowered.Contains(marker, StringComparison.Ordinal));
    }

    private static bool LooksLikeBodyContentLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 8)
        {
            return true;
        }

        return line.Length >= 55;
    }

    private static bool TryGetDocumentScopeRejectionReason(
        TextRetrievalResult result,
        CandidateAnalysis candidate,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        var message = result.Message ?? string.Empty;
        var source = result.Source;

        if (candidate.NormalizedLength <= DocumentScopeShortTextThreshold &&
            candidate.NonEmptyLineCount <= DocumentScopeShortTextMaxLines)
        {
            rejectionReason = "short_low_line_candidate";
            return true;
        }

        if (source == TextRetrievalSource.ClipboardFallback &&
            candidate.NormalizedLength <= DocumentScopeClipboardShortTextThreshold &&
            candidate.NonEmptyLineCount <= 3)
        {
            rejectionReason = "clipboard_candidate_scope_too_small";
            return true;
        }

        if (candidate.SuspiciousLeadingPreamble &&
            candidate.LeadingBodyLikeLineCount == 0)
        {
            rejectionReason = "viewer_ui_preamble_without_body_content";
            return true;
        }

        return false;
    }

    private static string BuildScopeRejectionMessage(string reason)
    {
        return reason switch
        {
            "short_low_line_candidate" =>
                "Document scope not achieved (short_low_line_candidate): captured text is too short to confirm full document.",
            "clipboard_candidate_scope_too_small" =>
                "Document scope not achieved (clipboard_candidate_scope_too_small): clipboard candidate looks like selection-level text.",
            "viewer_ui_preamble_without_body_content" =>
                "Document scope not achieved (viewer_ui_preamble_without_body_content): captured content appears to be viewer UI.",
            _ =>
                "Document scope not achieved: full-document confidence is low."
        };
    }

    private static bool IsRetryableDocumentFailure(TextRetrievalResult result)
    {
        if (result.Success)
        {
            return false;
        }

        var message = result.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return result.Source switch
        {
            TextRetrievalSource.FocusedControlDocument =>
                message.Equals("No focused control is available for document retrieval.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Focused control does not expose full document text through supported UI Automation patterns.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Focused control returned browser PDF accessibility UI text instead of document content.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Browser PDF document via focused-control UI Automation can drift; trying clipboard fallback.", StringComparison.OrdinalIgnoreCase),
            TextRetrievalSource.ClipboardFallback =>
                message.Equals("No focused control is available for clipboard document fallback.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard document fallback failed: unable to read current clipboard safely.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard document fallback failed: no foreground window to copy from.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard document fallback timed out waiting for copied text.", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Document scope not achieved", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string BuildLinePreview(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        const int maxLength = 180;
        var normalized = line.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string BuildLinesPreview(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var previewLines = lines
            .Take(4)
            .Select((line, index) => $"{index + 1}:{BuildLinePreview(line)}");
        return string.Join(" | ", previewLines);
    }

    private sealed record CandidateAnalysis(
        int Score,
        int NormalizedLength,
        int NonEmptyLineCount,
        bool SuspiciousLeadingPreamble,
        int LeadingNonEmptyLineCount,
        int LeadingUiLikeLineCount,
        int LeadingBodyLikeLineCount,
        int LeadingMarkerHitCount,
        string? FirstNonEmptyLinePreview,
        string? LeadingLinesPreview);
}
