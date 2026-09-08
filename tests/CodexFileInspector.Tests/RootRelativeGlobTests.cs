using CodexFileInspector.Contracts;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class RootRelativeGlobTests
{
    [Theory]
    [InlineData("glob", "Character/LyraHeroComponent.cpp", false)]
    [InlineData("glob", "**/Character/LyraHeroComponent.cpp", true)]
    [InlineData("grep", "Character/LyraHeroComponent.cpp", false)]
    [InlineData("grep", "**/Character/LyraHeroComponent.cpp", true)]
    public async Task Includes_are_relative_to_the_search_root_not_the_repository_or_server_cwd(
        string tool,
        string pattern,
        bool includesDescendant)
    {
        using RootRelativeGlobFixture fixture = new();
        string serverCwd = Directory.GetCurrentDirectory();
        Assert.NotEqual(serverCwd, fixture.SearchRoot);

        foreach (string path in new[] { fixture.SearchRoot, fixture.SearchRoot.Replace('\\', '/') })
        {
            IReadOnlyList<string> actual = await FindPaths(tool, path, pattern);
            string[] expected = includesDescendant ? [fixture.Target, fixture.Descendant] : [fixture.Target];
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        }

        Assert.Equal(serverCwd, Directory.GetCurrentDirectory());
    }

    [Theory]
    [InlineData("glob", "Character/LyraHeroComponent.cpp", false)]
    [InlineData("glob", "**/Character/LyraHeroComponent.cpp", true)]
    [InlineData("grep", "Character/LyraHeroComponent.cpp", false)]
    [InlineData("grep", "**/Character/LyraHeroComponent.cpp", true)]
    public async Task Excludes_use_the_same_root_and_override_includes(
        string tool,
        string pattern,
        bool excludesDescendant)
    {
        using RootRelativeGlobFixture fixture = new();

        foreach (string path in new[] { fixture.SearchRoot, fixture.SearchRoot.Replace('\\', '/') })
        {
            IReadOnlyList<string> actual = await FindPaths(tool, path, "**/*.cpp", pattern);
            string[] expected = excludesDescendant ? [fixture.Keep] : [fixture.Keep, fixture.Descendant];
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        }
    }

    [Fact]
    public void Invocation_uses_the_search_directory_without_rewriting_patterns_or_server_cwd()
    {
        using RootRelativeGlobFixture fixture = new();
        string serverCwd = Directory.GetCurrentDirectory();
        WindowsFileSystemPlatform platform = new();
        RipgrepInvocationBuilder builder = new(platform);
        const string pattern = "Character/LyraHeroComponent.cpp";

        RipgrepRunRequest glob = builder.BuildGlob(new GlobRequest(fixture.SearchRoot, [pattern]));
        RipgrepRunRequest grep = builder.BuildGrep(new GrepRequest(
            fixture.SearchRoot, "HandleChangeInitState", PatternKind.Literal, IncludeGlobs: [pattern]));
        RipgrepRunRequest file = builder.BuildGrep(new GrepRequest(
            fixture.Target, "HandleChangeInitState", PatternKind.Literal));

        foreach (RipgrepRunRequest invocation in new[] { glob, grep })
        {
            Assert.Equal(fixture.SearchRoot, invocation.WorkingDirectory);
            Assert.Equal(fixture.SearchRoot, invocation.Arguments[^1]);
            Assert.Contains(pattern, invocation.Arguments);
            Assert.DoesNotContain($"**/{pattern}", invocation.Arguments);
            Assert.DoesNotContain("--no-ignore-parent", invocation.Arguments);
        }

        Assert.Equal(Path.GetDirectoryName(fixture.Target), file.WorkingDirectory);
        Assert.Equal(fixture.Target, file.Arguments[^1]);
        Assert.Equal(serverCwd, Directory.GetCurrentDirectory());
    }

    private static async Task<IReadOnlyList<string>> FindPaths(
        string tool,
        string path,
        string includeGlob,
        string? excludeGlob = null)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        RipgrepInvocationBuilder builder = new(platform);
        RipgrepRunner runner = new(new WindowsJobProcessPlatform());
        string[] excludes = excludeGlob is null ? [] : [excludeGlob];

        if (tool == "glob")
        {
            GlobRequest request = validator.Validate(new GlobRequest(
                path, [includeGlob], excludes, MaxResults: 5));
            GlobOutput output = await new GlobService(platform, builder, runner)
                .FindAsync(request, CancellationToken.None);
            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.False(output.HasMore);
            Assert.Equal(output.Paths!.Count, output.TotalResults);
            return output.Paths;
        }

        GrepRequest grepRequest = validator.Validate(new GrepRequest(
            path, "HandleChangeInitState", PatternKind.Literal,
            IncludeGlobs: [includeGlob], ExcludeGlobs: excludes, MaxResults: 5));
        GrepOutput grepOutput = await new GrepService(platform, builder, runner)
            .SearchAsync(grepRequest, CancellationToken.None);
        Assert.Equal(ToolStatus.Success, grepOutput.Status);
        Assert.False(grepOutput.HasMore);
        Assert.Equal(grepOutput.Blocks!.Count, grepOutput.TotalResults);
        return grepOutput.Blocks.Select(block => block.Path).ToArray();
    }
}

internal sealed class RootRelativeGlobFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public RootRelativeGlobFixture()
    {
        Directory.CreateDirectory(_workspace.PathFor(".git"));
        SearchRoot = Path.Combine(_workspace.Root, "Source", "Lyra Game");
        Target = Path.Combine(SearchRoot, "Character", "LyraHeroComponent.cpp");
        Descendant = Path.Combine(SearchRoot, "Nested", "Character", "LyraHeroComponent.cpp");
        Keep = Path.Combine(SearchRoot, "Character", "Keep.cpp");
        string outside = Path.Combine(_workspace.Root, "Character", "LyraHeroComponent.cpp");
        foreach (string path in new[] { Target, Descendant, Keep, outside })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "HandleChangeInitState\n");
        }

        // Explicit includes must still override a rule loaded above the search root.
        File.WriteAllText(_workspace.PathFor(".gitignore"),
            "Source/Lyra Game/Character/LyraHeroComponent.cpp\n");
    }

    public string SearchRoot { get; }
    public string Target { get; }
    public string Descendant { get; }
    public string Keep { get; }

    public void Dispose() => _workspace.Dispose();
}
