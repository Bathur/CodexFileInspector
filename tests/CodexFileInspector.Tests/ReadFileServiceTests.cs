using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Reading;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ReadFileServiceTests
{
    [Fact]
    public async Task Reads_mixed_line_endings_without_a_fictional_trailing_line()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("mixed.txt");
        await File.WriteAllTextAsync(path, "one\r\ntwo\nthree\rfour\r\n", new UTF8Encoding(false));

        ReadFileOutput output = await Read(path);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal("one\ntwo\nthree\nfour", output.Content);
        Assert.Equal(4, output.ReturnedLines);
        Assert.Equal(4, output.TotalLines);
        Assert.False(output.HasMore);
        Assert.Null(output.NextStartLine);
        Assert.Equal("end_of_file", output.StopReason);
    }

    [Fact]
    public async Task Empty_file_is_a_success_with_zero_lines()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("empty.txt");
        await File.WriteAllBytesAsync(path, []);

        ReadFileOutput output = await Read(path);

        Assert.Equal(string.Empty, output.Content);
        Assert.Equal(0, output.ReturnedLines);
        Assert.Equal(0, output.TotalLines);
        Assert.Null(output.StartLine);
        Assert.Null(output.EndLine);
        Assert.False(output.HasMore);
    }

    [Theory]
    [InlineData("\n", "", 1)]
    [InlineData("\r\n", "", 1)]
    [InlineData("a\n\n", "a\n", 2)]
    [InlineData("\r\r", "\n", 2)]
    public async Task Empty_logical_lines_are_real_but_a_trailing_terminator_adds_no_extra_line(
        string text,
        string expectedContent,
        int expectedLines)
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("empty-lines.txt");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));

        ReadFileOutput output = await Read(path);

        Assert.Equal(expectedContent, output.Content);
        Assert.Equal(expectedLines, output.TotalLines);
    }

    [Fact]
    public async Task Start_past_end_reports_the_exact_total()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("short.txt");
        await File.WriteAllTextAsync(path, "one\ntwo", new UTF8Encoding(false));

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(
            async () => await Read(path, startLine: 3));

        Assert.Equal(ToolErrorCodes.RangePastEof, exception.Code);
        Assert.Equal(2, exception.Total);
    }

    [Theory]
    [InlineData("utf8-bom", "utf-8")]
    [InlineData("utf16-le", "utf-16le")]
    [InlineData("utf16-be", "utf-16be")]
    public async Task Supports_the_four_approved_encoding_forms(string form, string expectedName)
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor($"{form}.txt");
        Encoding encoding = form switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            _ => throw new InvalidOperationException(),
        };
        await File.WriteAllTextAsync(path, "α\nβ", encoding);

        ReadFileOutput output = await Read(path);

        Assert.Equal(expectedName, output.Encoding);
        Assert.Equal("α\nβ", output.Content);
    }

    [Fact]
    public async Task Rejects_invalid_utf8()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("invalid.txt");
        await File.WriteAllBytesAsync(path, [0xC3, 0x28]);

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(
            async () => await Read(path));

        Assert.Equal(ToolErrorCodes.UnsupportedEncoding, exception.Code);
    }

    [Fact]
    public async Task Rejects_bomless_utf16()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("bomless-utf16.txt");
        await File.WriteAllBytesAsync(path, Encoding.Unicode.GetBytes("ab"));

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(
            async () => await Read(path));

        Assert.Equal(ToolErrorCodes.UnsupportedEncoding, exception.Code);
    }

    [Fact]
    public async Task Rejects_nul_as_binary()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("binary.dat");
        await File.WriteAllBytesAsync(path, [0x61, 0x00, 0x62]);

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(
            async () => await Read(path));

        Assert.Equal(ToolErrorCodes.BinaryFile, exception.Code);
    }

    [Fact]
    public async Task Clips_a_megabyte_line_without_losing_continuation_semantics()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("long-line.txt");
        await File.WriteAllTextAsync(path, new string('x', 1024 * 1024), new UTF8Encoding(false));

        ReadFileOutput output = await Read(path);

        Assert.Equal(1, output.ReturnedLines);
        Assert.Equal(1, output.TotalLines);
        LineTruncation truncation = Assert.Single(output.LineTruncations!);
        Assert.Equal(4096, truncation.ReturnedBytes);
        Assert.Equal(1024 * 1024, truncation.TotalBytes);
        Assert.DoesNotContain("L1: ", output.Content, StringComparison.Ordinal);
        Assert.Equal(4096, output.Content!.Length);
    }

    [Fact]
    public async Task Uses_transparent_line_number_continuation()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("page.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\nfour", new UTF8Encoding(false));

        ReadFileOutput first = await Read(path, lineCount: 2);
        ReadFileOutput second = await Read(path, startLine: checked((int)first.NextStartLine!), lineCount: 2);

        Assert.True(first.HasMore);
        Assert.Equal(3, first.NextStartLine);
        Assert.Null(first.TotalLines);
        Assert.Equal("three\nfour", second.Content);
        Assert.False(second.HasMore);
        Assert.Equal(4, second.TotalLines);
    }

    [Fact]
    public async Task CrLf_and_multibyte_text_can_cross_the_internal_buffer_boundary()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("boundary.txt");
        string first = new('x', (64 * 1024) - 1);
        await File.WriteAllTextAsync(path, first + "\r\n😀", new UTF8Encoding(false));

        ReadFileOutput output = await Read(path, lineCount: 2);

        Assert.Equal(2, output.TotalLines);
        Assert.EndsWith("\n😀", output.Content, StringComparison.Ordinal);
        LineTruncation truncation = Assert.Single(output.LineTruncations!);
        Assert.Equal(first.Length, truncation.TotalBytes);
    }

    [Fact]
    public async Task Carriage_return_before_a_surrogate_pair_starts_a_new_line()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("cr-surrogate.txt");
        await File.WriteAllTextAsync(path, "first\r😀", new UTF8Encoding(false));

        ReadFileOutput output = await Read(path);

        Assert.Equal("first\n😀", output.Content);
        Assert.Equal(2, output.TotalLines);
    }

    [Fact]
    public async Task Source_text_that_looks_like_a_line_prefix_is_preserved_verbatim()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("prefix-like-source.txt");
        await File.WriteAllTextAsync(path, "L123: real source\nnext", new UTF8Encoding(false));

        ReadFileOutput output = await Read(path);

        Assert.Equal("L123: real source\nnext", output.Content);
        Assert.Equal(1, output.StartLine);
        Assert.Equal(2, output.EndLine);
    }

    [Fact]
    public async Task Whole_result_budget_removes_trailing_lines_with_continuation()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("escaped.txt");
        string line = new('\u0001', 4000);
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Repeat(line, 5)), new UTF8Encoding(false));

        ReadFileOutput output = await Read(path, lineCount: 5);

        Assert.Equal("byte_budget", output.StopReason);
        Assert.True(output.HasMore);
        Assert.InRange(output.ReturnedLines!.Value, 1, 4);
        Assert.Equal(output.EndLine + 1, output.NextStartLine);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public async Task Large_requested_pages_keep_a_bounded_allocation_cost(int lineCount)
    {
        string line = new('x', ToolBudgets.LineExcerptBytes);
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line + "\n", lineCount + 1)));
        ReadFileService service = new(new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) });
        ReadFileRequest request = lineCount == ToolBudgets.ReadFileLineCountDefault
            ? new(@"C:\budget.txt")
            : new(@"C:\budget.txt", LineCount: lineCount);

        // MemoryStream completes synchronously, so this counter isolates the
        // service from other tests without a machine-speed-dependent deadline.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        ValueTask<ReadFileOutput> pending = service.ReadAsync(request, CancellationToken.None);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(pending.IsCompletedSuccessfully);
        ReadFileOutput output = await pending;

        // The previous repeated tail removal allocated hundreds of MiB even
        // for the default page, and tens of GiB for the maximum page.
        Assert.InRange(allocatedBytes, 1, 192L * 1024 * 1024);
        Assert.Equal(7, output.ReturnedLines);
        Assert.Equal(string.Join('\n', Enumerable.Repeat(line, 7)), output.Content);
        Assert.Equal("byte_budget", output.StopReason);
        Assert.True(output.HasMore);
        Assert.Equal(8, output.NextStartLine);
        Assert.Null(output.TotalLines);
        Assert.Empty(output.LineTruncations!);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Fact]
    public async Task Escaped_text_keeps_the_longest_fitting_prefix_and_reusable_continuation()
    {
        string escapedText = string.Concat(Enumerable.Repeat("\u0001\"\\😀", 200));
        string[] lines = Enumerable.Range(1, 9).Select(number => $"{number}:{escapedText}").ToArray();
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        ReadFileService service = new(new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) });

        ReadFileOutput first = await service.ReadAsync(new ReadFileRequest(@"C:\escaped.txt", LineCount: 9), CancellationToken.None);

        int returned = first.ReturnedLines!.Value;
        Assert.InRange(returned, 1, lines.Length - 1);
        Assert.Equal(string.Join('\n', lines.Take(returned)), first.Content);
        Assert.Equal(lines.Length, first.TotalLines);
        Assert.Equal("byte_budget", first.StopReason);
        Assert.True(first.HasMore);
        Assert.Equal(returned + 1, first.NextStartLine);
        Assert.Empty(first.LineTruncations!);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(first, ToolJsonContext.Default.ReadFileOutput),
            1,
            ToolBudgets.CanonicalResultBytes);

        ReadFileOutput withOneMoreLine = first with
        {
            EndLine = first.EndLine + 1,
            ReturnedLines = returned + 1,
            NextStartLine = first.NextStartLine + 1,
            Content = first.Content + "\n" + lines[returned],
        };
        Assert.True(ToolResultFactory.GetCanonicalByteCount(withOneMoreLine, ToolJsonContext.Default.ReadFileOutput) >
            ToolBudgets.CanonicalResultBytes);

        ReadFileService nextService = new(new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) });
        long nextStartLine = first.NextStartLine ?? throw new InvalidOperationException("Expected a line continuation.");
        ReadFileOutput second = await nextService.ReadAsync(
            new ReadFileRequest(@"C:\escaped.txt", checked((int)nextStartLine), 9), CancellationToken.None);
        Assert.Equal(string.Join('\n', lines.Skip(returned)), second.Content);
        Assert.Equal(lines.Length, second.TotalLines);
        Assert.False(second.HasMore);
        Assert.Equal("end_of_file", second.StopReason);
    }

    [Fact]
    public async Task Maximum_page_of_empty_lines_is_not_discarded_by_the_content_size_bound()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('\n', ToolBudgets.ReadFileLineCountMaximum));
        ReadFileService service = new(new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) });

        ReadFileOutput output = await service.ReadAsync(
            new ReadFileRequest(@"C:\empty-lines.txt", LineCount: ToolBudgets.ReadFileLineCountMaximum), CancellationToken.None);

        Assert.Equal(ToolBudgets.ReadFileLineCountMaximum, output.ReturnedLines);
        Assert.Equal(ToolBudgets.ReadFileLineCountMaximum, output.TotalLines);
        Assert.Equal(new string('\n', ToolBudgets.ReadFileLineCountMaximum - 1), output.Content);
        Assert.False(output.HasMore);
        Assert.Null(output.NextStartLine);
        Assert.Equal("end_of_file", output.StopReason);
    }

    [Fact]
    public async Task Content_size_bound_and_json_escaping_preserve_complete_utf8_scalars()
    {
        string line = string.Concat(Enumerable.Repeat("😀", ToolBudgets.LineExcerptBytes / 4));
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat(line, 9)));
        ReadFileService service = new(new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) });

        ReadFileOutput output = await service.ReadAsync(new ReadFileRequest(@"C:\utf8-budget.txt", LineCount: 9), CancellationToken.None);

        Assert.Equal(2, output.ReturnedLines);
        Assert.Equal(line + "\n" + line, output.Content);
        Assert.Equal(9, output.TotalLines);
        Assert.Equal(3, output.NextStartLine);
        Assert.True(output.HasMore);
        Assert.Equal("byte_budget", output.StopReason);
        Assert.Empty(output.LineTruncations!);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_single_escaped_line_still_clips_when_the_paging_envelope_leaves_too_little_space(int lineCount)
    {
        string path = @"C:\" + string.Concat(Enumerable.Repeat(@"segment\", 1500)) + "sample.txt";
        string firstLine = new('\u0001', ToolBudgets.LineExcerptBytes);
        byte[] bytes = Encoding.UTF8.GetBytes(lineCount == 1 ? firstLine : firstLine + "\nlater");
        ReadFileService service = new(new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) });

        ReadFileOutput output = await service.ReadAsync(new ReadFileRequest(path, LineCount: lineCount), CancellationToken.None);

        Assert.Equal(1, output.ReturnedLines);
        Assert.Equal(lineCount, output.TotalLines);
        LineTruncation truncation = Assert.Single(output.LineTruncations!);
        Assert.Equal(firstLine.Length, truncation.TotalBytes);
        Assert.InRange(truncation.ReturnedBytes, 1, ToolBudgets.LineExcerptBytes - 1);
        Assert.Equal(firstLine[..truncation.ReturnedBytes], output.Content);
        Assert.Equal(lineCount == 2, output.HasMore);
        Assert.Equal(lineCount == 2 ? 2L : (long?)null, output.NextStartLine);
        Assert.Equal(lineCount == 2 ? "byte_budget" : "end_of_file", output.StopReason);
        Assert.InRange(
            ToolResultFactory.GetCanonicalByteCount(output, ToolJsonContext.Default.ReadFileOutput),
            1,
            ToolBudgets.CanonicalResultBytes);
    }

    [Fact]
    public async Task Material_change_during_read_fails_instead_of_returning_mixed_content()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("value");
        TestFileSystemPlatform platform = new()
        {
            ReadStream = new MemoryStream(bytes),
            InitialFileStamp = new FileChangeStamp(1, 1, bytes.Length, 10),
            FinalHandleStamp = new FileChangeStamp(1, 1, bytes.Length, 11),
            FinalPathStamp = new FileChangeStamp(1, 1, bytes.Length, 11),
        };
        ReadFileService service = new(platform);

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(
            async () => await service.ReadAsync(new ReadFileRequest(@"C:\value.txt"), CancellationToken.None));

        Assert.Equal(ToolErrorCodes.ChangedDuringRead, exception.Code);
    }

    [Fact]
    public async Task Directory_path_is_not_misreported_as_access_denied()
    {
        using TestWorkspace workspace = new();
        ReadFileService service = new(new WindowsFileSystemPlatform());

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(
            async () => await service.ReadAsync(new ReadFileRequest(workspace.Root), CancellationToken.None));

        Assert.Equal(ToolErrorCodes.PathNotFile, exception.Code);
    }

    [Fact]
    public async Task Cancellation_propagates_without_becoming_a_tool_error()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("cancel.txt");
        await File.WriteAllTextAsync(path, "value");
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        WindowsFileSystemPlatform platform = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ReadFileService(platform).ReadAsync(
                new ReadFileRequest(path),
                cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task Cancellation_after_reading_finishes_prevents_a_successful_result(int lineCount)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat(new string('x', ToolBudgets.LineExcerptBytes) + "\n", lineCount)));
        using CancellationTokenSource cancellation = new();
        CancelAfterReadFileSystem platform = new(
            new TestFileSystemPlatform { ReadStream = new MemoryStream(bytes) }, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ReadFileService(platform).ReadAsync(
                new ReadFileRequest(@"C:\cancel-after-read.txt", LineCount: ToolBudgets.ReadFileLineCountMaximum),
                cancellation.Token));

        Assert.True(platform.ReadCompleted);
        Assert.True(cancellation.IsCancellationRequested);
    }

    private sealed class CancelAfterReadFileSystem(
        IFileSystemPlatform inner,
        CancellationTokenSource cancellation) : IFileSystemPlatform
    {
        public bool ReadCompleted { get; private set; }

        public string NormalizeAbsolutePath(string path) => inner.NormalizeAbsolutePath(path);

        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

        public FileChangeStamp GetFileChangeStamp(string path)
        {
            FileChangeStamp stamp = inner.GetFileChangeStamp(path);
            ReadCompleted = true;
            cancellation.Cancel();
            return stamp;
        }

        public FileChangeStamp GetFileChangeStamp(Stream stream) => inner.GetFileChangeStamp(stream);

        public DirectoryChangeStamp GetDirectoryChangeStamp(string path) => inner.GetDirectoryChangeStamp(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public IEnumerable<FileSystemEntrySnapshot> EnumerateDirectory(string path, CancellationToken cancellationToken) =>
            inner.EnumerateDirectory(path, cancellationToken);
    }

    private static async ValueTask<ReadFileOutput> Read(
        string path,
        int startLine = 1,
        int lineCount = ToolBudgets.ReadFileLineCountDefault)
    {
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        ReadFileRequest request = validator.Validate(new ReadFileRequest(path, startLine, lineCount));
        return await new ReadFileService(platform).ReadAsync(request, CancellationToken.None);
    }
}
