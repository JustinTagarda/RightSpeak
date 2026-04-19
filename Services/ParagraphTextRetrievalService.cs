using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class ParagraphTextRetrievalService : IParagraphTextRetrievalService
{
    private const int BrowserPdfMaximumAcceptedParagraphCharacters = 700;
    private const int ParagraphMaximumAcceptedCharacters = 2200;
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

    private readonly IReadOnlyList<IParagraphTextProvider> _providers;

    public ParagraphTextRetrievalService(IReadOnlyList<IParagraphTextProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<TextRetrievalResult> RetrieveParagraphTextAsync(CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
        {
            return TextRetrievalResult.Failed("No paragraph-text providers are configured.");
        }

        var overallStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "paragraph_retrieval_started",
            new Dictionary<string, string?>
            {
                ["providerCount"] = _providers.Count.ToString(),
                ["providerOrder"] = string.Join(" -> ", _providers.Select(provider => provider.GetType().Name))
            });

        TextRetrievalResult? lastFailure = null;
        var failureDetails = new List<string>();
        var shouldRetry = false;

        for (var providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = _providers[providerIndex];

            var providerStopwatch = Stopwatch.StartNew();
            var result = await provider.TryGetParagraphTextAsync(cancellationToken).ConfigureAwait(false);
            providerStopwatch.Stop();
            AppDiagnostics.Info(
                "paragraph_retrieval_provider_result",
                new Dictionary<string, string?>
                {
                    ["provider"] = provider.GetType().Name,
                    ["providerIndex"] = providerIndex.ToString(),
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
                var candidate = AnalyzeCandidate(result);
                AppDiagnostics.Info(
                    "paragraph_retrieval_candidate_scored",
                    new Dictionary<string, string?>
                    {
                        ["provider"] = provider.GetType().Name,
                        ["providerIndex"] = providerIndex.ToString(),
                        ["source"] = result.Source?.ToString(),
                        ["accepted"] = candidate.Accepted.ToString(),
                        ["rejectionReason"] = candidate.RejectionReason,
                        ["normalizedLength"] = candidate.NormalizedLength.ToString(),
                        ["nonEmptyLineCount"] = candidate.NonEmptyLineCount.ToString(),
                        ["viewerMarkerHitCount"] = candidate.ViewerMarkerHitCount.ToString(),
                        ["preview"] = candidate.Preview,
                        ["message"] = result.Message
                    });

                if (!candidate.Accepted)
                {
                    lastFailure = TextRetrievalResult.Failed(
                        candidate.RejectionReason ?? "Paragraph candidate was rejected as low-confidence.",
                        result.Source,
                        shouldRetry: result.ShouldRetry);
                    shouldRetry |= result.ShouldRetry;
                    AppDiagnostics.Warn(
                        "paragraph_retrieval_candidate_rejected",
                        new Dictionary<string, string?>
                        {
                            ["provider"] = provider.GetType().Name,
                            ["providerIndex"] = providerIndex.ToString(),
                            ["source"] = result.Source?.ToString(),
                            ["rejectionReason"] = candidate.RejectionReason,
                            ["normalizedLength"] = candidate.NormalizedLength.ToString(),
                            ["nonEmptyLineCount"] = candidate.NonEmptyLineCount.ToString(),
                            ["viewerMarkerHitCount"] = candidate.ViewerMarkerHitCount.ToString(),
                            ["preview"] = candidate.Preview
                        });
                    continue;
                }

                overallStopwatch.Stop();
                AppDiagnostics.Info(
                    "paragraph_retrieval_success",
                    new Dictionary<string, string?>
                    {
                        ["provider"] = provider.GetType().Name,
                        ["providerIndex"] = providerIndex.ToString(),
                        ["source"] = result.Source?.ToString(),
                        ["textLength"] = result.Text?.Length.ToString(),
                        ["textPreview"] = BuildPreview(result.Text),
                        ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
                    });
                return result;
            }

            lastFailure = result;
            shouldRetry |= result.ShouldRetry || IsRetryableParagraphFailure(result);
            var source = result.Source?.ToString() ?? provider.GetType().Name;
            var message = string.IsNullOrWhiteSpace(result.Message) ? "No details." : result.Message;
            failureDetails.Add($"{source}: {message}");
            AppDiagnostics.Warn(
                "paragraph_retrieval_provider_failed",
                new Dictionary<string, string?>
                {
                    ["provider"] = provider.GetType().Name,
                    ["providerIndex"] = providerIndex.ToString(),
                    ["source"] = result.Source?.ToString(),
                    ["message"] = message,
                    ["shouldRetry"] = result.ShouldRetry.ToString(),
                    ["retryableByHeuristic"] = IsRetryableParagraphFailure(result).ToString()
                });
        }

        overallStopwatch.Stop();
        AppDiagnostics.Warn(
            "paragraph_retrieval_all_failed",
            new Dictionary<string, string?>
            {
                ["summary"] = string.Join(" | ", failureDetails.Where(detail => !string.IsNullOrWhiteSpace(detail))),
                ["shouldRetry"] = shouldRetry.ToString(),
                ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
            });
        return (lastFailure ?? TextRetrievalResult.Failed("Paragraph-text retrieval is unavailable."))
            .WithRetrySuggested(shouldRetry);
    }

    private static bool IsRetryableParagraphFailure(TextRetrievalResult result)
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
            TextRetrievalSource.UiAutomationParagraph =>
                message.Equals("No focused element is available for paragraph retrieval.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("No insertion point or selection found for paragraph retrieval.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("UI Automation returned an empty paragraph.", StringComparison.OrdinalIgnoreCase),
            TextRetrievalSource.FocusedControl =>
                message.Equals("No focused control is available for paragraph retrieval.", StringComparison.OrdinalIgnoreCase),
            TextRetrievalSource.ClipboardFallback =>
                message.Equals("No focused control is available for clipboard paragraph fallback.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard paragraph fallback failed: unable to read current clipboard safely.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard paragraph fallback timed out waiting for selected text copy.", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
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

    private static ParagraphCandidateAnalysis AnalyzeCandidate(TextRetrievalResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return new ParagraphCandidateAnalysis(false, 0, 0, 0, "empty_after_normalization", null);
        }

        var normalized = result.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            return new ParagraphCandidateAnalysis(false, 0, 0, 0, "empty_after_normalization", null);
        }

        var lines = normalized
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var viewerMarkerHitCount = lines
            .Take(8)
            .Sum(line => ViewerUiMarkers.Count(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)));
        var preview = BuildPreview(normalized);
        var message = result.Message ?? string.Empty;

        if (normalized.Length > ParagraphMaximumAcceptedCharacters)
        {
            return new ParagraphCandidateAnalysis(false, normalized.Length, lines.Length, viewerMarkerHitCount, "paragraph_candidate_exceeds_maximum_length", preview);
        }

        if (message.IndexOf("browser PDF", StringComparison.OrdinalIgnoreCase) >= 0 &&
            normalized.Length > BrowserPdfMaximumAcceptedParagraphCharacters)
        {
            return new ParagraphCandidateAnalysis(false, normalized.Length, lines.Length, viewerMarkerHitCount, "browser_pdf_paragraph_candidate_exceeds_maximum_length", preview);
        }

        if (result.Source == TextRetrievalSource.FocusedControl &&
            message.IndexOf("focused control value", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (normalized.Length > 650 || lines.Length > 3))
        {
            return new ParagraphCandidateAnalysis(false, normalized.Length, lines.Length, viewerMarkerHitCount, "focused_value_pattern_candidate_scope_too_wide", preview);
        }

        if (viewerMarkerHitCount >= 3 && normalized.Length < 400)
        {
            return new ParagraphCandidateAnalysis(false, normalized.Length, lines.Length, viewerMarkerHitCount, "paragraph_candidate_looks_like_viewer_ui", preview);
        }

        return new ParagraphCandidateAnalysis(true, normalized.Length, lines.Length, viewerMarkerHitCount, null, preview);
    }

    private sealed record ParagraphCandidateAnalysis(
        bool Accepted,
        int NormalizedLength,
        int NonEmptyLineCount,
        int ViewerMarkerHitCount,
        string? RejectionReason,
        string? Preview);
}
