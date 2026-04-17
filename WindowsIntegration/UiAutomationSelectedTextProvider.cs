using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                AppDiagnostics.Warn("selected_workflow_uia_no_focused_element");
                return Task.FromResult(TextRetrievalResult.Failed("No focused element is available for UI Automation.", Source));
            }

            var focusedControlType = focusedElement.Current.ControlType?.ProgrammaticName;
            var focusedName = focusedElement.Current.Name;
            var focusedClass = focusedElement.Current.ClassName;
            if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) || textPatternObject is not TextPattern textPattern)
            {
                AppDiagnostics.Info(
                    "selected_workflow_uia_text_pattern_unavailable",
                    new Dictionary<string, string?>
                    {
                        ["controlType"] = focusedControlType,
                        ["name"] = focusedName,
                        ["className"] = focusedClass
                    });
                return Task.FromResult(TextRetrievalResult.Failed("Focused element does not expose text selection through UI Automation.", Source));
            }

            var selectedRanges = textPattern.GetSelection();
            if (selectedRanges is null || selectedRanges.Length == 0)
            {
                AppDiagnostics.Info(
                    "selected_workflow_uia_no_selection_ranges",
                    new Dictionary<string, string?>
                    {
                        ["controlType"] = focusedControlType,
                        ["name"] = focusedName,
                        ["className"] = focusedClass
                    });
                return Task.FromResult(TextRetrievalResult.Failed("No selected text was found via UI Automation.", Source));
            }

            var rangeCount = selectedRanges.Length;
            var selectedText = string.Join(
                Environment.NewLine,
                selectedRanges
                    .Select(range => range.GetText(-1)?.Trim('\0', '\r', '\n', ' ', '\t'))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                AppDiagnostics.Warn(
                    "selected_workflow_uia_empty_selection_text",
                    new Dictionary<string, string?>
                    {
                        ["rangeCount"] = rangeCount.ToString(),
                        ["controlType"] = focusedControlType,
                        ["name"] = focusedName,
                        ["className"] = focusedClass
                    });
                return Task.FromResult(TextRetrievalResult.Failed("UI Automation returned an empty text selection.", Source));
            }

            if (ShouldDeferBrowserPdfSelectionToClipboard(focusedElement, focusedControlType, focusedName))
            {
                AppDiagnostics.Info(
                    "selected_workflow_uia_pdf_deferred_to_clipboard",
                    new Dictionary<string, string?>
                    {
                        ["controlType"] = focusedControlType,
                        ["name"] = focusedName,
                        ["className"] = focusedClass,
                        ["textLength"] = selectedText.Length.ToString()
                    });
                return Task.FromResult(
                    TextRetrievalResult.Failed(
                        "Browser PDF selection via UI Automation can be partial; trying clipboard fallback.",
                        Source));
            }

            AppDiagnostics.Info(
                "selected_workflow_uia_success",
                new Dictionary<string, string?>
                {
                    ["rangeCount"] = rangeCount.ToString(),
                    ["textLength"] = selectedText.Length.ToString(),
                    ["controlType"] = focusedControlType,
                    ["name"] = focusedName,
                    ["className"] = focusedClass
                });
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

    private static bool ShouldDeferBrowserPdfSelectionToClipboard(
        AutomationElement focusedElement,
        string? controlType,
        string? name)
    {
        var controlTypeIsDocument = string.Equals(controlType, "ControlType.Document", StringComparison.OrdinalIgnoreCase);
        if (!controlTypeIsDocument)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOf("PDF document", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        try
        {
            var processId = focusedElement.Current.ProcessId;
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            return string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
