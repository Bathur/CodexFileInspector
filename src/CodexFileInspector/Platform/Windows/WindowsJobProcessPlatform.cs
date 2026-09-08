using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexFileInspector.Platform.Windows;

internal sealed class WindowsJobProcessPlatform : IProcessPlatform
{
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

            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The child process did not start.");
            if (!request.KeepStandardInputOpen)
            {
                process.StandardInput.Close();
            }

            if (!NativeMethods.AssignProcessToJobObject(job, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the child process to its cleanup job.");
            }

            return new WindowsRunningProcess(process, job);
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }

            job.Dispose();
            throw;
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

    private sealed class WindowsRunningProcess(Process process, SafeJobHandle job) : IRunningProcess
    {
        private bool _disposed;

        public int Id => process.Id;

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
