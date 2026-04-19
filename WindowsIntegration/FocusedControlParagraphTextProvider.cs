using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class FocusedControlParagraphTextProvider : IParagraphTextProvider
{
    private const int AncestorProbeDepth = 8;
    private const int BrowserPdfMaxPointRangeDistanceSquared = 90000;
    private const int BrowserPdfDegenerateSelectionMaxDistanceSquared = 260000;
    private const int BrowserPdfRelaxedPointRangeDistanceSquared = 260000;
    private const int BrowserPdfPreferredParagraphMinLength = 80;
    private const int BrowserPdfMinimumAcceptedParagraphCharacters = 40;
    private const int BrowserPdfMaximumAcceptedParagraphCharacters = 700;
    private const int BrowserPdfLocalLineClusterRadius = 2;
    private const int BrowserPdfMaxIsolatedRelaxedDistanceSquared = 180000;
    private const int BrowserPdfMaxRelaxedOnlyDistanceSquared = 180000;
    private const int ValuePatternParagraphMaximumAcceptedCharacters = 650;
    private const int ValuePatternParagraphMaximumAcceptedLines = 3;
    private const int GenericParagraphMaximumAcceptedCharacters = 2200;
    private const int GenericParagraphMaximumAcceptedLines = 12;

    public Task<TextRetrievalResult> TryGetParagraphTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement is null)
            {
                AppDiagnostics.Warn("paragraph_provider_focused_no_focused_element");
                return Task.FromResult(TextRetrievalResult.Failed("No focused control is available for paragraph retrieval.", TextRetrievalSource.FocusedControl));
            }

            AppDiagnostics.Info("paragraph_provider_focused_started", BuildElementDiagnostics("focused", focusedElement));

            if (ShouldDeferBrowserPdfParagraphToClipboard(focusedElement))
            {
                if (TryReadBrowserPdfParagraphFromCursorPoint(
                        focusedElement,
                        out var browserPdfParagraphText,
                        out var browserPdfSourceMessage,
                        out var browserPdfProbe))
                {
                    var browserPdfSuccessData = BuildElementDiagnostics("focused", focusedElement);
                    browserPdfSuccessData["probeTrail"] = browserPdfProbe;
                    browserPdfSuccessData["sourceMessage"] = browserPdfSourceMessage;
                    browserPdfSuccessData["textLength"] = browserPdfParagraphText.Length.ToString();
                    AppDiagnostics.Info("paragraph_provider_focused_pdf_cursor_point_success", browserPdfSuccessData);
                    return Task.FromResult(
                        TextRetrievalResult.Retrieved(
                            browserPdfParagraphText,
                            TextRetrievalSource.FocusedControl,
                            browserPdfSourceMessage));
                }

                var deferData = BuildElementDiagnostics("focused", focusedElement);
                deferData["reason"] = "browser_pdf_focused_paragraph_can_be_inaccurate";
                AppDiagnostics.Info("paragraph_provider_focused_pdf_deferred_to_clipboard", deferData);
                return Task.FromResult(
                    TextRetrievalResult.Failed(
                        "Browser PDF paragraph via focused-control UI Automation can be inaccurate; trying clipboard fallback.",
                        TextRetrievalSource.FocusedControl));
            }

            var found = TryReadParagraphFromElementOrAncestors(
                focusedElement,
                out var paragraphText,
                out var sourceMessage,
                out var probeTrail);

            if (found &&
                !string.IsNullOrWhiteSpace(paragraphText))
            {
                var successData = BuildElementDiagnostics("focused", focusedElement);
                successData["sourceMessage"] = sourceMessage;
                successData["probeTrail"] = string.Join(" | ", probeTrail);
                successData["textLength"] = paragraphText.Length.ToString();
                AppDiagnostics.Info(
                    "paragraph_provider_focused_success",
                    successData);
                return Task.FromResult(
                    TextRetrievalResult.Retrieved(
                        paragraphText,
                        TextRetrievalSource.FocusedControl,
                        sourceMessage));
            }

            AppDiagnostics.Warn(
                "paragraph_provider_focused_unavailable_patterns",
                new Dictionary<string, string?>
                {
                    ["probeTrail"] = string.Join(" | ", probeTrail)
                });
            return Task.FromResult(
                TextRetrievalResult.Failed(
                    "Focused control does not expose paragraph text through supported UI Automation patterns.",
                    TextRetrievalSource.FocusedControl));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "paragraph_provider_focused_failed",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return Task.FromResult(
                TextRetrievalResult.Failed(
                    $"Focused-control paragraph retrieval failed: {ex.Message}",
                    TextRetrievalSource.FocusedControl));
        }
    }

    private static string? TryReadSelectionParagraph(TextPatternRange range)
    {
        var paragraph = TryExpandAndRead(range, TextUnit.Paragraph);
        if (!string.IsNullOrWhiteSpace(paragraph))
        {
            return paragraph;
        }

        return TryExpandAndRead(range, TextUnit.Line);
    }

    private static string? TryExpandAndRead(TextPatternRange range, TextUnit unit)
    {
        try
        {
            var expanded = range.Clone();
            expanded.ExpandToEnclosingUnit(unit);
            return Normalize(expanded.GetText(-1));
        }
        catch
        {
            return null;
        }
    }

    private static string? Normalize(string? value)
    {
        return value?.Trim('\0', '\r', '\n', ' ', '\t');
    }

    private static bool ShouldRejectParagraphCandidate(
        string text,
        bool fromValuePattern,
        out string rejectionReason,
        out int nonEmptyLineCount)
    {
        var normalized = Normalize(text) ?? string.Empty;
        var lines = normalized
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        nonEmptyLineCount = lines.Length;

        if (normalized.Length == 0)
        {
            rejectionReason = "empty_after_normalization";
            return true;
        }

        if (normalized.Length > GenericParagraphMaximumAcceptedCharacters)
        {
            rejectionReason = "candidate_exceeds_generic_maximum_length";
            return true;
        }

        if (nonEmptyLineCount > GenericParagraphMaximumAcceptedLines &&
            normalized.Length > 1000)
        {
            rejectionReason = "candidate_spans_too_many_lines";
            return true;
        }

        if (fromValuePattern &&
            (normalized.Length > ValuePatternParagraphMaximumAcceptedCharacters ||
             nonEmptyLineCount > ValuePatternParagraphMaximumAcceptedLines))
        {
            rejectionReason = "value_pattern_candidate_scope_too_wide";
            return true;
        }

        rejectionReason = string.Empty;
        return false;
    }

    private static bool TryReadParagraphFromElementOrAncestors(
        AutomationElement startElement,
        out string paragraphText,
        out string sourceMessage,
        out IReadOnlyList<string> probeTrail)
    {
        paragraphText = string.Empty;
        sourceMessage = string.Empty;
        var probes = new List<string>();

        var current = startElement;
        for (var depth = 0; depth <= AncestorProbeDepth && current is not null; depth++)
        {
            if (TryReadParagraphFromElement(current, depth, out paragraphText, out sourceMessage, out var probe))
            {
                probes.Add(probe);
                probeTrail = probes;
                return true;
            }

            probes.Add(probe);
            current = SafeGetParent(current);
        }

        probeTrail = probes;
        return false;
    }

    private static bool TryReadParagraphFromElement(
        AutomationElement element,
        int depth,
        out string paragraphText,
        out string sourceMessage,
        out string probe)
    {
        paragraphText = string.Empty;
        sourceMessage = string.Empty;
        probe = string.Empty;

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
            textPatternObject is TextPattern textPattern)
        {
            var selectionRanges = textPattern.GetSelection();
            if (selectionRanges is not null && selectionRanges.Length > 0)
            {
                var selectionParagraph = string.Join(
                    Environment.NewLine,
                    selectionRanges
                        .Select(TryReadSelectionParagraph)
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Distinct(StringComparer.Ordinal));

                if (!string.IsNullOrWhiteSpace(selectionParagraph))
                {
                    if (!ShouldRejectParagraphCandidate(
                            selectionParagraph,
                            fromValuePattern: false,
                            out var rejectionReason,
                            out var nonEmptyLineCount))
                    {
                        paragraphText = selectionParagraph;
                        sourceMessage = "Paragraph candidate retrieved from focused control text pattern selection.";
                        probe = BuildProbeInfo(depth, element, "text_pattern_selection", paragraphText.Length, selectionRanges.Length, valueLength: null);
                        return true;
                    }

                    probe = $"{BuildProbeInfo(depth, element, "text_pattern_selection_rejected_scope", selectionParagraph.Length, selectionRanges.Length, valueLength: null)};nonEmptyLineCount={nonEmptyLineCount};rejection={rejectionReason}";
                }
                else
                {
                    probe = BuildProbeInfo(depth, element, "text_pattern_empty_selection", 0, selectionRanges.Length, valueLength: null);
                }
            }
            else
            {
                probe = BuildProbeInfo(depth, element, "text_pattern_no_ranges", 0, 0, valueLength: null);
            }
        }
        else
        {
            probe = BuildProbeInfo(depth, element, "text_pattern_unavailable", 0, null, valueLength: null);
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
            valuePatternObject is ValuePattern valuePattern)
        {
            var valueText = Normalize(valuePattern.Current.Value);
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                if (!ShouldRejectParagraphCandidate(
                        valueText,
                        fromValuePattern: true,
                        out var rejectionReason,
                        out var nonEmptyLineCount))
                {
                    paragraphText = valueText;
                    sourceMessage = "Paragraph candidate retrieved from focused control value.";
                    probe = BuildProbeInfo(depth, element, "value_pattern", paragraphText.Length, selectionRangeCount: null, valueLength: valueText.Length);
                    return true;
                }

                probe = $"{BuildProbeInfo(depth, element, "value_pattern_rejected_scope", valueText.Length, selectionRangeCount: null, valueLength: valueText.Length)};nonEmptyLineCount={nonEmptyLineCount};rejection={rejectionReason}";
            }
            else
            {
                probe = BuildProbeInfo(depth, element, "value_pattern_empty", 0, selectionRangeCount: null, valueLength: 0);
            }
        }
        else if (string.IsNullOrWhiteSpace(probe))
        {
            probe = BuildProbeInfo(depth, element, "value_pattern_unavailable", 0, selectionRangeCount: null, valueLength: null);
        }

        return false;
    }

    private static AutomationElement? SafeGetParent(AutomationElement element)
    {
        try
        {
            return TreeWalker.ControlViewWalker.GetParent(element);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string?> BuildElementDiagnostics(string prefix, AutomationElement element)
    {
        var diagnostics = new Dictionary<string, string?>
        {
            [$"{prefix}AutomationId"] = element.Current.AutomationId,
            [$"{prefix}ClassName"] = element.Current.ClassName,
            [$"{prefix}ControlType"] = element.Current.ControlType?.ProgrammaticName,
            [$"{prefix}Name"] = element.Current.Name,
            [$"{prefix}BoundingRectangle"] = FormatRect(element.Current.BoundingRectangle)
        };

        return diagnostics;
    }

    private static string BuildProbeInfo(
        int depth,
        AutomationElement element,
        string path,
        int candidateLength,
        int? selectionRangeCount,
        int? valueLength)
    {
        return string.Join(
            ";",
            new[]
            {
                $"depth={depth}",
                $"class={element.Current.ClassName}",
                $"controlType={element.Current.ControlType?.ProgrammaticName}",
                $"path={path}",
                $"selectionRanges={(selectionRangeCount.HasValue ? selectionRangeCount.Value.ToString() : "na")}",
                $"valueLength={(valueLength.HasValue ? valueLength.Value.ToString() : "na")}",
                $"candidateLength={candidateLength}"
            });
    }

    private static bool ShouldDeferBrowserPdfParagraphToClipboard(AutomationElement focusedElement)
    {
        try
        {
            var processId = focusedElement.Current.ProcessId;
            if (processId == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            if (!string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var foregroundWindow = ClipboardInterop.GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
                return false;
            }

            var windowClass = WindowFocusInterop.GetWindowClassName(foregroundWindow);
            var windowTitle = WindowFocusInterop.GetWindowText(foregroundWindow);
            if (!string.Equals(windowClass, "Chrome_WidgetWin_1", StringComparison.Ordinal))
            {
                return false;
            }

            return windowTitle.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBrowserPdfParagraphFromCursorPoint(
        AutomationElement startElement,
        out string paragraphText,
        out string sourceMessage,
        out string probeTrail)
    {
        paragraphText = string.Empty;
        sourceMessage = string.Empty;
        probeTrail = "not_attempted";

        if (!CursorInterop.TryGetCursorPosition(out var cursorX, out var cursorY))
        {
            probeTrail = "cursor_position_unavailable";
            return false;
        }

        var probes = new List<string>
        {
            $"cursorX={cursorX};cursorY={cursorY}"
        };
        var pointOffsets = new (int Dx, int Dy)[]
        {
            (0, 0),
            (-8, 0),
            (8, 0),
            (0, -8),
            (0, 8),
            (-16, 0),
            (16, 0),
            (0, -16),
            (0, 16)
        };

        var candidates = new List<BrowserPdfProbeCandidate>();
        var current = startElement;
        for (var depth = 0; depth <= AncestorProbeDepth && current is not null; depth++)
        {
            var elementDiagnostics = BuildBrowserPdfElementProbeDiagnostics(current, depth, cursorX, cursorY);
            probes.Add(elementDiagnostics);

            if (TryReadCaretRangeCandidate(
                    current,
                    depth,
                    cursorX,
                    cursorY,
                    out var caretCandidate,
                    out var caretProbe))
            {
                probes.Add(caretProbe);
                candidates.Add(caretCandidate);
            }
            else if (!string.IsNullOrWhiteSpace(caretProbe))
            {
                probes.Add(caretProbe);
            }

            if (current.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                AddSelectionRangeCandidates(textPattern, current, depth, cursorX, cursorY, probes, candidates);

                for (var index = 0; index < pointOffsets.Length; index++)
                {
                    var offset = pointOffsets[index];
                    var probeX = cursorX + offset.Dx;
                    var probeY = cursorY + offset.Dy;
                    if (!IsPointInsideElement(current, probeX, probeY))
                    {
                        probes.Add(
                            $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_skipped_outside_element;cursorX={probeX};cursorY={probeY};elementBounds={FormatRect(current.Current.BoundingRectangle)};candidateLength=0");
                        continue;
                    }

                    try
                    {
                        var range = textPattern.RangeFromPoint(new System.Windows.Point(probeX, probeY));
                        if (range is not null)
                        {
                            var normalizedCandidate = TryReadMeaningfulAroundRange(range);
                            var geometry = BuildRangeGeometry(range, cursorX, cursorY);
                            if (geometry.DistanceSquared is null)
                            {
                                probes.Add(
                                    $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_rejected_unknown_distance;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate?.Length ?? 0};{geometry.Diagnostics};rejection=missing_geometry_distance");
                                continue;
                            }

                            var distanceSquared = geometry.DistanceSquared.Value;
                            if (distanceSquared > BrowserPdfMaxPointRangeDistanceSquared)
                            {
                                if (distanceSquared <= BrowserPdfRelaxedPointRangeDistanceSquared &&
                                    IsMeaningfulParagraphCandidate(normalizedCandidate))
                                {
                                    var relaxedProbe =
                                        $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_relaxed_threshold;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate!.Length};{geometry.Diagnostics};strictThreshold={BrowserPdfMaxPointRangeDistanceSquared};relaxedThreshold={BrowserPdfRelaxedPointRangeDistanceSquared}";
                                    probes.Add(relaxedProbe);
                                    candidates.Add(new BrowserPdfProbeCandidate(
                                        normalizedCandidate!,
                                        relaxedProbe,
                                        depth,
                                        MethodPriority: 5,
                                        DistanceSquared: distanceSquared,
                                        OffsetIndex: index,
                                        RectCount: geometry.RectCount,
                                        Method: "range_from_point_relaxed"));
                                    continue;
                                }

                                probes.Add(
                                    $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_rejected_distance;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate?.Length ?? 0};{geometry.Diagnostics};threshold={BrowserPdfMaxPointRangeDistanceSquared};rejection=range_geometry_too_far_from_point");
                                continue;
                            }

                            if (IsMeaningfulParagraphCandidate(normalizedCandidate))
                            {
                                var probeRecord =
                                    $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate!.Length};{geometry.Diagnostics}";
                                probes.Add(probeRecord);
                                candidates.Add(new BrowserPdfProbeCandidate(
                                    normalizedCandidate!,
                                    probeRecord,
                                    depth,
                                    MethodPriority: 2,
                                    DistanceSquared: distanceSquared,
                                    OffsetIndex: index,
                                    RectCount: geometry.RectCount,
                                    Method: "range_from_point"));
                                continue;
                            }

                            probes.Add(
                                $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_non_text;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate?.Length ?? 0};{geometry.Diagnostics};rejection=not_meaningful_text");
                            continue;
                        }

                        probes.Add(
                            $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_null;cursorX={probeX};cursorY={probeY};candidateLength=0");
                    }
                    catch
                    {
                        probes.Add(
                            $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_exception;cursorX={probeX};cursorY={probeY};candidateLength=0");
                    }
                }
            }
            else
            {
                probes.Add(
                    $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=text_pattern_unavailable;cursorX={cursorX};cursorY={cursorY};candidateLength=0");
            }

            current = SafeGetParent(current);
        }

        AppDiagnostics.Info(
            "paragraph_provider_focused_pdf_geometry_decision_trace",
            new Dictionary<string, string?>
            {
                ["candidateCount"] = candidates.Count.ToString(),
                ["distinctCandidateTextCount"] = candidates.Select(candidate => candidate.Text).Distinct(StringComparer.Ordinal).Count().ToString(),
                ["candidateMinDistanceSquared"] = candidates.Count == 0 ? "na" : candidates.Min(candidate => candidate.DistanceSquared).ToString(),
                ["candidateRelaxedOnlySet"] = candidates.Count == 0
                    ? "False"
                    : candidates.All(candidate => candidate.Method.IndexOf("relaxed", StringComparison.OrdinalIgnoreCase) >= 0).ToString(),
                ["caretHintTextPatternUnavailableCount"] = probes.Count(probe => probe.Contains("path=caret_hint_text_pattern_unavailable", StringComparison.Ordinal)).ToString(),
                ["caretHintSelectionRangesEmptyCount"] = probes.Count(probe => probe.Contains("path=caret_hint_selection_ranges_empty", StringComparison.Ordinal)).ToString(),
                ["caretHintExceptionCount"] = probes.Count(probe => probe.Contains("path=caret_hint_exception", StringComparison.Ordinal)).ToString(),
                ["caretHintAcceptedCount"] = probes.Count(probe => probe.Contains("path=caret_hint;", StringComparison.Ordinal)).ToString(),
                ["caretHintRejectedDistanceCount"] = probes.Count(probe => probe.Contains("path=caret_hint_rejected_distance", StringComparison.Ordinal)).ToString(),
                ["selectionRejectedEmptyCount"] = probes.Count(probe => probe.Contains("path=selection_range_rejected_empty_pdf_selection", StringComparison.Ordinal)).ToString(),
                ["rangeFromPointRelaxedAcceptedCount"] = probes.Count(probe => probe.Contains("path=range_from_point_relaxed_threshold", StringComparison.Ordinal)).ToString(),
                ["rangeFromPointRejectedDistanceCount"] = probes.Count(probe => probe.Contains("path=range_from_point_rejected_distance", StringComparison.Ordinal)).ToString(),
                ["candidates"] = string.Join(" | ", candidates.Select(candidate => candidate.ToDecisionSummary())),
                ["probeTrail"] = string.Join(" | ", probes)
            });

        var bestCandidate = ChooseBestBrowserPdfProbeCandidate(candidates);
        if (bestCandidate is not null)
        {
            if (ShouldRejectBrowserPdfFragmentCandidate(bestCandidate, candidates))
            {
                probes.Add(
                    $"selectedProbe={bestCandidate.Probe};selectedMethod={bestCandidate.Method};selectedDepth={bestCandidate.Depth};selectedMethodPriority={bestCandidate.MethodPriority};selectedQualityScore={bestCandidate.QualityScore};selectedDistanceSquared={bestCandidate.DistanceSquared};selectedOffsetIndex={bestCandidate.OffsetIndex};selectedRectCount={bestCandidate.RectCount};selectedLength={bestCandidate.Text.Length};selectedPreview={BuildPreview(bestCandidate.Text)};rejection=browser_pdf_candidate_rejected;minimumAcceptedLength={BrowserPdfMinimumAcceptedParagraphCharacters};maxIsolatedRelaxedDistanceSquared={BrowserPdfMaxIsolatedRelaxedDistanceSquared}");
                probeTrail = string.Join(" | ", probes);
                return false;
            }

            paragraphText = bestCandidate.Text;
            sourceMessage = $"Paragraph candidate retrieved from browser PDF {bestCandidate.Method}.";
            probes.Add(
                $"selectedProbe={bestCandidate.Probe};selectedMethod={bestCandidate.Method};selectedDepth={bestCandidate.Depth};selectedMethodPriority={bestCandidate.MethodPriority};selectedDistanceSquared={bestCandidate.DistanceSquared};selectedOffsetIndex={bestCandidate.OffsetIndex};selectedRectCount={bestCandidate.RectCount};selectedLength={bestCandidate.Text.Length};selectedPreview={BuildPreview(bestCandidate.Text)}");
            probeTrail = string.Join(" | ", probes);
            return true;
        }

        probeTrail = probes.Count == 0 ? "no_candidates" : string.Join(" | ", probes);
        return false;
    }

    private static BrowserPdfProbeCandidate? ChooseBestBrowserPdfProbeCandidate(
        IEnumerable<BrowserPdfProbeCandidate> candidates)
    {
        var candidateList = candidates.ToList();
        if (candidateList.Count == 0)
        {
            return null;
        }

        var hitCountByText = candidateList
            .GroupBy(candidate => candidate.Text, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return candidateList
            .OrderBy(candidate => candidate.MethodPriority)
            .ThenByDescending(candidate => hitCountByText.TryGetValue(candidate.Text, out var hitCount) ? hitCount : 1)
            .ThenByDescending(candidate => candidate.QualityScore)
            .ThenBy(candidate => candidate.DistanceSquared)
            .ThenBy(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.OffsetIndex)
            .ThenByDescending(candidate => candidate.Text.Length)
            .FirstOrDefault();
    }

    private static int ComputeBrowserPdfCandidateQuality(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return int.MinValue;
        }

        var trimmed = text.Trim();
        var score = Math.Min(trimmed.Length, 400);

        if (trimmed.Length >= BrowserPdfPreferredParagraphMinLength)
        {
            score += 500;
        }

        if (trimmed.Length > BrowserPdfMaximumAcceptedParagraphCharacters)
        {
            score -= 900;
        }

        if (trimmed.IndexOf(' ', StringComparison.Ordinal) >= 0)
        {
            score += 40;
        }

        if (trimmed.IndexOfAny(new[] { '.', '!', '?', ';' }) >= 0)
        {
            score += 120;
        }

        if (LooksLikeHeaderFragment(trimmed))
        {
            score -= 450;
        }

        return score;
    }

    private static bool LooksLikeHeaderFragment(string text)
    {
        if (text.Length > 120)
        {
            return false;
        }

        if (text.EndsWith(":", StringComparison.Ordinal))
        {
            return true;
        }

        // Common browser-PDF header shapes: short title-cased fragments without sentence punctuation.
        var hasSentencePunctuation = text.IndexOfAny(new[] { '.', '!', '?', ';' }) >= 0;
        var hasManyWords = text.Count(ch => ch == ' ') >= 4;
        return !hasSentencePunctuation && !hasManyWords;
    }

    private static bool ShouldRejectBrowserPdfFragmentCandidate(
        BrowserPdfProbeCandidate candidate,
        IReadOnlyList<BrowserPdfProbeCandidate> allCandidates)
    {
        var textHitCount = allCandidates.Count(item => string.Equals(item.Text, candidate.Text, StringComparison.Ordinal));
        var hasRelaxedOnlyCandidates = allCandidates.Count > 0 &&
                                       allCandidates.All(item => item.Method.IndexOf("relaxed", StringComparison.OrdinalIgnoreCase) >= 0);
        var minDistanceSquared = allCandidates.Count == 0
            ? int.MaxValue
            : allCandidates.Min(item => item.DistanceSquared);

        // A single relaxed far-distance hit is usually drift from the caret paragraph.
        if (candidate.Method.IndexOf("relaxed", StringComparison.OrdinalIgnoreCase) >= 0 &&
            candidate.DistanceSquared > BrowserPdfMaxIsolatedRelaxedDistanceSquared &&
            textHitCount <= 1)
        {
            return true;
        }

        // If all candidates are relaxed and all of them are far from cursor, they are usually stale/wrong blocks.
        if (candidate.Method.IndexOf("relaxed", StringComparison.OrdinalIgnoreCase) >= 0 &&
            hasRelaxedOnlyCandidates &&
            minDistanceSquared > BrowserPdfMaxRelaxedOnlyDistanceSquared)
        {
            return true;
        }

        if (candidate.Text.Length >= BrowserPdfMinimumAcceptedParagraphCharacters)
        {
            return candidate.Text.Length > BrowserPdfMaximumAcceptedParagraphCharacters;
        }

        return LooksLikeHeaderFragment(candidate.Text);
    }

    private static string BuildBrowserPdfElementProbeDiagnostics(
        AutomationElement element,
        int depth,
        int cursorX,
        int cursorY)
    {
        var bounds = element.Current.BoundingRectangle;
        return string.Join(
            ";",
            new[]
            {
                $"depth={depth}",
                $"class={element.Current.ClassName}",
                $"controlType={element.Current.ControlType?.ProgrammaticName}",
                $"path=element_probe",
                $"elementBounds={FormatRect(bounds)}",
                $"cursorInsideElement={IsPointInsideRect(bounds, cursorX, cursorY)}"
            });
    }

    private static bool IsPointInsideElement(AutomationElement element, int x, int y)
    {
        return IsPointInsideRect(element.Current.BoundingRectangle, x, y);
    }

    private static bool IsPointInsideRect(System.Windows.Rect rect, int x, int y)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        return x >= rect.Left &&
               x <= rect.Right &&
               y >= rect.Top &&
               y <= rect.Bottom;
    }

    private static RangeGeometryDiagnostics BuildRangeGeometry(TextPatternRange range, int cursorX, int cursorY)
    {
        try
        {
            var rectangles = range.GetBoundingRectangles();
            if (rectangles is null || rectangles.Length == 0)
            {
                return new RangeGeometryDiagnostics(
                    RectCount: 0,
                    DistanceSquared: null,
                    Diagnostics: "rectCount=0;rectSummary=none;distanceSquared=na");
            }

            var rectCount = rectangles.Length;
            double? bestDistanceSquared = null;
            string? nearestRect = null;
            var summaries = new List<string>();

            for (var index = 0; index < rectCount; index++)
            {
                var rect = rectangles[index];
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                var centerX = rect.Left + (rect.Width / 2d);
                var centerY = rect.Top + (rect.Height / 2d);
                var deltaX = centerX - cursorX;
                var deltaY = centerY - cursorY;
                var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                var rectSummary = FormatRect(rect);

                if (summaries.Count < 3)
                {
                    summaries.Add($"r{index}={rectSummary}");
                }

                if (!bestDistanceSquared.HasValue || distanceSquared < bestDistanceSquared.Value)
                {
                    bestDistanceSquared = distanceSquared;
                    nearestRect = rectSummary;
                }
            }

            return new RangeGeometryDiagnostics(
                RectCount: rectCount,
                DistanceSquared: bestDistanceSquared.HasValue ? ClampDistanceSquared(bestDistanceSquared.Value) : null,
                Diagnostics: $"rectCount={rectCount};nearestRect={nearestRect ?? "none"};rectSummary={string.Join(",", summaries)};distanceSquared={(bestDistanceSquared.HasValue ? ClampDistanceSquared(bestDistanceSquared.Value).ToString() : "na")}");
        }
        catch (Exception ex)
        {
            return new RangeGeometryDiagnostics(
                RectCount: 0,
                DistanceSquared: null,
                Diagnostics: $"rectCount=0;rectSummary=exception;distanceSquared=na;geometryError={SanitizeLogValue(ex.Message)}");
        }
    }

    private static int ClampDistanceSquared(double value)
    {
        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value <= 0)
        {
            return 0;
        }

        return (int)Math.Round(value);
    }

    private static string FormatRect(System.Windows.Rect rect)
    {
        if (rect.IsEmpty)
        {
            return "empty";
        }

        return FormatRect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static string FormatRect(double left, double top, double width, double height)
    {
        return FormattableString.Invariant(
            $"x={left:0.##},y={top:0.##},w={width:0.##},h={height:0.##}");
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
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static string SanitizeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static bool TryReadCaretRangeCandidate(
        AutomationElement element,
        int depth,
        int cursorX,
        int cursorY,
        out BrowserPdfProbeCandidate candidate,
        out string probe)
    {
        candidate = BrowserPdfProbeCandidate.Empty;
        probe = string.Empty;

        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) ||
                textPatternObject is not TextPattern textPattern)
            {
                probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_hint_text_pattern_unavailable;cursorX={cursorX};cursorY={cursorY}";
                return false;
            }

            var selectionRanges = textPattern.GetSelection();
            if (selectionRanges is null || selectionRanges.Length == 0)
            {
                probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_hint_selection_ranges_empty;cursorX={cursorX};cursorY={cursorY}";
                return false;
            }

            var range = selectionRanges[0];
            var selectedText = Normalize(range.GetText(-1));
            var candidateText = TryReadMeaningfulAroundRange(range);
            var geometry = BuildRangeGeometry(range, cursorX, cursorY);
            if (!IsMeaningfulParagraphCandidate(candidateText))
            {
                probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_hint_non_text;cursorX={cursorX};cursorY={cursorY};selectionLength={selectedText?.Length ?? 0};candidateLength={candidateText?.Length ?? 0};{geometry.Diagnostics};rejection=not_meaningful_text";
                return false;
            }

            var distanceSquared = geometry.DistanceSquared ?? int.MaxValue;
            if (distanceSquared > BrowserPdfDegenerateSelectionMaxDistanceSquared)
            {
                probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_hint_rejected_distance;cursorX={cursorX};cursorY={cursorY};selectionLength={selectedText?.Length ?? 0};candidateLength={candidateText!.Length};{geometry.Diagnostics};threshold={BrowserPdfDegenerateSelectionMaxDistanceSquared};rejection=degenerate_selection_range_too_far";
                return false;
            }

            var method = (selectedText?.Length ?? 0) == 0
                ? "degenerate_selection_caret_hint"
                : "selection_range_non_empty";
            probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_hint;cursorX={cursorX};cursorY={cursorY};selectionLength={selectedText?.Length ?? 0};candidateLength={candidateText!.Length};{geometry.Diagnostics};method={method}";
            candidate = new BrowserPdfProbeCandidate(
                candidateText!,
                probe,
                depth,
                MethodPriority: method == "degenerate_selection_caret_hint" ? 0 : 1,
                DistanceSquared: distanceSquared,
                OffsetIndex: 0,
                RectCount: geometry.RectCount,
                Method: method);
            return true;
        }
        catch (Exception ex)
        {
            probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_hint_exception;cursorX={cursorX};cursorY={cursorY};message={SanitizeLogValue(ex.Message)}";
            return false;
        }
    }

    private static void AddSelectionRangeCandidates(
        TextPattern textPattern,
        AutomationElement element,
        int depth,
        int cursorX,
        int cursorY,
        ICollection<string> probes,
        ICollection<BrowserPdfProbeCandidate> candidates)
    {
        try
        {
            var selectionRanges = textPattern.GetSelection();
            if (selectionRanges is null || selectionRanges.Length == 0)
            {
                probes.Add($"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=selection_ranges_empty");
                return;
            }

            for (var index = 0; index < selectionRanges.Length; index++)
            {
                var range = selectionRanges[index];
                var selectedText = Normalize(range.GetText(-1));
                var candidateText = TryReadMeaningfulAroundRange(range);
                var geometry = BuildRangeGeometry(range, cursorX, cursorY);
                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    probes.Add($"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=selection_range_rejected_empty_pdf_selection;selectionIndex={index};selectionLength=0;candidateLength={candidateText?.Length ?? 0};{geometry.Diagnostics};rejection=browser_pdf_empty_selection_is_not_caret_reliable");
                    continue;
                }

                if (!IsMeaningfulParagraphCandidate(candidateText))
                {
                    probes.Add($"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=selection_range_non_text;selectionIndex={index};selectionLength={selectedText?.Length ?? 0};candidateLength={candidateText?.Length ?? 0};{geometry.Diagnostics};rejection=not_meaningful_text");
                    continue;
                }

                var probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=selection_range;selectionIndex={index};selectionLength={selectedText?.Length ?? 0};candidateLength={candidateText!.Length};{geometry.Diagnostics}";
                probes.Add(probe);
                candidates.Add(new BrowserPdfProbeCandidate(
                    candidateText!,
                    probe,
                    depth,
                    MethodPriority: 1,
                    DistanceSquared: geometry.DistanceSquared ?? int.MaxValue,
                    OffsetIndex: index,
                    RectCount: geometry.RectCount,
                    Method: "selection_range"));
            }
        }
        catch (Exception ex)
        {
            probes.Add($"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=selection_ranges_exception;message={SanitizeLogValue(ex.Message)}");
        }
    }

    private static bool IsMeaningfulParagraphCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 5)
        {
            return false;
        }

        var hasLetterOrDigit = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                hasLetterOrDigit = true;
                break;
            }
        }

        if (!hasLetterOrDigit)
        {
            return false;
        }

        return !trimmed.All(ch => ch == '\uFFFC' || char.IsWhiteSpace(ch));
    }

    private static string? TryReadMeaningfulAroundRange(TextPatternRange sourceRange)
    {
        var centerLineCandidate = Normalize(TryExpandAndRead(sourceRange, TextUnit.Line));
        var localLineCluster = TryReadLocalLineCluster(sourceRange, BrowserPdfLocalLineClusterRadius);
        if (IsMeaningfulParagraphCandidate(localLineCluster))
        {
            return localLineCluster;
        }

        var paragraphCandidate = Normalize(TryExpandAndRead(sourceRange, TextUnit.Paragraph));
        if (IsMeaningfulParagraphCandidate(paragraphCandidate) &&
            paragraphCandidate!.Length <= BrowserPdfMaximumAcceptedParagraphCharacters)
        {
            return paragraphCandidate;
        }

        if (IsMeaningfulParagraphCandidate(centerLineCandidate))
        {
            return centerLineCandidate;
        }

        var candidate = TryReadNearbyUnit(sourceRange, TextUnit.Line, maxOffset: 3);
        if (IsMeaningfulParagraphCandidate(candidate))
        {
            return candidate;
        }

        candidate = TryReadNearbyUnit(sourceRange, TextUnit.Paragraph, maxOffset: 2);
        if (IsMeaningfulParagraphCandidate(candidate) &&
            candidate!.Length <= BrowserPdfMaximumAcceptedParagraphCharacters)
        {
            return candidate;
        }

        return paragraphCandidate;
    }

    private static string? TryReadLocalLineCluster(TextPatternRange sourceRange, int radius)
    {
        var linesByOffset = new Dictionary<int, string>();
        for (var offset = -radius; offset <= radius; offset++)
        {
            try
            {
                var moved = sourceRange.Clone();
                if (offset != 0)
                {
                    moved.Move(TextUnit.Line, offset);
                }

                moved.ExpandToEnclosingUnit(TextUnit.Line);
                var text = Normalize(moved.GetText(-1));
                if (!IsMeaningfulParagraphCandidate(text))
                {
                    continue;
                }

                linesByOffset[offset] = text!;
            }
            catch
            {
                // Keep probing nearby offsets.
            }
        }

        if (!linesByOffset.TryGetValue(0, out var centerLine) ||
            !IsMeaningfulParagraphCandidate(centerLine))
        {
            return null;
        }

        var selectedOffsets = new HashSet<int> { 0 };

        var cursor = -1;
        while (linesByOffset.ContainsKey(cursor))
        {
            selectedOffsets.Add(cursor);
            cursor--;
        }

        cursor = 1;
        while (linesByOffset.ContainsKey(cursor))
        {
            selectedOffsets.Add(cursor);
            cursor++;
        }

        var orderedLines = selectedOffsets
            .OrderBy(offset => offset)
            .Select(offset => linesByOffset[offset])
            .ToList();
        if (orderedLines.Count == 0)
        {
            return null;
        }

        var merged = string.Join(" ", orderedLines.Where(text => !string.IsNullOrWhiteSpace(text)));
        merged = Normalize(merged);
        if (string.IsNullOrWhiteSpace(merged))
        {
            return null;
        }

        if (merged.Length > BrowserPdfMaximumAcceptedParagraphCharacters)
        {
            return centerLine.Length <= BrowserPdfMaximumAcceptedParagraphCharacters
                ? centerLine
                : centerLine[..BrowserPdfMaximumAcceptedParagraphCharacters];
        }

        return merged;
    }

    private static string? TryReadNearbyUnit(TextPatternRange sourceRange, TextUnit unit, int maxOffset)
    {
        foreach (var offset in EnumerateOffsetsNearestFirst(maxOffset))
        {
            try
            {
                var moved = sourceRange.Clone();
                if (offset != 0)
                {
                    moved.Move(unit, offset);
                }

                moved.ExpandToEnclosingUnit(unit);
                var text = Normalize(moved.GetText(-1));
                if (!IsMeaningfulParagraphCandidate(text))
                {
                    continue;
                }

                return text;
            }
            catch
            {
                // Keep probing nearby offsets.
            }
        }

        return null;
    }

    private static IEnumerable<int> EnumerateOffsetsNearestFirst(int maxOffset)
    {
        yield return 0;
        for (var delta = 1; delta <= maxOffset; delta++)
        {
            yield return -delta;
            yield return delta;
        }
    }

    private sealed record BrowserPdfProbeCandidate(
        string Text,
        string Probe,
        int Depth,
        int MethodPriority,
        int DistanceSquared,
        int OffsetIndex,
        int RectCount,
        string Method)
    {
        public static BrowserPdfProbeCandidate Empty { get; } = new(
            string.Empty,
            string.Empty,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            0,
            "none");

        public int QualityScore { get; } = ComputeBrowserPdfCandidateQuality(Text);

        public string ToDecisionSummary()
        {
            return $"method={Method};depth={Depth};methodPriority={MethodPriority};qualityScore={QualityScore};distanceSquared={DistanceSquared};offsetIndex={OffsetIndex};rectCount={RectCount};length={Text.Length};preview={BuildPreview(Text)}";
        }
    }

    private sealed record RangeGeometryDiagnostics(
        int RectCount,
        int? DistanceSquared,
        string Diagnostics);
}
