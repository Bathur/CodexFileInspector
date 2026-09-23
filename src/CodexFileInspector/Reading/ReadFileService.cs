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
        cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();
        VerifyUnchanged(request.Path, stream, before);
        cancellationToken.ThrowIfCancellationRequested();
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
            EnsureOutputFits(empty, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return empty;
        }

        if (reachedEnd && request.StartLine > linesSeen)
        {
            throw RangePastEnd(request.Path, linesSeen);
        }

        // JSON cannot encode text more compactly than its UTF-8 bytes. Exclude
        // prefixes that cannot possibly fit before joining or serializing them;
        // escaping and the result envelope are checked exactly below.
        int requestedCount = Math.Min(captured.Count, request.LineCount);
        int possibleCount = 0;
        int contentBytes = 0;
        while (possibleCount < requestedCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lineBytes = Encoding.UTF8.GetByteCount(captured[possibleCount].Text);
            int candidateBytes = contentBytes + lineBytes + (possibleCount == 0 ? 0 : 1);
            if (candidateBytes > ToolBudgets.CanonicalResultBytes)
            {
                break;
            }

            contentBytes = candidateBytes;
            possibleCount++;
        }

        bool truncatedByByteBudget = possibleCount < requestedCount;
        bool hasMore = truncatedByByteBudget || captured.Count > request.LineCount;
        List<CapturedLine> returned = captured.Take(Math.Max(1, possibleCount)).ToList();
        long? totalLines = reachedEnd ? linesSeen : null;
        string stopReason = truncatedByByteBudget ? "byte_budget" : hasMore ? "line_count" : "end_of_file";

        ReadFileOutput output = CreateOutput(
            request.Path,
            detected.Name,
            returned,
            totalLines,
            hasMore,
            stopReason);

        if (returned.Count > 1 && !Fits(output, cancellationToken))
        {
            truncatedByByteBudget = true;
            // With the paging envelope fixed, each additional line only increases
            // the canonical size. Find the longest fitting prefix without
            // repeatedly rebuilding and serializing almost the entire request.
            int low = 1;
            int high = returned.Count - 1;
            int bestCount = 0;
            ReadFileOutput? best = null;
            while (low <= high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int middle = low + ((high - low) / 2);
                ReadFileOutput candidate = CreateOutput(
                    request.Path,
                    detected.Name,
                    returned.GetRange(0, middle),
                    totalLines,
                    hasMore: true,
                    stopReason: "byte_budget");
                if (Fits(candidate, cancellationToken))
                {
                    bestCount = middle;
                    best = candidate;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            // Keep one line for the existing excerpt-clipping fallback when no
            // complete line fits, so continuation never skips an unread line.
            int retainedCount = Math.Max(1, bestCount);
            returned.RemoveRange(retainedCount, returned.Count - retainedCount);
            output = best ?? CreateOutput(
                request.Path,
                detected.Name,
                returned,
                totalLines,
                hasMore: true,
                stopReason: "byte_budget");
        }

        if (returned.Count == 1 && !Fits(output, cancellationToken))
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
                cancellationToken.ThrowIfCancellationRequested();
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

                if (Fits(candidate, cancellationToken))
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

        if (returned.Count == 0 || !Fits(output, cancellationToken))
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "One read result cannot fit within the canonical result budget.",
                path: request.Path,
                limit: ToolBudgets.CanonicalResultBytes);
        }

        EnsureOutputFits(output, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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

    private static bool Fits(ReadFileOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int byteCount = ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput);
        cancellationToken.ThrowIfCancellationRequested();
        return byteCount <= ToolBudgets.CanonicalResultBytes;
    }

    private static void EnsureOutputFits(ReadFileOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int byteCount = ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput);
        cancellationToken.ThrowIfCancellationRequested();
        if (byteCount > ToolBudgets.CanonicalResultBytes)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The read result cannot fit within the canonical result budget.",
                path: output.Path,
                limit: ToolBudgets.CanonicalResultBytes,
                actual: byteCount);
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
