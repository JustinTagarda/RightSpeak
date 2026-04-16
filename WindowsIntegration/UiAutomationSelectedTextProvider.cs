using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class UiAutomationSelectedTextProvider : ISelectedTextProvider
{
    public TextRetrievalSource Source => TextRetrievalSource.UiAutomationSelection;

    public Task<TextRetrievalResult> TryGetSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                return Task.FromResult(TextRetrievalResult.Failed("No focused element is available for UI Automation.", Source));
            }

            if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) || textPatternObject is not TextPattern textPattern)
            {
                return Task.FromResult(TextRetrievalResult.Failed("Focused element does not expose text selection through UI Automation.", Source));
            }

            var selectedRanges = textPattern.GetSelection();
            if (selectedRanges is null || selectedRanges.Length == 0)
            {
                return Task.FromResult(TextRetrievalResult.Failed("No selected text was found via UI Automation.", Source));
            }

            var selectedText = string.Join(
                Environment.NewLine,
                selectedRanges
                    .Select(range => range.GetText(-1)?.Trim('\0', '\r', '\n', ' ', '\t'))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return Task.FromResult(TextRetrievalResult.Failed("UI Automation returned an empty text selection.", Source));
            }

            return Task.FromResult(TextRetrievalResult.Retrieved(selectedText, Source, "Selected text retrieved via UI Automation."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(TextRetrievalResult.Failed($"UI Automation retrieval failed: {ex.Message}", Source));
        }
    }
}
