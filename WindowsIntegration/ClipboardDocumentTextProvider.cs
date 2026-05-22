using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class ClipboardDocumentTextProvider : IDocumentTextProvider
{
    private const string BrowserPdfCopyBlockedFailureCode = "browser_pdf_copy_blocked_no_clipboard_change";
    private const int PollIntervalMilliseconds = 50;
    private const int PollTimeoutMilliseconds = 1800;
    private const int BrowserPdfPostCopySettleMilliseconds = 380;
    private const int BrowserPdfMaxCopyCycles = 4;
    private const int BrowserPdfShortCaptureThresholdCharacters = 2200;
    private const int BrowserPdfAutomationFallbackMinimumLength = 180;
    private const int BrowserPdfSelectionLeakMaxCandidateLength = 1200;
    private const double BrowserPdfSelectionLeakSimilarityThreshold = 0.92;
    private const int BrowserPdfSelectAllSettleMilliseconds = 140;
    private const int BrowserPdfSelectionResetSettleMilliseconds = 60;
    private const int BrowserPdfSelectionLeakBreakThreshold = 2;
    private const int BrowserPdfRepeatedShortCaptureThreshold = 2;
    private const int BrowserPdfTinyCaptureLengthThreshold = 120;
    private const int ClipboardAccessRetries = 8;
    private const int ClipboardAccessRetryDelayMilliseconds = 40;
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
        "page ",
        "accessibility",
        "screen reader",
        "pdf viewer"
    };

    public Task<TextRetrievalResult> TryGetDocumentTextAsync(CancellationToken cancellationToken = default)
    {
        var capturedScope = AppDiagnostics.CaptureScope();
        return RunOnStaThreadAsync(
            () => ExecuteWithCapturedScope(capturedScope, () => TryGetDocumentText(cancellationToken)),
            cancellationToken);
    }

    private TextRetrievalResult TryGetDocumentText(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        var focusedElement = System.Windows.Automation.AutomationElement.FocusedElement;
        if (focusedElement is null)
        {
            AppDiagnostics.Warn("clipboard_document_no_focused_element");
            return TextRetrievalResult.Failed(
                "No focused control is available for clipboard document fallback.",
                TextRetrievalSource.ClipboardFallback);
        }

        AppDiagnostics.Info("clipboard_document_focused_element_captured", BuildFocusedElementDiagnostics(focusedElement));

        if (focusedElement.Current.IsPassword)
        {
            AppDiagnostics.Warn("clipboard_document_password_field_blocked");
            return TextRetrievalResult.Failed(
                "Clipboard document fallback is not allowed on password fields.",
                TextRetrievalSource.ClipboardFallback);
        }

        var originalSequence = ClipboardInterop.GetClipboardSequenceNumber();
        var snapshotSucceeded = TryReadClipboardDataObject(out System.Windows.IDataObject? originalClipboard);
        if (!snapshotSucceeded)
        {
            AppDiagnostics.Warn(
                "clipboard_document_snapshot_failed",
                new Dictionary<string, string?>
                {
                    ["originalClipboardSequence"] = originalSequence.ToString()
                });
            return TextRetrievalResult.Failed(
                "Clipboard document fallback failed: unable to read current clipboard safely.",
                TextRetrievalSource.ClipboardFallback);
        }

        uint observedCopySequence = 0;
        string? documentText = null;
        var captureStrategy = "clipboard";
        string? failureMessage = null;
        string? failureCode = null;
        bool canceled = false;
        var shouldRetry = true;
        var pollCount = 0;
        var copySequenceTransitions = 0;
        var isBrowserPdfContext = false;
        var foregroundWindowClass = string.Empty;
        var foregroundWindowTitle = string.Empty;
        string? selectionBaselineText = null;
        string? selectionBaselineFingerprint = null;
        double latestSelectionOverlap = 0;
        var selectionLeakCycles = 0;
        var repeatedShortCaptureCount = 0;
        string? lastShortCaptureFingerprint = null;
        var forceAutomationFallback = false;
        string? automationFallbackReason = null;
        var temporarySelectAllSent = false;
        var foregroundWindow = nint.Zero;

        try
        {
            foregroundWindow = ClipboardInterop.GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
                AppDiagnostics.Warn("clipboard_document_no_foreground_window_for_copy");
                return TextRetrievalResult.Failed(
                    "Clipboard document fallback failed: no foreground window to copy from.",
                    TextRetrievalSource.ClipboardFallback);
            }

            AppDiagnostics.Info(
                "clipboard_document_capture_started",
                new Dictionary<string, string?>
                {
                    ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                    ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundWindow),
                    ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundWindow),
                    ["originalClipboardSequence"] = originalSequence.ToString(),
                    ["pollTimeoutMs"] = PollTimeoutMilliseconds.ToString(),
                    ["pollIntervalMs"] = PollIntervalMilliseconds.ToString()
                });

            foregroundWindowClass = WindowFocusInterop.GetWindowClassName(foregroundWindow);
            foregroundWindowTitle = WindowFocusInterop.GetWindowText(foregroundWindow);
            if (WindowFocusInterop.IsIgnoredReadTargetWindow(foregroundWindowClass, foregroundWindowTitle))
            {
                AppDiagnostics.Warn(
                    "clipboard_document_ignored_foreground_window",
                    new Dictionary<string, string?>
                    {
                        ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                        ["foregroundWindowClass"] = foregroundWindowClass,
                        ["foregroundWindowTitle"] = foregroundWindowTitle
                    });
                return TextRetrievalResult.Failed(
                    "Document capture is blocked because the current foreground window is a transient overlay, not the source app.",
                    TextRetrievalSource.ClipboardFallback);
            }

            isBrowserPdfContext = IsBrowserPdfContext(foregroundWindowClass, foregroundWindowTitle);
            AppDiagnostics.Info(
                "clipboard_document_capture_context",
                new Dictionary<string, string?>
                {
                    ["isBrowserPdfContext"] = isBrowserPdfContext.ToString(),
                    ["foregroundWindowClass"] = foregroundWindowClass,
                    ["foregroundWindowTitle"] = foregroundWindowTitle
                });

            if (isBrowserPdfContext &&
                TryCaptureSelectionBaseline(
                    foregroundWindow,
                    focusedElement,
                    originalSequence,
                    out var baselineText,
                    out var baselineObservedSequence))
            {
                selectionBaselineText = baselineText;
                selectionBaselineFingerprint = BuildScopeFingerprint(selectionBaselineText);
                if (baselineObservedSequence != 0)
                {
                    observedCopySequence = baselineObservedSequence;
                }

                AppDiagnostics.Info(
                    "document_scope_validation_started",
                    new Dictionary<string, string?>
                    {
                        ["provider"] = nameof(ClipboardDocumentTextProvider),
                        ["source"] = TextRetrievalSource.ClipboardFallback.ToString(),
                        ["phase"] = "pre_capture_selection_baseline",
                        ["baselineLength"] = selectionBaselineText?.Length.ToString(),
                        ["baselinePreview"] = BuildPreview(selectionBaselineText ?? string.Empty)
                    });
            }

            var copyCycles = isBrowserPdfContext ? BrowserPdfMaxCopyCycles : 1;
            for (var cycle = 1; cycle <= copyCycles; cycle++)
            {
                var cyclePollStart = pollCount;
                var cycleTransitionsStart = copySequenceTransitions;
                var cycleExpectedSequence = ClipboardInterop.GetClipboardSequenceNumber();
                AppDiagnostics.Info(
                    "clipboard_document_capture_cycle_started",
                    BuildCycleDiagnostics(cycle, copyCycles, isBrowserPdfContext));

                if (isBrowserPdfContext)
                {
                    var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
                    AppDiagnostics.Info(
                        "clipboard_document_capture_cycle_activation_attempted",
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
                            "clipboard_document_capture_cycle_focused_element_refocused",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["focusedElementSnapshot"] = BuildFocusedElementSnapshot()
                            });
                    }
                    catch (Exception ex)
                    {
                        AppDiagnostics.Warn(
                            "clipboard_document_capture_cycle_refocus_failed",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["message"] = ex.Message
                            });
                    }
                }

                if (isBrowserPdfContext)
                {
                    ClipboardInterop.SendEscapeKey();
                    Thread.Sleep(BrowserPdfSelectionResetSettleMilliseconds);
                    AppDiagnostics.Info(
                        "clipboard_document_capture_cycle_selection_reset",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["copyCycles"] = copyCycles.ToString(),
                            ["action"] = "escape_before_select_all"
                        });
                }

                ClipboardInterop.SendSelectAllShortcut();
                temporarySelectAllSent = true;
                Thread.Sleep(isBrowserPdfContext ? BrowserPdfSelectAllSettleMilliseconds : 120);
                ClipboardInterop.SendCopyShortcut();
                AppDiagnostics.Info(
                    "clipboard_document_capture_copy_shortcuts_sent",
                    new Dictionary<string, string?>
                    {
                        ["copyCycle"] = cycle.ToString(),
                        ["copyCycles"] = copyCycles.ToString(),
                        ["isBrowserPdfContext"] = isBrowserPdfContext.ToString()
                    });

                var cycleDeadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMilliseconds);
                var cycleCapturedText = string.Empty;
                while (DateTime.UtcNow < cycleDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pollCount++;

                    var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                    if (currentSequence != cycleExpectedSequence)
                    {
                        copySequenceTransitions++;
                        observedCopySequence = currentSequence;
                        cycleExpectedSequence = currentSequence;
                        if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                        {
                            cycleCapturedText = copiedText.Trim();
                            AppDiagnostics.Info(
                                "clipboard_document_capture_observed",
                                new Dictionary<string, string?>
                                {
                                    ["copyCycle"] = cycle.ToString(),
                                    ["pollCount"] = pollCount.ToString(),
                                    ["observedCopySequence"] = observedCopySequence.ToString(),
                                    ["capturedLength"] = cycleCapturedText.Length.ToString(),
                                    ["capturedPreview"] = BuildPreview(cycleCapturedText)
                                });
                            break;
                        }

                        AppDiagnostics.Info(
                            "clipboard_document_capture_sequence_changed_without_text",
                            new Dictionary<string, string?>
                            {
                                ["copyCycle"] = cycle.ToString(),
                                ["observedCopySequence"] = observedCopySequence.ToString(),
                                ["clipboardFormats"] = ReadClipboardFormatsSummary()
                            });
                    }

                    Thread.Sleep(PollIntervalMilliseconds);
                }

                if (isBrowserPdfContext && string.IsNullOrWhiteSpace(cycleCapturedText))
                {
                    ClipboardInterop.SendCopyShortcutCtrlInsert();
                    AppDiagnostics.Info(
                        "clipboard_document_capture_copy_alt_shortcut_sent",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["copyCycles"] = copyCycles.ToString(),
                            ["shortcut"] = "Ctrl+Insert"
                        });

                    var altDeadline = DateTime.UtcNow.AddMilliseconds(650);
                    while (DateTime.UtcNow < altDeadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pollCount++;

                        var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                        if (currentSequence != cycleExpectedSequence)
                        {
                            copySequenceTransitions++;
                            observedCopySequence = currentSequence;
                            cycleExpectedSequence = currentSequence;
                            if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                            {
                                cycleCapturedText = copiedText.Trim();
                                AppDiagnostics.Info(
                                    "clipboard_document_capture_observed",
                                    new Dictionary<string, string?>
                                    {
                                        ["copyCycle"] = cycle.ToString(),
                                        ["pollCount"] = pollCount.ToString(),
                                        ["observedCopySequence"] = observedCopySequence.ToString(),
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
                        "clipboard_document_capture_cycle_timeout",
                        new Dictionary<string, string?>
                        {
                            ["copyCycle"] = cycle.ToString(),
                            ["copyCycles"] = copyCycles.ToString(),
                            ["cyclePollCount"] = (pollCount - cyclePollStart).ToString(),
                            ["cycleSequenceTransitions"] = (copySequenceTransitions - cycleTransitionsStart).ToString(),
                            ["lastObservedClipboardSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString(),
                            ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(ClipboardInterop.GetForegroundWindow()),
                            ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(ClipboardInterop.GetForegroundWindow()),
                            ["focusedElementSnapshot"] = BuildFocusedElementSnapshot()
                        });
                }

                if (!string.IsNullOrWhiteSpace(cycleCapturedText))
                {
                    if (isBrowserPdfContext)
                    {
                        cycleCapturedText = CaptureBestClipboardTextWithinSettleWindow(
                            cycleCapturedText,
                            ref observedCopySequence,
                            cancellationToken);

                        if (IsSelectionLeakCandidate(
                                selectionBaselineFingerprint,
                                cycleCapturedText,
                                out var overlap))
                        {
                            latestSelectionOverlap = overlap;
                            selectionLeakCycles++;
                            AppDiagnostics.Warn(
                                "document_scope_selection_leak_detected",
                                new Dictionary<string, string?>
                                {
                                    ["provider"] = nameof(ClipboardDocumentTextProvider),
                                    ["source"] = TextRetrievalSource.ClipboardFallback.ToString(),
                                    ["copyCycle"] = cycle.ToString(),
                                    ["baselineLength"] = selectionBaselineText?.Length.ToString(),
                                    ["candidateLength"] = cycleCapturedText.Length.ToString(),
                                    ["similarity"] = overlap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                                    ["threshold"] = BrowserPdfSelectionLeakSimilarityThreshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                                    ["selectionLeakCycles"] = selectionLeakCycles.ToString(),
                                    ["candidatePreview"] = BuildPreview(cycleCapturedText)
                                });
                            cycleCapturedText = string.Empty;

                            if (selectionLeakCycles >= BrowserPdfSelectionLeakBreakThreshold)
                            {
                                forceAutomationFallback = true;
                                automationFallbackReason = "selection_leak_detected";
                                AppDiagnostics.Warn(
                                    "clipboard_document_escalating_to_automation_fallback",
                                    new Dictionary<string, string?>
                                    {
                                        ["copyCycle"] = cycle.ToString(),
                                        ["copyCycles"] = copyCycles.ToString(),
                                        ["reason"] = automationFallbackReason,
                                        ["selectionLeakCycles"] = selectionLeakCycles.ToString()
                                    });
                            }
                        }
                        else
                        {
                            selectionLeakCycles = 0;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(cycleCapturedText) &&
                        (string.IsNullOrWhiteSpace(documentText) || cycleCapturedText.Length > documentText.Length))
                    {
                        documentText = cycleCapturedText;
                    }

                    if (isBrowserPdfContext && !string.IsNullOrWhiteSpace(cycleCapturedText))
                    {
                        if (cycleCapturedText.Length <= BrowserPdfTinyCaptureLengthThreshold)
                        {
                            var fingerprint = BuildScopeFingerprint(cycleCapturedText);
                            if (!string.IsNullOrWhiteSpace(fingerprint) &&
                                string.Equals(fingerprint, lastShortCaptureFingerprint, StringComparison.Ordinal))
                            {
                                repeatedShortCaptureCount++;
                            }
                            else
                            {
                                repeatedShortCaptureCount = 1;
                                lastShortCaptureFingerprint = fingerprint;
                            }

                            AppDiagnostics.Warn(
                                "clipboard_document_tiny_capture_detected",
                                new Dictionary<string, string?>
                                {
                                    ["copyCycle"] = cycle.ToString(),
                                    ["copyCycles"] = copyCycles.ToString(),
                                    ["length"] = cycleCapturedText.Length.ToString(),
                                    ["repeatCount"] = repeatedShortCaptureCount.ToString(),
                                    ["threshold"] = BrowserPdfRepeatedShortCaptureThreshold.ToString(),
                                    ["preview"] = BuildPreview(cycleCapturedText)
                                });

                            if (repeatedShortCaptureCount >= BrowserPdfRepeatedShortCaptureThreshold)
                            {
                                forceAutomationFallback = true;
                                automationFallbackReason = "repeated_tiny_capture";
                                cycleCapturedText = string.Empty;
                                documentText = null;
                                AppDiagnostics.Warn(
                                    "clipboard_document_escalating_to_automation_fallback",
                                    new Dictionary<string, string?>
                                    {
                                        ["copyCycle"] = cycle.ToString(),
                                        ["copyCycles"] = copyCycles.ToString(),
                                        ["reason"] = automationFallbackReason,
                                        ["repeatCount"] = repeatedShortCaptureCount.ToString()
                                    });
                            }
                        }
                        else
                        {
                            repeatedShortCaptureCount = 0;
                            lastShortCaptureFingerprint = null;
                        }
                    }
                }

                if (isBrowserPdfContext && forceAutomationFallback)
                {
                    break;
                }

                if (!isBrowserPdfContext || !ShouldRetryBrowserPdfCopy(cycle, copyCycles, documentText))
                {
                    break;
                }

                AppDiagnostics.Info(
                    "clipboard_document_retrying_pdf_copy",
                    new Dictionary<string, string?>
                    {
                        ["copyCycle"] = cycle.ToString(),
                        ["copyCycles"] = copyCycles.ToString(),
                        ["capturedLength"] = documentText?.Length.ToString(),
                        ["threshold"] = BrowserPdfShortCaptureThresholdCharacters.ToString()
                    });
            }

            if (string.IsNullOrWhiteSpace(documentText))
            {
                if (isBrowserPdfContext)
                {
                    AppDiagnostics.Info(
                        "clipboard_document_browser_pdf_automation_fallback_started",
                        new Dictionary<string, string?>
                        {
                            ["reason"] = automationFallbackReason ?? "clipboard_capture_exhausted",
                            ["copySequenceTransitions"] = copySequenceTransitions.ToString(),
                            ["pollCount"] = pollCount.ToString()
                        });

                    if (TryCaptureBrowserPdfViaAutomationFallback(
                            foregroundWindow,
                            cancellationToken,
                            out var automationFallbackText,
                            out var automationFallbackDiagnostics))
                    {
                        if (IsSelectionLeakCandidate(
                                selectionBaselineFingerprint,
                                automationFallbackText ?? string.Empty,
                                out var overlap))
                        {
                            latestSelectionOverlap = overlap;
                            AppDiagnostics.Warn(
                                "document_scope_selection_leak_detected",
                                new Dictionary<string, string?>
                                {
                                    ["provider"] = nameof(ClipboardDocumentTextProvider),
                                    ["source"] = TextRetrievalSource.ClipboardFallback.ToString(),
                                    ["phase"] = "automation_fallback",
                                    ["baselineLength"] = selectionBaselineText?.Length.ToString(),
                                    ["candidateLength"] = automationFallbackText?.Length.ToString(),
                                    ["similarity"] = overlap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                                    ["threshold"] = BrowserPdfSelectionLeakSimilarityThreshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                                    ["candidatePreview"] = BuildPreview(automationFallbackText ?? string.Empty)
                                });
                            failureCode = "selection_leak_detected";
                            failureMessage = "Document capture stayed on selected text after full-document retries.";
                        }
                        else
                        {
                            documentText = automationFallbackText;
                            captureStrategy = "browser_pdf_automation_fallback";
                            AppDiagnostics.Info(
                                "clipboard_document_browser_pdf_automation_fallback_succeeded",
                                automationFallbackDiagnostics);
                        }
                    }
                    else
                    {
                        failureCode = BrowserPdfCopyBlockedFailureCode;
                        shouldRetry = false;
                        if (string.Equals(automationFallbackReason, "selection_leak_detected", StringComparison.Ordinal) ||
                            string.Equals(automationFallbackReason, "repeated_tiny_capture", StringComparison.Ordinal))
                        {
                            failureMessage = "Document capture stayed on selected text and full-document fallback was unavailable. Clear selection, click inside document body, then try Read Document again.";
                        }
                        else
                        {
                            failureMessage = "Browser PDF viewer blocked document copy to clipboard. Try enabling PDF accessibility text access, then retry. If it still fails, open the PDF in an external reader and run Read Document there.";
                        }

                        AppDiagnostics.Warn(
                            "clipboard_document_browser_pdf_copy_blocked",
                            new Dictionary<string, string?>
                            {
                                ["failureCode"] = failureCode,
                                ["pollCount"] = pollCount.ToString(),
                                ["copySequenceTransitions"] = copySequenceTransitions.ToString(),
                                ["foregroundWindowClass"] = foregroundWindowClass,
                                ["foregroundWindowTitle"] = foregroundWindowTitle,
                                ["focusedElementSnapshot"] = BuildFocusedElementSnapshot(),
                                ["recommendedWorkaround"] = "enable_pdf_accessibility_then_retry_or_open_pdf_externally"
                            });
                    }
                }

                if (string.IsNullOrWhiteSpace(documentText) && string.IsNullOrWhiteSpace(failureMessage))
                {
                    failureCode ??= "clipboard_document_capture_timeout";
                    failureMessage = "Clipboard document fallback timed out waiting for copied text.";
                }

                if (string.IsNullOrWhiteSpace(documentText))
                {
                    AppDiagnostics.Warn(
                        "clipboard_document_capture_timeout",
                        new Dictionary<string, string?>
                        {
                            ["failureCode"] = failureCode,
                            ["originalClipboardSequence"] = originalSequence.ToString(),
                            ["lastObservedClipboardSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString(),
                            ["pollCount"] = pollCount.ToString(),
                            ["copySequenceTransitions"] = copySequenceTransitions.ToString()
                        });
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            AppDiagnostics.Warn(
                "clipboard_document_capture_cancelled",
                new Dictionary<string, string?>
                {
                    ["pollCount"] = pollCount.ToString(),
                    ["copySequenceTransitions"] = copySequenceTransitions.ToString()
                });
        }

        if (temporarySelectAllSent && foregroundWindow != nint.Zero)
        {
            var clearPhase = canceled
                ? "after_capture_cancelled"
                : string.IsNullOrWhiteSpace(documentText)
                    ? "after_capture_failed"
                    : "after_capture_succeeded";
            ClearDocumentTemporarySelection(foregroundWindow, focusedElement, clearPhase);
        }

        if (observedCopySequence != 0)
        {
            var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
            if (currentSequence == observedCopySequence && !TryRestoreClipboard(originalClipboard))
            {
                AppDiagnostics.Warn("clipboard_document_restore_failed");
                if (!string.IsNullOrWhiteSpace(documentText))
                {
                    stopwatch.Stop();
                    return TextRetrievalResult.Retrieved(
                        documentText,
                        TextRetrievalSource.ClipboardFallback,
                        "Document text retrieved via clipboard fallback. Restoring previous clipboard content failed.");
                }

                return TextRetrievalResult.Failed(
                    "Clipboard document fallback failed and restoring previous clipboard content also failed.",
                    TextRetrievalSource.ClipboardFallback);
            }

            AppDiagnostics.Info(
                "clipboard_document_restore_outcome",
                new Dictionary<string, string?>
                {
                    ["restoreAttempted"] = (currentSequence == observedCopySequence).ToString(),
                    ["restoreSkippedSequenceChanged"] = (currentSequence != observedCopySequence).ToString(),
                    ["observedCopySequence"] = observedCopySequence.ToString(),
                    ["currentClipboardSequence"] = currentSequence.ToString()
                });
        }

        if (canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(documentText))
        {
            var originalLength = documentText.Length;
            documentText = RemoveLeadingViewerUiLines(documentText, out var removedLeadingLines);
            AppDiagnostics.Info(
                "clipboard_document_capture_succeeded",
                new Dictionary<string, string?>
                {
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                    ["pollCount"] = pollCount.ToString(),
                    ["copySequenceTransitions"] = copySequenceTransitions.ToString(),
                    ["originalLength"] = originalLength.ToString(),
                    ["sanitizedLength"] = documentText.Length.ToString(),
                    ["removedLeadingLines"] = removedLeadingLines.ToString(),
                    ["captureStrategy"] = captureStrategy,
                    ["preview"] = BuildPreview(documentText)
                });
            return TextRetrievalResult.Retrieved(
                documentText,
                TextRetrievalSource.ClipboardFallback,
                "Document text retrieved via clipboard fallback.");
        }

        stopwatch.Stop();
        AppDiagnostics.Warn(
            "clipboard_document_capture_failed",
            new Dictionary<string, string?>
            {
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString(),
                ["pollCount"] = pollCount.ToString(),
                ["copySequenceTransitions"] = copySequenceTransitions.ToString(),
                ["failureCode"] = failureCode,
                ["automationFallbackReason"] = automationFallbackReason,
                ["shouldRetry"] = shouldRetry.ToString(),
                ["failureMessage"] = failureMessage,
                ["selectionLeakSimilarity"] = latestSelectionOverlap <= 0
                    ? null
                    : latestSelectionOverlap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            });
        return TextRetrievalResult.Failed(
            failureMessage ?? "Clipboard document fallback failed.",
            TextRetrievalSource.ClipboardFallback,
            shouldRetry: shouldRetry);
    }

    private static TextRetrievalResult ExecuteWithCapturedScope(
        IReadOnlyDictionary<string, string?>? capturedScope,
        Func<TextRetrievalResult> action)
    {
        if (capturedScope is null)
        {
            return action();
        }

        using var scope = AppDiagnostics.BeginScope(capturedScope);
        return action();
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

    private static bool TryCaptureSelectionBaseline(
        nint foregroundWindow,
        System.Windows.Automation.AutomationElement focusedElement,
        uint originalSequence,
        out string? baselineText,
        out uint observedSequence)
    {
        baselineText = null;
        observedSequence = 0;

        var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
        AppDiagnostics.Info(
            "clipboard_document_pre_capture_baseline_activation_attempted",
            new Dictionary<string, string?>
            {
                ["activationResult"] = activationResult.ToString(),
                ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundWindow),
                ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundWindow)
            });

        try
        {
            focusedElement.SetFocus();
            AppDiagnostics.Info(
                "clipboard_document_pre_capture_baseline_refocused",
                new Dictionary<string, string?>
                {
                    ["focusedElementSnapshot"] = BuildFocusedElementSnapshot()
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "clipboard_document_pre_capture_baseline_refocus_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
        }

        ClipboardInterop.SendCopyShortcut();
        var deadline = DateTime.UtcNow.AddMilliseconds(700);
        var expectedSequence = ClipboardInterop.GetClipboardSequenceNumber();
        while (DateTime.UtcNow < deadline)
        {
            var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
            if (currentSequence != expectedSequence && currentSequence != originalSequence)
            {
                observedSequence = currentSequence;
                if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                {
                    baselineText = copiedText.Trim();
                    AppDiagnostics.Info(
                        "clipboard_document_pre_capture_baseline_observed",
                        new Dictionary<string, string?>
                        {
                            ["baselineLength"] = baselineText.Length.ToString(),
                            ["baselinePreview"] = BuildPreview(baselineText),
                            ["observedSequence"] = observedSequence.ToString()
                        });
                    return true;
                }
            }

            Thread.Sleep(PollIntervalMilliseconds);
        }

        AppDiagnostics.Info("clipboard_document_pre_capture_baseline_missing");
        return false;
    }

    private static string? BuildScopeFingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var filtered = new string(
            text
                .ToLowerInvariant()
                .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                .ToArray());
        var normalized = string.Join(
            " ",
            filtered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsSelectionLeakCandidate(
        string? baselineFingerprint,
        string candidateText,
        out double similarity)
    {
        similarity = 0;
        if (string.IsNullOrWhiteSpace(baselineFingerprint) ||
            string.IsNullOrWhiteSpace(candidateText))
        {
            return false;
        }

        if (candidateText.Length > BrowserPdfSelectionLeakMaxCandidateLength)
        {
            return false;
        }

        var candidateFingerprint = BuildScopeFingerprint(candidateText);
        if (string.IsNullOrWhiteSpace(candidateFingerprint))
        {
            return false;
        }

        similarity = ComputeTokenDiceSimilarity(baselineFingerprint, candidateFingerprint);
        return similarity >= BrowserPdfSelectionLeakSimilarityThreshold;
    }

    private static double ComputeTokenDiceSimilarity(string baseline, string candidate)
    {
        var baselineTokens = baseline
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var candidateTokens = candidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (baselineTokens.Length == 0 || candidateTokens.Length == 0)
        {
            return 0;
        }

        var intersection = baselineTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
        return (2d * intersection) / (baselineTokens.Length + candidateTokens.Length);
    }

    private static bool TryCaptureBrowserPdfViaAutomationFallback(
        nint foregroundWindow,
        CancellationToken cancellationToken,
        out string? text,
        out Dictionary<string, string?> diagnostics)
    {
        text = null;
        diagnostics = new Dictionary<string, string?>
        {
            ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
            ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundWindow),
            ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundWindow)
        };

        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(foregroundWindow);
            if (root is null)
            {
                diagnostics["reason"] = "automation_root_from_handle_null";
                AppDiagnostics.Warn("clipboard_document_browser_pdf_automation_fallback_failed", diagnostics);
                return false;
            }

            var candidates = new List<string>();
            var nameFragments = new List<string>();
            var scannedCount = 0;
            var textPatternCandidateCount = 0;
            var nameCandidateCount = 0;

            AddAutomationCandidateFromPatterns(root, candidates, ref textPatternCandidateCount);
            var descendants = root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                System.Windows.Automation.Condition.TrueCondition);
            scannedCount = descendants.Count;

            for (var index = 0; index < descendants.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var element = descendants[index];

                AddAutomationCandidateFromPatterns(element, candidates, ref textPatternCandidateCount);
                AddAutomationCandidateFromName(element, nameFragments, ref nameCandidateCount);
            }

            var assembledFromNames = BuildAssembledNameCandidate(nameFragments);
            if (!string.IsNullOrWhiteSpace(assembledFromNames))
            {
                candidates.Add(assembledFromNames);
            }

            if (candidates.Count == 0)
            {
                diagnostics["reason"] = "no_automation_candidates";
                diagnostics["scannedCount"] = scannedCount.ToString();
                AppDiagnostics.Warn("clipboard_document_browser_pdf_automation_fallback_failed", diagnostics);
                return false;
            }

            var best = SelectBestAutomationCandidate(candidates);
            if (string.IsNullOrWhiteSpace(best))
            {
                diagnostics["reason"] = "best_candidate_empty";
                diagnostics["candidateCount"] = candidates.Count.ToString();
                diagnostics["scannedCount"] = scannedCount.ToString();
                AppDiagnostics.Warn("clipboard_document_browser_pdf_automation_fallback_failed", diagnostics);
                return false;
            }

            var sanitized = RemoveLeadingViewerUiLines(best, out var removedLeadingLines);
            if (string.IsNullOrWhiteSpace(sanitized) ||
                IsMostlyViewerUiContent(sanitized) ||
                sanitized.Length < BrowserPdfAutomationFallbackMinimumLength)
            {
                diagnostics["reason"] = "candidate_looks_like_viewer_ui";
                diagnostics["candidateCount"] = candidates.Count.ToString();
                diagnostics["scannedCount"] = scannedCount.ToString();
                diagnostics["bestLength"] = best.Length.ToString();
                diagnostics["bestPreview"] = BuildPreview(best);
                AppDiagnostics.Warn("clipboard_document_browser_pdf_automation_fallback_failed", diagnostics);
                return false;
            }

            text = sanitized;
            diagnostics["candidateCount"] = candidates.Count.ToString();
            diagnostics["scannedCount"] = scannedCount.ToString();
            diagnostics["textPatternCandidateCount"] = textPatternCandidateCount.ToString();
            diagnostics["nameCandidateCount"] = nameCandidateCount.ToString();
            diagnostics["nameAssemblyLength"] = assembledFromNames?.Length.ToString();
            diagnostics["selectedLength"] = text.Length.ToString();
            diagnostics["removedLeadingLines"] = removedLeadingLines.ToString();
            diagnostics["selectedPreview"] = BuildPreview(text);
            return true;
        }
        catch (Exception ex)
        {
            diagnostics["reason"] = "exception";
            diagnostics["message"] = ex.Message;
            diagnostics["exceptionType"] = ex.GetType().Name;
            AppDiagnostics.Warn("clipboard_document_browser_pdf_automation_fallback_failed", diagnostics);
            return false;
        }
    }

    private static void AddAutomationCandidateFromPatterns(
        System.Windows.Automation.AutomationElement element,
        ICollection<string> candidates,
        ref int patternCandidateCount)
    {
        if (element.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var textPatternObject) &&
            textPatternObject is System.Windows.Automation.TextPattern textPattern)
        {
            var raw = textPattern.DocumentRange.GetText(-1);
            var normalized = NormalizeAutomationCandidate(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && normalized.Length >= 80)
            {
                candidates.Add(normalized);
                patternCandidateCount++;
            }
        }

        if (element.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var valuePatternObject) &&
            valuePatternObject is System.Windows.Automation.ValuePattern valuePattern)
        {
            var normalized = NormalizeAutomationCandidate(valuePattern.Current.Value);
            if (!string.IsNullOrWhiteSpace(normalized) && normalized.Length >= 80)
            {
                candidates.Add(normalized);
                patternCandidateCount++;
            }
        }
    }

    private static void AddAutomationCandidateFromName(
        System.Windows.Automation.AutomationElement element,
        ICollection<string> fragments,
        ref int nameCandidateCount)
    {
        var controlType = element.Current.ControlType;
        if (controlType is null ||
            !string.Equals(controlType.ProgrammaticName, System.Windows.Automation.ControlType.Text.ProgrammaticName, StringComparison.Ordinal))
        {
            return;
        }

        var name = NormalizeAutomationCandidate(element.Current.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Length < 24 || LooksLikeViewerUiLine(name))
        {
            return;
        }

        fragments.Add(name);
        nameCandidateCount++;
    }

    private static string BuildAssembledNameCandidate(IEnumerable<string> fragments)
    {
        var normalized = fragments
            .Select(fragment => fragment.Trim())
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
            .Where(fragment => !LooksLikeViewerUiLine(fragment))
            .ToList();
        if (normalized.Count == 0)
        {
            return string.Empty;
        }

        var deduped = new List<string>(normalized.Count);
        string? previous = null;
        foreach (var fragment in normalized)
        {
            if (string.Equals(fragment, previous, StringComparison.Ordinal))
            {
                continue;
            }

            deduped.Add(fragment);
            previous = fragment;
        }

        return string.Join(Environment.NewLine, deduped);
    }

    private static string? NormalizeAutomationCandidate(string? text)
    {
        return text?.Trim('\0', ' ', '\r', '\n', '\t');
    }

    private static string? SelectBestAutomationCandidate(IEnumerable<string> candidates)
    {
        string? best = null;
        var bestScore = int.MinValue;
        foreach (var candidate in candidates)
        {
            var normalized = candidate.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            var score = normalized.Length;
            var lines = normalized
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(12)
                .ToArray();

            var uiLikeCount = lines.Count(LooksLikeViewerUiLine);
            score -= uiLikeCount * 160;

            if (score > bestScore)
            {
                bestScore = score;
                best = normalized;
            }
        }

        return best;
    }

    private static bool IsMostlyViewerUiContent(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(14)
            .ToArray();

        if (lines.Length == 0)
        {
            return true;
        }

        var uiLikeCount = lines.Count(LooksLikeViewerUiLine);
        return uiLikeCount >= Math.Max(5, (int)Math.Ceiling(lines.Length * 0.65));
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

    private static string RemoveLeadingViewerUiLines(string text, out int removedLeadingLines)
    {
        removedLeadingLines = 0;
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var startIndex = 0;
        while (startIndex < lines.Length)
        {
            var line = lines[startIndex].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                startIndex++;
                removedLeadingLines++;
                continue;
            }

            if (!LooksLikeViewerUiLine(line))
            {
                break;
            }

            startIndex++;
            removedLeadingLines++;
        }

        var result = string.Join(Environment.NewLine, lines[startIndex..]).Trim();
        return string.IsNullOrWhiteSpace(result) ? text.Trim() : result;
    }

    private static bool IsBrowserPdfContext(string windowClass, string windowTitle)
    {
        if (!string.Equals(windowClass, "Chrome_WidgetWin_1", StringComparison.Ordinal))
        {
            return false;
        }

        return windowTitle.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ClearDocumentTemporarySelection(
        nint foregroundWindow,
        System.Windows.Automation.AutomationElement? focusedElement,
        string phase)
    {
        try
        {
            if (!WindowFocusInterop.IsValidWindow(foregroundWindow))
            {
                AppDiagnostics.Info(
                    "clipboard_document_selection_clear_skipped_invalid_window",
                    new Dictionary<string, string?>
                    {
                        ["phase"] = phase,
                        ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}"
                    });
                return;
            }

            var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
            if (!activationResult)
            {
                AppDiagnostics.Info(
                    "clipboard_document_selection_clear_skipped_activation_failed",
                    new Dictionary<string, string?>
                    {
                        ["phase"] = phase,
                        ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                        ["currentForegroundWindowHwnd"] = $"0x{ClipboardInterop.GetForegroundWindow().ToInt64():X}"
                    });
                return;
            }

            try
            {
                focusedElement?.SetFocus();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Warn(
                    "clipboard_document_selection_clear_refocus_failed",
                    new Dictionary<string, string?>
                    {
                        ["phase"] = phase,
                        ["message"] = ex.Message
                    });
            }

            ClipboardInterop.SendEscapeKey();
            Thread.Sleep(BrowserPdfSelectionResetSettleMilliseconds);
            ClipboardInterop.SendLeftArrowKey();
            Thread.Sleep(BrowserPdfSelectionResetSettleMilliseconds);
            AppDiagnostics.Info(
                "clipboard_document_selection_clear_sent",
                new Dictionary<string, string?>
                {
                    ["phase"] = phase,
                    ["activationResult"] = activationResult.ToString(),
                    ["clearSequence"] = "escape,left_arrow",
                    ["focusedElementSnapshot"] = BuildFocusedElementSnapshot()
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "clipboard_document_selection_clear_failed",
                new Dictionary<string, string?>
                {
                    ["phase"] = phase,
                    ["message"] = ex.Message
                });
        }
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

        return capturedText.Length < BrowserPdfShortCaptureThresholdCharacters;
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
                    var trimmed = copiedText.Trim();
                    if (trimmed.Length > bestText.Length)
                    {
                        bestText = trimmed;
                        AppDiagnostics.Info(
                            "clipboard_document_pdf_settle_window_upgrade",
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

    private static bool LooksLikeViewerUiLine(string line)
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

        var letterCount = line.Count(char.IsLetter);
        var symbolCount = line.Count(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch));
        return letterCount == 0 && symbolCount > 0;
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

    private static Dictionary<string, string?> BuildFocusedElementDiagnostics(System.Windows.Automation.AutomationElement focusedElement)
    {
        var processId = focusedElement.Current.ProcessId;
        var data = new Dictionary<string, string?>
        {
            ["focusedProcessId"] = processId.ToString(),
            ["focusedProcessName"] = ResolveProcessName(processId),
            ["focusedControlType"] = focusedElement.Current.ControlType?.ProgrammaticName,
            ["focusedElementName"] = BuildPreview(focusedElement.Current.Name ?? string.Empty),
            ["focusedElementClassName"] = focusedElement.Current.ClassName,
            ["focusedAutomationId"] = focusedElement.Current.AutomationId
        };

        var foregroundHwnd = ClipboardInterop.GetForegroundWindow();
        data["foregroundWindowHwnd"] = $"0x{foregroundHwnd.ToInt64():X}";
        data["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundHwnd);
        data["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundHwnd);
        return data;
    }

    private static Dictionary<string, string?> BuildCycleDiagnostics(int cycle, int copyCycles, bool isBrowserPdfContext)
    {
        var foregroundHwnd = ClipboardInterop.GetForegroundWindow();
        return new Dictionary<string, string?>
        {
            ["copyCycle"] = cycle.ToString(),
            ["copyCycles"] = copyCycles.ToString(),
            ["isBrowserPdfContext"] = isBrowserPdfContext.ToString(),
            ["foregroundWindowHwnd"] = $"0x{foregroundHwnd.ToInt64():X}",
            ["foregroundWindowClass"] = WindowFocusInterop.GetWindowClassName(foregroundHwnd),
            ["foregroundWindowTitle"] = WindowFocusInterop.GetWindowText(foregroundHwnd),
            ["focusedElementSnapshot"] = BuildFocusedElementSnapshot()
        };
    }

    private static string BuildFocusedElementSnapshot()
    {
        try
        {
            var focused = System.Windows.Automation.AutomationElement.FocusedElement;
            if (focused is null)
            {
                return "<none>";
            }

            return $"controlType={focused.Current.ControlType?.ProgrammaticName};class={focused.Current.ClassName};name={BuildPreview(focused.Current.Name ?? string.Empty)}";
        }
        catch (Exception ex)
        {
            return $"<error:{ex.GetType().Name}>";
        }
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
                completion.TrySetResult(TextRetrievalResult.Failed(
                    $"Clipboard document fallback failed: {ex.Message}",
                    TextRetrievalSource.ClipboardFallback));
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        return completion.Task;
    }
}
