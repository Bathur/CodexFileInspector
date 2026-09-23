using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class RipgrepCommandLineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Individually_valid_quoted_arguments_are_rejected_before_process_start(bool grep)
    {
        using TestWorkspace workspace = new();
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        RipgrepInvocationBuilder builder = new(platform);
        string[] globs = Enumerable.Repeat(new string('"', ToolBudgets.GlobBytes), 16).ToArray();

        ToolExecutionException failure = Assert.Throws<ToolExecutionException>(() =>
        {
            if (grep)
            {
                GrepRequest request = validator.Validate(new GrepRequest(workspace.Root,
                    new string('"', ToolBudgets.GrepPatternBytes), PatternKind.Literal, IncludeGlobs: globs));
                builder.BuildGrep(request);
            }
            else
            {
                GlobRequest request = validator.Validate(new GlobRequest(workspace.Root, globs));
                builder.BuildGlob(request);
            }
        });

        Assert.Equal(ToolErrorCodes.InvalidArgument, failure.Code);
        Assert.Equal(ToolBudgets.RipgrepCommandLineCharacters, failure.Limit);
        Assert.True(failure.Actual > failure.Limit);
        Assert.False(ToolExceptionMapper.Map(failure, workspace.Root).Retryable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_arguments_that_fit_after_quoting_remain_usable(bool quotes)
    {
        using TestWorkspace workspace = new();
        WindowsFileSystemPlatform platform = new();
        ToolRequestValidator validator = new(platform);
        string pattern = new(quotes ? '"' : 'x', ToolBudgets.GrepPatternBytes);
        string[] globs = Enumerable.Repeat(new string('a', ToolBudgets.GlobBytes), quotes ? 8 : 16).ToArray();
        GrepRequest request = validator.Validate(new GrepRequest(workspace.Root, pattern,
            PatternKind.Literal, IncludeGlobs: globs));

        RipgrepRunRequest invocation = new RipgrepInvocationBuilder(platform).BuildGrep(request);

        Assert.Contains(pattern, invocation.Arguments);
        Assert.Equal(workspace.Root, invocation.Arguments[^1]);
    }

    [Theory]
    [InlineData("before-quote")]
    [InlineData("trailing")]
    [InlineData("unicode-whitespace")]
    public void Backslashes_that_expand_inside_quoted_arguments_count_towards_the_limit(string shape)
    {
        using TestWorkspace workspace = new();
        WindowsFileSystemPlatform platform = new();
        string pattern = shape switch
        {
            "before-quote" => new string('\\', ToolBudgets.GrepPatternBytes - 1) + '"',
            "trailing" => " " + new string('\\', ToolBudgets.GrepPatternBytes - 1),
            _ => "\u2003" + new string('\\', ToolBudgets.GrepPatternBytes - 3),
        };
        string[] globs = Enumerable.Repeat(new string('"', ToolBudgets.GlobBytes), 8).ToArray();
        GrepRequest request = new ToolRequestValidator(platform).Validate(new GrepRequest(
            workspace.Root, pattern, PatternKind.Literal, IncludeGlobs: globs));

        ToolExecutionException failure = Assert.Throws<ToolExecutionException>(() =>
            new RipgrepInvocationBuilder(platform).BuildGrep(request));

        Assert.Equal(ToolErrorCodes.InvalidArgument, failure.Code);
        Assert.True(failure.Actual > failure.Limit);
    }
}
