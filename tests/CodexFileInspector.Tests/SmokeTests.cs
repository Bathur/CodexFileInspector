using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Test_host_is_available() => Assert.True(OperatingSystem.IsWindows());

    [Fact]
    public void Error_registry_contains_unique_canonical_codes()
    {
        Assert.Equal(ToolErrorRegistry.All.Count, ToolErrorRegistry.All.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("unsupported_network_path", ToolErrorRegistry.All.Keys);
    }

    [Theory]
    [InlineData(@"C:\work\file.cs")]
    [InlineData(@"C:/work/file.cs")]
    [InlineData(@"\\server\share\folder\file.cs")]
    public void Windows_path_accepts_fully_qualified_logical_paths(string path)
    {
        string normalized = WindowsPath.NormalizeAbsolute(path);

        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.DoesNotContain('/', normalized);
    }

    [Theory]
    [InlineData("relative/file.cs", ToolErrorCodes.PathNotAbsolute)]
    [InlineData(@"C:file.cs", ToolErrorCodes.PathNotAbsolute)]
    [InlineData(@"\Windows\win.ini", ToolErrorCodes.PathNotAbsolute)]
    [InlineData(@"\\?\C:\Windows\win.ini", ToolErrorCodes.InvalidPath)]
    [InlineData(@"\\.\PhysicalDrive0", ToolErrorCodes.InvalidPath)]
    public void Windows_path_rejects_non_contract_forms(string path, string expectedCode)
    {
        PathValidationException exception = Assert.Throws<PathValidationException>(
            () => WindowsPath.NormalizeAbsolute(path));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Utf8_truncation_preserves_scalar_boundaries()
    {
        string result = Utf8Budget.Truncate("ab😀cd", 6, out bool truncated);

        Assert.True(truncated);
        Assert.Equal("ab😀", result);
        Assert.Equal(6, System.Text.Encoding.UTF8.GetByteCount(result));
    }

    [Fact]
    public void Tool_result_mirrors_the_same_compact_json()
    {
        ReadFileOutput output = new()
        {
            Status = ToolStatus.Success,
            Path = @"C:\work\file.cs",
            Encoding = "utf-8",
            StartLine = 1,
            EndLine = 1,
            ReturnedLines = 1,
            TotalLines = 1,
            HasMore = false,
            Content = "value",
            LineTruncations = [],
        };

        CallToolResult result = ToolResultFactory.Create(output, ToolJsonContext.Default.ReadFileOutput);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));

        Assert.Equal(result.StructuredContent?.GetRawText(), text.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Bundled_ripgrep_is_copied_to_the_installation_relative_path()
    {
        string path = BundledRipgrep.RequireExecutable();

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith(@"tools\ripgrep\rg.exe", path, StringComparison.OrdinalIgnoreCase);
    }
}
