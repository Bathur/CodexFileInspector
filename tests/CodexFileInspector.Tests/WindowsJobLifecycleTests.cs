using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class WindowsJobLifecycleTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task An_already_exited_process_skips_assignment_and_preserves_its_result(
        bool writeMatch,
        int expectedExitCode)
    {
        Process process = StartBlockedRipgrep();
        try
        {
            if (writeMatch)
            {
                await process.StandardInput.WriteLineAsync("needle");
            }

            process.StandardInput.Close();
            Assert.True(process.WaitForExit(5_000));
            using SafeJobHandle job = new(IntPtr.Zero);
            int assignments = 0;

            int? completedExitCode = WindowsJobProcessPlatform.AssignOrObserveExit(
                job,
                process,
                (_, _) =>
                {
                    assignments++;
                    return 5;
                });

            Assert.Equal(0, assignments);
            Assert.Equal(expectedExitCode, completedExitCode);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            Assert.Equal(writeMatch ? "needle" : string.Empty, output.TrimEnd('\r', '\n'));
            Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally
        {
            StopAndDisposeOwnedProcess(process);
        }
    }

    [Fact]
    public async Task A_real_native_error_5_after_exit_preserves_the_completed_process_and_streams()
    {
        int assignments = 0;
        int? nativeError = null;
        WindowsJobProcessPlatform platform = new((job, process) =>
        {
            assignments++;
            Assert.False(process.HasExited);
            SafeProcessHandle processHandle = process.SafeHandle;
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(5_000));

            bool assigned = NativeMethods.AssignProcessToJobObject(job, processHandle);
            int error = assigned ? 0 : Marshal.GetLastPInvokeError();
            nativeError = error;
            Assert.False(assigned);
            return error;
        });

        await using IRunningProcess running = platform.Start(BlockedRipgrepRequest());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        Assert.Equal(1, assignments);
        Assert.Equal(5, nativeError);
        Assert.Equal(1, running.CompletedExitCode);
        Assert.Equal(string.Empty, await running.StandardOutput.ReadToEndAsync(timeout.Token));
        Assert.Equal(string.Empty, await running.StandardError.ReadToEndAsync(timeout.Token));
        Assert.Equal(1, await running.WaitForExitAsync(timeout.Token));
    }

    [Fact]
    public void Error_5_for_a_live_process_remains_a_failure_and_start_releases_its_resources()
    {
        Process? observer = null;
        SafeProcessHandle? ownedProcessHandle = null;
        SafeJobHandle? ownedJob = null;
        WindowsJobProcessPlatform platform = new((job, process) =>
        {
            Assert.False(process.HasExited);
            ownedProcessHandle = process.SafeHandle;
            ownedJob = job;
            observer = ObserveProcess(process);
            return 5;
        });

        try
        {
            Win32Exception failure = Assert.Throws<Win32Exception>(() => platform.Start(BlockedRipgrepRequest()));

            Assert.Equal(5, failure.NativeErrorCode);
            Process observed = observer ?? throw new InvalidOperationException("The live process was not observed.");
            Assert.True(observed.WaitForExit(5_000));
            Assert.True(ownedProcessHandle?.IsClosed);
            Assert.True(ownedJob?.IsClosed);
        }
        finally
        {
            StopAndDisposeOwnedProcess(observer);
        }
    }

    [Fact]
    public void A_different_assignment_error_is_not_hidden_by_the_process_exiting()
    {
        Process process = StartBlockedRipgrep();
        try
        {
            using SafeJobHandle job = new(IntPtr.Zero);
            int assignments = 0;

            Win32Exception failure = Assert.Throws<Win32Exception>(() =>
                WindowsJobProcessPlatform.AssignOrObserveExit(job, process, (_, running) =>
                {
                    assignments++;
                    Assert.False(running.HasExited);
                    running.StandardInput.Close();
                    Assert.True(running.WaitForExit(5_000));
                    return 1816;
                }));

            Assert.Equal(1, assignments);
            Assert.Equal(1816, failure.NativeErrorCode);
            Assert.True(process.HasExited);
        }
        finally
        {
            StopAndDisposeOwnedProcess(process);
        }
    }

    [Fact]
    public void Error_5_is_preserved_when_the_exited_state_cannot_be_confirmed()
    {
        Process process = StartBlockedRipgrep();
        Process? observer = null;
        try
        {
            observer = ObserveProcess(process);
            using SafeJobHandle job = new(IntPtr.Zero);
            int assignments = 0;

            Win32Exception failure = Assert.Throws<Win32Exception>(() =>
                WindowsJobProcessPlatform.AssignOrObserveExit(job, process, (_, running) =>
                {
                    assignments++;
                    Assert.False(running.HasExited);
                    // Test-only injection: disposing this object makes the
                    // post-assignment HasExited observation unavailable.
                    running.Dispose();
                    return 5;
                }));

            Assert.Equal(1, assignments);
            Assert.Equal(5, failure.NativeErrorCode);
        }
        finally
        {
            try
            {
                StopAndDisposeOwnedProcess(observer);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(7, true)]
    public void Failed_start_cleanup_attempts_every_step_despite_secondary_errors(
        int failingSteps,
        bool aggregateTerminationFailure)
    {
        List<string> calls = [];
        Exception terminationFailure = aggregateTerminationFailure
            ? new AggregateException(new Win32Exception(5))
            : new Win32Exception(5);

        WindowsJobProcessPlatform.CleanupFailedStart(
            () => CleanupStep("terminate", 1, terminationFailure),
            () => CleanupStep("dispose-process", 2, new ObjectDisposedException("process")),
            () => CleanupStep("dispose-job", 4, new InvalidOperationException("Injected job disposal failure.")));

        Assert.Equal(["terminate", "dispose-process", "dispose-job"], calls);

        void CleanupStep(string name, int flag, Exception failure)
        {
            calls.Add(name);
            if ((failingSteps & flag) != 0)
            {
                throw failure;
            }
        }
    }

    [Fact]
    public void Start_preserves_the_original_assignment_exception_after_cleanup()
    {
        InvalidOperationException original = new("Injected assignment failure.");
        Process? observer = null;
        SafeProcessHandle? ownedProcessHandle = null;
        SafeJobHandle? ownedJob = null;
        WindowsJobProcessPlatform platform = new((job, process) =>
        {
            ownedProcessHandle = process.SafeHandle;
            ownedJob = job;
            observer = ObserveProcess(process);
            throw original;
        });

        try
        {
            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
                platform.Start(BlockedRipgrepRequest()));

            Assert.Same(original, actual);
            Process observed = observer ?? throw new InvalidOperationException("The process was not observed.");
            Assert.True(observed.WaitForExit(5_000));
            Assert.True(ownedProcessHandle?.IsClosed);
            Assert.True(ownedJob?.IsClosed);
        }
        finally
        {
            StopAndDisposeOwnedProcess(observer);
        }
    }

    private static ProcessStartRequest BlockedRipgrepRequest() => new(
        BundledRipgrep.RequireExecutable(),
        ["--no-config", "needle", "-"],
        KeepStandardInputOpen: true);

    private static Process StartBlockedRipgrep()
    {
        ProcessStartInfo startInfo = new(BundledRipgrep.RequireExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "--no-config", "needle", "-" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the test ripgrep.");
    }

    private static Process ObserveProcess(Process process)
    {
        Process observer = Process.GetProcessById(process.Id);
        // Own an independent handle before Start disposes its Process, avoiding
        // a later PID lookup or a handle reopened after the process has exited.
        _ = observer.Handle;
        return observer;
    }

    private static void StopAndDisposeOwnedProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5_000);
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
