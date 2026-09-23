using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexFileInspector.Platform.Windows;

internal sealed class WindowsJobProcessPlatform : IProcessPlatform
{
    private const int ErrorAccessDenied = 5;
    private readonly Func<SafeJobHandle, Process, int> _assignProcess;

    public WindowsJobProcessPlatform()
        : this(AssignProcess)
    {
    }

    internal WindowsJobProcessPlatform(Func<SafeJobHandle, Process, int> assignProcess)
    {
        _assignProcess = assignProcess ?? throw new ArgumentNullException(nameof(assignProcess));
    }

    public IRunningProcess Start(ProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        SafeJobHandle job = CreateKillOnCloseJob();
        Process? process = null;
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (string argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            try
            {
                process = Process.Start(startInfo) ?? throw new InvalidOperationException("The child process did not start.");
            }
            catch (Win32Exception exception)
            {
                throw new ProcessStartFailureException(startInfo.WorkingDirectory, exception);
            }
            if (!request.KeepStandardInputOpen)
            {
                process.StandardInput.Close();
            }

            int? completedExitCode = AssignOrObserveExit(job, process, _assignProcess);
            return new WindowsRunningProcess(process, job, completedExitCode);
        }
        catch
        {
            CleanupFailedStart(
                () => process?.Kill(entireProcessTree: true),
                () => process?.Dispose(),
                job.Dispose);
            throw;
        }
    }

    internal static int? AssignOrObserveExit(
        SafeJobHandle job,
        Process process,
        Func<SafeJobHandle, Process, int> assignProcess)
    {
        // Production starts only bundled rg, without subprocess-producing
        // options. An arbitrary launcher could leave live descendants behind.
        if (TryGetCompletedExitCode(process) is int completedExitCode)
        {
            return completedExitCode;
        }

        int error = assignProcess(job, process);
        if (error == 0)
        {
            return null;
        }

        // A completed bundled rg can lose the Start -> Assign race with error 5.
        // Other assignment errors can themselves terminate a process, so exit
        // alone must not turn every Job failure into an accepted search result.
        if (error == ErrorAccessDenied && TryGetCompletedExitCode(process) is int racedExitCode)
        {
            return racedExitCode;
        }

        throw new Win32Exception(error, "Could not assign the child process to its cleanup job.");
    }

    private static int AssignProcess(SafeJobHandle job, Process process) =>
        NativeMethods.AssignProcessToJobObject(job, process.SafeHandle)
            ? 0
            : Marshal.GetLastPInvokeError();

    private static int? TryGetCompletedExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Failure to establish an exited state never authorizes bypassing
            // a failed assignment or replaces its captured native error.
            return null;
        }
    }

    internal static void CleanupFailedStart(Action terminate, Action disposeProcess, Action disposeJob)
    {
        // This runs only while propagating the original start/assignment error.
        // Attempt every cleanup step even when an earlier one fails, and keep
        // the original exception rather than a secondary cleanup exception.
        try
        {
            TryCleanup(terminate);
        }
        finally
        {
            try
            {
                TryCleanup(disposeProcess);
            }
            finally
            {
                TryCleanup(disposeJob);
            }
        }
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception)
        {
            // Best effort after another failure; remaining handles still need
            // their own cleanup attempt before that original failure escapes.
        }
    }

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        SafeJobHandle job = new(NativeMethods.CreateJobObject(0, null));
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a child-process cleanup job.");
        }

        NativeMethods.JobObjectExtendedLimitInformation information = new();
        information.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        uint informationSize = checked((uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>());

        if (!NativeMethods.SetInformationJobObject(
                job,
                NativeMethods.JobObjectInformationClass.ExtendedLimitInformation,
                ref information,
                informationSize))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "Could not configure the child-process cleanup job.");
        }

        return job;
    }

    private sealed class WindowsRunningProcess(Process process, SafeJobHandle job, int? completedExitCode) : IRunningProcess
    {
        private bool _disposed;

        public int Id => process.Id;

        public int? CompletedExitCode => completedExitCode;

        public StreamReader StandardOutput => process.StandardOutput;

        public StreamReader StandardError => process.StandardError;

        public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        public async ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!process.HasExited && !NativeMethods.TerminateJobObject(job, 1))
            {
                int error = Marshal.GetLastPInvokeError();
                if (!process.HasExited)
                {
                    throw new Win32Exception(error, "Could not terminate the child-process cleanup job.");
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!process.HasExited)
            {
                job.Dispose();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            else
            {
                job.Dispose();
            }

            process.Dispose();
        }
    }
}
