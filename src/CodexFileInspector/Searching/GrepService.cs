using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;
using CodexFileInspector.Ripgrep;

namespace CodexFileInspector.Searching;

internal sealed class GrepService(
    IFileSystemPlatform fileSystem,
    RipgrepInvocationBuilder invocationBuilder,
    IRipgrepRunner runner)
{
    public async ValueTask<GrepOutput> SearchAsync(
        GrepRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        OutputBudget.EnsurePathFits(request.Path);
        FileAttributes attributes = fileSystem.GetAttributes(request.Path);
        bool pathIsDirectory = (attributes & FileAttributes.Directory) != 0;
        if (!pathIsDirectory)
        {
            using Stream readable = fileSystem.OpenRead(request.Path);
        }

        RipgrepRunRequest runRequest = invocationBuilder.BuildGrep(request);
        WarningCollector warnings = new();
        GrepJsonAccumulator accumulator = new(
            request,
            runRequest.WorkingDirectory,
            fileSystem,
            warnings);
        RipgrepRunResult run = await runner.RunAsync(
            runRequest,
            accumulator.Handle,
            cancellationToken).ConfigureAwait(false);

        bool incomplete = accumulator.HasAnomaly;
        if (run.OversizedRecord)
        {
            incomplete = true;
            warnings.Add(
                ToolErrorCodes.RipgrepEventTooLarge,
                "A ripgrep JSON event exceeded the fixed 8 MiB event budget.",
                null);
        }

        if (!run.StoppedEarly && run.ExitCode == 2)
        {
            if (RipgrepFailureClassifier.IsInvalidRegex(run.StandardError))
            {
                throw RipgrepFailureClassifier.InvalidRegex();
            }

            if (RipgrepFailureClassifier.IsInvalidGlob(run.StandardError))
            {
                throw RipgrepFailureClassifier.InvalidGlob(
                    run.StandardError,
                    request.IncludeGlobs ?? [],
                    request.ExcludeGlobs ?? []);
            }

            if (!pathIsDirectory)
            {
                throw new ToolExecutionException(
                    ToolErrorCodes.RipgrepFailed,
                    "Bundled ripgrep could not search the named file.",
                    path: request.Path,
                    actual: run.ExitCode);
            }

            incomplete = true;
            RipgrepFailureClassifier.AddTraversalWarning(
                warnings,
                run.StandardErrorTruncated);
        }
        else if (!run.StoppedEarly && run.ExitCode is not 0 and not 1)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.RipgrepFailed,
                "Bundled ripgrep terminated with an unexpected exit code.",
                path: request.Path,
                actual: run.ExitCode);
        }

        return request.OutputMode switch
        {
            GrepOutputMode.Matches => BuildMatches(request, accumulator, run, warnings, incomplete),
            GrepOutputMode.FilesWithMatches => BuildFiles(request, accumulator, run, warnings, incomplete),
            GrepOutputMode.Count => BuildCounts(request, accumulator, warnings, incomplete),
            _ => throw new ToolExecutionException(
                ToolErrorCodes.InvalidArgument,
                "output_mode is not supported.",
                field: "output_mode"),
        };
    }

    private static GrepOutput BuildMatches(
        GrepRequest request,
        GrepJsonAccumulator accumulator,
        RipgrepRunResult run,
        WarningCollector warnings,
        bool incomplete)
    {
        List<GrepSelectedMatch> selected = accumulator.SelectedMatches.ToList();
        if (!run.StoppedEarly && !incomplete &&
            request.ResultOffset > 0 && request.ResultOffset >= accumulator.MatchResultsSeen)
        {
            throw OffsetPastEnd(request, accumulator.MatchResultsSeen);
        }

        long? totalResults = !run.StoppedEarly && !incomplete
            ? accumulator.MatchResultsSeen
            : null;
        bool hasMore = accumulator.ExtraResultSeen;
        string? truncatedBy = hasMore ? "result_limit" : null;
        HashSet<GrepLineKey> removedContext = new(accumulator.PreRemovedContext);
        IReadOnlyList<GrepLineKey> removalOrder = GrepBlockBuilder.ContextRemovalOrder(
            selected,
            accumulator.Lines);
        GrepOutput output = CreateMatchesOutput(
            request,
            selected,
            accumulator.Lines,
            removedContext,
            totalResults,
            hasMore,
            truncatedBy,
            warnings,
            incomplete);

        int removalIndex = 0;
        while (!Fits(output) && removalIndex < removalOrder.Count)
        {
            int currentBytes = ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GrepOutput);
            int remaining = removalOrder.Count - removalIndex;
            int batch = currentBytes > ToolBudgets.CanonicalResultBytes * 4
                ? Math.Max(1, remaining / 2)
                : currentBytes > ToolBudgets.CanonicalResultBytes * 2
                    ? Math.Max(1, remaining / 4)
                    : 1;
            for (int index = 0; index < batch && removalIndex < removalOrder.Count; index++)
            {
                removedContext.Add(removalOrder[removalIndex++]);
            }

            output = CreateMatchesOutput(
                request,
                selected,
                accumulator.Lines,
                removedContext,
                totalResults,
                hasMore,
                truncatedBy,
                warnings,
                incomplete);
        }

        while (!Fits(output) && selected.Count > 1)
        {
            selected.RemoveAt(selected.Count - 1);
            hasMore = true;
            truncatedBy = "byte_budget";
            output = CreateMatchesOutput(
                request,
                selected,
                accumulator.Lines,
                removedContext,
                totalResults,
                hasMore,
                truncatedBy,
                warnings,
                incomplete);
        }

        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "One minimum grep match result cannot fit within the canonical result budget.",
                path: request.Path,
                limit: ToolBudgets.CanonicalResultBytes);
        }

        return output;
    }

    private static GrepOutput BuildFiles(
        GrepRequest request,
        GrepJsonAccumulator accumulator,
        RipgrepRunResult run,
        WarningCollector warnings,
        bool incomplete)
    {
        List<string> paths = accumulator.MatchingFiles.ToList();
        if (!run.StoppedEarly && !incomplete &&
            request.ResultOffset > 0 && request.ResultOffset >= accumulator.MatchingFileCount)
        {
            throw OffsetPastEnd(request, accumulator.MatchingFileCount);
        }

        long? totalResults = !run.StoppedEarly && !incomplete
            ? accumulator.MatchingFileCount
            : null;
        bool hasMore = accumulator.ExtraResultSeen;
        string? truncatedBy = hasMore ? "result_limit" : null;
        GrepOutput output = CreateFilesOutput(
            request,
            paths,
            totalResults,
            hasMore,
            truncatedBy,
            warnings,
            incomplete);
        while (paths.Count > 0 && !Fits(output))
        {
            paths.RemoveAt(paths.Count - 1);
            hasMore = true;
            truncatedBy = "byte_budget";
            output = CreateFilesOutput(
                request,
                paths,
                totalResults,
                hasMore,
                truncatedBy,
                warnings,
                incomplete);
        }

        EnsureFits(output, request.Path);
        return output;
    }

    private static GrepOutput BuildCounts(
        GrepRequest request,
        GrepJsonAccumulator accumulator,
        WarningCollector warnings,
        bool incomplete)
    {
        if (!incomplete && request.ResultOffset > 0 && request.ResultOffset >= accumulator.MatchingFileCount)
        {
            throw OffsetPastEnd(request, accumulator.MatchingFileCount);
        }

        List<GrepFileCount> counts = accumulator.CountPage.ToList();
        long? totalResults = incomplete ? null : accumulator.MatchingFileCount;
        bool hasMore = accumulator.MatchingFileCount > request.ResultOffset + counts.Count;
        string? truncatedBy = hasMore ? "result_limit" : null;
        GrepOutput output = CreateCountOutput(
            request,
            counts,
            incomplete ? null : new GrepTotals(
                accumulator.MatchingFileCount,
                accumulator.MatchingLines,
                accumulator.Occurrences),
            totalResults,
            hasMore,
            truncatedBy,
            warnings,
            incomplete);
        while (counts.Count > 0 && !Fits(output))
        {
            counts.RemoveAt(counts.Count - 1);
            hasMore = true;
            truncatedBy = "byte_budget";
            output = CreateCountOutput(
                request,
                counts,
                incomplete ? null : new GrepTotals(
                    accumulator.MatchingFileCount,
                    accumulator.MatchingLines,
                    accumulator.Occurrences),
                totalResults,
                hasMore,
                truncatedBy,
                warnings,
                incomplete);
        }

        EnsureFits(output, request.Path);
        return output;
    }

    private static GrepOutput CreateMatchesOutput(
        GrepRequest request,
        IReadOnlyList<GrepSelectedMatch> selected,
        IReadOnlyDictionary<GrepLineKey, GrepLineData> lines,
        IReadOnlySet<GrepLineKey> removedContext,
        long? totalResults,
        bool hasMore,
        string? truncatedBy,
        WarningCollector warnings,
        bool incomplete)
    {
        IReadOnlyList<GrepMatchBlock> blocks = GrepBlockBuilder.Build(
            selected,
            lines,
            request.ContextLines,
            removedContext);
        return new GrepOutput
        {
            Status = incomplete ? ToolStatus.Partial : ToolStatus.Success,
            Path = request.Path,
            Blocks = blocks,
            ReturnedResults = selected.Count,
            TotalResults = incomplete ? null : totalResults,
            HasMore = incomplete ? null : hasMore,
            NextResultOffset = incomplete || !hasMore ? null : request.ResultOffset + selected.Count,
            TruncatedBy = truncatedBy,
            Warnings = incomplete ? warnings.Items : null,
            WarningsOmitted = incomplete && warnings.Omitted > 0 ? warnings.Omitted : null,
        };
    }

    private static GrepOutput CreateFilesOutput(
        GrepRequest request,
        IReadOnlyList<string> paths,
        long? totalResults,
        bool hasMore,
        string? truncatedBy,
        WarningCollector warnings,
        bool incomplete) => new()
    {
        Status = incomplete ? ToolStatus.Partial : ToolStatus.Success,
        Path = request.Path,
        Paths = paths,
        ReturnedResults = paths.Count,
        TotalResults = incomplete ? null : totalResults,
        HasMore = incomplete ? null : hasMore,
        NextResultOffset = incomplete || !hasMore ? null : request.ResultOffset + paths.Count,
        TruncatedBy = truncatedBy,
        Warnings = incomplete ? warnings.Items : null,
        WarningsOmitted = incomplete && warnings.Omitted > 0 ? warnings.Omitted : null,
    };

    private static GrepOutput CreateCountOutput(
        GrepRequest request,
        IReadOnlyList<GrepFileCount> counts,
        GrepTotals? totals,
        long? totalResults,
        bool hasMore,
        string? truncatedBy,
        WarningCollector warnings,
        bool incomplete) => new()
    {
        Status = incomplete ? ToolStatus.Partial : ToolStatus.Success,
        Path = request.Path,
        Counts = counts,
        Totals = totals,
        ReturnedResults = counts.Count,
        TotalResults = incomplete ? null : totalResults,
        HasMore = incomplete ? null : hasMore,
        NextResultOffset = incomplete || !hasMore ? null : request.ResultOffset + counts.Count,
        TruncatedBy = truncatedBy,
        Warnings = incomplete ? warnings.Items : null,
        WarningsOmitted = incomplete && warnings.Omitted > 0 ? warnings.Omitted : null,
    };

    private static ToolExecutionException OffsetPastEnd(GrepRequest request, long total) => new(
        ToolErrorCodes.ResultOffsetPastEnd,
        "result_offset is past the complete grep result set.",
        field: "result_offset",
        path: request.Path,
        actual: request.ResultOffset,
        total: total);

    private static bool Fits(GrepOutput output) =>
        ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GrepOutput) <=
        ToolBudgets.CanonicalResultBytes;

    private static void EnsureFits(GrepOutput output, string path)
    {
        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The grep result cannot fit within the canonical result budget.",
                path: path,
                limit: ToolBudgets.CanonicalResultBytes);
        }
    }
}
