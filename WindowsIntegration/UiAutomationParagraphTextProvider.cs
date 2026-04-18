using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using RightSpeak.Interop;
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

            AppDiagnostics.Info("paragraph_provider_uia_started", BuildElementDiagnostics(focusedElement));

            if (ShouldDeferBrowserPdfParagraphToClipboard(focusedElement))
            {
                var deferData = BuildElementDiagnostics(focusedElement);
                deferData["reason"] = "browser_pdf_uia_paragraph_can_be_inaccurate";
                AppDiagnostics.Info("paragraph_provider_uia_pdf_deferred_to_clipboard", deferData);
                return Task.FromResult(
                    TextRetrievalResult.Failed(
                        "Browser PDF paragraph via UI Automation can be inaccurate; trying clipboard fallback.",
                        TextRetrievalSource.UiAutomationParagraph));
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
                var noSelectionData = BuildElementDiagnostics(focusedElement);
                noSelectionData["selectionRangeCount"] = "0";
                AppDiagnostics.Warn("paragraph_provider_uia_no_selection_ranges", noSelectionData);
                return Task.FromResult(TextRetrievalResult.Failed("No insertion point or selection found for paragraph retrieval.", TextRetrievalSource.UiAutomationParagraph));
            }

            var paragraphCandidates = string.Join(
                Environment.NewLine,
                BuildParagraphCandidates(selectedRanges, out var rangeDiagnostics));

            if (string.IsNullOrWhiteSpace(paragraphCandidates))
            {
                var emptyData = BuildElementDiagnostics(focusedElement);
                emptyData["selectionRangeCount"] = selectedRanges.Length.ToString();
                emptyData["rangeDiagnostics"] = string.Join(" | ", rangeDiagnostics);
                AppDiagnostics.Warn("paragraph_provider_uia_candidates_empty", emptyData);
                return Task.FromResult(TextRetrievalResult.Failed("UI Automation returned an empty paragraph.", TextRetrievalSource.UiAutomationParagraph));
            }

            var successData = BuildElementDiagnostics(focusedElement);
            successData["selectionRangeCount"] = selectedRanges.Length.ToString();
            successData["rangeDiagnostics"] = string.Join(" | ", rangeDiagnostics);
            successData["textLength"] = paragraphCandidates.Length.ToString();
            successData["textPreview"] = BuildPreview(paragraphCandidates);
            AppDiagnostics.Info("paragraph_provider_uia_candidates_built", successData);
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

    private static IReadOnlyList<string> BuildParagraphCandidates(
        IReadOnlyList<TextPatternRange> ranges,
        out IReadOnlyList<string> rangeDiagnostics)
    {
        var candidates = new List<string>();
        var diagnostics = new List<string>(ranges.Count);

        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            var rawSelection = TryReadSelectionText(range);
            var paragraphText = TryExpandAndRead(range, TextUnit.Paragraph);
            if (!string.IsNullOrWhiteSpace(paragraphText))
            {
                candidates.Add(paragraphText);
                diagnostics.Add($"index={index};selectionLength={rawSelection?.Length};path=paragraph;candidateLength={paragraphText.Length}");
                continue;
            }

            var lineText = TryExpandAndRead(range, TextUnit.Line);
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                candidates.Add(lineText);
                diagnostics.Add($"index={index};selectionLength={rawSelection?.Length};path=line;candidateLength={lineText.Length}");
                continue;
            }

            diagnostics.Add($"index={index};selectionLength={rawSelection?.Length};path=none;candidateLength=0");
        }

        rangeDiagnostics = diagnostics;
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

    private static string? TryReadSelectionText(TextPatternRange range)
    {
        try
        {
            return Normalize(range.GetText(-1));
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

    private static Dictionary<string, string?> BuildElementDiagnostics(AutomationElement element)
    {
        return new Dictionary<string, string?>
        {
            ["automationId"] = element.Current.AutomationId,
            ["className"] = element.Current.ClassName,
            ["controlType"] = element.Current.ControlType?.ProgrammaticName,
            ["name"] = element.Current.Name
        };
    }

    private static bool ShouldDeferBrowserPdfParagraphToClipboard(AutomationElement focusedElement)
    {
        try
        {
            var processId = focusedElement.Current.ProcessId;
            if (processId == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            if (!string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var foregroundWindow = ClipboardInterop.GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
                return false;
            }

            var windowClass = WindowFocusInterop.GetWindowClassName(foregroundWindow);
            var windowTitle = WindowFocusInterop.GetWindowText(foregroundWindow);
            if (!string.Equals(windowClass, "Chrome_WidgetWin_1", StringComparison.Ordinal))
            {
                return false;
            }

            return windowTitle.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }
}
