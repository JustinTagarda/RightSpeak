using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class FocusedControlSelectedTextProvider : ISelectedTextProvider
{
    public TextRetrievalSource Source => TextRetrievalSource.FocusedControl;

    public Task<TextRetrievalResult> TryGetSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available.", Source));
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
                            Source,
                            "Text retrieved from focused control value."));
                }
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
                            Source,
                            "Text retrieved from focused control document range."));
                }
            }

            return Task.FromResult(
                TextRetrievalResult.Failed(
                    "Focused control does not expose readable text through supported UI Automation patterns.",
                    Source));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(TextRetrievalResult.Failed($"Focused-control retrieval failed: {ex.Message}", Source));
        }
    }

    private static string? Normalize(string? value)
    {
        return value?.Trim('\0', '\r', '\n', ' ', '\t');
    }
}
