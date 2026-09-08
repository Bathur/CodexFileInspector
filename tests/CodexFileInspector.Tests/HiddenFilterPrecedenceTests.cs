using CodexFileInspector.Contracts;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class HiddenFilterPrecedenceTests
{
    private static readonly string[] HiddenFiles =
        [".dot-file.txt", "attribute-file.txt", ".dot-dir/nested.txt", "attribute-dir/nested.txt"];

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Without_allow_rules_hidden_and_ignore_switches_control_separate_defaults(
        bool includeHidden, bool respectIgnoreFiles)
    {
        using TestWorkspace workspace = CreateFixture();
        List<string> expected = ["visible.txt"];
        if (includeHidden)
        {
            expected.AddRange(HiddenFiles);
        }

        if (!respectIgnoreFiles)
        {
            expected.Add("ignored.txt");
        }

        AssertPaths(expected, await Search("grep", workspace, includeHidden, respectIgnoreFiles));
    }

    [Theory]
    [InlineData("glob", false, true)]
    [InlineData("glob", false, false)]
    [InlineData("glob", true, true)]
    [InlineData("glob", true, false)]
    [InlineData("grep", false, true)]
    [InlineData("grep", false, false)]
    [InlineData("grep", true, true)]
    [InlineData("grep", true, false)]
    public async Task Explicit_file_and_directory_includes_override_default_hidden_and_ignore_filters(
        string tool, bool includeHidden, bool respectIgnoreFiles)
    {
        using TestWorkspace workspace = CreateFixture();

        string[] actual = await Search(tool, workspace, includeHidden, respectIgnoreFiles,
            ["**/*.txt", ".dot-dir", "attribute-dir"]);

        AssertPaths(["visible.txt", "ignored.txt", .. HiddenFiles], actual);
    }

    [Theory]
    [InlineData("glob", false)]
    [InlineData("glob", true)]
    [InlineData("grep", false)]
    [InlineData("grep", true)]
    public async Task A_file_glob_can_allow_hidden_files_without_allowing_hidden_parent_directories(
        string tool, bool includeHidden)
    {
        using TestWorkspace workspace = CreateFixture();
        List<string> expected = ["visible.txt", "ignored.txt", ".dot-file.txt", "attribute-file.txt"];
        if (includeHidden)
        {
            expected.AddRange([".dot-dir/nested.txt", "attribute-dir/nested.txt"]);
        }

        AssertPaths(expected, await Search(tool, workspace, includeHidden, true, ["**/*.txt"]));
    }

    [Theory]
    [InlineData("grep", true)]
    [InlineData("grep", false)]
    [InlineData("glob", true)]
    [InlineData("glob", false)]
    public async Task Disabling_ignore_files_also_disables_their_hidden_allow_rules(
        string tool, bool respectIgnoreFiles)
    {
        using TestWorkspace workspace = CreateFixture(withAllowRules: true);
        List<string> expected = ["visible.txt"];
        if (tool == "glob" || !respectIgnoreFiles)
        {
            expected.Add("ignored.txt");
        }

        if (respectIgnoreFiles)
        {
            expected.AddRange(HiddenFiles);
        }
        else if (tool == "glob")
        {
            expected.AddRange([".dot-file.txt", "attribute-file.txt"]);
        }

        AssertPaths(expected, await Search(tool, workspace, false, respectIgnoreFiles,
            tool == "glob" ? ["**/*.txt"] : null));
    }

    [Theory]
    [InlineData("glob")]
    [InlineData("grep")]
    public async Task Explicit_excludes_win_over_includes_and_ignore_file_allow_rules(string tool)
    {
        using TestWorkspace workspace = CreateFixture(withAllowRules: true);

        string[] actual = await Search(tool, workspace, false, true,
            ["**/*.txt", ".dot-dir", "attribute-dir"],
            [".dot-file.txt", "attribute-file.txt", ".dot-dir", "attribute-dir"]);

        AssertPaths(["visible.txt", "ignored.txt"], actual);
    }

    private static TestWorkspace CreateFixture(bool withAllowRules = false)
    {
        TestWorkspace workspace = new();
        Directory.CreateDirectory(workspace.PathFor(".git"));
        Directory.CreateDirectory(workspace.PathFor(".dot-dir"));
        Directory.CreateDirectory(workspace.PathFor("attribute-dir"));
        foreach (string path in new[] { "visible.txt", "ignored.txt" }.Concat(HiddenFiles))
        {
            File.WriteAllText(workspace.PathFor(path), "NEEDLE\n");
        }

        foreach (string path in new[] { "attribute-file.txt", "attribute-dir" })
        {
            string absolutePath = workspace.PathFor(path);
            File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) | FileAttributes.Hidden);
        }

        File.WriteAllText(workspace.PathFor(".gitignore"), "ignored.txt\n");
        if (withAllowRules)
        {
            File.WriteAllText(workspace.PathFor(".ignore"),
                "!.dot-file.txt\n!attribute-file.txt\n!.dot-dir/\n!attribute-dir/\n");
        }

        return workspace;
    }

    private static void AssertPaths(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    private static async Task<string[]> Search(
        string tool, TestWorkspace workspace, bool includeHidden, bool respectIgnoreFiles,
        string[]? includes = null, string[]? excludes = null)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        RipgrepInvocationBuilder invocationBuilder = new(platform);
        RipgrepRunner runner = new(new WindowsJobProcessPlatform());
        IReadOnlyList<string> paths;
        if (tool == "glob")
        {
            GlobRequest request = validator.Validate(new GlobRequest(
                workspace.Root, includes!, excludes,
                IncludeHidden: includeHidden, RespectIgnoreFiles: respectIgnoreFiles));
            GlobOutput output = await new GlobService(platform, invocationBuilder, runner)
                .FindAsync(request, CancellationToken.None);
            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.False(output.HasMore);
            Assert.Equal(output.ReturnedResults, output.TotalResults);
            paths = output.Paths!;
        }
        else
        {
            GrepRequest request = validator.Validate(new GrepRequest(
                workspace.Root, "NEEDLE", PatternKind.Literal, OutputMode: GrepOutputMode.FilesWithMatches,
                IncludeGlobs: includes, ExcludeGlobs: excludes,
                IncludeHidden: includeHidden, RespectIgnoreFiles: respectIgnoreFiles));
            GrepOutput output = await new GrepService(platform, invocationBuilder, runner)
                .SearchAsync(request, CancellationToken.None);
            Assert.Equal(ToolStatus.Success, output.Status);
            Assert.False(output.HasMore);
            Assert.Equal(output.ReturnedResults, output.TotalResults);
            paths = output.Paths!;
        }

        return paths.Select(path => Path.GetRelativePath(workspace.Root, path).Replace('\\', '/')).ToArray();
    }
}
