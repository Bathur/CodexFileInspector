using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform.Windows;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class RequestValidatorTests
{
    private readonly ToolRequestValidator _validator = new(new WindowsFileSystemPlatform());

    [Fact]
    public void Read_file_applies_defaults_and_normalizes_path()
    {
        ReadFileRequest result = _validator.Validate(new ReadFileRequest(@"C:/work/file.cs"));

        Assert.Equal(@"C:\work\file.cs", result.Path);
        Assert.Equal(1, result.StartLine);
        Assert.Equal(200, result.LineCount);
    }

    [Fact]
    public void Grep_rejects_context_for_non_match_mode()
    {
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GrepRequest(@"C:\work", "value", PatternKind.Literal, OutputMode: GrepOutputMode.Count, ContextLines: 1)));

        Assert.Equal(ToolErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("context_lines", exception.Field);
    }

    [Theory]
    [InlineData(@"..\*.cs")]
    [InlineData("../*.cs")]
    [InlineData("!bin/**")]
    [InlineData("/rooted/**")]
    [InlineData("C:/*.cs")]
    public void Glob_rejects_non_relative_forward_slash_contract_forms(string pattern)
    {
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GlobRequest(@"C:\work", [pattern])));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal("include_globs", exception.Field);
        Assert.Equal(0, exception.Index);
    }

    [Fact]
    public void List_directory_rejects_offset_plus_page_over_bound()
    {
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new ListDirectoryRequest(@"C:\work", 99_000, 2_000)));

        Assert.Equal(ToolErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal(100_000, exception.Limit);
        Assert.Equal(101_000, exception.Actual);
    }

    [Fact]
    public void Search_inputs_reject_embedded_nul_before_process_start()
    {
        ToolValidationException pattern = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GrepRequest(@"C:\work", "a\0b", PatternKind.Literal)));
        ToolValidationException glob = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GlobRequest(@"C:\work", ["a\0b"])));

        Assert.Equal(ToolErrorCodes.InvalidArgument, pattern.Code);
        Assert.Equal(ToolErrorCodes.InvalidPattern, glob.Code);
    }
}
