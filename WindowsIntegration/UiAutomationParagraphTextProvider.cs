using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
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
                AppDiagnostics.Warn("paragraph_provider_uia_no_focused_element");
                return Task.FromResult(TextRetrievalResult.Failed("No focused element is available for paragraph retrieval.", TextRetrievalSource.UiAutomationParagraph));
            }

            if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) || textPatternObject is not TextPattern textPattern)
            {
                AppDiagnostics.Info(
                    "paragraph_provider_uia_text_pattern_unavailable",
                    new Dictionary<string, string?>
                    {
                        ["automationId"] = focusedElement.Current.AutomationId,
                        ["className"] = focusedElement.Current.ClassName,
                        ["controlType"] = focusedElement.Current.ControlType?.ProgrammaticName,
                        ["name"] = focusedElement.Current.Name
                    });
                return Task.FromResult(TextRetrievalResult.Failed("Focused element does not expose text through UI Automation.", TextRetrievalSource.UiAutomationParagraph));
            }

            var selectedRanges = textPattern.GetSelection();
            if (selectedRanges is null || selectedRanges.Length == 0)
            {
                AppDiagnostics.Warn("paragraph_provider_uia_no_selection_ranges");
                return Task.FromResult(TextRetrievalResult.Failed("No insertion point or selection found for paragraph retrieval.", TextRetrievalSource.UiAutomationParagraph));
            }

            var paragraphCandidates = string.Join(Environment.NewLine, BuildParagraphCandidates(selectedRanges));

            if (string.IsNullOrWhiteSpace(paragraphCandidates))
            {
                AppDiagnostics.Warn(
                    "paragraph_provider_uia_candidates_empty",
                    new Dictionary<string, string?>
                    {
                        ["selectionRangeCount"] = selectedRanges.Length.ToString()
                    });
                return Task.FromResult(TextRetrievalResult.Failed("UI Automation returned an empty paragraph.", TextRetrievalSource.UiAutomationParagraph));
            }

            AppDiagnostics.Info(
                "paragraph_provider_uia_candidates_built",
                new Dictionary<string, string?>
                {
                    ["selectionRangeCount"] = selectedRanges.Length.ToString(),
                    ["textLength"] = paragraphCandidates.Length.ToString()
                });
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
            AppDiagnostics.Error(
                "paragraph_provider_uia_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return Task.FromResult(TextRetrievalResult.Failed($"Paragraph retrieval failed: {ex.Message}", TextRetrievalSource.UiAutomationParagraph));
        }
    }

    private static IReadOnlyList<string> BuildParagraphCandidates(IReadOnlyList<TextPatternRange> ranges)
    {
        var candidates = new List<string>();

        foreach (var range in ranges)
        {
            var paragraphText = TryExpandAndRead(range, TextUnit.Paragraph);
            if (!string.IsNullOrWhiteSpace(paragraphText))
            {
                candidates.Add(paragraphText);
                continue;
            }

            var lineText = TryExpandAndRead(range, TextUnit.Line);
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                candidates.Add(lineText);
            }
        }

        return candidates
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
}
