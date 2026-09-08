using CodexFileInspector.Contracts;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class RipgrepRunnerTests
{
    [Fact]
    public async Task Callback_can_stop_the_job_after_one_raw_record()
    {
        using TestWorkspace workspace = new();
        File.WriteAllText(workspace.PathFor("a.txt"), "a");
        File.WriteAllText(workspace.PathFor("b.txt"), "b");
        RipgrepRunner runner = new(new WindowsJobProcessPlatform());
        RipgrepRunRequest request = new(
            ["--no-config", "--files", "--null", "--", workspace.Root],
            workspace.Root,
            0,
            ToolBudgets.PathRecordBytes);

        RipgrepRunResult result = await runner.RunAsync(
            request,
            _ => false,
            CancellationToken.None);

        Assert.True(result.StoppedEarly);
        Assert.Equal(1, result.RecordsRead);
        Assert.False(result.OversizedRecord);
    }

    [Fact]
    public async Task Raw_record_limit_is_enforced_before_unbounded_decoding()
    {
        RipgrepRunner runner = new(new WindowsJobProcessPlatform());
        RipgrepRunRequest request = new(
            ["--no-config", "--version"],
            AppContext.BaseDirectory,
            (byte)'\n',
            MaximumRecordBytes: 4);

        RipgrepRunResult result = await runner.RunAsync(
            request,
            _ => true,
            CancellationToken.None);

        Assert.True(result.StoppedEarly);
        Assert.True(result.OversizedRecord);
        Assert.Equal(0, result.RecordsRead);
    }

    [Fact]
    public void Invocation_is_explicit_deterministic_and_never_follows_links()
    {
        using TestWorkspace workspace = new();
        Directory.CreateDirectory(workspace.PathFor(".git"));
        WindowsFileSystemPlatform platform = new();
        RipgrepInvocationBuilder builder = new(platform);
        GlobRequest request = new(
            workspace.Root,
            ["**/*.cs"],
            ["**/obj/**"],
            IncludeHidden: true,
            RespectIgnoreFiles: true);

        RipgrepRunRequest invocation = builder.BuildGlob(request);

        Assert.Contains("--no-config", invocation.Arguments);
        Assert.Contains("--no-ignore-global", invocation.Arguments);
        Assert.Contains("--no-mmap", invocation.Arguments);
        Assert.Contains("--sort", invocation.Arguments);
        Assert.Contains("--hidden", invocation.Arguments);
        Assert.Contains("--no-require-git", invocation.Arguments);
        Assert.DoesNotContain("--no-ignore", invocation.Arguments);
        Assert.DoesNotContain("--no-ignore-parent", invocation.Arguments);
        Assert.DoesNotContain("--follow", invocation.Arguments);
        Assert.Equal(workspace.Root, invocation.Arguments[^1]);
        Assert.Equal(workspace.Root, invocation.WorkingDirectory);
    }

    [Fact]
    public async Task Pre_cancelled_request_does_not_start_a_process()
    {
        NeverStartProcessPlatform platform = new();
        RipgrepRunner runner = new(platform);
        RipgrepRunRequest request = new(
            ["--no-config", "--version"], AppContext.BaseDirectory, (byte)'\n', ToolBudgets.RipgrepEventBytes);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(request, _ => true, cancellation.Token));
        Assert.False(platform.Started);
    }

    [Fact]
    public async Task Host_cancellation_terminates_the_job_and_propagates()
    {
        using TestWorkspace workspace = new();
        File.WriteAllText(workspace.PathFor("value.txt"), string.Concat(Enumerable.Repeat("MATCH\n", 100_000)));
        RipgrepRunner runner = new(new WindowsJobProcessPlatform());
        RipgrepRunRequest request = new(
            ["--no-config", "--json", "--fixed-strings", "--regexp", "MATCH", "--", workspace.Root],
            workspace.Root,
            (byte)'\n',
            ToolBudgets.RipgrepEventBytes);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        int receivedRecords = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(request, _ =>
            {
                receivedRecords++;
                cancellation.Cancel();
                return true;
            }, cancellation.Token));
        Assert.True(receivedRecords > 0);
    }

    private sealed class NeverStartProcessPlatform : IProcessPlatform
    {
        public bool Started { get; private set; }

        public IRunningProcess Start(ProcessStartRequest request)
        {
            Started = true;
            throw new InvalidOperationException("An already-cancelled request must not start a process.");
        }
    }
}
