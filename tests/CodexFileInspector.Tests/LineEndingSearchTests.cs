using CodexFileInspector.Contracts;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Reading;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class LineEndingSearchTests
{
    [Theory]
    [InlineData("\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public async Task Windows_and_unix_lines_keep_matches_context_counts_and_read_locations_consistent(
        string newline, bool regex)
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("lines.txt");
        await File.WriteAllTextAsync(path, string.Join(newline, ["first", "MATCH", "after", "MATCH", "last", ""]));
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        GrepService service = new(platform, new RipgrepInvocationBuilder(platform),
            new RipgrepRunner(new WindowsJobProcessPlatform()));
        GrepRequest request = validator.Validate(new GrepRequest(path,
            regex ? "^MATCH$" : "MATCH", regex ? PatternKind.Regex : PatternKind.Literal));

        GrepOutput matches = await service.SearchAsync(request with { ContextLines = 1 }, CancellationToken.None);
        Assert.Equal(ToolStatus.Success, matches.Status);
        Assert.Equal(2, matches.ReturnedResults);
        GrepMatchBlock block = Assert.Single(matches.Blocks!);
        Assert.Equal([2L, 4L], block.MatchLines.Select(match => match.LineNumber));
        Assert.Equal("L1: first\nL2: MATCH\nL3: after\nL4: MATCH\nL5: last", block.Content);

        GrepOutput first = await service.SearchAsync(request with { MaxResults = 1 }, CancellationToken.None);
        Assert.Equal(2, Assert.Single(Assert.Single(first.Blocks!).MatchLines).LineNumber);
        Assert.True(first.HasMore);
        GrepOutput second = await service.SearchAsync(request with
        {
            MaxResults = 1,
            ResultOffset = first.NextResultOffset!.Value,
        }, CancellationToken.None);
        Assert.Equal(4, Assert.Single(Assert.Single(second.Blocks!).MatchLines).LineNumber);
        Assert.False(second.HasMore);

        GrepOutput count = await service.SearchAsync(request with { OutputMode = GrepOutputMode.Count }, CancellationToken.None);
        Assert.Equal(new GrepTotals(1, 2, 2), count.Totals);
        GrepOutput files = await service.SearchAsync(request with { OutputMode = GrepOutputMode.FilesWithMatches }, CancellationToken.None);
        Assert.Equal(path, Assert.Single(files.Paths!));

        ReadFileService reader = new(platform);
        foreach (GrepMatchLine match in block.MatchLines)
        {
            ReadFileOutput read = await reader.ReadAsync(new ReadFileRequest(path, (int)match.LineNumber, 1), CancellationToken.None);
            Assert.Equal("MATCH", read.Content);
        }
    }
}
