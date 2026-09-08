using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;
using CodexFileInspector.Ripgrep;

namespace CodexFileInspector.Globbing;

internal sealed class GlobService(
    IFileSystemPlatform fileSystem,
    RipgrepInvocationBuilder invocationBuilder,
    IRipgrepRunner runner)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask<GlobOutput> FindAsync(
        GlobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        OutputBudget.EnsurePathFits(request.Path);
        FileAttributes attributes = fileSystem.GetAttributes(request.Path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.PathNotDirectory,
                "glob requires a directory path.",
                field: "path",
                path: request.Path);
        }

        RipgrepRunRequest runRequest = invocationBuilder.BuildGlob(request);
        WarningCollector warnings = new();
        List<string> pageAndExtra = new(request.MaxResults + 1);
        long totalResults = 0;
        bool incomplete = false;
        string? lastNormalizedPath = null;

        RipgrepRunResult run = await runner.RunAsync(
            runRequest,
            record =>
            {
                string rawPath;
                try
                {
                    rawPath = StrictUtf8.GetString(record);
                }
                catch (DecoderFallbackException)
                {
                    incomplete = true;
                    warnings.Add(
                        ToolWarningCodes.TextAnomaly,
                        "A discovered path could not be decoded as UTF-8 and was omitted.",
                        null);
                    return true;
                }

                string normalized;
                try
                {
                    normalized = fileSystem.NormalizeAbsolutePath(
                        Path.IsPathFullyQualified(rawPath)
                            ? rawPath
                            : Path.Combine(runRequest.WorkingDirectory, rawPath));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    incomplete = true;
                    warnings.Add(
                        ToolWarningCodes.TextAnomaly,
                        "A discovered path could not be normalized and was omitted.",
                        null);
                    return true;
                }

                if (StringComparer.Ordinal.Equals(lastNormalizedPath, normalized))
                {
                    return true;
                }

                lastNormalizedPath = normalized;
                totalResults++;
                if (totalResults <= request.ResultOffset)
                {
                    return true;
                }

                int serializedBytes = Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(normalized));
                if (serializedBytes > ToolBudgets.PathRecordBytes)
                {
                    incomplete = true;
                    warnings.Add(
                        ToolErrorCodes.OutputRecordTooLarge,
                        "A discovered path exceeded the fixed output-record budget and was omitted.",
                        null);
                    return true;
                }

                pageAndExtra.Add(normalized);
                return pageAndExtra.Count <= request.MaxResults;
            },
            cancellationToken).ConfigureAwait(false);

        if (run.OversizedRecord)
        {
            incomplete = true;
            warnings.Add(
                ToolErrorCodes.OutputRecordTooLarge,
                "A raw ripgrep path record exceeded the fixed record budget.",
                null);
        }

        if (!run.StoppedEarly && run.ExitCode == 2 && RipgrepFailureClassifier.IsInvalidGlob(run.StandardError))
        {
            throw RipgrepFailureClassifier.InvalidGlob(
                run.StandardError,
                request.IncludeGlobs,
                request.ExcludeGlobs ?? []);
        }

        if (!run.StoppedEarly && run.ExitCode == 2)
        {
            incomplete = true;
            RipgrepFailureClassifier.AddTraversalWarning(
                warnings,
                run.StandardErrorTruncated);
        }
        else if (!run.StoppedEarly && run.ExitCode is not 0 and not 1)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.RipgrepFailed,
                "Bundled ripgrep terminated unexpectedly.",
                path: request.Path,
                actual: run.ExitCode);
        }

        List<string> page = pageAndExtra.Take(request.MaxResults).ToList();
        if (incomplete)
        {
            return FitPartial(request.Path, page, warnings);
        }

        bool hasMore = pageAndExtra.Count > request.MaxResults;
        long? exactTotal = run.StoppedEarly ? null : totalResults;
        if (!run.StoppedEarly && request.ResultOffset > 0 && request.ResultOffset >= totalResults)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.ResultOffsetPastEnd,
                "result_offset is past the complete glob result set.",
                field: "result_offset",
                path: request.Path,
                actual: request.ResultOffset,
                total: totalResults);
        }

        string? truncatedBy = hasMore ? "result_limit" : null;
        GlobOutput output = CreateSuccess(
            request.Path,
            request.ResultOffset,
            page,
            exactTotal,
            hasMore,
            truncatedBy);
        while (page.Count > 0 && !Fits(output))
        {
            page.RemoveAt(page.Count - 1);
            hasMore = true;
            truncatedBy = "byte_budget";
            output = CreateSuccess(
                request.Path,
                request.ResultOffset,
                page,
                exactTotal,
                hasMore,
                truncatedBy);
        }

        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The glob result cannot fit within the canonical result budget.",
                path: request.Path,
                limit: ToolBudgets.CanonicalResultBytes);
        }

        return output;
    }

    private static GlobOutput FitPartial(
        string path,
        List<string> paths,
        WarningCollector warningCollector)
    {
        List<ToolWarning> warnings = warningCollector.Items.ToList();
        int warningsOmitted = warningCollector.Omitted;
        string? truncatedBy = null;
        GlobOutput output = CreatePartial(path, paths, warnings, warningsOmitted, truncatedBy);

        while (paths.Count > 0 && !Fits(output))
        {
            paths.RemoveAt(paths.Count - 1);
            truncatedBy = "byte_budget";
            output = CreatePartial(path, paths, warnings, warningsOmitted, truncatedBy);
        }

        while (warnings.Count > 1 && !Fits(output))
        {
            warnings.RemoveAt(warnings.Count - 1);
            warningsOmitted++;
            output = CreatePartial(path, paths, warnings, warningsOmitted, truncatedBy);
        }

        if (!Fits(output) && warnings.Any(static warning => warning.Path is not null))
        {
            warnings = warnings.Select(static warning => warning with { Path = null }).ToList();
            output = CreatePartial(path, paths, warnings, warningsOmitted, truncatedBy);
        }

        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The partial glob result cannot fit within the canonical result budget.",
                path: path,
                limit: ToolBudgets.CanonicalResultBytes);
        }

        return output;
    }

    private static GlobOutput CreateSuccess(
        string path,
        int resultOffset,
        IReadOnlyList<string> paths,
        long? totalResults,
        bool hasMore,
        string? truncatedBy) => new()
    {
        Status = ToolStatus.Success,
        Path = path,
        Paths = paths,
        ReturnedResults = paths.Count,
        TotalResults = totalResults,
        HasMore = hasMore,
        NextResultOffset = hasMore ? resultOffset + paths.Count : null,
        TruncatedBy = truncatedBy,
    };

    private static GlobOutput CreatePartial(
        string path,
        IReadOnlyList<string> paths,
        IReadOnlyList<ToolWarning> warnings,
        int warningsOmitted,
        string? truncatedBy) => new()
    {
        Status = ToolStatus.Partial,
        Path = path,
        Paths = paths,
        ReturnedResults = paths.Count,
        TotalResults = null,
        HasMore = null,
        NextResultOffset = null,
        TruncatedBy = truncatedBy,
        Warnings = warnings,
        WarningsOmitted = warningsOmitted > 0 ? warningsOmitted : null,
    };

    private static bool Fits(GlobOutput output) =>
        ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GlobOutput) <=
        ToolBudgets.CanonicalResultBytes;
}
