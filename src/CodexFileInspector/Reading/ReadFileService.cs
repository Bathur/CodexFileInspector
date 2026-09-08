using System.Runtime.ExceptionServices;
using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;

namespace CodexFileInspector.Reading;

internal sealed class ReadFileService(IFileSystemPlatform fileSystem)
{
    public async ValueTask<ReadFileOutput> ReadAsync(
        ReadFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        OutputBudget.EnsurePathFits(request.Path);

        FileAttributes attributes = fileSystem.GetAttributes(request.Path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.PathNotFile,
                "read_file requires a file path.",
                field: "path",
                path: request.Path);
        }

        await using Stream stream = fileSystem.OpenRead(request.Path);
        FileChangeStamp before = fileSystem.GetFileChangeStamp(stream);
        DetectedTextEncoding detected = await TextEncodingDetector.DetectAsync(stream, cancellationToken)
            .ConfigureAwait(false);

        List<CapturedLine> captured = new(Math.Min(request.LineCount + 1, ToolBudgets.ReadFileLineCountMaximum + 1));
        long linesSeen = 0;
        bool reachedEnd = false;
        Exception? readFailure = null;

        try
        {
            BoundedLogicalLineReader reader = new(detected.Encoding);
            reachedEnd = await reader.ReadAsync(
                stream,
                line =>
                {
                    linesSeen++;
                    if (linesSeen >= request.StartLine && captured.Count <= request.LineCount)
                    {
                        captured.Add(new CapturedLine(linesSeen, line.Text, line.TotalUtf8Bytes));
                    }

                    return captured.Count <= request.LineCount;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            readFailure = exception;
        }

        VerifyUnchanged(request.Path, stream, before);
        if (readFailure is not null)
        {
            ExceptionDispatchInfo.Capture(readFailure).Throw();
        }

        if (reachedEnd && linesSeen == 0)
        {
            if (request.StartLine > 1)
            {
                throw RangePastEnd(request.Path, linesSeen);
            }

            ReadFileOutput empty = CreateOutput(
                request.Path,
                detected.Name,
                [],
                totalLines: 0,
                hasMore: false,
                stopReason: "end_of_file");
            EnsureOutputFits(empty);
            return empty;
        }

        if (reachedEnd && request.StartLine > linesSeen)
        {
            throw RangePastEnd(request.Path, linesSeen);
        }

        bool hasMore = captured.Count > request.LineCount;
        List<CapturedLine> returned = captured.Take(request.LineCount).ToList();
        long? totalLines = reachedEnd ? linesSeen : null;
        string stopReason = hasMore ? "line_count" : "end_of_file";

        ReadFileOutput output = CreateOutput(
            request.Path,
            detected.Name,
            returned,
            totalLines,
            hasMore,
            stopReason);

        bool truncatedByByteBudget = false;
        while (returned.Count > 1 && !Fits(output))
        {
            returned.RemoveAt(returned.Count - 1);
            truncatedByByteBudget = true;
            output = CreateOutput(
                request.Path,
                detected.Name,
                returned,
                totalLines,
                hasMore: true,
                stopReason: "byte_budget");
        }

        if (returned.Count == 1 && !Fits(output))
        {
            CapturedLine original = returned[0];
            int originalVisibleBytes = Encoding.UTF8.GetByteCount(original.Text);
            bool candidateHasMore = truncatedByByteBudget || hasMore;
            string candidateStopReason = truncatedByByteBudget ? "byte_budget" : stopReason;
            int low = 0;
            int high = originalVisibleBytes;
            ReadFileOutput? best = null;
            CapturedLine bestLine = original with { Text = string.Empty };

            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                string clipped = Utf8Budget.Truncate(original.Text, middle, out _);
                CapturedLine candidateLine = original with { Text = clipped };
                ReadFileOutput candidate = CreateOutput(
                    request.Path,
                    detected.Name,
                    [candidateLine],
                    totalLines,
                    candidateHasMore,
                    candidateStopReason);

                if (Fits(candidate))
                {
                    best = candidate;
                    bestLine = candidateLine;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (best is null)
            {
                throw new ToolExecutionException(
                    ToolErrorCodes.OutputRecordTooLarge,
                    "One read result cannot fit within the canonical result budget.",
                    path: request.Path,
                    limit: ToolBudgets.CanonicalResultBytes);
            }

            returned[0] = bestLine;
            output = best;
        }

        if (returned.Count == 0 || !Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "One read result cannot fit within the canonical result budget.",
                path: request.Path,
                limit: ToolBudgets.CanonicalResultBytes);
        }

        if (truncatedByByteBudget)
        {
            output = CreateOutput(
                request.Path,
                detected.Name,
                returned,
                totalLines,
                hasMore: true,
                stopReason: "byte_budget");
        }

        EnsureOutputFits(output);
        return output;
    }

    private static ReadFileOutput CreateOutput(
        string path,
        string encoding,
        IReadOnlyList<CapturedLine> lines,
        long? totalLines,
        bool hasMore,
        string stopReason)
    {
        string content = string.Join('\n', lines.Select(static line => line.Text));
        LineTruncation[] truncations = lines
            .Select(static line => new
            {
                Line = line,
                ReturnedBytes = Encoding.UTF8.GetByteCount(line.Text),
            })
            .Where(static item => item.Line.TotalUtf8Bytes > item.ReturnedBytes)
            .Select(static item => new LineTruncation(
                item.Line.Number,
                item.ReturnedBytes,
                item.Line.TotalUtf8Bytes))
            .ToArray();

        return new ReadFileOutput
        {
            Status = ToolStatus.Success,
            Path = path,
            Encoding = encoding,
            StartLine = lines.Count == 0 ? null : lines[0].Number,
            EndLine = lines.Count == 0 ? null : lines[^1].Number,
            ReturnedLines = lines.Count,
            TotalLines = totalLines,
            HasMore = hasMore,
            NextStartLine = hasMore ? lines[^1].Number + 1 : null,
            StopReason = stopReason,
            Content = content,
            LineTruncations = truncations,
        };
    }

    private void VerifyUnchanged(string path, Stream stream, FileChangeStamp before)
    {
        try
        {
            FileChangeStamp afterHandle = fileSystem.GetFileChangeStamp(stream);
            FileChangeStamp currentPath = fileSystem.GetFileChangeStamp(path);
            if (before != afterHandle || before != currentPath)
            {
                throw Changed(path);
            }
        }
        catch (ToolExecutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Changed(path, exception);
        }
    }

    private static bool Fits(ReadFileOutput output) =>
        ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput) <=
        ToolBudgets.CanonicalResultBytes;

    private static void EnsureOutputFits(ReadFileOutput output)
    {
        if (!Fits(output))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The read result cannot fit within the canonical result budget.",
                path: output.Path,
                limit: ToolBudgets.CanonicalResultBytes,
                actual: ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput));
        }
    }

    private static ToolExecutionException RangePastEnd(string path, long totalLines) =>
        new(
            ToolErrorCodes.RangePastEof,
            "start_line is beyond the end of the file.",
            field: "start_line",
            path: path,
            total: totalLines);

    private static ToolExecutionException Changed(string path, Exception? innerException = null) =>
        new(
            ToolErrorCodes.ChangedDuringRead,
            "The file changed materially during this read; retry the request.",
            path: path,
            innerException: innerException);

    private sealed record CapturedLine(long Number, string Text, long TotalUtf8Bytes);
}
