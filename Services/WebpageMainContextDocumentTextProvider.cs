using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class WebpageMainContextDocumentTextProvider : IDocumentTextProvider
{
    private static readonly HashSet<string> AcceptedExtractionModes = new(StringComparer.Ordinal)
    {
        "google_docs_clipboard_document_chunks",
        "chatgpt_conversation_thread",
        "conversation_thread"
    };

    private readonly IWebpageMainContextAnalyzer _analyzer;

    public WebpageMainContextDocumentTextProvider(IWebpageMainContextAnalyzer analyzer)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public Task<TextRetrievalResult> TryGetDocumentTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var analysis = _analyzer.AnalyzeForegroundWindow();
            var core = analysis.MainContextCoreLogEntry;
            if (core is null)
            {
                AppDiagnostics.Info("webpage_document_provider_no_core");
                return Task.FromResult(TextRetrievalResult.Failed(
                    "Webpage main-context provider did not find a document core.",
                    TextRetrievalSource.WebpageMainContext));
            }

            AppDiagnostics.Info(
                "webpage_document_provider_core_evaluated",
                new Dictionary<string, string?>
                {
                    ["candidateName"] = core.CandidateName,
                    ["candidateScore"] = core.CandidateScore.ToString(),
                    ["pageType"] = core.PageType,
                    ["extractionMode"] = core.ExtractionMode,
                    ["normalizedLength"] = core.NormalizedLength.ToString(),
                    ["coreLength"] = core.CoreLength.ToString(),
                    ["keptLineCount"] = core.KeptLineCount.ToString(),
                    ["noiseLineCount"] = core.NoiseLineCount.ToString(),
                    ["chunkCount"] = core.ChunkCount.ToString(),
                    ["contentPreview"] = BuildPreview(core.CoreContent)
                });

            if (!AcceptedExtractionModes.Contains(core.ExtractionMode))
            {
                return Task.FromResult(TextRetrievalResult.Failed(
                    $"Webpage main-context mode '{core.ExtractionMode}' is not enabled for Read Document.",
                    TextRetrievalSource.WebpageMainContext));
            }

            if (!LooksLikeReadableDocumentCore(core, out var rejectionReason))
            {
                AppDiagnostics.Warn(
                    "webpage_document_provider_core_rejected",
                    new Dictionary<string, string?>
                    {
                        ["reason"] = rejectionReason,
                        ["candidateName"] = core.CandidateName,
                        ["pageType"] = core.PageType,
                        ["extractionMode"] = core.ExtractionMode,
                        ["coreLength"] = core.CoreLength.ToString(),
                        ["keptLineCount"] = core.KeptLineCount.ToString(),
                        ["chunkCount"] = core.ChunkCount.ToString(),
                        ["contentPreview"] = BuildPreview(core.CoreContent)
                    });
                return Task.FromResult(TextRetrievalResult.Failed(
                    $"Webpage main-context rejected: {rejectionReason}.",
                    TextRetrievalSource.WebpageMainContext,
                    shouldRetry: true));
            }

            AppDiagnostics.Info(
                "webpage_document_provider_success",
                new Dictionary<string, string?>
                {
                    ["candidateName"] = core.CandidateName,
                    ["pageType"] = core.PageType,
                    ["extractionMode"] = core.ExtractionMode,
                    ["coreLength"] = core.CoreLength.ToString(),
                    ["keptLineCount"] = core.KeptLineCount.ToString(),
                    ["chunkCount"] = core.ChunkCount.ToString(),
                    ["contentPreview"] = BuildPreview(core.CoreContent)
                });
            return Task.FromResult(TextRetrievalResult.Retrieved(
                core.CoreContent,
                TextRetrievalSource.WebpageMainContext,
                $"Document text retrieved from webpage main context ({core.ExtractionMode})."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "webpage_document_provider_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return Task.FromResult(TextRetrievalResult.Failed(
                $"Webpage main-context retrieval failed: {ex.Message}",
                TextRetrievalSource.WebpageMainContext));
        }
    }

    private static bool LooksLikeReadableDocumentCore(MainContextCoreLogEntry core, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        if (string.IsNullOrWhiteSpace(core.CoreContent))
        {
            rejectionReason = "empty_core";
            return false;
        }

        if (core.CoreLength < 180)
        {
            rejectionReason = "core_too_short";
            return false;
        }

        if (core.KeptLineCount < 3 && core.CoreLength < 800)
        {
            rejectionReason = "not_enough_document_lines";
            return false;
        }

        if (LooksLikeGoogleDocsAccessibilityPrompt(core.CoreContent))
        {
            rejectionReason = "google_docs_accessibility_prompt";
            return false;
        }

        if (core.Chunks.Count == 0)
        {
            rejectionReason = "no_chunks";
            return false;
        }

        if (core.Chunks.All(chunk => !chunk.IncludeByDefault))
        {
            rejectionReason = "no_default_chunks";
            return false;
        }

        return true;
    }

    private static bool LooksLikeGoogleDocsAccessibilityPrompt(string content)
    {
        var normalized = new string(content
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray())
            .ToLowerInvariant();
        return normalized.Contains("to enable screen reader support", StringComparison.Ordinal) &&
               normalized.Contains("keyboard shortcuts", StringComparison.Ordinal);
    }

    private static string BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        const int maxLength = 220;
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
