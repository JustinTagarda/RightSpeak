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
                ["providerCount"] = _providers.Count.ToString()
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
                    ["elapsedMs"] = providerStopwatch.ElapsedMilliseconds.ToString()
                });

            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
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
        }

        overallStopwatch.Stop();
        AppDiagnostics.Warn(
            "paragraph_retrieval_all_failed",
            new Dictionary<string, string?>
            {
                ["summary"] = string.Join(" | ", failureDetails.Where(detail => !string.IsNullOrWhiteSpace(detail))),
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
}
