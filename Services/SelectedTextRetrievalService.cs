using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Interop;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class SelectedTextRetrievalService : ISelectedTextRetrievalService
{
    private static readonly string[] BrowserProcessNames =
    {
        "chrome",
        "msedge",
        "firefox"
    };

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
        var providerSequence = ResolveProviderSequence();
        AppDiagnostics.Info(
            "selected_workflow_retrieval_started",
            new Dictionary<string, string?>
            {
                ["operationId"] = operationId,
                ["providerCount"] = providerSequence.Count.ToString(),
                ["providerOrder"] = string.Join(" -> ", providerSequence.Select(provider => provider.GetType().Name))
            });

        TextRetrievalResult? lastFailure = null;
        var failureDetails = new List<string>();
        var shouldRetry = false;

        foreach (var provider in providerSequence)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerStopwatch = Stopwatch.StartNew();
            TextRetrievalResult result;
            try
            {
                result = await provider.TryGetSelectedTextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                providerStopwatch.Stop();
                AppDiagnostics.Error(
                    "selected_workflow_provider_exception",
                    new Dictionary<string, string?>
                    {
                        ["operationId"] = operationId,
                        ["provider"] = provider.GetType().Name,
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message,
                        ["elapsedMs"] = providerStopwatch.ElapsedMilliseconds.ToString()
                    });
                result = TextRetrievalResult.Failed(
                    $"Provider exception: {ex.Message}",
                    provider.Source,
                    shouldRetry: true);
            }

            providerStopwatch.Stop();
            var retryableByHeuristic = IsRetryableSelectedFailure(result);
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
                    ["shouldRetry"] = result.ShouldRetry.ToString(),
                    ["retryableByHeuristic"] = retryableByHeuristic.ToString(),
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
            shouldRetry |= result.ShouldRetry || retryableByHeuristic;
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
                    ["message"] = message,
                    ["shouldRetry"] = result.ShouldRetry.ToString(),
                    ["retryableByHeuristic"] = retryableByHeuristic.ToString()
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
            return TextRetrievalResult.Failed(
                $"Selected-text retrieval failed across all strategies. {summary}",
                shouldRetry: shouldRetry);
        }

        return (lastFailure ?? TextRetrievalResult.Failed("Selected-text retrieval is unavailable."))
            .WithRetrySuggested(shouldRetry);
    }

    private IReadOnlyList<ISelectedTextProvider> ResolveProviderSequence()
    {
        if (!ShouldBypassUiAutomationForForegroundBrowser())
        {
            return _providers;
        }

        var clipboardFirst = _providers
            .Where(static provider => provider.Source == TextRetrievalSource.ClipboardFallback)
            .Concat(_providers.Where(static provider => provider.Source != TextRetrievalSource.ClipboardFallback))
            .ToArray();

        AppDiagnostics.Info(
            "selected_workflow_browser_provider_bypass_enabled",
            new Dictionary<string, string?>
            {
                ["providerOrder"] = string.Join(" -> ", clipboardFirst.Select(provider => provider.GetType().Name))
            });
        return clipboardFirst;
    }

    private static bool ShouldBypassUiAutomationForForegroundBrowser()
    {
        try
        {
            var foregroundWindow = WindowFocusInterop.GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
                return false;
            }

            var windowClass = WindowFocusInterop.GetWindowClassName(foregroundWindow);
            if (!string.Equals(windowClass, "Chrome_WidgetWin_1", StringComparison.Ordinal))
            {
                return false;
            }

            WindowFocusInterop.GetWindowThreadProcessId(foregroundWindow, out var processId);
            if (processId == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)processId);
            return BrowserProcessNames.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "selected_workflow_browser_bypass_probe_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return false;
        }
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

    private static bool IsRetryableSelectedFailure(TextRetrievalResult result)
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
            TextRetrievalSource.UiAutomationSelection =>
                message.Equals("No focused element is available for UI Automation.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Focused element does not expose text selection through UI Automation.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("No selected text was found via UI Automation.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("UI Automation returned an empty text selection.", StringComparison.OrdinalIgnoreCase),
            TextRetrievalSource.FocusedControl =>
                message.Equals("No focused control is available.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Focused control does not expose selected text through supported UI Automation patterns.", StringComparison.OrdinalIgnoreCase),
            TextRetrievalSource.ClipboardFallback =>
                message.Equals("Clipboard fallback failed: unable to read current clipboard safely.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard fallback failed: no foreground window to copy from.", StringComparison.OrdinalIgnoreCase) ||
                message.Equals("Clipboard fallback timed out waiting for copied text.", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
