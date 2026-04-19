using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class FocusedControlDocumentTextProvider : IDocumentTextProvider
{
    private static readonly string[] BrowserProcessNames =
    {
        "chrome",
        "msedge"
    };

    private static readonly string[] BrowserPdfAccessibilityMarkers =
    {
        "accessibility",
        "screen reader",
        "pdf viewer",
        "toolbar",
        "zoom",
        "fit to page"
    };
    private static readonly string[] BrowserPdfViewerUiLineMarkers =
    {
        "toolbar",
        "zoom",
        "fit to page",
        "rotate",
        "print",
        "download",
        "open in",
        "document properties",
        "two page view",
        "single page view",
        "read aloud",
        "find in file",
        "show thumbnails",
        "page "
    };

    public Task<TextRetrievalResult> TryGetDocumentTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                AppDiagnostics.Warn("document_provider_focused_no_focused_element");
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available for document retrieval.", TextRetrievalSource.FocusedControlDocument));
            }

            var focusedDiagnostics = BuildFocusedElementDiagnostics(focusedElement);
            AppDiagnostics.Info("document_provider_focused_started", focusedDiagnostics);

            var isBrowserContext = IsBrowserProcess(focusedElement, out var browserProcessName);
            var isBrowserPdfContext = IsBrowserPdfContext(focusedElement);
            if (isBrowserContext)
            {
                var browserDiagnostics = BuildBrowserContextDiagnostics(focusedElement, browserProcessName);
                AppDiagnostics.Info(
                    "document_retrieval_browser_prefers_clipboard_fallback",
                    browserDiagnostics);
                AppDiagnostics.Info(
                    "document_retrieval_browser_context_detected",
                    browserDiagnostics);
            }

            if (isBrowserPdfContext)
            {
                var deferDiagnostics = BuildFocusedElementDiagnostics(focusedElement);
                deferDiagnostics["reason"] = "browser_pdf_document_prefers_clipboard_pipeline";
                AppDiagnostics.Info("document_provider_focused_pdf_deferred_to_clipboard", deferDiagnostics);
                return Task.FromResult(
                    TextRetrievalResult.Failed(
                        "Browser PDF document via focused-control UI Automation can drift; trying clipboard fallback.",
                        TextRetrievalSource.FocusedControlDocument));
            }

            var hasTextPattern =
                focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern;
            AppDiagnostics.Info(
                "document_provider_focused_pattern_probe",
                new Dictionary<string, string?>
                {
                    ["hasTextPattern"] = hasTextPattern.ToString(),
                    ["hasValuePattern"] = focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out var _) ? bool.TrueString : bool.FalseString,
                    ["isBrowserContext"] = isBrowserContext.ToString(),
                    ["isBrowserPdfContext"] = isBrowserPdfContext.ToString()
                });

            if (hasTextPattern && textPatternObject is TextPattern textPattern)
            {
                var rawDocumentText = textPattern.DocumentRange.GetText(-1);
                var documentText = Normalize(rawDocumentText);
                AppDiagnostics.Info(
                    "document_provider_focused_text_pattern_result",
                    new Dictionary<string, string?>
                    {
                        ["rawLength"] = rawDocumentText?.Length.ToString(),
                        ["normalizedLength"] = documentText?.Length.ToString(),
                        ["normalizedPreview"] = BuildPreview(documentText ?? string.Empty)
                    });

                if (!string.IsNullOrWhiteSpace(documentText))
                {
                    if (isBrowserPdfContext)
                    {
                        var sanitized = RemoveLeadingBrowserPdfViewerUi(documentText, out var removedLines);
                        if (!string.IsNullOrWhiteSpace(sanitized) &&
                            !string.Equals(sanitized, documentText, StringComparison.Ordinal))
                        {
                            AppDiagnostics.Info(
                                "document_retrieval_browser_pdf_sanitized",
                                new System.Collections.Generic.Dictionary<string, string?>
                                {
                                    ["originalLength"] = documentText.Length.ToString(),
                                    ["sanitizedLength"] = sanitized.Length.ToString(),
                                    ["removedLeadingLines"] = removedLines.ToString(),
                                    ["beforePreview"] = BuildPreview(documentText),
                                    ["afterPreview"] = BuildPreview(sanitized)
                                });
                            documentText = sanitized;
                        }
                    }

                    if (IsLikelyBrowserPdfAccessibilityBoilerplate(focusedElement, documentText))
                    {
                        AppDiagnostics.Warn(
                            "document_retrieval_filtered_browser_pdf_accessibility_boilerplate",
                            new System.Collections.Generic.Dictionary<string, string?>
                            {
                                ["textLength"] = documentText.Length.ToString(),
                                ["preview"] = BuildPreview(documentText)
                            });
                        return Task.FromResult(
                            TextRetrievalResult.Failed(
                                "Focused control returned browser PDF accessibility UI text instead of document content.",
                                TextRetrievalSource.FocusedControlDocument));
                    }

                    AppDiagnostics.Info(
                        "document_provider_focused_success",
                        new Dictionary<string, string?>
                        {
                            ["strategy"] = "text_pattern_document_range",
                            ["length"] = documentText.Length.ToString(),
                            ["preview"] = BuildPreview(documentText)
                        });
                    return Task.FromResult(
                        TextRetrievalResult.Retrieved(
                            documentText,
                            TextRetrievalSource.FocusedControlDocument,
                            "Document text retrieved from focused control document range."));
                }

                AppDiagnostics.Warn(
                    "document_provider_focused_text_pattern_empty_after_normalization",
                    new Dictionary<string, string?>
                    {
                        ["rawLength"] = rawDocumentText?.Length.ToString()
                    });
            }
            else
            {
                AppDiagnostics.Info(
                    "document_provider_focused_text_pattern_unavailable",
                    new Dictionary<string, string?>
                    {
                        ["isBrowserContext"] = isBrowserContext.ToString()
                    });
            }

            if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                var rawValue = valuePattern.Current.Value;
                var valueText = Normalize(valuePattern.Current.Value);
                AppDiagnostics.Info(
                    "document_provider_focused_value_pattern_result",
                    new Dictionary<string, string?>
                    {
                        ["rawLength"] = rawValue?.Length.ToString(),
                        ["normalizedLength"] = valueText?.Length.ToString(),
                        ["normalizedPreview"] = BuildPreview(valueText ?? string.Empty)
                    });
                if (!string.IsNullOrWhiteSpace(valueText))
                {
                    AppDiagnostics.Info(
                        "document_provider_focused_success",
                        new Dictionary<string, string?>
                        {
                            ["strategy"] = "value_pattern",
                            ["length"] = valueText.Length.ToString(),
                            ["preview"] = BuildPreview(valueText)
                        });
                    return Task.FromResult(
                        TextRetrievalResult.Retrieved(
                            valueText,
                            TextRetrievalSource.FocusedControlDocument,
                            "Document text retrieved from focused control value."));
                }

                AppDiagnostics.Warn("document_provider_focused_value_pattern_empty_after_normalization");
            }
            else
            {
                AppDiagnostics.Info("document_provider_focused_value_pattern_unavailable");
            }

            AppDiagnostics.Warn(
                "document_provider_focused_failed",
                new Dictionary<string, string?>
                {
                    ["reason"] = "no_supported_pattern_with_non_empty_text"
                });
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
            AppDiagnostics.Error(
                "document_provider_focused_exception",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
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

    private static bool IsBrowserProcess(AutomationElement focusedElement, out string processName)
    {
        processName = ResolveProcessName(focusedElement.Current.ProcessId) ?? string.Empty;
        return BrowserProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> BuildBrowserContextDiagnostics(AutomationElement focusedElement, string browserProcessName)
    {
        var diagnostics = new Dictionary<string, string?>
        {
            ["focusedProcessName"] = browserProcessName,
            ["focusedProcessId"] = focusedElement.Current.ProcessId.ToString(),
            ["focusedControlType"] = focusedElement.Current.ControlType?.ProgrammaticName,
            ["focusedElementName"] = BuildPreview(focusedElement.Current.Name ?? string.Empty),
            ["focusedElementClassName"] = focusedElement.Current.ClassName,
            ["topLevelTitleFromAutomation"] = ResolveTopLevelWindowTitle(focusedElement)
        };

        var foregroundHwnd = WindowFocusInterop.GetForegroundWindow();
        diagnostics["foregroundWindowHwnd"] = $"0x{foregroundHwnd.ToInt64():X}";
        diagnostics["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundHwnd);
        diagnostics["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundHwnd);
        WindowFocusInterop.GetWindowThreadProcessId(foregroundHwnd, out var foregroundProcessId);
        diagnostics["foregroundProcessId"] = foregroundProcessId.ToString();
        diagnostics["foregroundProcessName"] = ResolveProcessName((int)foregroundProcessId);
        return diagnostics;
    }

    private static Dictionary<string, string?> BuildFocusedElementDiagnostics(AutomationElement focusedElement)
    {
        var processId = focusedElement.Current.ProcessId;
        var diagnostics = new Dictionary<string, string?>
        {
            ["focusedProcessId"] = processId.ToString(),
            ["focusedProcessName"] = ResolveProcessName(processId),
            ["focusedControlType"] = focusedElement.Current.ControlType?.ProgrammaticName,
            ["focusedElementName"] = BuildPreview(focusedElement.Current.Name ?? string.Empty),
            ["focusedElementClassName"] = focusedElement.Current.ClassName,
            ["focusedAutomationId"] = focusedElement.Current.AutomationId,
            ["topLevelTitleFromAutomation"] = ResolveTopLevelWindowTitle(focusedElement),
            ["isPassword"] = focusedElement.Current.IsPassword.ToString()
        };

        var foregroundHwnd = WindowFocusInterop.GetForegroundWindow();
        diagnostics["foregroundWindowHwnd"] = $"0x{foregroundHwnd.ToInt64():X}";
        diagnostics["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundHwnd);
        diagnostics["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundHwnd);
        return diagnostics;
    }

    private static bool IsLikelyBrowserPdfAccessibilityBoilerplate(AutomationElement focusedElement, string text)
    {
        if (!IsBrowserPdfContext(focusedElement))
        {
            return false;
        }

        var lowered = text.ToLowerInvariant();
        var markerHits = BrowserPdfAccessibilityMarkers.Count(marker => lowered.Contains(marker, StringComparison.Ordinal));

        if (markerHits >= 3 && text.Length < 2200)
        {
            return true;
        }

        if (markerHits >= 2 && text.Length < 900)
        {
            return true;
        }

        return false;
    }

    private static bool IsBrowserPdfContext(AutomationElement focusedElement)
    {
        var processName = ResolveProcessName(focusedElement.Current.ProcessId);
        if (string.IsNullOrWhiteSpace(processName) ||
            !BrowserProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var topWindowTitle = ResolveTopLevelWindowTitle(focusedElement);
        if (string.IsNullOrWhiteSpace(topWindowTitle))
        {
            return false;
        }

        return topWindowTitle.Contains(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTopLevelWindowTitle(AutomationElement startElement)
    {
        try
        {
            var current = startElement;
            AutomationElement? parent = null;
            while (true)
            {
                parent = TreeWalker.ControlViewWalker.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent;
            }

            return current.Current.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string RemoveLeadingBrowserPdfViewerUi(string text, out int removedLeadingLines)
    {
        removedLeadingLines = 0;
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        while (lines.Count > 0)
        {
            var line = lines[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.RemoveAt(0);
                removedLeadingLines++;
                continue;
            }

            if (!LooksLikeBrowserPdfViewerUiLine(line))
            {
                break;
            }

            lines.RemoveAt(0);
            removedLeadingLines++;
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool LooksLikeBrowserPdfViewerUiLine(string line)
    {
        var lowered = line.ToLowerInvariant();
        if (lowered.Length <= 3)
        {
            return true;
        }

        if (BrowserPdfViewerUiLineMarkers.Any(marker => lowered.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        // Pure control glyph-like lines are not document content.
        var letterCount = line.Count(char.IsLetter);
        var symbolCount = line.Count(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch));
        if (letterCount == 0 && symbolCount > 0)
        {
            return true;
        }

        return false;
    }

    private static string? ResolveProcessName(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPreview(string text)
    {
        const int maxLength = 220;
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }
}
