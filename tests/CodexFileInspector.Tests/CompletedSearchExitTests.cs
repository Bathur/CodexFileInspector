using System.Text;
using System.Text.Json;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Globbing;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class CompletedSearchExitTests
{
    [Theory]
    [InlineData("matches", 2)]
    [InlineData("matches", 17)]
    [InlineData("files_with_matches", 2)]
    [InlineData("files_with_matches", 17)]
    [InlineData("glob", 2)]
    [InlineData("glob", 17)]
    public async Task Buffered_pages_cannot_hide_a_completed_process_failure(string mode, int exitCode)
    {
        ToolExecutionException failure = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
        {
            await ReadPage(mode, exitCode, completed: true);
        });

        Assert.Equal(ToolErrorCodes.RipgrepFailed, failure.Code);
        Assert.Equal(exitCode, failure.Actual);
    }

    [Theory]
    [InlineData("matches")]
    [InlineData("files_with_matches")]
    [InlineData("glob")]
    public async Task Completed_success_can_page_without_claiming_the_unread_total(string mode)
    {
        PageResult page = await ReadPage(mode, 0, completed: true);

        Assert.Equal(ToolStatus.Success, page.Status);
        Assert.Equal(1, page.Returned);
        Assert.Null(page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(1, page.Next);
        Assert.Null(page.Warnings);
    }

    [Theory]
    [InlineData("matches")]
    [InlineData("files_with_matches")]
    [InlineData("glob")]
    public async Task Ordinary_active_process_pagination_keeps_its_existing_exit_policy(string mode)
    {
        PageResult page = await ReadPage(mode, 17, completed: false);

        Assert.Equal(ToolStatus.Success, page.Status);
        Assert.Equal(1, page.Returned);
        Assert.Null(page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(1, page.Next);
    }

    [Theory]
    [InlineData("matches")]
    [InlineData("files_with_matches")]
    [InlineData("glob")]
    public async Task Completed_traversal_failures_keep_evidence_and_suppress_continuation(string mode)
    {
        PageResult page = await ReadPage(mode, 2, completed: true,
            standardError: "rg: C:\\root\\locked.txt: Access is denied. (os error 5)\n");

        Assert.Equal(ToolStatus.Partial, page.Status);
        Assert.Equal(1, page.Returned);
        Assert.Null(page.Total);
        Assert.Null(page.HasMore);
        Assert.Null(page.Next);
        Assert.Equal(ToolWarningCodes.RipgrepTraversal, Assert.Single(page.Warnings!).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_query_errors_keep_their_original_field_and_classification(bool glob)
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner inner = new()
        {
            ExitCode = 2,
            StandardError = glob
                ? "rg: error parsing glob '[': unclosed character class; missing ']'\n"
                : "rg: regex parse error:\n    (\nerror: unclosed group\n",
        };
        SnapshotTaggedRunner runner = new(inner, completed: true);
        RipgrepInvocationBuilder builder = new(platform);

        ToolExecutionException failure = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
        {
            if (glob)
            {
                await new GlobService(platform, builder, runner).FindAsync(
                    new GlobRequest(@"C:\root", ["["]), CancellationToken.None);
            }
            else
            {
                await new GrepService(platform, builder, runner).SearchAsync(
                    new GrepRequest(@"C:\root", "(", PatternKind.Regex), CancellationToken.None);
            }
        });

        Assert.Equal(ToolErrorCodes.InvalidPattern, failure.Code);
        Assert.Equal(glob ? "include_globs" : "pattern", failure.Field);
        if (glob)
        {
            Assert.Equal(0, failure.Index);
        }
        Assert.False(ToolExceptionMapper.Map(failure, @"C:\root").Retryable);
    }

    private static async ValueTask<PageResult> ReadPage(
        string mode,
        int exitCode,
        bool completed,
        string standardError = "")
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner inner = new()
        {
            ExitCode = exitCode,
            StandardError = standardError,
            Records = mode == "glob"
                ? [Encoding.UTF8.GetBytes(@"C:\root\a.txt"), Encoding.UTF8.GetBytes(@"C:\root\b.txt")]
                : MatchRecords(@"C:\root\a.txt", @"C:\root\b.txt"),
        };
        SnapshotTaggedRunner runner = new(inner, completed);
        RipgrepInvocationBuilder builder = new(platform);
        if (mode == "glob")
        {
            GlobOutput output = await new GlobService(platform, builder, runner).FindAsync(
                new GlobRequest(@"C:\root", ["**/*.txt"], MaxResults: 1), CancellationToken.None);
            return new PageResult(output.Status, output.ReturnedResults, output.TotalResults,
                output.HasMore, output.NextResultOffset, output.Warnings);
        }

        GrepOutput matches = await new GrepService(platform, builder, runner).SearchAsync(
            new GrepRequest(@"C:\root", "MATCH", PatternKind.Literal,
                OutputMode: mode == "matches" ? GrepOutputMode.Matches : GrepOutputMode.FilesWithMatches,
                MaxResults: 1), CancellationToken.None);
        return new PageResult(matches.Status, matches.ReturnedResults, matches.TotalResults,
            matches.HasMore, matches.NextResultOffset, matches.Warnings);
    }

    private static IReadOnlyList<byte[]> MatchRecords(params string[] paths)
    {
        List<byte[]> records = [];
        foreach (string path in paths)
        {
            records.Add(JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "begin", data = new { path = new { text = path } },
            }));
            records.Add(JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "match",
                data = new
                {
                    path = new { text = path },
                    lines = new { text = "MATCH\n" },
                    line_number = 1,
                    submatches = new[] { new { start = 0, end = 5 } },
                },
            }));
            records.Add(JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "end",
                data = new
                {
                    path = new { text = path },
                    stats = new { matched_lines = 1, matches = 1 },
                },
            }));
        }
        return records;
    }

    private sealed class SnapshotTaggedRunner(TestRipgrepRunner inner, bool completed) : IRipgrepRunner
    {
        public async ValueTask<RipgrepRunResult> RunAsync(
            RipgrepRunRequest request,
            RipgrepRecordHandler onRecord,
            CancellationToken cancellationToken)
        {
            RipgrepRunResult result = await inner.RunAsync(request, onRecord, cancellationToken);
            return result with { CompletedBeforeReading = completed };
        }
    }

    private sealed record PageResult(
        ToolStatus Status,
        int? Returned,
        long? Total,
        bool? HasMore,
        int? Next,
        IReadOnlyList<ToolWarning>? Warnings);
}
