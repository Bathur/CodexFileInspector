using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Platform;
using CodexFileInspector.Ripgrep;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class CompletedProcessRunnerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    public async Task Buffered_pagination_keeps_a_completed_process_exit_code(int exitCode)
    {
        BufferedProcess process = new("first\nsecond\n", "retained diagnostic\n", exitCode, exitCode);
        RipgrepRunner runner = new(new BufferedProcessPlatform(process));

        RipgrepRunResult result = await runner.RunAsync(Request(), _ => false, CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.True(result.CompletedBeforeReading);
        Assert.True(result.ShouldValidateExitCode);
        Assert.True(result.StoppedEarly);
        Assert.Equal(1, result.RecordsRead);
        Assert.Equal("retained diagnostic\n", result.StandardError);
        Assert.Equal(0, process.TerminationCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Ordinary_pagination_still_terminates_the_process_and_distrusts_its_exit_code()
    {
        BufferedProcess process = new("first\nsecond\n", string.Empty, null, 17);
        RipgrepRunner runner = new(new BufferedProcessPlatform(process));

        RipgrepRunResult result = await runner.RunAsync(Request(), _ => false, CancellationToken.None);

        Assert.True(result.StoppedEarly);
        Assert.False(result.CompletedBeforeReading);
        Assert.False(result.ShouldValidateExitCode);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(1, result.RecordsRead);
        Assert.Equal(1, process.TerminationCalls);
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task End_of_output_requires_exit_validation_in_both_process_states(bool completed)
    {
        BufferedProcess process = new("first\nsecond\n", string.Empty, completed ? 17 : null, 17);
        RipgrepRunner runner = new(new BufferedProcessPlatform(process));

        RipgrepRunResult result = await runner.RunAsync(Request(), _ => true, CancellationToken.None);

        Assert.False(result.StoppedEarly);
        Assert.Equal(completed, result.CompletedBeforeReading);
        Assert.True(result.ShouldValidateExitCode);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(2, result.RecordsRead);
        Assert.Equal(0, process.TerminationCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task A_completed_process_still_obeys_the_raw_record_and_stderr_budgets()
    {
        BufferedProcess process = new("oversized-record\n", new string('x', ToolBudgets.RipgrepStandardErrorBytes + 100), 17, 17);
        RipgrepRunner runner = new(new BufferedProcessPlatform(process));
        int callbacks = 0;

        RipgrepRunResult result = await runner.RunAsync(Request() with { MaximumRecordBytes = 4 }, _ =>
        {
            callbacks++;
            return true;
        }, CancellationToken.None);

        Assert.True(result.OversizedRecord);
        Assert.True(result.StoppedEarly);
        Assert.True(result.ShouldValidateExitCode);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, result.RecordsRead);
        Assert.True(result.StandardErrorTruncated);
        Assert.Equal(ToolBudgets.RipgrepStandardErrorBytes, Encoding.UTF8.GetByteCount(result.StandardError));
        Assert.Equal(0, process.TerminationCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Cancellation_during_completed_output_consumption_propagates_without_termination()
    {
        BufferedProcess process = new("first\n", string.Empty, 0, 0);
        RipgrepRunner runner = new(new BufferedProcessPlatform(process));
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(Request(), _ =>
            {
                cancellation.Cancel();
                return true;
            }, cancellation.Token));

        Assert.Equal(0, process.TerminationCalls);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task A_handler_failure_keeps_its_exception_and_disposes_the_completed_process()
    {
        BufferedProcess process = new("first\n", string.Empty, 0, 0);
        RipgrepRunner runner = new(new BufferedProcessPlatform(process));
        InvalidDataException expected = new("The record could not be consumed.");

        InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runner.RunAsync(Request(), _ => throw expected, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(0, process.TerminationCalls);
        Assert.True(process.Disposed);
    }

    private static RipgrepRunRequest Request() => new(
        ["--no-config", "--version"], @"C:\fixture", (byte)'\n', ToolBudgets.RipgrepEventBytes);

    private sealed class BufferedProcessPlatform(BufferedProcess process) : IProcessPlatform
    {
        public IRunningProcess Start(ProcessStartRequest request) => process;
    }

    private sealed class BufferedProcess(
        string output,
        string error,
        int? completedExitCode,
        int waitExitCode) : IRunningProcess
    {
        public int Id => 1;

        public int? CompletedExitCode { get; } = completedExitCode;

        public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(output)));

        public StreamReader StandardError { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(error)));

        public int TerminationCalls { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(waitExitCode);
        }

        public ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminationCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
