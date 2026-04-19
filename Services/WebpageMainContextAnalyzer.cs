using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using RightSpeak.Interop;
using Rect = System.Windows.Rect;

namespace RightSpeak.Services;

public sealed class WebpageMainContextAnalyzer : IWebpageMainContextAnalyzer
{
    private const int GoogleDocsClipboardPollIntervalMilliseconds = 50;
    private const int GoogleDocsClipboardPollTimeoutMilliseconds = 2600;
    private const int GoogleDocsSelectAllSettleMilliseconds = 260;
    private const int GoogleDocsSelectionClearSettleMilliseconds = 120;
    private const int ClipboardAccessRetries = 8;
    private const int ClipboardAccessRetryDelayMilliseconds = 40;

    public WebpageAnalysisResult AnalyzeForegroundWindow()
    {
        return AnalyzeWindow(WindowFocusInterop.GetForegroundWindow());
    }

    public WebpageAnalysisResult AnalyzeWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return new WebpageAnalysisResult(
                "Analyze: no foreground window detected.",
                Array.Empty<DocumentNodeLogEntry>(),
                null);
        }

        WindowFocusInterop.GetWindowThreadProcessId(windowHandle, out var processId);
        var processName = TryGetProcessName(processId);
        var windowTitle = WindowFocusInterop.GetWindowText(windowHandle);
        var windowClass = WindowFocusInterop.GetWindowClassName(windowHandle);

        var root = AutomationElement.FromHandle(windowHandle);
        if (root is null)
        {
            return new WebpageAnalysisResult(
                $"Analyze: couldn't access UI Automation tree for window '{windowTitle}'.",
                Array.Empty<DocumentNodeLogEntry>(),
                null);
        }

        var focused = AutomationElement.FocusedElement;
        var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var totalDescendants = descendants.Count;

        var panelNames = new List<string>(8);
        var documentElements = new List<AutomationElement>(6);
        var panelCount = 0;
        var documentCount = 0;
        var textCount = 0;
        var buttonCount = 0;
        var controlTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < descendants.Count; i++)
        {
            var element = descendants[i];
            var controlTypeName = element.Current.ControlType?.ProgrammaticName ?? "<unknown>";
            controlTypeCounts.TryGetValue(controlTypeName, out var existing);
            controlTypeCounts[controlTypeName] = existing + 1;

            if (ReferenceEquals(element.Current.ControlType, ControlType.Pane) ||
                ReferenceEquals(element.Current.ControlType, ControlType.Group))
            {
                panelCount++;
                if (panelNames.Count < 8)
                {
                    var name = string.IsNullOrWhiteSpace(element.Current.Name) ? "<unnamed>" : element.Current.Name.Trim();
                    var className = string.IsNullOrWhiteSpace(element.Current.ClassName) ? "<no-class>" : element.Current.ClassName.Trim();
                    panelNames.Add($"{name} [{className}]");
                }
            }
            else if (ReferenceEquals(element.Current.ControlType, ControlType.Document))
            {
                documentCount++;
                documentElements.Add(element);
            }
            else if (ReferenceEquals(element.Current.ControlType, ControlType.Text))
            {
                textCount++;
            }
            else if (ReferenceEquals(element.Current.ControlType, ControlType.Button))
            {
                buttonCount++;
            }
        }

        var focusedSummary = focused is null
            ? "none"
            : $"{focused.Current.ControlType?.ProgrammaticName ?? "<unknown>"} | {SafeName(focused.Current.Name)} | {SafeName(focused.Current.ClassName)}";
        var topControlTypes = string.Join(
            ", ",
            controlTypeCounts
                .OrderByDescending(pair => pair.Value)
                .Take(5)
                .Select(pair => $"{pair.Key}:{pair.Value}"));
        var panelSample = panelNames.Count == 0 ? "<none>" : string.Join(" | ", panelNames);
        var documentCandidates = BuildDocumentCandidates(documentElements, focused, root, windowTitle);
        string? googleDocsFailureReason = null;
        if (LooksLikeGoogleDocsWindow(windowTitle) &&
            TryCaptureGoogleDocsClipboardDocument(windowHandle, focused, documentCandidates, out var googleDocsText, out googleDocsFailureReason))
        {
            documentCandidates.Add(BuildGoogleDocsClipboardCandidate(googleDocsText));
        }
        else if (LooksLikeGoogleDocsWindow(windowTitle))
        {
            AppDiagnostics.Warn(
                "analyze_google_docs_clipboard_candidate_rejected",
                new Dictionary<string, string?>
                {
                    ["reason"] = googleDocsFailureReason,
                    ["windowTitle"] = windowTitle
                });
        }

        var clusteredCandidates = ClusterDocumentCandidates(documentCandidates);
        var mainCluster = clusteredCandidates
            .OrderByDescending(cluster => cluster.BestCandidate.Score)
            .FirstOrDefault();
        var mainDocument = documentCandidates.Count == 0
            ? null
            : documentCandidates.OrderByDescending(candidate => candidate.Score).First();
        var topDocumentCandidates = documentCandidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(3)
            .Select((candidate, index) =>
                $"{index + 1}) score={candidate.Score}, focused={candidate.ContainsFocus}, offscreen={candidate.IsOffscreen}, descendants={candidate.DescendantCount}, text={candidate.TextNodeCount}, buttons={candidate.ButtonNodeCount}, name={candidate.Name}, class={candidate.ClassName}")
            .ToArray();
        var logEntries = documentCandidates
            .OrderByDescending(candidate => candidate.Score)
            .Take(2)
            .Select((candidate, index) => ToDocumentNodeLogEntry(candidate, index + 1))
            .ToArray();
        var mainContextCoreLogEntry = mainCluster is null
            ? null
            : BuildMainContextCoreLogEntry(mainCluster);
        var defaultChunks = mainContextCoreLogEntry?.Chunks
            .Where(chunk => chunk.IncludeByDefault)
            .ToArray();
        var chunkSummary = defaultChunks is null || defaultChunks.Length == 0
            ? "<none>"
            : string.Join(
                " | ",
                defaultChunks
                    .Take(4)
                    .Select(chunk =>
                        $"{chunk.ChunkIndex}) {chunk.ChunkType} score={chunk.ConfidenceScore}, lines={chunk.LineCount}, chars={chunk.CharacterCount}, speaker={chunk.Speaker}, preview={chunk.ContentPreview}"));

        var builder = new StringBuilder();
        builder.Append("Analyze External App: ");
        builder.Append(processName);
        builder.Append(" (PID ");
        builder.Append(processId);
        builder.Append(")");
        builder.AppendLine();
        builder.Append("Window: ");
        builder.Append(string.IsNullOrWhiteSpace(windowTitle) ? "<untitled>" : windowTitle.Trim());
        builder.AppendLine();
        builder.Append("Class: ");
        builder.Append(string.IsNullOrWhiteSpace(windowClass) ? "<none>" : windowClass);
        builder.AppendLine();
        builder.Append("Focused: ");
        builder.Append(focusedSummary);
        builder.AppendLine();
        builder.Append("UI Summary: Descendants=");
        builder.Append(totalDescendants);
        builder.Append(", Panels=");
        builder.Append(panelCount);
        builder.Append(", Documents=");
        builder.Append(documentCount);
        builder.Append(", Text=");
        builder.Append(textCount);
        builder.Append(", Buttons=");
        builder.Append(buttonCount);
        builder.AppendLine();
        builder.Append("Top Control Types: ");
        builder.Append(topControlTypes);
        builder.AppendLine();
        builder.Append("Main Context Document: ");
        if (mainDocument is null)
        {
            builder.Append("<none>");
        }
        else
        {
            builder.Append($"score={mainDocument.Score}, focused={mainDocument.ContainsFocus}, offscreen={mainDocument.IsOffscreen}, descendants={mainDocument.DescendantCount}, text={mainDocument.TextNodeCount}, buttons={mainDocument.ButtonNodeCount}, name={mainDocument.Name}, class={mainDocument.ClassName}");
        }
        builder.AppendLine();
        builder.Append("Document Candidates: ");
        builder.Append(topDocumentCandidates.Length == 0 ? "<none>" : string.Join(" | ", topDocumentCandidates));
        builder.AppendLine();
        builder.Append("Document Clusters: ");
        builder.Append(clusteredCandidates.Count == 0
            ? "<none>"
            : string.Join(
                " | ",
                clusteredCandidates
                    .Take(3)
                    .Select((cluster, index) =>
                        $"{index + 1}) hash={cluster.ClusterHash[..Math.Min(12, cluster.ClusterHash.Length)]}, count={cluster.Candidates.Count}, bestScore={cluster.BestCandidate.Score}, name={cluster.BestCandidate.Name}")));
        builder.AppendLine();
        builder.Append("Main Context Core: ");
        if (mainContextCoreLogEntry is null)
        {
            builder.Append("<none>");
        }
        else
        {
            builder.Append($"candidate={mainContextCoreLogEntry.CandidateName}, pageType={mainContextCoreLogEntry.PageType}, mode={mainContextCoreLogEntry.ExtractionMode}, coreLength={mainContextCoreLogEntry.CoreLength}, keptLines={mainContextCoreLogEntry.KeptLineCount}, noiseLines={mainContextCoreLogEntry.NoiseLineCount}, blocks={mainContextCoreLogEntry.ConversationBlockCount}, chunks={mainContextCoreLogEntry.ChunkCount}, cluster={mainContextCoreLogEntry.ClusterHash[..Math.Min(12, mainContextCoreLogEntry.ClusterHash.Length)]}");
        }
        builder.AppendLine();
        builder.Append("Main Context Chunks: ");
        builder.Append(chunkSummary);
        builder.AppendLine();
        builder.Append("Panel Samples: ");
        builder.Append(panelSample);
        return new WebpageAnalysisResult(builder.ToString(), logEntries, mainContextCoreLogEntry);
    }

    private static List<DocumentCandidate> BuildDocumentCandidates(
        IReadOnlyList<AutomationElement> documentElements,
        AutomationElement? focusedElement,
        AutomationElement root,
        string windowTitle)
    {
        var candidates = new List<DocumentCandidate>(documentElements.Count);
        var rootBounds = root.Current.BoundingRectangle;
        var isPdfWindow = LooksLikePdfWindow(windowTitle);

        foreach (var document in documentElements)
        {
            var subtree = document.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var descendantCount = subtree.Count;
            var textNodeCount = 0;
            var buttonNodeCount = 0;
            for (var i = 0; i < subtree.Count; i++)
            {
                var controlType = subtree[i].Current.ControlType;
                if (ReferenceEquals(controlType, ControlType.Text))
                {
                    textNodeCount++;
                }
                else if (ReferenceEquals(controlType, ControlType.Button))
                {
                    buttonNodeCount++;
                }
            }

            var containsFocus = ContainsElement(document, focusedElement);
            var isOffscreen = document.Current.IsOffscreen;
            var name = SafeName(document.Current.Name);
            var className = SafeName(document.Current.ClassName);
            var bounds = document.Current.BoundingRectangle;
            var area = bounds.IsEmpty ? 0d : bounds.Width * bounds.Height;
            var rootArea = rootBounds.IsEmpty ? 0d : rootBounds.Width * rootBounds.Height;
            var areaRatio = rootArea <= 0 ? 0 : area / rootArea;
            var textDensity = descendantCount <= 0 ? 0 : (double)textNodeCount / descendantCount;

            var extractedText = ExtractDocumentNodeText(document, out var contentSource);
            var normalizedContent = NormalizeDocumentContent(extractedText);
            var contentFingerprint = ComputeStableFingerprint(normalizedContent);
            var loweredName = name.ToLowerInvariant();

            var score = 0;
            score += descendantCount / 5;
            score += textNodeCount * 2;
            score -= buttonNodeCount;
            score += containsFocus ? 260 : 0;
            score += isOffscreen ? -180 : 120;
            score += areaRatio > 0 ? (int)Math.Min(140, areaRatio * 200) : 0;
            score += textDensity > 0.55 ? 90 : textDensity > 0.35 ? 45 : 0;
            score += normalizedContent.Length >= 12000 ? 120 : normalizedContent.Length >= 4000 ? 70 : normalizedContent.Length >= 1000 ? 25 : 0;
            score += string.Equals(contentSource, "text_pattern_document_range", StringComparison.Ordinal) ? 30 : 0;

            if (loweredName.Contains("conversation", StringComparison.Ordinal) ||
                loweredName.Contains("copilot", StringComparison.Ordinal) ||
                loweredName.Contains("chat", StringComparison.Ordinal) ||
                loweredName.Contains("article", StringComparison.Ordinal) ||
                loweredName.Contains("document", StringComparison.Ordinal))
            {
                score += 80;
            }

            if (isPdfWindow)
            {
                if (LooksLikePdfDocumentCandidate(name, normalizedContent))
                {
                    score += 260;
                }

                if (LooksLikePdfWarningCandidate(name))
                {
                    score -= 240;
                }

                if (buttonNodeCount == 0 && textNodeCount >= 40)
                {
                    score += 80;
                }
            }

            candidates.Add(new DocumentCandidate(
                name,
                className,
                descendantCount,
                textNodeCount,
                buttonNodeCount,
                containsFocus,
                isOffscreen,
                score,
                contentSource,
                extractedText,
                normalizedContent,
                contentFingerprint));
        }

        return candidates;
    }

    private static DocumentCandidate BuildGoogleDocsClipboardCandidate(string content)
    {
        var normalizedContent = NormalizeDocumentContent(content);
        var lineCount = normalizedContent
            .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
            .Length;
        var score = 5000 + Math.Min(320, normalizedContent.Length / 20) + Math.Min(160, lineCount * 8);
        return new DocumentCandidate(
            "Google Docs clipboard document",
            "<clipboard>",
            lineCount,
            lineCount,
            0,
            true,
            false,
            score,
            "google_docs_clipboard_select_all_copy",
            content,
            normalizedContent,
            ComputeStableFingerprint(normalizedContent));
    }

    private static bool TryCaptureGoogleDocsClipboardDocument(
        nint foregroundWindow,
        AutomationElement? focusedElement,
        IReadOnlyList<DocumentCandidate> existingCandidates,
        out string text,
        out string? failureReason)
    {
        text = string.Empty;
        failureReason = null;

        var shouldEscalateToClipboard = existingCandidates.Count == 0 ||
            existingCandidates.All(candidate =>
                candidate.NormalizedContent.Length < 500 ||
                LooksLikeGoogleDocsAccessibilityPrompt(candidate.NormalizedContent));
        AppDiagnostics.Info(
            "analyze_google_docs_clipboard_capture_considered",
            new Dictionary<string, string?>
            {
                ["candidateCount"] = existingCandidates.Count.ToString(),
                ["shouldEscalateToClipboard"] = shouldEscalateToClipboard.ToString(),
                ["bestCandidateLength"] = existingCandidates.OrderByDescending(candidate => candidate.Score).FirstOrDefault()?.NormalizedContent.Length.ToString()
            });

        if (!shouldEscalateToClipboard)
        {
            failureReason = "uia_candidate_already_has_body_like_content";
            return false;
        }

        var originalSequence = ClipboardInterop.GetClipboardSequenceNumber();
        if (!TryReadClipboardDataObject(out var originalClipboard))
        {
            failureReason = "clipboard_snapshot_failed";
            return false;
        }

        uint observedSequence = 0;
        try
        {
            var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
            try
            {
                focusedElement?.SetFocus();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Warn(
                    "analyze_google_docs_focused_element_refocus_failed",
                    new Dictionary<string, string?>
                    {
                        ["message"] = ex.Message
                    });
            }

            AppDiagnostics.Info(
                "analyze_google_docs_clipboard_capture_started",
                new Dictionary<string, string?>
                {
                    ["activationResult"] = activationResult.ToString(),
                    ["originalClipboardSequence"] = originalSequence.ToString(),
                    ["focusedElementName"] = BuildPreview(focusedElement?.Current.Name ?? string.Empty),
                    ["focusedElementControlType"] = focusedElement?.Current.ControlType?.ProgrammaticName
                });

            for (var cycle = 1; cycle <= 2; cycle++)
            {
                var expectedSequence = ClipboardInterop.GetClipboardSequenceNumber();
                ClipboardInterop.SendSelectAllShortcut();
                Thread.Sleep(GoogleDocsSelectAllSettleMilliseconds);
                ClipboardInterop.SendSelectAllShortcut();
                Thread.Sleep(GoogleDocsSelectAllSettleMilliseconds);

                ClipboardInterop.SendCopyShortcut();
                AppDiagnostics.Info(
                    "analyze_google_docs_clipboard_shortcuts_sent",
                    new Dictionary<string, string?>
                    {
                        ["cycle"] = cycle.ToString(),
                        ["selectAllCount"] = "2"
                    });

                var deadline = DateTime.UtcNow.AddMilliseconds(GoogleDocsClipboardPollTimeoutMilliseconds);
                while (DateTime.UtcNow < deadline)
                {
                    var currentSequence = ClipboardInterop.GetClipboardSequenceNumber();
                    if (currentSequence != expectedSequence)
                    {
                        observedSequence = currentSequence;
                        expectedSequence = currentSequence;
                        if (TryReadClipboardText(out var copiedText) && !string.IsNullOrWhiteSpace(copiedText))
                        {
                            copiedText = copiedText.Trim();
                            AppDiagnostics.Info(
                                "analyze_google_docs_clipboard_capture_observed",
                                new Dictionary<string, string?>
                                {
                                    ["cycle"] = cycle.ToString(),
                                    ["capturedLength"] = copiedText.Length.ToString(),
                                    ["capturedPreview"] = BuildPreview(copiedText)
                                });

                            if (ValidateGoogleDocsClipboardCandidate(copiedText, out failureReason))
                            {
                                text = copiedText;
                                AppDiagnostics.Info(
                                    "analyze_google_docs_clipboard_candidate_accepted",
                                    new Dictionary<string, string?>
                                    {
                                        ["cycle"] = cycle.ToString(),
                                        ["capturedLength"] = copiedText.Length.ToString(),
                                        ["lineCount"] = NormalizeDocumentContent(copiedText).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length.ToString()
                                    });
                                return true;
                            }
                        }
                    }

                    Thread.Sleep(GoogleDocsClipboardPollIntervalMilliseconds);
                }

                AppDiagnostics.Warn(
                    "analyze_google_docs_clipboard_capture_cycle_timeout",
                    new Dictionary<string, string?>
                    {
                        ["cycle"] = cycle.ToString(),
                        ["lastObservedClipboardSequence"] = ClipboardInterop.GetClipboardSequenceNumber().ToString(),
                        ["expectedClipboardSequence"] = expectedSequence.ToString()
                    });
            }

            failureReason ??= "clipboard_capture_timeout_or_invalid_candidate";
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"clipboard_capture_exception:{ex.GetType().Name}";
            AppDiagnostics.Warn(
                "analyze_google_docs_clipboard_capture_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return false;
        }
        finally
        {
            if (observedSequence != 0 && !string.IsNullOrWhiteSpace(text))
            {
                ClearGoogleDocsTemporarySelection(foregroundWindow, focusedElement, "finally_after_capture");
            }

            if (observedSequence != 0 && ClipboardInterop.GetClipboardSequenceNumber() == observedSequence)
            {
                if (!TryRestoreClipboard(originalClipboard))
                {
                    AppDiagnostics.Warn("analyze_google_docs_clipboard_restore_failed");
                }
            }
        }
    }

    private static void ClearGoogleDocsTemporarySelection(nint foregroundWindow, AutomationElement? focusedElement, string phase)
    {
        try
        {
            var activationResult = WindowFocusInterop.TryActivateWindow(foregroundWindow);
            try
            {
                focusedElement?.SetFocus();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Warn(
                    "analyze_google_docs_selection_clear_refocus_failed",
                    new Dictionary<string, string?>
                    {
                        ["phase"] = phase,
                        ["message"] = ex.Message
                    });
            }

            ClipboardInterop.SendEscapeKey();
            Thread.Sleep(GoogleDocsSelectionClearSettleMilliseconds);
            ClipboardInterop.SendLeftArrowKey();
            Thread.Sleep(GoogleDocsSelectionClearSettleMilliseconds);
            AppDiagnostics.Info(
                "analyze_google_docs_selection_clear_sent",
                new Dictionary<string, string?>
                {
                    ["phase"] = phase,
                    ["activationResult"] = activationResult.ToString(),
                    ["clearSequence"] = "escape,left_arrow"
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "analyze_google_docs_selection_clear_failed",
                new Dictionary<string, string?>
                {
                    ["phase"] = phase,
                    ["message"] = ex.Message
                });
        }
    }

    private static bool ValidateGoogleDocsClipboardCandidate(string text, out string? failureReason)
    {
        var normalized = NormalizeDocumentContent(text);
        var lines = normalized
            .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
        if (normalized.Length < 180)
        {
            failureReason = "candidate_too_short";
            return false;
        }

        if (LooksLikeGoogleDocsAccessibilityPrompt(normalized))
        {
            failureReason = "candidate_is_google_docs_accessibility_prompt";
            return false;
        }

        if (lines.Length < 3 && normalized.Length < 800)
        {
            failureReason = "candidate_not_document_like";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static List<DocumentCandidateCluster> ClusterDocumentCandidates(IReadOnlyList<DocumentCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.ContentFingerprint, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupedCandidates = group
                    .OrderByDescending(candidate => candidate.Score)
                    .ToArray();
                return new DocumentCandidateCluster(group.Key, groupedCandidates, groupedCandidates[0]);
            })
            .OrderByDescending(cluster => cluster.BestCandidate.Score)
            .ToList();
    }

    private static string ExtractDocumentNodeText(AutomationElement document, out string contentSource)
    {
        contentSource = "none";
        try
        {
            if (document.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                var text = textPattern.DocumentRange.GetText(-1)?.Trim('\0', ' ', '\r', '\n', '\t');
                if (!string.IsNullOrWhiteSpace(text))
                {
                    contentSource = "text_pattern_document_range";
                    return text;
                }
            }
        }
        catch
        {
        }

        try
        {
            if (document.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                var text = valuePattern.Current.Value?.Trim('\0', ' ', '\r', '\n', '\t');
                if (!string.IsNullOrWhiteSpace(text))
                {
                    contentSource = "value_pattern";
                    return text;
                }
            }
        }
        catch
        {
        }

        try
        {
            var textNodes = document.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var fragments = new List<string>(Math.Min(200, textNodes.Count));
            var last = string.Empty;
            for (var i = 0; i < textNodes.Count && fragments.Count < 200; i++)
            {
                var fragment = SafeName(textNodes[i].Current.Name);
                if (fragment == "<none>" || fragment == "<unnamed>" || string.Equals(fragment, last, StringComparison.Ordinal))
                {
                    continue;
                }

                fragments.Add(fragment);
                last = fragment;
            }

            if (fragments.Count > 0)
            {
                contentSource = "text_node_names";
                return string.Join(Environment.NewLine, fragments);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static DocumentNodeLogEntry ToDocumentNodeLogEntry(DocumentCandidate candidate, int rank)
    {
        const int maxContentLength = 12000;
        var rawContent = candidate.Content ?? string.Empty;
        var contentWasTruncated = rawContent.Length > maxContentLength;
        var contentForLog = contentWasTruncated
            ? rawContent[..maxContentLength]
            : rawContent;

        return new DocumentNodeLogEntry(
            rank,
            candidate.Score,
            candidate.Name,
            candidate.ClassName,
            candidate.ContainsFocus,
            candidate.IsOffscreen,
            candidate.DescendantCount,
            candidate.TextNodeCount,
            candidate.ButtonNodeCount,
            candidate.ContentSource,
            rawContent.Length,
            contentWasTruncated,
            contentForLog);
    }

    private static MainContextCoreLogEntry BuildMainContextCoreLogEntry(DocumentCandidateCluster cluster)
    {
        var candidate = cluster.BestCandidate;
        var trimmed = ExtractMainContextCore(candidate);
        var chunkLogEntries = trimmed.Chunks
            .Select(ToMainContextChunkLogEntry)
            .ToArray();
        return new MainContextCoreLogEntry(
            cluster.ClusterHash,
            candidate.Name,
            candidate.Score,
            trimmed.PageType,
            trimmed.ExtractionMode,
            candidate.NormalizedContent.Length,
            trimmed.CoreContent.Length,
            trimmed.NoiseLineCount,
            trimmed.KeptLineCount,
            trimmed.ConversationBlockCount,
            trimmed.Chunks.Count,
            trimmed.CoreContent,
            chunkLogEntries);
    }

    private static string NormalizeDocumentContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Replace("\uFFFC", string.Empty, StringComparison.Ordinal).Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        return string.Join(Environment.NewLine, lines);
    }

    private static string ComputeStableFingerprint(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "<empty>";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static TrimmedMainContext ExtractMainContextCore(DocumentCandidate candidate)
    {
        var normalizedContent = candidate.NormalizedContent;
        if (string.IsNullOrWhiteSpace(normalizedContent))
        {
            return new TrimmedMainContext("unknown", "empty", string.Empty, 0, 0, 0, Array.Empty<MainContextChunk>());
        }

        var lines = normalizedContent
            .Split(new[] { Environment.NewLine }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var preferPdfChunking = candidate.Name.Contains("PDF Document", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(candidate.ContentSource, "google_docs_clipboard_select_all_copy", StringComparison.Ordinal))
        {
            return ExtractGoogleDocsDocumentContext(lines);
        }

        if (TryExtractChatGptConversationThread(lines, out var rawChatGptBlocks, out var rawChatGptConversationContent))
        {
            var contentLineCount = rawChatGptBlocks.Sum(block => block.MessageLines.Count);
            var derivedNoiseLineCount = Math.Max(0, lines.Length - contentLineCount);
            var chunks = rawChatGptBlocks
                .Select((block, index) => BuildConversationChunk(index + 1, block))
                .ToArray();
            return new TrimmedMainContext(
                "conversation",
                "chatgpt_conversation_thread",
                rawChatGptConversationContent,
                derivedNoiseLineCount,
                contentLineCount,
                rawChatGptBlocks.Count,
                chunks);
        }

        var filteredLines = new List<string>(lines.Length);
        var noiseLineCount = 0;
        var previousLine = string.Empty;
        foreach (var line in lines)
        {
            if ((LooksLikeChromeNoiseLine(line, previousLine) &&
                 !ShouldPreservePdfStructuralLine(line, preferPdfChunking)) ||
                (preferPdfChunking && LooksLikePdfViewerWarningLine(line)))
            {
                noiseLineCount++;
                continue;
            }

            filteredLines.Add(line);
            previousLine = line;
        }

        if (TryExtractConversationThread(filteredLines, out var conversationBlocks, out var conversationContent))
        {
            var chunks = conversationBlocks
                .Select((block, index) => BuildConversationChunk(index + 1, block))
                .ToArray();
            return new TrimmedMainContext(
                "conversation",
                "conversation_thread",
                conversationContent,
                noiseLineCount,
                filteredLines.Count,
                conversationBlocks.Count,
                chunks);
        }

        if (preferPdfChunking && LooksLikeShortFormDocument(filteredLines))
        {
            var shortFormLines = filteredLines
                .Where(line => ShouldKeepShortFormDocumentLine(line))
                .ToArray();
            var shortFormChunks = BuildShortFormDocumentChunks(shortFormLines);
            if (shortFormChunks.Count > 0)
            {
                return new TrimmedMainContext(
                    "document",
                    "pdf_short_form_document_chunks",
                    string.Join(Environment.NewLine + Environment.NewLine, shortFormChunks.Select(chunk => chunk.Content)),
                    noiseLineCount,
                    shortFormLines.Length,
                    0,
                    shortFormChunks);
            }
        }

        if (preferPdfChunking && LooksLikeResumeDocument(filteredLines))
        {
            var resumeLines = filteredLines
                .Where(ShouldKeepResumeDocumentLine)
                .ToArray();
            var resumeChunks = BuildResumeDocumentChunks(resumeLines);
            if (resumeChunks.Count > 0)
            {
                return new TrimmedMainContext(
                    "resume",
                    "pdf_resume_document_chunks",
                    string.Join(Environment.NewLine + Environment.NewLine, resumeChunks.Select(chunk => chunk.Content)),
                    noiseLineCount,
                    resumeChunks.Sum(chunk => chunk.LineCount),
                    0,
                    resumeChunks);
            }
        }

        var contentLines = filteredLines
            .Where(line => ShouldKeepStructuredContentLine(line, preferPdfChunking))
            .ToArray();
        var structuredChunks = BuildStructuredContentChunks(contentLines, preferPdfChunking);
        if (structuredChunks.Count > 0)
        {
            if (preferPdfChunking)
            {
                structuredChunks = OrderPdfArticleChunks(structuredChunks);
            }

            var defaultContentChunks = structuredChunks
                .Where(chunk => chunk.IncludeByDefault)
                .ToArray();
            var coreContentChunks = defaultContentChunks.Length == 0
                ? structuredChunks.ToArray()
                : defaultContentChunks;
            return new TrimmedMainContext(
                "article",
                preferPdfChunking ? "pdf_structured_chunks" : "structured_article_chunks",
                string.Join(Environment.NewLine + Environment.NewLine, coreContentChunks.Select(chunk => chunk.Content)),
                noiseLineCount,
                coreContentChunks.Sum(chunk => chunk.LineCount),
                0,
                structuredChunks);
        }

        if (contentLines.Length >= 2)
        {
            return new TrimmedMainContext(
                "article",
                "dominant_paragraphs",
                string.Join(Environment.NewLine, contentLines),
                noiseLineCount,
                contentLines.Length,
                0,
            new[] { BuildStructuredChunk(1, "body", preferPdfChunking ? "body" : "content", string.Empty, contentLines, false, preferPdfChunking) });
        }

        var mixedLines = filteredLines.Where(line => !LooksLikeReferenceNoiseLine(line)).ToArray();
        var mixedChunks = BuildStructuredContentChunks(mixedLines, false);
        return new TrimmedMainContext(
            "mixed",
            mixedChunks.Count > 0 ? "filtered_content_chunks" : "filtered_content",
            string.Join(Environment.NewLine, mixedLines),
            noiseLineCount,
            filteredLines.Count,
            0,
            mixedChunks);
    }

    private static TrimmedMainContext ExtractGoogleDocsDocumentContext(IReadOnlyList<string> lines)
    {
        var documentLines = new List<string>(lines.Count);
        var noiseLineCount = 0;
        foreach (var line in lines)
        {
            var cleaned = CleanGoogleDocsDocumentLine(line);
            if (string.IsNullOrWhiteSpace(cleaned) ||
                LooksLikeGoogleDocsAccessibilityPrompt(cleaned))
            {
                noiseLineCount++;
                continue;
            }

            documentLines.Add(cleaned);
        }

        var chunks = BuildGoogleDocsDocumentChunks(documentLines);
        return new TrimmedMainContext(
            "document",
            "google_docs_clipboard_document_chunks",
            string.Join(Environment.NewLine + Environment.NewLine, chunks.Select(chunk => chunk.Content)),
            noiseLineCount,
            documentLines.Count,
            0,
            chunks);
    }

    private static string CleanGoogleDocsDocumentLine(string line)
    {
        var cleaned = line
            .Replace("\uFFFC", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (cleaned.StartsWith("pro", StringComparison.Ordinal) &&
            cleaned.Length > 8 &&
            char.IsUpper(cleaned[3]))
        {
            cleaned = cleaned[3..].TrimStart();
        }

        return cleaned;
    }

    private static List<MainContextChunk> BuildGoogleDocsDocumentChunks(IReadOnlyList<string> lines)
    {
        var chunks = new List<MainContextChunk>();
        var currentLines = new List<string>();
        var chunkIndex = 1;

        void FlushCurrent()
        {
            if (currentLines.Count == 0)
            {
                return;
            }

            chunks.Add(BuildStructuredChunk(chunkIndex++, "document_chunk", "document", string.Empty, currentLines, false, false));
            currentLines = new List<string>();
        }

        foreach (var line in lines)
        {
            if (currentLines.Count > 0 &&
                LooksLikeGoogleDocsSectionHeading(line) &&
                currentLines.Sum(entry => entry.Length) >= 500)
            {
                FlushCurrent();
            }

            currentLines.Add(line);
            var characterCount = currentLines.Sum(entry => entry.Length);
            if (characterCount >= 1400 || currentLines.Count >= 14)
            {
                FlushCurrent();
            }
        }

        FlushCurrent();
        return chunks;
    }

    private static bool LooksLikeGoogleDocsSectionHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 90)
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.EndsWith(':'))
        {
            return true;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 8 &&
               words.Count(word => word.All(character => !char.IsLetter(character) || char.IsUpper(character))) >= Math.Max(1, words.Length - 1);
    }

    private static MainContextChunkLogEntry ToMainContextChunkLogEntry(MainContextChunk chunk)
    {
        const int maxChunkContentLength = 2500;
        var contentWasTruncated = chunk.Content.Length > maxChunkContentLength;
        var contentForLog = contentWasTruncated
            ? chunk.Content[..maxChunkContentLength]
            : chunk.Content;
        var preview = chunk.Content
            .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
            .Trim();
        if (preview.Length > 140)
        {
            preview = preview[..140] + "...";
        }

        return new MainContextChunkLogEntry(
            chunk.Index,
            chunk.ChunkType,
            chunk.SectionType,
            string.IsNullOrWhiteSpace(chunk.Speaker) ? "<none>" : chunk.Speaker,
            chunk.LineCount,
            chunk.CharacterCount,
            chunk.ConfidenceScore,
            chunk.IsHeadingLike,
            chunk.IncludeByDefault,
            contentWasTruncated,
            preview,
            contentForLog);
    }

    private static List<MainContextChunk> BuildStructuredContentChunks(IReadOnlyList<string> lines, bool preferPdfChunking)
    {
        var chunks = new List<MainContextChunk>();
        if (lines.Count == 0)
        {
            return chunks;
        }

        var currentLines = new List<string>();
        var currentHasHeading = false;
        var currentSectionType = preferPdfChunking ? "body" : "content";
        var chunkIndex = 1;

        void FlushCurrent()
        {
            if (currentLines.Count == 0)
            {
                return;
            }

            chunks.Add(BuildStructuredChunk(chunkIndex++, currentHasHeading ? "section" : "body", currentSectionType, string.Empty, currentLines, currentHasHeading, preferPdfChunking));
            currentLines = new List<string>();
            currentHasHeading = false;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushCurrent();
                continue;
            }

            var hasSectionTransition = TryClassifySectionTransition(line, preferPdfChunking, out var nextSectionType);
            if (hasSectionTransition && currentLines.Count > 0 &&
                !string.Equals(nextSectionType, currentSectionType, StringComparison.Ordinal))
            {
                FlushCurrent();
                currentSectionType = nextSectionType;
            }

            var isHeading = LooksLikeHeadingLine(line, preferPdfChunking) || hasSectionTransition;
            if (isHeading && currentLines.Count > 0 && !hasSectionTransition)
            {
                FlushCurrent();
            }

            if (hasSectionTransition)
            {
                currentSectionType = nextSectionType;
            }
            else if (currentLines.Count == 0)
            {
                currentSectionType = preferPdfChunking ? "body" : "content";
            }

            currentLines.Add(line);
            currentHasHeading = currentHasHeading || isHeading;

            var characterCount = currentLines.Sum(entry => entry.Length);
            if (characterCount >= (preferPdfChunking ? 1300 : 950) || currentLines.Count >= (preferPdfChunking ? 12 : 8))
            {
                FlushCurrent();
            }
        }

        FlushCurrent();
        return chunks;
    }

    private static bool LooksLikeShortFormDocument(IReadOnlyList<string> lines)
    {
        if (lines.Count is < 4 or > 40)
        {
            return false;
        }

        var totalLength = lines.Sum(line => line.Length);
        if (totalLength > 6000)
        {
            return false;
        }

        var hasLetterSignal = lines.Any(line =>
            line.StartsWith("Dear ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Parents/Guardians", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Respectfully", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Sincerely", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Greetings", StringComparison.OrdinalIgnoreCase));
        var hasReferencesSignal = lines.Any(line =>
            NormalizeSectionLabel(line) is "references" or "reference" ||
            LooksLikeStrongBibliographyLine(line));

        return hasLetterSignal && !hasReferencesSignal;
    }

    private static bool ShouldKeepShortFormDocumentLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (LooksLikePdfMetadataLine(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        if (normalized is "website" or "quick response code")
        {
            return false;
        }

        return true;
    }

    private static List<MainContextChunk> BuildShortFormDocumentChunks(IReadOnlyList<string> lines)
    {
        var chunks = new List<MainContextChunk>();
        if (lines.Count == 0)
        {
            return chunks;
        }

        var currentLines = new List<string>();
        var chunkIndex = 1;

        void FlushCurrent()
        {
            if (currentLines.Count == 0)
            {
                return;
            }

            chunks.Add(BuildStructuredChunk(chunkIndex++, "short_form_block", "body", string.Empty, currentLines, false, true));
            currentLines = new List<string>();
        }

        foreach (var line in lines)
        {
            var normalized = NormalizeSectionLabel(line);
            var startsNewBlock =
                currentLines.Count > 0 &&
                (line.StartsWith("Dear ", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Greetings", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("We ", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Please ", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Should ", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("Respectfully", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("treasurer", StringComparison.Ordinal) ||
                 normalized.Contains("instructor", StringComparison.Ordinal));

            if (startsNewBlock)
            {
                FlushCurrent();
            }

            currentLines.Add(line);

            var characterCount = currentLines.Sum(entry => entry.Length);
            if (characterCount >= 1400 || currentLines.Count >= 8)
            {
                FlushCurrent();
            }
        }

        FlushCurrent();
        return chunks;
    }

    private static bool LooksLikeResumeDocument(IReadOnlyList<string> lines)
    {
        if (lines.Count is < 8 or > 160)
        {
            return false;
        }

        var normalizedLines = lines
            .Select(NormalizeSectionLabel)
            .Where(line => line.Length > 0)
            .ToArray();
        var resumeSignals = 0;

        if (normalizedLines.Any(line => line.Contains("professional summary", StringComparison.Ordinal) ||
                                        line.Contains("rofessional summary", StringComparison.Ordinal)))
        {
            resumeSignals++;
        }

        if (normalizedLines.Any(line => line.Contains("work experience", StringComparison.Ordinal) ||
                                        line.Contains("experience", StringComparison.Ordinal)))
        {
            resumeSignals++;
        }

        if (normalizedLines.Any(line => line is "skills" or "kills" ||
                                        line.Contains("backend", StringComparison.Ordinal) ||
                                        line.Contains("frontend", StringComparison.Ordinal)))
        {
            resumeSignals++;
        }

        if (normalizedLines.Any(line => line is "education" or "ducation" ||
                                        line.Contains("bachelor", StringComparison.Ordinal)))
        {
            resumeSignals++;
        }

        if (lines.Any(line => line.Contains('@') ||
                              line.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase) ||
                              line.Contains("portfolio", StringComparison.OrdinalIgnoreCase)))
        {
            resumeSignals++;
        }

        return resumeSignals >= 3;
    }

    private static bool ShouldKeepResumeDocumentLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (LooksLikePdfViewerWarningLine(line) ||
            LooksLikePdfBoilerplateNoiseLine(line) ||
            LooksLikePdfMetadataLine(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        return normalized is not "powered by adobe acrobat" and not "page" and not "1";
    }

    private static List<MainContextChunk> BuildResumeDocumentChunks(IReadOnlyList<string> lines)
    {
        var chunks = new List<MainContextChunk>();
        if (lines.Count == 0)
        {
            return chunks;
        }

        var currentLines = new List<string>();
        var currentSectionType = "resume_header";
        var currentHasHeading = false;
        var chunkIndex = 1;

        void FlushCurrent()
        {
            if (currentLines.Count == 0)
            {
                return;
            }

            chunks.Add(BuildStructuredChunk(chunkIndex++, "resume_section", currentSectionType, string.Empty, currentLines, currentHasHeading, true));
            currentLines = new List<string>();
            currentHasHeading = false;
        }

        foreach (var line in lines)
        {
            var sectionType = ClassifyResumeSectionLine(line);
            if (!string.Equals(sectionType, "resume_body", StringComparison.Ordinal))
            {
                if (currentLines.Count > 0 && !string.Equals(sectionType, currentSectionType, StringComparison.Ordinal))
                {
                    FlushCurrent();
                }

                currentSectionType = sectionType;
                currentHasHeading = true;
            }
            else if (currentLines.Count == 0 && chunks.Count == 0)
            {
                currentSectionType = LooksLikeResumeHeaderLine(line)
                    ? "resume_header"
                    : "resume_body";
            }

            currentLines.Add(line);

            var characterCount = currentLines.Sum(entry => entry.Length);
            if (characterCount >= 1400 || currentLines.Count >= 12)
            {
                FlushCurrent();
            }
        }

        FlushCurrent();
        return chunks;
    }

    private static string ClassifyResumeSectionLine(string line)
    {
        var normalized = NormalizeSectionLabel(line);
        if (normalized.Length == 0)
        {
            return "resume_body";
        }

        if (LooksLikeResumeHeaderLine(line))
        {
            return "resume_header";
        }

        if (normalized.Contains("professional summary", StringComparison.Ordinal) ||
            normalized.Contains("rofessional summary", StringComparison.Ordinal))
        {
            return "resume_summary";
        }

        if (normalized.Contains("work experience", StringComparison.Ordinal) ||
            normalized is "experience")
        {
            return "resume_experience";
        }

        if (normalized is "skills" or "kills" ||
            LooksLikeResumeSkillsHeading(line, normalized))
        {
            return "resume_skills";
        }

        if (normalized is "education" or "ducation")
        {
            return "resume_education";
        }

        return "resume_body";
    }

    private static bool LooksLikeResumeHeaderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        if (line.Contains('@') ||
            line.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("portfolio", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var titleLike = wordCount <= 14 &&
                        line.Length <= 120;

        return titleLike &&
               (normalized.Contains("full stack", StringComparison.Ordinal) ||
                normalized.Contains("developer", StringComparison.Ordinal) &&
                normalized.Contains("rest apis", StringComparison.Ordinal));
    }

    private static bool LooksLikeResumeSkillsHeading(string line, string normalized)
    {
        var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var isShortHeading = wordCount <= 4 && line.Length <= 40;
        if (!isShortHeading)
        {
            return false;
        }

        return normalized.Contains("backend", StringComparison.Ordinal) ||
               normalized.Contains("frontend", StringComparison.Ordinal) ||
               normalized.Contains("databases", StringComparison.Ordinal) ||
               normalized.Contains("devops", StringComparison.Ordinal) ||
               normalized.Contains("infrastructure", StringComparison.Ordinal) ||
               normalized.Contains("tools", StringComparison.Ordinal) ||
               normalized.Contains("collaboration", StringComparison.Ordinal) ||
               normalized.Contains("productivity", StringComparison.Ordinal);
    }

    private static MainContextChunk BuildConversationChunk(int index, ConversationBlock block)
    {
        return BuildStructuredChunk(index, "conversation_message", "conversation", block.Speaker, block.MessageLines, false, false);
    }

    private static MainContextChunk BuildStructuredChunk(
        int index,
        string chunkType,
        string sectionType,
        string speaker,
        IReadOnlyList<string> lines,
        bool isHeadingLike,
        bool preferPdfChunking)
    {
        var normalizedSectionType = string.IsNullOrWhiteSpace(sectionType) ? "content" : sectionType;
        var content = string.Join(Environment.NewLine, lines);
        return new MainContextChunk(
            index,
            chunkType,
            normalizedSectionType,
            speaker,
            lines.Count,
            content.Length,
            ComputeChunkConfidence(chunkType, normalizedSectionType, lines, isHeadingLike, preferPdfChunking),
            isHeadingLike,
            ShouldIncludeSectionByDefault(normalizedSectionType),
            content);
    }

    private static int ComputeChunkConfidence(
        string chunkType,
        string sectionType,
        IReadOnlyList<string> lines,
        bool isHeadingLike,
        bool preferPdfChunking)
    {
        var score = 45;
        if (string.Equals(chunkType, "conversation_message", StringComparison.Ordinal))
        {
            score += 30;
        }

        if (isHeadingLike)
        {
            score += 10;
        }

        if (preferPdfChunking)
        {
            score += 10;
        }

        if (sectionType is "references" or "acknowledgement" or "metadata")
        {
            score -= 20;
        }
        else if (sectionType is "abstract" or "body" or "introduction" or "methods" or "results" or "discussion" or "conclusion")
        {
            score += 8;
        }

        var sentenceLikeLines = lines.Count(line =>
            line.EndsWith(".", StringComparison.Ordinal) ||
            line.EndsWith("?", StringComparison.Ordinal) ||
            line.EndsWith("!", StringComparison.Ordinal) ||
            line.EndsWith(":", StringComparison.Ordinal));
        var noiseLikeLines = lines.Count(LooksLikeReferenceNoiseLine);
        var averageWords = lines.Count == 0
            ? 0
            : (int)lines.Average(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var characterCount = lines.Sum(line => line.Length);

        score += Math.Min(12, lines.Count * 2);
        score += Math.Min(18, characterCount / 120);
        score += sentenceLikeLines > 0 ? 12 : 0;
        score += averageWords >= 8 ? 12 : averageWords >= 5 ? 6 : 0;
        score -= noiseLikeLines * 10;

        return Math.Max(1, Math.Min(100, score));
    }

    private static bool ShouldKeepStructuredContentLine(string line, bool preferPdfChunking)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (preferPdfChunking)
        {
            if (TryClassifySectionTransition(line, true, out _))
            {
                return true;
            }

            if (LooksLikeStrongBibliographyLine(line) || LooksLikePdfMetadataLine(line))
            {
                return true;
            }

            if (LooksLikePdfBoilerplateNoiseLine(line) ||
                LooksLikePdfCaptionLine(line))
            {
                return false;
            }
        }

        return LooksLikeMainContentLine(line) && !LooksLikeReferenceNoiseLine(line);
    }

    private static List<MainContextChunk> OrderPdfArticleChunks(IReadOnlyList<MainContextChunk> chunks)
    {
        return chunks
            .Select((chunk, originalIndex) => (Chunk: chunk, OriginalIndex: originalIndex))
            .OrderBy(entry => GetPdfArticleSectionOrder(entry.Chunk))
            .ThenBy(entry => entry.OriginalIndex)
            .Select((entry, newIndex) => entry.Chunk with { Index = newIndex + 1 })
            .ToList();
    }

    private static int GetPdfArticleSectionOrder(MainContextChunk chunk)
    {
        return chunk.SectionType switch
        {
            "abstract" => 0,
            "introduction" => 1,
            "body" => 2,
            "methods" => 3,
            "results" => 4,
            "discussion" => 5,
            "conclusion" => 6,
            "acknowledgement" => 7,
            "references" => 8,
            "metadata" => 9,
            _ => chunk.IncludeByDefault ? 2 : 9
        };
    }

    private static bool TryClassifySectionTransition(string line, bool preferPdfChunking, out string sectionType)
    {
        sectionType = string.Empty;
        if (!preferPdfChunking || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var isHeaderSized = wordCount <= 8 && line.Length <= 90;
        var isSectionHeadingCandidate =
            isHeaderSized &&
            !LooksLikeSentenceFragment(line) &&
            LooksLikeIntentionalPdfSectionHeading(line, normalized);

        if (normalized is "abstract")
        {
            sectionType = "abstract";
            return true;
        }

        if (normalized is "introduction")
        {
            sectionType = "introduction";
            return true;
        }

        if (isSectionHeadingCandidate &&
            (normalized.Contains("method", StringComparison.Ordinal) ||
             normalized.Contains("materials", StringComparison.Ordinal)))
        {
            sectionType = "methods";
            return true;
        }

        if (isSectionHeadingCandidate && normalized.Contains("result", StringComparison.Ordinal))
        {
            sectionType = "results";
            return true;
        }

        if (isSectionHeadingCandidate && normalized.Contains("discussion", StringComparison.Ordinal))
        {
            sectionType = "discussion";
            return true;
        }

        if (isSectionHeadingCandidate && normalized is ("conclusion" or "conclusions"))
        {
            sectionType = "conclusion";
            return true;
        }

        if (normalized.Contains("acknowledg", StringComparison.Ordinal) ||
            normalized.Contains("authors would like to", StringComparison.Ordinal))
        {
            sectionType = "acknowledgement";
            return true;
        }

        if (normalized is "references" or "reference" ||
            LooksLikeStrongBibliographyLine(line))
        {
            sectionType = "references";
            return true;
        }

        if (LooksLikePdfMetadataLine(line))
        {
            sectionType = "metadata";
            return true;
        }

        return false;
    }

    private static bool LooksLikeSentenceFragment(string line)
    {
        return line.Contains('.') ||
               line.Contains('?') ||
               line.Contains('!');
    }

    private static bool LooksLikeIntentionalPdfSectionHeading(string line, string normalized)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized is "methods" or "materials and methods" or "results" or "discussion" or
            "conclusion" or "conclusions")
        {
            return true;
        }

        var firstLetter = line.FirstOrDefault(char.IsLetter);
        if (firstLetter == default || !char.IsUpper(firstLetter))
        {
            return false;
        }

        return LooksLikePdfArticleHeadingLine(line);
    }

    private static string NormalizeSectionLabel(string line)
    {
        var trimmed = line.Trim().Trim(':', '.', '-', ' ');
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ShouldIncludeSectionByDefault(string sectionType)
    {
        return sectionType is not "references" and not "acknowledgement" and not "metadata";
    }

    private static bool LooksLikePdfMetadataLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        return normalized.Contains("how to cite this article", StringComparison.Ordinal) ||
               normalized.Contains("address for correspondence", StringComparison.Ordinal) ||
               normalized.Contains("access this article online", StringComparison.Ordinal) ||
               normalized.Contains("quick response code", StringComparison.Ordinal) ||
               normalized.Contains("published by", StringComparison.Ordinal) ||
               normalized.Contains("for reprints contact", StringComparison.Ordinal);
    }

    private static bool LooksLikePdfBoilerplateNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var normalized = NormalizeSectionLabel(line);
        if (normalized.Length == 0)
        {
            return true;
        }

        return normalized.Contains("this is an open access journal", StringComparison.Ordinal) ||
               normalized.Contains("distributed under the terms of the creative commons", StringComparison.Ordinal) ||
               normalized.Contains("attribution noncommercial sharealike", StringComparison.Ordinal) ||
               normalized.Contains("others to remix tweak and build upon the work", StringComparison.Ordinal) ||
               normalized.Contains("as long as appropriate credit is given", StringComparison.Ordinal) ||
               normalized.Contains("licensed under the identical terms", StringComparison.Ordinal) ||
               normalized.Contains("annals of cardiac anaesthesia volume", StringComparison.Ordinal) ||
               normalized.Contains("mishra et al descriptive statistics and normality tests", StringComparison.Ordinal);
    }

    private static bool LooksLikePdfCaptionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        return (trimmed.StartsWith("Figure ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Fig. ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Table ", StringComparison.OrdinalIgnoreCase)) &&
               trimmed.Contains(':');
    }

    private static bool LooksLikePdfViewerWarningLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        return normalized.Contains("this pdf is inaccessible", StringComparison.Ordinal) ||
               normalized.Contains("couldnt download text extraction files", StringComparison.Ordinal) ||
               normalized.Contains("could not download text extraction files", StringComparison.Ordinal) ||
               normalized.Contains("please try again later", StringComparison.Ordinal);
    }

    private static bool ShouldPreservePdfStructuralLine(string line, bool preferPdfChunking)
    {
        if (!preferPdfChunking || string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (LooksLikePdfViewerWarningLine(line))
        {
            return false;
        }

        return TryClassifySectionTransition(line, true, out _) ||
               LooksLikePdfArticleHeadingLine(line) ||
               LooksLikeResumeStructuralLine(line);
    }

    private static bool LooksLikeResumeStructuralLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(line);
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized.Contains("professional summary", StringComparison.Ordinal) ||
               normalized.Contains("rofessional summary", StringComparison.Ordinal) ||
               normalized.Contains("work experience", StringComparison.Ordinal) ||
               normalized is ("skills" or "kills" or "education" or "ducation") ||
               normalized.Contains("backend", StringComparison.Ordinal) ||
               normalized.Contains("frontend", StringComparison.Ordinal) ||
               normalized.Contains("databases", StringComparison.Ordinal) ||
               normalized.Contains("devops", StringComparison.Ordinal) ||
               normalized.Contains("tools", StringComparison.Ordinal) ||
               normalized.Contains("collaboration", StringComparison.Ordinal) ||
               normalized.Contains("productivity", StringComparison.Ordinal) ||
               normalized.Contains("portfolio", StringComparison.Ordinal) ||
               line.Contains('@') ||
               line.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePdfArticleHeadingLine(string line)
    {
        var normalized = NormalizeSectionLabel(line);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized is "abstract" or "introduction" or "keywords" or
            "background" or "objective" or "objectives" or "methods" or
            "results" or "discussion" or "conclusion" or "conclusions" or
            "descriptive statistics" or "measures of frequency" or
            "measures of central tendency" or "measures of dispersion" or
            "common measures" or "computation of measures of central tendency" or
            "computation of measures of dispersion" or "mean" or "median" or "mode" or
            "standard error" or "standard deviation and variance")
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeBibliographyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length < 12)
        {
            return false;
        }

        return trimmed.Contains(" et al.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(" ed.", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("available from:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("doi:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStrongBibliographyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length < 12)
        {
            return false;
        }

        if (trimmed.Contains("available from:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("doi:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeStandaloneCitationFragment(trimmed))
        {
            return true;
        }

        if (StartsWithNumberedReferencePrefix(trimmed))
        {
            return true;
        }

        var hasCitationAuthorMarker = trimmed.Contains(" et al.", StringComparison.OrdinalIgnoreCase);
        var hasPublicationMarker =
            trimmed.Contains(" ed.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("BMJ", StringComparison.Ordinal) ||
            trimmed.Contains("J ", StringComparison.Ordinal) ||
            trimmed.Contains("Int J", StringComparison.Ordinal) ||
            trimmed.Contains("Pediatr", StringComparison.Ordinal) ||
            trimmed.Contains("Wiley", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("CRC", StringComparison.Ordinal);
        var hasYear = ContainsYearLikeToken(trimmed);

        return hasCitationAuthorMarker && hasPublicationMarker && hasYear;
    }

    private static bool LooksLikeStandaloneCitationFragment(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 5 or > 18)
        {
            return false;
        }

        return ContainsYearLikeToken(text) &&
               text.Contains(';') &&
               text.Contains(':') &&
               (text.Contains(" J ", StringComparison.Ordinal) ||
                text.Contains(" Sci ", StringComparison.Ordinal) ||
                text.Contains(" Med ", StringComparison.Ordinal) ||
                text.Contains(" Anaesth ", StringComparison.Ordinal) ||
                text.Contains(" Card ", StringComparison.Ordinal));
    }

    private static bool StartsWithNumberedReferencePrefix(string text)
    {
        var digitCount = 0;
        while (digitCount < text.Length && char.IsDigit(text[digitCount]))
        {
            digitCount++;
        }

        return digitCount is > 0 and <= 3 &&
               digitCount < text.Length &&
               text[digitCount] == '.' &&
               digitCount + 1 < text.Length &&
               char.IsWhiteSpace(text[digitCount + 1]);
    }

    private static bool ContainsYearLikeToken(string text)
    {
        for (var i = 0; i <= text.Length - 4; i++)
        {
            if (!char.IsDigit(text[i]) ||
                !char.IsDigit(text[i + 1]) ||
                !char.IsDigit(text[i + 2]) ||
                !char.IsDigit(text[i + 3]))
            {
                continue;
            }

            var yearText = text.Substring(i, 4);
            if (int.TryParse(yearText, out var year) && year is >= 1800 and <= 2099)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeHeadingLine(string line, bool preferPdfChunking)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.EndsWith(".", StringComparison.Ordinal) ||
            line.EndsWith("?", StringComparison.Ordinal) ||
            line.EndsWith("!", StringComparison.Ordinal))
        {
            return false;
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        if (words.Length <= 5 && line.Length <= 48)
        {
            return true;
        }

        if (preferPdfChunking && words.Length <= 8 && line.Length <= 72)
        {
            var titleCaseWordCount = words.Count(word => word.Length > 0 && char.IsUpper(word[0]));
            return titleCaseWordCount >= Math.Max(2, words.Length - 1);
        }

        return false;
    }

    private static bool LooksLikePdfWindow(string windowTitle)
    {
        return !string.IsNullOrWhiteSpace(windowTitle) &&
               windowTitle.Contains(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeGoogleDocsWindow(string windowTitle)
    {
        return !string.IsNullOrWhiteSpace(windowTitle) &&
               (windowTitle.Contains("Google Docs", StringComparison.OrdinalIgnoreCase) ||
                windowTitle.Contains("docs.google.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeGoogleDocsAccessibilityPrompt(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalized = NormalizeSectionLabel(content);
        return normalized.Contains("to enable screen reader support", StringComparison.Ordinal) &&
               normalized.Contains("keyboard shortcuts", StringComparison.Ordinal);
    }

    private static bool LooksLikePdfDocumentCandidate(string candidateName, string normalizedContent)
    {
        if (candidateName.Contains("PDF Document", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedContent.Length >= 2500 &&
               normalizedContent.Contains("Abstract", StringComparison.OrdinalIgnoreCase) &&
               normalizedContent.Contains("Introduction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePdfWarningCandidate(string candidateName)
    {
        return candidateName.Contains("untagged", StringComparison.OrdinalIgnoreCase) ||
               candidateName.Contains("reading experience may not be optimal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeChromeNoiseLine(string line, string previousLine)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var lowered = line.ToLowerInvariant();
        if (string.Equals(line, previousLine, StringComparison.Ordinal))
        {
            return true;
        }

        if (LooksLikeChatGptActionLine(line) || LooksLikeChatGptShellLine(line))
        {
            return false;
        }

        if (IsConversationSpeakerLabel(line))
        {
            return false;
        }

        if (LooksLikeCopilotShellLine(line) || LooksLikeCopilotActionLine(line))
        {
            return true;
        }

        var wordCount = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount <= 2 && line.Length <= 18)
        {
            return true;
        }

        return false;
    }

    private static bool TryExtractChatGptConversationThread(
        IReadOnlyList<string> lines,
        out List<ConversationBlock> blocks,
        out string conversationContent)
    {
        blocks = new List<ConversationBlock>();
        conversationContent = string.Empty;

        if (!lines.Any(IsLikelyChatGptConversationMarker))
        {
            return false;
        }

        var indexedAnchors = lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(entry => TryMapChatGptAnchorSpeaker(entry.Line, out _))
            .Select(entry =>
            {
                TryMapChatGptAnchorSpeaker(entry.Line, out var speaker);
                return new ChatGptAnchor(speaker, entry.Index);
            })
            .ToArray();

        foreach (var anchor in indexedAnchors)
        {
            var blockLines = ExtractChatGptAnchorSpan(lines, anchor.Index, anchor.Speaker);
            if (blockLines.Count == 0)
            {
                continue;
            }

            blocks.Add(new ConversationBlock(anchor.Speaker, blockLines));
        }

        if (blocks.Count < 2)
        {
            return false;
        }

        var distinctSpeakers = blocks
            .Select(block => block.Speaker)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctSpeakers < 2)
        {
            return false;
        }

        conversationContent = string.Join(
            Environment.NewLine + Environment.NewLine,
            blocks.Select(block => $"{block.Speaker}:{Environment.NewLine}{string.Join(Environment.NewLine, block.MessageLines)}"));
        return !string.IsNullOrWhiteSpace(conversationContent);
    }

    private static List<string> ExtractChatGptAnchorSpan(
        IReadOnlyList<string> lines,
        int markerIndex,
        string speaker)
    {
        var collected = new List<string>();
        var started = false;
        var maxLookback = string.Equals(speaker, "User", StringComparison.Ordinal) ? 24 : 120;

        for (var i = markerIndex - 1; i >= 0 && markerIndex - i <= maxLookback; i--)
        {
            var line = lines[i];
            if (!started)
            {
                if (ShouldSkipBeforeChatGptAnchorContent(speaker, line))
                {
                    continue;
                }

                if (LooksLikeChatGptAnchorBoundaryLine(line))
                {
                    break;
                }

                collected.Add(line);
                started = true;
                continue;
            }

            if (LooksLikeChatGptAnchorBoundaryLine(line))
            {
                break;
            }

            if (string.Equals(speaker, "Assistant", StringComparison.Ordinal) &&
                LooksLikeSkippableAssistantAnchorLine(line))
            {
                continue;
            }

            if (string.Equals(speaker, "User", StringComparison.Ordinal) &&
                LooksLikeDiscardableUserAnchorLine(line))
            {
                break;
            }

            collected.Add(line);
        }

        collected.Reverse();
        return collected;
    }

    private static bool TryMapChatGptAnchorSpeaker(string line, out string speaker)
    {
        speaker = string.Empty;
        if (string.Equals(line, "Copy message", StringComparison.OrdinalIgnoreCase))
        {
            speaker = "User";
            return true;
        }

        if (string.Equals(line, "Copy response", StringComparison.OrdinalIgnoreCase))
        {
            speaker = "Assistant";
            return true;
        }

        return false;
    }

    private static bool ShouldSkipBeforeChatGptAnchorContent(string speaker, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (LooksLikeChatGptShellLine(line) || LooksLikeChatGptActionLine(line))
        {
            return true;
        }

        if (LooksLikeTransientChatGptLine(line))
        {
            return true;
        }

        if (string.Equals(speaker, "Assistant", StringComparison.Ordinal) &&
            LooksLikeSkippableAssistantAnchorLine(line))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeChatGptAnchorBoundaryLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (LooksLikeChatGptShellLine(line) || LooksLikeChatGptActionLine(line))
        {
            return true;
        }

        if (line.StartsWith("Open conversation options", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeSkippableAssistantAnchorLine(string line)
    {
        return LooksLikeReferenceNoiseLine(line) || LooksLikeTransientChatGptLine(line);
    }

    private static bool LooksLikeDiscardableUserAnchorLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (LooksLikeChatGptShellLine(line) || LooksLikeChatGptActionLine(line))
        {
            return true;
        }

        if (LooksLikeTransientChatGptLine(line))
        {
            return true;
        }

        if (line.StartsWith("Open conversation options", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeTransientChatGptLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (line.StartsWith("Thought for ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return line.Length <= 4 && line.All(ch => char.IsDigit(ch) || ch == '/');
    }

    private static bool TryExtractConversationThread(
        IReadOnlyList<string> lines,
        out List<ConversationBlock> blocks,
        out string conversationContent)
    {
        blocks = new List<ConversationBlock>();
        conversationContent = string.Empty;

        ConversationBlock? currentBlock = null;
        foreach (var line in lines)
        {
            if (TryMapSpeakerLabel(line, out var speaker))
            {
                if (currentBlock is not null && currentBlock.MessageLines.Count > 0)
                {
                    blocks.Add(currentBlock);
                }

                currentBlock = new ConversationBlock(speaker, new List<string>());
                continue;
            }

            if (currentBlock is null)
            {
                continue;
            }

            if (LooksLikeReferenceNoiseLine(line))
            {
                continue;
            }

            currentBlock.MessageLines.Add(line);
        }

        if (currentBlock is not null && currentBlock.MessageLines.Count > 0)
        {
            blocks.Add(currentBlock);
        }

        if (blocks.Count < 2)
        {
            return false;
        }

        var distinctSpeakers = blocks
            .Select(block => block.Speaker)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctSpeakers < 2)
        {
            return false;
        }

        conversationContent = string.Join(
            Environment.NewLine + Environment.NewLine,
            blocks.Select(block => $"{block.Speaker}:{Environment.NewLine}{string.Join(Environment.NewLine, block.MessageLines)}"));
        return !string.IsNullOrWhiteSpace(conversationContent);
    }

    private static bool IsConversationSpeakerLabel(string line)
    {
        return TryMapSpeakerLabel(line, out _);
    }

    private static bool TryMapSpeakerLabel(string line, out string speaker)
    {
        speaker = string.Empty;
        var lowered = line.Trim().ToLowerInvariant();
        switch (lowered)
        {
            case "you said":
            case "you":
            case "user":
            case "user said":
                speaker = "User";
                return true;
            case "copilot said":
            case "copilot":
            case "assistant":
            case "assistant said":
            case "ai":
            case "ai said":
                speaker = "Assistant";
                return true;
            default:
                return false;
        }
    }

    private static bool IsLikelyChatGptConversationMarker(string line)
    {
        return string.Equals(line, "Copy message", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(line, "Copy response", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(line, "Previous response", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(line, "Next response", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(line, "ChatGPT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeChatGptActionLine(string line)
    {
        return line switch
        {
            "Copy message" => true,
            "Edit message" => true,
            "Copy response" => true,
            "Good response" => true,
            "Bad response" => true,
            "Share" => true,
            "Switch model" => true,
            "More actions" => true,
            "Sources" => true,
            "Previous response" => true,
            "Next response" => true,
            "Scroll to bottom" => true,
            "Add files and more" => true,
            "Ask anything" => true,
            "Extended" => true,
            "Start Voice" => true,
            _ => false
        };
    }

    private static bool LooksLikeChatGptShellLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (line.StartsWith("Open conversation options for ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lowered = line.ToLowerInvariant();
        if (lowered is "skip to content" or "chat history" or "home" or "close sidebar" or "new chat" or "search chats" or "projects" or "more" or "gpts" or "explore gpts" or "recents" or "plus" or "chatgpt")
        {
            return true;
        }

        if (lowered.Contains("chatgpt can make mistakes", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeReferenceNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var lowered = line.ToLowerInvariant();
        if (lowered is "show all" or "today")
        {
            return true;
        }

        if (LooksLikeChatGptShellLine(line) || LooksLikeChatGptActionLine(line))
        {
            return true;
        }

        if (LooksLikeCopilotShellLine(line) || LooksLikeCopilotActionLine(line))
        {
            return true;
        }

        if (line == "+1" || line == "+2" || line == "+3")
        {
            return true;
        }

        if (lowered is "britannica" or "wikipedia" or "local histories")
        {
            return true;
        }

        if (line.Contains('|') || line.Contains('…'))
        {
            return true;
        }

        var wordCount = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount <= 6 &&
            !line.EndsWith(".", StringComparison.Ordinal) &&
            !line.EndsWith("?", StringComparison.Ordinal) &&
            !line.EndsWith("!", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeCopilotShellLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var lowered = line.Trim().ToLowerInvariant();
        if (lowered is "open sidebar" or "library" or "invite" or "today" or "yesterday" or "show all")
        {
            return true;
        }

        if (lowered is "message copilot" or "talk to copilot" or "edit in a page" or "smart")
        {
            return true;
        }

        if (lowered.Contains("attach files", StringComparison.Ordinal) ||
            lowered.Contains("connect apps", StringComparison.Ordinal) ||
            lowered.Contains("make something with copilot", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeCopilotActionLine(string line)
    {
        var lowered = line.Trim().ToLowerInvariant();
        return lowered switch
        {
            "copy" => true,
            "share" => true,
            "like" => true,
            "dislike" => true,
            "retry" => true,
            "regenerate" => true,
            "sources" => true,
            "speak" => true,
            "stop" => true,
            _ => false
        };
    }

    private static bool LooksLikeMainContentLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 8 || line.Length >= 55;
    }

    private static bool ContainsElement(AutomationElement container, AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        if (Automation.Compare(container, element))
        {
            return true;
        }

        var walker = TreeWalker.RawViewWalker;
        var cursor = element;
        while (cursor is not null)
        {
            if (Automation.Compare(container, cursor))
            {
                return true;
            }

            cursor = walker.GetParent(cursor);
        }

        return false;
    }

    private static string TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return "<unknown>";
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? "<unknown>" : process.ProcessName;
        }
        catch
        {
            return "<unknown>";
        }
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
                return dataObject is not null;
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
            return false;
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

    private static string BuildPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160] + "...";
    }

    private static string SafeName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private sealed record DocumentCandidate(
        string Name,
        string ClassName,
        int DescendantCount,
        int TextNodeCount,
        int ButtonNodeCount,
        bool ContainsFocus,
        bool IsOffscreen,
        int Score,
        string ContentSource,
        string Content,
        string NormalizedContent,
        string ContentFingerprint);

    private sealed record DocumentCandidateCluster(
        string ClusterHash,
        IReadOnlyList<DocumentCandidate> Candidates,
        DocumentCandidate BestCandidate);

    private sealed record TrimmedMainContext(
        string PageType,
        string ExtractionMode,
        string CoreContent,
        int NoiseLineCount,
        int KeptLineCount,
        int ConversationBlockCount,
        IReadOnlyList<MainContextChunk> Chunks);

    private sealed record MainContextChunk(
        int Index,
        string ChunkType,
        string SectionType,
        string Speaker,
        int LineCount,
        int CharacterCount,
        int ConfidenceScore,
        bool IsHeadingLike,
        bool IncludeByDefault,
        string Content);

    private sealed record ConversationBlock(
        string Speaker,
        List<string> MessageLines);

    private sealed record ChatGptAnchor(
        string Speaker,
        int Index);
}

public sealed record WebpageAnalysisResult(
    string Report,
    IReadOnlyList<DocumentNodeLogEntry> TopDocumentCandidatesForLog,
    MainContextCoreLogEntry? MainContextCoreLogEntry);

public sealed record DocumentNodeLogEntry(
    int Rank,
    int Score,
    string Name,
    string ClassName,
    bool ContainsFocus,
    bool IsOffscreen,
    int DescendantCount,
    int TextNodeCount,
    int ButtonNodeCount,
    string ContentSource,
    int ContentLength,
    bool ContentWasTruncated,
    string ContentForLog);

public sealed record MainContextCoreLogEntry(
    string ClusterHash,
    string CandidateName,
    int CandidateScore,
    string PageType,
    string ExtractionMode,
    int NormalizedLength,
    int CoreLength,
    int NoiseLineCount,
    int KeptLineCount,
    int ConversationBlockCount,
    int ChunkCount,
    string CoreContent,
    IReadOnlyList<MainContextChunkLogEntry> Chunks);

public sealed record MainContextChunkLogEntry(
    int ChunkIndex,
    string ChunkType,
    string SectionType,
    string Speaker,
    int LineCount,
    int CharacterCount,
    int ConfidenceScore,
    bool IsHeadingLike,
    bool IncludeByDefault,
    bool ContentWasTruncated,
    string ContentPreview,
    string ContentForLog);
