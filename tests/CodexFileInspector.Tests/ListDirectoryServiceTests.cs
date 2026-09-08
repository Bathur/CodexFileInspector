using CodexFileInspector.Contracts;
using CodexFileInspector.DirectoryListing;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ListDirectoryServiceTests
{
    [Fact]
    public void Lists_all_direct_children_in_stable_name_order()
    {
        using TestWorkspace workspace = new();
        Directory.CreateDirectory(workspace.PathFor("src"));
        File.WriteAllText(workspace.PathFor("B.txt"), "b");
        File.WriteAllText(workspace.PathFor("a.txt"), "a");
        File.WriteAllText(workspace.PathFor(".gitignore"), "ignored.txt");
        string ignored = workspace.PathFor("ignored.txt");
        File.WriteAllText(ignored, "still visible");
        File.SetAttributes(ignored, File.GetAttributes(ignored) | FileAttributes.Hidden);

        ListDirectoryOutput output = List(workspace.Root);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal([".gitignore", "a.txt", "B.txt", "ignored.txt", "src"], output.Entries!.Select(entry => entry.Name));
        Assert.Equal(5, output.TotalEntries);
        Assert.Equal(5, output.ReturnedEntries);
        Assert.False(output.HasMore);
        Assert.Equal(DirectoryEntryKind.Directory, output.Entries!.Single(entry => entry.Name == "src").Kind);
        Assert.Equal(DirectoryEntryKind.File, output.Entries!.Single(entry => entry.Name == "a.txt").Kind);
    }

    [Fact]
    public void Empty_directory_is_a_success()
    {
        using TestWorkspace workspace = new();

        ListDirectoryOutput output = List(workspace.Root);

        Assert.Empty(output.Entries!);
        Assert.Equal(0, output.TotalEntries);
        Assert.False(output.HasMore);
        Assert.Null(output.NextResultOffset);
    }

    [Fact]
    public void Pagination_uses_offset_plus_returned_entries()
    {
        using TestWorkspace workspace = new();
        foreach (string name in new[] { "e", "c", "a", "d", "b" })
        {
            File.WriteAllText(workspace.PathFor(name), name);
        }

        ListDirectoryOutput output = List(workspace.Root, resultOffset: 2, maxEntries: 2);

        Assert.Equal(["c", "d"], output.Entries!.Select(entry => entry.Name));
        Assert.Equal(5, output.TotalEntries);
        Assert.True(output.HasMore);
        Assert.Equal(4, output.NextResultOffset);
        Assert.Equal("entry_limit", output.TruncatedBy);
    }

    [Fact]
    public void Positive_offset_at_end_is_a_typed_error()
    {
        using TestWorkspace workspace = new();
        File.WriteAllText(workspace.PathFor("only"), "value");
        WindowsFileSystemPlatform platform = new();
        ListDirectoryService service = new(platform);

        ToolExecutionException exception = Assert.Throws<ToolExecutionException>(
            () => service.List(new ListDirectoryRequest(workspace.Root, 1, 1), CancellationToken.None));

        Assert.Equal(ToolErrorCodes.ResultOffsetPastEnd, exception.Code);
        Assert.Equal(1, exception.Total);
    }

    [Fact]
    public void File_root_is_a_typed_not_directory_error()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("file.txt");
        File.WriteAllText(path, "value");

        ToolExecutionException exception = Assert.Throws<ToolExecutionException>(
            () => List(path));

        Assert.Equal(ToolErrorCodes.PathNotDirectory, exception.Code);
    }

    [Fact]
    public void Link_kind_is_preserved_without_target_metadata()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries =
            [
                new FileSystemEntrySnapshot("link", @"C:\root\link", DirectoryEntryKind.Link),
                new FileSystemEntrySnapshot("file", @"C:\root\file", DirectoryEntryKind.File),
            ],
        };

        ListDirectoryOutput output = new ListDirectoryService(platform).List(
            new ListDirectoryRequest(@"C:\root"),
            CancellationToken.None);

        Assert.Equal(DirectoryEntryKind.File, output.Entries![0].Kind);
        Assert.Equal(DirectoryEntryKind.Link, output.Entries[1].Kind);
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint, (int)DirectoryEntryKind.Link)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory, (int)DirectoryEntryKind.Link)]
    [InlineData(FileAttributes.Directory, (int)DirectoryEntryKind.Directory)]
    [InlineData(FileAttributes.Archive, (int)DirectoryEntryKind.File)]
    [InlineData(FileAttributes.Device, (int)DirectoryEntryKind.Other)]
    public void Windows_attribute_classification_uses_the_platform_neutral_kinds(
        FileAttributes attributes,
        int expected)
    {
        Assert.Equal((DirectoryEntryKind)expected, WindowsFileSystemPlatform.Classify(attributes));
    }

    [Fact]
    public void Mid_enumeration_failure_preserves_evidence_as_partial()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries = YieldThenFail(),
        };

        ListDirectoryOutput output = new ListDirectoryService(platform).List(
            new ListDirectoryRequest(@"C:\root"),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Equal(["a", "b"], output.Entries!.Select(entry => entry.Name));
        Assert.Null(output.TotalEntries);
        Assert.Null(output.HasMore);
        Assert.Null(output.NextResultOffset);
        Assert.Single(output.Warnings!);
    }

    [Fact]
    public void Directory_change_makes_totals_and_continuation_unavailable()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            DirectoryStampBefore = new DirectoryChangeStamp(DateTime.UnixEpoch, DateTime.UnixEpoch),
            DirectoryStampAfter = new DirectoryChangeStamp(DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1)),
            Entries = [new FileSystemEntrySnapshot("a", @"C:\root\a", DirectoryEntryKind.File)],
        };

        ListDirectoryOutput output = new ListDirectoryService(platform).List(
            new ListDirectoryRequest(@"C:\root"),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Null(output.TotalEntries);
        Assert.Equal(ToolWarningCodes.DirectoryChanged, Assert.Single(output.Warnings!).Code);
    }

    [Fact]
    public void Scan_limit_returns_bounded_partial_evidence()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries = Enumerable.Range(0, 4).Select(index =>
                new FileSystemEntrySnapshot($"{index}", $@"C:\root\{index}", DirectoryEntryKind.File)),
        };

        ListDirectoryOutput output = new ListDirectoryService(platform, scanMaximum: 2).List(
            new ListDirectoryRequest(@"C:\root"),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Partial, output.Status);
        Assert.Equal(2, output.ReturnedEntries);
        Assert.Equal(ToolErrorCodes.ScanLimitExceeded, Assert.Single(output.Warnings!).Code);
    }

    [Fact]
    public void Top_k_selection_is_stable_even_when_native_order_is_reversed()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries = Enumerable.Range(0, 10).Reverse().Select(index =>
                new FileSystemEntrySnapshot($"{index:D2}", $@"C:\root\{index:D2}", DirectoryEntryKind.File)),
        };

        ListDirectoryOutput output = new ListDirectoryService(platform).List(
            new ListDirectoryRequest(@"C:\root", 2, 3),
            CancellationToken.None);

        Assert.Equal(["02", "03", "04"], output.Entries!.Select(entry => entry.Name));
        Assert.Equal(5, output.NextResultOffset);
    }

    [Fact]
    public void Case_insensitive_name_ties_use_ordinal_case_sensitive_order()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries =
            [
                new FileSystemEntrySnapshot("a", @"C:\root\a", DirectoryEntryKind.File),
                new FileSystemEntrySnapshot("A", @"C:\root\A", DirectoryEntryKind.File),
            ],
        };

        ListDirectoryOutput output = new ListDirectoryService(platform).List(
            new ListDirectoryRequest(@"C:\root"),
            CancellationToken.None);

        Assert.Equal(["A", "a"], output.Entries!.Select(entry => entry.Name));
    }

    [Fact]
    public void Whole_result_budget_truncates_only_between_entries()
    {
        string longSegment = new('x', 900);
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries = Enumerable.Range(0, 100).Select(index =>
                new FileSystemEntrySnapshot(
                    $"{index:D3}-{longSegment}",
                    $@"C:\root\{index:D3}-{longSegment}",
                    DirectoryEntryKind.File)),
        };

        ListDirectoryOutput output = new ListDirectoryService(platform).List(
            new ListDirectoryRequest(@"C:\root", 0, 100),
            CancellationToken.None);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal("byte_budget", output.TruncatedBy);
        Assert.True(output.HasMore);
        Assert.InRange(output.ReturnedEntries!.Value, 1, 99);
        Assert.Equal(output.ReturnedEntries, output.NextResultOffset);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ListDirectoryOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Fact]
    public void Cancellation_propagates_from_directory_enumeration()
    {
        TestFileSystemPlatform platform = new()
        {
            Attributes = FileAttributes.Directory,
            Entries = [new FileSystemEntrySnapshot("a", @"C:\root\a", DirectoryEntryKind.File)],
        };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ListDirectoryService(platform).List(
                new ListDirectoryRequest(@"C:\root"),
                cancellation.Token));
    }

    private static ListDirectoryOutput List(string path, int resultOffset = 0, int maxEntries = 200)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        ListDirectoryRequest request = validator.Validate(new ListDirectoryRequest(path, resultOffset, maxEntries));
        return new ListDirectoryService(platform).List(request, CancellationToken.None);
    }

    private static IEnumerable<FileSystemEntrySnapshot> YieldThenFail()
    {
        yield return new FileSystemEntrySnapshot("b", @"C:\root\b", DirectoryEntryKind.File);
        yield return new FileSystemEntrySnapshot("a", @"C:\root\a", DirectoryEntryKind.File);
        throw new IOException("Synthetic enumeration failure.");
    }
}
