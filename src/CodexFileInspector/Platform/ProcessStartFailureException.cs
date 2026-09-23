using System.ComponentModel;

namespace CodexFileInspector.Platform;

// Carries context only for the native Process.Start call, not Job setup or
// failures after a child has started. Public tool calls normalize the explicit
// working directory before constructing the process request.
internal sealed class ProcessStartFailureException(
    string workingDirectory,
    Win32Exception nativeFailure) : Exception("The child process could not start.", nativeFailure)
{
    public string WorkingDirectory { get; } = workingDirectory;

    public Win32Exception NativeFailure { get; } = nativeFailure;
}
