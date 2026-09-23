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

    [Theory]
    [InlineData(@"\\?\C:\work\file.txt")]
    [InlineData("//?/C:/work/file.txt")]
    [InlineData(@"\/?\C:/work/file.txt")]
    [InlineData(@"\\.\C:\work\file.txt")]
    [InlineData("//./C:/work/file.txt")]
    [InlineData(@"/\./C:/work/file.txt")]
    [InlineData(@"\??\C:\work\file.txt")]
    [InlineData("/??/C:/work/file.txt")]
    [InlineData(@"\\??\C:\work\file.txt")]
    [InlineData("//??/C:/work/file.txt")]
    public void Concrete_paths_reject_device_namespaces_with_any_separator_style(string path)
    {
        Assert.All<Action>(
            [
                () => _validator.Validate(new ReadFileRequest(path)),
                () => _validator.Validate(new GrepRequest(path, "value", PatternKind.Literal)),
                () => _validator.Validate(new GlobRequest(path, ["*.txt"])),
                () => _validator.Validate(new ListDirectoryRequest(path)),
            ],
            validate =>
            {
                PathValidationException exception = Assert.Throws<PathValidationException>(validate);
                ToolError error = ToolExceptionMapper.Map(exception, path);
                Assert.Equal(ToolErrorCodes.InvalidPath, error.Code);
                Assert.Equal("path", error.Field);
                Assert.False(error.Retryable);
            });
    }

    [Theory]
    [InlineData("C:/work/child/../file.txt", @"C:\work\file.txt")]
    [InlineData(@"Z:\work/file.txt", @"Z:\work\file.txt")]
    [InlineData("//server/share/work/file.txt", @"\\server\share\work\file.txt")]
    [InlineData(@"\/server\share/work/file.txt", @"\\server\share\work\file.txt")]
    public void Concrete_paths_preserve_ordinary_drive_and_unc_normalization(string path, string expected)
    {
        Assert.Equal(expected, _validator.Validate(new ReadFileRequest(path)).Path);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void Grep_rejects_undefined_pattern_kind_before_path_validation(int value)
    {
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GrepRequest("relative-path", "value", (PatternKind)value)));

        ToolError error = ToolExceptionMapper.Map(exception, null);
        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("pattern_kind", error.Field);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void Grep_rejects_undefined_output_mode_before_path_validation(int value)
    {
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GrepRequest("relative-path", "value", PatternKind.Literal, OutputMode: (GrepOutputMode)value)));

        ToolError error = ToolExceptionMapper.Map(exception, null);
        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("output_mode", error.Field);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(PatternKind.Literal, GrepOutputMode.Matches)]
    [InlineData(PatternKind.Literal, GrepOutputMode.FilesWithMatches)]
    [InlineData(PatternKind.Literal, GrepOutputMode.Count)]
    [InlineData(PatternKind.Regex, GrepOutputMode.Matches)]
    [InlineData(PatternKind.Regex, GrepOutputMode.FilesWithMatches)]
    [InlineData(PatternKind.Regex, GrepOutputMode.Count)]
    public void Grep_accepts_declared_modes_and_omitted_optional_globs(PatternKind patternKind, GrepOutputMode mode)
    {
        GrepRequest request = new(@"C:\work", "value", patternKind, OutputMode: mode);

        Assert.Equal(request, _validator.Validate(request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Glob_rejects_null_or_empty_required_includes(bool useNull)
    {
        IReadOnlyList<string>? includes = useNull ? null : [];
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GlobRequest(@"C:\work", includes!)));

        ToolError error = ToolExceptionMapper.Map(exception, null);
        Assert.Equal(ToolErrorCodes.InvalidArgument, error.Code);
        Assert.Equal("include_globs", error.Field);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Glob_rejects_null_or_empty_pattern_with_its_index(string? pattern)
    {
        ToolValidationException exception = Assert.Throws<ToolValidationException>(() => _validator.Validate(
            new GlobRequest(@"C:\work", ["*.txt", pattern!])));

        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal("include_globs", exception.Field);
        Assert.Equal(1, exception.Index);
    }

    [Fact]
    public void Glob_accepts_valid_required_includes_and_omitted_excludes()
    {
        GlobRequest request = new(@"C:\work", ["*.txt"]);

        Assert.Equal(request, _validator.Validate(request));
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
