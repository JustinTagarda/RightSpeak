using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        var operationId = Guid.NewGuid().ToString("N");
        var overallStopwatch = Stopwatch.StartNew();
        AppDiagnostics.Info(
            "selected_workflow_retrieval_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["providerCount"] = _providers.Count.ToString(),
                ["providerOrder"] = string.Join(" -> ", _providers.Select(provider => provider.GetType().Name))
            });

        TextRetrievalResult? lastFailure = null;
        var failureDetails = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerStopwatch = Stopwatch.StartNew();
            var result = await provider.TryGetSelectedTextAsync(cancellationToken).ConfigureAwait(false);
            providerStopwatch.Stop();
            AppDiagnostics.Info(
                "selected_workflow_provider_result",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
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
                    "selected_workflow_retrieval_success",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["provider"] = provider.GetType().Name,
                        ["source"] = result.Source?.ToString(),
                        ["message"] = result.Message,
                        ["textLength"] = result.Text?.Length.ToString(),
                        ["textPreview"] = BuildPreview(result.Text),
                        ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
                    });
                return result;
            }

            lastFailure = result;
            var source = result.Source?.ToString() ?? provider.GetType().Name;
            var message = string.IsNullOrWhiteSpace(result.Message) ? "No details." : result.Message;
            failureDetails.Add($"{source}: {message}");
            AppDiagnostics.Warn(
                "selected_workflow_provider_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["provider"] = provider.GetType().Name,
                    ["source"] = result.Source?.ToString(),
                    ["message"] = message
                });
        }

        if (failureDetails.Count > 0)
        {
            overallStopwatch.Stop();
            var summary = string.Join(" | ", failureDetails.Where(detail => !string.IsNullOrWhiteSpace(detail)));
            AppDiagnostics.Warn(
                "selected_workflow_retrieval_all_failed",
                new Dictionary<string, string?>
                {
                    ["operationId"] = operationId,
                    ["summary"] = summary,
                    ["elapsedMs"] = overallStopwatch.ElapsedMilliseconds.ToString()
                });
            return TextRetrievalResult.Failed($"Selected-text retrieval failed across all strategies. {summary}");
        }

        return lastFailure ?? TextRetrievalResult.Failed("Selected-text retrieval is unavailable.");
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
