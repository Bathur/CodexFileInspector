using System.Buffers;
using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;

namespace CodexFileInspector.Ripgrep;

internal delegate bool RipgrepRecordHandler(ReadOnlySpan<byte> record);

internal sealed record RipgrepRunRequest(
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    byte RecordDelimiter,
    int MaximumRecordBytes);

internal sealed record RipgrepRunResult(
    int ExitCode,
    int RecordsRead,
    bool StoppedEarly,
    bool OversizedRecord,
    string StandardError,
    bool StandardErrorTruncated);

internal interface IRipgrepRunner
{
    ValueTask<RipgrepRunResult> RunAsync(
        RipgrepRunRequest request,
        RipgrepRecordHandler onRecord,
        CancellationToken cancellationToken);
}

internal sealed class RipgrepRunner(IProcessPlatform processPlatform) : IRipgrepRunner
{
    private const int PipeBufferBytes = 64 * 1024;

    public async ValueTask<RipgrepRunResult> RunAsync(
        RipgrepRunRequest request,
        RipgrepRecordHandler onRecord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onRecord);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartRequest startRequest = new(
            BundledRipgrep.RequireExecutable(),
            request.Arguments,
            request.WorkingDirectory);

        await using IRunningProcess process = processPlatform.Start(startRequest);
        Task<BoundedStandardError> standardErrorTask = ReadStandardErrorAsync(
            process.StandardError.BaseStream,
            cancellationToken);

        int recordsRead = 0;
        bool stoppedEarly = false;
        bool oversizedRecord = false;

        try
        {
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(PipeBufferBytes);
            ArrayBufferWriter<byte> recordBuffer = new();
            try
            {
                while (!stoppedEarly)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int bytesRead = await process.StandardOutput.BaseStream
                        .ReadAsync(readBuffer.AsMemory(0, PipeBufferBytes), cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        if (recordBuffer.WrittenCount > 0)
                        {
                            if (!HandleRecord(recordBuffer.WrittenSpan, request, onRecord, ref recordsRead, out oversizedRecord))
                            {
                                stoppedEarly = true;
                            }
                        }

                        break;
                    }

                    int segmentStart = 0;
                    for (int index = 0; index < bytesRead; index++)
                    {
                        if (readBuffer[index] != request.RecordDelimiter)
                        {
                            continue;
                        }

                        AppendSegment(recordBuffer, readBuffer.AsSpan(segmentStart, index - segmentStart));
                        if (recordBuffer.WrittenCount > request.MaximumRecordBytes)
                        {
                            oversizedRecord = true;
                            stoppedEarly = true;
                            break;
                        }

                        if (!HandleRecord(recordBuffer.WrittenSpan, request, onRecord, ref recordsRead, out oversizedRecord))
                        {
                            stoppedEarly = true;
                            break;
                        }

                        recordBuffer.Clear();
                        segmentStart = index + 1;
                    }

                    if (!stoppedEarly)
                    {
                        AppendSegment(recordBuffer, readBuffer.AsSpan(segmentStart, bytesRead - segmentStart));
                        if (recordBuffer.WrittenCount > request.MaximumRecordBytes)
                        {
                            oversizedRecord = true;
                            stoppedEarly = true;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
            }

            if (stoppedEarly)
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }

            int exitCode = await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            BoundedStandardError standardError = await standardErrorTask.ConfigureAwait(false);
            return new RipgrepRunResult(
                exitCode,
                recordsRead,
                stoppedEarly,
                oversizedRecord,
                standardError.Text,
                standardError.Truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryTerminateAsync(process).ConfigureAwait(false);
            await IgnoreStandardErrorFailureAsync(standardErrorTask).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TryTerminateAsync(process).ConfigureAwait(false);
            await IgnoreStandardErrorFailureAsync(standardErrorTask).ConfigureAwait(false);
            throw;
        }
    }

    private static bool HandleRecord(
        ReadOnlySpan<byte> record,
        RipgrepRunRequest request,
        RipgrepRecordHandler onRecord,
        ref int recordsRead,
        out bool oversized)
    {
        if (request.RecordDelimiter == (byte)'\n' && record.Length > 0 && record[^1] == (byte)'\r')
        {
            record = record[..^1];
        }

        if (record.Length > request.MaximumRecordBytes)
        {
            oversized = true;
            return false;
        }

        oversized = false;
        if (record.IsEmpty)
        {
            return true;
        }

        recordsRead++;
        return onRecord(record);
    }

    private static void AppendSegment(ArrayBufferWriter<byte> destination, ReadOnlySpan<byte> segment)
    {
        if (segment.IsEmpty)
        {
            return;
        }

        segment.CopyTo(destination.GetSpan(segment.Length));
        destination.Advance(segment.Length);
    }

    private static async Task<BoundedStandardError> ReadStandardErrorAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PipeBufferBytes);
        ArrayBufferWriter<byte> retained = new(ToolBudgets.RipgrepStandardErrorBytes);
        bool truncated = false;
        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, PipeBufferBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                int available = ToolBudgets.RipgrepStandardErrorBytes - retained.WrittenCount;
                int toRetain = Math.Min(available, bytesRead);
                if (toRetain > 0)
                {
                    buffer.AsSpan(0, toRetain).CopyTo(retained.GetSpan(toRetain));
                    retained.Advance(toRetain);
                }

                truncated |= toRetain < bytesRead;
            }

            return new BoundedStandardError(
                Encoding.UTF8.GetString(retained.WrittenSpan),
                truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask TryTerminateAsync(IRunningProcess process)
    {
        try
        {
            await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // DisposeAsync closes the kill-on-close Job Object as the final cleanup path.
        }
    }

    private static async ValueTask IgnoreStandardErrorFailureAsync(Task<BoundedStandardError> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed record BoundedStandardError(string Text, bool Truncated);
}
