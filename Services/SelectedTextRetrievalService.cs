using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class SelectedTextRetrievalService : ISelectedTextRetrievalService
{
    private readonly IReadOnlyList<ISelectedTextProvider> _providers;

    public SelectedTextRetrievalService(IReadOnlyList<ISelectedTextProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<TextRetrievalResult> RetrieveSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
        {
            return TextRetrievalResult.Failed("No selected-text providers are configured.");
        }

        TextRetrievalResult? lastFailure = null;
        var failureDetails = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await provider.TryGetSelectedTextAsync(cancellationToken).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                AppDiagnostics.Info(
                    "selected_text_retrieval_success",
                    new Dictionary<string, string?>
                    {
                        ["provider"] = provider.GetType().Name,
                        ["source"] = result.Source?.ToString(),
                        ["message"] = result.Message
                    });
                return result;
            }

            lastFailure = result;
            var source = result.Source?.ToString() ?? provider.GetType().Name;
            var message = string.IsNullOrWhiteSpace(result.Message) ? "No details." : result.Message;
            failureDetails.Add($"{source}: {message}");
            AppDiagnostics.Warn(
                "selected_text_retrieval_provider_failed",
                new Dictionary<string, string?>
                {
                    ["provider"] = provider.GetType().Name,
                    ["source"] = result.Source?.ToString(),
                    ["message"] = message
                });
        }

        if (failureDetails.Count > 0)
        {
            var summary = string.Join(" | ", failureDetails.Where(detail => !string.IsNullOrWhiteSpace(detail)));
            AppDiagnostics.Warn(
                "selected_text_retrieval_all_failed",
                new Dictionary<string, string?>
                {
                    ["summary"] = summary
                });
            return TextRetrievalResult.Failed($"Selected-text retrieval failed across all strategies. {summary}");
        }

        return lastFailure ?? TextRetrievalResult.Failed("Selected-text retrieval is unavailable.");
    }
}
