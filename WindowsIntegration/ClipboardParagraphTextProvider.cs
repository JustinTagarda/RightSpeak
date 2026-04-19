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
    private const int PollIntervalMilliseconds = 50;
    private const int PollTimeoutMilliseconds = 1200;
    private const int BrowserPdfPostCopySettleMilliseconds = 320;
    private const int BrowserPdfMaxCopyCycles = 3;
    private const int BrowserPdfShortSelectionThresholdCharacters = 420;
    private const int BrowserPdfMinimumAcceptedParagraphCharacters = 40;
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
        var isBrowserPdfContext = IsBrowserPdfContext(foregroundWindowClass, foregroundWindowTitle);
        AppDiagnostics.Info(
            "paragraph_provider_clipboard_context",
            new Dictionary<string, string?>
            {
                ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                ["foregroundWindowClass"] = foregroundWindowClass,
                ["foregroundWindowTitle"] = foregroundWindowTitle,
                ["isBrowserPdfContext"] = isBrowserPdfContext.ToString()
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

        try
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
                        ["isBrowserPdfContext"] = isBrowserPdfContext.ToString()
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

        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
            AppDiagnostics.Info(
                "paragraph_provider_clipboard_success",
                new Dictionary<string, string?>
                {
                    ["textLength"] = paragraphText.Length.ToString(),
                    ["textPreview"] = BuildPreview(paragraphText),
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
