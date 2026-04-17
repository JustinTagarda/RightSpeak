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

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerStopwatch = Stopwatch.StartNew();
            var result = await provider.TryGetParagraphTextAsync(cancellationToken).ConfigureAwait(false);
            providerStopwatch.Stop();
            AppDiagnostics.Info(
                "paragraph_retrieval_provider_result",
                new Dictionary<string, string?>
                {
                    ["provider"] = provider.GetType().Name,
                    ["success"] = result.Success.ToString(),
                    ["source"] = result.Source?.ToString(),
                    ["message"] = result.Message,
                    ["textLength"] = result.Text?.Length.ToString(),
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
                        ["source"] = result.Source?.ToString(),
                        ["textLength"] = result.Text?.Length.ToString(),
                        ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
                    });
                return result;
            }

            lastFailure = result;
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
        return lastFailure ?? TextRetrievalResult.Failed("Paragraph-text retrieval is unavailable.");
    }
}
