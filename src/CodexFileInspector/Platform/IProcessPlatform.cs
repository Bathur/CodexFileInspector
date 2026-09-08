namespace CodexFileInspector.Platform;

internal sealed record ProcessStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool KeepStandardInputOpen = false);

internal interface IRunningProcess : IAsyncDisposable
{
    int Id { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken);

    ValueTask TerminateAsync(CancellationToken cancellationToken);
}

internal interface IProcessPlatform
{
    IRunningProcess Start(ProcessStartRequest request);
}
