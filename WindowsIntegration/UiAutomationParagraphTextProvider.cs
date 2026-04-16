using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class UiAutomationParagraphTextProvider : IParagraphTextProvider
{
    public Task<TextRetrievalResult> TryGetParagraphTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                return Task.FromResult(TextRetrievalResult.Failed("No focused element is available for paragraph retrieval.", TextRetrievalSource.UiAutomationParagraph));
            }

            if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) || textPatternObject is not TextPattern textPattern)
            {
                return Task.FromResult(TextRetrievalResult.Failed("Focused element does not expose text through UI Automation.", TextRetrievalSource.UiAutomationParagraph));
            }

            var selectedRanges = textPattern.GetSelection();
            if (selectedRanges is null || selectedRanges.Length == 0)
            {
                return Task.FromResult(TextRetrievalResult.Failed("No insertion point or selection found for paragraph retrieval.", TextRetrievalSource.UiAutomationParagraph));
            }

            var paragraphCandidates = string.Join(
                Environment.NewLine,
                selectedRanges
                    .Select(range => range.GetText(-1)?.Trim('\0', '\r', '\n', ' ', '\t'))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Distinct(StringComparer.Ordinal));

            if (string.IsNullOrWhiteSpace(paragraphCandidates))
            {
                return Task.FromResult(TextRetrievalResult.Failed("UI Automation returned an empty paragraph.", TextRetrievalSource.UiAutomationParagraph));
            }

            return Task.FromResult(
                TextRetrievalResult.Retrieved(
                    paragraphCandidates,
                    TextRetrievalSource.UiAutomationParagraph,
                    "Paragraph candidate retrieved via UI Automation selection."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(TextRetrievalResult.Failed($"Paragraph retrieval failed: {ex.Message}", TextRetrievalSource.UiAutomationParagraph));
        }
    }
}
