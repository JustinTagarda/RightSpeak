using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class ClipboardDocumentTextProvider : IDocumentTextProvider
{
    private const int PollIntervalMilliseconds = 50;
    private const int PollTimeoutMilliseconds = 1400;
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
        return RunOnStaThreadAsync(() => TryGetDocumentText(cancellationToken), cancellationToken);
    }

    private TextRetrievalResult TryGetDocumentText(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var focusedElement = System.Windows.Automation.AutomationElement.FocusedElement;
        if (focusedElement is null)
        {
            return TextRetrievalResult.Failed(
                "No focused control is available for clipboard document fallback.",
                TextRetrievalSource.ClipboardFallback);
        }

        if (focusedElement.Current.IsPassword)
        {
            return TextRetrievalResult.Failed(
                "Clipboard document fallback is not allowed on password fields.",
                TextRetrievalSource.ClipboardFallback);
        }

        var originalSequence = ClipboardInterop.GetClipboardSequenceNumber();
        var snapshotSucceeded = TryReadClipboardDataObject(out System.Windows.IDataObject? originalClipboard);
        if (!snapshotSucceeded)
        {
            return TextRetrievalResult.Failed(
                "Clipboard document fallback failed: unable to read current clipboard safely.",
                TextRetrievalSource.ClipboardFallback);
        }

        uint observedCopySequence = 0;
        string? documentText = null;
        string? failureMessage = null;
        bool canceled = false;

        try
        {
            var foregroundWindow = ClipboardInterop.GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
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
                    ["originalClipboardSequence"] = originalSequence.ToString()
                });

            ClipboardInterop.SendSelectAllShortcut();
            Thread.Sleep(120);
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
                        documentText = copiedText.Trim();
                        break;
                    }
                }

                Thread.Sleep(PollIntervalMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(documentText))
            {
                failureMessage = "Clipboard document fallback timed out waiting for copied text.";
                AppDiagnostics.Warn(
                    "clipboard_document_capture_timeout",
                    new Dictionary<string, string?>
                    {
                        ["originalClipboardSequence"] = originalSequence.ToString(),
                        ["lastObservedClipboardSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString()
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
            if (currentSequence == observedCopySequence && !TryRestoreClipboard(originalClipboard))
            {
                AppDiagnostics.Warn("clipboard_document_restore_failed");
                if (!string.IsNullOrWhiteSpace(documentText))
                {
                    return TextRetrievalResult.Retrieved(
                        documentText,
                        TextRetrievalSource.ClipboardFallback,
                        "Document text retrieved via clipboard fallback. Restoring previous clipboard content failed.");
                }

                return TextRetrievalResult.Failed(
                    "Clipboard document fallback failed and restoring previous clipboard content also failed.",
                    TextRetrievalSource.ClipboardFallback);
            }
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
                    ["originalLength"] = originalLength.ToString(),
                    ["sanitizedLength"] = documentText.Length.ToString(),
                    ["removedLeadingLines"] = removedLeadingLines.ToString(),
                    ["preview"] = BuildPreview(documentText)
                });
            return TextRetrievalResult.Retrieved(
                documentText,
                TextRetrievalSource.ClipboardFallback,
                "Document text retrieved via clipboard fallback.");
        }

        return TextRetrievalResult.Failed(
            failureMessage ?? "Clipboard document fallback failed.",
            TextRetrievalSource.ClipboardFallback);
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
