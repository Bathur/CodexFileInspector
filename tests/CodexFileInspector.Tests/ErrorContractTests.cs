using System.ComponentModel;
using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.DirectoryListing;
using CodexFileInspector.Errors;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ErrorContractTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_windows_name_is_a_non_retryable_path_error(bool useWin32Exception)
    {
        const string path = "C:\\work\\bad\u0001name.txt";
        Exception exception = useWin32Exception
            ? new Win32Exception(123, "Native error details")
            : new IOException("Native error details", unchecked((int)0x8007007B));

        ToolError error = ToolExceptionMapper.Map(exception, path);

        Assert.Equal(ToolErrorCodes.InvalidPath, error.Code);
        Assert.Equal("path", error.Field);
        Assert.Equal(path, error.Path);
        Assert.Equal(123, error.OsCode);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("Native error details", error.Message);
    }

    [Theory]
    [InlineData(32, ToolErrorCodes.SharingViolation)]
    [InlineData(33, ToolErrorCodes.SharingViolation)]
    [InlineData(1117, ToolErrorCodes.IoError)]
    public void Other_operating_system_errors_keep_their_retryable_classification(int osCode, string expectedCode)
    {
        Exception[] exceptions =
        [
            new Win32Exception(osCode, "Native error details"),
            new IOException("Native error details", unchecked((int)(0x80070000u | (uint)osCode))),
        ];

        Assert.All(exceptions, exception =>
        {
            ToolError error = ToolExceptionMapper.Map(exception, @"C:\work\file.txt");
            Assert.Equal(expectedCode, error.Code);
            Assert.Equal(osCode, error.OsCode);
            Assert.Null(error.Field);
            Assert.True(error.Retryable);
        });
    }

    [Fact]
    public void Non_win32_hresult_with_invalid_name_low_word_remains_an_io_error()
    {
        IOException exception = new("Other failure domain", unchecked((int)0x8004007B));

        ToolError error = ToolExceptionMapper.Map(exception, @"C:\work\file.txt");

        Assert.Equal(ToolErrorCodes.IoError, error.Code);
        Assert.Null(error.Field);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void Error_messages_are_utf8_bounded_and_keep_registry_retryability()
    {
        ToolExecutionException exception = new(
            ToolErrorCodes.ChangedDuringRead,
            new string('界', 1000));

        ToolError error = ToolExceptionMapper.Map(exception, @"C:\file.txt");

        Assert.InRange(Encoding.UTF8.GetByteCount(error.Message), 1, ToolBudgets.ErrorMessageBytes);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void Warning_collector_caps_items_and_reports_omissions()
    {
        WarningCollector collector = new();
        for (int index = 0; index < 25; index++)
        {
            collector.Add("warning", new string('界', 1000), @"C:\file.txt");
        }

        Assert.InRange(collector.Items.Count, 1, ToolBudgets.WarningCount);
        Assert.Equal(25, collector.Items.Count + collector.Omitted);
        Assert.All(
            collector.Items,
            warning => Assert.InRange(
                Encoding.UTF8.GetByteCount(warning.Message),
                1,
                ToolBudgets.WarningMessageBytes));
    }
}
