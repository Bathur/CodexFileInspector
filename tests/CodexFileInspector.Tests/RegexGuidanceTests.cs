using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using CodexFileInspector.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class RegexGuidanceTests
{
    private const string GenericMessage =
        "pattern cannot be compiled by bundled ripgrep 15.2.0 for this search.";

    [Fact]
    public void Grep_pattern_kind_describes_the_fixed_regex_engine()
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == "grep").ProtocolTool;
        string description = tool.InputSchema.GetProperty("properties")
            .GetProperty("pattern_kind").GetProperty("description").GetString()!;

        Assert.Contains("literal or regex", description, StringComparison.Ordinal);
        Assert.Contains(
            "Regex uses ripgrep's default Rust engine; PCRE2 is not supported.",
            description,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foo(?=bar)", "lookaround")]
    [InlineData("foo(?!baz)", "lookaround")]
    [InlineData("(?<=foo)bar", "lookaround")]
    [InlineData("(?<!foo)bar", "lookaround")]
    [InlineData("(a)\\1", "backreferences")]
    public async Task Pinned_unsupported_constructs_explain_the_tool_capability(
        string pattern,
        string feature)
    {
        using TestWorkspace workspace = CreateFixture();

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Search(workspace.Root, pattern, PatternKind.Regex));

        AssertQueryError(exception);
        Assert.Contains(feature, exception.Message, StringComparison.Ordinal);
        Assert.Contains("PCRE2 is not supported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Rewrite the pattern", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--pcre2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PatternKind.Regex, "\\(\\?=x\\)|\\\\1")]
    [InlineData(PatternKind.Literal, "foo(?=bar)")]
    [InlineData(PatternKind.Literal, "\\1")]
    public async Task Valid_escaped_and_literal_patterns_are_not_rejected_by_feature_spelling(
        PatternKind kind,
        string pattern)
    {
        using TestWorkspace workspace = CreateFixture();

        GrepOutput output = await Search(workspace.Root, pattern, kind);

        Assert.Equal(ToolStatus.Success, output.Status);
        Assert.Equal(1, output.ReturnedResults);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("a{10000000}")]
    [InlineData("\\n")]
    [InlineData("\\x00")]
    public async Task Other_pinned_compilation_failures_keep_the_existing_message(string pattern)
    {
        using TestWorkspace workspace = CreateFixture();

        ToolExecutionException exception = await Assert.ThrowsAsync<ToolExecutionException>(async () =>
            await Search(workspace.Root, pattern, PatternKind.Regex));

        AssertQueryError(exception);
        Assert.Equal(GenericMessage, exception.Message);
    }

    [Theory]
    [InlineData("rg: regex parse error:\n    error: backreferences are not supported\n    ^\nerror: unclosed group\n")]
    [InlineData("rg: regex parse error:\n    (\n    ^\nerror: unclosed group\n\nerror: backreferences are not supported\n")]
    [InlineData("rg: regex parse error:\n    (\n    ^\n\nerror: backreferences are not supported\n")]
    [InlineData("rg: regex parse error:\n    (\n    ^\nrg: C:\\root\\other.txt: read error\nerror: backreferences are not supported\n")]
    [InlineData("rg: compiled regex exceeds size limit of 104857600\nerror: backreferences are not supported\n")]
    public void Misleading_text_outside_the_parse_reason_does_not_change_the_failure_kind(string standardError)
    {
        RipgrepRunResult run = new(2, 0, false, false, standardError, false);

        ToolExecutionException exception = Assert.IsType<ToolExecutionException>(
            RipgrepFailureClassifier.QueryFailure(run, searchesContents: true, [], []));

        AssertQueryError(exception);
        Assert.Equal(GenericMessage, exception.Message);
    }

    [Fact]
    public void A_truncated_unsupported_reason_keeps_the_generic_message()
    {
        RipgrepRunResult run = new(
            2, 0, false, false,
            "rg: regex parse error:\n    (?:(a)\\1)\n          ^^\nerror: backreferences are not supp",
            true);

        ToolExecutionException exception = Assert.IsType<ToolExecutionException>(
            RipgrepFailureClassifier.QueryFailure(run, searchesContents: true, [], []));

        AssertQueryError(exception);
        Assert.Equal(GenericMessage, exception.Message);
    }

    [Fact]
    public void Unsupported_words_in_a_path_diagnostic_are_not_a_query_failure()
    {
        RipgrepRunResult run = new(
            2, 0, false, false,
            "rg: C:\\root\\regex parse error backreferences are not supported.txt: Access is denied. (os error 5)\n",
            false);

        Assert.Null(RipgrepFailureClassifier.QueryFailure(run, searchesContents: true, [], []));
        Assert.True(RipgrepFailureClassifier.HasTraversalFailure(run.StandardError));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 0, true)]
    public void Unsupported_guidance_requires_the_query_compilation_failure_phase(
        int exitCode,
        int recordsRead,
        bool stoppedEarly)
    {
        RipgrepRunResult run = new(
            exitCode, recordsRead, stoppedEarly, false,
            "rg: regex parse error:\n    (?:(a)\\1)\n          ^^\nerror: backreferences are not supported\n",
            false);

        Assert.Null(RipgrepFailureClassifier.QueryFailure(run, searchesContents: true, [], []));
    }

    private static void AssertQueryError(ToolExecutionException exception)
    {
        Assert.Equal(ToolErrorCodes.InvalidPattern, exception.Code);
        Assert.Equal("pattern", exception.Field);
        Assert.False(ToolExceptionMapper.Map(exception, @"C:\root").Retryable);
    }

    private static TestWorkspace CreateFixture()
    {
        TestWorkspace workspace = new();
        File.WriteAllText(workspace.PathFor("patterns.txt"), "foobar aa foo(?=bar) (?=x) \\1\n");
        return workspace;
    }

    private static ValueTask<GrepOutput> Search(string path, string pattern, PatternKind kind)
    {
        WindowsFileSystemPlatform platform = new();
        GrepRequest request = new ToolRequestValidator(platform).Validate(new GrepRequest(
            path, pattern, kind, RespectIgnoreFiles: false));
        GrepService service = new(
            platform,
            new RipgrepInvocationBuilder(platform),
            new RipgrepRunner(new WindowsJobProcessPlatform()));
        return service.SearchAsync(request, CancellationToken.None);
    }
}
