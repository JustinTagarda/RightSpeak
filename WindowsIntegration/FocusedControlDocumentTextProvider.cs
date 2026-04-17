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
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available for document retrieval.", TextRetrievalSource.FocusedControlDocument));
            }

            if (IsBrowserProcess(focusedElement, out var browserProcessName))
            {
                AppDiagnostics.Info(
                    "document_retrieval_browser_prefers_clipboard_fallback",
                    BuildBrowserContextDiagnostics(focusedElement, browserProcessName));
                return Task.FromResult(
                    TextRetrievalResult.Failed(
                        "Focused-control document retrieval skipped for browser context; trying clipboard document fallback.",
                        TextRetrievalSource.FocusedControlDocument));
            }

            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var documentText = Normalize(textPattern.DocumentRange.GetText(-1));
                if (!string.IsNullOrWhiteSpace(documentText))
                {
                    var isBrowserPdfContext = IsBrowserPdfContext(focusedElement);
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
                                    ["removedLeadingLines"] = removedLines.ToString()
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
