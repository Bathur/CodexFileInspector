using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;

namespace CodexFileInspector.DirectoryListing;

internal sealed class ListDirectoryService(
    IFileSystemPlatform fileSystem,
    int scanMaximum = ToolBudgets.ListDirectoryScanMaximum)
{
    private static readonly EntrySnapshotComparer SnapshotComparer = new();
    private static readonly ReverseEntrySnapshotComparer ReverseSnapshotComparer = new();

    public ListDirectoryOutput List(
        ListDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        OutputBudget.EnsurePathFits(request.Path);

        FileAttributes attributes = fileSystem.GetAttributes(request.Path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.PathNotDirectory,
                "list_directory requires a directory path.",
                field: "path",
                path: request.Path);
        }

        DirectoryChangeStamp before = fileSystem.GetDirectoryChangeStamp(request.Path);
        int retainCount = checked(request.ResultOffset + request.MaxEntries + 1);
        PriorityQueue<FileSystemEntrySnapshot, FileSystemEntrySnapshot> retained = new(ReverseSnapshotComparer);
        WarningCollector warnings = new();
        int scanned = 0;
        bool incomplete = false;

        try
        {
            foreach (FileSystemEntrySnapshot snapshot in fileSystem.EnumerateDirectory(request.Path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scanned >= scanMaximum)
                {
                    incomplete = true;
                    warnings.Add(
                        ToolErrorCodes.ScanLimitExceeded,
                        "Directory enumeration reached the fixed scan limit before completion.",
                        request.Path);
                    break;
                }

                scanned++;
                DirectoryEntry entry = new(snapshot.Name, snapshot.Path, snapshot.Kind);
                if (!OutputBudget.EntryFits(entry, out _))
                {
                    incomplete = true;
                    warnings.Add(
                        ToolErrorCodes.OutputRecordTooLarge,
                        $"A directory entry exceeded the {ToolBudgets.PathRecordBytes}-byte record limit.",
                        null);
                    continue;
                }

                if (retained.Count < retainCount)
                {
                    retained.Enqueue(snapshot, snapshot);
                }
                else if (SnapshotComparer.Compare(snapshot, retained.Peek()) < 0)
                {
                    retained.Dequeue();
                    retained.Enqueue(snapshot, snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (ToolExceptionMapper.IsExpected(exception))
        {
            if (scanned == 0)
            {
                throw;
            }

            incomplete = true;
            ToolError error = ToolExceptionMapper.Map(exception, request.Path);
            warnings.Add(error.Code, error.Message, error.Path);
        }

        try
        {
            DirectoryChangeStamp after = fileSystem.GetDirectoryChangeStamp(request.Path);
            if (before != after)
            {
                incomplete = true;
                warnings.Add(
                    ToolWarningCodes.DirectoryChanged,
                    "The directory changed during enumeration; totals and continuation are unavailable.",
                    request.Path);
            }
        }
        catch (Exception exception) when (ToolExceptionMapper.IsExpected(exception))
        {
            incomplete = true;
            warnings.Add(
                ToolWarningCodes.DirectoryChanged,
                "The directory could not be revalidated after enumeration; totals and continuation are unavailable.",
                request.Path);
        }

        List<DirectoryEntry> page = retained.UnorderedItems
            .Select(static item => item.Element)
            .OrderBy(static item => item, SnapshotComparer)
            .Skip(request.ResultOffset)
            .Take(request.MaxEntries)
            .Select(static item => new DirectoryEntry(item.Name, item.Path, item.Kind))
            .ToList();

        if (incomplete)
        {
            return FitPartial(request.Path, page, warnings);
        }

        if (request.ResultOffset > 0 && request.ResultOffset >= scanned)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.ResultOffsetPastEnd,
                "result_offset is past the complete directory entry set.",
                field: "result_offset",
                path: request.Path,
                actual: request.ResultOffset,
                total: scanned);
        }

        bool hasMore = scanned > request.ResultOffset + page.Count;
        string? truncatedBy = hasMore ? "entry_limit" : null;
        ListDirectoryOutput output = CreateSuccess(
            request.Path,
            request.ResultOffset,
            page,
            scanned,
            hasMore,
            truncatedBy);

        while (page.Count > 1 && !Fits(output))
        {
            page.RemoveAt(page.Count - 1);
            hasMore = true;
            truncatedBy = "byte_budget";
            output = CreateSuccess(
                request.Path,
                request.ResultOffset,
                page,
                scanned,
                hasMore,
                truncatedBy);
        }

        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The directory result cannot fit within the canonical result budget.",
                path: request.Path,
                limit: ToolBudgets.CanonicalResultBytes,
                actual: ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ListDirectoryOutput));
        }

        return output;
    }

    private static ListDirectoryOutput FitPartial(
        string path,
        List<DirectoryEntry> entries,
        WarningCollector warningCollector)
    {
        List<ToolWarning> warnings = warningCollector.Items.ToList();
        int warningsOmitted = warningCollector.Omitted;
        string? truncatedBy = null;
        ListDirectoryOutput output = CreatePartial(path, entries, warnings, warningsOmitted, truncatedBy);

        while (entries.Count > 0 && !Fits(output))
        {
            entries.RemoveAt(entries.Count - 1);
            truncatedBy = "byte_budget";
            output = CreatePartial(path, entries, warnings, warningsOmitted, truncatedBy);
        }

        while (warnings.Count > 1 && !Fits(output))
        {
            warnings.RemoveAt(warnings.Count - 1);
            warningsOmitted++;
            output = CreatePartial(path, entries, warnings, warningsOmitted, truncatedBy);
        }

        if (!Fits(output) && warnings.Any(static warning => warning.Path is not null))
        {
            warnings = warnings.Select(static warning => warning with { Path = null }).ToList();
            output = CreatePartial(path, entries, warnings, warningsOmitted, truncatedBy);
        }

        if (!Fits(output) && warnings.Count == 1)
        {
            ToolWarning warning = warnings[0];
            int high = System.Text.Encoding.UTF8.GetByteCount(warning.Message);
            int low = 0;
            ListDirectoryOutput? best = null;
            ToolWarning bestWarning = warning with { Message = string.Empty, Path = null };
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                string message = Utf8Budget.Truncate(warning.Message, middle, out _);
                ToolWarning candidateWarning = warning with { Message = message, Path = null };
                ListDirectoryOutput candidate = CreatePartial(
                    path,
                    entries,
                    [candidateWarning],
                    warningsOmitted,
                    truncatedBy);
                if (Fits(candidate))
                {
                    best = candidate;
                    bestWarning = candidateWarning;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (best is not null)
            {
                warnings = [bestWarning];
                output = best;
            }
        }

        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The partial directory result cannot fit within the canonical result budget.",
                path: path,
                limit: ToolBudgets.CanonicalResultBytes);
        }

        return output;
    }

    private static ListDirectoryOutput CreateSuccess(
        string path,
        int resultOffset,
        IReadOnlyList<DirectoryEntry> entries,
        int totalEntries,
        bool hasMore,
        string? truncatedBy) => new()
    {
        Status = ToolStatus.Success,
        Path = path,
        Entries = entries,
        ReturnedEntries = entries.Count,
        TotalEntries = totalEntries,
        HasMore = hasMore,
        NextResultOffset = hasMore ? resultOffset + entries.Count : null,
        TruncatedBy = truncatedBy,
    };

    private static ListDirectoryOutput CreatePartial(
        string path,
        IReadOnlyList<DirectoryEntry> entries,
        IReadOnlyList<ToolWarning> warnings,
        int warningsOmitted,
        string? truncatedBy) => new()
    {
        Status = ToolStatus.Partial,
        Path = path,
        Entries = entries,
        ReturnedEntries = entries.Count,
        TotalEntries = null,
        HasMore = null,
        NextResultOffset = null,
        TruncatedBy = truncatedBy,
        Warnings = warnings,
        WarningsOmitted = warningsOmitted > 0 ? warningsOmitted : null,
    };

    private static bool Fits(ListDirectoryOutput output) =>
        ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ListDirectoryOutput) <=
        ToolBudgets.CanonicalResultBytes;

    private sealed class EntrySnapshotComparer : IComparer<FileSystemEntrySnapshot>
    {
        public int Compare(FileSystemEntrySnapshot x, FileSystemEntrySnapshot y)
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(x.Name, y.Name);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(x.Path, y.Path);
        }
    }

    private sealed class ReverseEntrySnapshotComparer : IComparer<FileSystemEntrySnapshot>
    {
        public int Compare(FileSystemEntrySnapshot x, FileSystemEntrySnapshot y) =>
            SnapshotComparer.Compare(y, x);
    }
}
