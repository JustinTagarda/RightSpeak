using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class FocusedControlDocumentTextProvider : IDocumentTextProvider
{
    public Task<TextRetrievalResult> TryGetDocumentTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available for document retrieval.", TextRetrievalSource.FocusedControlDocument));
            }

            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var documentText = Normalize(textPattern.DocumentRange.GetText(-1));
                if (!string.IsNullOrWhiteSpace(documentText))
                {
                    return Task.FromResult(
                        TextRetrievalResult.Retrieved(
                            documentText,
                            TextRetrievalSource.FocusedControlDocument,
                            "Document text retrieved from focused control document range."));
                }
            }

            if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                var valueText = Normalize(valuePattern.Current.Value);
                if (!string.IsNullOrWhiteSpace(valueText))
                {
                    return Task.FromResult(
                        TextRetrievalResult.Retrieved(
                            valueText,
                            TextRetrievalSource.FocusedControlDocument,
                            "Document text retrieved from focused control value."));
                }
            }

            return Task.FromResult(
                TextRetrievalResult.Failed(
                    "Focused control does not expose full document text through supported UI Automation patterns.",
                    TextRetrievalSource.FocusedControlDocument));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                TextRetrievalResult.Failed(
                    $"Document retrieval failed: {ex.Message}",
                    TextRetrievalSource.FocusedControlDocument));
        }
    }

    private static string? Normalize(string? value)
    {
        return value?.Trim('\0', '\r', '\n', ' ', '\t');
    }
}
