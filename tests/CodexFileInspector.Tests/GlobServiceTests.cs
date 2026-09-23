using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class GlobServiceTests
{
    [Fact]
    public async Task Multiple_includes_form_one_deduplicated_files_only_set()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        Directory.CreateDirectory(workspace.PathFor("src"));
        File.WriteAllText(workspace.PathFor("src/a.cs"), "a");
        File.WriteAllText(workspace.PathFor("src/b.md"), "b");
        File.WriteAllText(workspace.PathFor("src/c.txt"), "c");
        Directory.CreateDirectory(workspace.PathFor("src/nested.cs"));

        GlobOutput output = await Find(
            workspace.Root,
            ["**/*.cs", "src/*", "**/*.md"],
            respectIgnoreFiles: false);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal(
            ["a.cs", "b.md", "c.txt"],
            output.Paths!.Select(Path.GetFileName));
        Assert.All(output.Paths!, path => Assert.True(Path.IsPathFullyQualified(path)));
        Assert.Equal(3, output.TotalResults);
    }

    [Fact]
    public async Task Glob_case_is_insensitive_by_default_and_can_be_exact()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("README.md"), "value");

        GlobOutput insensitive = await Find(workspace.Root, ["**/readme.md"], caseSensitive: false);
        GlobOutput sensitive = await Find(workspace.Root, ["**/readme.md"], caseSensitive: true);

        Assert.Single(insensitive.Paths!);
        Assert.Empty(sensitive.Paths!);
    }

    [Fact]
    public async Task Excludes_override_includes()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        Directory.CreateDirectory(workspace.PathFor("src/obj"));
        File.WriteAllText(workspace.PathFor("src/keep.cs"), "keep");
        File.WriteAllText(workspace.PathFor("src/obj/drop.cs"), "drop");

        GlobOutput output = await Find(
            workspace.Root,
            ["**/*.cs"],
            ["**/obj/**"],
            respectIgnoreFiles: false);

        Assert.Equal(["keep.cs"], output.Paths!.Select(Path.GetFileName));
    }

    [Fact]
    public async Task File_extension_glob_does_not_whitelist_a_hidden_parent_directory()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        Directory.CreateDirectory(workspace.PathFor(".hidden"));
        File.WriteAllText(workspace.PathFor(".hidden/inside.cs"), "hidden");
        File.WriteAllText(workspace.PathFor("visible.cs"), "visible");

        GlobOutput hiddenOff = await Find(
            workspace.Root,
            ["**/*.cs"],
            includeHidden: false,
            respectIgnoreFiles: false);
        GlobOutput hiddenOn = await Find(
            workspace.Root,
            ["**/*.cs"],
            includeHidden: true,
            respectIgnoreFiles: false);

        Assert.Equal(["visible.cs"], hiddenOff.Paths!.Select(Path.GetFileName));
        Assert.Equal(2, hiddenOn.Paths!.Count);
    }

    [Fact]
    public async Task Explicit_include_can_override_a_loaded_ignore_rule()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor(".gitignore"), "ignored.cs\n");
        File.WriteAllText(workspace.PathFor("ignored.cs"), "ignored");
        File.WriteAllText(workspace.PathFor("visible.cs"), "visible");

        GlobOutput output = await Find(
            workspace.Root,
            ["**/*.cs"],
            respectIgnoreFiles: true);

        Assert.Equal(["ignored.cs", "visible.cs"], output.Paths!.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Pagination_is_stable_and_last_page_has_an_exact_total()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        foreach (string name in new[] { "e.txt", "c.txt", "a.txt", "d.txt", "b.txt" })
        {
            File.WriteAllText(workspace.PathFor(name), name);
        }

        GlobOutput first = await Find(
            workspace.Root,
            ["**/*.txt"],
            resultOffset: 0,
            maxResults: 2,
            respectIgnoreFiles: false);
        GlobOutput second = await Find(
            workspace.Root,
            ["**/*.txt"],
            resultOffset: first.NextResultOffset!.Value,
            maxResults: 10,
            respectIgnoreFiles: false);

        Assert.Equal(["a.txt", "b.txt"], first.Paths!.Select(Path.GetFileName));
        Assert.True(first.HasMore);
        Assert.Null(first.TotalResults);
        Assert.Equal(2, first.NextResultOffset);
        Assert.Equal(["c.txt", "d.txt", "e.txt"], second.Paths!.Select(Path.GetFileName));
        Assert.False(second.HasMore);
        Assert.Equal(5, second.TotalResults);
    }

    [Fact]
    public async Task Offset_past_complete_result_is_a_typed_error()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllText(workspace.PathFor("one.txt"), "one");

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Find(
                workspace.Root,
                ["**/*.txt"],
                resultOffset: 1,
                respectIgnoreFiles: false));

        Assert.Equal(ToolErrorCodes.ResultOffsetPastEnd, exception.Code);
        Assert.Equal(1, exception.Total);
    }

    [Fact]
    public async Task Invalid_glob_identifies_the_input_array_and_index()
    {
        using TestWorkspace workspace = CreateFixtureRepository();

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Find(workspace.Root, ["**/*.txt", "["]));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal("include_globs", exception.Field);
        Assert.Equal(1, exception.Index);
    }

    [Fact]
    public async Task Binary_files_are_discovered_by_name()
    {
        using TestWorkspace workspace = CreateFixtureRepository();
        File.WriteAllBytes(workspace.PathFor("asset.bin"), [0, 1, 2, 3]);

        GlobOutput output = await Find(workspace.Root, ["**/*.bin"], respectIgnoreFiles: false);

        Assert.Equal("asset.bin", Path.GetFileName(Assert.Single(output.Paths!)));
    }

    [Fact]
    public async Task Whole_result_budget_truncates_only_between_paths()
    {
        string segment = new('x', 900);
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = Enumerable.Range(0, 100)
                .Select(index => Encoding.UTF8.GetBytes($@"C:\root\{index:D3}-{segment}.txt"))
                .ToArray(),
        };
        GlobService service = new(platform, new RipgrepInvocationBuilder(platform), runner);
        GlobRequest request = new(@"C:\root", ["**/*.txt"], RespectIgnoreFiles: false, MaxResults: 100);

        GlobOutput output = await service.FindAsync(request, CancellationToken.None);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal("byte_budget", output.TruncatedBy);
        Assert.True(output.HasMore);
        Assert.InRange(output.ReturnedResults!.Value, 1, 99);
        Assert.Equal(output.ReturnedResults, output.NextResultOffset);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GlobOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Minimum_path_budget_failure_never_returns_a_zero_progress_success(bool incomplete)
    {
        string root = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('x', 200), 80)) +
            "\\" + new string('y', 170);
        string path = root + "\\a";
        OutputBudget.EnsurePathFits(root);
        OutputBudget.EnsurePathFits(path);
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = [Encoding.UTF8.GetBytes(path)],
            ExitCode = incomplete ? 2 : 0,
            StandardError = incomplete ? "rg: C:\\other: Access is denied. (os error 5)" : string.Empty,
        };
        GlobService service = new(platform, new RipgrepInvocationBuilder(platform), runner);
        GlobRequest request = new(root, ["**/*"]);

        if (!incomplete)
        {
            ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
                await service.FindAsync(request, CancellationToken.None));

            Assert.Equal(ToolErrorCodes.OutputRecordTooLarge, exception.Code);
            return;
        }

        GlobOutput output = await service.FindAsync(request, CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Empty(output.Paths!);
        Assert.Equal(0, output.ReturnedResults);
        Assert.Null(output.TotalResults);
        Assert.Null(output.HasMore);
        Assert.Null(output.NextResultOffset);
        Assert.NotEmpty(output.Warnings!);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.GlobOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Fact]
    public async Task Traversal_exit_two_returns_partial_paths_and_no_continuation()
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new()
        {
            Records = [Encoding.UTF8.GetBytes(@"C:\root\a.txt")],
            ExitCode = 2,
            StandardError = "rg: C:\\root\\locked: Access is denied. (os error 5)",
        };
        GlobService service = new(platform, new RipgrepInvocationBuilder(platform), runner);

        GlobOutput output = await service.FindAsync(
            new GlobRequest(@"C:\root", ["**/*.txt"]),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Single(output.Paths!);
        Assert.Null(output.TotalResults);
        Assert.Null(output.HasMore);
        Assert.Null(output.NextResultOffset);
        Assert.NotEmpty(output.Warnings!);
    }

    [Fact]
    public async Task Unexpected_ripgrep_exit_is_a_typed_process_failure()
    {
        TestFileSystemPlatform platform = new() { Attributes = FileAttributes.Directory };
        TestRipgrepRunner runner = new() { ExitCode = 3 };
        GlobService service = new(platform, new RipgrepInvocationBuilder(platform), runner);

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await service.FindAsync(
                new GlobRequest(@"C:\root", ["**/*.txt"]),
                CancellationToken.None));

        Assert.Equal(ToolErrorCodes.RipgrepFailed, exception.Code);
    }

    private static TestWorkspace CreateFixtureRepository()
    {
        TestWorkspace workspace = new();
        Directory.CreateDirectory(workspace.PathFor(".git"));
        return workspace;
    }

    private static async ValueTask<GlobOutput> Find(
        string path,
        IReadOnlyList<string> includes,
        IReadOnlyList<string>? excludes = null,
        bool caseSensitive = false,
        bool includeHidden = false,
        bool respectIgnoreFiles = true,
        int resultOffset = 0,
        int maxResults = ToolBudgets.GlobResultsDefault)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        GlobRequest request = validator.Validate(new GlobRequest(
            path,
            includes,
            excludes,
            caseSensitive,
            includeHidden,
            respectIgnoreFiles,
            resultOffset,
            maxResults));
        GlobService service = new(
            platform,
            new RipgrepInvocationBuilder(platform),
            new RipgrepRunner(new WindowsJobProcessPlatform()));
        return await service.FindAsync(request, CancellationToken.None);
    }
}
