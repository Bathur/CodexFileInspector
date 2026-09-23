using System.Text;
using System.Text.Json;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class SearchFailureTests
{
    [Theory]
    [InlineData("(")]
    [InlineData("\\n")]
    [InlineData("a{10000000}")]
    [InlineData("\\x00")]
    public async Task Pinned_regex_compilation_failures_are_non_retryable_query_errors(string pattern)
    {
        using TestWorkspace workspace = new();
        File.WriteAllText(workspace.PathFor("a.txt"), "MATCH");

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Search(workspace.Root, pattern, PatternKind.Regex));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal("pattern", exception.Field);
        Assert.False(ToolExceptionMapper.Map(exception, workspace.Root).Retryable);
    }

    [Theory]
    [InlineData(GrepOutputMode.Matches)]
    [InlineData(GrepOutputMode.FilesWithMatches)]
    public async Task A_locked_file_keeps_both_small_and_complete_searches_partial(GrepOutputMode mode)
    {
        using TestWorkspace workspace = new();
        foreach (string name in new[] { "a-locked.txt", "b.txt", "c.txt" })
        {
            File.WriteAllText(workspace.PathFor(name), "MATCH");
        }

        using FileStream locked = File.Open(
            workspace.PathFor("a-locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None);

        GrepOutput first = await Search(workspace.Root, "MATCH", PatternKind.Literal, mode, maxResults: 1);
        GrepOutput complete = await Search(workspace.Root, "MATCH", PatternKind.Literal, mode, maxResults: 10);

        AssertPartial(first, 1);
        AssertPartial(complete, 2);
    }

    [Theory]
    [InlineData("regex parse error.txt", PatternKind.Literal)]
    [InlineData("regex parse error.txt", PatternKind.Regex)]
    [InlineData("error parsing glob.txt", PatternKind.Literal)]
    [InlineData("error parsing glob.txt", PatternKind.Regex)]
    public async Task Error_words_in_a_locked_filename_do_not_replace_completed_matches(
        string filename,
        PatternKind patternKind)
    {
        using TestWorkspace workspace = new();
        string lockedPath = workspace.PathFor(filename);
        File.WriteAllText(lockedPath, "MATCH");
        File.WriteAllText(workspace.PathFor("a.txt"), "MATCH");
        using FileStream locked = File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        GrepOutput output = await Search(workspace.Root, "MATCH", patternKind);

        AssertPartial(output, 1);
        Assert.Equal("a.txt", Path.GetFileName(Assert.Single(output.Blocks!).Path));
    }

    [Theory]
    [InlineData("[", false)]
    [InlineData("a'[", false)]
    [InlineData("a': [", false)]
    [InlineData("[", true)]
    [InlineData("a': [", true)]
    public async Task Pinned_glob_errors_identify_the_exact_input_item(string invalidGlob, bool exclude)
    {
        using TestWorkspace workspace = new();
        WindowsFileSystemPlatform platform = new();
        GlobService service = new(
            platform,
            new RipgrepInvocationBuilder(platform),
            new RipgrepRunner(new WindowsJobProcessPlatform()));
        GlobRequest request = new(
            workspace.Root,
            exclude ? ["a", "**/*.txt"] : ["a", invalidGlob],
            exclude ? ["a", invalidGlob] : null,
            RespectIgnoreFiles: false);

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await service.FindAsync(request, CancellationToken.None));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal(exclude ? "exclude_globs" : "include_globs", exception.Field);
        Assert.Equal(1, exception.Index);
    }

    [Theory]
    [InlineData("rg: error parsing glob '[unknown': unclosed character class; missing ']'\n", false)]
    [InlineData("rg: error parsing glob '[truncated", true)]
    [InlineData("rg: error parsing glob 'a': [", true)]
    public void An_unmapped_or_truncated_glob_diagnostic_does_not_guess_an_index(
        string standardError,
        bool stderrTruncated)
    {
        RipgrepRunResult run = new(2, 0, false, false, standardError, stderrTruncated);

        ToolExecutionException exception = Assert.IsType<ToolExecutionException>(
            RipgrepFailureClassifier.QueryFailure(run, searchesContents: false, ["a"], ["b"]));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Null(exception.Field);
        Assert.Null(exception.Index);
    }

    [Fact]
    public void A_compilation_error_after_a_path_diagnostic_is_still_a_query_failure()
    {
        RipgrepRunResult run = new(
            2, 0, false, false,
            "rg: C:\\root\\.ignore: Access is denied. (os error 5)\nrg: regex parse error:\n    (\nerror: unclosed group\n",
            false);

        ToolExecutionException exception = Assert.IsType<ToolExecutionException>(
            RipgrepFailureClassifier.QueryFailure(run, searchesContents: true, [], []));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal("pattern", exception.Field);
    }

    [Theory]
    [InlineData(GrepOutputMode.Matches, true, false)]
    [InlineData(GrepOutputMode.Matches, true, true)]
    [InlineData(GrepOutputMode.Matches, false, false)]
    [InlineData(GrepOutputMode.FilesWithMatches, true, false)]
    [InlineData(GrepOutputMode.FilesWithMatches, true, true)]
    [InlineData(GrepOutputMode.FilesWithMatches, false, false)]
    public async Task Early_stopped_grep_uses_observed_path_errors_independently_of_the_exit_code(
        GrepOutputMode mode,
        bool traversalFailure,
        bool stderrTruncated)
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = MatchRecords(@"C:\root\b.txt", @"C:\root\c.txt"),
            ExitCode = -1,
            StandardError = DiagnosticText(traversalFailure),
            StandardErrorTruncated = stderrTruncated,
        };
        GrepService service = new(platform, new RipgrepInvocationBuilder(platform), runner);

        GrepOutput output = await service.SearchAsync(
            new GrepRequest(@"C:\root", "MATCH", PatternKind.Literal, OutputMode: mode, MaxResults: 1),
            CancellationToken.None);

        if (traversalFailure)
        {
            AssertPartial(output, 1);
        }
        else
        {
            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.Equal(1, output.ReturnedResults);
            Assert.True(output.HasMore);
            Assert.Equal(1, output.NextResultOffset);
            Assert.Null(output.Warnings);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Early_stopped_glob_uses_observed_path_errors_independently_of_the_exit_code(
        bool traversalFailure,
        bool stderrTruncated)
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = [Encoding.UTF8.GetBytes(@"C:\root\b.txt"), Encoding.UTF8.GetBytes(@"C:\root\c.txt")],
            ExitCode = -1,
            StandardError = DiagnosticText(traversalFailure),
            StandardErrorTruncated = stderrTruncated,
        };
        GlobService service = new(platform, new RipgrepInvocationBuilder(platform), runner);

        GlobOutput output = await service.FindAsync(
            new GlobRequest(@"C:\root", ["**/*"], MaxResults: 1),
            CancellationToken.None);

        Assert.Equal(1, output.ReturnedResults);
        if (traversalFailure)
        {
            Assert.Equal(ToolStatus.Partial, output.Status);
            Assert.Null(output.TotalResults);
            Assert.Null(output.HasMore);
            Assert.Null(output.NextResultOffset);
            Assert.Equal(ToolWarningCodes.RipgrepTraversal, Assert.Single(output.Warnings!).Code);
        }
        else
        {
            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.True(output.HasMore);
            Assert.Equal(1, output.NextResultOffset);
            Assert.Null(output.Warnings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unclassified_startup_failure_is_not_reported_as_partial_traversal(bool grep)
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            ExitCode = 2,
            StandardError = "rg: failed to get current working directory: stale working directory\n",
        };
        RipgrepInvocationBuilder builder = new(platform);

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
        {
            if (grep)
            {
                await new GrepService(platform, builder, runner).SearchAsync(
                    new GrepRequest(@"C:\root", "MATCH", PatternKind.Literal),
                    CancellationToken.None);
            }
            else
            {
                await new GlobService(platform, builder, runner).FindAsync(
                    new GlobRequest(@"C:\root", ["**/*"]),
                    CancellationToken.None);
            }
        });

        Assert.Equal(ToolErrorCodes.RipgrepFailed, exception.Code);
    }

    private static string DiagnosticText(bool traversalFailure) => traversalFailure
        ? "rg: C:\\root\\regex parse error.txt: Access is denied. (os error 5)\n"
        : "note: regex parse error and error parsing glob are diagnostic descriptions\n";

    private static void AssertPartial(GrepOutput output, int returnedResults)
    {
        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Equal(returnedResults, output.ReturnedResults);
        Assert.Null(output.TotalResults);
        Assert.Null(output.HasMore);
        Assert.Null(output.NextResultOffset);
        Assert.Equal(ToolWarningCodes.RipgrepTraversal, Assert.Single(output.Warnings!).Code);
    }

    private static IReadOnlyList<byte[]> MatchRecords(params string[] paths)
    {
        List<byte[]> records = [];
        foreach (string path in paths)
        {
            records.Add(JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "begin",
                data = new { path = new { text = path } },
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
                data = new { path = new { text = path }, stats = new { matched_lines = 1, matches = 1 } },
            }));
        }

        return records;
    }

    private static ValueTask<GrepOutput> Search(
        string path,
        string pattern,
        PatternKind patternKind,
        GrepOutputMode mode = GrepOutputMode.Matches,
        int maxResults = ToolBudgets.GrepResultsDefault)
    {
        WindowsFileSystemPlatform platform = new();
        GrepRequest request = new ToolRequestValidator(platform).Validate(new GrepRequest(
            path,
            pattern,
            patternKind,
            OutputMode: mode,
            RespectIgnoreFiles: false,
            MaxResults: maxResults));
        GrepService service = new(
            platform,
            new RipgrepInvocationBuilder(platform),
            new RipgrepRunner(new WindowsJobProcessPlatform()));
        return service.SearchAsync(request, CancellationToken.None);
    }
}
