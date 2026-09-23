using System.Text;
using CodexFileInspector.Contracts;

namespace CodexFileInspector.Searching;

internal static class GrepBlockBuilder
{
    public static IReadOnlyList<GrepMatchBlock> Build(
        IReadOnlyList<GrepSelectedMatch> selectedMatches,
        IReadOnlyDictionary<GrepLineKey, GrepLineData> availableLines,
        int contextLines,
        IReadOnlySet<GrepLineKey> removedContext)
    {
        List<GrepMatchBlock> blocks = [];
        // GroupBy preserves the selected ripgrep file order used by offsets and budget trimming.
        foreach (IGrouping<string, GrepSelectedMatch> fileGroup in selectedMatches
                     .GroupBy(static match => match.Path, StringComparer.Ordinal))
        {
            GrepSelectedMatch[] fileMatches = fileGroup.OrderBy(static match => match.LineNumber).ToArray();
            Dictionary<long, GrepSelectedMatch> matchByLine = fileMatches.ToDictionary(static match => match.LineNumber);
            List<LineInterval> intervals = MergeIntervals(fileMatches, contextLines);
            GrepLineData[] fileLines = availableLines
                .Where(pair => StringComparer.Ordinal.Equals(pair.Key.Path, fileGroup.Key))
                .Where(pair => matchByLine.ContainsKey(pair.Key.LineNumber) || !removedContext.Contains(pair.Key))
                .Select(static pair => pair.Value)
                .OrderBy(static line => line.LineNumber)
                .ToArray();

            foreach (LineInterval interval in intervals)
            {
                SortedDictionary<long, GrepLineData> intervalLines = new();
                foreach (GrepLineData line in fileLines)
                {
                    if (line.LineNumber < interval.Start)
                    {
                        continue;
                    }

                    if (line.LineNumber > interval.End)
                    {
                        break;
                    }

                    intervalLines[line.LineNumber] = line;
                }

                foreach (GrepSelectedMatch match in fileMatches.Where(
                             match => match.LineNumber >= interval.Start && match.LineNumber <= interval.End))
                {
                    intervalLines[match.LineNumber] = new GrepLineData(
                        match.Path,
                        match.LineNumber,
                        match.Excerpt,
                        true,
                        match.OccurrenceCount);
                }

                bool intervalContextTruncated = removedContext.Any(
                    key => StringComparer.Ordinal.Equals(key.Path, fileGroup.Key) &&
                           key.LineNumber >= interval.Start &&
                           key.LineNumber <= interval.End);
                AddContiguousBlocks(
                    blocks,
                    fileGroup.Key,
                    intervalLines.Values,
                    matchByLine,
                    intervalContextTruncated);
            }
        }

        return blocks;
    }

    public static IReadOnlyList<GrepLineKey> ContextRemovalOrder(
        IReadOnlyList<GrepSelectedMatch> selectedMatches,
        IReadOnlyDictionary<GrepLineKey, GrepLineData> availableLines)
    {
        Dictionary<string, long[]> selectedByPath = selectedMatches
            .GroupBy(static match => match.Path, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(match => match.LineNumber).Order().ToArray(),
                StringComparer.Ordinal);

        return availableLines
            .Where(pair => selectedByPath.TryGetValue(pair.Key.Path, out long[]? selectedLines) &&
                           selectedLines.BinarySearch(pair.Key.LineNumber) < 0)
            .Select(pair => new
            {
                pair.Key,
                Distance = DistanceToNearest(pair.Key.LineNumber, selectedByPath[pair.Key.Path]),
            })
            .OrderByDescending(static item => item.Distance)
            .ThenByDescending(static item => item.Key.Path, StablePathComparer.Instance)
            .ThenByDescending(static item => item.Key.LineNumber)
            .Select(static item => item.Key)
            .ToArray();
    }

    private static List<LineInterval> MergeIntervals(
        IReadOnlyList<GrepSelectedMatch> matches,
        int contextLines)
    {
        List<LineInterval> intervals = [];
        foreach (GrepSelectedMatch match in matches)
        {
            long start = Math.Max(1, match.LineNumber - contextLines);
            long end = match.LineNumber + contextLines;
            if (intervals.Count == 0 || start > intervals[^1].End + 1)
            {
                intervals.Add(new LineInterval(start, end));
            }
            else
            {
                intervals[^1] = intervals[^1] with { End = Math.Max(intervals[^1].End, end) };
            }
        }

        return intervals;
    }

    private static void AddContiguousBlocks(
        List<GrepMatchBlock> destination,
        string path,
        IEnumerable<GrepLineData> lines,
        IReadOnlyDictionary<long, GrepSelectedMatch> matchByLine,
        bool contextTruncated)
    {
        List<GrepLineData> contiguous = [];
        foreach (GrepLineData line in lines)
        {
            if (contiguous.Count > 0 && line.LineNumber != contiguous[^1].LineNumber + 1)
            {
                AddBlockIfMatched(destination, path, contiguous, matchByLine, contextTruncated);
                contiguous.Clear();
            }

            contiguous.Add(line);
        }

        AddBlockIfMatched(destination, path, contiguous, matchByLine, contextTruncated);
    }

    private static void AddBlockIfMatched(
        List<GrepMatchBlock> destination,
        string path,
        IReadOnlyList<GrepLineData> lines,
        IReadOnlyDictionary<long, GrepSelectedMatch> matchByLine,
        bool contextTruncated)
    {
        if (lines.Count == 0)
        {
            return;
        }

        GrepMatchLine[] matchLines = lines
            .Where(line => matchByLine.ContainsKey(line.LineNumber))
            .Select(line => matchByLine[line.LineNumber])
            .Select(static match => new GrepMatchLine(match.LineNumber, match.OccurrenceCount))
            .ToArray();
        if (matchLines.Length == 0)
        {
            return;
        }

        string content = string.Join('\n', lines.Select(static line => $"L{line.LineNumber}: {line.Excerpt.Text}"));
        LineTruncation[] truncations = lines
            .Where(static line => line.Excerpt.IsTruncated)
            .Select(static line => new LineTruncation(
                line.LineNumber,
                line.Excerpt.ReturnedUtf8Bytes,
                line.Excerpt.TotalUtf8Bytes))
            .ToArray();
        destination.Add(new GrepMatchBlock(
            path,
            lines[0].LineNumber,
            lines[^1].LineNumber,
            content,
            matchLines,
            truncations,
            contextTruncated));
    }

    private static long DistanceToNearest(long lineNumber, IReadOnlyList<long> selectedLines)
    {
        int index = selectedLines.BinarySearch(lineNumber);
        if (index >= 0)
        {
            return 0;
        }

        index = ~index;
        long before = index > 0 ? lineNumber - selectedLines[index - 1] : long.MaxValue;
        long after = index < selectedLines.Count ? selectedLines[index] - lineNumber : long.MaxValue;
        return Math.Min(before, after);
    }

    private sealed class StablePathComparer : IComparer<string>
    {
        public static StablePathComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(x, y);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(x, y);
        }
    }

    private sealed record LineInterval(long Start, long End);
}

internal static class ReadOnlyListBinarySearchExtensions
{
    public static int BinarySearch(this IReadOnlyList<long> values, long value)
    {
        int low = 0;
        int high = values.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            long candidate = values[middle];
            if (candidate == value)
            {
                return middle;
            }

            if (candidate < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }
}
