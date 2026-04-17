using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class FocusedControlParagraphTextProvider : IParagraphTextProvider
{
    private const int AncestorProbeDepth = 8;

    public Task<TextRetrievalResult> TryGetParagraphTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                AppDiagnostics.Warn("paragraph_provider_focused_no_focused_element");
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available for paragraph retrieval.", TextRetrievalSource.FocusedControl));
            }

            if (TryReadParagraphFromElementOrAncestors(focusedElement, out var paragraphText, out var sourceMessage) &&
                !string.IsNullOrWhiteSpace(paragraphText))
            {
                AppDiagnostics.Info(
                    "paragraph_provider_focused_success",
                    new System.Collections.Generic.Dictionary<string, string?>
                    {
                        ["sourceMessage"] = sourceMessage,
                        ["textLength"] = paragraphText.Length.ToString()
                    });
                return Task.FromResult(
                    TextRetrievalResult.Retrieved(
                        paragraphText,
                        TextRetrievalSource.FocusedControl,
                        sourceMessage));
            }

            AppDiagnostics.Warn("paragraph_provider_focused_unavailable_patterns");
            return Task.FromResult(
                TextRetrievalResult.Failed(
                    "Focused control does not expose paragraph text through supported UI Automation patterns.",
                    TextRetrievalSource.FocusedControl));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "paragraph_provider_focused_failed",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return Task.FromResult(
                TextRetrievalResult.Failed(
                    $"Focused-control paragraph retrieval failed: {ex.Message}",
                    TextRetrievalSource.FocusedControl));
        }
    }

    private static string? TryReadSelectionParagraph(TextPatternRange range)
    {
        var paragraph = TryExpandAndRead(range, TextUnit.Paragraph);
        if (!string.IsNullOrWhiteSpace(paragraph))
        {
            return paragraph;
        }

        return TryExpandAndRead(range, TextUnit.Line);
    }

    private static string? TryExpandAndRead(TextPatternRange range, TextUnit unit)
    {
        try
        {
            var expanded = range.Clone();
            expanded.ExpandToEnclosingUnit(unit);
            return Normalize(expanded.GetText(-1));
        }
        catch
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
    {
        return value?.Trim('\0', '\r', '\n', ' ', '\t');
    }

    private static bool TryReadParagraphFromElementOrAncestors(
        AutomationElement startElement,
        out string paragraphText,
        out string sourceMessage)
    {
        paragraphText = string.Empty;
        sourceMessage = string.Empty;

        var current = startElement;
        for (var depth = 0; depth <= AncestorProbeDepth && current is not null; depth++)
        {
            if (TryReadParagraphFromElement(current, out paragraphText, out sourceMessage))
            {
                return true;
            }

            current = SafeGetParent(current);
        }

        return false;
    }

    private static bool TryReadParagraphFromElement(
        AutomationElement element,
        out string paragraphText,
        out string sourceMessage)
    {
        paragraphText = string.Empty;
        sourceMessage = string.Empty;

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
            textPatternObject is TextPattern textPattern)
        {
            var selectionRanges = textPattern.GetSelection();
            if (selectionRanges is not null && selectionRanges.Length > 0)
            {
                var selectionParagraph = string.Join(
                    Environment.NewLine,
                    selectionRanges
                        .Select(TryReadSelectionParagraph)
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Distinct(StringComparer.Ordinal));

                if (!string.IsNullOrWhiteSpace(selectionParagraph))
                {
                    paragraphText = selectionParagraph;
                    sourceMessage = "Paragraph candidate retrieved from focused control text pattern selection.";
                    return true;
                }
            }
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
            valuePatternObject is ValuePattern valuePattern)
        {
            var valueText = Normalize(valuePattern.Current.Value);
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                paragraphText = valueText;
                sourceMessage = "Paragraph candidate retrieved from focused control value.";
                return true;
            }
        }

        return false;
    }

    private static AutomationElement? SafeGetParent(AutomationElement element)
    {
        try
        {
            return TreeWalker.ControlViewWalker.GetParent(element);
        }
        catch
        {
            return null;
        }
    }
}
