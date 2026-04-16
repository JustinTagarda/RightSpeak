using System;
using System.Linq;
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

            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var selectedRanges = textPattern.GetSelection();
                if (selectedRanges is not null && selectedRanges.Length > 0)
                {
                    var selectedText = string.Join(
                        Environment.NewLine,
                        selectedRanges
                            .Select(range => Normalize(range.GetText(-1)))
                            .Where(text => !string.IsNullOrWhiteSpace(text)));

                    if (!string.IsNullOrWhiteSpace(selectedText))
                    {
                        return Task.FromResult(
                            TextRetrievalResult.Retrieved(
                                selectedText,
                                Source,
                                "Selected text retrieved from focused control selection."));
                    }
                }
            }

            return Task.FromResult(
                TextRetrievalResult.Failed(
                    "Focused control does not expose selected text through supported UI Automation patterns.",
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
