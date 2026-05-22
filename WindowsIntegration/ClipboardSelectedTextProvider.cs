using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class ClipboardSelectedTextProvider : ISelectedTextProvider
{
    private const int PollIntervalMilliseconds = 50;
    private const int PollTimeoutMilliseconds = 800;
    private const int BrowserPdfPostCopySettleMilliseconds = 320;
    private const int BrowserPdfMaxCopyCycles = 3;
    private const int BrowserPdfShortSelectionThresholdCharacters = 420;
    private const int ClipboardAccessRetries = 8;
    private const int ClipboardAccessRetryDelayMilliseconds = 40;

    public TextRetrievalSource Source => TextRetrievalSource.ClipboardFallback;

    public Task<TextRetrievalResult> TryGetSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        return RunOnStaThreadAsync(() => TryGetSelectedText(cancellationToken), cancellationToken);
    }

    private TextRetrievalResult TryGetSelectedText(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        System.Windows.IDataObject? originalClipboard = null;
        var originalSequence = ClipboardInterop.GetClipboardSequenceNumber();
        var originalSnapshotSucceeded = TryReadClipboardDataObject(out originalClipboard);
        if (!originalSnapshotSucceeded)
        {
            return TextRetrievalResult.Failed("Clipboard fallback failed: unable to read current clipboard safely.", Source);
        }

        uint observedCopySequence = 0;
        string? selectedText = null;
        string? failureMessage = null;
        bool canceled = false;

        try
        {
            var foregroundWindow = ClipboardInterop.GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
                failureMessage = "Clipboard fallback failed: no foreground window to copy from.";
                AppDiagnostics.Warn(
                    "clipboard_fallback_no_foreground_window",
                    new Dictionary<string, string?>
                    {
                        ["originalSequence"] = originalSequence.ToString()
                    });
                return TextRetrievalResult.Failed(failureMessage, Source);
            }

            var foregroundWindowClass = WindowFocusInterop.GetWindowClassName(foregroundWindow);
            var foregroundWindowTitle = WindowFocusInterop.GetWindowText(foregroundWindow);
            if (WindowFocusInterop.IsIgnoredReadTargetWindow(foregroundWindowClass, foregroundWindowTitle))
            {
                failureMessage = "Selected-text capture is blocked because the current foreground window is a transient overlay, not the source app.";
                AppDiagnostics.Warn(
                    "clipboard_fallback_ignored_foreground_window",
                    new Dictionary<string, string?>
                    {
                        ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                        ["foregroundWindowClass"] = foregroundWindowClass,
                        ["foregroundWindowTitle"] = foregroundWindowTitle
                    });
                return TextRetrievalResult.Failed(failureMessage, Source);
            }

            var isBrowserPdfContext = IsBrowserPdfContext(foregroundWindowClass, foregroundWindowTitle);
            AppDiagnostics.Info(
                "selected_workflow_clipboard_copy_started",
                new Dictionary<string, string?>
                {
                    ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                    ["foregroundWindowClass"] = foregroundWindowClass,
                    ["foregroundWindowTitle"] = foregroundWindowTitle,
                    ["originalClipboardSequence"] = originalSequence.ToString(),
                    ["isBrowserPdfContext"] = isBrowserPdfContext.ToString()
                });
            var copyCycles = isBrowserPdfContext ? BrowserPdfMaxCopyCycles : 1;
            for (var cycle = 1; cycle <= copyCycles; cycle++)
            {
                if (!WindowFocusInterop.IsValidWindow(foregroundWindow))
                {
                    failureMessage = "Clipboard fallback stopped because the target window is no longer available.";
                    AppDiagnostics.Warn(
                        "clipboard_fallback_target_window_invalid",
                        new Dictionary<string, string?>
                        {
                            ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                            ["copyCycle"] = cycle.ToString(),
                            ["copyCycles"] = copyCycles.ToString()
                        });
                    break;
                }

                if (isBrowserPdfContext)
                {
                    var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
                    AppDiagnostics.Info(
                        "clipboard_fallback_pdf_activation_attempted",
                        new Dictionary<string, string?>
                        {
                            ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                            ["copyCycle"] = cycle.ToString(),
                            ["copyCycles"] = copyCycles.ToString(),
                            ["activationResult"] = activationResult.ToString()
                        });

                    if (!activationResult)
                    {
                        failureMessage = "Clipboard fallback could not restore focus to the target PDF window.";
                        AppDiagnostics.Warn(
                            "clipboard_fallback_pdf_activation_failed",
                            new Dictionary<string, string?>
                            {
                                ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                                ["copyCycle"] = cycle.ToString(),
                                ["copyCycles"] = copyCycles.ToString(),
                                ["currentForegroundWindowHwnd"] = $"0x{ClipboardInterop.GetForegroundWindow().ToInt64():X}"
                            });
                        break;
                    }
                }

                ClipboardInterop.SendCopyShortcut();

                var cycleDeadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMilliseconds);
                var cycleCapturedText = string.Empty;
                uint cycleObservedSequence = 0;
                while (DateTime.UtcNow < cycleDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                    if (currentSequence != originalSequence)
                    {
                        cycleObservedSequence = currentSequence;
                        observedCopySequence = currentSequence;

                        if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                        {
                            cycleCapturedText = copiedText.Trim();
                            AppDiagnostics.Info(
                                "clipboard_fallback_selected_text_captured",
                                new Dictionary<string, string?>
                                {
                                    ["length"] = cycleCapturedText.Length.ToString(),
                                    ["copiedSequence"] = cycleObservedSequence.ToString(),
                                    ["copyCycle"] = cycle.ToString()
                                });
                            break;
                        }
                    }

                    Thread.Sleep(PollIntervalMilliseconds);
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

                    if (string.IsNullOrWhiteSpace(selectedText) || cycleCapturedText.Length > selectedText.Length)
                    {
                        selectedText = cycleCapturedText;
                    }
                }

                if (!isBrowserPdfContext || !ShouldRetryBrowserPdfCopy(cycle, copyCycles, selectedText))
                {
                    break;
                }

                AppDiagnostics.Info(
                    "clipboard_fallback_retrying_pdf_copy",
                    new Dictionary<string, string?>
                    {
                        ["copyCycle"] = cycle.ToString(),
                        ["capturedLength"] = selectedText?.Length.ToString(),
                        ["threshold"] = BrowserPdfShortSelectionThresholdCharacters.ToString()
                    });
            }

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                failureMessage = "Clipboard fallback timed out waiting for copied text.";
                AppDiagnostics.Warn(
                    "clipboard_fallback_timeout",
                    new Dictionary<string, string?>
                    {
                        ["originalSequence"] = originalSequence.ToString(),
                        ["lastObservedSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString(),
                        ["foregroundWindowHwnd"] = $"0x{foregroundWindow.ToInt64():X}",
                        ["foregroundWindowClass"] = foregroundWindowClass,
                        ["foregroundWindowTitle"] = foregroundWindowTitle
                    });
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        if (observedCopySequence != 0)
        {
            var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
            if (currentSequence == observedCopySequence)
            {
                var restored = TryRestoreClipboard(originalClipboard);
                if (!restored)
                {
                    AppDiagnostics.Warn("clipboard_fallback_restore_failed");
                    if (!string.IsNullOrWhiteSpace(selectedText))
                    {
                        return TextRetrievalResult.Retrieved(
                            selectedText,
                            Source,
                            "Selected text retrieved via clipboard fallback. Restoring previous clipboard content failed.");
                    }

                    return TextRetrievalResult.Failed(
                        "Clipboard fallback failed and restoring previous clipboard content also failed.",
                        Source);
                }
            }
            else if (!string.IsNullOrWhiteSpace(selectedText))
            {
                AppDiagnostics.Warn("clipboard_fallback_restore_skipped_sequence_changed");
                return TextRetrievalResult.Retrieved(
                    selectedText,
                    Source,
                    "Selected text retrieved via clipboard fallback. Clipboard changed again before restore, so restore was skipped.");
            }
        }

        if (canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            AppDiagnostics.Info(
                "clipboard_fallback_success",
                new Dictionary<string, string?>
                {
                    ["textLength"] = selectedText.Length.ToString(),
                    ["originalSequence"] = originalSequence.ToString(),
                    ["copiedSequence"] = observedCopySequence == 0 ? null : observedCopySequence.ToString(),
                    ["currentSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString()
                });
            return TextRetrievalResult.Retrieved(selectedText, Source, "Selected text retrieved via clipboard fallback.");
        }

        AppDiagnostics.Warn(
            "clipboard_fallback_failed",
            new Dictionary<string, string?>
            {
                ["message"] = failureMessage,
                ["originalSequence"] = originalSequence.ToString(),
                ["lastObservedSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString()
            });
        return TextRetrievalResult.Failed(failureMessage ?? "Clipboard fallback failed.", Source);
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
                            "clipboard_fallback_pdf_settle_window_upgrade",
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
                completion.TrySetResult(TextRetrievalResult.Failed($"Clipboard fallback failed: {ex.Message}", TextRetrievalSource.ClipboardFallback));
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        return completion.Task;
    }
}
