using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.DirectoryListing;
using CodexFileInspector.Errors;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ErrorContractTests
{
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
