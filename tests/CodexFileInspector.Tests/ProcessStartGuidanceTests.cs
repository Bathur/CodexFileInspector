using System.ComponentModel;
using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ProcessStartGuidanceTests
{
    [Theory]
    [InlineData(249, 267, false)]
    [InlineData(250, 267, true)]
    [InlineData(251, 267, true)]
    [InlineData(250, 206, true)]
    [InlineData(250, 1117, true)]
    public void Only_long_native_start_io_failures_receive_the_recovery_hint(
        int cwdLength, int osCode, bool hinted)
    {
        const string requestPath = @"C:\requested-file.txt";
        string cwd = @"C:\" + new string('d', cwdLength - 3);
        Win32Exception native = new(osCode, "Private native error details");
        ProcessStartFailureException failure = new(cwd, native);

        ToolError error = ToolExceptionMapper.Map(failure, requestPath);

        Assert.True(ToolExceptionMapper.IsExpected(failure));
        Assert.Same(native, failure.InnerException);
        Assert.Equal(ToolErrorCodes.IoError, error.Code);
        Assert.Equal(osCode, error.OsCode);
        Assert.Equal(requestPath, error.Path);
        Assert.Equal(!hinted, error.Retryable);
        Assert.Null(error.Limit);
        Assert.Null(error.Actual);
        Assert.DoesNotContain("Private native error details", error.Message);
        Assert.DoesNotContain(cwd, error.Message);
        if (hinted)
        {
            Assert.Contains("may trigger", error.Message);
            Assert.Contains("Do not repeat this request unchanged", error.Message);
            Assert.Contains("include_globs/exclude_globs", error.Message);
            Assert.Contains("read_file", error.Message);
            Assert.Contains("list_directory", error.Message);
        }
        else
        {
            Assert.DoesNotContain("working directory", error.Message);
        }
        Assert.InRange(Encoding.UTF8.GetByteCount(error.Message), 1, ToolBudgets.ErrorMessageBytes);
        Assert.True(ToolErrorRegistry.All[ToolErrorCodes.IoError].Retryable);
    }

    [Theory]
    [InlineData(32, ToolErrorCodes.SharingViolation, true)]
    [InlineData(33, ToolErrorCodes.SharingViolation, true)]
    [InlineData(123, ToolErrorCodes.InvalidPath, false)]
    public void Specific_native_error_classes_keep_their_existing_policy(
        int osCode, string expectedCode, bool retryable)
    {
        Win32Exception native = new(osCode, "Private native error details");
        ToolError expected = ToolExceptionMapper.Map(native, @"C:\request.txt");
        ProcessStartFailureException failure = new(@"C:\" + new string('d', 300), native);

        ToolError actual = ToolExceptionMapper.Map(failure, @"C:\request.txt");

        Assert.Equal(expected, actual);
        Assert.Equal(expectedCode, actual.Code);
        Assert.Equal(retryable, actual.Retryable);
    }

    [Fact]
    public void Long_request_paths_do_not_stand_in_for_process_start_context()
    {
        string path = @"C:\" + new string('d', 300) + "\file.txt";
        Exception[] failures =
        [
            new Win32Exception(267, "Job or another native operation failed"),
            new IOException("File I/O failed", unchecked((int)0x8007010B)),
            new ProcessStartFailureException(@"C:\short", new Win32Exception(267)),
            new ProcessStartFailureException(string.Empty, new Win32Exception(267)),
        ];

        foreach (Exception failure in failures)
        {
            ToolError error = ToolExceptionMapper.Map(failure, path);
            Assert.Equal(ToolErrorCodes.IoError, error.Code);
            Assert.Equal(267, error.OsCode);
            Assert.Equal(path, error.Path);
            Assert.True(error.Retryable);
            Assert.DoesNotContain("working directory", error.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_threshold_counts_actual_utf16_units_including_a_trailing_separator(bool trailingSeparator)
    {
        string prefix = @"C:\" + string.Concat(Enumerable.Repeat("😀", 123));
        Assert.Equal(249, prefix.Length);
        string cwd = prefix + (trailingSeparator ? "\\" : "x");
        ProcessStartFailureException failure = new(cwd, new Win32Exception(267));

        ToolError error = ToolExceptionMapper.Map(failure, @"C:\file.txt");

        Assert.False(error.Retryable);
        Assert.Contains("250 UTF-16 code units", error.Message);
    }

    [Fact]
    public async Task A_real_250_character_working_directory_can_still_start_successfully()
    {
        using TestWorkspace workspace = new();
        string cwd = CreateDirectoryOfLength(workspace.Root, 250);
        WindowsJobProcessPlatform platform = new();
        await using IRunningProcess process = platform.Start(new ProcessStartRequest(
            BundledRipgrep.RequireExecutable(), ["--no-config", "needle", "-"], cwd, KeepStandardInputOpen: true));

        Assert.True(process.Id > 0);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await process.TerminateAsync(timeout.Token);
    }

    [Fact]
    public async Task A_real_long_working_directory_failure_keeps_its_native_cause_and_context()
    {
        using TestWorkspace workspace = new();
        string cwd = CreateDirectoryOfLength(workspace.Root, 317);
        WindowsJobProcessPlatform platform = new();

        IRunningProcess? unexpectedlyStarted = null;
        try
        {
            ProcessStartFailureException failure = Assert.Throws<ProcessStartFailureException>(() =>
                unexpectedlyStarted = platform.Start(new ProcessStartRequest(
                    BundledRipgrep.RequireExecutable(), ["--no-config", "needle", "-"], cwd, KeepStandardInputOpen: true)));

            Assert.Equal(cwd, failure.WorkingDirectory);
            Win32Exception native = Assert.IsType<Win32Exception>(failure.InnerException);
            ToolError error = ToolExceptionMapper.Map(failure, cwd);
            Assert.Equal(native.NativeErrorCode, error.OsCode);
            Assert.Equal(ToolErrorCodes.IoError, error.Code);
            Assert.False(error.Retryable);
            Assert.True(Directory.Exists(cwd));
        }
        finally
        {
            if (unexpectedlyStarted is not null)
            {
                await unexpectedlyStarted.DisposeAsync();
            }
        }
    }

    internal static string CreateDirectoryOfLength(string root, int length)
    {
        Assert.True(root.Length < length - 1, "The fixture root must leave room for the requested cwd length.");
        string path = root;
        while (length - path.Length - 1 > 100)
        {
            path = Path.Combine(path, new string('d', 99));
        }
        path = Path.Combine(path, new string('e', length - path.Length - 1));
        Directory.CreateDirectory(path);
        Assert.Equal(length, path.Length);
        return path;
    }
}
