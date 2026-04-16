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

            ClipboardInterop.SendCopyShortcut();

            var deadline = DateTime.UtcNow.AddMilliseconds(PollTimeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                if (currentSequence != originalSequence)
                {
                    observedCopySequence = currentSequence;

                    if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                    {
                        selectedText = copiedText.Trim();
                        AppDiagnostics.Info(
                            "clipboard_fallback_selected_text_captured",
                            new System.Collections.Generic.Dictionary<string, string?>
                            {
                                ["length"] = selectedText.Length.ToString()
                            });
                        break;
                    }
                }

                Thread.Sleep(PollIntervalMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                failureMessage = "Clipboard fallback timed out waiting for copied text.";
                AppDiagnostics.Warn(
                    "clipboard_fallback_timeout",
                    new Dictionary<string, string?>
                    {
                        ["originalSequence"] = originalSequence.ToString(),
                        ["lastObservedSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString()
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
            AppDiagnostics.Info("clipboard_fallback_success");
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
                completion.TrySetResult(TextRetrievalResult.Failed($"Clipboard fallback failed: {ex.Message}", TextRetrievalSource.ClipboardFallback));
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        return completion.Task;
    }
}
