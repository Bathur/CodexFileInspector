using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class WindowsProcessPlatformTests
{
    [Fact]
    public async Task Starts_bundled_ripgrep_without_path_lookup()
    {
        WindowsJobProcessPlatform platform = new();
        await using IRunningProcess process = platform.Start(new ProcessStartRequest(
            BundledRipgrep.RequireExecutable(),
            ["--no-config", "--version"]));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        string standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        string standardError = await process.StandardError.ReadToEndAsync(timeout.Token);
        int exitCode = await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("ripgrep 15.2.0", standardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError);
    }

    [Fact]
    public async Task Terminates_a_running_job()
    {
        WindowsJobProcessPlatform platform = new();
        await using IRunningProcess process = platform.Start(new ProcessStartRequest(
            BundledRipgrep.RequireExecutable(),
            ["--no-config", "needle"],
            KeepStandardInputOpen: true));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await process.TerminateAsync(timeout.Token);
        int exitCode = await process.WaitForExitAsync(timeout.Token);

        Assert.NotEqual(0, exitCode);
    }
}
