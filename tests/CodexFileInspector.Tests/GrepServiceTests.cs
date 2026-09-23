using System.Text;
using System.Text.Json;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class GrepServiceTests
{
    [Fact]
    public async Task Literal_and_regex_patterns_are_not_guessed()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("values.txt"), "a.b\naXb\n");

        GrepOutput literal = await Search(workspace.Root, "a.b", PatternKind.Literal);
        GrepOutput regex = await Search(workspace.Root, "a.b", PatternKind.Regex);

        Assert.Equal(1, literal.ReturnedResults);
        Assert.Equal(2, regex.ReturnedResults);
    }

    [Fact]
    public async Task Case_sensitivity_is_explicit()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("values.txt"), "Value\nvalue\n");

        GrepOutput sensitive = await Search(workspace.Root, "Value", PatternKind.Literal, caseSensitive: true);
        GrepOutput insensitive = await Search(workspace.Root, "Value", PatternKind.Literal, caseSensitive: false);

        Assert.Equal(1, sensitive.ReturnedResults);
        Assert.Equal(2, insensitive.ReturnedResults);
    }

    [Fact]
    public async Task Multiple_occurrences_on_one_line_consume_one_result_unit()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("todo.txt"), "TODO TODO TODO\n");

        GrepOutput output = await Search(workspace.Root, "TODO", PatternKind.Literal);

        GrepMatchBlock block = Assert.Single(output.Blocks!);
        GrepMatchLine match = Assert.Single(block.MatchLines);
        Assert.Equal(3, match.OccurrenceCount);
        Assert.Equal(1, output.ReturnedResults);
        Assert.Equal(1, output.TotalResults);
    }

    [Fact]
    public async Task Context_intervals_that_are_directly_adjacent_merge_within_one_file()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        string[] lines = Enumerable.Range(1, 8)
            .Select(index => index is 3 or 6 ? $"{index} MATCH" : index.ToString())
            .ToArray();
        File.WriteAllText(workspace.PathFor("context.txt"), string.Join('\n', lines));

        GrepOutput output = await Search(
            workspace.Root,
            "MATCH",
            PatternKind.Literal,
            contextLines: 1);

        GrepMatchBlock block = Assert.Single(output.Blocks!);
        Assert.Equal(2, block.StartLine);
        Assert.Equal(7, block.EndLine);
        Assert.Equal([3L, 6L], block.MatchLines.Select(match => match.LineNumber));
        Assert.False(block.ContextTruncated);
        Assert.Contains("L5: 5", block.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocks_from_different_files_never_merge()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("a.txt"), "MATCH");
        File.WriteAllText(workspace.PathFor("b.txt"), "MATCH");

        GrepOutput output = await Search(workspace.Root, "MATCH", PatternKind.Literal, contextLines: 2);

        Assert.Equal(2, output.Blocks!.Count);
        Assert.All(output.Blocks, block => Assert.Single(block.MatchLines));
    }

    [Fact]
    public async Task Pagination_counts_matching_lines_and_has_transparent_continuation()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("matches.txt"), "M\nM M\nM\n");

        GrepOutput first = await Search(workspace.Root, "M", PatternKind.Literal, maxResults: 1);
        GrepOutput second = await Search(
            workspace.Root,
            "M",
            PatternKind.Literal,
            resultOffset: first.NextResultOffset!.Value,
            maxResults: 5);

        Assert.Equal(1, first.ReturnedResults);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.NextResultOffset);
        Assert.Null(first.TotalResults);
        Assert.Equal(2, second.ReturnedResults);
        Assert.Equal(3, second.TotalResults);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task A_match_that_belongs_to_another_page_is_unmarked_context()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("page-context.txt"), "MATCH first\nMATCH second\nplain");

        GrepOutput output = await Search(
            workspace.Root,
            "MATCH",
            PatternKind.Literal,
            contextLines: 1,
            resultOffset: 1,
            maxResults: 1);

        GrepMatchBlock block = Assert.Single(output.Blocks!);
        Assert.Contains("L1: MATCH first", block.Content, StringComparison.Ordinal);
        GrepMatchLine selected = Assert.Single(block.MatchLines);
        Assert.Equal(2, selected.LineNumber);
        Assert.Equal(1, output.ReturnedResults);
    }

    [Fact]
    public async Task Match_centered_excerpt_keeps_a_match_near_the_end_of_a_long_line()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        string line = new string('x', 20_000) + "NEEDLE" + new string('y', 100);
        File.WriteAllText(workspace.PathFor("long.txt"), line);

        GrepOutput output = await Search(workspace.Root, "NEEDLE", PatternKind.Literal);

        GrepMatchBlock block = Assert.Single(output.Blocks!);
        Assert.Contains("NEEDLE", block.Content, StringComparison.Ordinal);
        Assert.Contains("…", block.Content, StringComparison.Ordinal);
        LineTruncation truncation = Assert.Single(block.LineTruncations);
        Assert.InRange(truncation.ReturnedBytes, 1, ToolBudgets.LineExcerptBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(line), truncation.TotalBytes);
    }

    [Fact]
    public async Task Files_with_matches_returns_unique_files_and_paginates_by_file()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("a.txt"), "x\nx");
        File.WriteAllText(workspace.PathFor("b.txt"), "x");
        File.WriteAllText(workspace.PathFor("c.txt"), "none");

        GrepOutput first = await Search(
            workspace.Root,
            "x",
            PatternKind.Literal,
            outputMode: GrepOutputMode.FilesWithMatches,
            maxResults: 1);
        GrepOutput second = await Search(
            workspace.Root,
            "x",
            PatternKind.Literal,
            outputMode: GrepOutputMode.FilesWithMatches,
            resultOffset: 1,
            maxResults: 5);

        Assert.Equal("a.txt", Path.GetFileName(Assert.Single(first.Paths!)));
        Assert.True(first.HasMore);
        Assert.Equal("b.txt", Path.GetFileName(Assert.Single(second.Paths!)));
        Assert.Equal(2, second.TotalResults);
    }

    [Fact]
    public async Task Count_distinguishes_files_lines_and_occurrences()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("a.txt"), "x x\nx");
        File.WriteAllText(workspace.PathFor("b.txt"), "x");

        GrepOutput output = await Search(
            workspace.Root,
            "x",
            PatternKind.Literal,
            outputMode: GrepOutputMode.Count);

        Assert.Equal(2, output.Counts!.Count);
        GrepFileCount a = output.Counts.Single(count => Path.GetFileName(count.Path) == "a.txt");
        Assert.Equal(2, a.MatchingLines);
        Assert.Equal(3, a.Occurrences);
        Assert.Equal(new GrepTotals(2, 3, 4), output.Totals);
        Assert.Equal(2, output.TotalResults);
    }

    [Fact]
    public async Task Count_paginates_records_but_keeps_exact_whole_search_totals()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("a.txt"), "x x");
        File.WriteAllText(workspace.PathFor("b.txt"), "x");

        GrepOutput output = await Search(
            workspace.Root,
            "x",
            PatternKind.Literal,
            outputMode: GrepOutputMode.Count,
            maxResults: 1);

        Assert.Single(output.Counts!);
        Assert.Equal(new GrepTotals(2, 2, 3), output.Totals);
        Assert.Equal(2, output.TotalResults);
        Assert.True(output.HasMore);
        Assert.Equal(1, output.NextResultOffset);
    }

    [Fact]
    public async Task Zero_matches_is_a_complete_success()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("value.txt"), "value");

        GrepOutput output = await Search(workspace.Root, "missing", PatternKind.Literal);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Empty(output.Blocks!);
        Assert.Equal(0, output.TotalResults);
        Assert.False(output.HasMore);
    }

    [Fact]
    public async Task Positive_offset_past_end_is_a_typed_error()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("value.txt"), "one match");

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Search(workspace.Root, "match", PatternKind.Literal, resultOffset: 1));

        Assert.Equal(ToolErrorCodes.ResultOffsetPastEnd, exception.Code);
        Assert.Equal(1, exception.Total);
    }

    [Fact]
    public async Task Ignore_hidden_and_explicit_globs_follow_the_approved_priority()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        Directory.CreateDirectory(workspace.PathFor(".hidden"));
        File.WriteAllText(workspace.PathFor(".gitignore"), "ignored.txt\n");
        File.WriteAllText(workspace.PathFor("ignored.txt"), "MATCH");
        File.WriteAllText(workspace.PathFor("visible.txt"), "MATCH");
        File.WriteAllText(workspace.PathFor(".hidden/hidden.txt"), "MATCH");

        GrepOutput defaultSearch = await Search(workspace.Root, "MATCH", PatternKind.Literal);
        GrepOutput explicitInclude = await Search(
            workspace.Root,
            "MATCH",
            PatternKind.Literal,
            includeGlobs: ["**/ignored.txt"]);
        GrepOutput hidden = await Search(
            workspace.Root,
            "MATCH",
            PatternKind.Literal,
            includeGlobs: ["**/*.txt"],
            excludeGlobs: ["**/ignored.txt"],
            includeHidden: true);

        Assert.Equal(["visible.txt"], MatchFileNames(defaultSearch));
        Assert.Equal(["ignored.txt"], MatchFileNames(explicitInclude));
        Assert.Equal(["hidden.txt", "visible.txt"], MatchFileNames(hidden).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Same_repository_parent_and_private_ignore_rules_are_loaded()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        Directory.CreateDirectory(workspace.PathFor(".git/info"));
        Directory.CreateDirectory(workspace.PathFor("src"));
        File.WriteAllText(workspace.PathFor(".gitignore"), "src/parent-ignored.txt\n");
        File.WriteAllText(workspace.PathFor(".git/info/exclude"), "src/private-ignored.txt\n");
        File.WriteAllText(workspace.PathFor("src/parent-ignored.txt"), "MATCH");
        File.WriteAllText(workspace.PathFor("src/private-ignored.txt"), "MATCH");
        File.WriteAllText(workspace.PathFor("src/visible.txt"), "MATCH");

        GrepOutput respected = await Search(
            workspace.PathFor("src"),
            "MATCH",
            PatternKind.Literal,
            respectIgnoreFiles: true);
        GrepOutput disabled = await Search(
            workspace.PathFor("src"),
            "MATCH",
            PatternKind.Literal,
            respectIgnoreFiles: false);

        Assert.Equal(["visible.txt"], MatchFileNames(respected));
        Assert.Equal(
            ["parent-ignored.txt", "private-ignored.txt", "visible.txt"],
            MatchFileNames(disabled));
    }

    [Fact]
    public async Task Invalid_regex_and_glob_are_typed_query_errors()
    {
        using TestWorkspace workspace = CreateFixtureRepository();

        ToolExecutionException regex = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Search(workspace.Root, "(", PatternKind.Regex));
        ToolExecutionException glob = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Search(workspace.Root, "x", PatternKind.Literal, includeGlobs: ["**/*.txt", "["]));

        Assert.Equal(ToolErrorCodes.InvalidPattern, regex.Code);
        Assert.Equal("pattern", regex.Field);
        Assert.Equal(ToolErrorCodes.InvalidPattern, glob.Code);
        Assert.Equal("include_globs", glob.Field);
        Assert.Equal(1, glob.Index);
    }

    [Fact]
    public async Task Context_is_removed_farthest_first_before_the_selected_match()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        string[] lines = Enumerable.Range(1, 50)
            .Select(index => index == 25 ? "MATCH" : $"{index:D2} {new string('x', 1500)}")
            .ToArray();
        File.WriteAllText(workspace.PathFor("large-context.txt"), string.Join('\n', lines));

        GrepOutput output = await Search(
            workspace.Root,
            "MATCH",
            PatternKind.Literal,
            contextLines: 20);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal(1, output.ReturnedResults);
        Assert.Contains(output.Blocks!, block => block.ContextTruncated);
        string content = string.Join('\n', output.Blocks!.Select(block => block.Content));
        Assert.Contains("MATCH", content, StringComparison.Ordinal);
        Assert.Contains("L24:", content, StringComparison.Ordinal);
        Assert.Contains("L26:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("L5:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("L45:", content, StringComparison.Ordinal);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GrepOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    public async Task Match_payload_budget_drops_trailing_result_units_with_continuation(int contextLines)
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        string line = "MATCH" + new string('\u0001', 3990);
        File.WriteAllText(workspace.PathFor("large-matches.txt"), string.Join('\n', Enumerable.Repeat(line, 10)));

        List<long> returnedLines = [];
        int offset = 0;
        for (int page = 0; page < 10; page++)
        {
            GrepOutput output = await Search(
                workspace.Root,
                "MATCH",
                PatternKind.Literal,
                contextLines: contextLines,
                resultOffset: offset,
                maxResults: 10);

            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.Equal(1, output.ReturnedResults);
            returnedLines.AddRange(output.Blocks!.SelectMany(block => block.MatchLines)
                .Select(match => match.LineNumber));
            Assert.InRange(
                ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GrepOutput),
                1,
                ToolBudgets.CanonicalResultBytes);
            if (contextLines > 0)
            {
                Assert.Contains(output.Blocks!, block => block.ContextTruncated);
            }

            if (output.HasMore is not true)
            {
                Assert.Null(output.NextResultOffset);
                break;
            }

            Assert.Equal("byte_budget", output.TruncatedBy);
            Assert.Equal(offset + output.ReturnedResults, output.NextResultOffset);
            Assert.True(output.NextResultOffset > offset);
            offset = output.NextResultOffset!.Value;
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(index => (long)index), returnedLines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Match_pages_keep_the_ripgrep_file_order_with_mixed_case_and_unicode(bool byteBudget)
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        string[] names = ["a.txt", "B.txt", "c.txt", "é.txt", "中.txt"];
        foreach (string name in names)
        {
            File.WriteAllText(workspace.PathFor(name), "MATCH");
        }

        GrepOutput files = await Search(
            workspace.Root,
            "MATCH",
            PatternKind.Literal,
            outputMode: GrepOutputMode.FilesWithMatches);
        GrepOutput whole = await Search(workspace.Root, "MATCH", PatternKind.Literal);
        Assert.Equal(files.Paths, whole.Blocks!.Select(block => block.Path));

        if (byteBudget)
        {
            string line = "MATCH" + new string('\u0001', 3990);
            foreach (string name in names)
            {
                File.WriteAllText(workspace.PathFor(name), line);
            }
        }

        List<string> returnedPaths = [];
        int offset = 0;
        for (int page = 0; page < names.Length; page++)
        {
            GrepOutput output = await Search(
                workspace.Root,
                "MATCH",
                PatternKind.Literal,
                resultOffset: offset,
                maxResults: byteBudget ? names.Length : 1);
            returnedPaths.AddRange(output.Blocks!.Select(block => block.Path));
            Assert.Equal(1, output.ReturnedResults);
            if (output.HasMore is not true)
            {
                Assert.Null(output.NextResultOffset);
                break;
            }

            Assert.Equal(byteBudget ? "byte_budget" : "result_limit", output.TruncatedBy);
            Assert.Equal(offset + 1, output.NextResultOffset);
            offset = output.NextResultOffset!.Value;
        }

        Assert.Equal(files.Paths, returnedPaths);
    }

    [Theory]
    [InlineData(GrepOutputMode.FilesWithMatches, false)]
    [InlineData(GrepOutputMode.FilesWithMatches, true)]
    [InlineData(GrepOutputMode.Count, false)]
    [InlineData(GrepOutputMode.Count, true)]
    public async Task Minimum_record_budget_failure_never_returns_a_zero_progress_success(
        GrepOutputMode outputMode,
        bool incomplete)
    {
        string root = NearRecordLimitRoot();
        string path = root + "\\a";
        OutputBudget.EnsurePathFits(root);
        OutputBudget.EnsurePathFits(path);
        Assert.InRange(
            JsonSerializer.SerializeToUtf8Bytes(new GrepFileCount(path, 1, 1)).Length,
            1,
            ToolBudgets.PathRecordBytes);
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = CreateSingleMatchEvents(path, "MATCH"),
            ExitCode = incomplete ? 2 : 0,
            StandardError = incomplete ? "rg: C:\\other: Access is denied. (os error 5)" : string.Empty,
        };
        GrepService service = new(platform, new RipgrepInvocationBuilder(platform), runner);
        GrepRequest request = new(root, "MATCH", PatternKind.Literal, OutputMode: outputMode);

        if (!incomplete)
        {
            ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
                await service.SearchAsync(request, CancellationToken.None));

            Assert.Equal(ToolErrorCodes.OutputRecordTooLarge, exception.Code);
            return;
        }

        GrepOutput output = await service.SearchAsync(request, CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Equal(0, output.ReturnedResults);
        Assert.Null(output.TotalResults);
        Assert.Null(output.HasMore);
        Assert.Null(output.NextResultOffset);
        Assert.NotEmpty(output.Warnings!);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GrepOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Fact]
    public async Task Byte_representation_event_makes_the_result_partial()
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        string begin = "{\"type\":\"begin\",\"data\":{\"path\":{\"text\":\"C:\\\\root\\\\bad.bin\"}}}";
        string match = "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"C:\\\\root\\\\bad.bin\"},\"lines\":{\"bytes\":\"/w==\"},\"line_number\":1,\"absolute_offset\":0,\"submatches\":[{\"match\":{\"text\":\"x\"},\"start\":0,\"end\":1}]}}";
        string end = "{\"type\":\"end\",\"data\":{\"path\":{\"text\":\"C:\\\\root\\\\bad.bin\"},\"binary_offset\":0,\"stats\":{\"matched_lines\":1,\"matches\":1}}}";
        TestRipgrepRunner runner = new()
        {
            Records = [Encoding.UTF8.GetBytes(begin), Encoding.UTF8.GetBytes(match), Encoding.UTF8.GetBytes(end)],
        };
        GrepService service = new(platform, new RipgrepInvocationBuilder(platform), runner);

        GrepOutput output = await service.SearchAsync(
            new GrepRequest(@"C:\root", "x", PatternKind.Literal),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Null(output.TotalResults);
        Assert.Null(output.HasMore);
        Assert.NotEmpty(output.Warnings!);
    }

    [Fact]
    public async Task Directory_traversal_exit_two_preserves_matches_as_partial()
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = CreateSingleMatchEvents(@"C:\root\a.txt", "MATCH"),
            ExitCode = 2,
            StandardError = "rg: C:\\root\\locked.txt: Access is denied. (os error 5)",
        };
        GrepService service = new(platform, new RipgrepInvocationBuilder(platform), runner);

        GrepOutput output = await service.SearchAsync(
            new GrepRequest(@"C:\root", "MATCH", PatternKind.Literal),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Equal(1, output.ReturnedResults);
        Assert.Null(output.HasMore);
        Assert.NotEmpty(output.Warnings!);
    }

    private static string[] MatchFileNames(GrepOutput output) => output.Blocks!
        .Select(block => Path.GetFileName(block.Path))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string NearRecordLimitRoot() => @"C:\" +
        string.Join('\\', Enumerable.Repeat(new string('x', 200), 80)) + "\\" + new string('y', 170);

    private static IReadOnlyList<byte[]> CreateSingleMatchEvents(string path, string line)
    {
        string escapedPath = path.Replace("\\", "\\\\", StringComparison.Ordinal);
        string begin = $"{{\"type\":\"begin\",\"data\":{{\"path\":{{\"text\":\"{escapedPath}\"}}}}}}";
        string match = $"{{\"type\":\"match\",\"data\":{{\"path\":{{\"text\":\"{escapedPath}\"}},\"lines\":{{\"text\":\"{line}\\n\"}},\"line_number\":1,\"absolute_offset\":0,\"submatches\":[{{\"match\":{{\"text\":\"MATCH\"}},\"start\":0,\"end\":5}}]}}}}";
        string end = $"{{\"type\":\"end\",\"data\":{{\"path\":{{\"text\":\"{escapedPath}\"}},\"binary_offset\":null,\"stats\":{{\"matched_lines\":1,\"matches\":1}}}}}}";
        return [Encoding.UTF8.GetBytes(begin), Encoding.UTF8.GetBytes(match), Encoding.UTF8.GetBytes(end)];
    }

    private static TestWorkspace CreateFixtureRepository()
    {
        TestWorkspace workspace = new();
        Directory.CreateDirectory(workspace.PathFor(".git"));
        return workspace;
    }

    private static async ValueTask<GrepOutput> Search(
        string path,
        string pattern,
        PatternKind patternKind,
        bool caseSensitive = true,
        GrepOutputMode outputMode = GrepOutputMode.Matches,
        IReadOnlyList<string>? includeGlobs = null,
        IReadOnlyList<string>? excludeGlobs = null,
        bool includeHidden = false,
        bool respectIgnoreFiles = true,
        int contextLines = 0,
        int resultOffset = 0,
        int maxResults = ToolBudgets.GrepResultsDefault)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        GrepRequest request = validator.Validate(new GrepRequest(
            path,
            pattern,
            patternKind,
            caseSensitive,
            outputMode,
            includeGlobs,
            excludeGlobs,
            includeHidden,
            respectIgnoreFiles,
            contextLines,
            resultOffset,
            maxResults));
        GrepService service = new(
            platform,
            new RipgrepInvocationBuilder(platform),
            new RipgrepRunner(new WindowsJobProcessPlatform()));
        return await service.SearchAsync(request, CancellationToken.None);
    }
}
