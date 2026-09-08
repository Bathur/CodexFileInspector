using System.Text.Json;
using System.Reflection;
using CodexFileInspector.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ToolContractTests
{
    [Fact]
    public void Catalog_contains_exactly_the_approved_four_tools()
    {
        IReadOnlyList<McpServerTool> tools = ToolContractCatalog.Create();

        Assert.Equal(["glob", "grep", "list_directory", "read_file"], tools.Select(tool => tool.ProtocolTool.Name));
    }

    [Fact]
    public void Every_tool_has_object_schemas_and_read_only_annotations()
    {
        foreach (Tool tool in ToolContractCatalog.Create().Select(item => item.ProtocolTool))
        {
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.Equal("object", tool.OutputSchema?.GetProperty("type").GetString());
            Assert.NotNull(tool.Annotations);
            Assert.True(tool.Annotations.ReadOnlyHint);
            Assert.False(tool.Annotations.DestructiveHint);
            Assert.True(tool.Annotations.IdempotentHint);
            Assert.False(tool.Annotations.OpenWorldHint);
        }
    }

    [Fact]
    public void Read_file_schema_has_one_required_argument_and_expected_defaults()
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == "read_file").ProtocolTool;
        JsonElement schema = tool.InputSchema;

        Assert.Equal(["path"], schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        JsonElement startLine = schema.GetProperty("properties").GetProperty("start_line");
        JsonElement lineCount = schema.GetProperty("properties").GetProperty("line_count");
        Assert.Equal(1, startLine.GetProperty("default").GetInt32());
        Assert.Equal(1, startLine.GetProperty("minimum").GetInt32());
        Assert.Equal(int.MaxValue, startLine.GetProperty("maximum").GetInt32());
        Assert.Equal(200, lineCount.GetProperty("default").GetInt32());
        Assert.Equal(1, lineCount.GetProperty("minimum").GetInt32());
        Assert.Equal(2000, lineCount.GetProperty("maximum").GetInt32());
        Assert.False(schema.GetProperty("properties").TryGetProperty("include_line_numbers", out _));
    }

    [Fact]
    public void Grep_schema_keeps_pattern_kind_explicit_and_context_optional()
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == "grep").ProtocolTool;
        JsonElement schema = tool.InputSchema;
        JsonElement properties = schema.GetProperty("properties");

        Assert.Equal(
            ["path", "pattern", "pattern_kind"],
            schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["literal", "regex"],
            properties.GetProperty("pattern_kind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(0, properties.GetProperty("context_lines").GetProperty("default").GetInt32());
        Assert.Equal(0, properties.GetProperty("context_lines").GetProperty("minimum").GetInt32());
        Assert.Equal(20, properties.GetProperty("context_lines").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void Grep_output_mode_guidance_defines_result_units_and_full_search_totals()
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == "grep").ProtocolTool;
        string description = tool.InputSchema.GetProperty("properties")
            .GetProperty("output_mode").GetProperty("description").GetString()!;

        Assert.Contains("Pagination and returned_results/total_results", description, StringComparison.Ordinal);
        Assert.Contains("matching lines in matches", description, StringComparison.Ordinal);
        Assert.Contains("matching files in files_with_matches/count", description, StringComparison.Ordinal);
        Assert.Contains("not context blocks or occurrences", description, StringComparison.Ordinal);
        Assert.Contains("totals summarize the full search, not the page", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_file_guidance_separates_paging_from_line_clipping()
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == "read_file").ProtocolTool;

        Assert.Contains("has_more indicates later lines", tool.Description!, StringComparison.Ordinal);
        Assert.Contains("line_truncations reports clipped text within returned lines", tool.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void Glob_schema_requires_plural_include_globs()
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == "glob").ProtocolTool;

        Assert.Equal(
            ["path", "include_globs"],
            tool.InputSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            1,
            tool.InputSchema.GetProperty("properties").GetProperty("include_globs").GetProperty("minItems").GetInt32());
    }

    [Theory]
    [InlineData("grep")]
    [InlineData("glob")]
    public void Hidden_guidance_describes_native_allow_rule_precedence(string toolName)
    {
        Tool tool = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == toolName).ProtocolTool;
        string description = tool.InputSchema.GetProperty("properties")
            .GetProperty("include_hidden").GetProperty("description").GetString()!;

        Assert.Contains("Defaults to false", description, StringComparison.Ordinal);
        Assert.Contains("explicit include globs and ignore-file allow rules can override hidden filtering",
            description, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_token_is_absent_from_every_input_schema()
    {
        foreach (Tool tool in ToolContractCatalog.Create().Select(item => item.ProtocolTool))
        {
            Assert.False(tool.InputSchema.GetProperty("properties").TryGetProperty("cancellationToken", out _));
            Assert.False(tool.InputSchema.GetProperty("properties").TryGetProperty("cancellation_token", out _));
        }
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("list_directory")]
    [InlineData("glob")]
    [InlineData("grep")]
    public void Registered_handler_schema_matches_the_frozen_contract(string toolName)
    {
        Tool frozen = ToolContractCatalog.Create().Single(item => item.ProtocolTool.Name == toolName).ProtocolTool;
        MethodInfo method = typeof(FileInspectorTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        Tool registered = McpServerTool.Create(method).ProtocolTool;

        Assert.Equal(frozen.Title, registered.Title);
        Assert.Equal(frozen.Description, registered.Description);
        Assert.Equal(frozen.InputSchema.GetRawText(), registered.InputSchema.GetRawText());
        Assert.Equal(frozen.OutputSchema?.GetRawText(), registered.OutputSchema?.GetRawText());
        Assert.Equal(frozen.Annotations?.ReadOnlyHint, registered.Annotations?.ReadOnlyHint);
        Assert.Equal(frozen.Annotations?.DestructiveHint, registered.Annotations?.DestructiveHint);
        Assert.Equal(frozen.Annotations?.IdempotentHint, registered.Annotations?.IdempotentHint);
        Assert.Equal(frozen.Annotations?.OpenWorldHint, registered.Annotations?.OpenWorldHint);
    }
}
