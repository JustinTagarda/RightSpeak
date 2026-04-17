using System;
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
    private const int PollTimeoutMilliseconds = 900;
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

        try
        {
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
                        paragraphText = Normalize(copiedText);
                        break;
                    }
                }

                Thread.Sleep(PollIntervalMilliseconds);
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
        }

        if (!string.IsNullOrWhiteSpace(paragraphText))
        {
            AppDiagnostics.Info(
                "paragraph_provider_clipboard_success",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["textLength"] = paragraphText.Length.ToString()
                });
            return TextRetrievalResult.Retrieved(
                paragraphText,
                TextRetrievalSource.ClipboardFallback,
                "Paragraph candidate retrieved via clipboard fallback.");
        }

        AppDiagnostics.Warn(
            "paragraph_provider_clipboard_timeout",
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["pollTimeoutMs"] = PollTimeoutMilliseconds.ToString(),
                ["pollIntervalMs"] = PollIntervalMilliseconds.ToString()
            });
        return TextRetrievalResult.Failed(
            "Clipboard paragraph fallback timed out waiting for selected text copy.",
            TextRetrievalSource.ClipboardFallback);
    }

    private static string? Normalize(string? value)
    {
        return value?.Trim('\0', '\r', '\n', ' ', '\t');
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
}
