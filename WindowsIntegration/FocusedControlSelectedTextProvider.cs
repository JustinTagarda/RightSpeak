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
                AppDiagnostics.Warn("selected_workflow_focused_control_missing");
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available.", Source));
            }

            var focusedControlType = focusedElement.Current.ControlType?.ProgrammaticName;
            var focusedName = focusedElement.Current.Name;
            var focusedClass = focusedElement.Current.ClassName;
            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var selectedRanges = textPattern.GetSelection();
                if (selectedRanges is not null && selectedRanges.Length > 0)
                {
                    var rangeCount = selectedRanges.Length;
                    var selectedText = string.Join(
                        Environment.NewLine,
                        selectedRanges
                            .Select(range => Normalize(range.GetText(-1)))
                            .Where(text => !string.IsNullOrWhiteSpace(text)));

                    if (!string.IsNullOrWhiteSpace(selectedText))
                    {
                        if (ShouldDeferBrowserPdfSelectionToClipboard(focusedElement, focusedControlType, focusedName))
                        {
                            AppDiagnostics.Info(
                                "selected_workflow_focused_control_pdf_deferred_to_clipboard",
                                new Dictionary<string, string?>
                                {
                                    ["rangeCount"] = rangeCount.ToString(),
                                    ["textLength"] = selectedText.Length.ToString(),
                                    ["controlType"] = focusedControlType,
                                    ["name"] = focusedName,
                                    ["className"] = focusedClass
                                });
                            return Task.FromResult(
                                TextRetrievalResult.Failed(
                                    "Focused-control PDF selection in browser can be partial; trying clipboard fallback.",
                                    Source));
                        }

                        AppDiagnostics.Info(
                            "selected_workflow_focused_control_success",
                            new Dictionary<string, string?>
                            {
                                ["rangeCount"] = rangeCount.ToString(),
                                ["textLength"] = selectedText.Length.ToString(),
                                ["controlType"] = focusedControlType,
                                ["name"] = focusedName,
                                ["className"] = focusedClass
                            });
                        return Task.FromResult(
                            TextRetrievalResult.Retrieved(
                                selectedText,
                                Source,
                                "Selected text retrieved from focused control selection."));
                    }

                    AppDiagnostics.Warn(
                        "selected_workflow_focused_control_empty_selection",
                        new Dictionary<string, string?>
                        {
                            ["rangeCount"] = rangeCount.ToString(),
                            ["controlType"] = focusedControlType,
                            ["name"] = focusedName,
                            ["className"] = focusedClass
                        });
                }
                else
                {
                    AppDiagnostics.Info(
                        "selected_workflow_focused_control_no_ranges",
                        new Dictionary<string, string?>
                        {
                            ["controlType"] = focusedControlType,
                            ["name"] = focusedName,
                            ["className"] = focusedClass
                        });
                }
            }
            else
            {
                AppDiagnostics.Info(
                    "selected_workflow_focused_control_text_pattern_unavailable",
                    new Dictionary<string, string?>
                    {
                        ["controlType"] = focusedControlType,
                        ["name"] = focusedName,
                        ["className"] = focusedClass
                    });
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
