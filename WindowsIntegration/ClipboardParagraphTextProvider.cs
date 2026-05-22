using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class ClipboardParagraphTextProvider : IParagraphTextProvider
{
    private static readonly HashSet<string> CommonEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","and","are","as","at","audio","be","but","by","copy","details","downloaded","dropbox","email","files","finished","folder","for","from","give","has","have","he","her","his","i","if","in","invite","is","it","its","just","know","let","manager","of","on","or","our","process","project","send","shared","she","so","submission","that","the","their","them","there","they","this","through","to","transcript","transcripts","uploaded","wait","we","where","what","with","work","you","your"
    };
    private const int PollIntervalMilliseconds = 50;
    private const int PollTimeoutMilliseconds = 1200;
    private const int BrowserPdfPostCopySettleMilliseconds = 320;
    private const int BrowserPdfMaxCopyCycles = 3;
    private const int BrowserPdfShortSelectionThresholdCharacters = 420;
    private const int BrowserPdfMinimumAcceptedParagraphCharacters = 40;
    private const int GoogleDocsSelectionSettleMilliseconds = 120;
    private const int GoogleDocsParagraphBoundaryExpansionCount = 2;
    private const int ClipboardAccessRetries = 8;
    private const int ClipboardAccessRetryDelayMilliseconds = 40;

    public Task<TextRetrievalResult> TryGetParagraphTextAsync(CancellationToken cancellationToken = default)
    {
        return RunOnStaThreadAsync(() => TryGetParagraphText(cancellationToken), cancellationToken);
    }

    private TextRetrievalResult TryGetParagraphText(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var focusedElement = AutomationElement.FocusedElement;
        if (focusedElement is null)
        {
            AppDiagnostics.Warn("paragraph_provider_clipboard_no_focused_element");
            return TextRetrievalResult.Failed("No focused control is available for clipboard paragraph fallback.", TextRetrievalSource.ClipboardFallback);
        }

        var focusedData = BuildElementDiagnostics(focusedElement);
        AppDiagnostics.Info("paragraph_provider_clipboard_started", focusedData);

        var foregroundWindow = ClipboardInterop.GetForegroundWindow();
        var foregroundWindowClass = WindowFocusInterop.GetWindowClassName(foregroundWindow);
        var foregroundWindowTitle = WindowFocusInterop.GetWindowText(foregroundWindow);
        if (WindowFocusInterop.IsIgnoredReadTargetWindow(foregroundWindowClass, foregroundWindowTitle))
        {
            AppDiagnostics.Warn(
                "paragraph_provider_clipboard_ignored_foreground_window",
                new Dictionary<string, string?>
                {
                    ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                    ["foregroundWindowClass"] = foregroundWindowClass,
                    ["foregroundWindowTitle"] = foregroundWindowTitle
                });
            return TextRetrievalResult.Failed(
                "Paragraph capture is blocked because the current foreground window is a transient overlay, not the source app.",
                TextRetrievalSource.ClipboardFallback);
        }

        var isBrowserPdfContext = IsBrowserPdfContext(foregroundWindowClass, foregroundWindowTitle);
        var isGoogleDocsContext = IsGoogleDocsContext(foregroundWindowClass, foregroundWindowTitle, focusedElement);
        AppDiagnostics.Info(
            "paragraph_provider_clipboard_context",
            new Dictionary<string, string?>
            {
                ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                ["foregroundWindowClass"] = foregroundWindowClass,
                ["foregroundWindowTitle"] = foregroundWindowTitle,
                ["isBrowserPdfContext"] = isBrowserPdfContext.ToString(),
                ["isGoogleDocsContext"] = isGoogleDocsContext.ToString()
            });

        // Last-resort paragraph fallback: copy only current selection.
        // Do not issue select-all here to avoid page-wide reads in browsers.
        // Keep password fields excluded.
        if (focusedElement.Current.IsPassword)
        {
            AppDiagnostics.Warn("paragraph_provider_clipboard_password_field_blocked");
            return TextRetrievalResult.Failed(
                "Clipboard paragraph fallback is not allowed on password fields.",
                TextRetrievalSource.ClipboardFallback);
        }

        var originalSequence = ClipboardInterop.GetClipboardSequenceNumber();
        var originalSnapshotSucceeded = TryReadClipboardDataObject(out System.Windows.IDataObject? originalClipboard);
        if (!originalSnapshotSucceeded)
        {
            AppDiagnostics.Warn("paragraph_provider_clipboard_snapshot_failed");
            return TextRetrievalResult.Failed(
                "Clipboard paragraph fallback failed: unable to read current clipboard safely.",
                TextRetrievalSource.ClipboardFallback);
        }

        uint observedCopySequence = 0;
        string? paragraphText = null;
        string? failureMessage = null;
        var copySequenceTransitions = 0;

        try
        {
            if (isGoogleDocsContext)
            {
                if (TryCaptureGoogleDocsParagraphText(
                        foregroundWindow,
                        foregroundWindowTitle,
                        focusedElement,
                        originalSequence,
                        cancellationToken,
                        out paragraphText,
                        out observedCopySequence,
                        out failureMessage))
                {
                    // paragraphText already assigned.
                }
            }

            if (!isGoogleDocsContext)
            {
            var copyCycles = isBrowserPdfContext ? BrowserPdfMaxCopyCycles : 1;
            for (var cycle = 1; cycle <= copyCycles; cycle++)
            {
                var cyclePollCount = 0;
                var cycleObservedSequenceChanges = 0;
                AppDiagnostics.Info(
                    "paragraph_provider_clipboard_capture_cycle_started",
                    new Dictionary<string, string?>
                    {
                        ["copyCycle"] = cycle.ToString(),
                        ["maxCopyCycles"] = copyCycles.ToString(),
                        ["isBrowserPdfContext"] = isBrowserPdfContext.ToString(),
                        ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                        ["foregroundWindowClass"] = foregroundWindowClass,
                        ["foregroundWindowTitle"] = foregroundWindowTitle
                    });

                if (isBrowserPdfContext)
                {
                    var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
                    AppDiagnostics.Info(
                        "paragraph_provider_clipboard_capture_cycle_activation_attempted",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["activationResult"] = activationResult.ToString(),
                            ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                            ["foregroundWindowClass"] = foregroundWindowClass,
                            ["foregroundWindowTitle"] = foregroundWindowTitle
                        });

                    try
                    {
                        focusedElement.SetFocus();
                        AppDiagnostics.Info(
                            "paragraph_provider_clipboard_capture_cycle_focused_element_refocused",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["focusedElementControlType"] = focusedElement.Current.ControlType?.ProgrammaticName,
                                ["focusedElementClassName"] = focusedElement.Current.ClassName,
                                ["focusedElementName"] = BuildPreview(focusedElement.Current.Name)
                            });
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.Warn(
                            "paragraph_provider_clipboard_capture_cycle_refocus_failed",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["message"] = ex.Message
                            });
                    }
                }
                else if (isGoogleDocsContext)
                {
                    var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
                    AppDiagnostics.Info(
                        "paragraph_provider_clipboard_google_docs_activation_attempted",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["activationResult"] = activationResult.ToString(),
                            ["foregroundWindowTitle"] = foregroundWindowTitle
                        });

                    try
                    {
                        focusedElement.SetFocus();
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.Warn(
                            "paragraph_provider_clipboard_google_docs_refocus_failed",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["message"] = ex.Message
                            });
                    }

                    for (var index = 0; index < GoogleDocsParagraphBoundaryExpansionCount; index++)
                    {
                        ClipboardInterop.SendSelectToParagraphStartShortcut();
                        Thread.Sleep(GoogleDocsSelectionSettleMilliseconds);
                    }

                    for (var index = 0; index < GoogleDocsParagraphBoundaryExpansionCount; index++)
                    {
                        ClipboardInterop.SendSelectToParagraphEndShortcut();
                        Thread.Sleep(GoogleDocsSelectionSettleMilliseconds);
                    }

                    AppDiagnostics.Info(
                        "paragraph_provider_clipboard_google_docs_paragraph_selection_sent",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["shortcutSequence"] = "ctrl+shift+up(x2),ctrl+shift+down(x2)"
                        });
                }

                ClipboardInterop.SendCopyShortcut();
                AppDiagnostics.Info(
                    "paragraph_provider_clipboard_copy_sent",
                    new Dictionary<string, string?>
                    {
                        ["copyCycle"] = cycle.ToString(),
                        ["maxCopyCycles"] = copyCycles.ToString(),
                        ["originalSequence"] = originalSequence.ToString(),
                        ["pollTimeoutMs"] = PollTimeoutMilliseconds.ToString(),
                        ["pollIntervalMs"] = PollIntervalMilliseconds.ToString(),
                        ["isBrowserPdfContext"] = isBrowserPdfContext.ToString(),
                        ["isGoogleDocsContext"] = isGoogleDocsContext.ToString()
                    });

                var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMilliseconds);
                var pollCount = 0;
                var cycleCapturedText = string.Empty;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pollCount++;
                    cyclePollCount++;

                    var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                    if (currentSequence != originalSequence)
                    {
                        cycleObservedSequenceChanges++;
                        observedCopySequence = currentSequence;
                        if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                        {
                            cycleCapturedText = Normalize(copiedText) ?? string.Empty;
                            if (isGoogleDocsContext)
                            {
                                cycleCapturedText = NormalizeGoogleDocsParagraphCapture(cycleCapturedText);
                            }
                            AppDiagnostics.Info(
                                "paragraph_provider_clipboard_capture_observed",
                                new Dictionary<string, string?>
                                {
                                    ["copyCycle"] = cycle.ToString(),
                                    ["pollCount"] = pollCount.ToString(),
                                    ["copiedSequence"] = observedCopySequence.ToString(),
                                    ["capturedLength"] = cycleCapturedText.Length.ToString(),
                                    ["capturedPreview"] = BuildPreview(cycleCapturedText)
                                });
                            break;
                        }

                        AppDiagnostics.Info(
                            "paragraph_provider_clipboard_sequence_changed_without_text",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["copiedSequence"] = observedCopySequence.ToString(),
                                ["clipboardFormats"] = ReadClipboardFormatsSummary()
                            });
                    }

                    Thread.Sleep(PollIntervalMilliseconds);
                }

                if (isBrowserPdfContext && string.IsNullOrWhiteSpace(cycleCapturedText))
                {
                    ClipboardInterop.SendCopyShortcutCtrlInsert();
                    AppDiagnostics.Info(
                        "paragraph_provider_clipboard_copy_alt_shortcut_sent",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["shortcut"] = "Ctrl+Insert"
                        });

                    var altDeadline = DateTime.UtcNow.AddMilliseconds(700);
                    while (DateTime.UtcNow < altDeadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pollCount++;
                        cyclePollCount++;

                        var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                        if (currentSequence != originalSequence)
                        {
                            cycleObservedSequenceChanges++;
                            observedCopySequence = currentSequence;
                            if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                            {
                                cycleCapturedText = Normalize(copiedText) ?? string.Empty;
                                AppDiagnostics.Info(
                                    "paragraph_provider_clipboard_capture_observed",
                                    new Dictionary<string, string?>
                                    {
                                        ["copyCycle"] = cycle.ToString(),
                                        ["pollCount"] = pollCount.ToString(),
                                        ["copiedSequence"] = observedCopySequence.ToString(),
                                        ["capturedLength"] = cycleCapturedText.Length.ToString(),
                                        ["capturedPreview"] = BuildPreview(cycleCapturedText),
                                        ["capturePhase"] = "alt_copy_shortcut"
                                    });
                                break;
                            }
                        }

                        Thread.Sleep(PollIntervalMilliseconds);
                    }
                }

                if (string.IsNullOrWhiteSpace(cycleCapturedText))
                {
                    AppDiagnostics.Warn(
                        "paragraph_provider_clipboard_capture_cycle_timeout",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["maxCopyCycles"] = copyCycles.ToString(),
                            ["cyclePollCount"] = cyclePollCount.ToString(),
                            ["cycleObservedSequenceChanges"] = cycleObservedSequenceChanges.ToString(),
                            ["lastObservedSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString(),
                            ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(ClipboardInterop.GetForegroundWindow()),
                            ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(ClipboardInterop.GetForegroundWindow()),
                            ["focusedElementControlType"] = focusedElement.Current.ControlType?.ProgrammaticName,
                            ["focusedElementClassName"] = focusedElement.Current.ClassName,
                            ["focusedElementName"] = BuildPreview(focusedElement.Current.Name)
                        });
                }

                copySequenceTransitions += cycleObservedSequenceChanges;

                if (!string.IsNullOrWhiteSpace(cycleCapturedText))
                {
                    if (isBrowserPdfContext)
                    {
                        cycleCapturedText = CaptureBestClipboardTextWithinSettleWindow(
                            cycleCapturedText,
                            ref observedCopySequence,
                            cancellationToken);
                    }

                    if (string.IsNullOrWhiteSpace(paragraphText) || cycleCapturedText.Length > paragraphText.Length)
                    {
                        paragraphText = cycleCapturedText;
                    }
                }

                if (!isBrowserPdfContext || !ShouldRetryBrowserPdfCopy(cycle, copyCycles, paragraphText))
                {
                    break;
                }

                AppDiagnostics.Info(
                    "paragraph_provider_clipboard_retrying_pdf_copy",
                    new Dictionary<string, string?>
                    {
                        ["copyCycle"] = cycle.ToString(),
                        ["capturedLength"] = paragraphText?.Length.ToString(),
                        ["threshold"] = BrowserPdfShortSelectionThresholdCharacters.ToString()
                    });
            }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TextRetrievalResult.Failed(
                $"Clipboard paragraph fallback failed: {ex.Message}",
                TextRetrievalSource.ClipboardFallback);
        }

        if (observedCopySequence != 0)
        {
            if (isGoogleDocsContext)
            {
                WindowFocusInterop.TryActivateWindow(foregroundWindow);
                try
                {
                    focusedElement.SetFocus();
                }
                catch
                {
                }

                ClipboardInterop.SendRightArrowKey();
                AppDiagnostics.Info(
                    "paragraph_provider_clipboard_google_docs_selection_cleared",
                    new Dictionary<string, string?>
                    {
                        ["foregroundWindowTitle"] = foregroundWindowTitle,
                        ["clearSequence"] = "right_arrow"
                    });
            }

            var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
            if (currentSequence == observedCopySequence && !TryRestoreClipboard(originalClipboard))
            {
                AppDiagnostics.Warn("clipboard_paragraph_restore_failed");
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    return TextRetrievalResult.Retrieved(
                        paragraphText,
                        TextRetrievalSource.ClipboardFallback,
                        "Paragraph candidate retrieved via clipboard fallback. Restoring previous clipboard content failed.");
                }
            }
            else
            {
                AppDiagnostics.Info(
                    "paragraph_provider_clipboard_restore_outcome",
                    new Dictionary<string, string?>
                    {
                        ["observedCopySequence"] = observedCopySequence.ToString(),
                        ["currentSequence"] = currentSequence.ToString(),
                        ["restoreAttempted"] = (currentSequence == observedCopySequence).ToString(),
                        ["restoreSkippedSequenceChanged"] = (currentSequence != observedCopySequence).ToString()
                    });
            }
        }

        if (isBrowserPdfContext &&
            !string.IsNullOrWhiteSpace(paragraphText) &&
            IsLikelyBrowserPdfFragment(paragraphText))
        {
            AppDiagnostics.Warn(
                "paragraph_provider_clipboard_rejected_short_pdf_fragment",
                new Dictionary<string, string?>
                {
                    ["textLength"] = paragraphText.Length.ToString(),
                    ["textPreview"] = BuildPreview(paragraphText),
                    ["minimumAcceptedLength"] = BrowserPdfMinimumAcceptedParagraphCharacters.ToString(),
                    ["reason"] = "browser_pdf_fragment_not_full_paragraph"
                });

            failureMessage = "Clipboard paragraph fallback captured only a short PDF fragment, not a full paragraph.";
            paragraphText = null;
        }

        if (isBrowserPdfContext &&
            string.IsNullOrWhiteSpace(paragraphText) &&
            copySequenceTransitions == 0 &&
            FocusedControlParagraphTextProvider.TryReadBrowserPdfParagraphFromCursorPoint(
                focusedElement,
                out var pdfPointParagraphText,
                out var pdfPointSourceMessage,
                out var pdfPointProbeTrail))
        {
            paragraphText = pdfPointParagraphText;
            AppDiagnostics.Info(
                "paragraph_provider_clipboard_pdf_cursor_point_fallback_success",
                new Dictionary<string, string?>
                {
                    ["copySequenceTransitions"] = copySequenceTransitions.ToString(),
                    ["textLength"] = paragraphText.Length.ToString(),
                    ["textPreview"] = BuildPreview(paragraphText),
                    ["text"] = paragraphText,
                    ["sourceMessage"] = pdfPointSourceMessage,
                    ["probeTrail"] = pdfPointProbeTrail
                });
        }

        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
            AppDiagnostics.Info(
                "paragraph_provider_clipboard_success",
                new Dictionary<string, string?>
                {
                    ["textLength"] = paragraphText.Length.ToString(),
                    ["textPreview"] = BuildPreview(paragraphText),
                    ["text"] = paragraphText,
                    ["originalSequence"] = originalSequence.ToString(),
                    ["copiedSequence"] = observedCopySequence == 0 ? null : observedCopySequence.ToString(),
                    ["currentSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString()
                });
            return TextRetrievalResult.Retrieved(
                paragraphText,
                TextRetrievalSource.ClipboardFallback,
                "Paragraph candidate retrieved via clipboard fallback.");
        }

        if (!string.IsNullOrWhiteSpace(failureMessage))
        {
            return TextRetrievalResult.Failed(
                failureMessage,
                TextRetrievalSource.ClipboardFallback);
        }

        AppDiagnostics.Warn(
            "paragraph_provider_clipboard_timeout",
            new Dictionary<string, string?>
            {
                ["pollTimeoutMs"] = PollTimeoutMilliseconds.ToString(),
                ["pollIntervalMs"] = PollIntervalMilliseconds.ToString(),
                ["copySequenceTransitions"] = copySequenceTransitions.ToString(),
                ["originalSequence"] = originalSequence.ToString(),
                ["lastObservedSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString(),
                ["clipboardFormats"] = ReadClipboardFormatsSummary()
            });
        return TextRetrievalResult.Failed(
            "Clipboard paragraph fallback timed out waiting for selected text copy.",
            TextRetrievalSource.ClipboardFallback);
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

    private static bool IsBrowserPdfContext(string windowClass, string windowTitle)
    {
        if (!string.Equals(windowClass, "Chrome_WidgetWin_1", StringComparison.Ordinal))
        {
            return false;
        }

        return windowTitle.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGoogleDocsContext(string windowClass, string windowTitle, AutomationElement focusedElement)
    {
        if (!string.Equals(windowClass, "Chrome_WidgetWin_1", StringComparison.Ordinal))
        {
            return false;
        }

        return windowTitle.IndexOf("Google Docs", StringComparison.OrdinalIgnoreCase) >= 0 &&
               string.Equals(focusedElement.Current.ControlType?.ProgrammaticName, "ControlType.Edit", StringComparison.Ordinal) &&
               string.Equals(focusedElement.Current.Name, "Document content", StringComparison.Ordinal);
    }

    private static string NormalizeGoogleDocsParagraphCapture(string capturedText)
    {
        if (string.IsNullOrWhiteSpace(capturedText))
        {
            return string.Empty;
        }

        var lines = capturedText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Replace("\uFFFC", string.Empty, StringComparison.Ordinal).Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        lines = lines
            .Where(line => !LooksLikeGoogleDocsShellLine(line))
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var bodyLines = lines
            .Where(line => !LooksLikeGoogleDocsHeadingLine(line))
            .ToArray();
        if (bodyLines.Length == 0)
        {
            bodyLines = lines.ToArray();
        }

        return JoinGoogleDocsBodyLines(bodyLines);
    }

    private static bool LooksLikeGoogleDocsHeadingLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length <= 90 && (trimmed.EndsWith("?", StringComparison.Ordinal) || trimmed.EndsWith(":", StringComparison.Ordinal)))
        {
            return true;
        }

        return trimmed.Length <= 70 &&
               trimmed.Any(char.IsLetter) &&
               trimmed.Count(character => !char.IsLetter(character) || char.IsUpper(character)) >= Math.Max(1, trimmed.Count(char.IsLetter) - 4);
    }

    private static bool LooksLikeGoogleDocsShellLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Equals("Document content", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("Show tabs", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("outlines", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinGoogleDocsBodyLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (builder.Length == 0)
            {
                builder.Append(trimmed);
                continue;
            }

            var previousChar = builder[^1];
            var startsLower = char.IsLower(trimmed[0]);
            var previousTokenLength = GetTrailingTokenLength(builder);
            var joinWithoutSpace =
                previousChar == '-' ||
                (startsLower && previousTokenLength == 1 && char.IsLetter(previousChar));

            if (!joinWithoutSpace)
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder
            .ToString()
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static int GetTrailingTokenLength(System.Text.StringBuilder builder)
    {
        var length = 0;
        for (var index = builder.Length - 1; index >= 0; index--)
        {
            var character = builder[index];
            if (char.IsWhiteSpace(character))
            {
                break;
            }

            length++;
        }

        return length;
    }

    private static bool TryCaptureGoogleDocsParagraphText(
        nint foregroundWindow,
        string foregroundWindowTitle,
        AutomationElement focusedElement,
        uint originalSequence,
        CancellationToken cancellationToken,
        out string? paragraphText,
        out uint observedCopySequence,
        out string? failureMessage)
    {
        paragraphText = null;
        observedCopySequence = 0;
        failureMessage = null;

        if (!TryCaptureGoogleDocsSelectionCopy(
                foregroundWindow,
                foregroundWindowTitle,
                focusedElement,
                originalSequence,
                cancellationToken,
                "prefix",
                selectionAction: () =>
                {
                    for (var index = 0; index < GoogleDocsParagraphBoundaryExpansionCount; index++)
                    {
                        ClipboardInterop.SendSelectToParagraphStartShortcut();
                        Thread.Sleep(GoogleDocsSelectionSettleMilliseconds);
                    }
                },
                collapseAction: () => ClipboardInterop.SendRightArrowKey(),
                out var prefixText,
                out observedCopySequence))
        {
            failureMessage = "Google Docs paragraph prefix capture failed.";
            return false;
        }

        if (!TryCaptureGoogleDocsSelectionCopy(
                foregroundWindow,
                foregroundWindowTitle,
                focusedElement,
                originalSequence,
                cancellationToken,
                "suffix",
                selectionAction: () =>
                {
                    for (var index = 0; index < GoogleDocsParagraphBoundaryExpansionCount; index++)
                    {
                        ClipboardInterop.SendSelectToParagraphEndShortcut();
                        Thread.Sleep(GoogleDocsSelectionSettleMilliseconds);
                    }
                },
                collapseAction: () => ClipboardInterop.SendLeftArrowKey(),
                out var suffixText,
                out var suffixObservedSequence))
        {
            failureMessage = "Google Docs paragraph suffix capture failed.";
            return false;
        }

        if (suffixObservedSequence != 0)
        {
            observedCopySequence = suffixObservedSequence;
        }

        paragraphText = MergeGoogleDocsParagraphFragments(prefixText, suffixText);
        paragraphText = NormalizeGoogleDocsParagraphCapture(paragraphText);
        if (string.IsNullOrWhiteSpace(paragraphText))
        {
            failureMessage = "Google Docs paragraph capture returned empty text.";
            return false;
        }

        AppDiagnostics.Info(
            "paragraph_provider_clipboard_google_docs_merged",
            new Dictionary<string, string?>
            {
                ["prefixLength"] = prefixText?.Length.ToString(),
                ["suffixLength"] = suffixText?.Length.ToString(),
                ["mergedLength"] = paragraphText.Length.ToString(),
                ["mergedPreview"] = BuildPreview(paragraphText),
                ["mergedText"] = paragraphText
            });

        return true;
    }

    private static bool TryCaptureGoogleDocsSelectionCopy(
        nint foregroundWindow,
        string foregroundWindowTitle,
        AutomationElement focusedElement,
        uint originalSequence,
        CancellationToken cancellationToken,
        string phase,
        Action selectionAction,
        Action collapseAction,
        out string capturedText,
        out uint observedCopySequence)
    {
        capturedText = string.Empty;
        observedCopySequence = 0;

        var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
        AppDiagnostics.Info(
            "paragraph_provider_clipboard_google_docs_phase_started",
            new Dictionary<string, string?>
            {
                ["phase"] = phase,
                ["activationResult"] = activationResult.ToString(),
                ["foregroundWindowTitle"] = foregroundWindowTitle
            });

        try
        {
            focusedElement.SetFocus();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "paragraph_provider_clipboard_google_docs_phase_refocus_failed",
                new Dictionary<string, string?>
                {
                    ["phase"] = phase,
                    ["message"] = ex.Message
                });
        }

        selectionAction();
        AppDiagnostics.Info(
            "paragraph_provider_clipboard_google_docs_phase_selection_sent",
            new Dictionary<string, string?>
            {
                ["phase"] = phase
            });

        ClipboardInterop.SendCopyShortcut();
        var expectedSequence = ClipboardInterop.GetClipboardSequenceNumber();
        var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
            if (currentSequence != expectedSequence)
            {
                observedCopySequence = currentSequence;
                if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                {
                    capturedText = Normalize(copiedText) ?? string.Empty;
                    AppDiagnostics.Info(
                        "paragraph_provider_clipboard_google_docs_phase_capture_observed",
                        new Dictionary<string, string?>
                        {
                            ["phase"] = phase,
                            ["copiedSequence"] = observedCopySequence.ToString(),
                            ["capturedLength"] = capturedText.Length.ToString(),
                            ["capturedPreview"] = BuildPreview(capturedText),
                            ["capturedText"] = capturedText
                        });
                    break;
                }
            }

            Thread.Sleep(PollIntervalMilliseconds);
        }

        try
        {
            WindowFocusInterop.TryActivateWindow(foregroundWindow);
            focusedElement.SetFocus();
        }
        catch
        {
        }

        collapseAction();
        AppDiagnostics.Info(
            "paragraph_provider_clipboard_google_docs_phase_selection_cleared",
            new Dictionary<string, string?>
            {
                ["phase"] = phase
            });

        if (!string.IsNullOrWhiteSpace(capturedText))
        {
            return true;
        }

        AppDiagnostics.Warn(
            "paragraph_provider_clipboard_google_docs_phase_timeout",
            new Dictionary<string, string?>
            {
                ["phase"] = phase,
                ["originalSequence"] = originalSequence.ToString(),
                ["lastObservedSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString()
            });
        return false;
    }

    private static string MergeGoogleDocsParagraphFragments(string? prefixText, string? suffixText)
    {
        var prefix = NormalizeGoogleDocsParagraphCapture(prefixText ?? string.Empty);
        var suffix = NormalizeGoogleDocsParagraphCapture(suffixText ?? string.Empty);

        if (string.IsNullOrWhiteSpace(prefix))
        {
            return suffix;
        }

        if (string.IsNullOrWhiteSpace(suffix))
        {
            return prefix;
        }

        if (prefix.Contains(suffix, StringComparison.Ordinal))
        {
            return prefix;
        }

        if (suffix.Contains(prefix, StringComparison.Ordinal))
        {
            return suffix;
        }

        var maxOverlap = Math.Min(prefix.Length, suffix.Length);
        for (var overlap = maxOverlap; overlap >= 8; overlap--)
        {
            if (string.Equals(
                    prefix[^overlap..],
                    suffix[..overlap],
                    StringComparison.OrdinalIgnoreCase))
            {
                return prefix + suffix[overlap..];
            }
        }

        var trailingToken = GetTrailingToken(prefix);
        var leadingToken = GetLeadingToken(suffix);
        if (ShouldMergeGoogleDocsBoundaryFragments(trailingToken, leadingToken))
        {
            return prefix + suffix;
        }

        return $"{prefix} {suffix}".Trim();
    }

    private static int GetTrailingTokenLength(string value)
    {
        var length = 0;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character))
            {
                break;
            }

            length++;
        }

        return length;
    }

    private static string GetTrailingToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var length = GetTrailingTokenLength(value);
        if (length <= 0)
        {
            return string.Empty;
        }

        return value[^length..];
    }

    private static string GetLeadingToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.TrimStart();
        var tokenLength = 0;
        while (tokenLength < trimmed.Length && !char.IsWhiteSpace(trimmed[tokenLength]))
        {
            tokenLength++;
        }

        return tokenLength == 0 ? string.Empty : trimmed[..tokenLength];
    }

    private static bool ShouldMergeGoogleDocsBoundaryFragments(string trailingToken, string leadingToken)
    {
        if (string.IsNullOrWhiteSpace(trailingToken) || string.IsNullOrWhiteSpace(leadingToken))
        {
            return false;
        }

        if (!char.IsLower(trailingToken[^1]) || !char.IsLower(leadingToken[0]))
        {
            return false;
        }

        if (trailingToken.Length > 12 || leadingToken.Length > 8)
        {
            return false;
        }

        if (trailingToken.Length <= 2)
        {
            return true;
        }

        if (trailingToken.Length > 4 && leadingToken.Length > 4)
        {
            return false;
        }

        return !CommonEnglishWords.Contains(trailingToken) &&
               !CommonEnglishWords.Contains(leadingToken);
    }

    private static bool ShouldRetryBrowserPdfCopy(int cycle, int maxCycles, string? capturedText)
    {
        if (cycle >= maxCycles)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(capturedText))
        {
            return true;
        }

        return capturedText.Length < BrowserPdfShortSelectionThresholdCharacters;
    }

    private static bool IsLikelyBrowserPdfFragment(string capturedText)
    {
        if (capturedText.Length < BrowserPdfMinimumAcceptedParagraphCharacters)
        {
            return true;
        }

        var hasWhitespaceSeparator = capturedText.Any(char.IsWhiteSpace);
        if (!hasWhitespaceSeparator)
        {
            return true;
        }

        var tokenCount = capturedText
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Length;
        return tokenCount < 6;
    }

    private static string CaptureBestClipboardTextWithinSettleWindow(
        string initialText,
        ref uint observedCopySequence,
        CancellationToken cancellationToken)
    {
        var bestText = initialText;
        var settleDeadline = DateTime.UtcNow.AddMilliseconds(BrowserPdfPostCopySettleMilliseconds);
        while (DateTime.UtcNow < settleDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
            if (currentSequence != observedCopySequence)
            {
                observedCopySequence = currentSequence;
                if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                {
                    var trimmed = Normalize(copiedText) ?? string.Empty;
                    if (trimmed.Length > bestText.Length)
                    {
                        bestText = trimmed;
                        AppDiagnostics.Info(
                            "paragraph_provider_clipboard_pdf_settle_window_upgrade",
                            new Dictionary<string, string?>
                            {
                                ["length"] = bestText.Length.ToString(),
                                ["copiedSequence"] = observedCopySequence.ToString()
                            });
                    }
                }
            }

            Thread.Sleep(PollIntervalMilliseconds);
        }

        return bestText;
    }

    private static string ReadClipboardFormatsSummary()
    {
        for (var attempt = 0; attempt < ClipboardAccessRetries; attempt++)
        {
            try
            {
                var dataObject = System.Windows.Clipboard.GetDataObject();
                if (dataObject is null)
                {
                    return "<none>";
                }

                var formats = dataObject.GetFormats();
                if (formats is null || formats.Length == 0)
                {
                    return "<none>";
                }

                return string.Join(", ", formats.Take(8));
            }
            catch
            {
                Thread.Sleep(ClipboardAccessRetryDelayMilliseconds);
            }
        }

        return "<unavailable>";
    }

    private static bool TryReadClipboardText(out string text)
    {
        text = string.Empty;

        for (var attempt = 0; attempt < ClipboardAccessRetries; attempt++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    text = System.Windows.Clipboard.GetText();
                    return true;
                }

                return false;
            }
            catch
            {
                Thread.Sleep(ClipboardAccessRetryDelayMilliseconds);
            }
        }

        return false;
    }

    private static bool TryReadClipboardDataObject(out System.Windows.IDataObject? dataObject)
    {
        dataObject = null;

        for (var attempt = 0; attempt < ClipboardAccessRetries; attempt++)
        {
            try
            {
                dataObject = System.Windows.Clipboard.GetDataObject();
                return true;
            }
            catch
            {
                Thread.Sleep(ClipboardAccessRetryDelayMilliseconds);
            }
        }

        return false;
    }

    private static bool TryRestoreClipboard(System.Windows.IDataObject? dataObject)
    {
        if (dataObject is null)
        {
            return true;
        }

        for (var attempt = 0; attempt < ClipboardAccessRetries; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(dataObject, true);
                return true;
            }
            catch
            {
                Thread.Sleep(ClipboardAccessRetryDelayMilliseconds);
            }
        }

        return false;
    }

    private static Task<TextRetrievalResult> RunOnStaThreadAsync(Func<TextRetrievalResult> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TextRetrievalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var (taskCompletionSource, token) = ((TaskCompletionSource<TextRetrievalResult>, CancellationToken))state!;
                taskCompletionSource.TrySetCanceled(token);
            },
            (completion, cancellationToken));
        _ = completion.Task.ContinueWith(
            static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            cancellationRegistration,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var worker = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                completion.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetResult(
                    TextRetrievalResult.Failed(
                        $"Clipboard paragraph fallback failed: {ex.Message}",
                        TextRetrievalSource.ClipboardFallback));
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        return completion.Task;
    }


    private static Dictionary<string, string?> BuildElementDiagnostics(AutomationElement element)
    {
        return new Dictionary<string, string?>
        {
            ["automationId"] = element.Current.AutomationId,
            ["className"] = element.Current.ClassName,
            ["controlType"] = element.Current.ControlType?.ProgrammaticName,
            ["name"] = element.Current.Name,
            ["isPassword"] = element.Current.IsPassword.ToString()
        };
    }
}
