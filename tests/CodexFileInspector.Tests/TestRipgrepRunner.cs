using CodexFileInspector.Ripgrep;

namespace CodexFileInspector.Tests;

internal sealed class TestRipgrepRunner : IRipgrepRunner
{
    public IReadOnlyList<byte[]> Records { get; init; } = [];

    public int ExitCode { get; init; }

    public string StandardError { get; init; } = string.Empty;

    public bool StandardErrorTruncated { get; init; }

    public bool OversizedRecord { get; init; }

    public ValueTask<RipgrepRunResult> RunAsync(
        RipgrepRunRequest request,
        RipgrepRecordHandler onRecord,
        CancellationToken cancellationToken)
    {
        int read = 0;
        bool stopped = false;
        foreach (byte[] record in Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read++;
            if (!onRecord(record))
            {
                stopped = true;
                break;
            }
        }

        return ValueTask.FromResult(new RipgrepRunResult(
            ExitCode,
            read,
            stopped,
            OversizedRecord,
            StandardError,
            StandardErrorTruncated));
    }
}
