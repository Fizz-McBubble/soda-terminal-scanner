using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static partial class ScanBenchmark
{
    private static List<ClickInterval> BuildClickIntervals(IReadOnlyList<ScanEvent> events)
    {
        var intervals = new List<ClickInterval>();
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (item.Kind != "CELL_CLICK" || !CellClickRegex.IsMatch(item.Detail))
            {
                continue;
            }

            var afterScroll = false;
            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind == "ROW_SCROLL_START")
                {
                    afterScroll = true;
                }

                if (events[j].Kind == "CELL_MOVE")
                {
                    intervals.Add(new ClickInterval((events[j].Timestamp - item.Timestamp).TotalMilliseconds, afterScroll));
                    break;
                }
            }
        }

        return intervals;
    }

    private static List<ClickPosition> ParseClickPositions(IReadOnlyList<ScanEvent> events)
    {
        var positions = new List<ClickPosition>();
        foreach (var item in events)
        {
            if (item.Kind != "CELL_CLICK")
            {
                continue;
            }

            var match = SafeBandPositionRegex.Match(item.Detail);
            if (!match.Success)
            {
                continue;
            }

            positions.Add(new ClickPosition(
                ParseInt(match.Groups["logical"].Value),
                ParseInt(match.Groups["visual"].Value),
                ParseInt(match.Groups["top"].Value),
                match.Groups["state"].Value));
        }

        return positions;
    }

    private static bool IsAllowedVisualRow2Click(ClickPosition position, bool overlapMode)
    {
        if (overlapMode && position.VisualRow == 2 && position.LogicalRow == position.VisibleTopLogicalRow + 1)
        {
            return true;
        }

        return position.VisualRow == 2
            && position.LogicalRow == 2
            && position.VisibleTopLogicalRow == 1
            && position.State is "Top" or "TopAndBottom";
    }

    private static List<double> BuildScrollDurations(IReadOnlyList<ScanEvent> events)
    {
        var durations = new List<double>();
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Kind != "ROW_SCROLL_START")
            {
                continue;
            }

            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind is "ROW_SCROLL_DONE" or "ROW_SCROLL_VERIFY")
                {
                    durations.Add((events[j].Timestamp - events[i].Timestamp).TotalMilliseconds);
                    break;
                }
            }
        }

        return durations;
    }

    private static IReadOnlyList<Dictionary<string, string>> ReadCsv(string file)
    {
        if (!File.Exists(file))
        {
            return Array.Empty<Dictionary<string, string>>();
        }

        var lines = File.ReadAllLines(file);
        if (lines.Length < 2)
        {
            return Array.Empty<Dictionary<string, string>>();
        }

        var headers = lines[0].Split(',');
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var values = lines[i].Split(',');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < Math.Min(headers.Length, values.Length); c++)
            {
                row[headers[c]] = values[c];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<double> ReadColumn(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private static IEnumerable<double> ReadOcrMillisecondsPerItem(IReadOnlyList<Dictionary<string, string>> rows)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue("total_ms", out var totalValue)
                || !double.TryParse(totalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalMs)
                || !row.TryGetValue("batch_size", out var batchValue)
                || !double.TryParse(batchValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var batchSize)
                || batchSize <= 0)
            {
                continue;
            }

            yield return totalMs / batchSize;
        }
    }

    private static IEnumerable<double> ReadColumnPerItem(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue(column, out var value)
                || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || !row.TryGetValue("batch_size", out var batchValue)
                || !double.TryParse(batchValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var batchSize)
                || batchSize <= 0)
            {
                continue;
            }

            yield return parsed / batchSize;
        }
    }

    private static DateTime? ParseStartTime(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("Start scan.", StringComparison.Ordinal))
            {
                return ParseLineTime(line);
            }
        }

        return null;
    }

    private static DateTime? ParseEndTime(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var time = ParseLineTime(lines[i]);
            if (time is not null)
            {
                return time;
            }
        }

        return null;
    }

    private static DateTime? ParseLineTime(string line)
    {
        var end = line.IndexOf(']');
        if (end <= 1 || line[0] != '[')
        {
            return null;
        }

        return DateTime.TryParse(line.Substring(1, end - 1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
            ? timestamp
            : null;
    }

    private static string ParseStopReason(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("Scan canceled.", StringComparison.Ordinal)
                || line.Contains("Stop at #", StringComparison.Ordinal)
                || line.Contains("Scan failed:", StringComparison.Ordinal)
                || line.Contains("End:", StringComparison.Ordinal))
            {
                var close = line.IndexOf("] ", StringComparison.Ordinal);
                return close >= 0 ? line[(close + 2)..] : line;
            }
        }

        return "unknown";
    }

    private static StartSettings? ParseStartSettings(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = StartRegex.Match(line);
            if (match.Success)
            {
                return new StartSettings(
                    ParseInt(match.Groups["workers"].Value),
                    ParseInt(match.Groups["batch"].Value),
                    ParseInt(match.Groups["queue"].Value),
                    ParseInt(match.Groups["intra"].Value));
            }
        }

        return null;
    }

    private static int? ParseMaxItems(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = MaxItemsRegex.Match(line);
            if (match.Success)
            {
                return ParseInt(match.Groups["max"].Value);
            }
        }

        return null;
    }

    private static string ParseTraversal(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = TraversalRegex.Match(line);
            if (match.Success)
            {
                return match.Groups["mode"].Value;
            }
        }

        return "unknown";
    }

    private static OverlapTraversalSummary ParseOverlapTraversalSummary(IReadOnlyList<string> lines, IReadOnlyList<ScanEvent> events)
    {
        int? totalRows = null;
        var completed = false;
        foreach (var line in lines)
        {
            if (totalRows is null)
            {
                var traversalMatch = OverlapTraversalRegex.Match(line);
                if (traversalMatch.Success)
                {
                    totalRows = ParseInt(traversalMatch.Groups["total"].Value);
                }
            }

            if (OverlapCompletedRegex.IsMatch(line))
            {
                completed = true;
            }
        }

        var scannedRows = events.Count(x => x.Kind == "OVERLAP_ROW_SCANNED");
        var missingRows = totalRows is null ? 0 : Math.Max(0, totalRows.Value - scannedRows);
        return new OverlapTraversalSummary(totalRows, scannedRows, missingRows, completed && missingRows == 0);
    }

    private static DateTime? ParseCaptureEndTime(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("End:", StringComparison.Ordinal)
                || CellTimingIndexRegex.IsMatch(line))
            {
                return ParseLineTime(line);
            }
        }

        return null;
    }

    private static ExportStats ReadExportStats(string exportFile, IReadOnlySet<int> verifiedIdenticalNeighborIndices)
    {
        if (!File.Exists(exportFile))
        {
            return new ExportStats(null, 0, 0, 0, 0, 0, 0, 0);
        }

        try
        {
            var rules = WikiData.Load().StatRules;
            using var document = JsonDocument.Parse(File.ReadAllText(exportFile));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ExportStats(null, 0, 0, 0, 0, 0, 0, 0);
            }

            var itemCount = 0;
            var fingerprints = new Dictionary<string, int>(StringComparer.Ordinal);
            string? previousFingerprint = null;
            var verifiedAdjacentDuplicates = 0;
            var unverifiedDuplicates = 0;
            var slotOutOfRange = 0;
            var slotMainStatViolation = 0;
            var slotFixedValueViolation = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                itemCount++;
                var fingerprint = ExportFingerprint(item);
                var alreadySeen = fingerprints.ContainsKey(fingerprint);
                if (alreadySeen)
                {
                    var verifiedAdjacent = string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal)
                        && verifiedIdenticalNeighborIndices.Contains(itemCount);
                    if (verifiedAdjacent)
                    {
                        verifiedAdjacentDuplicates++;
                    }
                    else
                    {
                        unverifiedDuplicates++;
                    }
                }

                fingerprints[fingerprint] = fingerprints.TryGetValue(fingerprint, out var count) ? count + 1 : 1;
                previousFingerprint = fingerprint;
                var export = item.Deserialize<DriveDiscExport>(JsonDefaults.Read);
                if (export is not null)
                {
                    var issues = DriveDiscSlotSafety.Validate(export, rules);
                    if (issues.Any(issue => issue.Code == DriveDiscSlotSafety.SlotOutOfRange))
                    {
                        slotOutOfRange++;
                    }

                    if (issues.Any(issue => issue.Code == DriveDiscSlotSafety.SlotMainStatViolation
                        || issue.Code == DriveDiscSlotSafety.MissingMainStat))
                    {
                        slotMainStatViolation++;
                    }

                    if (issues.Any(issue => issue.Code == DriveDiscSlotSafety.SlotFixedValueViolation))
                    {
                        slotFixedValueViolation++;
                    }
                }
            }

            var duplicateGroups = fingerprints.Values.Count(count => count > 1);
            var duplicateItems = fingerprints.Values.Where(count => count > 1).Sum();
            return new ExportStats(
                itemCount,
                duplicateGroups,
                duplicateItems,
                verifiedAdjacentDuplicates,
                unverifiedDuplicates,
                slotOutOfRange,
                slotMainStatViolation,
                slotFixedValueViolation);
        }
        catch
        {
            return new ExportStats(null, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    internal static bool IsCompleteVariableRoiLayout(int visibleRois, int totalRois)
    {
        const int requiredCoreRois = 4;
        return totalRois >= requiredCoreRois
            && visibleRois >= requiredCoreRois
            && visibleRois <= totalRois
            && (visibleRois - requiredCoreRois) % 2 == 0;
    }

    private static string ResolveExportFile(string scanDirectory, string? terminalExportFile)
    {
        if (!string.IsNullOrWhiteSpace(terminalExportFile))
        {
            var terminalCandidate = Path.Combine(scanDirectory, Path.GetFileName(terminalExportFile));
            if (File.Exists(terminalCandidate))
            {
                return terminalCandidate;
            }
        }

        var complete = Path.Combine(scanDirectory, "export.json");
        return File.Exists(complete)
            ? complete
            : Path.Combine(scanDirectory, "export.partial.json");
    }
}
