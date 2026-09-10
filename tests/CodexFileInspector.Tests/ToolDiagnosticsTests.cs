using System.Text;
using System.Text.Json;
using CodexFileInspector.Contracts;
using CodexFileInspector.Diagnostics;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Reading;
using CodexFileInspector.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ToolDiagnosticsTests
{
    [Fact]
    public void Success_never_resolves_diagnostics_or_encodes_arguments()
    {
        ReadFileOutput output = new() { Status = ToolStatus.Success, Content = "normal content" };
        CallToolResult result = ToolDiagnostics.CreateResult(
            output, ToolJsonContext.Default.ReadFileOutput, new RejectingServices(), "read_file", new object());

        Assert.Equal("normal content", result.StructuredContent?.GetProperty("content").GetString());
        Assert.False(result.IsError);
    }

    [Fact]
    public void Disabled_writer_does_not_encode_an_event()
    {
        RecordingWriter writer = new() { IsEnabled = false };
        using ServiceProvider services = new ServiceCollection().AddSingleton<IDiagnosticWriter>(writer).BuildServiceProvider();
        CallToolResult result = ToolDiagnostics.CreateResult(
            Failure(), ToolJsonContext.Default.ReadFileOutput, services, "read_file", new object());

        Assert.True(result.IsError);
        Assert.Empty(writer.Records);
    }

    [Fact]
    public void Writer_failure_does_not_replace_the_standard_result()
    {
        RecordingWriter writer = new() { ThrowOnWrite = true };
        using ServiceProvider services = new ServiceCollection().AddSingleton<IDiagnosticWriter>(writer).BuildServiceProvider();
        ReadFileOutput output = Failure();
        CallToolResult expected = ToolResultFactory.Create(output, ToolJsonContext.Default.ReadFileOutput);
        CallToolResult actual = ToolDiagnostics.CreateResult(
            output, ToolJsonContext.Default.ReadFileOutput, services, "read_file", new ReadFileRequest("relative.txt"));

        Assert.Equal(expected.StructuredContent?.GetRawText(), actual.StructuredContent?.GetRawText());
        Assert.Equal(Assert.IsType<TextContentBlock>(Assert.Single(expected.Content)).Text,
            Assert.IsType<TextContentBlock>(Assert.Single(actual.Content)).Text);
        Assert.Equal(1, writer.Attempts);
    }

    [Fact]
    public void Partial_keeps_warnings_but_does_not_copy_matched_content()
    {
        RecordingWriter writer = new();
        using ServiceProvider services = new ServiceCollection().AddSingleton<IDiagnosticWriter>(writer).BuildServiceProvider();
        GrepOutput output = new()
        {
            Status = ToolStatus.Partial,
            Warnings = [new("text_anomaly", "Binary data was encountered.", @"C:\sample.txt")],
            WarningsOmitted = 2,
            Blocks = [new(@"C:\sample.txt", 1, 1, "DO_NOT_LOG_FILE_CONTENT", [new(1, 1)], [], false)],
        };
        CallToolResult result = ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.GrepOutput, services,
            "grep", new GrepRequest(@"C:\sample.txt", "needle", PatternKind.Literal));

        Assert.False(result.IsError);
        byte[] bytes = Assert.Single(writer.Records);
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("warnings_omitted").GetInt32());
        Assert.True(JsonElement.DeepEquals(result.StructuredContent!.Value.GetProperty("warnings"),
            document.RootElement.GetProperty("warnings")));
        Assert.DoesNotContain("DO_NOT_LOG_FILE_CONTENT", Encoding.UTF8.GetString(bytes));
        Assert.False(document.RootElement.TryGetProperty("blocks", out _));
    }

    [Fact]
    public void Error_captures_bound_arguments_and_existing_exception_once()
    {
        RecordingWriter writer = new();
        using ServiceProvider services = new ServiceCollection().AddSingleton<IDiagnosticWriter>(writer).BuildServiceProvider();
        Exception exception = CaptureException();
        ToolDiagnostics.CreateResult(Failure(), ToolJsonContext.Default.ReadFileOutput, services,
            "read_file", new ReadFileRequest(@"C:\folder\..\sample.txt", 3, 7), exception);

        using JsonDocument document = JsonDocument.Parse(Assert.Single(writer.Records));
        JsonElement record = document.RootElement;
        Assert.Equal(@"C:\folder\..\sample.txt", record.GetProperty("arguments").GetProperty("path").GetString());
        Assert.Equal(3, record.GetProperty("arguments").GetProperty("start_line").GetInt32());
        Assert.Equal(7, record.GetProperty("arguments").GetProperty("line_count").GetInt32());
        Assert.Equal(typeof(InvalidOperationException).FullName, record.GetProperty("exception").GetProperty("type").GetString());
        Assert.Contains(nameof(CaptureException), record.GetProperty("exception").GetProperty("stack").GetString());
        Assert.Empty(record.GetProperty("truncations").EnumerateArray());
    }

    [Fact]
    public async Task Cancellation_stays_outside_the_diagnostic_result_boundary()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("cancel.txt");
        await File.WriteAllTextAsync(path, "text");
        RecordingWriter writer = new();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IDiagnosticWriter>(writer)
            .AddSingleton<IFileSystemPlatform, WindowsFileSystemPlatform>()
            .AddSingleton<ToolRequestValidator>()
            .AddSingleton<ReadFileService>()
            .BuildServiceProvider();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await FileInspectorTools.ReadFileAsync(path, services, cancellationToken: cancellation.Token));
        Assert.Empty(writer.Records);
    }

    [Theory]
    [InlineData("--diagnostics", "--version")]
    [InlineData("--version", "--diagnostics")]
    public void Diagnostic_switch_is_consumed_before_host_argument_processing(string first, string second)
    {
        DiagnosticCommandLine parsed = DiagnosticCommandLine.Parse([first, second]);
        Assert.True(parsed.Enabled);
        Assert.Equal(["--version"], parsed.HostArguments);
        DiagnosticCommandLine disabled = DiagnosticCommandLine.Parse(["--environment", "Development"]);
        Assert.False(disabled.Enabled);
        Assert.Equal(["--environment", "Development"], disabled.HostArguments);
    }

    [Fact]
    public void Huge_escaped_inputs_remain_valid_bounded_json_with_explicit_fragments()
    {
        string pattern = "BEGIN" + string.Concat(Enumerable.Repeat("\u0001😀\"\\", 150_000)) + "END";
        GrepRequest arguments = new(@"C:\x", pattern, PatternKind.Regex,
            IncludeGlobs: Enumerable.Repeat(new string('界', 100_000), 100).ToArray());
        GrepOutput output = new()
        {
            Status = ToolStatus.Error,
            Error = new("invalid_argument", "pattern is too long", false, "pattern", Limit: 8192, Actual: pattern.Length),
        };
        CallToolResult result = ToolResultFactory.Create(output, ToolJsonContext.Default.GrepOutput);
        byte[] bytes = DiagnosticRecordEncoder.Encode("grep", arguments, result.StructuredContent!.Value,
            new Exception(new string('e', 1_000_000)));

        Assert.InRange(bytes.Length, 1, DiagnosticRecordEncoder.MaximumRecordBytes);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement record = document.RootElement;
        string retained = record.GetProperty("arguments").GetProperty("pattern").GetString()!;
        Assert.StartsWith("BEGIN", retained);
        Assert.EndsWith("END", retained);
        Assert.DoesNotContain('\uFFFD', retained);
        JsonElement patternTruncation = record.GetProperty("truncations").EnumerateArray()
            .Single(item => item.GetProperty("field").GetString() == "arguments.pattern");
        Assert.Equal(pattern.Length, patternTruncation.GetProperty("original_length").GetInt32());
        Assert.Contains(record.GetProperty("truncations").EnumerateArray(),
            item => item.GetProperty("field").GetString() == "arguments.include_globs"
                && item.GetProperty("unit").GetString() == "array_items");
        Assert.True(JsonElement.DeepEquals(result.StructuredContent.Value.GetProperty("error"), record.GetProperty("error")));
    }

    [Fact]
    public void Ordinary_valid_size_arguments_are_preserved_even_with_json_escaping()
    {
        string pattern = new('\u0001', ToolBudgets.GrepPatternBytes);
        string[] globs = Enumerable.Repeat(new string('\u0002', ToolBudgets.GlobBytes), 16).ToArray();
        GrepRequest arguments = new(@"C:\a", pattern, PatternKind.Literal, IncludeGlobs: globs);
        GrepOutput output = new() { Status = ToolStatus.Error, Error = new("io_error", "IO failed", true) };
        CallToolResult result = ToolResultFactory.Create(output, ToolJsonContext.Default.GrepOutput);
        byte[] bytes = DiagnosticRecordEncoder.Encode("grep", arguments, result.StructuredContent!.Value, null);

        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Empty(document.RootElement.GetProperty("truncations").EnumerateArray());
        Assert.Equal(pattern, document.RootElement.GetProperty("arguments").GetProperty("pattern").GetString());
        Assert.Equal(globs, document.RootElement.GetProperty("arguments").GetProperty("include_globs")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public void Offending_array_item_survives_when_other_values_are_too_large()
    {
        GlobRequest arguments = new(@"C:\x", [new string('x', 300_000), "bad\\glob"]);
        GlobOutput output = new() { Status = ToolStatus.Error, Error = new("invalid_pattern", "bad glob", false, "include_globs", 1) };
        CallToolResult result = ToolResultFactory.Create(output, ToolJsonContext.Default.GlobOutput);
        byte[] bytes = DiagnosticRecordEncoder.Encode("glob", arguments, result.StructuredContent!.Value, null);

        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal("bad\\glob", document.RootElement.GetProperty("argument_issue").GetProperty("value").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("argument_issue").GetProperty("index").GetInt32());
    }

    private static ReadFileOutput Failure() => new()
    {
        Status = ToolStatus.Error,
        Error = new("path_not_absolute", "Absolute path required.", false, "path"),
    };

    private static Exception CaptureException()
    {
        try
        {
            throw new InvalidOperationException("Existing exception details");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private sealed class RejectingServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("Success touched diagnostics.");
    }

    private sealed class RecordingWriter : IDiagnosticWriter
    {
        public bool IsEnabled { get; init; } = true;
        public bool ThrowOnWrite { get; init; }
        public List<byte[]> Records { get; } = [];
        public int Attempts { get; private set; }

        public void TryWrite(byte[] record)
        {
            Attempts++;
            if (ThrowOnWrite)
            {
                throw new IOException("Diagnostic destination unavailable.");
            }

            Records.Add(record);
        }

        public void Dispose() { }
    }
}
