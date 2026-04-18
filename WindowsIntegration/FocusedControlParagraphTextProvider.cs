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
                    paragraphText = selectionParagraph;
                    sourceMessage = "Paragraph candidate retrieved from focused control text pattern selection.";
                    probe = BuildProbeInfo(depth, element, "text_pattern_selection", paragraphText.Length, selectionRanges.Length, valueLength: null);
                    return true;
                }

                probe = BuildProbeInfo(depth, element, "text_pattern_empty_selection", 0, selectionRanges.Length, valueLength: null);
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
                paragraphText = valueText;
                sourceMessage = "Paragraph candidate retrieved from focused control value.";
                probe = BuildProbeInfo(depth, element, "value_pattern", paragraphText.Length, selectionRangeCount: null, valueLength: valueText.Length);
                return true;
            }

            probe = BuildProbeInfo(depth, element, "value_pattern_empty", 0, selectionRangeCount: null, valueLength: 0);
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
                            if (geometry.DistanceSquared is null ||
                                geometry.DistanceSquared.Value > BrowserPdfMaxPointRangeDistanceSquared)
                            {
                                probes.Add(
                                    $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point_rejected_distance;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate?.Length ?? 0};{geometry.Diagnostics};threshold={BrowserPdfMaxPointRangeDistanceSquared};rejection=range_geometry_too_far_from_point");
                                continue;
                            }

                            if (IsMeaningfulParagraphCandidate(normalizedCandidate))
                            {
                                var distanceSquared = (offset.Dx * offset.Dx) + (offset.Dy * offset.Dy);
                                var probeRecord =
                                    $"depth={depth};class={current.Current.ClassName};controlType={current.Current.ControlType?.ProgrammaticName};path=range_from_point;cursorX={probeX};cursorY={probeY};offsetDx={offset.Dx};offsetDy={offset.Dy};candidateLength={normalizedCandidate!.Length};{geometry.Diagnostics}";
                                probes.Add(probeRecord);
                                candidates.Add(new BrowserPdfProbeCandidate(
                                    normalizedCandidate!,
                                    probeRecord,
                                    depth,
                                    MethodPriority: 2,
                                    DistanceSquared: geometry.DistanceSquared ?? distanceSquared,
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
                ["caretRangeUnavailableCount"] = probes.Count(probe => probe.Contains("path=caret_range_unavailable_managed_api", StringComparison.Ordinal)).ToString(),
                ["selectionRejectedEmptyCount"] = probes.Count(probe => probe.Contains("path=selection_range_rejected_empty_pdf_selection", StringComparison.Ordinal)).ToString(),
                ["rangeFromPointRejectedDistanceCount"] = probes.Count(probe => probe.Contains("path=range_from_point_rejected_distance", StringComparison.Ordinal)).ToString(),
                ["candidates"] = string.Join(" | ", candidates.Select(candidate => candidate.ToDecisionSummary())),
                ["probeTrail"] = string.Join(" | ", probes)
            });

        var bestCandidate = ChooseBestBrowserPdfProbeCandidate(candidates);
        if (bestCandidate is not null)
        {
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
        return candidates
            .OrderBy(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.MethodPriority)
            .ThenBy(candidate => candidate.DistanceSquared)
            .ThenBy(candidate => candidate.OffsetIndex)
            .ThenByDescending(candidate => candidate.Text.Length)
            .FirstOrDefault();
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

        probe = $"depth={depth};class={element.Current.ClassName};controlType={element.Current.ControlType?.ProgrammaticName};path=caret_range_unavailable_managed_api;cursorX={cursorX};cursorY={cursorY}";
        return false;
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
        var bestCandidate = Normalize(TryExpandAndRead(sourceRange, TextUnit.Paragraph)) ??
                            Normalize(TryExpandAndRead(sourceRange, TextUnit.Line));
        if (IsMeaningfulParagraphCandidate(bestCandidate))
        {
            return bestCandidate;
        }

        var candidate = TryReadNearbyUnit(sourceRange, TextUnit.Line, maxOffset: 3);
        if (IsMeaningfulParagraphCandidate(candidate))
        {
            return candidate;
        }

        candidate = TryReadNearbyUnit(sourceRange, TextUnit.Paragraph, maxOffset: 2);
        if (IsMeaningfulParagraphCandidate(candidate))
        {
            return candidate;
        }

        return bestCandidate;
    }

    private static string? TryReadNearbyUnit(TextPatternRange sourceRange, TextUnit unit, int maxOffset)
    {
        string? best = null;
        for (var offset = -maxOffset; offset <= maxOffset; offset++)
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

                if (best is null || text!.Length > best.Length)
                {
                    best = text;
                }
            }
            catch
            {
                // Keep probing nearby offsets.
            }
        }

        return best;
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

        public string ToDecisionSummary()
        {
            return $"method={Method};depth={Depth};methodPriority={MethodPriority};distanceSquared={DistanceSquared};offsetIndex={OffsetIndex};rectCount={RectCount};length={Text.Length};preview={BuildPreview(Text)}";
        }
    }

    private sealed record RangeGeometryDiagnostics(
        int RectCount,
        int? DistanceSquared,
        string Diagnostics);
}
