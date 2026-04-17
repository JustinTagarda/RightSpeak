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
                ["providerCount"] = _providers.Count.ToString()
            });

        TextRetrievalResult? lastFailure = null;
        var failureDetails = new List<string>();
        var shouldRetry = false;

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerStopwatch = Stopwatch.StartNew();
            var result = await provider.TryGetDocumentTextAsync(cancellationToken).ConfigureAwait(false);
            providerStopwatch.Stop();
            AppDiagnostics.Info(
                "document_retrieval_provider_result",
                new Dictionary<string, string?>
                {
                    ["provider"] = provider.GetType().Name,
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
                    "document_retrieval_success",
                    new Dictionary<string, string?>
                    {
                        ["provider"] = provider.GetType().Name,
                        ["source"] = result.Source?.ToString(),
                        ["textLength"] = result.Text?.Length.ToString(),
                        ["textPreview"] = BuildPreview(result.Text),
                        ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
                    });
                return result;
            }

            lastFailure = result;
            shouldRetry |= result.ShouldRetry;
            var source = result.Source?.ToString() ?? provider.GetType().Name;
            var message = string.IsNullOrWhiteSpace(result.Message) ? "No details." : result.Message;
            failureDetails.Add($"{source}: {message}");
        }

        overallStopwatch.Stop();
        AppDiagnostics.Warn(
            "document_retrieval_all_failed",
            new Dictionary<string, string?>
            {
                ["summary"] = string.Join(" | ", failureDetails.Where(detail => !string.IsNullOrWhiteSpace(detail))),
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
}
