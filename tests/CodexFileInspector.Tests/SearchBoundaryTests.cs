using CodexFileInspector.Contracts;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class SearchBoundaryTests
{
    [Theory]
    [InlineData("glob", "--help")]
    [InlineData("glob", "--version")]
    [InlineData("glob", "-n")]
    [InlineData("grep", "--help")]
    [InlineData("grep", "--version")]
    [InlineData("grep", "-n")]
    public async Task Option_shaped_inputs_are_data_not_ripgrep_switches(string tool, string value)
    {
        using TestWorkspace workspace = CreateRepository();
        string target = workspace.PathFor(tool == "glob" ? value : "target.txt");
        File.WriteAllText(target, value + "\n");

        IReadOnlyList<string> paths = await SearchPaths(
            tool, workspace.Root, value, tool == "glob" ? [value] : ["target.txt"]);

        Assert.Equal([target], paths);
    }

    [Theory]
    [InlineData("glob")]
    [InlineData("grep")]
    public async Task Search_roots_with_glob_metacharacters_spaces_and_unicode_remain_literal(string tool)
    {
        using TestWorkspace workspace = CreateRepository();
        string root = workspace.PathFor("source [literal] (中文)");
        string characterDirectory = Path.Combine(root, "Character");
        Directory.CreateDirectory(characterDirectory);
        string target = Path.Combine(characterDirectory, "target.txt");
        File.WriteAllText(target, "NEEDLE");
        File.WriteAllText(Path.Combine(characterDirectory, "drop.txt"), "NEEDLE");

        IReadOnlyList<string> paths = await SearchPaths(
            tool, root, "NEEDLE", ["Character/*.txt"], ["Character/drop.txt"]);

        Assert.Equal([target], paths);
    }

    [Theory]
    [InlineData("glob")]
    [InlineData("grep")]
    public async Task Git_file_marker_does_not_change_the_filter_root(string tool)
    {
        using TestWorkspace workspace = new();
        string checkout = workspace.PathFor("checkout");
        string gitDirectory = workspace.PathFor("git-admin");
        string root = Path.Combine(checkout, "src", "module");
        Directory.CreateDirectory(gitDirectory);
        Directory.CreateDirectory(Path.Combine(root, "Character"));
        File.WriteAllText(Path.Combine(checkout, ".git"), $"gitdir: {gitDirectory.Replace('\\', '/')}\n");
        string target = Path.Combine(root, "Character", "target.txt");
        File.WriteAllText(target, "NEEDLE");

        IReadOnlyList<string> paths = await SearchPaths(tool, root, "NEEDLE", ["Character/target.txt"]);

        Assert.Equal([target], paths);
    }

    [Theory]
    [InlineData(GrepOutputMode.Matches)]
    [InlineData(GrepOutputMode.FilesWithMatches)]
    [InlineData(GrepOutputMode.Count)]
    public async Task Small_pages_reconstruct_the_complete_result_in_each_mode(GrepOutputMode mode)
    {
        using TestWorkspace workspace = CreateRepository();
        File.WriteAllText(workspace.PathFor("a.txt"), "before\nNEEDLE NEEDLE\nNEEDLE\nafter\n");
        File.WriteAllText(workspace.PathFor("b.txt"), "NEEDLE\n");
        GrepRequest request = new(
            workspace.Root, "NEEDLE", PatternKind.Literal,
            OutputMode: mode, ContextLines: mode == GrepOutputMode.Matches ? 1 : 0, MaxResults: 20);
        GrepOutput whole = await Search(request);
        List<string> paged = [];
        int offset = 0;

        for (int pageIndex = 0; pageIndex < 10; pageIndex++)
        {
            GrepOutput page = await Search(request with { ResultOffset = offset, MaxResults = 1 });
            paged.AddRange(ResultKeys(page, mode));
            if (mode == GrepOutputMode.Count)
            {
                Assert.Equal(whole.Totals, page.Totals);
                Assert.Equal(whole.TotalResults, page.TotalResults);
            }

            if (page.HasMore == false)
            {
                Assert.Null(page.NextResultOffset);
                Assert.Equal(whole.TotalResults, page.TotalResults);
                Assert.Equal(ResultKeys(whole, mode), paged);
                return;
            }

            Assert.Equal(offset + page.ReturnedResults, page.NextResultOffset);
            Assert.True(page.NextResultOffset > offset);
            offset = page.NextResultOffset!.Value;
        }

        Assert.Fail("Pagination did not finish within the small fixture's bounded number of pages.");
    }

    private static IReadOnlyList<string> ResultKeys(GrepOutput output, GrepOutputMode mode) => mode switch
    {
        GrepOutputMode.Matches => output.Blocks!
            .SelectMany(block => block.MatchLines.Select(match => $"{block.Path}:{match.LineNumber}:{match.OccurrenceCount}"))
            .ToArray(),
        GrepOutputMode.FilesWithMatches => output.Paths!,
        GrepOutputMode.Count => output.Counts!
            .Select(count => $"{count.Path}:{count.MatchingLines}:{count.Occurrences}")
            .ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static TestWorkspace CreateRepository()
    {
        TestWorkspace workspace = new();
        Directory.CreateDirectory(workspace.PathFor(".git"));
        return workspace;
    }

    private static async Task<IReadOnlyList<string>> SearchPaths(
        string tool,
        string root,
        string pattern,
        string[] includes,
        string[]? excludes = null)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        if (tool == "glob")
        {
            GlobRequest request = validator.Validate(new GlobRequest(root, includes, excludes));
            GlobOutput output = await new GlobService(
                platform, new RipgrepInvocationBuilder(platform), new RipgrepRunner(new WindowsJobProcessPlatform()))
                .FindAsync(request, CancellationToken.None);
            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.False(output.HasMore);
            return output.Paths!;
        }

        GrepOutput matches = await Search(new GrepRequest(
            root, pattern, PatternKind.Literal, IncludeGlobs: includes, ExcludeGlobs: excludes));
        Assert.False(matches.HasMore);
        return matches.Blocks!.Select(block => block.Path).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async Task<GrepOutput> Search(GrepRequest request)
    {
        WindowsFileSystemPlatform platform = new();
        request = new ToolRequestValidator(platform).Validate(request);
        GrepOutput output = await new GrepService(
            platform, new RipgrepInvocationBuilder(platform), new RipgrepRunner(new WindowsJobProcessPlatform()))
            .SearchAsync(request, CancellationToken.None);
        Assert.Equal(ToolStatus.Success, output.Status);
        return output;
    }
}
